#include "mapping.h"
#include "device_pool.h"
#include "pedals.h"
#include <EEPROM.h>
#include <string.h>

// ---------------- Defaults ----------------
// X/Y voltage table from the pre-refactor GEARS[] constants.
const GearOutputCalibration GEAR_DEFAULT_OUT[NUM_GEAR_OUTPUTS] = {
  { 4095, 3430 },  // R: X1, Y1
  { 2790, 3430 },  // 1: X2, Y1
  { 2790,  779 },  // 2: X2, Y3
  { 2163, 3430 },  // 3: X3, Y1
  { 2163,  779 },  // 4: X3, Y3
  { 1766, 3430 },  // 5: X4, Y1
  { 1766,  779 },  // 6: X4, Y3
  { 1310, 3430 },  // 7: X5, Y1
  { 2163, 2048 },  // N: X3, Y2 (neutral)
};

const uint16_t DEFAULT_PULSE_MS = 50;

// ---------------- State ----------------

static Config         g_cfg;
static OutputSnapshot g_outputs;
static ChannelId      g_currentGear = CH_GEAR_N;

// Test overrides — `test_*` JSON commands force an output for ~500 ms.
constexpr uint32_t TEST_HOLD_MS = 500;
static uint16_t g_testAxisValue[CH_COUNT] = {0};
static uint32_t g_testAxisUntil[CH_COUNT] = {0};
static ChannelId g_testGearOverride = CH_GEAR_N;
static uint32_t  g_testGearUntil    = 0;

// Sequential pulse state. PrevPressed lives here so we can edge-detect
// across mappingTick() calls without exposing it.
struct Pulse {
  uint8_t  pin;
  uint32_t endMillis    = 0;
  bool     active       = false;
  bool     prevPressed  = false;

  void start(uint16_t durationMs) {
    digitalWrite(pin, LOW);
    endMillis = millis() + (durationMs ? durationMs : DEFAULT_PULSE_MS);
    active    = true;
  }
  void update() {
    if (active && (int32_t)(millis() - endMillis) >= 0) {
      digitalWrite(pin, HIGH);
      active = false;
    }
  }
};
static Pulse g_pulseUp   = { PIN_SEQ_UP   };
static Pulse g_pulseDown = { PIN_SEQ_DOWN };

// ---------------- CRC-32/ISO-HDLC (zlib/PNG) ----------------
// Polynomial 0xEDB88320 reflected, init 0xFFFFFFFF, final XOR 0xFFFFFFFF.
// Same algorithm the web app implements client-side for preset validation.
static uint32_t crc32(const uint8_t* data, size_t len) {
  uint32_t crc = 0xFFFFFFFFu;
  for (size_t i = 0; i < len; ++i) {
    crc ^= data[i];
    for (uint8_t j = 0; j < 8; ++j) {
      const uint32_t mask = -(int32_t)(crc & 1u);
      crc = (crc >> 1) ^ (0xEDB88320u & mask);
    }
  }
  return crc ^ 0xFFFFFFFFu;
}

// ---------------- Channel name table ----------------

struct ChannelNameRow {
  ChannelId   id;
  const char* name;
};
static const ChannelNameRow CHANNEL_NAMES[] = {
  { CH_GEAR_R,     "gear_R" },
  { CH_GEAR_1,     "gear_1" },
  { CH_GEAR_2,     "gear_2" },
  { CH_GEAR_3,     "gear_3" },
  { CH_GEAR_4,     "gear_4" },
  { CH_GEAR_5,     "gear_5" },
  { CH_GEAR_6,     "gear_6" },
  { CH_GEAR_7,     "gear_7" },
  { CH_GEAR_N,     "gear_N" },
  { CH_SHIFT_UP,   "shift_up" },
  { CH_SHIFT_DOWN, "shift_down" },
  { CH_HANDBRAKE,  "handbrake" },
  { CH_THROTTLE,   "throttle" },
  { CH_BRAKE,      "brake" },
  { CH_CLUTCH,     "clutch" },
};
static constexpr size_t CHANNEL_NAMES_COUNT =
  sizeof(CHANNEL_NAMES) / sizeof(CHANNEL_NAMES[0]);

