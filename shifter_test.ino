// Fanatec USB HID Adapter — H-pattern + Sequential + Handbrake
//
// Reads three Logitech RS devices through a powered USB hub on the Teensy 4.1
// USB host port and translates them to three Fanatec shifter outputs:
//
//   H-pattern shifter (RS H-Shifter, VID 046D PID C26B):
//     8 buttons → gear → PWM voltages on pins 4/5 via 1k+1uF RC filters
//                       → Shifter 1 RJ12 pins 4/5 (X/Y axes)
//
//   Sequential shifter (RS Shifter & Handbrake combo, VID 046D PID C278):
//     Button 1 (up) / button 2 (down) → 50ms LOW pulse on pin 6/7
//                       → Shifter 2 RJ12 pins 4/5 (open-drain to GND)
//
//   Handbrake (same combo PID C278, second device):
//     Z axis (analog) and button 3 (digital) → PWM on pin 8 via RC filter
//                       → Handbrake RJ12 pin 5
//
// The two C278 devices report identically; we aggregate buttons (OR) and Z
// (max) across all connected C278 instances, so whichever device is in
// sequential mode drives buttons 1/2 and whichever is in handbrake mode
// drives Z / button 3. No hub-port identification needed.
//
// Serial commands still work as manual override — see `?`.

#include <Arduino.h>
#include <USBHost_t36.h>
#include "pedals.h"

// ---------------- Pin & PWM configuration ----------------

// H-pattern (analog X/Y via RC filters)
constexpr uint8_t  PIN_X         = 4;
constexpr uint8_t  PIN_Y         = 5;

// Sequential (open-drain pulses to wheelbase pull-ups)
constexpr uint8_t  PIN_SEQ_UP    = 6;
constexpr uint8_t  PIN_SEQ_DOWN  = 7;

// Handbrake (analog via RC filter)
constexpr uint8_t  PIN_HANDBRAKE = 8;

constexpr uint8_t  PWM_BITS      = 12;       // 0..4095 → 0..3.3V via RC filter
constexpr uint32_t PWM_FREQ_HZ   = 36000;    // 36 kHz: clean DC after 1kΩ/1µF
constexpr uint32_t CYCLE_HOLD_MS = 1500;     // hold per gear when running 'c' cycle

// The wheelbase needs to see a neutral transit between gears, otherwise
// same-row moves (1→R, 5→7, 4→6, ...) are ignored — the Y voltage never
// changes, so the firmware thinks the lever never left the previous gear.
// Drop this if shifts start feeling laggy; raise it if any get dropped.
constexpr uint32_t NEUTRAL_TRANSIT_MS = 50;

// Sequential pulse width — matches real RS sequential shifter timing.
constexpr uint32_t SEQ_PULSE_MS  = 50;

// Handbrake calibration. Sample-count gate prevents garbage output at boot
// before we've observed the actual idle/full-pull range.
constexpr uint32_t HB_MIN_SAMPLES        = 100;
constexpr uint32_t HB_DEBUG_INTERVAL_MS  = 500;

// ---------------- DAC value targets ----------------
// Derived from the ClubSport SQ V1.5 resistor ladder at 3.3V Vref.
// 12-bit DAC values; voltages shown for reference only.

// X-axis: 5 columns, left (reverse) → right (7th)
constexpr uint16_t X1 = 4095;  // 3.30 V — Reverse rail        (leftmost)
constexpr uint16_t X2 = 2790;  // 2.25 V — 1-2 gear gate
constexpr uint16_t X3 = 2163;  // 1.74 V — 3-4 gear gate       (neutral column)
constexpr uint16_t X4 = 1766;  // 1.42 V — 5-6 gear gate
constexpr uint16_t X5 = 1310;  // 1.06 V — 7th gear rail       (rightmost)

// Y-axis: 3 rows, up → down
constexpr uint16_t Y1 = 3430;  // 2.76 V — Up row
constexpr uint16_t Y2 = 2048;  // 1.65 V — Middle row          (neutral)
constexpr uint16_t Y3 = 779;   // 0.63 V — Down row

// ---------------- Gear table ----------------
// Gear IDs. R = reverse, N = neutral. 1..7 are forward gears.
// Kept as a small enum so callers don't pass raw ints around.

enum Gear : uint8_t {
  GEAR_R = 0,
  GEAR_1,
  GEAR_2,
  GEAR_3,
  GEAR_4,
  GEAR_5,
  GEAR_6,
  GEAR_7,
  GEAR_N,
  GEAR_COUNT
};

struct GearEntry {
  const char* name;
  uint16_t    x;     // DAC value for X-axis PWM
  uint16_t    y;     // DAC value for Y-axis PWM
};

