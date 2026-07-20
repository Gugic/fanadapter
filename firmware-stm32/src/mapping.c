// Config schema, persistence, and binding evaluators. C port of firmware/mapping.cpp. See mapping.h.
#include "mapping.h"

#include <string.h>

#include "config_store.h"
#include "input_source.h"
#include "outputs.h"
#include "pedals.h"
#include "stm32h7xx_hal.h" // HAL_GetTick

extern int console_printf(const char *fmt, ...);

// ---------------- Defaults ----------------
// X/Y voltage table carried over verbatim from the Teensy GEARS[] constants.
const GearOutputCalibration GEAR_DEFAULT_OUT[NUM_GEAR_OUTPUTS] = {
    {4095, 3430}, // R
    {2790, 3430}, // 1
    {2790, 779},  // 2
    {2163, 3430}, // 3
    {2163, 779},  // 4
    {1766, 3430}, // 5
    {1766, 779},  // 6
    {1310, 3430}, // 7
    {2163, 2048}, // N (neutral)
};

const uint16_t DEFAULT_PULSE_MS = 50;

// ---------------- State ----------------

static Config         g_cfg;
static OutputSnapshot g_outputs;

// Test overrides — test_gear forces a gear onto the DAC, test_axis an axis level, both for ~500 ms.
#define TEST_HOLD_MS 500u
static ChannelId g_test_gear_override = CH_GEAR_N;
static uint32_t  g_test_gear_until    = 0;
static uint16_t  g_test_axis_value[CH_COUNT];
static uint32_t  g_test_axis_until[CH_COUNT];

// Rising-edge shadow for latch mode — one bool per gear binding slot, so a held binding only
// switches the gear once per press.
static bool g_gear_prev_pressed[NUM_GEAR_BINDINGS];

// Direct-output overrides (PC/SimHub via serial). Command override: when active, the PC value drives
// the channel instead of the USB-device mapping. Sticky until mapping_release_outputs().
static bool      g_ovr_gear_active;
static ChannelId g_ovr_gear = CH_GEAR_N;
static bool      g_ovr_axis_active[CH_COUNT];
static uint16_t  g_ovr_axis_value[CH_COUNT];

// Rising-edge shadow for USB sequential bindings (so a held shift binding pulses only once).
static bool g_seq_up_prev, g_seq_down_prev;

// ---------------- CRC-32/ISO-HDLC (zlib/PNG) ----------------
// Polynomial 0xEDB88320 reflected, init 0xFFFFFFFF, final XOR 0xFFFFFFFF. Identical to the Teensy
// and to webconfig/src/lib/crc32.ts.
static uint32_t crc32(const uint8_t *data, size_t len) {
  uint32_t crc = 0xFFFFFFFFu;
  for (size_t i = 0; i < len; ++i) {
    crc ^= data[i];
    for (uint8_t j = 0; j < 8; ++j) {
      const uint32_t mask = (uint32_t) - (int32_t)(crc & 1u);
      crc                 = (crc >> 1) ^ (0xEDB88320u & mask);
    }
  }
  return crc ^ 0xFFFFFFFFu;
}

// ---------------- Channel name table ----------------

typedef struct {
  ChannelId   id;
  const char *name;
} channel_name_row;

static const channel_name_row CHANNEL_NAMES[] = {
    {CH_GEAR_R, "gear_R"},     {CH_GEAR_1, "gear_1"},         {CH_GEAR_2, "gear_2"},
    {CH_GEAR_3, "gear_3"},     {CH_GEAR_4, "gear_4"},         {CH_GEAR_5, "gear_5"},
    {CH_GEAR_6, "gear_6"},     {CH_GEAR_7, "gear_7"},         {CH_GEAR_N, "gear_N"},
    {CH_SHIFT_UP, "shift_up"}, {CH_SHIFT_DOWN, "shift_down"}, {CH_HANDBRAKE, "handbrake"},
    {CH_THROTTLE, "throttle"}, {CH_BRAKE, "brake"},           {CH_CLUTCH, "clutch"},
};
#define CHANNEL_NAMES_COUNT (sizeof(CHANNEL_NAMES) / sizeof(CHANNEL_NAMES[0]))

