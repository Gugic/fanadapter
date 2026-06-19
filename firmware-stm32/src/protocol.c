// Line-based JSON protocol over the console (USART1 + CDC) — the WebSerial contract. See protocol.h.
//
// M2 scope adds the config command set on top of M1's device/live machinery: get_config (streamed),
// set_binding, set_gear_dac, set_pulse_ms, set_gear_mode, save_config, reset_config. Command/event
// shapes match firmware/protocol.cpp and webconfig/src/lib/{serial.ts,types.ts} exactly.
#include "protocol.h"

#include <string.h>

#include "input_source.h"
#include "json_min.h"
#include "mapping.h"

extern int console_printf(const char *fmt, ...);

#define PROTO_VERSION    5
#define FW_NAME          "fanadapter-stm32"
#define FW_VER           "0.6.0"
#define LIVE_PERIOD_MS    33u // ~30 Hz; the console is the bottleneck (USART1 @115200 8E1)
#define OUTPUTS_PERIOD_MS 33u
#define MAX_GLOBAL_SLOTS  16u
#define MAX_TOP_KV        24
#define MAX_BIND_KV       16

static bool     g_live_inputs;
static bool     g_live_outputs;
static bool     g_shadow_connected[MAX_GLOBAL_SLOTS];
static uint32_t g_shadow_change[MAX_GLOBAL_SLOTS];
static uint32_t g_last_live_ms;
static uint32_t g_last_outputs_ms;

// ---------------- small response helpers --------------------------------------------------------
static void send_ok(void) { console_printf("{\"ok\":true}\r\n"); }
static void send_err(const char *msg) { console_printf("{\"err\":\"%s\"}\r\n", msg); }

// Escape a sanitized string for embedding in JSON (copy_string already stripped non-printables).
static void esc(const char *in, char *out, size_t outlen) {
  size_t o = 0;
  for (const char *p = in; *p && o < outlen - 2; p++) {
    if (*p == '"' || *p == '\\') out[o++] = '\\';
    out[o++] = *p;
  }
  out[o] = '\0';
}

// ---------------- type <-> name helpers ---------------------------------------------------------
static const char *input_type_name(uint8_t t) {
  switch (t) {
    case INPUT_BUTTON: return "button";
    case INPUT_AXIS: return "axis";
    case INPUT_HAT: return "hat";
    case INPUT_KEY: return "key";
    default: return "none";
  }
}
static uint8_t input_type_from_name(const char *s) {
  if (!s) return INPUT_NONE;
  if (!strcmp(s, "button")) return INPUT_BUTTON;
  if (!strcmp(s, "axis")) return INPUT_AXIS;
  if (!strcmp(s, "hat")) return INPUT_HAT;
  if (!strcmp(s, "key")) return INPUT_KEY;
  return INPUT_NONE;
}
static const char *gear_mode_name(uint8_t m) { return (m == GEAR_MODE_LATCH) ? "latch" : "hold"; }
static uint8_t     gear_mode_from_name(const char *s) {
  return (s && !strcmp(s, "latch")) ? GEAR_MODE_LATCH : GEAR_MODE_HOLD;
}

// Apply an optional uint16 binding field: only overwrite when the key is present (mirrors the
// Teensy's readBindingFrom, which skips absent fields so partial set_binding edits are safe).
static void apply_u16(const json_kv *kv, int n, const char *key, uint16_t *dst) {
  long v;
  if (json_get_int(kv, n, key, &v)) *dst = (uint16_t)v;
}

// ---------------- command responses (FIFO-matched to the request) -------------------------------
static void cmd_version(void) {
  console_printf("{\"fw\":\"%s\",\"ver\":\"%s\",\"protocol\":%u,\"max_bindings_per_channel\":%u}\r\n",
                 FW_NAME, FW_VER, PROTO_VERSION, MAX_BINDINGS_PER_CHANNEL);
}

