#include "device_pool.h"

#include <EspUsbHost.h>
#include <string.h>

#include "protocol.h" // serialLockTake / serialLockGive

// ---------------- USB host singleton ----------------
// EspUsbHost spawns a FreeRTOS task that drives the ESP-IDF USB host. All
// callbacks run on THAT task; the slot fields are read from the main loop
// task via mappingTick(). The data races are loose-but-safe: we update the
// observable fields first and bump changeSeq last, so a reader that sees
// a new changeSeq is guaranteed to re-read on the next tick and pick up
// the latest values (worst case one extra tick of staleness).

static EspUsbHost g_usb;
static bool g_usbReady = false;

// ---------------- Pool storage ----------------

static GenericJoystickHID g_devicePool[DEVICE_POOL_SIZE];

uint8_t devicePoolSize() { return DEVICE_POOL_SIZE; }

GenericJoystickHID* devicePoolSlot(uint8_t i) {
  if (i >= DEVICE_POOL_SIZE)
    return nullptr;
  return &g_devicePool[i];
}

// ---------------- Slot lookup ----------------

static GenericJoystickHID* slotByAddress(uint8_t addr) {
  if (addr == 0 || addr == 0xff)
    return nullptr;
  for (uint8_t i = 0; i < DEVICE_POOL_SIZE; ++i) {
    if (g_devicePool[i].m_claimed && g_devicePool[i].m_address == addr)
      return &g_devicePool[i];
  }
  return nullptr;
}

static GenericJoystickHID* claimSlot(uint8_t addr) {
  // First-fit: walk the pool, take the first idle slot.
  for (uint8_t i = 0; i < DEVICE_POOL_SIZE; ++i) {
    if (!g_devicePool[i].m_claimed) {
      g_devicePool[i].m_address = addr;
      g_devicePool[i].m_claimed = true;
      return &g_devicePool[i];
    }
  }
  return nullptr;
}

// Copy an EspUsbHost device string into a fixed buffer, keeping only
// printable ASCII (some receivers spit high-byte garbage that would make
// the JSON protocol invalid UTF-8) and right-trimming trailing spaces.
// Always null-terminates; src == nullptr yields an empty string.
static void copyDeviceString(char* dst, uint8_t dstLen, const char* src) {
  uint8_t n = 0;
  if (src) {
    for (; n < (uint8_t)(dstLen - 1) && src[n]; ++n) {
      const uint8_t c = (uint8_t)src[n];
      dst[n] = (c >= 0x20 && c <= 0x7E) ? (char)c : '?';
    }
  }
  dst[n] = 0;
  while (n > 0 && dst[n - 1] == ' ')
    dst[--n] = 0;
}

// ---------------- Device attach / detach ----------------

static void handleConnect(const EspUsbHostDeviceInfo& device) {
  // EspUsbHost surfaces hubs themselves as devices — skip them, the lib
  // handles hub enumeration internally and we'll see the downstream
  // devices as separate connect events.
  if (device.isHub)
    return;
  // Address 0 is the default control pipe before assignment; we should
  // never see a real device with it, but defend anyway.
  if (device.address == 0 || device.address == 0xff)
    return;

  // If this address is already claimed (replug while we hadn't seen the
  // disconnect yet), reuse the existing slot rather than allocating a
  // new one — keeps the slot index stable in the webconfig UI.
  GenericJoystickHID* slot = slotByAddress(device.address);
  if (!slot) {
    slot = claimSlot(device.address);
  }
  if (!slot) {
    serialLockTake();
    Serial.print("[USB] pool full, ignoring device VID=0x");
    Serial.print(device.vid, HEX);
    Serial.print(" PID=0x");
    Serial.println(device.pid, HEX);
    serialLockGive();
    return;
  }

  slot->m_vid = device.vid;
  slot->m_pid = device.pid;
  copyDeviceString(slot->m_manufacturer, DEVICE_STR_LEN, device.manufacturer);
  copyDeviceString(slot->m_product, DEVICE_STR_LEN, device.product);
  slot->m_buttons = 0;
  memset((void*)slot->m_axes, 0, sizeof(slot->m_axes));
  slot->m_hat = GenericJoystickHID::HAT_RELEASED;
  slot->m_hasHat = false;
  memset(slot->m_keys, 0, sizeof(slot->m_keys));
  slot->m_hasKeyboard = false;
  slot->m_modifierMask = 0;
  slot->m_buttonCount = 0;
  slot->m_axisCount = 0;
  slot->m_seenIfaceMask = 0;
  slot->m_changeSeq++;

  serialLockTake();
  Serial.print("[USB] slot claimed addr=");
  Serial.print(device.address);
  Serial.print(" VID=0x");
  Serial.print(device.vid, HEX);
  Serial.print(" PID=0x");
  Serial.print(device.pid, HEX);
  Serial.print(" \"");
  Serial.print(slot->m_manufacturer);
  if (slot->m_manufacturer[0] && slot->m_product[0])
    Serial.print(' ');
  Serial.print(slot->m_product);
  Serial.print("\" parent=");
  Serial.print(device.parentAddress);
  Serial.print(":port");
  Serial.println(device.portId);
  serialLockGive();
}

