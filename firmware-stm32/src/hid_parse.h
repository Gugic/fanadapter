// Generic HID report-descriptor parser + report decoder.
//
// TinyUSB hands us the raw report descriptor at mount and raw reports at runtime, with NO field
// offsets (its built-in parser only yields top-level usages). So we walk the descriptor ourselves
// into a flat field map, then decode each incoming report by that map — the analogue of what the
// Teensy USBHIDParser did for free. Validated against the four captured wheel descriptors
// (tools/hid_descriptor_capture.txt): Logitech C278 (3 buttons + Z, report id 6), C26B (8 buttons,
// id 3), Simnet CAFE:A301 pedals (X/Y/Z 12-bit, id 1) — all report-ID'd, standard pages.
#pragma once
#include <stdbool.h>
#include <stdint.h>
#include "input_source.h" // INPUT_MAX_AXES / INPUT_MAX_KEYS / INPUT_HAT_RELEASED

#define HID_MAX_FIELDS 24u // per interface; we only emit fields for mappable usage pages

// One decoded element of an input report. Variable items emit one field per element (count==1);
// array items (keyboard key arrays) emit a single field spanning `count` slots.
typedef struct {
  uint8_t  report_id;   // 0 if the device uses no report IDs
  uint16_t usage_page;  // 0x01 Desktop, 0x07 Keyboard, 0x09 Button
  uint16_t usage;       // variable: this element's usage; array: usage minimum
  uint16_t usage_max;   // array only
  uint16_t bit_offset;  // within the report payload (after the report-ID byte)
  uint8_t  bit_size;
  uint8_t  count;       // array: number of slots; variable: 1
  int32_t  logical_min;
  int32_t  logical_max;
  bool     is_array;    // keyboard/keypad key array (value = scancode) vs variable (value = magnitude)
} hid_field_t;

typedef struct {
  hid_field_t fields[HID_MAX_FIELDS];
  uint8_t     field_count;
  bool        uses_report_id;
  bool        overflow; // descriptor produced more mappable fields than HID_MAX_FIELDS
} hid_layout_t;

// Live decoded state — the read contract the mapping layer depends on.
typedef struct {
  uint16_t axes[INPUT_MAX_AXES];
  uint8_t  axis_count;
  uint32_t buttons;
  uint8_t  button_count;
  uint8_t  hat;
  bool     has_hat;
  uint8_t  keys[INPUT_MAX_KEYS];
  bool     has_keyboard;
} hid_state_t;

// Walk a report descriptor into `out`. Returns false on a malformed/empty descriptor.
bool hid_parse_descriptor(const uint8_t *desc, uint16_t len, hid_layout_t *out);

// Summarize a layout's capabilities for list_devices (max axis index, max button usage, hat/kbd).
void hid_layout_summary(const hid_layout_t *l, uint8_t *axis_count, uint8_t *button_count,
                        bool *has_hat, bool *has_keyboard);

// Decode one input report into `st` using `layout`. Returns true if any value changed.
bool hid_decode_report(const hid_layout_t *layout, const uint8_t *report, uint16_t len,
                       hid_state_t *st);

// Fixed 8-byte boot-keyboard report [mods, reserved, k0..k5] (used when the interface is in BOOT
// protocol, where the report-descriptor layout doesn't apply). Returns true if changed.
bool hid_decode_boot_keyboard(const uint8_t *report, uint16_t len, hid_state_t *st);
