// Generic HID report-descriptor walker + report decoder. See hid_parse.h.
#include "hid_parse.h"
#include <string.h>

// ---- HID item prefix: bits[1:0]=bSize(0,1,2,4) bits[3:2]=bType(0 Main,1 Global,2 Local) bits[7:4]=bTag
#define BT_MAIN 0u
#define BT_GLOBAL 1u
#define BT_LOCAL 2u
// Global tags
#define G_USAGE_PAGE 0x0u
#define G_LOGICAL_MIN 0x1u
#define G_LOGICAL_MAX 0x2u
#define G_REPORT_SIZE 0x7u
#define G_REPORT_ID 0x8u
#define G_REPORT_COUNT 0x9u
#define G_PUSH 0xAu
#define G_POP 0xBu
// Local tags
#define L_USAGE 0x0u
#define L_USAGE_MIN 0x1u
#define L_USAGE_MAX 0x2u
// Main tags
#define M_INPUT 0x8u
#define M_OUTPUT 0x9u
#define M_FEATURE 0xBu
#define M_COLLECTION 0xAu
#define M_END_COLLECTION 0xCu
// Input flags
#define IN_CONSTANT (1u << 0)
#define IN_VARIABLE (1u << 1)
// Mappable usage pages
#define UP_DESKTOP 0x01u
#define UP_KEYBOARD 0x07u
#define UP_BUTTON 0x09u

#define MAX_LOCAL_USAGES 16u
#define MAX_REPORT_IDS 16u
#define GLOBAL_STACK_DEPTH 4u

typedef struct {
  uint16_t usage_page;
  int32_t  logical_min, logical_max;
  uint8_t  report_size, report_count, report_id;
} global_state_t;

typedef struct {
  uint16_t usages[MAX_LOCAL_USAGES];
  uint8_t  usage_count;
  uint16_t usage_min, usage_max;
  bool     has_range;
} local_state_t;

