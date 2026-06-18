// Line-based JSON protocol (M1 subset). See protocol.h.
#include "protocol.h"

#include <stdio.h>
#include <string.h>

#include "input_source.h"

extern int console_printf(const char *fmt, ...);

#define PROTO_VERSION            2
#define MAX_BINDINGS_PER_CHANNEL 4
#define FW_NAME                  "fanadapter-stm32"
#define FW_VER                   "0.6.0"
#define LIVE_PERIOD_MS           33u // ~30 Hz; the console is the bottleneck (USART1 @115200 8E1)
#define MAX_GLOBAL_SLOTS         16u

static bool     g_live_inputs;
static bool     g_shadow_connected[MAX_GLOBAL_SLOTS];
static uint32_t g_shadow_change[MAX_GLOBAL_SLOTS];
static uint32_t g_last_live_ms;

// --- minimal JSON value extraction (M1 only needs "cmd" string + "on" bool) -----------------------
static const char *find_value(const char *s, const char *key) {
  char pat[24];
  snprintf(pat, sizeof(pat), "\"%s\"", key);
  const char *p = strstr(s, pat);
  if (!p) return 0;
  p = strchr(p + strlen(pat), ':');
  if (!p) return 0;
  p++;
  while (*p == ' ') p++;
  return p;
}
static bool json_str(const char *s, const char *key, char *out, size_t outlen) {
  const char *p = find_value(s, key);
  if (!p || *p != '"') return false;
  p++;
  size_t o = 0;
  while (*p && *p != '"' && o < outlen - 1) out[o++] = *p++;
  out[o] = '\0';
  return true;
}
static int json_bool(const char *s, const char *key) {
  const char *p = find_value(s, key);
  if (!p) return -1;
  if (!strncmp(p, "true", 4)) return 1;
  if (!strncmp(p, "false", 5)) return 0;
  return -1;
}

// Escape a sanitized string for embedding in JSON (copy_string already stripped non-printables).
static void esc(const char *in, char *out, size_t outlen) {
  size_t o = 0;
  for (const char *p = in; *p && o < outlen - 2; p++) {
    if (*p == '"' || *p == '\\') out[o++] = '\\';
    out[o++] = *p;
  }
  out[o] = '\0';
}

// --- command responses (non-event lines; webconfig matches them FIFO to the request) --------------
static void cmd_version(void) {
  console_printf("{\"fw\":\"%s\",\"ver\":\"%s\",\"protocol\":%u,\"max_bindings_per_channel\":%u}\r\n",
                 FW_NAME, FW_VER, PROTO_VERSION, MAX_BINDINGS_PER_CHANNEL);
}

static void cmd_list_devices(void) {
  console_printf("{\"devices\":[");
  bool first = true;
  for (uint8_t si = 0; si < input_source_count(); si++) {
    const InputSource *src = input_source_get(si);
    uint8_t            n   = src->device_count();
    for (uint8_t i = 0; i < n; i++) {
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

// --- events ---------------------------------------------------------------------------------------
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

// --- dispatch -------------------------------------------------------------------------------------
void protocol_handle_line(const char *line) {
  if (line[0] != '{') return; // non-JSON lines are the bring-up CLI (handled in main.c)
  char cmd[24];
  if (!json_str(line, "cmd", cmd, sizeof(cmd))) {
    console_printf("{\"error\":\"no cmd\"}\r\n");
    return;
  }
  if (!strcmp(cmd, "version")) {
    cmd_version();
  } else if (!strcmp(cmd, "list_devices")) {
    cmd_list_devices();
  } else if (!strcmp(cmd, "live_inputs")) {
    int on = json_bool(line, "on");
    if (on >= 0) g_live_inputs = (on == 1);
    console_printf("{\"ok\":true}\r\n");
  } else {
    console_printf("{\"error\":\"unknown cmd\"}\r\n");
  }
}

void protocol_init(void) {
  g_live_inputs = false;
  memset(g_shadow_connected, 0, sizeof(g_shadow_connected));
  memset(g_shadow_change, 0, sizeof(g_shadow_change));
}

void protocol_tick(uint32_t now_ms) {
  // Attach/detach: diff connected state per global slot. Shadows always track; emit only if streaming.
  for (uint8_t si = 0; si < input_source_count(); si++) {
    const InputSource *src = input_source_get(si);
    uint8_t            n   = src->device_count();
    for (uint8_t i = 0; i < n; i++) {
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

  if (!g_live_inputs || (uint32_t)(now_ms - g_last_live_ms) < LIVE_PERIOD_MS) return;
  g_last_live_ms = now_ms;
  for (uint8_t si = 0; si < input_source_count(); si++) {
    const InputSource *src = input_source_get(si);
    uint8_t            n   = src->device_count();
    for (uint8_t i = 0; i < n; i++) {
      InputLive lv;
      if (!src->device_live(i, &lv) || !lv.connected || lv.slot >= MAX_GLOBAL_SLOTS) continue;
      if (lv.change_seq != g_shadow_change[lv.slot]) {
        g_shadow_change[lv.slot] = lv.change_seq;
        emit_live(&lv);
      }
    }
  }
}