// Order here is also the cycle order: R, 1..7, N, then back to R.
// Edit these DAC values during calibration — nothing else needs to change.
constexpr GearEntry GEARS[GEAR_COUNT] = {
  { "R", X1, Y1 },
  { "1", X2, Y1 },
  { "2", X2, Y3 },
  { "3", X3, Y1 },
  { "4", X3, Y3 },
  { "5", X4, Y1 },
  { "6", X4, Y3 },
  { "7", X5, Y1 },
  { "N", X3, Y2 },
};

// ---------------- Core: gear → PWM ----------------
// Keep this separable from the timer loop. Milestone 2 will call setGear()
// from the USB HID parser instead of the cycle in loop().

static Gear g_currentGear = GEAR_N;

static void writeGearPwm(Gear g) {
  const GearEntry& e = GEARS[g];
  analogWrite(PIN_X, e.x);
  analogWrite(PIN_Y, e.y);
}

void setGear(Gear g) {
  // Pass through neutral so the wheelbase sees a gear-release event before
  // the new gear is latched. Skip when either end is already neutral —
  // that transit is implicit.
  if (g != GEAR_N && g_currentGear != GEAR_N && g != g_currentGear) {
    writeGearPwm(GEAR_N);
    delay(NEUTRAL_TRANSIT_MS);
  }

  writeGearPwm(g);
  g_currentGear = g;

  const GearEntry& e = GEARS[g];
  Serial.print("Gear ");
  Serial.print(e.name);
  Serial.print("  X=");
  Serial.print(e.x);
  Serial.print("  Y=");
  Serial.println(e.y);
}

// ---------------- USB host ----------------
//
// Three Logitech RS devices on a powered hub:
//   VID 046D PID C26B — RS H-Shifter
//     8 buttons (Generic Desktop > Joystick collection), 1 bit per gear.
//   VID 046D PID C278 — RS Shifter & Handbrake (TWO physical devices)
//     3 buttons + 16-bit Z axis. Physical switch on the device selects mode
//     (sequential / digital handbrake / analog handbrake) — but the HID
//     report shape is identical regardless. We aggregate across both C278
//     devices (buttons OR'd, Z axis MAXed) so whichever happens to be in
//     which mode just drives the corresponding output.
//
// USBHost_t36's stock JoystickController refused to claim these devices'
// HID collections in earlier testing, so we use a custom USBHIDInput
// subclass keyed by VID/PID. One instance per physical device we expect.

USBHost      g_usb;
USBHub       g_hub1(g_usb);
USBHub       g_hub2(g_usb);

// USBHIDParser instances parse HID reports off enumerated devices.
// One parser per HID interface — bumped to 8 since H-Shifter alone uses
// two interfaces (HID1 + HID2) per earlier observation.
USBHIDParser g_hid1(g_usb);
USBHIDParser g_hid2(g_usb);
USBHIDParser g_hid3(g_usb);
USBHIDParser g_hid4(g_usb);
USBHIDParser g_hid5(g_usb);
USBHIDParser g_hid6(g_usb);
USBHIDParser g_hid7(g_usb);
USBHIDParser g_hid8(g_usb);

// Generic HID consumer. Claims one device by VID/PID, parses up to 32 buttons
// (page 0x09) and the Z axis (Generic Desktop > Z, usage 0x01:0x32). Multiple
// instances with the same VID/PID will claim multiple physical devices in
// enumeration order (each instance claims at most one).
class GenericJoystickHID : public USBHIDInput {
public:
  GenericJoystickHID(uint16_t vid, uint16_t pid, const char* logName)
    : m_vid(vid), m_pid(pid), m_name(logName) {
    USBHIDParser::driver_ready_for_hid_collection(this);
  }

  // Generic Desktop > Joystick (page 0x01, usage 0x04). Combined 32-bit form
  // matches what USBHIDParser passes for the top-level Application Collection.
  static constexpr uint32_t TOPUSAGE_JOYSTICK = 0x10004;

  hidclaim_t claim_collection(USBHIDParser* /*driver*/, Device_t* dev, uint32_t topusage) override {
    if (topusage != TOPUSAGE_JOYSTICK) return CLAIM_NO;
    if (m_claimed)                     return CLAIM_NO;
    if (dev->idVendor  != m_vid)       return CLAIM_NO;
    if (dev->idProduct != m_pid)       return CLAIM_NO;

    m_claimed = true;
    m_buttons = 0;
    m_xAxis   = 0;
    m_yAxis   = 0;
    m_zAxis   = 0;
    m_hubPort = dev->hub_port;

    Serial.print("[USB] ");
    Serial.print(m_name);
    Serial.print(" CLAIMED  VID=0x");
    Serial.print(dev->idVendor, HEX);
    Serial.print("  PID=0x");
    Serial.print(dev->idProduct, HEX);
    Serial.print("  hub_port=");
    Serial.println(dev->hub_port);
    return CLAIM_REPORT;
  }

