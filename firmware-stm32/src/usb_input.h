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

// Interrupt-pipe watchdog — call from the main loop. The report pipe is kept alive solely by the
// re-arm in tuh_hid_report_received_cb, and tuh_hid_receive_report() can fail transiently (endpoint
// claim or channel allocation); a single dropped re-arm used to silence the device PERMANENTLY
// while it stayed listed as connected. This walks the claimed slots and re-arms any whose IN pipe
// is idle. Single-threaded with tuh_task(), so "claimed slot, idle pipe" is never a legitimate
// state — no false positives.
void usb_input_task(uint32_t now_ms);
