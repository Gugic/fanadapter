// Physical output stage. M3 scope: the H-pattern X/Y gear voltages on DAC1 (PA4/PA5) plus the
// non-blocking neutral-transit FSM. Sequential pulse pins and the handbrake PWM fallback land in M5.
//
// The Teensy drove the H-pattern X/Y through PWM + RC filters; the STM32 has a real 2-channel DAC,
// so the stored per-gear 12-bit codes (gearOut[].x/.y, 0..4095) map straight to the two DAC channels
// — cleaner analog, no RC. Handbrake stays on a PWM+RC leg (added in M5); both DAC channels are
// taken by the H-pattern.
#pragma once
#include "mapping.h" // ChannelId

// Configure DAC1 ch1/ch2 on PA4/PA5 and snap the output to the neutral gear. Call ONCE, AFTER
// mapping_init() (it reads gearOut[] for the neutral codes).
void outputs_init(void);

// Request a target gear (CH_GEAR_R..CH_GEAR_N). Non-neutral -> non-neutral transitions drop to
// neutral first and latch the target after NEUTRAL_TRANSIT_MS, so the wheelbase always sees a gear
// release between gears. Never blocks — the wait is advanced by outputs_tick().
void outputs_request_gear(ChannelId target);

// Advance the neutral-transit FSM + sequential pulse timers. Call every loop iteration (mapping_tick does).
void outputs_tick(void);

// The gear currently applied to the DAC (CH_GEAR_N while mid-transit).
ChannelId outputs_current_gear(void);

// Sequential shift: fire a non-blocking open-drain LOW pulse of `duration_ms` (0 -> default) on the
// up or down line. Idle HIGH (the wheelbase provides the pull-up). Advanced by outputs_tick().
void outputs_pulse_shift(bool up, uint16_t duration_ms);
bool outputs_shift_active(bool up); // pulse currently asserted (for the outputs snapshot)