static void handleDisconnect(const EspUsbHostDeviceInfo& device) {
  GenericJoystickHID* slot = slotByAddress(device.address);
  if (!slot)
    return;
  serialLockTake();
  Serial.print("[USB] slot released addr=");
  Serial.print(device.address);
  Serial.print(" VID=0x");
  Serial.print(slot->m_vid, HEX);
  Serial.print(" PID=0x");
  Serial.println(slot->m_pid, HEX);
  serialLockGive();

  slot->m_claimed = false;
  slot->m_buttons = 0;
  memset((void*)slot->m_axes, 0, sizeof(slot->m_axes));
  slot->m_hat = GenericJoystickHID::HAT_RELEASED;
  memset(slot->m_keys, 0, sizeof(slot->m_keys));
  slot->m_modifierMask = 0;
  slot->m_seenIfaceMask = 0;
  // Keep m_vid/m_pid/m_manufacturer/m_product/m_hasHat/m_hasKeyboard/
  // m_buttonCount/m_axisCount around briefly so any final log lines about
  // this slot still make sense — the next claim overwrites them.
  slot->m_changeSeq++;
}

// ---------------- Input dispatch ----------------

static void handleGamepad(const EspUsbHostGamepadEvent& event) {
  GenericJoystickHID* slot = slotByAddress(event.address);
  if (!slot) {
    // Race: input fired before onDeviceConnected reached us. Reserve a
    // slot lazily so the report isn't dropped silently. VID/PID and
    // strings come from the event payload (EspUsbHost mirrors them onto
    // every input event).
    slot = claimSlot(event.address);
    if (!slot)
      return;
    slot->m_vid = event.vid;
    slot->m_pid = event.pid;
    copyDeviceString(slot->m_manufacturer, DEVICE_STR_LEN, event.manufacturer);
    copyDeviceString(slot->m_product, DEVICE_STR_LEN, event.product);
  }

  // Record that this interface actually produced a report (see m_seenIfaceMask).
  slot->m_seenIfaceMask |= (uint8_t)(1u << (event.interfaceNumber & 0x07));

  // Walk parsed fields: buttons on usage page 0x09, generic-desktop axes
  // (X/Y/Z/Rx/Ry/Rz/Slider/Dial = 0x30..0x37) and hat (0x39) on page
  // 0x01. Anything else (vendor-defined pages, consumer controls
  // accidentally bundled in) is ignored — the mapping schema only knows
  // about button bits, axis indices, and hat directions.
  uint32_t newButtons = 0;     // rebuilt fresh each report
  bool buttonChange = false;   // any button bit changed
  bool axisChange = false;     // any axis value changed
  bool hatChange = false;      // hat value changed
  bool sawAnyButton = false;   // tracks button-page presence
  uint8_t maxButtonBit = 0;    // for lazy buttonCount discovery
  uint8_t maxAxisIdx = 0;      // for lazy axisCount discovery
  bool sawAnyAxis = false;

  for (size_t i = 0; i < event.fieldCount; ++i) {
    const EspUsbHostHIDFieldValue& f = event.fields[i];

    if (f.usagePage == 0x09) {
      // Button Page — 1-indexed usage, bit (usage - 1).
      if (f.usage == 0 || f.usage > 32)
        continue;
      sawAnyButton = true;
      const uint8_t bit = (uint8_t)(f.usage - 1);
      if (f.value)
        newButtons |= (1u << bit);
      if (bit + 1 > maxButtonBit)
        maxButtonBit = bit + 1;
    } else if (f.usagePage == 0x01 && f.usage == 0x39) {
      // Hat Switch. Values 0..7 are real directions; everything else
      // means released. Diagonal directions don't fire cardinal
      // bindings (the mapping engine matches strictly).
      const uint8_t newHat = (f.value >= 0 && f.value <= 7)
                                 ? (uint8_t)f.value
                                 : GenericJoystickHID::HAT_RELEASED;
      if (!slot->m_hasHat || slot->m_hat != newHat)
        hatChange = true;
      slot->m_hasHat = true;
      slot->m_hat = newHat;
    } else if (f.usagePage == 0x01 && f.usage >= 0x30 && f.usage <= 0x37) {
      // Generic Desktop axes — X/Y/Z/Rx/Ry/Rz/Slider/Dial.
      const uint8_t axisIdx = (uint8_t)(f.usage - 0x30);
      if (axisIdx >= DEVICE_MAX_AXES)
        continue;
      sawAnyAxis = true;
      // Silent cast int32 → uint16 mirrors the Teensy build's behaviour
      // (USBHIDParser also produced int32 values that we narrowed). The
      // webconfig "Listen" calibration handles whatever range the device
      // actually uses — including signed ranges where 0 sits mid-scale.
      const uint16_t v = (uint16_t)(uint32_t)f.value;
      if (slot->m_axes[axisIdx] != v)
        axisChange = true;
      slot->m_axes[axisIdx] = v;
      if (axisIdx + 1 > maxAxisIdx)
        maxAxisIdx = axisIdx + 1;
    }
  }

  if (sawAnyButton) {
    if (newButtons != slot->m_buttons) {
      slot->m_buttons = newButtons;
      buttonChange = true;
    }
    if (maxButtonBit > slot->m_buttonCount)
      slot->m_buttonCount = maxButtonBit;
  }
  if (sawAnyAxis && maxAxisIdx > slot->m_axisCount)
    slot->m_axisCount = maxAxisIdx;

  if (buttonChange || axisChange || hatChange)
    slot->m_changeSeq++;
}

