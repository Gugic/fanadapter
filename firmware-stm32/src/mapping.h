// Input -> output mapping: the Config schema, persistence, and the binding evaluators.
//
// This is the C port of the Teensy firmware/mapping.{h,cpp}. The Config struct, InputBinding,
// ChannelBindings and GearOutputCalibration are byte-IDENTICAL to the Teensy so the JSON wire shape
// and the stored blob round-trip across both firmwares — the _Static_asserts lock the layout, and
// CONFIG_VERSION must be bumped on any field change (older stored blobs are then rejected and the
// firmware boots defaults). See AGENTS.md "cross-file invariants".
//
// The one intentional difference from the Teensy: the evaluators read inputs through the modular
// InputSource folds (input_fold_*) instead of the USB device pool directly, so a binding aggregates
// across USB ∪ serial-injected devices (OR for buttons/hat/key, MAX for axes) — the math is the
// same, the source set is wider.
#pragma once
#include <stdbool.h>
#include <stdint.h>

// ---------------- Config schema ----------------

#define CONFIG_MAGIC             0x46414E41u // 'FANA'
#define CONFIG_VERSION           3u
#define NUM_GEAR_BINDINGS        9 // gear_R + gear_1..gear_7 + gear_N
#define NUM_GEAR_OUTPUTS         9 // includes gear_N
#define GEAR_N_BINDING_INDEX     8 // gear[8] = gear_N's bindings
#define MAX_BINDINGS_PER_CHANNEL 4

// Neutral transit time when shifting between two non-neutral gears (used by the M3 output FSM).
#define NEUTRAL_TRANSIT_MS 50u

// InputBinding.type values. Stored in the uint8_t `type` field, so the enum width is irrelevant to
// the layout. INPUT_HAT matches one of the 8 HID directions strictly; INPUT_KEY carries a HID
// Keyboard/Keypad scancode in `index`.
enum {
  INPUT_NONE   = 0,
  INPUT_BUTTON = 1,
  INPUT_AXIS   = 2,
  INPUT_HAT    = 3,
  INPUT_KEY    = 4,
};

// A single input binding. threshold/rawMin/rawMax/deadzone* are compared against POST-scale values
// (0..65535). Explicit padding keeps the size locked at 18 bytes.
typedef struct {
  uint16_t vid; // 0 = unmapped (paired with type = INPUT_NONE)
  uint16_t pid;
  uint8_t  type;  // InputType
  uint8_t  index; // button bit 0-31, axis index 0-7, hat dir 0-7, or HID scancode
  uint16_t threshold;    // 0..65535 (post-scale): axis-as-button cutoff
  uint16_t rawMin;       // axis raw value mapped to output 0
  uint16_t rawMax;       // axis raw value mapped to output 65535
  uint16_t deadzoneLow;  // 0..65535: output clamped to 0 below this
  uint16_t deadzoneHigh; // 0..65535: output clamped to 65535 above this
  uint8_t  invert;       // 0/1
  uint8_t  _pad;
} InputBinding;
_Static_assert(sizeof(InputBinding) == 18, "InputBinding layout locked — bump CONFIG_VERSION");

typedef struct {
  InputBinding bindings[MAX_BINDINGS_PER_CHANNEL];
} ChannelBindings;
_Static_assert(sizeof(ChannelBindings) == MAX_BINDINGS_PER_CHANNEL * 18,
               "ChannelBindings layout locked — bump CONFIG_VERSION");

// Per-gear DAC output codes (the X/Y voltages the wheelbase reads). 0..4095 (12-bit).
typedef struct {
  uint16_t x;
  uint16_t y;
} GearOutputCalibration;
_Static_assert(sizeof(GearOutputCalibration) == 4, "GearOutputCalibration layout locked");

// H-pattern shifter mode (see the M3 updateShifter port).
enum {
  GEAR_MODE_HOLD  = 0, // gear active only while its binding is held
  GEAR_MODE_LATCH = 1, // rising edge switches gear; stays until the next edge
};

// Persisted config. magic + version + CRC32 guard against corruption. 1132 bytes total.
typedef struct {
  uint32_t              magic;
  uint16_t              version;
  uint16_t              _pad;
  ChannelBindings       gear[NUM_GEAR_BINDINGS]; // gear[0..7] = R/1..7, gear[8] = N
  GearOutputCalibration gearOut[NUM_GEAR_OUTPUTS];
  ChannelBindings       shiftUp;
  ChannelBindings       shiftDown;
  uint16_t              pulseMs;
  uint8_t               gearMode; // GearMode
  uint8_t               _pad2;
  ChannelBindings       handbrake;
  ChannelBindings       throttle;
  ChannelBindings       brake;
  ChannelBindings       clutch;
  uint32_t              crc;
} Config;
_Static_assert(sizeof(Config) % 4 == 0, "Config must be 4-byte aligned");
_Static_assert(sizeof(Config) == 1132, "Config layout locked — bump CONFIG_VERSION");