const char* mappingChannelName(ChannelId id) {
  for (size_t i = 0; i < CHANNEL_NAMES_COUNT; ++i) {
    if (CHANNEL_NAMES[i].id == id) return CHANNEL_NAMES[i].name;
  }
  return "unknown";
}

ChannelId mappingChannelByName(const char* name) {
  if (!name) return CH_NONE;
  for (size_t i = 0; i < CHANNEL_NAMES_COUNT; ++i) {
    if (strcmp(name, CHANNEL_NAMES[i].name) == 0) return CHANNEL_NAMES[i].id;
  }
  return CH_NONE;
}

ChannelBindings* mappingChannelBindings(ChannelId id) {
  switch (id) {
    case CH_GEAR_R:     return &g_cfg.gear[0];
    case CH_GEAR_1:     return &g_cfg.gear[1];
    case CH_GEAR_2:     return &g_cfg.gear[2];
    case CH_GEAR_3:     return &g_cfg.gear[3];
    case CH_GEAR_4:     return &g_cfg.gear[4];
    case CH_GEAR_5:     return &g_cfg.gear[5];
    case CH_GEAR_6:     return &g_cfg.gear[6];
    case CH_GEAR_7:     return &g_cfg.gear[7];
    case CH_GEAR_N:     return &g_cfg.gear[GEAR_N_BINDING_INDEX];
    case CH_SHIFT_UP:   return &g_cfg.shiftUp;
    case CH_SHIFT_DOWN: return &g_cfg.shiftDown;
    case CH_HANDBRAKE:  return &g_cfg.handbrake;
    case CH_THROTTLE:   return &g_cfg.throttle;
    case CH_BRAKE:      return &g_cfg.brake;
    case CH_CLUTCH:     return &g_cfg.clutch;
    default:            return nullptr;  // CH_NONE
  }
}

InputBinding* mappingBindingSlot(ChannelId id, uint8_t slot) {
  if (slot >= MAX_BINDINGS_PER_CHANNEL) return nullptr;
  ChannelBindings* cb = mappingChannelBindings(id);
  return cb ? &cb->bindings[slot] : nullptr;
}

GearOutputCalibration* mappingGearOutFor(ChannelId id) {
  if (id >= CH_GEAR_R && id <= CH_GEAR_N) {
    return &g_cfg.gearOut[id - CH_GEAR_R];
  }
  return nullptr;
}

// ---------------- Evaluators ----------------

// Apply rawMin/rawMax linear remap, invert, deadzones to a raw axis value.
// Output is 0..65535. An all-zero (rawMin=rawMax=0) binding passes raw through
// so the UI's "uncalibrated" first-look mode still shows movement.
static uint16_t scaleAxis(uint16_t raw, const InputBinding& b) {
  uint32_t scaled;
  if (b.rawMin == 0 && b.rawMax == 0) {
    scaled = raw;
  } else if (raw <= b.rawMin) {
    scaled = 0;
  } else if (raw >= b.rawMax) {
    scaled = 65535;
  } else {
    scaled = (uint32_t)(raw - b.rawMin) * 65535u / (uint32_t)(b.rawMax - b.rawMin);
  }
  if (b.invert) scaled = 65535u - scaled;
  if (scaled <= b.deadzoneLow)  return 0;
  if (scaled >= b.deadzoneHigh) return 65535;
  return (uint16_t)scaled;
}