static void pressKey(GenericJoystickHID* slot, uint8_t code) {
  if (!code)
    return;
  for (uint8_t i = 0; i < GenericJoystickHID::MAX_KEYS_PRESSED; ++i) {
    if (slot->m_keys[i] == code)
      return; // already tracked
  }
  for (uint8_t i = 0; i < GenericJoystickHID::MAX_KEYS_PRESSED; ++i) {
    if (slot->m_keys[i] == 0) {
      slot->m_keys[i] = code;
      slot->m_changeSeq++;
      return;
    }
  }
  // Buffer full — silently drop. Boot keyboards report rollover via 0x01
  // in slot 0 which our reader doesn't surface either; the mapping engine
  // only cares about the specific scancodes the user bound.
}

static void releaseKey(GenericJoystickHID* slot, uint8_t code) {
  if (!code)
    return;
  for (uint8_t i = 0; i < GenericJoystickHID::MAX_KEYS_PRESSED; ++i) {
    if (slot->m_keys[i] == code) {
      slot->m_keys[i] = 0;
      slot->m_changeSeq++;
      return;
    }
  }
}

static void handleKeyboard(const EspUsbHostKeyboardEvent& event) {
  GenericJoystickHID* slot = slotByAddress(event.address);
  if (!slot) {
    slot = claimSlot(event.address);
    if (!slot)
      return;
    slot->m_vid = event.vid;
    slot->m_pid = event.pid;
    copyDeviceString(slot->m_manufacturer, DEVICE_STR_LEN, event.manufacturer);
    copyDeviceString(slot->m_product, DEVICE_STR_LEN, event.product);
  }
  slot->m_hasKeyboard = true;
  slot->m_seenIfaceMask |= (uint8_t)(1u << (event.interfaceNumber & 0x07));

  // Modifier diff first — EspUsbHost reports the full current modifier
  // mask on every event, so we can derive press/release for each modifier
  // bit by comparing against our shadow. Modifiers occupy scancodes
  // 0xE0..0xE7 in HID Keyboard/Keypad usage tables.
  const uint8_t prev = slot->m_modifierMask;
  const uint8_t curr = event.modifiers;
  if (prev != curr) {
    for (uint8_t i = 0; i < 8; ++i) {
      const uint8_t mask = 1u << i;
      const uint8_t scancode = 0xE0 + i;
      if ((curr & mask) && !(prev & mask))
        pressKey(slot, scancode);
      else if (!(curr & mask) && (prev & mask))
        releaseKey(slot, scancode);
    }
    slot->m_modifierMask = curr;
  }

  // Then the main keycode press/release reported in this event.
  if (event.keycode != 0) {
    if (event.pressed)
      pressKey(slot, event.keycode);
    else if (event.released)
      releaseKey(slot, event.keycode);
  }
}

