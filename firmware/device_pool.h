// USB HID joystick device pool.
//
// Replaces the pre-refactor hardcoded GenericJoystickHID instances keyed by
// VID/PID. Now: N=8 generic slots claim any Joystick HID collection
// (topusage 0x10004) in connect order and record the actual VID/PID of the
// device that landed there. The mapping engine looks up devices by VID/PID,
// aggregating across multiple slots when needed (e.g. two same-VID/PID
// combo devices).
//
// Axis/button counts are inferred lazily from observed HID reports — the
// USBHIDInput API doesn't expose descriptor metadata after parsing. A freshly
// attached device will show counts=0 until it sends its first input report.

#pragma once
#include <Arduino.h>
#include <USBHost_t36.h>

#define DEVICE_POOL_SIZE 8
#define DEVICE_MAX_AXES  8
// USB string descriptors (manufacturer/product) are down-converted to ASCII
// by USBHost_t36 and share a single 50-byte per-device buffer across all three
// strings, so each individual string fits comfortably here.
#define DEVICE_STR_LEN   48

class GenericJoystickHID : public USBHIDInput {
public:
  GenericJoystickHID();

  static constexpr uint32_t TOPUSAGE_JOYSTICK  = 0x10004;
  static constexpr uint32_t TOPUSAGE_GAMEPAD   = 0x10005;
  static constexpr uint32_t TOPUSAGE_KEYBOARD  = 0x10006;
  static constexpr uint32_t TOPUSAGE_MULTIAXIS = 0x10008;

  // Hat Switch value when the D-pad is released / centred. Real directions
  // are 0..7 (N, NE, E, SE, S, SW, W, NW — clockwise from North per HID
  // Usage Tables).
  static constexpr uint8_t HAT_RELEASED = 0xFF;

  // Standard HID boot-keyboard array holds up to 6 simultaneously pressed
  // keys; we use the same size for storage. Modifier keys (LCtrl/LShift/
  // etc, scancodes 0xE0..0xE7) come through the same code path here, so
  // they get a slot in the array just like printable keys.
  static constexpr uint8_t MAX_KEYS_PRESSED = 6;

  // USBHIDInput overrides
  hidclaim_t claim_collection(USBHIDParser* driver, Device_t* dev, uint32_t topusage) override;
  void       disconnect_collection(Device_t* dev) override;
  void       hid_input_data(uint32_t usage, int32_t value) override;
  bool       hid_process_in_data(const Transfer_t*)  override { return false; }
  bool       hid_process_out_data(const Transfer_t*) override { return false; }
  void       hid_input_begin(uint32_t, uint32_t, int, int) override {}
  void       hid_input_end() override {}

  // State accessors
  bool     connected()   const { return m_claimed; }
  uint16_t vid()         const { return m_vid; }
  uint16_t pid()         const { return m_pid; }
  uint32_t buttons()     const { return m_buttons; }
  uint16_t axis(uint8_t i) const { return (i < DEVICE_MAX_AXES) ? m_axes[i] : 0; }
  uint8_t  hat()         const { return m_hat; }
  bool     hasHat()      const { return m_hasHat; }
  uint8_t  keyAt(uint8_t i) const { return (i < MAX_KEYS_PRESSED) ? m_keys[i] : 0; }
  bool     hasKeyboard() const { return m_hasKeyboard; }
  bool     isKeyPressed(uint8_t scancode) const {
    if (!scancode) return false;
    for (uint8_t i = 0; i < MAX_KEYS_PRESSED; ++i) {
      if (m_keys[i] == scancode) return true;
    }
    return false;
  }
  uint8_t  buttonCount() const { return m_buttonCount; }
  uint8_t  axisCount()   const { return m_axisCount; }
  uint8_t  hubPort()     const { return m_hubPort; }

  // USB string descriptors captured at claim time (sanitised to printable
  // ASCII, never null). Empty string when the device reports no such string.
  const char* manufacturerName() const { return m_manufacturer; }
  const char* productName()      const { return m_product; }

  // Monotonic counter incremented on every input report that changed any
  // observable state — lets the protocol layer detect "something happened"
  // for the live_inputs stream without per-field comparisons.
  uint32_t changeSeq()   const { return m_changeSeq; }

private:
  bool      m_claimed     = false;
  uint16_t  m_vid         = 0;
  uint16_t  m_pid         = 0;
  char      m_manufacturer[DEVICE_STR_LEN] = {0};
  char      m_product[DEVICE_STR_LEN]      = {0};
  uint32_t  m_buttons     = 0;
  uint16_t  m_axes[DEVICE_MAX_AXES] = {0};
  uint8_t   m_hat         = HAT_RELEASED;
  bool      m_hasHat      = false;
  uint8_t   m_keys[MAX_KEYS_PRESSED] = {0};
  bool      m_hasKeyboard = false;
  uint8_t   m_buttonCount = 0;
  uint8_t   m_axisCount   = 0;
  uint8_t   m_hubPort     = 0;
  uint32_t  m_changeSeq   = 0;
};

// ---------------- Pool API ----------------

// Pool size is fixed at compile time.
uint8_t devicePoolSize();

// Returns the slot pointer (always non-null for i < devicePoolSize()).
// Caller checks .connected() before reading state.
GenericJoystickHID* devicePoolSlot(uint8_t i);

// Safely fetches and clears logged HID collections
uint8_t devicePoolGetLoggedCollections(uint16_t* vids, uint16_t* pids, uint32_t* topusages, uint8_t maxCount);