static void cmd_list_devices(void) {
  console_printf("{\"devices\":[");
  bool first = true;
  for (uint8_t si = 0; si < input_source_count(); si++) {
    const InputSource *src = input_source_get(si);
    uint8_t            cnt = src->device_count();
    for (uint8_t i = 0; i < cnt; i++) {
      InputDeviceInfo d;
      if (!src->device_info(i, &d)) continue;
      char mbuf[DEVICE_STR_LEN * 2], pbuf[DEVICE_STR_LEN * 2];
      esc(d.manufacturer, mbuf, sizeof(mbuf));
      esc(d.product, pbuf, sizeof(pbuf));
      console_printf("%s{\"slot\":%u,\"connected\":%s,\"vid\":%u,\"pid\":%u,\"manufacturer\":\"%s\","
                     "\"product\":\"%s\",\"axis_count\":%u,\"button_count\":%u,\"has_hat\":%s,"
                     "\"has_keyboard\":%s}",
                     first ? "" : ",", d.slot, d.connected ? "true" : "false", d.vid, d.pid, mbuf,
                     pbuf, d.axis_count, d.button_count, d.has_hat ? "true" : "false",
                     d.has_keyboard ? "true" : "false");
      first = false;
    }
  }
  console_printf("]}\r\n");
}

// get_config is ~8 KB of JSON — far larger than any single console buffer — so it is STREAMED as
// many small console_printf() chunks (each well under the 256-byte sink buffer). The USART1 leg
// blocks-until-sent and the CDC leg drains with backpressure (see console_printf), so nothing is
// truncated. Each binding/gearOut entry is one chunk.
static void emit_binding(const InputBinding *b) {
  console_printf("{\"vid\":%u,\"pid\":%u,\"type\":\"%s\",\"index\":%u,\"threshold\":%u,\"rawMin\":%u,"
                 "\"rawMax\":%u,\"deadzoneLow\":%u,\"deadzoneHigh\":%u,\"invert\":%s}",
                 b->vid, b->pid, input_type_name(b->type), b->index, b->threshold, b->rawMin,
                 b->rawMax, b->deadzoneLow, b->deadzoneHigh, b->invert ? "true" : "false");
}

static void emit_channel(const ChannelBindings *cb) {
  console_printf("[");
  for (uint8_t i = 0; i < MAX_BINDINGS_PER_CHANNEL; i++) {
    if (i) console_printf(",");
    emit_binding(&cb->bindings[i]);
  }
  console_printf("]");
}

static void cmd_get_config(void) {
  const Config *c = mapping_config();
  console_printf("{\"version\":%u,\"pulseMs\":%u,\"gearMode\":\"%s\",\"max_bindings_per_channel\":%u",
                 c->version, c->pulseMs, gear_mode_name(c->gearMode), MAX_BINDINGS_PER_CHANNEL);

  console_printf(",\"gear\":{");
  for (uint8_t i = 0; i < NUM_GEAR_BINDINGS; i++) {
    ChannelId ch = (ChannelId)(CH_GEAR_R + i);
    console_printf("%s\"%s\":", i ? "," : "", mapping_channel_name(ch));
    emit_channel(&c->gear[i]);
  }
  console_printf("}");

  console_printf(",\"gearOut\":{");
  for (uint8_t i = 0; i < NUM_GEAR_OUTPUTS; i++) {
    ChannelId ch = (ChannelId)(CH_GEAR_R + i);
    console_printf("%s\"%s\":{\"x\":%u,\"y\":%u}", i ? "," : "", mapping_channel_name(ch),
                   c->gearOut[i].x, c->gearOut[i].y);
  }
  console_printf("}");

  console_printf(",\"shift_up\":");
  emit_channel(&c->shiftUp);
  console_printf(",\"shift_down\":");
  emit_channel(&c->shiftDown);
  console_printf(",\"handbrake\":");
  emit_channel(&c->handbrake);
  console_printf(",\"throttle\":");
  emit_channel(&c->throttle);
  console_printf(",\"brake\":");
  emit_channel(&c->brake);
  console_printf(",\"clutch\":");
  emit_channel(&c->clutch);
  console_printf("}\r\n");
}