uint16_t evalAxis(const InputBinding& b) {
  if (b.type == INPUT_NONE) return 0;

  // Button input → 0 or 65535. OR aggregation across same-VID/PID slots.
  if (b.type == INPUT_BUTTON) {
    if (b.index >= 32) return 0;
    const uint32_t mask = (1u << b.index);
    bool pressed = false;
    for (uint8_t i = 0; i < devicePoolSize(); ++i) {
      GenericJoystickHID* d = devicePoolSlot(i);
      if (!d->connected())             continue;
      if (d->vid() != b.vid || d->pid() != b.pid) continue;
      if (d->buttons() & mask) { pressed = true; break; }
    }
    if (b.invert) pressed = !pressed;
    return pressed ? 65535 : 0;
  }

  // Hat input → strict direction match across same-VID/PID slots, mapped
  // to 0 or 65535. Lets a D-pad direction drive an axis-shaped channel
  // (e.g. handbrake) the same way a button can.
  if (b.type == INPUT_HAT) {
    if (b.index > 7) return 0;
    bool matched = false;
    for (uint8_t i = 0; i < devicePoolSize(); ++i) {
      GenericJoystickHID* d = devicePoolSlot(i);
      if (!d->connected())             continue;
      if (d->vid() != b.vid || d->pid() != b.pid) continue;
      if (d->hat() == b.index) { matched = true; break; }
    }
    if (b.invert) matched = !matched;
    return matched ? 65535 : 0;
  }

  // Keyboard key input → key currently pressed on any matching slot.
  // Mapped to 0 or 65535 like the button-as-axis path.
  if (b.type == INPUT_KEY) {
    if (!b.index) return 0;
    bool pressed = false;
    for (uint8_t i = 0; i < devicePoolSize(); ++i) {
      GenericJoystickHID* d = devicePoolSlot(i);
      if (!d->connected())             continue;
      if (d->vid() != b.vid || d->pid() != b.pid) continue;
      if (d->isKeyPressed((uint8_t)b.index)) { pressed = true; break; }
    }
    if (b.invert) pressed = !pressed;
    return pressed ? 65535 : 0;
  }

  // Axis input → MAX raw across matching slots, then scaleAxis.
  if (b.index >= DEVICE_MAX_AXES) return 0;
  bool found = false;
  uint16_t maxRaw = 0;
  for (uint8_t i = 0; i < devicePoolSize(); ++i) {
    GenericJoystickHID* d = devicePoolSlot(i);
    if (!d->connected())             continue;
    if (d->vid() != b.vid || d->pid() != b.pid) continue;
    const uint16_t v = d->axis(b.index);
    if (!found || v > maxRaw) { maxRaw = v; found = true; }
  }
  if (!found) return 0;
  return scaleAxis(maxRaw, b);
}

bool evalButton(const InputBinding& b) {
  if (b.type == INPUT_NONE) return false;

  if (b.type == INPUT_BUTTON) {
    if (b.index >= 32) return false;
    const uint32_t mask = (1u << b.index);
    bool pressed = false;
    for (uint8_t i = 0; i < devicePoolSize(); ++i) {
      GenericJoystickHID* d = devicePoolSlot(i);
      if (!d->connected())             continue;
      if (d->vid() != b.vid || d->pid() != b.pid) continue;
      if (d->buttons() & mask) { pressed = true; break; }
    }
    if (b.invert) pressed = !pressed;
    return pressed;
  }

  // Hat direction matches strictly — diagonals do NOT fire cardinal
  // bindings. Use multiple slots on a channel for lenient matching.
  if (b.type == INPUT_HAT) {
    if (b.index > 7) return false;
    bool matched = false;
    for (uint8_t i = 0; i < devicePoolSize(); ++i) {
      GenericJoystickHID* d = devicePoolSlot(i);
      if (!d->connected())             continue;
      if (d->vid() != b.vid || d->pid() != b.pid) continue;
      if (d->hat() == b.index) { matched = true; break; }
    }
    if (b.invert) matched = !matched;
    return matched;
  }

  // Keyboard key — pressed if any matching slot has the scancode in its
  // currently-pressed set.
  if (b.type == INPUT_KEY) {
    if (!b.index) return false;
    bool pressed = false;
    for (uint8_t i = 0; i < devicePoolSize(); ++i) {
      GenericJoystickHID* d = devicePoolSlot(i);
      if (!d->connected())             continue;
      if (d->vid() != b.vid || d->pid() != b.pid) continue;
      if (d->isKeyPressed((uint8_t)b.index)) { pressed = true; break; }
    }
    if (b.invert) pressed = !pressed;
    return pressed;
  }

  // Axis-as-button → threshold against post-scale value, so a single 50%
  // default (32768) works regardless of source bit depth.
  return evalAxis(b) >= b.threshold;
}