  void disconnect_collection(Device_t* /*dev*/) override {
    Serial.print("[USB] ");
    Serial.print(m_name);
    Serial.println(" disconnected");
    m_claimed = false;
    m_buttons = 0;
    m_xAxis   = 0;
    m_yAxis   = 0;
    m_zAxis   = 0;
  }

  void hid_input_data(uint32_t usage, int32_t value) override {
    const uint16_t page = usage >> 16;
    const uint16_t id   = usage & 0xFFFF;

    if (page == 0x09 && id >= 1 && id <= 32) {
      // Button page
      const uint32_t mask = (1u << (id - 1));
      m_buttons = value ? (m_buttons | mask) : (m_buttons & ~mask);
    } else if (page == 0x01) {
      // Generic Desktop axes
      switch (id) {
        case 0x30: m_xAxis = (uint16_t)value; break;  // X
        case 0x31: m_yAxis = (uint16_t)value; break;  // Y
        case 0x32: m_zAxis = (uint16_t)value; break;  // Z
        default: break;
      }
    }
    // Vendor-defined pages (FF00, FF01, ...) ignored — see SP Pro report
    // descriptor: 18 bytes of FF00:0001 after the axes, plus a tower of
    // feature reports. Not relevant for pedal position.
  }

  // Required overrides — no per-report-boundary work.
  bool hid_process_in_data (const Transfer_t* /*t*/) override { return false; }
  bool hid_process_out_data(const Transfer_t* /*t*/) override { return false; }
  void hid_input_begin(uint32_t, uint32_t, int, int) override {}
  void hid_input_end() override {}

  bool        connected() const { return m_claimed; }
  uint32_t    buttons()   const { return m_buttons; }
  uint16_t    xAxis()     const { return m_xAxis; }
  uint16_t    yAxis()     const { return m_yAxis; }
  uint16_t    zAxis()     const { return m_zAxis; }

  // Selector by index: 0=X, 1=Y, 2=Z. Returns 0 for any other value.
  uint16_t    axisByIndex(uint8_t i) const {
    switch (i) {
      case 0:  return m_xAxis;
      case 1:  return m_yAxis;
      case 2:  return m_zAxis;
      default: return 0;
    }
  }
  uint8_t     hubPort()   const { return m_hubPort; }
  const char* name()      const { return m_name; }

private:
  uint16_t    m_vid;
  uint16_t    m_pid;
  const char* m_name;
  bool        m_claimed = false;
  uint32_t    m_buttons = 0;
  uint16_t    m_xAxis   = 0;
  uint16_t    m_yAxis   = 0;
  uint16_t    m_zAxis   = 0;
  uint8_t     m_hubPort = 0;
};

// One instance for the H-pattern shifter; two for the combo devices (one
// configured as sequential, the other as handbrake by the user's physical
// switch — but we don't care which is which).
GenericJoystickHID g_hPattern(0x046D, 0xC26B, "H-Pattern");
GenericJoystickHID g_combo1  (0x046D, 0xC278, "Combo#1");
GenericJoystickHID g_combo2  (0x046D, 0xC278, "Combo#2");

// Simnet SP Pro Pedals (VID 0xCAFE PID 0xA301). Three 12-bit axes:
//   Generic Desktop > X → throttle (default mapping, can swap below)
//   Generic Desktop > Y → brake
//   Generic Desktop > Z → clutch
// 18 bytes of vendor-defined data and 13 feature reports are ignored.
GenericJoystickHID g_spPedals(0xCAFE, 0xA301, "SP-Pro");

GenericJoystickHID* const g_combos[] = { &g_combo1, &g_combo2 };
constexpr uint8_t g_comboCount = sizeof(g_combos) / sizeof(g_combos[0]);

// ---- Pedal axis mapping (edit if a pedal moves the wrong slider) ----
// AXIS values: 0 = X, 1 = Y, 2 = Z. Set INVERT to true if the device
// reports 4095 at rest and drops when pressed.
constexpr uint8_t PEDAL_AXIS_THROTTLE   = 0;
constexpr uint8_t PEDAL_AXIS_BRAKE      = 1;
constexpr uint8_t PEDAL_AXIS_CLUTCH     = 2;
constexpr bool    PEDAL_INVERT_THROTTLE = false;
constexpr bool    PEDAL_INVERT_BRAKE    = false;
constexpr bool    PEDAL_INVERT_CLUTCH   = false;