static void cmd_set_binding(const json_kv *kv, int n) {
  char ch_name[16];
  if (!json_get_str(kv, n, "channel", ch_name, sizeof(ch_name))) {
    send_err("missing_channel");
    return;
  }
  ChannelId ch   = mapping_channel_by_name(ch_name);
  long      slot = 0;
  json_get_int(kv, n, "slot", &slot); // optional, defaults to 0
  if (slot < 0 || slot >= MAX_BINDINGS_PER_CHANNEL) {
    send_err("invalid_slot");
    return;
  }
  InputBinding *b = mapping_binding_slot(ch, (uint8_t)slot);
  if (!b) {
    send_err("unknown_channel_or_slot");
    return;
  }
  const json_kv *bind = json_find(kv, n, "binding");
  if (!bind || bind->type != JSON_OBJ) {
    send_err("missing_binding");
    return;
  }
  json_kv bkv[MAX_BIND_KV];
  int     nb = json_parse_object(bind->val, bind->vallen, bkv, MAX_BIND_KV);
  if (nb < 0) {
    send_err("bad_binding");
    return;
  }
  if (nb > MAX_BIND_KV) nb = MAX_BIND_KV;

  apply_u16(bkv, nb, "vid", &b->vid);
  apply_u16(bkv, nb, "pid", &b->pid);
  char type_name[12];
  if (json_get_str(bkv, nb, "type", type_name, sizeof(type_name)))
    b->type = input_type_from_name(type_name); // "none" -> INPUT_NONE clears the slot
  long idx;
  if (json_get_int(bkv, nb, "index", &idx) && idx >= 0 && idx <= 255) b->index = (uint8_t)idx;
  apply_u16(bkv, nb, "threshold", &b->threshold);
  apply_u16(bkv, nb, "rawMin", &b->rawMin);
  apply_u16(bkv, nb, "rawMax", &b->rawMax);
  apply_u16(bkv, nb, "deadzoneLow", &b->deadzoneLow);
  apply_u16(bkv, nb, "deadzoneHigh", &b->deadzoneHigh);
  int inv = json_get_bool(bkv, nb, "invert");
  if (inv >= 0) b->invert = inv ? 1 : 0;

  mapping_recompute_crc();
  send_ok();
}

static void cmd_set_gear_dac(const json_kv *kv, int n) {
  char ch_name[16];
  if (!json_get_str(kv, n, "channel", ch_name, sizeof(ch_name))) {
    send_err("missing_channel");
    return;
  }
  GearOutputCalibration *g = mapping_gearout_for(mapping_channel_by_name(ch_name));
  if (!g) {
    send_err("not_a_gear_channel");
    return;
  }
  apply_u16(kv, n, "x", &g->x);
  apply_u16(kv, n, "y", &g->y);
  mapping_recompute_crc();
  send_ok();
}

static void cmd_set_pulse_ms(const json_kv *kv, int n) {
  long v;
  if (!json_get_int(kv, n, "value", &v)) {
    send_err("missing_value");
    return;
  }
  mapping_config_mutable()->pulseMs = (uint16_t)v;
  mapping_recompute_crc();
  send_ok();
}

static void cmd_set_gear_mode(const json_kv *kv, int n) {
  char v[12];
  if (!json_get_str(kv, n, "value", v, sizeof(v))) {
    send_err("missing_value");
    return;
  }
  mapping_config_mutable()->gearMode = gear_mode_from_name(v);
  mapping_recompute_crc();
  send_ok();
}

static void cmd_save_config(void) {
  if (mapping_save()) send_ok();
  else send_err("flash_save_failed");
}

static void cmd_reset_config(void) {
  mapping_reset();
  send_ok();
}

static void cmd_live_inputs(const json_kv *kv, int n) {
  int on = json_get_bool(kv, n, "on");
  if (on >= 0) g_live_inputs = (on == 1);
  send_ok();
}

static void cmd_live_outputs(const json_kv *kv, int n) {
  int on = json_get_bool(kv, n, "on");
  if (on >= 0) g_live_outputs = (on == 1);
  send_ok();
}

static void cmd_test_gear(const json_kv *kv, int n) {
  char ch_name[16];
  if (!json_get_str(kv, n, "channel", ch_name, sizeof(ch_name))) {
    send_err("missing_channel");
    return;
  }
  ChannelId ch = mapping_channel_by_name(ch_name);
  if (ch < CH_GEAR_R || ch > CH_GEAR_N) {
    send_err("not_a_gear_channel");
    return;
  }
  mapping_test_gear(ch);
  send_ok();
}

