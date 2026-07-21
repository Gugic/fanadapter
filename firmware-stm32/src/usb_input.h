// USB HID device pool — claims HID interfaces, decodes their reports (hid_parse), and exposes them
// to the mapping layer as an InputSource. The TinyUSB host callbacks in main.c forward here.
#pragma once
#include <stdbool.h>
#include <stdint.h>

#define USB_POOL_SIZE 8u // one slot per (daddr, instance); matches the 8 host channels we budget

void usb_input_init(void); // registers the USB InputSource (call once at startup)

// Forwarded from the TinyUSB host callbacks (tuh_hid_mount_cb / umount / report_received).
// on_mount returns true if the interface was CLAIMED into the pool — only then should the caller
// arm its report pipe. False means it was rejected (pool full, or the report descriptor declares
// no bindable inputs, as a composite controller's second HID interface often does), so leave its
// interrupt-IN channel unarmed rather than polling an interface nothing can ever bind to.
bool usb_input_on_mount(uint8_t daddr, uint8_t instance, const uint8_t *report_desc, uint16_t len);
void usb_input_on_umount(uint8_t daddr, uint8_t instance);
void usb_input_on_report(uint8_t daddr, uint8_t instance, const uint8_t *report, uint16_t len);

// Interrupt-pipe watchdog — call from the main loop. Deliberately minimal: it re-arms a pipe that
// is genuinely IDLE (the arm was dropped and nothing else will ever reschedule it) and touches
// nothing else. It does NOT recover the field "device goes silent while still enumerated" freeze —
// a frozen pipe reads BUSY, indistinguishable from a healthy NAK-ing one, and an earlier version
// that auto-aborted busy pipes fired constantly on untouched devices and decoded garbage. Read the
// scope comment in usb_input.c before making this cleverer.
void usb_input_task(uint32_t now_ms);

// --- manual diagnostics (the usb_status / usb_kick JSON commands in protocol.c) ------------------
typedef struct {
  bool     in_use;
  uint16_t vid, pid;
  uint8_t  daddr;       // USB device address — the key for matching this slot against the
                        // dwc2 host-channel dump in usb_status (HCCHAR carries the same address)
  bool     mounted;     // interface still enumerated (tuh_hid_mounted)
  bool     busy;        // transfer outstanding. Healthy devices are near-always busy: a device
                        // with nothing to report NAKs, and a NAK loop reads as busy.
  uint32_t reports;     // total reports received since claim
  uint32_t age_ms;      // ms since the last report (since claim if none yet)
  uint32_t idle_rearms; // times usb_input_task() re-armed a dropped pipe
} UsbSlotDiag;

// Snapshot one slot's pipe state. Returns false only for an out-of-range slot.
bool usb_input_diag(uint8_t slot, UsbSlotDiag *out);

// One-shot recovery probe: abort any outstanding transfer and re-arm every claimed pipe. Returns
// the bitmask of slots that were busy and got aborted. User-initiated diagnostic ONLY — never wire
// this to a timer (see the scope comment in usb_input.c).
uint32_t usb_input_kick(void);