bool hid_parse_descriptor(const uint8_t *desc, uint16_t len, hid_layout_t *out) {
  memset(out, 0, sizeof(*out));
  if (!desc || len == 0) return false;

  global_state_t g = {0};
  global_state_t gstack[GLOBAL_STACK_DEPTH];
  uint8_t        gsp = 0;
  local_state_t  l = {0};

  uint8_t  rid_id[MAX_REPORT_IDS]  = {0};
  uint16_t rid_cur[MAX_REPORT_IDS] = {0};
  uint8_t  n_rid                   = 0;

  uint16_t i = 0;
  while (i < len) {
    uint8_t b = desc[i++];
    if (b == 0xFEu) { // long item — skip (data size in next byte)
      if (i >= len) break;
      uint8_t dsize = desc[i++];
      i = (uint16_t)(i + 1u + dsize); // bTag byte + data bytes
      continue;
    }
    uint8_t bSize = (uint8_t)(b & 0x03u);
    if (bSize == 3u) bSize = 4u;
    uint8_t  bType = (uint8_t)((b >> 2) & 0x03u);
    uint8_t  bTag  = (uint8_t)((b >> 4) & 0x0Fu);
    uint32_t data  = 0;
    for (uint8_t k = 0; k < bSize && i < len; k++) data |= (uint32_t)desc[i++] << (8u * k);
    int32_t sdata = (int32_t)data; // sign-extended view for logical/physical
    if (bSize == 1u && (data & 0x80u)) sdata = (int32_t)(data | 0xFFFFFF00u);
    else if (bSize == 2u && (data & 0x8000u)) sdata = (int32_t)(data | 0xFFFF0000u);

    if (bType == BT_GLOBAL) {
      switch (bTag) {
        case G_USAGE_PAGE: g.usage_page = (uint16_t)data; break;
        case G_LOGICAL_MIN: g.logical_min = sdata; break;
        case G_LOGICAL_MAX: g.logical_max = sdata; break;
        case G_REPORT_SIZE: g.report_size = (uint8_t)data; break;
        case G_REPORT_COUNT: g.report_count = (uint8_t)data; break;
        case G_REPORT_ID:
          g.report_id       = (uint8_t)data;
          out->uses_report_id = true;
          break;
        case G_PUSH: if (gsp < GLOBAL_STACK_DEPTH) gstack[gsp++] = g; break;
        case G_POP: if (gsp > 0) g = gstack[--gsp]; break;
        default: break;
      }
    } else if (bType == BT_LOCAL) {
      switch (bTag) {
        case L_USAGE:
          if (l.usage_count < MAX_LOCAL_USAGES) l.usages[l.usage_count++] = (uint16_t)data;
          break;
        case L_USAGE_MIN: l.usage_min = (uint16_t)data; l.has_range = true; break;
        case L_USAGE_MAX: l.usage_max = (uint16_t)data; l.has_range = true; break;
        default: break;
      }
    } else if (bType == BT_MAIN) {
      if (bTag == M_INPUT) {
        // Locate (or create) this report id's INPUT bit cursor. OUTPUT/FEATURE live in separate
        // report spaces, so only INPUT advances it.
        uint16_t *cur = 0;
        for (uint8_t c = 0; c < n_rid; c++)
          if (rid_id[c] == g.report_id) { cur = &rid_cur[c]; break; }
        if (!cur && n_rid < MAX_REPORT_IDS) {
          rid_id[n_rid] = g.report_id;
          rid_cur[n_rid] = 0;
          cur            = &rid_cur[n_rid];
          n_rid++;
        }
        uint16_t base = cur ? *cur : 0;

        bool     constant = (data & IN_CONSTANT) != 0;
        bool     variable = (data & IN_VARIABLE) != 0;
        uint16_t page     = g.usage_page;
        bool mappable = (page == UP_DESKTOP || page == UP_KEYBOARD || page == UP_BUTTON);

        if (!constant && mappable && g.report_size > 0 && g.report_count > 0) {
          if (variable) {
            for (uint8_t e = 0; e < g.report_count; e++) {
              uint16_t usage;
              if (l.usage_count > 0)
                usage = l.usages[e < l.usage_count ? e : (uint8_t)(l.usage_count - 1)];
              else if (l.has_range)
                usage = (uint16_t)(l.usage_min + e);
              else
                usage = 0;
              if (l.has_range && usage > l.usage_max) usage = l.usage_max;
              if (out->field_count < HID_MAX_FIELDS) {
                hid_field_t *f  = &out->fields[out->field_count++];
                f->report_id    = g.report_id;
                f->usage_page   = page;
                f->usage        = usage;
                f->usage_max    = usage;
                f->bit_offset   = (uint16_t)(base + (uint16_t)e * g.report_size);
                f->bit_size     = g.report_size;
                f->count        = 1;
                f->logical_min  = g.logical_min;
                f->logical_max  = g.logical_max;
                f->is_array     = false;
              } else {
                out->overflow = true;
              }
            }
          } else { // array (e.g. keyboard key list)
            if (out->field_count < HID_MAX_FIELDS) {
              hid_field_t *f = &out->fields[out->field_count++];
              f->report_id   = g.report_id;
              f->usage_page  = page;
              f->usage       = l.has_range ? l.usage_min : (l.usage_count ? l.usages[0] : 0);
              f->usage_max   = l.has_range ? l.usage_max : f->usage;
              f->bit_offset  = base;
              f->bit_size    = g.report_size;
              f->count       = g.report_count;
              f->logical_min = g.logical_min;
              f->logical_max = g.logical_max;
              f->is_array    = true;
            } else {
              out->overflow = true;
            }
          }
        }
        if (cur) *cur = (uint16_t)(base + (uint16_t)g.report_size * g.report_count);
        memset(&l, 0, sizeof(l)); // local state clears after every main item
      } else if (bTag == M_OUTPUT || bTag == M_FEATURE || bTag == M_COLLECTION ||
                 bTag == M_END_COLLECTION) {
        memset(&l, 0, sizeof(l));
      }
    }
  }
  return out->field_count > 0 || out->uses_report_id;
}

void hid_layout_summary(const hid_layout_t *l, uint8_t *axis_count, uint8_t *button_count,
                        bool *has_hat, bool *has_keyboard) {
  uint8_t ax = 0, btn = 0;
  bool    hat = false, kb = false;
  for (uint8_t i = 0; i < l->field_count; i++) {
    const hid_field_t *f = &l->fields[i];
    if (f->usage_page == UP_BUTTON) {
      if (f->usage <= 32 && f->usage > btn) btn = (uint8_t)f->usage;
    } else if (f->usage_page == UP_DESKTOP) {
      if (f->usage >= 0x30 && f->usage <= 0x37) {
        uint8_t a = (uint8_t)(f->usage - 0x30 + 1);
        if (a > ax) ax = a;
      } else if (f->usage == 0x39) {
        hat = true;
      }
    } else if (f->usage_page == UP_KEYBOARD) {
      kb = true;
    }
  }
  if (axis_count) *axis_count = ax;
  if (button_count) *button_count = btn;
  if (has_hat) *has_hat = hat;
  if (has_keyboard) *has_keyboard = kb;
}

static uint32_t extract_bits(const uint8_t *p, uint16_t off, uint8_t n) {
  uint32_t v = 0;
  for (uint8_t i = 0; i < n; i++) {
    uint16_t bit = (uint16_t)(off + i);
    if (p[bit >> 3] & (1u << (bit & 7u))) v |= (1u << i);
  }
  return v;
}

