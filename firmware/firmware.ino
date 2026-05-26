// fanadapter — USB HID → Fanatec wheelbase adapter (Teensy 4.1)
//
// Reads USB HID joystick devices through the host port and drives three
// Fanatec shifter/handbrake outputs plus the CSL Elite V2 pedal UART. Input
// → output assignment is configured at runtime via JSON over the USB CDC
// Serial port (typically driven by the bundled webconfig WebSerial UI),
// and persisted to EEPROM.
//
// This .ino file is just the orchestrator: USB host bring-up, pin/PWM
// setup, the minimal serial CLI, and the loop dispatch. Everything else
// lives in the side modules:
//
//   device_pool.h/cpp   USB HID joystick slot pool (claims-any, 8 slots)
//   mapping.h/cpp       Config schema, EEPROM I/O, input → output engine
//   protocol.h/cpp      JSON command parser, live event emitter
//   pedals.h/cpp        Fanatec pedal-port UART state machine (unchanged)

#include <Arduino.h>
#include <USBHost_t36.h>
#include "pedals.h"
#include "mapping.h"
#include "device_pool.h"
#include "protocol.h"

// ---------------- USB host ----------------

USBHost      g_usb;
USBHub       g_hub1(g_usb);
USBHub       g_hub2(g_usb);

// One USBHIDParser per claimable HID interface. The Logitech RS H-Shifter
// alone exposes two interfaces (HID1 + HID2), and the SP Pro adds a third,
// so 8 covers the current build with headroom.
USBHIDParser g_hid1(g_usb);
USBHIDParser g_hid2(g_usb);
USBHIDParser g_hid3(g_usb);
USBHIDParser g_hid4(g_usb);
USBHIDParser g_hid5(g_usb);
USBHIDParser g_hid6(g_usb);
USBHIDParser g_hid7(g_usb);
USBHIDParser g_hid8(g_usb);

USBDriver* const g_usbDrivers[] = {
  &g_hub1, &g_hub2,
  &g_hid1, &g_hid2, &g_hid3, &g_hid4, &g_hid5, &g_hid6, &g_hid7, &g_hid8,
};
constexpr uint8_t g_usbDriverCount =
  sizeof(g_usbDrivers) / sizeof(g_usbDrivers[0]);
bool g_usbDriverActive[g_usbDriverCount] = { false };

static const char* driverName(USBDriver* d) {
  if (d == &g_hub1) return "Hub1";
  if (d == &g_hub2) return "Hub2";
  if (d == &g_hid1) return "HID1";
  if (d == &g_hid2) return "HID2";
  if (d == &g_hid3) return "HID3";
  if (d == &g_hid4) return "HID4";
  if (d == &g_hid5) return "HID5";
  if (d == &g_hid6) return "HID6";
  if (d == &g_hid7) return "HID7";
  if (d == &g_hid8) return "HID8";
  return "?";
}

