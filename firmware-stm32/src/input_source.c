// InputSource registry + the cross-source aggregation folds. See input_source.h.
#include "input_source.h"

static const InputSource *s_sources[INPUT_SOURCE_MAX];
static uint8_t            s_count;

void input_source_register(const InputSource *src) {
  if (src && s_count < INPUT_SOURCE_MAX) s_sources[s_count++] = src;
}
uint8_t            input_source_count(void) { return s_count; }
const InputSource *input_source_get(uint8_t i) { return (i < s_count) ? s_sources[i] : 0; }

// Buttons OR across every source that has the device.
uint32_t input_fold_buttons(uint16_t vid, uint16_t pid) {
  uint32_t bits = 0;
  for (uint8_t i = 0; i < s_count; i++)
    if (s_sources[i]->has_device(vid, pid)) bits |= s_sources[i]->buttons(vid, pid);
  return bits;
}

// Axis MAX across sources; *found stays false if no source reports it.
uint16_t input_fold_axis(uint16_t vid, uint16_t pid, uint8_t index, bool *found) {
  uint16_t best = 0;
  bool     any  = false;
  for (uint8_t i = 0; i < s_count; i++) {
    bool     f = false;
    uint16_t v = s_sources[i]->axis(vid, pid, index, &f);
    if (f && (!any || v > best)) { best = v; any = true; }
  }
  if (found) *found = any;
  return best;
}

bool input_fold_hat_is(uint16_t vid, uint16_t pid, uint8_t dir) {
  for (uint8_t i = 0; i < s_count; i++)
    if (s_sources[i]->hat_is(vid, pid, dir)) return true;
  return false;
}

bool input_fold_key(uint16_t vid, uint16_t pid, uint8_t scancode) {
  for (uint8_t i = 0; i < s_count; i++)
    if (s_sources[i]->key_pressed(vid, pid, scancode)) return true;
  return false;
}

uint32_t input_total_change_seq(void) {
  uint32_t s = 0;
  for (uint8_t i = 0; i < s_count; i++) s += s_sources[i]->change_seq();
  return s;
}