static uint16_t normalize_axis(uint32_t raw, const hid_field_t *f) {
  int32_t lo = f->logical_min, hi = f->logical_max;
  int32_t val;
  if (lo < 0 && f->bit_size < 32u) { // signed field — sign-extend raw from bit_size
    uint32_t signbit = 1u << (f->bit_size - 1u);
    if (raw & signbit) val = (int32_t)(raw | ~((signbit << 1) - 1u));
    else val = (int32_t)raw;
  } else {
    val = (int32_t)raw;
  }
  if (hi <= lo) { // malformed range — left-justify to 16 bits
    if (f->bit_size >= 16u) return (uint16_t)(raw & 0xFFFFu);
    return (uint16_t)(raw << (16u - f->bit_size));
  }
  if (val <= lo) return 0;
  if (val >= hi) return 65535u;
  return (uint16_t)(((int64_t)(val - lo) * 65535) / (hi - lo));
}

bool hid_decode_report(const hid_layout_t *layout, const uint8_t *report, uint16_t len,
                       hid_state_t *st) {
  if (!layout || !report || len == 0) return false;
  hid_state_t prev = *st;

  uint8_t  rid = layout->uses_report_id ? report[0] : 0;
  uint16_t hdr = layout->uses_report_id ? 1u : 0u;
  if (len < hdr) return false;
  const uint8_t *pl    = report + hdr;
  uint16_t       plbit = (uint16_t)(len - hdr) * 8u;

  // If this report carries keyboard fields, rebuild the key set from scratch.
  bool kb_here = false;
  for (uint8_t i = 0; i < layout->field_count; i++)
    if (layout->fields[i].report_id == rid && layout->fields[i].usage_page == UP_KEYBOARD) {
      kb_here = true;
      break;
    }
  uint8_t key_idx = 0;
  if (kb_here) memset(st->keys, 0, sizeof(st->keys));

  for (uint8_t i = 0; i < layout->field_count; i++) {
    const hid_field_t *f = &layout->fields[i];
    if (f->report_id != rid) continue;

    if (f->is_array) {
      for (uint8_t e = 0; e < f->count; e++) {
        uint16_t off = (uint16_t)(f->bit_offset + (uint16_t)e * f->bit_size);
        if ((uint16_t)(off + f->bit_size) > plbit) break;
        uint32_t code = extract_bits(pl, off, f->bit_size);
        if (code != 0 && f->usage_page == UP_KEYBOARD && key_idx < INPUT_MAX_KEYS) {
          st->keys[key_idx++] = (uint8_t)code;
          st->has_keyboard    = true;
        }
      }
      continue;
    }

    if ((uint16_t)(f->bit_offset + f->bit_size) > plbit) continue;
    uint32_t raw = extract_bits(pl, f->bit_offset, f->bit_size);

    if (f->usage_page == UP_BUTTON) {
      if (f->usage >= 1 && f->usage <= 32) {
        uint32_t bit = 1u << (f->usage - 1u);
        if (raw) st->buttons |= bit;
        else st->buttons &= ~bit;
      }
    } else if (f->usage_page == UP_DESKTOP) {
      if (f->usage >= 0x30 && f->usage <= 0x37) {
        st->axes[f->usage - 0x30] = normalize_axis(raw, f);
      } else if (f->usage == 0x39) { // hat switch
        int32_t hv = (int32_t)raw - f->logical_min;
        if (hv >= 0 && hv < 8 && (int32_t)raw <= f->logical_max) st->hat = (uint8_t)hv;
        else st->hat = INPUT_HAT_RELEASED;
        st->has_hat = true;
      }
    } else if (f->usage_page == UP_KEYBOARD) {
      if (raw && key_idx < INPUT_MAX_KEYS) {
        st->keys[key_idx++] = (uint8_t)f->usage;
        st->has_keyboard    = true;
      }
    }
  }

  return memcmp(&prev, st, sizeof(prev)) != 0;
}

bool hid_decode_boot_keyboard(const uint8_t *report, uint16_t len, hid_state_t *st) {
  if (len < 3u) return false;
  hid_state_t prev = *st;
  memset(st->keys, 0, sizeof(st->keys));
  uint8_t ki   = 0;
  uint8_t mods = report[0];
  for (uint8_t b = 0; b < 8u && ki < INPUT_MAX_KEYS; b++)
    if (mods & (1u << b)) st->keys[ki++] = (uint8_t)(0xE0u + b);
  for (uint16_t i = 2; i < len && ki < INPUT_MAX_KEYS; i++)
    if (report[i] >= 4u) st->keys[ki++] = report[i];
  st->has_keyboard = true;
  return memcmp(&prev, st, sizeof(prev)) != 0;
}