// Log driver attach/detach. Per-slot HID claim/disconnect events come from
// device_pool.cpp itself. The protocol layer additionally emits JSON
// device_attached/detached events when a WebSerial client is connected.
static void pollUsbDriverStatus() {
  for (uint8_t i = 0; i < g_usbDriverCount; ++i) {
    const bool nowActive = (*g_usbDrivers[i]);
    if (nowActive == g_usbDriverActive[i]) continue;
    g_usbDriverActive[i] = nowActive;
    Serial.print("[USB] ");
    Serial.print(driverName(g_usbDrivers[i]));
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

// ---------------- Minimal serial CLI ----------------
// Everything beyond u/p/X/? lives in the JSON protocol now.

static void printUsbStatus() {
  Serial.println();
  Serial.println("Device pool (joystick HID slots):");
  for (uint8_t i = 0; i < devicePoolSize(); ++i) {
    GenericJoystickHID* d = devicePoolSlot(i);
    Serial.print("  slot ");
    Serial.print(i);
    Serial.print(": ");
    if (d->connected()) {
      Serial.print("VID=0x");
      Serial.print(d->vid(), HEX);
      Serial.print(" PID=0x");
      Serial.print(d->pid(), HEX);
      Serial.print("  buttons=0x");
      Serial.print(d->buttons(), HEX);
      Serial.print("  axes=[");
      for (uint8_t j = 0; j < d->axisCount(); ++j) {
        if (j) Serial.print(',');
        Serial.print(d->axis(j));
      }
      Serial.print("]  ac=");
      Serial.print(d->axisCount());
      Serial.print(" bc=");
      Serial.println(d->buttonCount());
    } else {
      Serial.println("idle");
    }
  }

  Serial.println("USB drivers (bus state):");
  for (uint8_t i = 0; i < g_usbDriverCount; ++i) {
    Serial.print("  ");
    Serial.print(driverName(g_usbDrivers[i]));
    Serial.print(": ");
    if (*g_usbDrivers[i]) {
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
  Serial.print(" | T=");
  Serial.print(((uint32_t)getPedalThrottle() * 100) / 65535);
  Serial.print("% B=");
  Serial.print(((uint32_t)getPedalBrake() * 100) / 65535);
  Serial.print("% C=");
  Serial.print(((uint32_t)getPedalClutch() * 100) / 65535);
  Serial.print("% H=");
  Serial.print(((uint32_t)getPedalHandbrake() * 100) / 65535);
  Serial.println("%");
  Serial.println();
}

static void printHelp() {
  Serial.println();
  Serial.println("Minimal serial CLI (everything else is JSON / WebSerial):");
  Serial.println("  u    print USB host + device pool status");
  Serial.println("  p    force pedal handshake reset (back to Step 0)");
  Serial.println("  X    CPU soft-reset");
  Serial.println("  ?    this help");
  Serial.println();
  Serial.println("JSON commands: send a single line starting with '{' to");
  Serial.println("the same Serial port. See protocol.cpp for the full list,");
  Serial.println("or use the webconfig WebSerial UI.");
  Serial.println();
}

static inline void cpuSoftReset() {
  Serial.flush();
  delay(50);
  (*((volatile uint32_t*)0xE000ED0C)) = 0x5FA0004;
  while (true) {}
}

// CliCharCallback registered with the protocol layer. The layer forwards
// any non-'{' byte here.
static void handleCliChar(char c) {
  switch (c) {
    case 'u': case 'U':
      printUsbStatus();
      break;
    case 'p': case 'P':
      pedalsForceReset();
      break;
    case 'X':
      Serial.println("[CPU] Manual soft-reset.");
      cpuSoftReset();
      break;
    case '?': case 'h': case 'H':
      printHelp();
      break;
    default:
      Serial.print("Unknown CLI char '");
      Serial.print(c);
      Serial.println("' (type ? for help, or send a JSON command)");
      break;
  }
}

// ---------------- Setup / loop ----------------

static void printBanner() {
  Serial.println();
  Serial.println("=== fanadapter — USB HID → Fanatec wheelbase ===");
  Serial.print  ("=== fw 0.3.0  protocol 2  config v");
  Serial.print  (CONFIG_VERSION);
  Serial.println(" ===");
  Serial.println("Configure via WebSerial (see webconfig/). '?' for the CLI.");
  Serial.println();
}

void setup() {
  Serial.begin(115200);
  delay(500);  // let the USB CDC enumeration settle before the banner

  // PWM resolution is global; set once.
  analogWriteResolution(PWM_BITS);

  // H-pattern X/Y DACs (PWM through 1k+1µF RC)
  pinMode(PIN_X, OUTPUT);
  pinMode(PIN_Y, OUTPUT);
  analogWriteFrequency(PIN_X, PWM_FREQ_HZ);
  analogWriteFrequency(PIN_Y, PWM_FREQ_HZ);

  // Sequential pins — open-drain, idle high (wheelbase has internal pull-up)
  pinMode(PIN_SEQ_UP,   OUTPUT_OPENDRAIN);
  pinMode(PIN_SEQ_DOWN, OUTPUT_OPENDRAIN);
  digitalWrite(PIN_SEQ_UP,   HIGH);
  digitalWrite(PIN_SEQ_DOWN, HIGH);

  // Handbrake DAC
  pinMode(PIN_HANDBRAKE, OUTPUT);
  analogWriteFrequency(PIN_HANDBRAKE, PWM_FREQ_HZ);
  analogWrite(PIN_HANDBRAKE, 0);

  // Order: pedals first (initializes Serial3), then mapping (loads EEPROM
  // and snaps DACs / pedal stream to neutral), then protocol.
  pedalsInit();
  mappingInit();
  protocolInit();
  protocolSetCliCallback(&handleCliChar);

  g_usb.begin();

  printBanner();
}

void loop() {
  g_usb.Task();
  pollUsbDriverStatus();
  protocolTick();   // drain Serial → dispatch JSON / CLI, emit live events
  mappingTick();    // read device pool → drive DACs / sequential / pedal stream
  pedalsUpdate();   // Fanatec pedal-port UART state machine
}