const char *mapping_channel_name(ChannelId id) {
  for (size_t i = 0; i < CHANNEL_NAMES_COUNT; ++i)
    if (CHANNEL_NAMES[i].id == id) return CHANNEL_NAMES[i].name;
  return "unknown";
}

ChannelId mapping_channel_by_name(const char *s) {
  if (!s) return CH_NONE;
  for (size_t i = 0; i < CHANNEL_NAMES_COUNT; ++i)
    if (strcmp(s, CHANNEL_NAMES[i].name) == 0) return CHANNEL_NAMES[i].id;
  return CH_NONE;
}

ChannelBindings *mapping_channel_bindings(ChannelId id) {
  switch (id) {
    case CH_GEAR_R: return &g_cfg.gear[0];
    case CH_GEAR_1: return &g_cfg.gear[1];
    case CH_GEAR_2: return &g_cfg.gear[2];
    case CH_GEAR_3: return &g_cfg.gear[3];
    case CH_GEAR_4: return &g_cfg.gear[4];
    case CH_GEAR_5: return &g_cfg.gear[5];
    case CH_GEAR_6: return &g_cfg.gear[6];
    case CH_GEAR_7: return &g_cfg.gear[7];
    case CH_GEAR_N: return &g_cfg.gear[GEAR_N_BINDING_INDEX];
    case CH_SHIFT_UP: return &g_cfg.shiftUp;
    case CH_SHIFT_DOWN: return &g_cfg.shiftDown;
    case CH_HANDBRAKE: return &g_cfg.handbrake;
    case CH_THROTTLE: return &g_cfg.throttle;
    case CH_BRAKE: return &g_cfg.brake;
    case CH_CLUTCH: return &g_cfg.clutch;
    default: return NULL;
  }
}

InputBinding *mapping_binding_slot(ChannelId id, uint8_t slot) {
  if (slot >= MAX_BINDINGS_PER_CHANNEL) return NULL;
  ChannelBindings *cb = mapping_channel_bindings(id);
  return cb ? &cb->bindings[slot] : NULL;
}

GearOutputCalibration *mapping_gearout_for(ChannelId id) {
  if (id >= CH_GEAR_R && id <= CH_GEAR_N) return &g_cfg.gearOut[id - CH_GEAR_R];
  return NULL;
}

// ---------------- Evaluators ----------------

// Linear remap (rawMin/rawMax) + invert + deadzones. All-zero rawMin/rawMax passes raw through so
// the UI's uncalibrated first-look still shows movement. Identical to the Teensy / scaleAxis.ts.
static uint16_t scale_axis(uint16_t raw, const InputBinding *b) {
  uint32_t scaled;
  if (b->rawMin == 0 && b->rawMax == 0) {
    scaled = raw;
  } else if (raw <= b->rawMin) {
    scaled = 0;
  } else if (raw >= b->rawMax) {
    scaled = 65535;
  } else {
    scaled = (uint32_t)(raw - b->rawMin) * 65535u / (uint32_t)(b->rawMax - b->rawMin);
  }
  if (b->invert) scaled = 65535u - scaled;
  if (scaled <= b->deadzoneLow) return 0;
  if (scaled >= b->deadzoneHigh) return 65535;
  return (uint16_t)scaled;
}

uint16_t eval_axis(const InputBinding *b) {
  if (b->type == INPUT_NONE) return 0;

  if (b->type == INPUT_BUTTON) {
    if (b->index >= 32) return 0;
    bool pressed = (input_fold_buttons(b->vid, b->pid) & (1u << b->index)) != 0;
    if (b->invert) pressed = !pressed;
    return pressed ? 65535 : 0;
  }

  if (b->type == INPUT_HAT) {
    if (b->index > 7) return 0;
    bool matched = input_fold_hat_is(b->vid, b->pid, b->index);
    if (b->invert) matched = !matched;
    return matched ? 65535 : 0;
  }

  if (b->type == INPUT_KEY) {
    if (!b->index) return 0;
    bool pressed = input_fold_key(b->vid, b->pid, b->index);
    if (b->invert) pressed = !pressed;
    return pressed ? 65535 : 0;
  }

  // Axis: MAX raw across sources, then scale.
  if (b->index >= INPUT_MAX_AXES) return 0;
  bool     found = false;
  uint16_t raw   = input_fold_axis(b->vid, b->pid, b->index, &found);
  if (!found) return 0;
  return scale_axis(raw, b);
}

