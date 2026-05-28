// Dynamic input → output mapping, calibration, and EEPROM persistence.
//
// Layout is locked with static_asserts so the EEPROM schema can't drift
// silently. Bump CONFIG_VERSION on any field change.

#pragma once
#include <Arduino.h>

// ---------------- Pin & PWM configuration ----------------
// Single source of truth — both the main sketch (for pinMode/PWM setup)
// and the mapping engine (for analogWrite) include this header.

constexpr uint8_t PIN_X = 4;         // H-pattern X DAC (PWM via RC)
constexpr uint8_t PIN_Y = 5;         // H-pattern Y DAC
constexpr uint8_t PIN_SEQ_UP = 6;    // sequential up pulse (open-drain)
constexpr uint8_t PIN_SEQ_DOWN = 7;  // sequential down pulse
constexpr uint8_t PIN_HANDBRAKE = 8; // handbrake DAC

constexpr uint8_t PWM_BITS = 12;
constexpr uint32_t PWM_FREQ_HZ = 36000;

// Neutral transit time when shifting between two non-neutral gears.
// Same value as the pre-refactor code; the wheelbase needs to see a
// gear-release before the new gear is latched.
constexpr uint32_t NEUTRAL_TRANSIT_MS = 50;

// ---------------- Config schema ----------------

#define CONFIG_MAGIC 0x46414E41u // 'FANA'
#define CONFIG_VERSION 3u
#define NUM_GEAR_BINDINGS 9    // gear_R + gear_1..gear_7 + gear_N
#define NUM_GEAR_OUTPUTS 9     // includes gear_N
#define GEAR_N_BINDING_INDEX 8 // gear[8] = gear_N's bindings
// How many independent physical inputs may target a single output channel.
// Buttons OR together, axes MAX together — same as the firmware's
// same-VID/PID aggregation, extended across arbitrary VID/PID/index combos.
#define MAX_BINDINGS_PER_CHANNEL 4

enum InputType : uint8_t {
  INPUT_NONE = 0,
  INPUT_BUTTON = 1,
  INPUT_AXIS = 2,
  // Hat Switch / D-pad. `index` selects which of the 8 HID directions
  // (0=N, 1=NE, 2=E, 3=SE, 4=S, 5=SW, 6=W, 7=NW) this binding strictly
  // matches. Diagonals do NOT match adjacent cardinals — bind multiple
  // slots on the same channel for lenient matching (the channel-level
  // OR aggregation handles it). Adding a new enum value does not change
  // the byte layout of InputBinding, so no CONFIG_VERSION bump is needed.
  INPUT_HAT = 3,
  // Keyboard key. `index` is the HID Keyboard/Keypad usage code (0x04 = A,
  // 0x05 = B, …, 0x1E..0x27 = 1..0, 0x28 = Enter, 0xE0..0xE7 = modifiers).
  // Evaluated against the device's currently-pressed-keys set.
  INPUT_KEY = 4,
};

// A single input binding. `threshold`, `rawMin`, `rawMax`, `deadzoneLow`,
// `deadzoneHigh` are compared against POST-SCALE values (0..65535), so the
// 50% threshold default (32768) works regardless of the source device's
// native bit depth. Explicit padding fields keep the struct size locked.
struct InputBinding {
  uint16_t vid; // 0 = unmapped (combined with type=INPUT_NONE)
  uint16_t pid;
  uint8_t type;          // InputType
  uint8_t index;         // button bit 0-31, or axis index 0-7
  uint16_t threshold;    // 0..65535 (post-scale): axis-as-button cutoff
  uint16_t rawMin;       // axis raw value mapped to output 0
  uint16_t rawMax;       // axis raw value mapped to output 65535
  uint16_t deadzoneLow;  // 0..65535: output clamped to 0 below this
  uint16_t deadzoneHigh; // 0..65535: output clamped to 65535 above this
  uint8_t invert;        // 0/1
  uint8_t _pad;
};
static_assert(sizeof(InputBinding) == 18,
              "InputBinding layout locked — bump CONFIG_VERSION on change");

// All bindings driving a single output channel. Empty slots have
// type=INPUT_NONE and are skipped during evaluation.
struct ChannelBindings {
  InputBinding bindings[MAX_BINDINGS_PER_CHANNEL];
};
static_assert(sizeof(ChannelBindings) == MAX_BINDINGS_PER_CHANNEL * 18,
              "ChannelBindings layout locked — bump CONFIG_VERSION on change");

// Per-gear DAC output values (the X/Y PWM voltages the wheelbase reads).
struct GearOutputCalibration {
  uint16_t x;
  uint16_t y;
};
static_assert(sizeof(GearOutputCalibration) == 4, "GearOutputCalibration layout locked");