// ---------------- Report descriptor summary (diagnostic) ----------------
//
// One-shot per interface: when EspUsbHost fetches a HID report descriptor,
// we walk it just far enough to characterise what the interface carries —
// its top-level usage and how many input bits live on the Button (0x09),
// Generic-Desktop (0x01), and Vendor (0xFF00+) usage pages. This is the
// evidence for "can we skip interface #1 to reclaim a channel?": an
// interface that declares zero button/axis input (a vendor stub) is a safe
// skip; one that declares real buttons or axes is not.

static const char* topUsageName(uint16_t page, uint16_t usage) {
  if (page == 0x01) {
    switch (usage) {
      case 0x02:
        return "Mouse";
      case 0x04:
        return "Joystick";
      case 0x05:
        return "Gamepad";
      case 0x06:
        return "Keyboard";
      case 0x07:
        return "Keypad";
      case 0x08:
        return "MultiAxis";
      default:
        return "GenericDesktop";
    }
  }
  if (page == 0x0C)
    return "Consumer";
  if (page >= 0xFF00)
    return "Vendor";
  return "?";
}

static void handleReportDescriptor(const EspUsbHostHIDReportDescriptor& d) {
  const uint8_t* p = d.data;
  const size_t n = d.length;

  uint16_t usagePage = 0;    // current global Usage Page
  uint16_t pendingUsage = 0; // last local Usage (names the next collection)
  uint16_t topPage = 0;      // first top-level collection's page/usage
  uint16_t topUsage = 0;
  bool gotTop = false;
  uint32_t reportSize = 0;  // bits per field
  uint32_t reportCount = 0; // field count
  uint32_t btnBits = 0;     // input bits on Button page (0x09)
  uint32_t axisBits = 0;    // input bits on Generic Desktop (0x01)
  uint32_t vendorBits = 0;  // input bits on Vendor pages (>=0xFF00)
  uint32_t otherBits = 0;   // input bits on any other page

  size_t i = 0;
  while (i < n) {
    const uint8_t b0 = p[i];
    if (b0 == 0xFE) { // long item: skip
      const uint8_t sz = (i + 1 < n) ? p[i + 1] : 0;
      i += 3 + sz;
      continue;
    }
    const uint8_t bSize = b0 & 0x03;             // 0,1,2,3
    const uint8_t dataLen = (bSize == 3) ? 4 : bSize; // -> 0,1,2,4 bytes
    const uint8_t bType = (uint8_t)((b0 >> 2) & 0x03);
    const uint8_t bTag = (uint8_t)((b0 >> 4) & 0x0F);
    uint32_t val = 0;
    for (uint8_t k = 0; k < dataLen && (i + 1 + k) < n; ++k)
      val |= (uint32_t)p[i + 1 + k] << (8 * k);

    if (bType == 1) { // Global
      if (bTag == 0x0)
        usagePage = (uint16_t)val; // Usage Page
      else if (bTag == 0x7)
        reportSize = val; // Report Size
      else if (bTag == 0x9)
        reportCount = val; // Report Count
    } else if (bType == 2) {        // Local
      if (bTag == 0x0)
        pendingUsage = (uint16_t)val; // Usage
    } else if (bType == 0) {          // Main
      if (bTag == 0xA) {              // Collection
        if (!gotTop) {
          topPage = usagePage;
          topUsage = pendingUsage;
          gotTop = true;
        }
      } else if (bTag == 0x8) { // Input
        const uint32_t bits = reportSize * reportCount;
        if (!(val & 0x01)) { // skip constant/padding fields
          if (usagePage == 0x09)
            btnBits += bits;
          else if (usagePage == 0x01)
            axisBits += bits;
          else if (usagePage >= 0xFF00)
            vendorBits += bits;
          else
            otherBits += bits;
        }
      }
      pendingUsage = 0; // locals reset after each Main item
    }
    i += 1 + dataLen;
  }

  serialLockTake();
  Serial.print("[USB] HID-desc addr=");
  Serial.print(d.address);
  Serial.print(" iface=");
  Serial.print(d.interfaceNumber);
  Serial.print(" top=0x");
  if (topPage < 0x10)
    Serial.print('0');
  Serial.print(topPage, HEX);
  Serial.print("/0x");
  if (topUsage < 0x10)
    Serial.print('0');
  Serial.print(topUsage, HEX);
  Serial.print('(');
  Serial.print(topUsageName(topPage, topUsage));
  Serial.print(") inputs: btn=");
  Serial.print(btnBits);
  Serial.print("b axis=");
  Serial.print(axisBits);
  Serial.print("b vendor=");
  Serial.print(vendorBits);
  Serial.print("b other=");
  Serial.print(otherBits);
  Serial.print("b  (descLen=");
  Serial.print(n);
  Serial.println(")");
  serialLockGive();
}

