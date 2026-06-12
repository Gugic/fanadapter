#include "protocol.h"

#include <ArduinoJson.h>
#include <freertos/FreeRTOS.h>
#include <freertos/semphr.h>
#include <string.h>

#include "device_pool.h"
#include "mapping.h"
#include "pedals.h"

// ---------------- Tuning ----------------

constexpr size_t LINE_BUF_SIZE =
    1024; // largest expected incoming line; grew for multi-binding get_config
constexpr uint32_t INPUTS_PERIOD_MS = 20;  // ~50 Hz max
constexpr uint32_t OUTPUTS_PERIOD_MS = 33; // ~30 Hz

// ---------------- State ----------------

static char g_lineBuf[LINE_BUF_SIZE];
static size_t g_lineLen = 0;
static bool g_inJsonLine = false;
static CliCharCallback g_cliCb = nullptr;

static bool g_liveInputs = false;
static bool g_liveOutputs = false;
static uint32_t g_lastInputsMs = 0;
static uint32_t g_lastOutputsMs = 0;

// Recursive mutex serialising Serial writes across the main loop task and
// the USB host task. Created in protocolInit(); see protocol.h for why.
static SemaphoreHandle_t g_serialMutex = nullptr;

struct SlotShadow {
  bool connected = false;
  uint16_t vid = 0;
  uint16_t pid = 0;
  uint32_t lastChangeSeq = 0;
};
static_assert(sizeof(SlotShadow) == 12, "SlotShadow layout size mismatch");
static SlotShadow g_slotShadow[DEVICE_POOL_SIZE];

// ---------------- Helpers ----------------

static const char* inputTypeName(uint8_t t) {
  switch (t) {
    case INPUT_BUTTON:
      return "button";
    case INPUT_AXIS:
      return "axis";
    case INPUT_HAT:
      return "hat";
    case INPUT_KEY:
      return "key";
    default:
      return "none";
  }
}

static uint8_t inputTypeFromName(const char* s) {
  if (!s)
    return INPUT_NONE;
  if (strcmp(s, "button") == 0)
    return INPUT_BUTTON;
  if (strcmp(s, "axis") == 0)
    return INPUT_AXIS;
  if (strcmp(s, "hat") == 0)
    return INPUT_HAT;
  if (strcmp(s, "key") == 0)
    return INPUT_KEY;
  return INPUT_NONE;
}

static void writeBindingTo(JsonObject obj, const InputBinding& b) {
  obj["vid"] = b.vid;
  obj["pid"] = b.pid;
  obj["type"] = inputTypeName(b.type);
  obj["index"] = b.index;
  obj["threshold"] = b.threshold;
  obj["rawMin"] = b.rawMin;
  obj["rawMax"] = b.rawMax;
  obj["deadzoneLow"] = b.deadzoneLow;
  obj["deadzoneHigh"] = b.deadzoneHigh;
  obj["invert"] = (bool)b.invert;
}

static void writeChannelTo(JsonArray arr, const ChannelBindings& cb) {
  for (uint8_t i = 0; i < MAX_BINDINGS_PER_CHANNEL; ++i) {
    JsonObject obj = arr.add<JsonObject>();
    writeBindingTo(obj, cb.bindings[i]);
  }
}

static void readBindingFrom(JsonVariantConst v, InputBinding& b) {
  if (v["vid"].is<int>())
    b.vid = v["vid"].as<uint16_t>();
  if (v["pid"].is<int>())
    b.pid = v["pid"].as<uint16_t>();
  if (v["type"].is<const char*>()) {
    uint8_t t = inputTypeFromName(v["type"]);
    if (t <= INPUT_KEY)
      b.type = t;
  }
  if (v["index"].is<int>()) {
    int idx = v["index"].as<int>();
    if (idx >= 0 && idx <= 255)
      b.index = (uint8_t)idx;
  }
  if (v["threshold"].is<int>())
    b.threshold = v["threshold"].as<uint16_t>();
  if (v["rawMin"].is<int>())
    b.rawMin = v["rawMin"].as<uint16_t>();
  if (v["rawMax"].is<int>())
    b.rawMax = v["rawMax"].as<uint16_t>();
  if (v["deadzoneLow"].is<int>())
    b.deadzoneLow = v["deadzoneLow"].as<uint16_t>();
  if (v["deadzoneHigh"].is<int>())
    b.deadzoneHigh = v["deadzoneHigh"].as<uint16_t>();
  if (v["invert"].is<bool>())
    b.invert = v["invert"].as<bool>() ? 1 : 0;
}