USBDriver* g_usbDrivers[] = {
  &g_hub1, &g_hub2,
  &g_hid1, &g_hid2, &g_hid3, &g_hid4, &g_hid5, &g_hid6, &g_hid7, &g_hid8,
};
const uint8_t g_usbDriverCount = sizeof(g_usbDrivers) / sizeof(g_usbDrivers[0]);
bool g_usbDriverActive[g_usbDriverCount] = { false };

Gear buttonsToGear(uint32_t buttons) {
  switch (buttons & 0xFF) {
    case 0x01: return GEAR_1;
    case 0x02: return GEAR_2;
    case 0x04: return GEAR_3;
    case 0x08: return GEAR_4;
    case 0x10: return GEAR_5;
    case 0x20: return GEAR_6;
    case 0x40: return GEAR_7;
    case 0x80: return GEAR_R;
    default:   return GEAR_N;  // 0x00, or any unexpected multi-bit value
  }
}

// Log device attach/detach by watching each driver's claimed state. Per-HID
// claim events are also logged by GenericJoystickHID itself.
void pollUsbDriverStatus() {
  for (uint8_t i = 0; i < g_usbDriverCount; ++i) {
    const bool nowActive = (*g_usbDrivers[i]);
    if (nowActive == g_usbDriverActive[i]) continue;
    g_usbDriverActive[i] = nowActive;

    const char* name =
      (g_usbDrivers[i] == &g_hub1) ? "Hub1" :
      (g_usbDrivers[i] == &g_hub2) ? "Hub2" :
      (g_usbDrivers[i] == &g_hid1) ? "HID1" :
      (g_usbDrivers[i] == &g_hid2) ? "HID2" :
      (g_usbDrivers[i] == &g_hid3) ? "HID3" :
      (g_usbDrivers[i] == &g_hid4) ? "HID4" :
      (g_usbDrivers[i] == &g_hid5) ? "HID5" :
      (g_usbDrivers[i] == &g_hid6) ? "HID6" :
      (g_usbDrivers[i] == &g_hid7) ? "HID7" :
      (g_usbDrivers[i] == &g_hid8) ? "HID8" : "USB device";

    Serial.print("[USB] ");
    Serial.print(name);
    if (nowActive) {
      Serial.print(" attached  VID=0x");
      Serial.print(g_usbDrivers[i]->idVendor(), HEX);
      Serial.print("  PID=0x");
      Serial.println(g_usbDrivers[i]->idProduct(), HEX);
    } else {
      Serial.println(" detached");
    }
  }
}

// ---------------- Manual CPU soft-reset ----------------
// USB host hot-plug behavior with USBHost_t36 is unreliable on some hubs
// (notably the Sabrent 4-port USB 2.0, VID 0x5E3 PID 0x610 — see
// https://forum.pjrc.com/threads/68145 and README "Compatibility" section).
// We don't try to auto-recover anymore; tests showed that CPU reset alone
// can't clear hub-side wedge states, and the symptom doesn't recur with a
// known-good hub. Manual 'X' command kept for general debug convenience.

static inline void cpuSoftReset() {
  Serial.flush();
  delay(50);
  (*((volatile uint32_t*)0xE000ED0C)) = 0x5FA0004;
  while (true) {}  // never reached
}

// ---------------- Role: H-pattern shifter ----------------
// Reads g_hPattern's button bitmap, maps to gear, drives PWM. Edge-detected
// so we only call setGear() on actual change.

void updateHPattern() {
  static uint32_t prevButtons = 0;
  static bool     wasConnected = false;
  const bool      isConnected  = g_hPattern.connected();
  const uint32_t  buttons      = isConnected ? g_hPattern.buttons() : 0u;

  // Force a recompute on connect or disconnect transitions.
  if (isConnected != wasConnected) {
    wasConnected = isConnected;
    prevButtons  = ~buttons;  // guarantee inequality below
  }

  if (buttons != prevButtons) {
    prevButtons = buttons;
    setGear(buttonsToGear(buttons));
  }
}

// ---------------- Role: sequential shifter ----------------
// Aggregate button states across all C278 devices, edge-detect, emit a 50 ms
// LOW pulse on pin 6 (up) or pin 7 (down). Non-blocking — pulse end is timed
// by millis().

struct Pulse {
  uint8_t  pin;
  uint32_t endMillis = 0;
  bool     active    = false;

  void start() {
    digitalWrite(pin, LOW);
    endMillis = millis() + SEQ_PULSE_MS;
    active    = true;
  }