uint16_t evalChannelAxis(const ChannelBindings& cb) {
  uint16_t best = 0;
  for (uint8_t i = 0; i < MAX_BINDINGS_PER_CHANNEL; ++i) {
    const InputBinding& b = cb.bindings[i];
    if (b.type == INPUT_NONE) continue;
    const uint16_t v = evalAxis(b);
    if (v > best) best = v;
  }
  return best;
}

bool evalChannelButton(const ChannelBindings& cb) {
  for (uint8_t i = 0; i < MAX_BINDINGS_PER_CHANNEL; ++i) {
    const InputBinding& b = cb.bindings[i];
    if (b.type == INPUT_NONE) continue;
    if (evalButton(b)) return true;
  }
  return false;
}

// ---------------- Channel updaters ----------------

static void writeGearDac(ChannelId gear) {
  if (gear < CH_GEAR_R || gear > CH_GEAR_N) return;
  const GearOutputCalibration& g = g_cfg.gearOut[gear - CH_GEAR_R];
  analogWrite(PIN_X, g.x);
  analogWrite(PIN_Y, g.y);
}

// Rising-edge shadow for latch mode — one bool per gear binding slot,
// remembers whether the binding was active last tick so we can fire only
// once per press.
static bool g_gearPrevPressed[NUM_GEAR_BINDINGS] = {0};

static void transitGear(ChannelId newGear, const char* tag) {
  if (newGear == g_currentGear) return;
  if (newGear != CH_GEAR_N && g_currentGear != CH_GEAR_N) {
    writeGearDac(CH_GEAR_N);
    delay(NEUTRAL_TRANSIT_MS);
  }
  writeGearDac(newGear);
  g_currentGear = newGear;
  Serial.print(tag);
  Serial.println(mappingChannelName(g_currentGear));
}

static void updateShifter() {
  // Test override wins (both modes).
  if (g_testGearUntil != 0 && (int32_t)(millis() - g_testGearUntil) < 0) {
    transitGear(g_testGearOverride, "[Shifter/test] → ");
    g_outputs.gear = g_currentGear;
    return;
  }
  if (g_testGearUntil != 0) g_testGearUntil = 0;  // expired; clear

  if (g_cfg.gearMode == GEAR_MODE_LATCH) {
    // Latch: rising-edge on a gear binding switches the current gear.
    // The gear persists until another rising edge moves it elsewhere
    // (including gear_N as the explicit "shift to neutral" key). First
    // edge per tick wins on simultaneous keypresses — deterministic.
    ChannelId newGear = g_currentGear;
    bool changed = false;
    for (uint8_t i = 0; i < NUM_GEAR_BINDINGS; ++i) {
      const bool pressed = evalChannelButton(g_cfg.gear[i]);
      if (pressed && !g_gearPrevPressed[i] && !changed) {
        newGear = (ChannelId)(CH_GEAR_R + i);
        changed = true;
      }
      g_gearPrevPressed[i] = pressed;
    }
    if (changed) transitGear(newGear, "[Shifter/latch] → ");
    g_outputs.gear = g_currentGear;
    return;
  }

  // Hold mode (default): gear active only while binding held.
  // Walk gear bindings R, 1..7. Count true hits.
  // 0 → neutral.  1 → that gear.  2+ → neutral (defensive: matches the
  // pre-refactor buttonsToGear() behavior where any unexpected multi-bit
  // value fell through to GEAR_N).
  ChannelId target = CH_GEAR_N;
  uint8_t hits = 0;
  for (uint8_t i = 0; i < GEAR_N_BINDING_INDEX; ++i) {
    if (evalChannelButton(g_cfg.gear[i])) {
      ++hits;
      if (hits == 1) target = (ChannelId)(CH_GEAR_R + i);
    }
  }
  if (hits >= 2) target = CH_GEAR_N;
  // Explicit neutral binding overrides — useful as a panic-neutral key
  // independent of whether any gear binding is also active.
  if (evalChannelButton(g_cfg.gear[GEAR_N_BINDING_INDEX])) target = CH_GEAR_N;

  transitGear(target, "[Shifter] → ");
  // Reset the latch shadow whenever we're in hold mode so a later switch
  // to latch starts clean.
  memset(g_gearPrevPressed, 0, sizeof(g_gearPrevPressed));
  g_outputs.gear = g_currentGear;
}