void serialLockTake() {
  if (g_serialMutex)
    xSemaphoreTakeRecursive(g_serialMutex, portMAX_DELAY);
}

void serialLockGive() {
  if (g_serialMutex)
    xSemaphoreGiveRecursive(g_serialMutex);
}

static void emit(const JsonDocument& doc, bool isTelemetry = false) {
  serialLockTake();
  // Telemetry (live/outputs) is droppable under back-pressure; command
  // responses and events are not. Check inside the lock so availableForWrite
  // reflects the buffer state at the moment we'd actually write.
  if (!(isTelemetry && Serial.availableForWrite() < 128)) {
    serializeJson(doc, Serial);
    Serial.println();
  }
  serialLockGive();
}

static void sendOk() {
  JsonDocument doc;
  doc["ok"] = true;
  emit(doc);
}

static void sendErr(const char* msg) {
  JsonDocument doc;
  doc["err"] = msg;
  emit(doc);
}

// ---------------- Commands ----------------

static void cmdVersion() {
  JsonDocument doc;
  doc["fw"] = "fanadapter";
  doc["ver"] = "0.8.0";
  doc["protocol"] = 5;
#if defined(CONFIG_IDF_TARGET_ESP32P4)
  doc["platform"] = "esp32p4";
#else
  doc["platform"] = "esp32s3";
#endif
  doc["max_bindings_per_channel"] = MAX_BINDINGS_PER_CHANNEL;
  emit(doc);
}

static void cmdListDevices() {
  JsonDocument doc;
  JsonArray arr = doc["devices"].to<JsonArray>();
  for (uint8_t i = 0; i < devicePoolSize(); ++i) {
    GenericJoystickHID* d = devicePoolSlot(i);
    JsonObject row = arr.add<JsonObject>();
    row["slot"] = i;
    row["connected"] = d->connected();
    row["vid"] = d->vid();
    row["pid"] = d->pid();
    row["manufacturer"] = d->manufacturerName();
    row["product"] = d->productName();
    row["axis_count"] = d->axisCount();
    row["button_count"] = d->buttonCount();
    row["has_hat"] = d->hasHat();
    row["has_keyboard"] = d->hasKeyboard();
  }
  emit(doc);
}

static const char* gearModeName(uint8_t m) { return (m == GEAR_MODE_LATCH) ? "latch" : "hold"; }

static uint8_t gearModeFromName(const char* s) {
  if (s && strcmp(s, "latch") == 0)
    return GEAR_MODE_LATCH;
  return GEAR_MODE_HOLD;
}

static void cmdGetConfig() {
  const Config& c = mappingConfig();
  JsonDocument doc;
  doc["version"] = c.version;
  doc["pulseMs"] = c.pulseMs;
  doc["gearMode"] = gearModeName(c.gearMode);
  doc["max_bindings_per_channel"] = MAX_BINDINGS_PER_CHANNEL;

  JsonObject gear = doc["gear"].to<JsonObject>();
  for (uint8_t i = 0; i < NUM_GEAR_BINDINGS; ++i) {
    ChannelId ch = (ChannelId)(CH_GEAR_R + i);
    JsonArray arr = gear[mappingChannelName(ch)].to<JsonArray>();
    writeChannelTo(arr, c.gear[i]);
  }

  JsonObject gearOut = doc["gearOut"].to<JsonObject>();
  for (uint8_t i = 0; i < NUM_GEAR_OUTPUTS; ++i) {
    ChannelId ch = (ChannelId)(CH_GEAR_R + i);
    JsonObject obj = gearOut[mappingChannelName(ch)].to<JsonObject>();
    obj["x"] = c.gearOut[i].x;
    obj["y"] = c.gearOut[i].y;
  }

  writeChannelTo(doc["shift_up"].to<JsonArray>(), c.shiftUp);
  writeChannelTo(doc["shift_down"].to<JsonArray>(), c.shiftDown);
  writeChannelTo(doc["handbrake"].to<JsonArray>(), c.handbrake);
  writeChannelTo(doc["throttle"].to<JsonArray>(), c.throttle);
  writeChannelTo(doc["brake"].to<JsonArray>(), c.brake);
  writeChannelTo(doc["clutch"].to<JsonArray>(), c.clutch);

  emit(doc);
}