bool eval_button(const InputBinding *b) {
  if (b->type == INPUT_NONE) return false;

  if (b->type == INPUT_BUTTON) {
    if (b->index >= 32) return false;
    bool pressed = (input_fold_buttons(b->vid, b->pid) & (1u << b->index)) != 0;
    if (b->invert) pressed = !pressed;
    return pressed;
  }

  if (b->type == INPUT_HAT) {
    if (b->index > 7) return false;
    bool matched = input_fold_hat_is(b->vid, b->pid, b->index);
    if (b->invert) matched = !matched;
    return matched;
  }

  if (b->type == INPUT_KEY) {
    if (!b->index) return false;
    bool pressed = input_fold_key(b->vid, b->pid, b->index);
    if (b->invert) pressed = !pressed;
    return pressed;
  }

  // Axis-as-button: threshold against the post-scale value.
  return eval_axis(b) >= b->threshold;
}

uint16_t eval_channel_axis(const ChannelBindings *cb) {
  uint16_t best = 0;
  for (uint8_t i = 0; i < MAX_BINDINGS_PER_CHANNEL; ++i) {
    if (cb->bindings[i].type == INPUT_NONE) continue;
    uint16_t v = eval_axis(&cb->bindings[i]);
    if (v > best) best = v;
  }
  return best;
}

bool eval_channel_button(const ChannelBindings *cb) {
  for (uint8_t i = 0; i < MAX_BINDINGS_PER_CHANNEL; ++i) {
    if (cb->bindings[i].type == INPUT_NONE) continue;
    if (eval_button(&cb->bindings[i])) return true;
  }
  return false;
}

// ---------------- Channel updaters ----------------

// H-pattern shifter. Computes the target gear (test-override > hold/latch logic) and hands it to the
// non-blocking transit FSM in outputs.c. Ported from the Teensy updateShifter().
static void update_shifter(void) {
  // Test override wins in both modes.
  if (g_test_gear_until != 0 && (int32_t)(HAL_GetTick() - g_test_gear_until) < 0) {
    outputs_request_gear(g_test_gear_override);
    g_outputs.gear = outputs_current_gear();
    return;
  }
  g_test_gear_until = 0; // expired (no-op if already zero)

  // PC/SimHub direct gear override (command override) — wins over the USB-device shifter logic.
  if (g_ovr_gear_active) {
    outputs_request_gear(g_ovr_gear);
    g_outputs.gear = outputs_current_gear();
    return;
  }

  if (g_cfg.gearMode == GEAR_MODE_LATCH) {
    // Latch: a rising edge on any gear binding switches the current gear; it stays until the next
    // edge moves it (including gear_N as the explicit shift-to-neutral). First edge per tick wins.
    ChannelId new_gear = outputs_current_gear();
    bool      changed  = false;
    for (uint8_t i = 0; i < NUM_GEAR_BINDINGS; ++i) {
      bool pressed = eval_channel_button(&g_cfg.gear[i]);
      if (pressed && !g_gear_prev_pressed[i] && !changed) {
        new_gear = (ChannelId)(CH_GEAR_R + i);
        changed  = true;
      }
      g_gear_prev_pressed[i] = pressed;
    }
    if (changed) outputs_request_gear(new_gear);
    g_outputs.gear = outputs_current_gear();
    return;
  }

  // Hold mode (default): gear active only while its binding is held. 0 hits -> neutral, 1 -> that
  // gear, 2+ -> neutral (defensive). An explicit gear_N binding forces neutral (panic key).
  ChannelId target = CH_GEAR_N;
  uint8_t   hits   = 0;
  for (uint8_t i = 0; i < GEAR_N_BINDING_INDEX; ++i) {
    if (eval_channel_button(&g_cfg.gear[i])) {
      ++hits;
      if (hits == 1) target = (ChannelId)(CH_GEAR_R + i);
    }
  }
  if (hits >= 2) target = CH_GEAR_N;
  if (eval_channel_button(&g_cfg.gear[GEAR_N_BINDING_INDEX])) target = CH_GEAR_N;

  outputs_request_gear(target);
  memset(g_gear_prev_pressed, 0, sizeof(g_gear_prev_pressed)); // keep latch shadow clean in hold mode
  g_outputs.gear = outputs_current_gear();
}