static void updateSequential() {
  const bool upNow   = evalChannelButton(g_cfg.shiftUp);
  const bool downNow = evalChannelButton(g_cfg.shiftDown);

  // Rising-edge detect (don't refire on held).
  if (upNow && !g_pulseUp.prevPressed) {
    g_pulseUp.start(g_cfg.pulseMs);
    Serial.println("[Seq] UP");
  }
  if (downNow && !g_pulseDown.prevPressed) {
    g_pulseDown.start(g_cfg.pulseMs);
    Serial.println("[Seq] DOWN");
  }
  g_pulseUp.prevPressed   = upNow;
  g_pulseDown.prevPressed = downNow;

  g_pulseUp.update();
  g_pulseDown.update();

  g_outputs.shiftUp   = g_pulseUp.active;
  g_outputs.shiftDown = g_pulseDown.active;
}

static bool testAxisActive(ChannelId ch) {
  if (ch >= CH_COUNT)         return false;
  if (g_testAxisUntil[ch] == 0) return false;
  if ((int32_t)(millis() - g_testAxisUntil[ch]) >= 0) {
    g_testAxisUntil[ch] = 0;
    return false;
  }
  return true;
}

static void updateHandbrake() {
  uint16_t out = evalChannelAxis(g_cfg.handbrake);
  if (testAxisActive(CH_HANDBRAKE)) out = g_testAxisValue[CH_HANDBRAKE];

  // Pedal stream gets the value verbatim (0..65535) — no shift needed,
  // evalAxis already produces a 16-bit value.
  setPedalHandbrake(out);
  // DAC pin is 12-bit. Downscale 16→12.
  analogWrite(PIN_HANDBRAKE, out >> 4);

  g_outputs.handbrake = out;
}

static void updatePedal(ChannelId ch,
                        void (*setter)(uint16_t),
                        uint16_t& outField) {
  ChannelBindings* cb = mappingChannelBindings(ch);
  if (!cb) return;
  uint16_t out = evalChannelAxis(*cb);
  if (testAxisActive(ch)) out = g_testAxisValue[ch];
  setter(out);
  outField = out;
}

void mappingTick() {
  updateShifter();
  updateSequential();
  updateHandbrake();
  updatePedal(CH_THROTTLE, setPedalThrottle, g_outputs.throttle);
  updatePedal(CH_BRAKE,    setPedalBrake,    g_outputs.brake);
  updatePedal(CH_CLUTCH,   setPedalClutch,   g_outputs.clutch);
}

const OutputSnapshot& mappingOutputs() {
  return g_outputs;
}

// ---------------- Lifecycle ----------------