// ---------------- Lifecycle ----------------

bool devicePoolBegin() {
  // Wire callbacks BEFORE begin() so we don't miss the first device's
  // connect event (the library posts queued events as soon as the host
  // task starts).
  g_usb.onDeviceConnected(&handleConnect);
  g_usb.onDeviceDisconnected(&handleDisconnect);
  g_usb.onGamepad(&handleGamepad);
  g_usb.onKeyboard(&handleKeyboard);
  g_usb.onHIDReportDescriptor(&handleReportDescriptor);

  // On the ESP32-P4 there are TWO USB OTG controllers: a High-Speed core
  // with 16 HCD channels and a Full-Speed core with only 8 (same as the
  // S3 — no gain). We explicitly select HIGH_SPEED so we get the 16-channel
  // headroom that motivated this board. On the S3 (and any non-P4 target)
  // hostPeripheralMap() ignores the port field and this is a harmless no-op,
  // so the call is unconditional.
  EspUsbHostConfig cfg;
  cfg.port = ESP_USB_HOST_PORT_HIGH_SPEED;
  if (!g_usb.begin(cfg)) {
    serialLockTake();
    Serial.print("[USB] host begin() FAILED: ");
    Serial.println(g_usb.lastErrorName());
    serialLockGive();
    g_usbReady = false;
    return false;
  }
  g_usbReady = true;
  serialLockTake();
  Serial.print("[USB] host begin() OK — ");
  Serial.print((unsigned)g_usb.maxEndpointChannelCount());
  Serial.println(" HCD channels available");
  serialLockGive();
  return true;
}

// Short label for a USB interface bInterfaceClass — just the ones we care
// about distinguishing when accounting for HCD channels. Anything that
// isn't HID is, for this adapter, an interface whose endpoints we claim
// but never use (CDC debug-serial, vendor pages on DIY adapters, etc.).
static const char* usbClassName(uint8_t c) {
  switch (c) {
    case 0x00:
      return "per-iface";
    case 0x01:
      return "audio";
    case 0x02:
      return "CDC-ctrl";
    case 0x03:
      return "HID";
    case 0x08:
      return "MSC";
    case 0x09:
      return "hub";
    case 0x0A:
      return "CDC-data";
    case 0x0B:
      return "smartcard";
    case 0x0E:
      return "video";
    case 0xE0:
      return "wireless";
    case 0xEF:
      return "misc";
    case 0xFE:
      return "app-spec";
    case 0xFF:
      return "vendor";
    default:
      return "?";
  }
}