static void cmdSetBinding(const JsonDocument& doc) {
  const char* chName = doc["channel"] | (const char*)nullptr;
  ChannelId ch = mappingChannelByName(chName);
  // `slot` is optional, defaults to 0 — preserves single-binding ergonomics.
  const uint8_t slot = doc["slot"].is<int>() ? doc["slot"].as<uint8_t>() : 0;
  if (slot >= MAX_BINDINGS_PER_CHANNEL) {
    sendErr("invalid_slot");
    return;
  }
  InputBinding* b = mappingBindingSlot(ch, slot);
  if (!b) {
    sendErr("unknown_channel_or_slot");
    return;
  }
  JsonVariantConst bind = doc["binding"];
  if (bind.isNull()) {
    sendErr("missing_binding");
    return;
  }
  readBindingFrom(bind, *b);
  mappingRecomputeCrc();
  sendOk();
}

static void cmdSetGearDac(const JsonDocument& doc) {
  const char* chName = doc["channel"] | (const char*)nullptr;
  ChannelId ch = mappingChannelByName(chName);
  GearOutputCalibration* g = mappingGearOutFor(ch);
  if (!g) {
    sendErr("not_a_gear_channel");
    return;
  }
  if (doc["x"].is<int>())
    g->x = doc["x"].as<uint16_t>();
  if (doc["y"].is<int>())
    g->y = doc["y"].as<uint16_t>();
  mappingRecomputeCrc();
  sendOk();
}

static void cmdSetPulseMs(const JsonDocument& doc) {
  if (!doc["value"].is<int>()) {
    sendErr("missing_value");
    return;
  }
  mappingConfigMutable().pulseMs = doc["value"].as<uint16_t>();
  mappingRecomputeCrc();
  sendOk();
}

static void cmdSetGearMode(const JsonDocument& doc) {
  const char* v = doc["value"] | (const char*)nullptr;
  if (!v) {
    sendErr("missing_value");
    return;
  }
  mappingConfigMutable().gearMode = gearModeFromName(v);
  mappingRecomputeCrc();
  sendOk();
}

static void cmdSaveConfig() {
  if (!mappingSave()) {
    sendErr("nvs_verify_failed");
    return;
  }
  sendOk();
}

static void cmdResetConfig() {
  mappingReset();
  sendOk();
}

static void cmdLiveInputs(const JsonDocument& doc) {
  if (doc["on"].is<bool>())
    g_liveInputs = doc["on"].as<bool>();
  sendOk();
}

static void cmdLiveOutputs(const JsonDocument& doc) {
  if (doc["on"].is<bool>())
    g_liveOutputs = doc["on"].as<bool>();
  sendOk();
}

static void cmdTestAxis(const JsonDocument& doc) {
  const char* chName = doc["channel"] | (const char*)nullptr;
  ChannelId ch = mappingChannelByName(chName);
  if (ch != CH_HANDBRAKE && ch != CH_THROTTLE && ch != CH_BRAKE && ch != CH_CLUTCH) {
    sendErr("not_an_axis_channel");
    return;
  }
  uint16_t v = 0;
  if (doc["value"].is<int>())
    v = doc["value"].as<uint16_t>();
  mappingTestAxis(ch, v);
  sendOk();
}

