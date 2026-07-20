// Modular input abstraction — the cornerstone of the port.
//
// The mapping evaluator never talks to USB (or anything) directly. It asks a set of registered
// InputSources "what is button N / axis N / hat / key for device (vid,pid)?" and folds the answers
// (OR for buttons/hat/key, MAX for axes). Today there are two sources — USB HID devices (input_usb)
// and serial-injected virtual devices (input_serial) — and they aggregate identically, so PC-over-
// serial input drives the outputs exactly like a real wheel, and the two combine. New transports
// just register another source; the evaluator never changes.
#pragma once
#include <stdbool.h>
#include <stdint.h>

#define INPUT_MAX_AXES     8
#define INPUT_MAX_KEYS     6
#define INPUT_HAT_RELEASED 0xFFu
#define DEVICE_STR_LEN     32u

// One logical device as seen by list_devices / device_attached. `slot` is a global id so the UI can
// address devices across sources (USB uses 0..7, serial uses 8.., assigned by each source).
typedef struct {
  uint8_t  slot;
  bool     connected;
  uint16_t vid;
  uint16_t pid;
  char     manufacturer[DEVICE_STR_LEN];
  char     product[DEVICE_STR_LEN];
  uint8_t  axis_count;
  uint8_t  button_count;
  bool     has_hat;
  bool     has_keyboard;
} InputDeviceInfo;

// Live per-device state, for the `live` event (reported per physical/virtual device, not aggregated).
typedef struct {
  uint8_t  slot; // global slot id (matches InputDeviceInfo.slot)
  bool     connected;
  uint32_t buttons;
  uint16_t axes[INPUT_MAX_AXES];
  uint8_t  axis_count;
  uint8_t  hat; // 0..7 or INPUT_HAT_RELEASED
  bool     has_hat;
  uint8_t  keys[INPUT_MAX_KEYS];
  bool     has_keyboard;
  uint32_t change_seq; // per-device; the live emitter diffs on this
} InputLive;

// A source aggregates internally across all its devices matching (vid,pid):
//   buttons -> OR,  axis -> MAX,  hat_is/key_pressed -> OR.
typedef struct InputSource {
  const char *name; // "usb", "serial"
  bool     (*has_device)(uint16_t vid, uint16_t pid);
  uint32_t (*buttons)(uint16_t vid, uint16_t pid);
  uint16_t (*axis)(uint16_t vid, uint16_t pid, uint8_t index, bool *found);
  bool     (*hat_is)(uint16_t vid, uint16_t pid, uint8_t dir);
  bool     (*key_pressed)(uint16_t vid, uint16_t pid, uint8_t scancode);
  uint8_t  (*device_count)(void);                           // total slots this source exposes
  bool     (*device_info)(uint8_t i, InputDeviceInfo *out); // static info, i in 0..device_count()-1
  bool     (*device_live)(uint8_t i, InputLive *out);       // live state for the `live` event
  uint32_t (*change_seq)(void);                             // bumps whenever any device changes
} InputSource;

#define INPUT_SOURCE_MAX 4
void               input_source_register(const InputSource *src);
uint8_t            input_source_count(void);
const InputSource *input_source_get(uint8_t i);

// Cross-source folds used by the mapping evaluator (and live diffing).
uint32_t input_fold_buttons(uint16_t vid, uint16_t pid);                 // OR across sources
uint16_t input_fold_axis(uint16_t vid, uint16_t pid, uint8_t index, bool *found); // MAX across sources
bool     input_fold_hat_is(uint16_t vid, uint16_t pid, uint8_t dir);     // OR
bool     input_fold_key(uint16_t vid, uint16_t pid, uint8_t scancode);   // OR
uint32_t input_total_change_seq(void);                                   // sum of source change_seqs