void mappingReset() {
  memset(&g_cfg, 0, sizeof(Config));
  g_cfg.magic    = CONFIG_MAGIC;
  g_cfg.version  = CONFIG_VERSION;
  g_cfg.pulseMs  = DEFAULT_PULSE_MS;
  g_cfg.gearMode = GEAR_MODE_HOLD;
  for (uint8_t i = 0; i < NUM_GEAR_OUTPUTS; ++i) {
    g_cfg.gearOut[i] = GEAR_DEFAULT_OUT[i];
  }
  mappingRecomputeCrc();

  // Clear test overrides so a reset_config doesn't leave a ghost holding.
  for (uint8_t i = 0; i < CH_COUNT; ++i) {
    g_testAxisUntil[i] = 0;
    g_testAxisValue[i] = 0;
  }
  g_testGearUntil = 0;
  g_currentGear   = CH_GEAR_N;

  // Drive outputs to neutral immediately so a fresh-flash boot doesn't sit
  // at 0V on the X/Y DACs (which is not a valid Fanatec column).
  writeGearDac(CH_GEAR_N);
  setPedalHandbrake(0);
  setPedalThrottle(0);
  setPedalBrake(0);
  setPedalClutch(0);
  analogWrite(PIN_HANDBRAKE, 0);

  memset(&g_outputs, 0, sizeof(OutputSnapshot));
  g_outputs.gear = CH_GEAR_N;
}

void mappingRecomputeCrc() {
  g_cfg.crc = crc32((const uint8_t*)&g_cfg, sizeof(Config) - sizeof(uint32_t));
}

void mappingInit() {
  // Read EEPROM bytes into a temp Config and validate before adopting.
  Config tmp;
  uint8_t* p = (uint8_t*)&tmp;
  for (size_t i = 0; i < sizeof(Config); ++i) {
    p[i] = EEPROM.read(i);
  }

  const bool magicOk   = (tmp.magic   == CONFIG_MAGIC);
  const bool versionOk = (tmp.version == CONFIG_VERSION);
  uint32_t   crcCalc   = 0;
  if (magicOk && versionOk) {
    crcCalc = crc32(p, sizeof(Config) - sizeof(uint32_t));
  }

  if (magicOk && versionOk && crcCalc == tmp.crc) {
    g_cfg = tmp;
    Serial.println("[Config] loaded from EEPROM");
    g_currentGear = CH_GEAR_N;
    writeGearDac(CH_GEAR_N);  // start at neutral, gear updates apply over time
    memset(&g_outputs, 0, sizeof(OutputSnapshot));
    g_outputs.gear = CH_GEAR_N;
    return;
  }

  if (!magicOk) {
    Serial.println("[Config] EEPROM magic missing — first boot or wiped; using defaults");
  } else if (!versionOk) {
    Serial.print("[Config] EEPROM version mismatch (have=");
    Serial.print(tmp.version);
    Serial.print(" expected=");
    Serial.print(CONFIG_VERSION);
    Serial.println(") — using defaults");
  } else {
    Serial.println("[Config] EEPROM CRC mismatch — using defaults");
  }
  mappingReset();
}

bool mappingSave() {
  mappingRecomputeCrc();
  const uint8_t* p = (const uint8_t*)&g_cfg;
  for (size_t i = 0; i < sizeof(Config); ++i) {
    EEPROM.update(i, p[i]);
  }
  // Verify the bytes round-trip.
  for (size_t i = 0; i < sizeof(Config); ++i) {
    if (EEPROM.read(i) != p[i]) {
      Serial.print("[Config] EEPROM verify FAILED at byte ");
      Serial.println(i);
      return false;
    }
  }
  Serial.println("[Config] saved to EEPROM");
  return true;
}

// ---------------- Config access ----------------

const Config& mappingConfig()         { return g_cfg; }
Config&       mappingConfigMutable()  { return g_cfg; }

// ---------------- Test overrides ----------------

void mappingTestAxis(ChannelId ch, uint16_t value) {
  if (ch != CH_HANDBRAKE && ch != CH_THROTTLE &&
      ch != CH_BRAKE     && ch != CH_CLUTCH) return;
  g_testAxisValue[ch] = value;
  g_testAxisUntil[ch] = millis() + TEST_HOLD_MS;
}

void mappingTestPulse(bool up) {
  Pulse& p = up ? g_pulseUp : g_pulseDown;
  p.start(g_cfg.pulseMs);
}

void mappingTestGear(ChannelId ch) {
  if (ch < CH_GEAR_R || ch > CH_GEAR_N) return;
  g_testGearOverride = ch;
  g_testGearUntil    = millis() + TEST_HOLD_MS;
}