  void update() {
    if (active && (int32_t)(millis() - endMillis) >= 0) {
      digitalWrite(pin, HIGH);
      active = false;
    }
  }
};

Pulse g_pulseUp   = { PIN_SEQ_UP   };
Pulse g_pulseDown = { PIN_SEQ_DOWN };

void updateSequential() {
  // OR button states across all connected C278 devices.
  uint32_t comboButtons = 0;
  for (uint8_t i = 0; i < g_comboCount; ++i) {
    if (g_combos[i]->connected()) comboButtons |= g_combos[i]->buttons();
  }

  static uint32_t prevButtons = 0;
  const uint32_t  rising      = comboButtons & ~prevButtons;
  prevButtons = comboButtons;

  const bool upEdge   = (rising & 0x01) != 0;  // button 1
  const bool downEdge = (rising & 0x02) != 0;  // button 2

  if (upEdge && downEdge) {
    Serial.println("[Seq] both buttons pressed simultaneously — ignored");
  } else {
    if (upEdge) {
      Serial.println("[Seq] UP");
      g_pulseUp.start();
    }
    if (downEdge) {
      Serial.println("[Seq] DOWN");
      g_pulseDown.start();
    }
  }

  g_pulseUp.update();
  g_pulseDown.update();
}

// ---------------- Role: handbrake ----------------
// Aggregate Z axis (max) and button 3 (any) across all C278 devices.
// Auto-calibrate the active Z range over the session. Output 0 until at
// least HB_MIN_SAMPLES observations have accumulated AND zMax > zMin.

struct HandbrakeCalibration {
  uint16_t zMin    = 0xFFFF;
  uint16_t zMax    = 0;
  uint32_t samples = 0;
};
HandbrakeCalibration g_hb;

void resetHandbrakeCalibration() {
  g_hb = HandbrakeCalibration{};
  Serial.println("[HB] calibration reset");
}

void updateHandbrake() {
  // Aggregate state.
  bool     anyConnected = false;
  uint16_t maxZ         = 0;
  bool     digital      = false;
  for (uint8_t i = 0; i < g_comboCount; ++i) {
    if (!g_combos[i]->connected()) continue;
    anyConnected = true;
    const uint16_t z = g_combos[i]->zAxis();
    if (z > maxZ) maxZ = z;
    if (g_combos[i]->buttons() & 0x04) digital = true;   // button 3
  }

  if (!anyConnected) {
    analogWrite(PIN_HANDBRAKE, 0);
    setPedalHandbrake(0);
    return;
  }

  // Update calibration.
  if (maxZ < g_hb.zMin) g_hb.zMin = maxZ;
  if (maxZ > g_hb.zMax) g_hb.zMax = maxZ;
  g_hb.samples++;

  // Compute analog output.
  uint16_t analogOut = 0;
  if (g_hb.samples >= HB_MIN_SAMPLES && g_hb.zMax > g_hb.zMin) {
    const int32_t scaled =
      ((int32_t)(maxZ - g_hb.zMin) * 4095) / (g_hb.zMax - g_hb.zMin);
    analogOut = (uint16_t)constrain(scaled, 0, 4095);
  }

  // Digital handbrake overrides to full when pressed.
  const uint16_t output = digital ? 4095 : analogOut;
  analogWrite(PIN_HANDBRAKE, output);

  // Also feed handbrake into the CSL Elite V2 pedal stream's Handbrake
  // channel. Newer Fanatec firmware treats the digital pedal protocol as
  // the canonical handbrake source when present, ignoring the dedicated
  // handbrake RJ12 port. We send to both channels so the system works
  // regardless of which the wheelbase listens to. 12→16-bit with bit
  // replication so 4095 maps cleanly to 65535.
  const uint16_t pedalHandbrake = (uint16_t)((output << 4) | (output >> 8));
  setPedalHandbrake(pedalHandbrake);

  // Periodic debug — only while handbrake is actually engaged. Avoids
  // spamming the serial log when the lever is at idle.
  static uint32_t lastPrint = 0;
  if (output > 0 && millis() - lastPrint >= HB_DEBUG_INTERVAL_MS) {
    lastPrint = millis();
    Serial.print("[HB] raw=");
    Serial.print(maxZ);
    Serial.print(" cal=[");
    Serial.print(g_hb.zMin);
    Serial.print(",");
    Serial.print(g_hb.zMax);
    Serial.print("] n=");
    Serial.print(g_hb.samples);
    Serial.print(" out=");
    Serial.print(output);
    if (digital) Serial.print(" (digital)");
    Serial.println();
  }
}

