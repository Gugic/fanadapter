#include "device_pool.h"

GenericJoystickHID::GenericJoystickHID() {
  USBHIDParser::driver_ready_for_hid_collection(this);
}

struct LoggedCollection {
  uint16_t vid;
  uint16_t pid;
  uint32_t topusage;
};
#define MAX_LOGGED_COLLECTIONS 16
static LoggedCollection g_loggedCollections[MAX_LOGGED_COLLECTIONS];
static volatile uint8_t g_loggedCollectionsCount = 0;

// Copy a USBHost_t36 device string into a fixed buffer, keeping only printable
// ASCII (the lib's UTF-16→ASCII pass can leave stray high bytes that would make
// the protocol's JSON invalid UTF-8) and right-trimming trailing spaces. Always
// null-terminates; `src == nullptr` yields an empty string.
static void copyDeviceString(char* dst, uint8_t dstLen, const uint8_t* src) {
  uint8_t n = 0;
  if (src) {
    for (; n < (uint8_t)(dstLen - 1) && src[n]; ++n) {
      const uint8_t c = src[n];
      dst[n] = (c >= 0x20 && c <= 0x7E) ? (char)c : '?';
    }
  }
  dst[n] = 0;
  while (n > 0 && dst[n - 1] == ' ') dst[--n] = 0;
}

hidclaim_t GenericJoystickHID::claim_collection(USBHIDParser* /*driver*/,
                                                Device_t* dev,
                                                uint32_t  topusage) {
  // Log this collection if not already logged
  bool foundLog = false;
  for (uint8_t i = 0; i < g_loggedCollectionsCount; ++i) {
    if (g_loggedCollections[i].vid == dev->idVendor &&
        g_loggedCollections[i].pid == dev->idProduct &&
        g_loggedCollections[i].topusage == topusage) {
      foundLog = true;
      break;
    }
  }
  if (!foundLog && g_loggedCollectionsCount < MAX_LOGGED_COLLECTIONS) {
    g_loggedCollections[g_loggedCollectionsCount++] = {
      dev->idVendor,
      dev->idProduct,
      topusage
    };
  }

  if (topusage != TOPUSAGE_JOYSTICK &&
      topusage != TOPUSAGE_GAMEPAD &&
      topusage != TOPUSAGE_KEYBOARD &&
      topusage != TOPUSAGE_MULTIAXIS) {
    return CLAIM_NO;
  }
  if (m_claimed)                     return CLAIM_NO;

  m_claimed     = true;
  m_vid         = dev->idVendor;
  m_pid         = dev->idProduct;
  // Bind the base-class device pointer so the inherited manufacturer()/
  // product() accessors resolve, then snapshot the strings. They're already
  // populated at claim time — enumeration reads them before claim_drivers().
  mydevice      = dev;
  copyDeviceString(m_manufacturer, DEVICE_STR_LEN, manufacturer());
  copyDeviceString(m_product,      DEVICE_STR_LEN, product());
  m_buttons     = 0;
  memset(m_axes, 0, sizeof(m_axes));
  m_hat         = HAT_RELEASED;
  m_hasHat      = false;
  memset(m_keys, 0, sizeof(m_keys));
  m_hasKeyboard = false;
  m_buttonCount = 0;
  m_axisCount   = 0;
  m_hubPort     = dev->hub_port;
  m_changeSeq++;

  Serial.print("[USB] slot claimed  VID=0x");
  Serial.print(dev->idVendor, HEX);
  Serial.print("  PID=0x");
  Serial.print(dev->idProduct, HEX);
  Serial.print("  \"");
  Serial.print(m_manufacturer);
  if (m_manufacturer[0] && m_product[0]) Serial.print(' ');
  Serial.print(m_product);
  Serial.print("\"  hub_port=");
  Serial.println(dev->hub_port);

  return CLAIM_REPORT;
}

void GenericJoystickHID::disconnect_collection(Device_t* /*dev*/) {
  Serial.print("[USB] slot disconnected  VID=0x");
  Serial.print(m_vid, HEX);
  Serial.print("  PID=0x");
  Serial.println(m_pid, HEX);

  m_claimed     = false;
  mydevice      = NULL;  // device's strbuf is freed on disconnect — don't read it
  m_buttons     = 0;
  memset(m_axes, 0, sizeof(m_axes));
  m_hat         = HAT_RELEASED;
  memset(m_keys, 0, sizeof(m_keys));
  // Keep m_vid/m_pid/m_manufacturer/m_product/m_hasHat/m_hasKeyboard/
  // m_buttonCount/m_axisCount around briefly so any final log lines about
  // this slot still make sense — the next claim_collection overwrites them.
  m_changeSeq++;
}

