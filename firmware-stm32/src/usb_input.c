// USB HID device pool + InputSource impl. See usb_input.h / input_source.h / hid_parse.h.
#include "usb_input.h"

#include <string.h>

#include "hid_parse.h"
#include "input_source.h"
#include "stm32h7xx_hal.h" // HAL_GetTick for the per-slot report age (usb_status diagnostics)
#include "tusb.h"

#define LANGUAGE_ID 0x0409

extern int console_printf(const char *fmt, ...);

typedef struct {
  bool     in_use;
  uint8_t  daddr;
  uint8_t  instance;
  uint16_t vid, pid;
  char     manufacturer[DEVICE_STR_LEN];
  char     product[DEVICE_STR_LEN];
  uint8_t  itf_protocol; // HID_ITF_PROTOCOL_{NONE,KEYBOARD,MOUSE}
  hid_layout_t layout;
  hid_state_t  state;
  uint8_t  axis_count, button_count;
  bool     has_hat, has_keyboard;
  uint32_t change_seq;
  // Diagnostics for the field "device goes silent while still enumerated" bug — see usb_status.
  uint32_t report_count;     // total reports received since claim (changed or not)
  uint32_t last_report_tick; // HAL tick of the last report (claim time until the first one)
  uint32_t idle_rearms;      // times usb_input_task() found the pipe idle and re-armed it
} pool_slot_t;

static pool_slot_t g_pool[USB_POOL_SIZE];
static uint32_t    g_change_seq; // global: any mount/umount/report change

// --- string descriptor (UTF-16LE) -> printable ASCII, right-trimmed -------------------------------
static void copy_string(uint8_t daddr, bool product, char *out, size_t outlen) {
  out[0] = '\0';
  static uint16_t buf[64];
  uint8_t         res =
      product ? tuh_descriptor_get_product_string_sync(daddr, LANGUAGE_ID, buf, sizeof(buf))
              : tuh_descriptor_get_manufacturer_string_sync(daddr, LANGUAGE_ID, buf, sizeof(buf));
  if (res != XFER_RESULT_SUCCESS) return;
  size_t total = buf[0] & 0xFFu;             // bLength in bytes (incl. 2-byte header)
  size_t count = (total >= 2) ? (total - 2) / 2 : 0;
  size_t o     = 0;
  for (size_t i = 0; i < count && o < outlen - 1; i++) {
    uint16_t c = buf[1 + i];
    out[o++]   = (c >= 0x20 && c < 0x7F) ? (char)c : '?';
  }
  while (o > 0 && out[o - 1] == ' ') o--; // right-trim
  out[o] = '\0';
}

static pool_slot_t *find_slot(uint8_t daddr, uint8_t instance) {
  for (uint8_t i = 0; i < USB_POOL_SIZE; i++)
    if (g_pool[i].in_use && g_pool[i].daddr == daddr && g_pool[i].instance == instance)
      return &g_pool[i];
  return 0;
}

bool usb_input_on_mount(uint8_t daddr, uint8_t instance, const uint8_t *report_desc, uint16_t len) {
  pool_slot_t *s = 0;
  for (uint8_t i = 0; i < USB_POOL_SIZE; i++)
    if (!g_pool[i].in_use) { s = &g_pool[i]; break; }
  if (!s) return false; // pool full

  memset(s, 0, sizeof(*s));
  s->in_use   = true;
  s->daddr    = daddr;
  s->instance = instance;
  s->state.hat = INPUT_HAT_RELEASED;
  tuh_vid_pid_get(daddr, &s->vid, &s->pid);
  s->itf_protocol = tuh_hid_interface_protocol(daddr, instance);
  copy_string(daddr, false, s->manufacturer, sizeof(s->manufacturer));
  copy_string(daddr, true, s->product, sizeof(s->product));

  if (s->itf_protocol == HID_ITF_PROTOCOL_KEYBOARD) {
    s->has_keyboard = true; // boot keyboard — decoded by fixed layout, no descriptor parse
  } else if (report_desc && len) {
    hid_parse_descriptor(report_desc, len, &s->layout);
    hid_layout_summary(&s->layout, &s->axis_count, &s->button_count, &s->has_hat, &s->has_keyboard);
  }

  // Drop interfaces that expose nothing bindable. Composite controllers commonly present a second
  // HID interface with no axes/buttons/hat/keys (the Logitech RS Shifter & Handbrake and RS
  // H-Shifter each do), which showed up as phantom duplicate devices in list_devices — 4 physical
  // devices filling 7 of the 8 slots. These counts come straight from the report descriptor and are
  // never revised from observed reports, so such an interface can never become bindable; keeping it
  // only burns a pool slot. Release it and leave the slot for a real device.
  if (!s->axis_count && !s->button_count && !s->has_hat && !s->has_keyboard) {
    s->in_use = false;
    return false;
  }

  s->last_report_tick = HAL_GetTick(); // age counts from claim until the first report lands

  g_change_seq++;
  s->change_seq++;
  return true;
}

void usb_input_on_umount(uint8_t daddr, uint8_t instance) {
  pool_slot_t *s = find_slot(daddr, instance);
  if (!s) return;
  s->in_use = false;
  g_change_seq++;
}