// --- direct output control --------------------------------------------------------------------
// The PC (SimHub / the web app) computes its own mapping and commands the adapter's outputs. These
// override the USB-device mapping for the channels they touch (sticky until release_outputs). set_gear
// + set_outputs are the live drive path (set_outputs is the 100 Hz pedal/handbrake hot path); the PC
// streams them fire-and-forget — the ok reply can be ignored.
static void cmd_set_gear(const json_kv *kv, int n) {
  char gear[16];
  if (!json_get_str(kv, n, "gear", gear, sizeof(gear))) {
    send_err("missing_gear");
    return;
  }
  ChannelId ch = mapping_channel_by_name(gear);
  if (ch < CH_GEAR_R || ch > CH_GEAR_N) {
    send_err("not_a_gear");
    return;
  }
  mapping_set_gear_override(ch);
  send_ok();
}

static void cmd_set_outputs(const json_kv *kv, int n) {
  long v;
  if (json_get_int(kv, n, "throttle", &v)) mapping_set_axis_override(CH_THROTTLE, (uint16_t)v);
  if (json_get_int(kv, n, "brake", &v)) mapping_set_axis_override(CH_BRAKE, (uint16_t)v);
  if (json_get_int(kv, n, "clutch", &v)) mapping_set_axis_override(CH_CLUTCH, (uint16_t)v);
  if (json_get_int(kv, n, "handbrake", &v)) mapping_set_axis_override(CH_HANDBRAKE, (uint16_t)v);
  send_ok();
}

static void cmd_pulse_shift(const json_kv *kv, int n) {
  char dir[8];
  if (!json_get_str(kv, n, "direction", dir, sizeof(dir))) {
    send_err("missing_direction");
    return;
  }
  if (!strcmp(dir, "up")) mapping_pulse_shift(true);
  else if (!strcmp(dir, "down")) mapping_pulse_shift(false);
  else {
    send_err("invalid_direction");
    return;
  }
  send_ok();
}

static void cmd_release_outputs(void) {
  mapping_release_outputs();
  send_ok();
}

// ---------------- events ------------------------------------------------------------------------
static void emit_attached(const InputDeviceInfo *d) {
  char mbuf[DEVICE_STR_LEN * 2], pbuf[DEVICE_STR_LEN * 2];
  esc(d->manufacturer, mbuf, sizeof(mbuf));
  esc(d->product, pbuf, sizeof(pbuf));
  console_printf(
      "{\"event\":\"device_attached\",\"slot\":%u,\"vid\":%u,\"pid\":%u,\"manufacturer\":\"%s\","
      "\"product\":\"%s\",\"axis_count\":%u,\"button_count\":%u,\"has_hat\":%s,\"has_keyboard\":%s}"
      "\r\n",
      d->slot, d->vid, d->pid, mbuf, pbuf, d->axis_count, d->button_count,
      d->has_hat ? "true" : "false", d->has_keyboard ? "true" : "false");
}

static void emit_detached(uint8_t slot) {
  console_printf("{\"event\":\"device_detached\",\"slot\":%u}\r\n", slot);
}

static void emit_outputs(void) {
  const OutputSnapshot *o = mapping_outputs();
  console_printf("{\"event\":\"outputs\",\"gear\":\"%s\",\"shift_up\":%s,\"shift_down\":%s,"
                 "\"throttle\":%u,\"brake\":%u,\"clutch\":%u,\"handbrake\":%u}\r\n",
                 mapping_channel_name(o->gear), o->shiftUp ? "true" : "false",
                 o->shiftDown ? "true" : "false", o->throttle, o->brake, o->clutch, o->handbrake);
}

static void emit_live(const InputLive *lv) {
  console_printf("{\"event\":\"live\",\"slot\":%u,\"buttons\":%lu,\"axes\":[", lv->slot,
                 (unsigned long)lv->buttons);
  for (uint8_t a = 0; a < lv->axis_count && a < INPUT_MAX_AXES; a++)
    console_printf("%s%u", a ? "," : "", lv->axes[a]);
  console_printf("]");
  if (lv->has_hat)
    console_printf(",\"hat\":%d", (lv->hat == INPUT_HAT_RELEASED) ? -1 : (int)lv->hat);
  if (lv->has_keyboard) {
    console_printf(",\"keys\":[");
    for (uint8_t k = 0; k < INPUT_MAX_KEYS; k++) console_printf("%s%u", k ? "," : "", lv->keys[k]);
    console_printf("]");
  }
  console_printf("}\r\n");
}

