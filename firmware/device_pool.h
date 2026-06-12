// USB HID joystick device pool.
//
// 8-slot pool that claims any non-hub HID-like device EspUsbHost surfaces:
// gamepads, joysticks, multi-axis controllers, keyboards. Slots are
// first-come-first-served; the slot index has no semantic meaning, the
// mapping engine looks devices up by VID/PID and aggregates across multiple
// slots holding the same VID/PID.
//
// Axis/button/hat presence is inferred lazily from observed reports —
// counts start at 0 and grow as the device emits fields. Same semantics
// as the Teensy build.

#pragma once
#include <Arduino.h>

#define DEVICE_POOL_SIZE 8
#define DEVICE_MAX_AXES 8
// Plenty of headroom for USB string descriptors (EspUsbHost stores them as
// Strings; we copy into fixed buffers for stable c_str access from the
// protocol layer's event emitters, which run on a different task context
// than the USB host task).
#define DEVICE_STR_LEN 64

class GenericJoystickHID {
public:
  // Hat Switch value when the D-pad is released / centred. Real directions
  // are 0..7 (N, NE, E, SE, S, SW, W, NW — clockwise from North per HID
  // Usage Tables).
  static constexpr uint8_t HAT_RELEASED = 0xFF;

  // Standard HID boot-keyboard array holds up to 6 simultaneously pressed
  // keys; we use the same size for storage. Modifier keys (LCtrl/LShift/
  // etc, scancodes 0xE0..0xE7) come through the same array, just appended
  // when EspUsbHost reports their modifier-mask bits as set.
  static constexpr uint8_t MAX_KEYS_PRESSED = 6;

  // ---- State accessors ----
  bool connected() const { return m_claimed; }
  uint8_t address() const { return m_address; }
  uint16_t vid() const { return m_vid; }
  uint16_t pid() const { return m_pid; }
  uint32_t buttons() const { return m_buttons; }
  uint16_t axis(uint8_t i) const { return (i < DEVICE_MAX_AXES) ? m_axes[i] : 0; }
  uint8_t hat() const { return m_hat; }
  bool hasHat() const { return m_hasHat; }
  uint8_t keyAt(uint8_t i) const { return (i < MAX_KEYS_PRESSED) ? m_keys[i] : 0; }
  bool hasKeyboard() const { return m_hasKeyboard; }
  bool isKeyPressed(uint8_t scancode) const {
    if (!scancode)
      return false;
    for (uint8_t i = 0; i < MAX_KEYS_PRESSED; ++i) {
      if (m_keys[i] == scancode)
        return true;
    }
    return false;
  }
  uint8_t buttonCount() const { return m_buttonCount; }
  uint8_t axisCount() const { return m_axisCount; }

  // USB string descriptors captured at claim time (sanitised to printable
  // ASCII, never null). Empty string when the device reports no such string.
  const char* manufacturerName() const { return m_manufacturer; }
  const char* productName() const { return m_product; }

  // Monotonic counter incremented on every input report that changed any
  // observable state — lets the protocol layer detect "something happened"
  // for the live_inputs stream without per-field comparisons.
  uint32_t changeSeq() const { return m_changeSeq; }

  // ---- Internal API — invoked by device_pool.cpp's USB callback handlers
  // ----
  // (Not literally private because we want field access from free functions
  // without friend declarations; treat as implementation detail.)
  bool m_claimed = false;
  uint8_t m_address = 0;
  uint16_t m_vid = 0;
  uint16_t m_pid = 0;
  char m_manufacturer[DEVICE_STR_LEN] = {0};
  char m_product[DEVICE_STR_LEN] = {0};
  uint32_t m_buttons = 0;
  uint16_t m_axes[DEVICE_MAX_AXES] = {0};
  uint8_t m_hat = HAT_RELEASED;
  bool m_hasHat = false;
  uint8_t m_keys[MAX_KEYS_PRESSED] = {0};
  bool m_hasKeyboard = false;
  uint8_t m_modifierMask = 0;
  uint8_t m_buttonCount = 0;
  uint8_t m_axisCount = 0;
  uint32_t m_changeSeq = 0;
  // Bitmask of USB interface numbers this slot has actually received HID
  // reports on (bit N = interface N, capped at 8). A device that claims two
  // HID interfaces but only ever emits reports on one tells us the other is
  // an idle stub — a candidate to skip and reclaim its HCD channel. Set in
  // the gamepad/keyboard handlers, surfaced in the channel-detail dump.
  uint8_t m_seenIfaceMask = 0;
};

// ---------------- Pool API ----------------

// Initialise the underlying USB host driver and wire callbacks into the
// slot pool. Call from setup() AFTER Serial is up (so claim/disconnect
// log lines actually print). Returns false if USB host init fails.
bool devicePoolBegin();

// Pool size is fixed at compile time.
uint8_t devicePoolSize();

// Returns the slot pointer (always non-null for i < devicePoolSize()).
// Caller checks .connected() before reading state.
GenericJoystickHID* devicePoolSlot(uint8_t i);

// Diagnostic: report every HID collection top-usage observed since the
// last call. Used by the JSON protocol's stream of [USB/Info] log lines
// so the user can see "unrecognised gamepad VID=… PID=…" for devices the
// mapping engine won't claim. The current ESP32 port doesn't expose
// per-collection topusages from EspUsbHost (the library hides them
// behind the parsed gamepad/keyboard split), so this stays a stub for
// API compatibility with the Teensy build's protocol layer.
uint8_t devicePoolGetLoggedCollections(uint16_t* vids, uint16_t* pids, uint32_t* topusages,
                                       uint8_t maxCount);

// Diagnostic: returns the number of EspUsbHost-tracked endpoint channels
// currently in use (or 0 if the host isn't running yet). Surfaced via the
// "u" CLI command so the user can see how close they are to the 8-channel
// EHCI limit on ESP32-S3 — a known scaling cliff when going through hubs.
uint8_t devicePoolEndpointChannels();

// Total HCD channels the active USB host controller exposes (0 if host not
// running). 8 on ESP32-S3 / P4 full-speed core, 16 on the P4 high-speed
// core. Pairs with devicePoolEndpointChannels() for the "N / MAX" display.
uint8_t devicePoolMaxChannels();

// Diagnostic: dump the per-device USB interface + endpoint + HCD-channel
// breakdown for every device EspUsbHost currently tracks (including hubs).
// Prints which interface CLASS each device exposes (HID / CDC / vendor /
// …) and whether each was claimed, plus the running channel total against
// the ESP32-S3's hard 8-channel ceiling. This is the tool for answering
// "why did the Nth device fail to enumerate" — composite devices that
// expose CDC or vendor interfaces alongside their HID gamepad burn extra
// channels we never use. Emitted automatically on each device attach (so
// it shows up in webconfig's Logs tab without needing CLI access) and
// also reachable via the "u" CLI command.
void devicePoolDumpUsbDetail();