// H-pattern shifter mode. Determines how `updateShifter()` reacts to
// gear bindings — see updateShifter() for the actual semantics.
enum GearMode : uint8_t {
  GEAR_MODE_HOLD = 0,  // default; gear active only while binding held
  GEAR_MODE_LATCH = 1, // rising-edge switches gear, stays until next edge
};

// Persisted config. Magic + version + CRC32 guard against corruption.
// Total = 8 (header) + 648 (gear[9] @ 72) + 36 (gearOut[9]) + 144 (shift ×2)
//       + 4 (pulseMs+gearMode+pad) + 288 (4 axis channels @ 72) + 4 (crc)
//       = 1132 bytes.
struct Config {
  uint32_t magic;
  uint16_t version;
  uint16_t _pad;
  ChannelBindings gear[NUM_GEAR_BINDINGS]; // gear[0..7] = R/1..7, gear[8] = N
  GearOutputCalibration gearOut[NUM_GEAR_OUTPUTS];
  ChannelBindings shiftUp;
  ChannelBindings shiftDown;
  uint16_t pulseMs;
  uint8_t gearMode; // GearMode
  uint8_t _pad2;
  ChannelBindings handbrake;
  ChannelBindings throttle;
  ChannelBindings brake;
  ChannelBindings clutch;
  uint32_t crc;
};
static_assert(sizeof(Config) % 4 == 0, "Config must be 4-byte aligned");
static_assert(sizeof(Config) == 1132, "Config layout locked — bump CONFIG_VERSION on change");

// ---------------- Channel naming ----------------
// Uniform across set_binding, set_gear_dac, test_axis, test_gear, etc.
// String form: "gear_R", "gear_1".."gear_7", "gear_N",
//              "shift_up", "shift_down",
//              "handbrake", "throttle", "brake", "clutch".
enum ChannelId : uint8_t {
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
};

// Snapshot of currently-applied outputs, for live_outputs events.
struct OutputSnapshot {
  ChannelId gear; // CH_GEAR_R..CH_GEAR_N
  bool shiftUp;   // pulse currently active
  bool shiftDown;
  uint16_t throttle;
  uint16_t brake;
  uint16_t clutch;
  uint16_t handbrake;
};
static_assert(sizeof(OutputSnapshot) == 12, "OutputSnapshot layout size mismatch");

// ---------------- Lifecycle ----------------

// Load Config from EEPROM. On magic/version/CRC mismatch, zero all bindings
// (everything unmapped) and seed gearOut[] from GEAR_DEFAULT_OUT. EEPROM is
// NOT written by this call — user must explicitly save_config.
void mappingInit();

// Write current Config to EEPROM. Returns true if the bytes verify after
// write-back.
bool mappingSave();

// Wipe bindings to INPUT_NONE, restore gearOut[] defaults and pulseMs.
// Does NOT touch EEPROM — call mappingSave() to persist.
void mappingReset();

// Recompute CRC and mark Config dirty (so a subsequent save_config picks it
// up). Call after any direct modification via mappingConfigMutable().
void mappingRecomputeCrc();

// ---------------- Config access ----------------

const Config& mappingConfig();
Config& mappingConfigMutable(); // call mappingRecomputeCrc() after edit

// ---------------- Channel helpers ----------------

const char* mappingChannelName(ChannelId id);     // "gear_R", ...
ChannelId mappingChannelByName(const char* name); // CH_NONE if unknown
ChannelBindings*
mappingChannelBindings(ChannelId id); // nullptr for CH_GEAR_N / CH_NONE / out-of-range
// Convenience: bindings(channel).bindings[slot], with bounds-check.
InputBinding* mappingBindingSlot(ChannelId id, uint8_t slot);
GearOutputCalibration* mappingGearOutFor(ChannelId id); // nullptr if not gear_*

// ---------------- Per-tick application ----------------

// Read device pool, evaluate bindings, drive pins + pedal stream.
// Called every loop iteration.
void mappingTick();

// Latest applied output state — used by live_outputs event emitter.
const OutputSnapshot& mappingOutputs();

// ---------------- Evaluators ----------------
// Exposed for unit-test / diagnostic use; main code paths go through
// mappingTick().

uint16_t evalAxis(const InputBinding& b); // 0..65535
bool evalButton(const InputBinding& b);

// Per-channel aggregation across all populated bindings: OR for buttons,
// MAX for axis values.
uint16_t evalChannelAxis(const ChannelBindings& cb);
bool evalChannelButton(const ChannelBindings& cb);

// ---------------- Test overrides (500 ms holds) ----------------

void mappingTestAxis(ChannelId ch, uint16_t value); // handbrake/throttle/brake/clutch
void mappingTestPulse(bool up);                     // shift_up / shift_down
void mappingTestGear(ChannelId ch);                 // gear_R..gear_N

// ---------------- Defaults ----------------

extern const GearOutputCalibration GEAR_DEFAULT_OUT[NUM_GEAR_OUTPUTS];
extern const uint16_t DEFAULT_PULSE_MS;
