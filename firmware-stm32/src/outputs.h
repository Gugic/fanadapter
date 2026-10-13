// Physical outputs: H-pattern X/Y on DAC1 (PA4/PA5), sequential pulses on PC6/PC7,
// and handbrake PWM on PC8. Gear neutral transit and sequential timing are non-blocking.
//
// The STM32 has a real 2-channel DAC: stored per-gear 12-bit codes (gearOut[].x/.y, 0..4095) map
// straight to the DAC channels with no RC filters. Handbrake uses a PWM+RC leg; both DAC channels are
// taken by the H-pattern. The pedal UART also carries handbrake; its mapping is refreshed in parallel.
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

// Handbrake PWM fallback (M5): drive a TIM3_CH3 PWM duty (PC8) from a 0..65535 level, for an RC
// low-pass into an analog handbrake input. This is the FALLBACK leg — modern Fanatec firmware reads
// the handbrake from the pedal stream (pedals.c), which mapping.c writes in parallel; this PWM pin
// may never be wired. value is mapped 16-bit -> 12-bit duty (value >> 4 against ARR = 4095).
void outputs_set_handbrake_pwm(uint16_t value);