static void cmdTestPulse(const JsonDocument& doc) {
  const char* dir = doc["direction"] | (const char*)nullptr;
  if (!dir) {
    sendErr("missing_direction");
    return;
  }
  if (strcmp(dir, "up") == 0)
    mappingTestPulse(true);
  else if (strcmp(dir, "down") == 0)
    mappingTestPulse(false);
  else {
    sendErr("invalid_direction");
    return;
  }
  sendOk();
}

static void cmdTestGear(const JsonDocument& doc) {
  const char* chName = doc["channel"] | (const char*)nullptr;
  ChannelId ch = mappingChannelByName(chName);
  if (ch < CH_GEAR_R || ch > CH_GEAR_N) {
    sendErr("not_a_gear_channel");
    return;
  }
  mappingTestGear(ch);
  sendOk();
}

// Soft-reset the ESP32 back into this sketch (NOT bootloader). Useful when
// the wheelbase pedals UART desyncs or you want a clean reload of RAM state
// without unplugging USB. ESP.restart() triggers the chip's RTC watchdog
// reset path and is safe to call from any task.
static void cmdReboot() {
  sendOk();
  Serial.flush(); // drain the OK before the chip restarts
  delay(50);
  ESP.restart();
  while (1) {
  } // never reached
}

// Re-initialise the CSL Elite pedals UART state machine back to Step 0 at
// 250000 baud. The chip reboot survives the wheelbase keeping its prior
// state; this lets the user force a fresh handshake from the UI when the
// wheelbase isn't re-initiating on its own.
static void cmdResetPedals() {
  pedalsForceReset();
  sendOk();
}

// Report the pedal handshake state (HANDSHAKE_250K / HANDSHAKE_115K /
// STREAMING_115K / INIT). Lets the UI surface why the wheelbase isn't
// receiving pedal data.
static void cmdPedalsStatus() {
  JsonDocument doc;
  doc["state"] = getPedalsStateName();
  doc["throttle"] = getPedalThrottle();
  doc["brake"] = getPedalBrake();
  doc["clutch"] = getPedalClutch();
  doc["handbrake"] = getPedalHandbrake();
  emit(doc);
}

// ---------------- Dispatcher ----------------

static void handleJsonLine(const char* line) {
  JsonDocument doc;
  const DeserializationError err = deserializeJson(doc, line);
  if (err) {
    sendErr("json_parse_error");
    return;
  }

  const char* cmd = doc["cmd"] | (const char*)nullptr;
  if (!cmd) {
    sendErr("missing_cmd");
    return;
  }

  if (strcmp(cmd, "version") == 0)
    cmdVersion();
  else if (strcmp(cmd, "list_devices") == 0)
    cmdListDevices();
  else if (strcmp(cmd, "get_config") == 0)
    cmdGetConfig();
  else if (strcmp(cmd, "set_binding") == 0)
    cmdSetBinding(doc);
  else if (strcmp(cmd, "set_gear_dac") == 0)
    cmdSetGearDac(doc);
  else if (strcmp(cmd, "set_pulse_ms") == 0)
    cmdSetPulseMs(doc);
  else if (strcmp(cmd, "set_gear_mode") == 0)
    cmdSetGearMode(doc);
  else if (strcmp(cmd, "save_config") == 0)
    cmdSaveConfig();
  else if (strcmp(cmd, "reset_config") == 0)
    cmdResetConfig();
  else if (strcmp(cmd, "live_inputs") == 0)
    cmdLiveInputs(doc);
  else if (strcmp(cmd, "live_outputs") == 0)
    cmdLiveOutputs(doc);
  else if (strcmp(cmd, "test_axis") == 0)
    cmdTestAxis(doc);
  else if (strcmp(cmd, "test_pulse") == 0)
    cmdTestPulse(doc);
  else if (strcmp(cmd, "test_gear") == 0)
    cmdTestGear(doc);
  else if (strcmp(cmd, "reboot") == 0)
    cmdReboot();
  else if (strcmp(cmd, "reset_pedals") == 0)
    cmdResetPedals();
  else if (strcmp(cmd, "pedals_status") == 0)
    cmdPedalsStatus();
  else
    sendErr("unknown_cmd");
}