// ---------------- dispatch ----------------------------------------------------------------------
void protocol_handle_line(const char *line) {
  if (line[0] != '{') return; // non-JSON lines are the bring-up CLI (handled in main.c)
  json_kv kv[MAX_TOP_KV];
  int     n = json_parse_object(line, strlen(line), kv, MAX_TOP_KV);
  if (n < 0) {
    send_err("bad_json");
    return;
  }
  if (n > MAX_TOP_KV) n = MAX_TOP_KV;

  char cmd[24];
  if (!json_get_str(kv, n, "cmd", cmd, sizeof(cmd))) {
    send_err("missing_cmd");
    return;
  }

  if (!strcmp(cmd, "version")) cmd_version();
  else if (!strcmp(cmd, "list_devices")) cmd_list_devices();
  else if (!strcmp(cmd, "get_config")) cmd_get_config();
  else if (!strcmp(cmd, "set_binding")) cmd_set_binding(kv, n);
  else if (!strcmp(cmd, "set_gear_dac")) cmd_set_gear_dac(kv, n);
  else if (!strcmp(cmd, "set_pulse_ms")) cmd_set_pulse_ms(kv, n);
  else if (!strcmp(cmd, "set_gear_mode")) cmd_set_gear_mode(kv, n);
  else if (!strcmp(cmd, "save_config")) cmd_save_config();
  else if (!strcmp(cmd, "reset_config")) cmd_reset_config();
  else if (!strcmp(cmd, "live_inputs")) cmd_live_inputs(kv, n);
  else if (!strcmp(cmd, "live_outputs")) cmd_live_outputs(kv, n);
  else if (!strcmp(cmd, "test_gear")) cmd_test_gear(kv, n);
  else if (!strcmp(cmd, "set_gear")) cmd_set_gear(kv, n);
  else if (!strcmp(cmd, "set_outputs")) cmd_set_outputs(kv, n);
  else if (!strcmp(cmd, "pulse_shift")) cmd_pulse_shift(kv, n);
  else if (!strcmp(cmd, "release_outputs")) cmd_release_outputs();
  else send_err("unknown_cmd");
}

void protocol_init(void) {
  g_live_inputs  = false;
  g_live_outputs = false;
  memset(g_shadow_connected, 0, sizeof(g_shadow_connected));
  memset(g_shadow_change, 0, sizeof(g_shadow_change));
}

void protocol_tick(uint32_t now_ms) {
  // Attach/detach: diff connected state per global slot. Shadows always track; emit only when streaming.
  for (uint8_t si = 0; si < input_source_count(); si++) {
    const InputSource *src = input_source_get(si);
    uint8_t            cnt = src->device_count();
    for (uint8_t i = 0; i < cnt; i++) {
      InputDeviceInfo d;
      if (!src->device_info(i, &d) || d.slot >= MAX_GLOBAL_SLOTS) continue;
      if (d.connected != g_shadow_connected[d.slot]) {
        g_shadow_connected[d.slot] = d.connected;
        if (g_live_inputs) {
          if (d.connected) emit_attached(&d);
          else emit_detached(d.slot);
        }
      }
    }
  }

  // Live outputs — current output snapshot, rate-limited.
  if (g_live_outputs && (uint32_t)(now_ms - g_last_outputs_ms) >= OUTPUTS_PERIOD_MS) {
    g_last_outputs_ms = now_ms;
    emit_outputs();
  }

  if (!g_live_inputs || (uint32_t)(now_ms - g_last_live_ms) < LIVE_PERIOD_MS) return;
  g_last_live_ms = now_ms;
  for (uint8_t si = 0; si < input_source_count(); si++) {
    const InputSource *src = input_source_get(si);
    uint8_t            cnt = src->device_count();
    for (uint8_t i = 0; i < cnt; i++) {
      InputLive lv;
      if (!src->device_live(i, &lv) || !lv.connected || lv.slot >= MAX_GLOBAL_SLOTS) continue;
      if (lv.change_seq != g_shadow_change[lv.slot]) {
        g_shadow_change[lv.slot] = lv.change_seq;
        emit_live(&lv);
      }
    }
  }
}