// ---------------- Role: USB pedals → Fanatec pedal stream ----------------
// Reads X/Y/Z from g_spPedals (or any future generic HID pedal device) and
// pushes the values into the pedal module so the UART streaming layer can
// forward them to the wheelbase. Each axis is mapped to one Fanatec pedal
// channel via the PEDAL_AXIS_* constants at the top of the USB host block.

// 12-bit (0..4095) → 16-bit (0..65535) with bit replication so the top
// hits 0xFFFF exactly, not 0xFFF0. Optionally invert.
static uint16_t scalePedal(uint16_t raw12, bool invert) {
  if (invert) raw12 = (raw12 > 4095) ? 0 : (uint16_t)(4095 - raw12);
  if (raw12 > 4095) raw12 = 4095;
  return (uint16_t)((raw12 << 4) | (raw12 >> 8));
}

void updatePedalsFromHID() {
  if (!g_spPedals.connected()) return;

  const uint16_t t = scalePedal(g_spPedals.axisByIndex(PEDAL_AXIS_THROTTLE), PEDAL_INVERT_THROTTLE);
  const uint16_t b = scalePedal(g_spPedals.axisByIndex(PEDAL_AXIS_BRAKE),    PEDAL_INVERT_BRAKE);
  const uint16_t c = scalePedal(g_spPedals.axisByIndex(PEDAL_AXIS_CLUTCH),   PEDAL_INVERT_CLUTCH);

  setPedalThrottle(t);
  setPedalBrake(b);
  setPedalClutch(c);

  // Light periodic debug, only when something is non-zero.
  static uint32_t lastPrint = 0;
  if ((t || b || c) && millis() - lastPrint >= 500) {
    lastPrint = millis();
    Serial.print("[Pedals/HID] x=");
    Serial.print(g_spPedals.xAxis());
    Serial.print(" y=");
    Serial.print(g_spPedals.yAxis());
    Serial.print(" z=");
    Serial.print(g_spPedals.zAxis());
    Serial.print("  → T=");
    Serial.print((uint32_t)t * 100 / 65535);
    Serial.print("% B=");
    Serial.print((uint32_t)b * 100 / 65535);
    Serial.print("% C=");
    Serial.print((uint32_t)c * 100 / 65535);
    Serial.println("%");
  }
}

// Map a single input character to a Gear. Returns true on hit.
bool charToGear(char c, Gear& out) {
  switch (c) {
    case 'R': case 'r': out = GEAR_R; return true;
    case '1':           out = GEAR_1; return true;
    case '2':           out = GEAR_2; return true;
    case '3':           out = GEAR_3; return true;
    case '4':           out = GEAR_4; return true;
    case '5':           out = GEAR_5; return true;
    case '6':           out = GEAR_6; return true;
    case '7':           out = GEAR_7; return true;
    case 'N': case 'n': out = GEAR_N; return true;
    default:                          return false;
  }
}

// ---------------- Setup / loop ----------------

void printHelp() {
  Serial.println();
  Serial.println("Commands:");
  Serial.println("  R 1 2 3 4 5 6 7 N   set H-pattern gear (case-insensitive)");
  Serial.println("  +                   manual sequential UP pulse");
  Serial.println("  -                   manual sequential DOWN pulse");
  Serial.println("  k                   reset handbrake calibration");
  Serial.println("  c                   cycle through all H-pattern gears once");
  Serial.println("  t                   print current gear table");
  Serial.println("  u                   print USB host status");
  Serial.println("  p                   force pedal handshake reset (to Step 0)");
  Serial.println("  X                   force CPU soft-reset (full restart)");
  Serial.println("  q / w               decrease / increase pedal Throttle by 1%");
  Serial.println("  a / s               decrease / increase pedal Brake by 1%");
  Serial.println("  z / x               decrease / increase pedal Clutch by 1%");
  Serial.println("  ?                   this help");
  Serial.println();
}