// ---------------- Events ----------------

static void emitDeviceAttached(uint8_t slot, GenericJoystickHID* d) {
  JsonDocument doc;
  doc["event"] = "device_attached";
  doc["slot"] = slot;
  doc["vid"] = d->vid();
  doc["pid"] = d->pid();
  doc["manufacturer"] = d->manufacturerName();
  doc["product"] = d->productName();
  doc["axis_count"] = d->axisCount();
  doc["button_count"] = d->buttonCount();
  doc["has_hat"] = d->hasHat();
  doc["has_keyboard"] = d->hasKeyboard();
  emit(doc);
}

static void emitDeviceDetached(uint8_t slot) {
  JsonDocument doc;
  doc["event"] = "device_detached";
  doc["slot"] = slot;
  emit(doc);
}

static void emitLiveSlot(uint8_t slot, GenericJoystickHID* d) {
  JsonDocument doc;
  doc["event"] = "live";
  doc["slot"] = slot;
  doc["buttons"] = d->buttons();
  JsonArray axes = doc["axes"].to<JsonArray>();
  const uint8_t n = d->axisCount();
  for (uint8_t i = 0; i < n && i < DEVICE_MAX_AXES; ++i) {
    axes.add(d->axis(i));
  }
  // Hat state — only emit for devices that actually have a hat. Direction
  // 0..7 is reported as a number, released as -1 (chosen over JSON null
  // to dodge ArduinoJson v7's null-emission edge cases and to keep the
  // wire shape numeric-only). "Device has no hat at all" is represented
  // by omitting the field, which the client detects via has_hat from
  // device_attached / list_devices.
  if (d->hasHat()) {
    const uint8_t h = d->hat();
    doc["hat"] = (h == GenericJoystickHID::HAT_RELEASED) ? -1 : (int)h;
  }
  // Keyboard state — array of currently-pressed scancodes (HID Keyboard/
  // Keypad usage IDs). Zero-padded to MAX_KEYS_PRESSED so the client gets
  // a stable shape; empty slots are 0. Field is omitted for devices that
  // aren't a keyboard.
  if (d->hasKeyboard()) {
    JsonArray keys = doc["keys"].to<JsonArray>();
    for (uint8_t i = 0; i < GenericJoystickHID::MAX_KEYS_PRESSED; ++i) {
      keys.add(d->keyAt(i));
    }
  }
  emit(doc, true);
}

static void emitLiveOutputs() {
  const OutputSnapshot& o = mappingOutputs();
  JsonDocument doc;
  doc["event"] = "outputs";
  doc["gear"] = mappingChannelName(o.gear);
  doc["shift_up"] = o.shiftUp;
  doc["shift_down"] = o.shiftDown;
  doc["throttle"] = o.throttle;
  doc["brake"] = o.brake;
  doc["clutch"] = o.clutch;
  doc["handbrake"] = o.handbrake;
  emit(doc, true);
}

// ---------------- Public API ----------------

void protocolInit() {
  // Create the Serial mutex before the USB host task is spawned (devicePool
  // Begin() runs after protocolInit() in setup()), so the first device's
  // connect diagnostics are already serialised against any main-loop output.
  if (!g_serialMutex)
    g_serialMutex = xSemaphoreCreateRecursiveMutex();
  g_lineLen = 0;
  g_inJsonLine = false;
  g_liveInputs = false;
  g_liveOutputs = false;
  g_lastInputsMs = 0;
  g_lastOutputsMs = 0;
  for (uint8_t i = 0; i < DEVICE_POOL_SIZE; ++i) {
    g_slotShadow[i] = SlotShadow{};
  }
}

void protocolSetCliCallback(CliCharCallback cb) { g_cliCb = cb; }

