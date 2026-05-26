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
      topusage != TOPUSAGE_MULTIAXIS) {
    return CLAIM_NO;
  }
  if (m_claimed)                     return CLAIM_NO;

  m_claimed     = true;
  m_vid         = dev->idVendor;
  m_pid         = dev->idProduct;
  m_buttons     = 0;
  memset(m_axes, 0, sizeof(m_axes));
  m_buttonCount = 0;
  m_axisCount   = 0;
  m_hubPort     = dev->hub_port;
  m_changeSeq++;

  Serial.print("[USB] slot claimed  VID=0x");
  Serial.print(dev->idVendor, HEX);
  Serial.print("  PID=0x");
  Serial.print(dev->idProduct, HEX);
  Serial.print("  hub_port=");
  Serial.println(dev->hub_port);

  return CLAIM_REPORT;
}

void GenericJoystickHID::disconnect_collection(Device_t* /*dev*/) {
  Serial.print("[USB] slot disconnected  VID=0x");
  Serial.print(m_vid, HEX);
  Serial.print("  PID=0x");
  Serial.println(m_pid, HEX);

  m_claimed     = false;
  m_buttons     = 0;
  memset(m_axes, 0, sizeof(m_axes));
  // Keep m_vid/m_pid/m_buttonCount/m_axisCount around briefly so any final
  // log lines about this slot still make sense — the next claim_collection
  // overwrites them.
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
  return count;
}