void usb_input_on_report(uint8_t daddr, uint8_t instance, const uint8_t *report, uint16_t len) {
  pool_slot_t *s = find_slot(daddr, instance);
  if (!s) return;
  s->report_count++;
  s->last_report_tick = HAL_GetTick();
  bool changed = (s->itf_protocol == HID_ITF_PROTOCOL_KEYBOARD)
                     ? hid_decode_boot_keyboard(report, len, &s->state)
                     : hid_decode_report(&s->layout, report, len, &s->state);
  if (changed) {
    s->change_seq++;
    g_change_seq++;
  }
}

// --- InputSource vtable ---------------------------------------------------------------------------
static bool src_has_device(uint16_t vid, uint16_t pid) {
  for (uint8_t i = 0; i < USB_POOL_SIZE; i++)
    if (g_pool[i].in_use && g_pool[i].vid == vid && g_pool[i].pid == pid) return true;
  return false;
}

static uint32_t src_buttons(uint16_t vid, uint16_t pid) {
  uint32_t bits = 0;
  for (uint8_t i = 0; i < USB_POOL_SIZE; i++)
    if (g_pool[i].in_use && g_pool[i].vid == vid && g_pool[i].pid == pid)
      bits |= g_pool[i].state.buttons;
  return bits;
}

static uint16_t src_axis(uint16_t vid, uint16_t pid, uint8_t index, bool *found) {
  uint16_t best = 0;
  bool     any  = false;
  if (index < INPUT_MAX_AXES)
    for (uint8_t i = 0; i < USB_POOL_SIZE; i++)
      if (g_pool[i].in_use && g_pool[i].vid == vid && g_pool[i].pid == pid) {
        uint16_t v = g_pool[i].state.axes[index];
        if (!any || v > best) { best = v; any = true; }
      }
  if (found) *found = any;
  return best;
}

static bool src_hat_is(uint16_t vid, uint16_t pid, uint8_t dir) {
  for (uint8_t i = 0; i < USB_POOL_SIZE; i++)
    if (g_pool[i].in_use && g_pool[i].vid == vid && g_pool[i].pid == pid && g_pool[i].state.has_hat &&
        g_pool[i].state.hat == dir)
      return true;
  return false;
}

static bool src_key_pressed(uint16_t vid, uint16_t pid, uint8_t scancode) {
  for (uint8_t i = 0; i < USB_POOL_SIZE; i++)
    if (g_pool[i].in_use && g_pool[i].vid == vid && g_pool[i].pid == pid)
      for (uint8_t k = 0; k < INPUT_MAX_KEYS; k++)
        if (g_pool[i].state.keys[k] == scancode && scancode != 0) return true;
  return false;
}

static uint8_t src_device_count(void) { return USB_POOL_SIZE; }

static bool src_device_info(uint8_t i, InputDeviceInfo *out) {
  if (i >= USB_POOL_SIZE) return false;
  pool_slot_t *s = &g_pool[i];
  memset(out, 0, sizeof(*out));
  out->slot      = i; // USB occupies global slots 0..7
  out->connected = s->in_use;
  if (!s->in_use) return true;
  out->vid          = s->vid;
  out->pid          = s->pid;
  out->axis_count   = s->axis_count;
  out->button_count = s->button_count;
  out->has_hat      = s->has_hat;
  out->has_keyboard = s->has_keyboard;
  strncpy(out->manufacturer, s->manufacturer, DEVICE_STR_LEN - 1);
  strncpy(out->product, s->product, DEVICE_STR_LEN - 1);
  return true;
}

static bool src_device_live(uint8_t i, InputLive *out) {
  if (i >= USB_POOL_SIZE) return false;
  pool_slot_t *s = &g_pool[i];
  memset(out, 0, sizeof(*out));
  out->slot      = i; // USB occupies global slots 0..7
  out->connected = s->in_use;
  if (!s->in_use) return true;
  out->buttons      = s->state.buttons;
  out->axis_count   = s->axis_count;
  for (uint8_t a = 0; a < INPUT_MAX_AXES; a++) out->axes[a] = s->state.axes[a];
  out->hat          = s->state.hat;
  out->has_hat      = s->has_hat;
  out->has_keyboard = s->has_keyboard;
  for (uint8_t k = 0; k < INPUT_MAX_KEYS; k++) out->keys[k] = s->state.keys[k];
  out->change_seq = s->change_seq;
  return true;
}

static uint32_t src_change_seq(void) { return g_change_seq; }

static const InputSource s_usb_source = {
    .name        = "usb",
    .has_device  = src_has_device,
    .buttons     = src_buttons,
    .axis        = src_axis,
    .hat_is      = src_hat_is,
    .key_pressed = src_key_pressed,
    .device_count = src_device_count,
    .device_info  = src_device_info,
    .device_live  = src_device_live,
    .change_seq   = src_change_seq,
};

void usb_input_init(void) { input_source_register(&s_usb_source); }