void protocolTick() {
  // NB: on ESP32 the config UART is a plain hardware UART, so `Serial` is
  // always "true" — there's no DTR equivalent that tells us the WebSerial
  // client has disconnected. Live subscriptions stay on until the client
  // sends live_inputs/live_outputs with on=false (or sends nothing, in
  // which case we just keep emitting into the UART FIFO and the
  // availableForWrite() guard in emit() drops telemetry if the buffer
  // fills up). The Teensy build dropped subscriptions on USB CDC close;
  // here, we don't have that signal.

  // Drain any pending HID-collection diagnostics from the device pool.
  // On ESP32 this is currently a no-op stub (see device_pool.cpp comment).
  uint16_t vids[8];
  uint16_t pids[8];
  uint32_t topusages[8];
  uint8_t count = devicePoolGetLoggedCollections(vids, pids, topusages, 8);
  if (count) {
    serialLockTake();
    for (uint8_t i = 0; i < count; ++i) {
      Serial.print("[USB/Info] HID Collection: VID=0x");
      Serial.print(vids[i], HEX);
      Serial.print(" PID=0x");
      Serial.print(pids[i], HEX);
      Serial.print(" topusage=0x");
      Serial.println(topusages[i], HEX);
    }
    serialLockGive();
  }

  // 1) Drain Serial bytes
  while (Serial.available()) {
    const char c = (char)Serial.read();

    if (!g_inJsonLine) {
      if (c == '{') {
        g_inJsonLine = true;
        g_lineLen = 0;
        g_lineBuf[g_lineLen++] = c;
      } else if (c == '\r' || c == '\n' || c == ' ' || c == '\t') {
        // skip whitespace between JSON lines / single-char commands
      } else if (g_cliCb) {
        g_cliCb(c);
      }
    } else {
      if (c == '\n') {
        g_lineBuf[g_lineLen] = 0;
        handleJsonLine(g_lineBuf);
        g_lineLen = 0;
        g_inJsonLine = false;
      } else if (c == '\r') {
        // ignore stray CR inside JSON line
      } else if (g_lineLen < LINE_BUF_SIZE - 1) {
        g_lineBuf[g_lineLen++] = c;
      } else {
        // overflow — drop the line, error, and resync
        g_lineLen = 0;
        g_inJsonLine = false;
        sendErr("line_too_long");
      }
    }
  }

  // 2) Device attach/detach transitions
  for (uint8_t i = 0; i < devicePoolSize(); ++i) {
    GenericJoystickHID* d = devicePoolSlot(i);
    SlotShadow& s = g_slotShadow[i];
    const bool nowConnected = d->connected();
    if (nowConnected != s.connected) {
      s.connected = nowConnected;
      if (nowConnected) {
        s.vid = d->vid();
        s.pid = d->pid();
        s.lastChangeSeq = d->changeSeq();
        emitDeviceAttached(i, d);
        // Print the running channel budget right after each attach. Lets
        // the user see, in webconfig's Logs tab, exactly how many of the
        // ESP32-S3's 8 HCD channels are spoken for and which interface
        // classes are eating them — the answer to "why won't the next
        // device enumerate".
        devicePoolDumpUsbDetail();
      } else {
        emitDeviceDetached(i);
      }
    }
  }

  // 3) Live inputs — emit changed slots, rate-limited
  if (g_liveInputs) {
    const uint32_t now = millis();
    if (now - g_lastInputsMs >= INPUTS_PERIOD_MS) {
      g_lastInputsMs = now;
      for (uint8_t i = 0; i < devicePoolSize(); ++i) {
        GenericJoystickHID* d = devicePoolSlot(i);
        if (!d->connected())
          continue;
        const uint32_t seq = d->changeSeq();
        if (seq != g_slotShadow[i].lastChangeSeq) {
          g_slotShadow[i].lastChangeSeq = seq;
          emitLiveSlot(i, d);
        }
      }
    }
  }

  // 4) Live outputs — emit current state, rate-limited
  if (g_liveOutputs) {
    const uint32_t now = millis();
    if (now - g_lastOutputsMs >= OUTPUTS_PERIOD_MS) {
      g_lastOutputsMs = now;
      emitLiveOutputs();
    }
  }
}