void devicePoolDumpUsbDetail() {
  if (!g_usbReady)
    return;

  EspUsbHostDeviceInfo devs[16];
  const size_t n = g_usb.getDevices(devs, 16);

  serialLockTake();
  Serial.println("[USB] ---- HCD channel detail ----");
  for (size_t i = 0; i < n; ++i) {
    const EspUsbHostDeviceInfo& di = devs[i];
    Serial.print("  addr=");
    Serial.print(di.address);
    Serial.print(di.isHub ? " [HUB] VID=0x" : "       VID=0x");
    Serial.print(di.vid, HEX);
    Serial.print(" PID=0x");
    Serial.print(di.pid, HEX);
    Serial.print(" \"");
    Serial.print(di.product ? di.product : "");
    Serial.println("\"");

    EspUsbHostInterfaceInfo ifs[8];
    const size_t ni = g_usb.getInterfaces(di.address, ifs, 8);
    for (size_t k = 0; k < ni; ++k) {
      const EspUsbHostInterfaceInfo& it = ifs[k];
      Serial.print("      iface#");
      Serial.print(it.number);
      Serial.print(" cls=0x");
      if (it.interfaceClass < 0x10)
        Serial.print('0');
      Serial.print(it.interfaceClass, HEX);
      Serial.print('(');
      Serial.print(usbClassName(it.interfaceClass));
      Serial.print(") eps=");
      Serial.print(it.endpointCount);
      Serial.println(it.claimed       ? " -> CLAIMED"
                     : it.claimAttempted ? " -> CLAIM-FAILED"
                                         : " -> skipped");
    }
    // ep0 = the device's control pipe (always 1 persistent channel); ep =
    // sum of claimed-interface endpoints; hub = the hub's status interrupt
    // endpoint(s). Each is a dedicated DWC2 host channel.
    Serial.print("      channels: ep0=");
    Serial.print((unsigned)g_usb.ep0ChannelCount(di.address));
    Serial.print(" ep=");
    Serial.print((unsigned)g_usb.endpointChannelCount(di.address));
    Serial.print(" hub=");
    Serial.print((unsigned)g_usb.hubEndpointChannelCount(di.address));
    // Which interface numbers have actually emitted reports so far (live
    // evidence of which ifaces carry input — actuate the device to populate
    // this; an iface that never appears is a stub we could skip).
    const GenericJoystickHID* s = slotByAddress(di.address);
    if (s && s->m_seenIfaceMask) {
      Serial.print(" reportsFrom-iface=[");
      bool first = true;
      for (uint8_t b = 0; b < 8; ++b) {
        if (s->m_seenIfaceMask & (1u << b)) {
          if (!first)
            Serial.print(',');
          Serial.print(b);
          first = false;
        }
      }
      Serial.print(']');
    }
    Serial.println();
  }
  Serial.print("[USB] TOTAL estimated channels = ");
  Serial.print((unsigned)g_usb.estimatedHcdChannelCount());
  Serial.print(" / ");
  Serial.print((unsigned)g_usb.maxEndpointChannelCount());
  Serial.println("  (new devices fail to enumerate once this hits the max)");
  serialLockGive();
}

uint8_t devicePoolGetLoggedCollections(uint16_t* /*vids*/, uint16_t* /*pids*/,
                                       uint32_t* /*topusages*/, uint8_t /*maxCount*/) {
  // EspUsbHost hides per-collection topusages behind its parsed callbacks
  // (we register onGamepad / onKeyboard rather than walking the raw
  // descriptor). The Teensy build used this to surface "unrecognised HID
  // collection VID=… PID=…" log lines for devices the pool didn't claim;
  // here, slot claiming happens at the device level (any non-hub device
  // is claimed) so there's no "claimed but unrecognised collection"
  // intermediate state to report. Returning 0 keeps the protocol layer's
  // drain loop a no-op without forcing it to ifdef around the platform.
  return 0;
}

uint8_t devicePoolEndpointChannels() {
  if (!g_usbReady)
    return 0;
  return (uint8_t)g_usb.endpointChannelCount();
}

uint8_t devicePoolMaxChannels() {
  if (!g_usbReady)
    return 0;
  return (uint8_t)g_usb.maxEndpointChannelCount();
}