// Axis output value for a channel. Priority: test-axis override (500 ms) > PC command override
// (sticky) > USB-device mapping (MAX across bindings). Used for handbrake/throttle/brake/clutch.
static uint16_t axis_out(ChannelId ch) {
  if (ch < CH_COUNT && g_test_axis_until[ch] != 0) {
    if ((int32_t)(HAL_GetTick() - g_test_axis_until[ch]) < 0) return g_test_axis_value[ch];
    g_test_axis_until[ch] = 0; // expired
  }
  if (ch < CH_COUNT && g_ovr_axis_active[ch]) return g_ovr_axis_value[ch];
  ChannelBindings *cb = mapping_channel_bindings(ch);
  return cb ? eval_channel_axis(cb) : 0;
}

// Pedal + handbrake levels into the output snapshot AND the physical sinks: the handbrake PWM
// fallback (M5) and the CSL Elite pedal stream (M6). Handbrake is dual-written to both (modern
// Fanatec firmware reads it from the pedal stream); throttle/brake/clutch go to the stream only. The
// snapshot still drives the `outputs` event so the UI reflects USB-mapped + PC-commanded levels.
static void update_axes(void) {
  g_outputs.handbrake = axis_out(CH_HANDBRAKE);
  g_outputs.throttle  = axis_out(CH_THROTTLE);
  g_outputs.brake     = axis_out(CH_BRAKE);
  g_outputs.clutch    = axis_out(CH_CLUTCH);

  outputs_set_handbrake_pwm(g_outputs.handbrake);
  pedals_set_throttle(g_outputs.throttle);
  pedals_set_brake(g_outputs.brake);
  pedals_set_clutch(g_outputs.clutch);
  pedals_set_handbrake(g_outputs.handbrake);
}

// Sequential shifts from USB bindings: rising edge on shift_up/shift_down fires a pulse. (PC-driven
// shifts come in via mapping_pulse_shift / the pulse_shift command.) Snapshot reflects pulse state.
static void update_sequential(void) {
  bool up = eval_channel_button(&g_cfg.shiftUp);
  bool dn = eval_channel_button(&g_cfg.shiftDown);
  if (up && !g_seq_up_prev) outputs_pulse_shift(true, g_cfg.pulseMs);
  if (dn && !g_seq_down_prev) outputs_pulse_shift(false, g_cfg.pulseMs);
  g_seq_up_prev   = up;
  g_seq_down_prev = dn;
  g_outputs.shiftUp   = outputs_shift_active(true);
  g_outputs.shiftDown = outputs_shift_active(false);
}

// ---------------- Per-tick application ----------------
// Gears drive the DAC (M3); sequential drives the open-drain pins. Handbrake PWM (M5) and the pedal
// stream (M6) consume the axis snapshot values once those sinks land.
void mapping_tick(void) {
  update_shifter();
  update_axes();
  update_sequential();
  outputs_tick(); // advance the neutral-transit FSM + sequential pulse timers
}

const OutputSnapshot *mapping_outputs(void) { return &g_outputs; }

// ---------------- Test overrides ----------------

void mapping_test_gear(ChannelId ch) {
  if (ch < CH_GEAR_R || ch > CH_GEAR_N) return;
  g_test_gear_override = ch;
  g_test_gear_until    = HAL_GetTick() + TEST_HOLD_MS;
}

void mapping_test_axis(ChannelId ch, uint16_t value) {
  if (ch != CH_HANDBRAKE && ch != CH_THROTTLE && ch != CH_BRAKE && ch != CH_CLUTCH) return;
  g_test_axis_value[ch] = value;
  g_test_axis_until[ch] = HAL_GetTick() + TEST_HOLD_MS;
}

void mapping_test_pulse(bool up) { outputs_pulse_shift(up, mapping_config()->pulseMs); }