void printUsbStatus() {
  Serial.println();
  Serial.print("H-Pattern: ");
  if (g_hPattern.connected()) {
    Serial.print("yes  buttons=0x");
    Serial.print(g_hPattern.buttons(), HEX);
    Serial.print("  hub_port=");
    Serial.println(g_hPattern.hubPort());
  } else {
    Serial.println("no");
  }
  for (uint8_t i = 0; i < g_comboCount; ++i) {
    Serial.print(g_combos[i]->name());
    Serial.print(": ");
    if (g_combos[i]->connected()) {
      Serial.print("yes  buttons=0x");
      Serial.print(g_combos[i]->buttons(), HEX);
      Serial.print("  z=");
      Serial.print(g_combos[i]->zAxis());
      Serial.print("  hub_port=");
      Serial.println(g_combos[i]->hubPort());
    } else {
      Serial.println("no");
    }
  }
  Serial.print("Handbrake cal: zMin=");
  Serial.print(g_hb.zMin);
  Serial.print(" zMax=");
  Serial.print(g_hb.zMax);
  Serial.print(" samples=");
  Serial.println(g_hb.samples);

  Serial.print("SP-Pro pedals: ");
  if (g_spPedals.connected()) {
    Serial.print("yes  X=");
    Serial.print(g_spPedals.xAxis());
    Serial.print("  Y=");
    Serial.print(g_spPedals.yAxis());
    Serial.print("  Z=");
    Serial.print(g_spPedals.zAxis());
    Serial.print("  hub_port=");
    Serial.println(g_spPedals.hubPort());
  } else {
    Serial.println("no");
  }

  // Per-driver bus state — shows whether USBHub / USBHIDParser slots are
  // currently bound to physical devices. Useful when a HID consumer (above)
  // reports "no" — if the parser slot IS claimed, the device enumerated but
  // no consumer wanted it; if the slot is empty, the device didn't enumerate
  // at all (cable / power / hub issue).
  Serial.println("USB drivers:");
  for (uint8_t i = 0; i < g_usbDriverCount; ++i) {
    const bool active = (*g_usbDrivers[i]);
    const char* name =
      (g_usbDrivers[i] == &g_hub1) ? "Hub1" :
      (g_usbDrivers[i] == &g_hub2) ? "Hub2" :
      (g_usbDrivers[i] == &g_hid1) ? "HID1" :
      (g_usbDrivers[i] == &g_hid2) ? "HID2" :
      (g_usbDrivers[i] == &g_hid3) ? "HID3" :
      (g_usbDrivers[i] == &g_hid4) ? "HID4" :
      (g_usbDrivers[i] == &g_hid5) ? "HID5" :
      (g_usbDrivers[i] == &g_hid6) ? "HID6" :
      (g_usbDrivers[i] == &g_hid7) ? "HID7" :
      (g_usbDrivers[i] == &g_hid8) ? "HID8" : "?";
    Serial.print("  ");
    Serial.print(name);
    Serial.print(": ");
    if (active) {
      Serial.print("VID=0x");
      Serial.print(g_usbDrivers[i]->idVendor(), HEX);
      Serial.print(" PID=0x");
      Serial.println(g_usbDrivers[i]->idProduct(), HEX);
    } else {
      Serial.println("idle");
    }
  }

  Serial.print("Pedals (Serial3): ");
  Serial.print(getPedalsStateName());
  Serial.print(" | Throttle=");
  Serial.print(((uint32_t)getPedalThrottle() * 100) / 65535);
  Serial.print("% Brake=");
  Serial.print(((uint32_t)getPedalBrake() * 100) / 65535);
  Serial.print("% Clutch=");
  Serial.print(((uint32_t)getPedalClutch() * 100) / 65535);
  Serial.println("%");
  Serial.println();
}

void printTable() {
  Serial.println();
  Serial.println("Gear |  X (DAC)  |  Y (DAC)");
  Serial.println("-----+-----------+----------");
  for (uint8_t i = 0; i < GEAR_COUNT; ++i) {
    Serial.print("  ");
    Serial.print(GEARS[i].name);
    Serial.print("  |   ");
    Serial.print(GEARS[i].x);
    Serial.print("    |   ");
    Serial.println(GEARS[i].y);
  }
  Serial.println();
}

void printBanner() {
  Serial.println();
  Serial.println("=== Fanatec USB HID Adapter — H-Pattern + Seq + Handbrake ===");
  Serial.println("USB host: 1x RS H-Shifter (C26B) + 2x RS Combo (C278) via hub.");
  Serial.println("Serial commands still work as a manual override.");
  printTable();
  printHelp();
}

void setup() {
  Serial.begin(115200);
  delay(500);  // let USB serial come up so the banner isn't lost

  // PWM resolution is global; set once.
  analogWriteResolution(PWM_BITS);

  // H-pattern (X/Y via RC filters)
  pinMode(PIN_X, OUTPUT);
  pinMode(PIN_Y, OUTPUT);
  analogWriteFrequency(PIN_X, PWM_FREQ_HZ);
  analogWriteFrequency(PIN_Y, PWM_FREQ_HZ);

  // Sequential (open-drain, idle HIGH)
  pinMode(PIN_SEQ_UP,   OUTPUT_OPENDRAIN);
  pinMode(PIN_SEQ_DOWN, OUTPUT_OPENDRAIN);
  digitalWrite(PIN_SEQ_UP,   HIGH);
  digitalWrite(PIN_SEQ_DOWN, HIGH);

  // Handbrake (PWM via RC filter)
  pinMode(PIN_HANDBRAKE, OUTPUT);
  analogWriteFrequency(PIN_HANDBRAKE, PWM_FREQ_HZ);
  analogWrite(PIN_HANDBRAKE, 0);

  // Start in H-pattern neutral so the wheelbase sees a sane state during boot.
  setGear(GEAR_N);

  // Initialize pedal emulator
  pedalsInit();

  g_usb.begin();

  printBanner();
}