// --- interrupt-pipe watchdog ----------------------------------------------------------------------
// See usb_input.h. Field symptom this exists for: a device on the hub goes silent while still
// listed as connected, freezing its last values (a pedal caught mid-press stays at 100% and the
// wheelbase keeps receiving that), and only a reboot recovers it.
//
// Two distinct failures, and the second is the one that actually bites:
//
//  1. IDLE pipe — the re-arm in tuh_hid_report_received_cb failed and nothing rescheduled it.
//     Recovery is just tuh_hid_receive_report().
//  2. BUSY pipe — a transfer is submitted but never completes: the host channel is wedged. The
//     first version of this watchdog skipped exactly this case (it only re-armed idle pipes) and
//     never fired in the field, which is how we learned the wedge is the real mode. Recovery
//     needs tuh_hid_receive_abort() to tear the dead transfer down and release the endpoint,
//     then a fresh arm.
//
// SCOPE, learned the hard way — read before making this cleverer:
//
// An earlier revision escalated to tuh_hid_receive_abort() whenever a claimed slot went quiet, on
// the theory that a wedged host channel was silencing the pedals. On the bench it looked like a
// clean recovery. On the rig it was actively harmful, for two reasons worth keeping written down:
//
//   * A quiet device is INDISTINGUISHABLE from a wedged one here. A device with nothing to report
//     NAKs, and a NAK-looping endpoint reads exactly as busy as a dead one. So the escalation
//     fired constantly on the untouched shifters — and aborting a live transfer can leave a
//     partial report in the endpoint buffer, which then decodes as garbage axis values.
//   * console_printf() BLOCKS until the sink drains, and this runs in the main loop ahead of
//     pedals_update(). Logging each episode stalled the loop badly enough that the 100 Hz pedal
//     cadence collapsed — the wheelbase fell back to analog and gear changes arrived a MINUTE
//     late. Exactly the failure the AGENTS.md loop-order warning describes.
//
// So this is deliberately minimal and SILENT: re-arm a pipe that is genuinely idle (the arm was
// dropped and nothing else will ever reschedule it — cheap, and it cannot fire on a healthy
// device, which always has a transfer outstanding). Nothing here may block, and nothing here may
// touch a transfer that is still in flight. The "device goes silent while still enumerated" bug is
// NOT fixed by this and remains open; see PORT-STATUS.
void usb_input_task(uint32_t now_ms) {
  static uint32_t s_last_check;
  if (now_ms - s_last_check < 250u) return;
  s_last_check = now_ms;

  for (uint8_t i = 0; i < USB_POOL_SIZE; i++) {
    pool_slot_t *s = &g_pool[i];
    if (!s->in_use) continue;
    if (!tuh_hid_mounted(s->daddr, s->instance)) continue;
    // Idle = no transfer outstanding = the arm was dropped. A healthy device never looks like
    // this, so this cannot false-positive; anything busy is left strictly alone.
    if (!tuh_hid_receive_ready(s->daddr, s->instance)) continue;
    s->idle_rearms++;
    tuh_hid_receive_report(s->daddr, s->instance);
  }
}

// --- manual diagnostics (usb_status / usb_kick JSON commands, protocol.c) -------------------------
// The freeze is rare (minutes to an hour into a session) and unreproducible on demand, so these
// exist to interrogate it IN THE ACT instead of guessing: usb_status says whether the frozen
// slot's pipe is armed and when it last delivered; usb_kick then distinguishes the two remaining
// theories. If a kick revives the device, the wedge was host-side (a dwc2 channel stuck busy). If
// the re-armed pipe stays silent while the user works the control, the device itself stopped
// talking and only a port-level reset can bring it back.

bool usb_input_diag(uint8_t slot, UsbSlotDiag *out) {
  if (slot >= USB_POOL_SIZE) return false;
  pool_slot_t *s = &g_pool[slot];
  memset(out, 0, sizeof(*out));
  out->in_use = s->in_use;
  if (!s->in_use) return true;
  out->vid         = s->vid;
  out->pid         = s->pid;
  out->daddr       = s->daddr;
  out->mounted     = tuh_hid_mounted(s->daddr, s->instance);
  out->busy        = !tuh_hid_receive_ready(s->daddr, s->instance);
  out->reports     = s->report_count;
  out->age_ms      = HAL_GetTick() - s->last_report_tick;
  out->idle_rearms = s->idle_rearms;
  return true;
}

uint32_t usb_input_kick(void) {
  uint32_t aborted = 0;
  for (uint8_t i = 0; i < USB_POOL_SIZE; i++) {
    pool_slot_t *s = &g_pool[i];
    if (!s->in_use || !tuh_hid_mounted(s->daddr, s->instance)) continue;
    if (!tuh_hid_receive_ready(s->daddr, s->instance)) {
      // Aborting a live transfer can leave a partial report behind that decodes as one frame of
      // garbage — acceptable for a one-shot user-initiated probe, and exactly why this must never
      // run automatically (see the scope comment above usb_input_task).
      tuh_hid_receive_abort(s->daddr, s->instance);
      aborted |= (1u << i);
    }
    tuh_hid_receive_report(s->daddr, s->instance);
  }
  return aborted;
}