// ---------------- Direct output control ----------------

void mapping_set_gear_override(ChannelId gear) {
  if (gear < CH_GEAR_R || gear > CH_GEAR_N) return;
  g_ovr_gear        = gear;
  g_ovr_gear_active = true;
}

void mapping_set_axis_override(ChannelId ch, uint16_t value) {
  if (ch != CH_HANDBRAKE && ch != CH_THROTTLE && ch != CH_BRAKE && ch != CH_CLUTCH) return;
  g_ovr_axis_value[ch]  = value;
  g_ovr_axis_active[ch] = true;
}

void mapping_pulse_shift(bool up) { outputs_pulse_shift(up, g_cfg.pulseMs); }

void mapping_release_outputs(void) {
  g_ovr_gear_active = false;
  for (uint8_t i = 0; i < CH_COUNT; ++i) g_ovr_axis_active[i] = false;
}

// ---------------- Lifecycle ----------------

void mapping_recompute_crc(void) {
  g_cfg.crc = crc32((const uint8_t *)&g_cfg, sizeof(Config) - sizeof(uint32_t));
}

void mapping_reset(void) {
  memset(&g_cfg, 0, sizeof(Config));
  g_cfg.magic    = CONFIG_MAGIC;
  g_cfg.version  = CONFIG_VERSION;
  g_cfg.pulseMs  = DEFAULT_PULSE_MS;
  g_cfg.gearMode = GEAR_MODE_HOLD;
  for (uint8_t i = 0; i < NUM_GEAR_OUTPUTS; ++i) g_cfg.gearOut[i] = GEAR_DEFAULT_OUT[i];
  mapping_recompute_crc();

  // Clear runtime state so a reset_config doesn't leave a ghost holding. The DAC isn't touched here
  // (it may be pre-init at boot); with bindings wiped, the next mapping_tick drives neutral.
  g_test_gear_until    = 0;
  g_test_gear_override = CH_GEAR_N;
  memset(g_test_axis_until, 0, sizeof(g_test_axis_until));
  memset(g_test_axis_value, 0, sizeof(g_test_axis_value));
  memset(g_gear_prev_pressed, 0, sizeof(g_gear_prev_pressed));
  g_ovr_gear_active = false;
  g_ovr_gear        = CH_GEAR_N;
  memset(g_ovr_axis_active, 0, sizeof(g_ovr_axis_active));
  g_seq_up_prev = g_seq_down_prev = false;

  memset(&g_outputs, 0, sizeof(g_outputs));
  g_outputs.gear = CH_GEAR_N;
}

void mapping_init(void) {
  Config tmp;
  memset(&tmp, 0, sizeof(tmp));
  config_store_load(&tmp, sizeof(tmp));

  const bool magic_ok   = (tmp.magic == CONFIG_MAGIC);
  const bool version_ok = (tmp.version == CONFIG_VERSION);
  uint32_t   crc_calc   = 0;
  if (magic_ok && version_ok)
    crc_calc = crc32((const uint8_t *)&tmp, sizeof(Config) - sizeof(uint32_t));

  if (magic_ok && version_ok && crc_calc == tmp.crc) {
    g_cfg = tmp;
    console_printf("[config] loaded from flash\r\n");
    memset(&g_outputs, 0, sizeof(g_outputs));
    g_outputs.gear = CH_GEAR_N;
    return;
  }

  if (!magic_ok)
    console_printf("[config] flash magic missing — first boot or wiped; using defaults\r\n");
  else if (!version_ok)
    console_printf("[config] flash version %u != %u — using defaults\r\n", tmp.version,
                   CONFIG_VERSION);
  else
    console_printf("[config] flash CRC mismatch — using defaults\r\n");
  mapping_reset();
}

bool mapping_save(void) {
  mapping_recompute_crc();
  bool ok = config_store_save(&g_cfg, sizeof(Config));
  console_printf(ok ? "[config] saved to flash\r\n" : "[config] flash save FAILED\r\n");
  return ok;
}

const Config *mapping_config(void) { return &g_cfg; }
Config       *mapping_config_mutable(void) { return &g_cfg; }