// ---------------- Channel naming ----------------
// "gear_R","gear_1".."gear_7","gear_N","shift_up","shift_down","handbrake","throttle","brake","clutch".
typedef enum {
  CH_NONE = 0,
  CH_GEAR_R,
  CH_GEAR_1,
  CH_GEAR_2,
  CH_GEAR_3,
  CH_GEAR_4,
  CH_GEAR_5,
  CH_GEAR_6,
  CH_GEAR_7,
  CH_GEAR_N,
  CH_SHIFT_UP,
  CH_SHIFT_DOWN,
  CH_HANDBRAKE,
  CH_THROTTLE,
  CH_BRAKE,
  CH_CLUTCH,
  CH_COUNT,
} ChannelId;

// Snapshot of currently-applied outputs, for the `outputs` event (driven from M3 on).
typedef struct {
  ChannelId gear; // CH_GEAR_R..CH_GEAR_N
  bool      shiftUp;
  bool      shiftDown;
  uint16_t  throttle;
  uint16_t  brake;
  uint16_t  clutch;
  uint16_t  handbrake;
} OutputSnapshot;

// ---------------- Lifecycle ----------------

// Load Config from flash; on magic/version/CRC mismatch, reset to defaults (everything unmapped,
// gearOut[] seeded). Does NOT write flash — the user must save_config explicitly.
void mapping_init(void);

// Recompute CRC and write Config to flash. Returns true if the bytes verify after write-back.
bool mapping_save(void);

// Wipe bindings to INPUT_NONE, restore gearOut[] defaults + pulseMs. Does NOT touch flash.
void mapping_reset(void);

// Recompute the stored CRC. Call after any direct edit via mapping_config_mutable().
void mapping_recompute_crc(void);

const Config *mapping_config(void);
Config       *mapping_config_mutable(void); // call mapping_recompute_crc() after editing

// ---------------- Channel helpers ----------------

const char            *mapping_channel_name(ChannelId id);     // "gear_R", ...
ChannelId              mapping_channel_by_name(const char *s); // CH_NONE if unknown
ChannelBindings       *mapping_channel_bindings(ChannelId id); // NULL for CH_NONE / out-of-range
InputBinding          *mapping_binding_slot(ChannelId id, uint8_t slot);
GearOutputCalibration *mapping_gearout_for(ChannelId id); // NULL if not a gear_* channel

// ---------------- Evaluators ----------------
// Fold inputs across all registered InputSources (OR buttons/hat/key, MAX axes), then apply
// scale/invert/deadzone/threshold. Identical math to webconfig/src/lib/scaleAxis.ts.

uint16_t eval_axis(const InputBinding *b);   // 0..65535
bool     eval_button(const InputBinding *b);
uint16_t eval_channel_axis(const ChannelBindings *cb);   // MAX across the 4 slots
bool     eval_channel_button(const ChannelBindings *cb); // OR across the 4 slots

// ---------------- Per-tick application ----------------

// Read inputs, evaluate bindings, drive outputs. Wired into the main loop. (M3 drives the H-pattern
// gears; sequential/handbrake/pedals join in M5/M6.)
void                  mapping_tick(void);
const OutputSnapshot *mapping_outputs(void);

// ---------------- Test overrides (500 ms holds) ----------------

void mapping_test_gear(ChannelId ch);                  // force gear_R..gear_N onto the DAC for ~500 ms
void mapping_test_axis(ChannelId ch, uint16_t value);  // force a handbrake/throttle/brake/clutch level
void mapping_test_pulse(bool up);                      // fire one sequential shift pulse (test)

// ---------------- Direct output control (PC / SimHub over serial) ----------------
// The PC computes its own input->action mapping and commands the adapter's outputs directly. These
// overrides take precedence over the USB-device mapping for the channels they touch ("command
// override") and are STICKY until mapping_release_outputs(). A pure PC-driven setup (no USB bindings)
// just sees the commanded values; a hybrid setup has the PC win on the channels it drives.
void mapping_set_gear_override(ChannelId gear);               // CH_GEAR_R..CH_GEAR_N
void mapping_set_axis_override(ChannelId ch, uint16_t value); // handbrake/throttle/brake/clutch, 0..65535
void mapping_pulse_shift(bool up);                            // fire one sequential shift pulse
void mapping_release_outputs(void);                          // hand all channels back to USB mapping

// ---------------- Defaults ----------------

extern const GearOutputCalibration GEAR_DEFAULT_OUT[NUM_GEAR_OUTPUTS];
extern const uint16_t              DEFAULT_PULSE_MS;