// Sleep up to `ms` ms while keeping USB host and all roles alive. Returns
// false if any serial byte arrives — the byte is consumed so the caller
// knows to abort.
bool sleepOrAbort(uint32_t ms) {
  const uint32_t end = millis() + ms;
  while ((int32_t)(end - millis()) > 0) {
    g_usb.Task();
    pollUsbDriverStatus();
    updateHPattern();
    updateSequential();
    updateHandbrake();
    updatePedalsFromHID();
    pedalsUpdate();
    if (Serial.available()) { Serial.read(); return false; }
  }
  return true;
}

void runCycle() {
  Serial.println("Cycling R 1 2 3 4 5 6 7 N ...  (any key aborts)");
  for (uint8_t i = 0; i < GEAR_COUNT; ++i) {
    setGear(static_cast<Gear>(i));
    if (!sleepOrAbort(CYCLE_HOLD_MS)) {
      Serial.println("Cycle aborted.");
      return;
    }
  }
  Serial.println("Cycle done.");
}

void loop() {
  g_usb.Task();
  pollUsbDriverStatus();
  updateHPattern();
  updateSequential();
  updateHandbrake();
  updatePedalsFromHID();
  pedalsUpdate();

  if (!Serial.available()) return;

  char c = (char)Serial.read();

  // Ignore line endings and whitespace from terminals.
  if (c == '\r' || c == '\n' || c == ' ' || c == '\t') return;

  Gear g;
  if (charToGear(c, g)) {
    setGear(g);
  } else if (c == '+') {
    Serial.println("[Seq] UP (manual)");
    g_pulseUp.start();
  } else if (c == '-') {
    Serial.println("[Seq] DOWN (manual)");
    g_pulseDown.start();
  } else if (c == 'k' || c == 'K') {
    resetHandbrakeCalibration();
  } else if (c == 'c' || c == 'C') {
    runCycle();
  } else if (c == 't' || c == 'T') {
    printTable();
  } else if (c == 'u' || c == 'U') {
    printUsbStatus();
  } else if (c == 'q') {
    int32_t val = (int32_t)getPedalThrottle() - 655; // ~1%
    if (val < 0) val = 0;
    setPedalThrottle(val);
    Serial.print("[Pedals] Throttle: ");
    Serial.print((val * 100) / 65535);
    Serial.println("%");
  } else if (c == 'w') {
    int32_t val = (int32_t)getPedalThrottle() + 655; // ~1%
    if (val > 65535) val = 65535;
    setPedalThrottle(val);
    Serial.print("[Pedals] Throttle: ");
    Serial.print((val * 100) / 65535);
    Serial.println("%");
  } else if (c == 'a') {
    int32_t val = (int32_t)getPedalBrake() - 655; // ~1%
    if (val < 0) val = 0;
    setPedalBrake(val);
    Serial.print("[Pedals] Brake: ");
    Serial.print((val * 100) / 65535);
    Serial.println("%");
  } else if (c == 's') {
    int32_t val = (int32_t)getPedalBrake() + 655; // ~1%
    if (val > 65535) val = 65535;
    setPedalBrake(val);
    Serial.print("[Pedals] Brake: ");
    Serial.print((val * 100) / 65535);
    Serial.println("%");
  } else if (c == 'z') {
    int32_t val = (int32_t)getPedalClutch() - 655; // ~1%
    if (val < 0) val = 0;
    setPedalClutch(val);
    Serial.print("[Pedals] Clutch: ");
    Serial.print((val * 100) / 65535);
    Serial.println("%");
  } else if (c == 'x') {
    int32_t val = (int32_t)getPedalClutch() + 655; // ~1%
    if (val > 65535) val = 65535;
    setPedalClutch(val);
    Serial.print("[Pedals] Clutch: ");
    Serial.print((val * 100) / 65535);
    Serial.println("%");
  } else if (c == 'X') {
    Serial.println("[CPU] Manual soft-reset.");
    cpuSoftReset();
  } else if (c == 'p' || c == 'P') {
    pedalsForceReset();
  } else if (c == '?' || c == 'h' || c == 'H') {
    printHelp();
  } else {
    Serial.print("Unknown command: '");
    Serial.print(c);
    Serial.println("'  (type ? for help)");
  }
}