void GenericJoystickHID::hid_input_data(uint32_t usage, int32_t value) {
  const uint16_t page = usage >> 16;
  const uint16_t id   = usage & 0xFFFF;

  if (page == 0x09 && id >= 1 && id <= 32) {
    // Button page — 1-indexed button id, bit (id-1)
    const uint8_t  bit  = (uint8_t)(id - 1);
    const uint32_t mask = (1u << bit);
    const uint32_t newButtons = value ? (m_buttons | mask) : (m_buttons & ~mask);
    if (newButtons != m_buttons) {
      m_buttons = newButtons;
      m_changeSeq++;
    }
    if (bit + 1 > m_buttonCount) m_buttonCount = bit + 1;
  } else if (page == 0x01 && id == 0x39) {
    // Hat Switch — first-class direction value, NOT four virtual buttons.
    // The HID spec uses 0..7 walking clockwise from North (N, NE, E, SE,
    // S, SW, W, NW); 8 / 15 / -1 / out-of-range all mean "released".
    // The mapping layer (`INPUT_HAT` bindings) matches strict direction —
    // diagonals only fire bindings explicitly bound to that diagonal.
    const uint8_t newHat = (value >= 0 && value <= 7) ? (uint8_t)value : HAT_RELEASED;
    // Diagnostic: log when the raw value or decoded direction transitions.
    // Only when WebSerial is open — otherwise these would queue into the
    // CDC TX buffer and eventually stall the main loop. Goes silent once
    // the hat sits at a stable value.
    if (Serial && (m_hat != newHat || !m_hasHat)) {
      Serial.print("[HID/Hat] VID=0x");
      Serial.print(m_vid, HEX);
      Serial.print(" PID=0x");
      Serial.print(m_pid, HEX);
      Serial.print(" raw=");
      Serial.print(value);
      Serial.print(" decoded=");
      if (newHat == HAT_RELEASED) Serial.println("RELEASED");
      else                        Serial.println((int)newHat);
    }
    m_hasHat = true;
    if (m_hat != newHat) {
      m_hat = newHat;
      m_changeSeq++;
    }
  } else if (page == 0x01) {
    // Generic Desktop — axes are usage 0x30..0x37 (X, Y, Z, Rx, Ry, Rz,
    // Slider, Dial). Anything outside that range we ignore here.
    if (id >= 0x30 && id <= 0x37) {
      const uint8_t  axisIdx  = (uint8_t)(id - 0x30);
      const uint16_t newValue = (uint16_t)value;
      if (m_axes[axisIdx] != newValue) {
        m_axes[axisIdx] = newValue;
        m_changeSeq++;
      }
      if (axisIdx + 1 > m_axisCount) m_axisCount = axisIdx + 1;
    }
  } else if (page == 0x07) {
    // Keyboard/Keypad usage page. `id` is the HID scancode (0x04 = A,
    // 0x05 = B, …, 0x1E..0x27 = 1..0, 0x28 = Enter, … 0xE0..0xE7 =
    // modifiers). `value` is 1 on press, 0 on release. USBHIDParser
    // synthesises one event per usage even for array-typed report items
    // (so we don't need to track the 6-slot key array directly).
    m_hasKeyboard = true;
    const uint8_t code = (uint8_t)(id & 0xFF);
    if (!code) return;  // 0x00 = "no key" filler, never a real scancode
    if (value) {
      // Press — append to the first empty slot if not already tracked.
      bool present = false;
      int8_t firstEmpty = -1;
      for (uint8_t i = 0; i < MAX_KEYS_PRESSED; ++i) {
        if (m_keys[i] == code) { present = true; break; }
        if (firstEmpty < 0 && m_keys[i] == 0) firstEmpty = i;
      }
      if (!present && firstEmpty >= 0) {
        m_keys[firstEmpty] = code;
        m_changeSeq++;
      }
    } else {
      // Release — clear the slot holding this scancode.
      for (uint8_t i = 0; i < MAX_KEYS_PRESSED; ++i) {
        if (m_keys[i] == code) {
          m_keys[i] = 0;
          m_changeSeq++;
          break;
        }
      }
    }
  }
  // Vendor-defined pages and unknown usages: ignored. The SP Pro pedals'
  // 18 bytes of FF00 vendor data falls here; harmless.
}

// ---------------- Pool storage ----------------

static GenericJoystickHID g_devicePool[DEVICE_POOL_SIZE];

uint8_t devicePoolSize() {
  return DEVICE_POOL_SIZE;
}

GenericJoystickHID* devicePoolSlot(uint8_t i) {
  if (i >= DEVICE_POOL_SIZE) return nullptr;
  return &g_devicePool[i];
}

uint8_t devicePoolGetLoggedCollections(uint16_t* vids, uint16_t* pids, uint32_t* topusages, uint8_t maxCount) {
  noInterrupts();
  uint8_t count = g_loggedCollectionsCount;
  if (count > maxCount) count = maxCount;
  for (uint8_t i = 0; i < count; ++i) {
    if (vids)      vids[i]      = g_loggedCollections[i].vid;
    if (pids)      pids[i]      = g_loggedCollections[i].pid;
    if (topusages) topusages[i] = g_loggedCollections[i].topusage;
  }
  // Shift remaining
  if (count < g_loggedCollectionsCount) {
    memmove(g_loggedCollections, &g_loggedCollections[count], (g_loggedCollectionsCount - count) * sizeof(LoggedCollection));
    g_loggedCollectionsCount -= count;
  } else {
    g_loggedCollectionsCount = 0;
  }
  interrupts();
  return count;
}
