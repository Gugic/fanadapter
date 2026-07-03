// Output stage: H-pattern X/Y on DAC1 + non-blocking neutral transit. See outputs.h.
#include "outputs.h"

#include <string.h>

#include "stm32h7xx_hal.h"

extern int  console_printf(const char *fmt, ...);
extern void Error_Handler(void);

// DAC1: channel 1 -> PA4 (X / column), channel 2 -> PA5 (Y / row). The stored gearOut[].x/.y codes
// (0..4095, 12-bit) map 1:1 to the 12-bit DAC, so the per-gear target voltages from
// schematics/README.md "Shifter 1 Port" come straight through:
//   X1 3.30V=4095  X2 2.25V=2790  X3 1.74V=2163  X4 1.42V=1766  X5 1.06V=1310
//   Y1 2.76V=3430  Y2 1.65V=2048  Y3 0.63V=779
// Output buffer ENABLED for a low-impedance drive into the wheelbase's analog input. Its swing is
// clamped to ~0.2V..VDDA-0.2V (~3.1V at 3.3V VDDA), so the single near-rail code — Reverse's
// X1 = 3.30V (4095) — settles ~0.2V low. That is still unambiguous against X2 = 2.25V, and the
// wheelbase normalizes it during shifter calibration; recalibrate any column via set_gear_dac if a
// meter shows it off (per the M3/M7 DAC-recal pass).
static DAC_HandleTypeDef s_dac;

// Neutral-transit FSM. s_current is the gear on the DAC right now (CH_GEAR_N while mid-transit);
// s_pending is the gear to latch once the transit wait elapses.
static ChannelId s_current      = CH_GEAR_N;
static ChannelId s_pending      = CH_GEAR_N;
static uint32_t  s_transit_until = 0;
static bool      s_in_transit    = false;

// Sequential shift outputs: open-drain, idle HIGH (wheelbase has the pull-up); a shift = a brief LOW
// pulse. PC6 = up, PC7 = down (free header pins, clear of USB PA11/12, DAC PA4/5, console PA9/10 and
// the heartbeat set). Non-blocking — outputs_tick() releases the line when the pulse elapses.
#define SEQ_UP_PORT   GPIOC
#define SEQ_UP_PIN    GPIO_PIN_6
#define SEQ_DOWN_PORT GPIOC
#define SEQ_DOWN_PIN  GPIO_PIN_7
#define SEQ_DEFAULT_MS 50u
typedef struct {
  GPIO_TypeDef *port;
  uint16_t      pin;
  uint32_t      end_ms;
  bool          active;
} pulse_t;
static pulse_t s_seq_up   = {SEQ_UP_PORT, SEQ_UP_PIN, 0, false};
static pulse_t s_seq_down = {SEQ_DOWN_PORT, SEQ_DOWN_PIN, 0, false};

// Handbrake PWM fallback on TIM3_CH3 = PC8 (AF2). ARR = 4095, PSC = 0 -> 64 MHz / 4096 ≈ 15.6 kHz
// carrier (clean for an RC low-pass). Outside the heartbeat set and clear of PC6/PC7 (sequential).
// This is the FALLBACK handbrake leg; the primary path is the pedal stream (dual-written in mapping).
#define HB_PWM_PORT    GPIOC
#define HB_PWM_PIN     GPIO_PIN_8
#define HB_PWM_CHANNEL TIM_CHANNEL_3
#define HB_PWM_ARR     4095u
static TIM_HandleTypeDef s_hb_tim;
static bool              s_hb_pwm_ok;

static void pulse_start(pulse_t *p, uint16_t dur_ms) {
  HAL_GPIO_WritePin(p->port, p->pin, GPIO_PIN_RESET); // pull LOW = shift asserted
  p->end_ms = HAL_GetTick() + (dur_ms ? dur_ms : SEQ_DEFAULT_MS);
  p->active = true;
}
static void pulse_update(pulse_t *p) {
  if (p->active && (int32_t)(HAL_GetTick() - p->end_ms) >= 0) {
    HAL_GPIO_WritePin(p->port, p->pin, GPIO_PIN_SET); // release HIGH (open-drain)
    p->active = false;
  }
}

static void write_gear_dac(ChannelId gear) {
  const GearOutputCalibration *g = mapping_gearout_for(gear);
  if (!g) return;
  HAL_DAC_SetValue(&s_dac, DAC_CHANNEL_1, DAC_ALIGN_12B_R, g->x);
  HAL_DAC_SetValue(&s_dac, DAC_CHANNEL_2, DAC_ALIGN_12B_R, g->y);
}

void outputs_init(void) {
  GPIO_InitTypeDef gp = {0};
  __HAL_RCC_GPIOA_CLK_ENABLE();
  __HAL_RCC_DAC12_CLK_ENABLE(); // DAC1 + DAC2 share the DAC12 clock gate on the H7

  gp.Pin  = GPIO_PIN_4 | GPIO_PIN_5; // PA4 = DAC1_OUT1 (X), PA5 = DAC1_OUT2 (Y)
  gp.Mode = GPIO_MODE_ANALOG;
  gp.Pull = GPIO_NOPULL;
  HAL_GPIO_Init(GPIOA, &gp);

  // Sequential shift pins (configured before the DAC so a DAC fault can't skip them).
  __HAL_RCC_GPIOC_CLK_ENABLE();
  gp.Pin   = SEQ_UP_PIN | SEQ_DOWN_PIN;
  gp.Mode  = GPIO_MODE_OUTPUT_OD; // open-drain; wheelbase pulls the line high
  gp.Pull  = GPIO_NOPULL;
  gp.Speed = GPIO_SPEED_FREQ_LOW;
  HAL_GPIO_Init(GPIOC, &gp);
  HAL_GPIO_WritePin(SEQ_UP_PORT, SEQ_UP_PIN, GPIO_PIN_SET); // idle released (HIGH)
  HAL_GPIO_WritePin(SEQ_DOWN_PORT, SEQ_DOWN_PIN, GPIO_PIN_SET);

  // Handbrake PWM fallback on TIM3_CH3 / PC8. Non-fatal on failure — the pedal stream is the primary
  // handbrake path, and a TIM fault must not brick the USB-host primary function.
  __HAL_RCC_TIM3_CLK_ENABLE();
  gp.Pin       = HB_PWM_PIN;
  gp.Mode      = GPIO_MODE_AF_PP;
  gp.Pull      = GPIO_NOPULL;
  gp.Speed     = GPIO_SPEED_FREQ_LOW;
  gp.Alternate = GPIO_AF2_TIM3;
  HAL_GPIO_Init(HB_PWM_PORT, &gp);

  s_hb_tim.Instance               = TIM3;
  s_hb_tim.Init.Prescaler         = 0;
  s_hb_tim.Init.CounterMode       = TIM_COUNTERMODE_UP;
  s_hb_tim.Init.Period            = HB_PWM_ARR;
  s_hb_tim.Init.ClockDivision     = TIM_CLOCKDIVISION_DIV1;
  s_hb_tim.Init.AutoReloadPreload = TIM_AUTORELOAD_PRELOAD_ENABLE;
  if (HAL_TIM_PWM_Init(&s_hb_tim) == HAL_OK) {
    TIM_OC_InitTypeDef oc = {0};
    oc.OCMode     = TIM_OCMODE_PWM1;
    oc.Pulse      = 0; // start at 0% (handbrake released)
    oc.OCPolarity = TIM_OCPOLARITY_HIGH;
    oc.OCFastMode = TIM_OCFAST_DISABLE;
    if (HAL_TIM_PWM_ConfigChannel(&s_hb_tim, &oc, HB_PWM_CHANNEL) == HAL_OK &&
        HAL_TIM_PWM_Start(&s_hb_tim, HB_PWM_CHANNEL) == HAL_OK)
      s_hb_pwm_ok = true;
  }
  if (!s_hb_pwm_ok) console_printf("[outputs] handbrake PWM init failed (non-fatal)\r\n");

  s_dac.Instance = DAC1;
  if (HAL_DAC_Init(&s_dac) != HAL_OK) {
    console_printf("[outputs] DAC init failed — H-pattern output disabled\r\n");
    return; // non-fatal: don't brick the USB-host primary function over a DAC fault
  }

  DAC_ChannelConfTypeDef ch = {0};
  ch.DAC_SampleAndHold           = DAC_SAMPLEANDHOLD_DISABLE;
  ch.DAC_Trigger                 = DAC_TRIGGER_NONE; // software-loaded, no external trigger
  ch.DAC_OutputBuffer            = DAC_OUTPUTBUFFER_ENABLE;
  ch.DAC_ConnectOnChipPeripheral = DAC_CHIPCONNECT_EXTERNAL; // drive the external pin
  ch.DAC_UserTrimming            = DAC_TRIMMING_FACTORY;
  if (HAL_DAC_ConfigChannel(&s_dac, &ch, DAC_CHANNEL_1) != HAL_OK ||
      HAL_DAC_ConfigChannel(&s_dac, &ch, DAC_CHANNEL_2) != HAL_OK) {
    console_printf("[outputs] DAC channel config failed\r\n");
    return;
  }
  HAL_DAC_Start(&s_dac, DAC_CHANNEL_1);
  HAL_DAC_Start(&s_dac, DAC_CHANNEL_2);

  // Rest at the neutral X/Y voltages (not 0 V, which is not a valid Fanatec column).
  s_current   = CH_GEAR_N;
  s_pending   = CH_GEAR_N;
  s_in_transit = false;
  write_gear_dac(CH_GEAR_N);
}

void outputs_request_gear(ChannelId target) {
  if (target < CH_GEAR_R || target > CH_GEAR_N) return;

  if (s_in_transit) {
    s_pending = target; // newest target wins; the running wait latches it
    return;
  }
  if (target == s_current) return;

  if (target != CH_GEAR_N && s_current != CH_GEAR_N) {
    // Non-neutral -> non-neutral: drop to neutral now, latch the target after the transit wait so
    // the wheelbase sees a release between gears. Replaces the Teensy's blocking delay(50).
    write_gear_dac(CH_GEAR_N);
    s_current      = CH_GEAR_N;
    s_pending      = target;
    s_transit_until = HAL_GetTick() + NEUTRAL_TRANSIT_MS;
    s_in_transit   = true;
    return;
  }

  // To/from neutral: apply immediately.
  write_gear_dac(target);
  s_current = target;
}

void outputs_tick(void) {
  if (s_in_transit && (int32_t)(HAL_GetTick() - s_transit_until) >= 0) {
    write_gear_dac(s_pending);
    s_current    = s_pending;
    s_in_transit = false;
  }
  pulse_update(&s_seq_up);
  pulse_update(&s_seq_down);
}

ChannelId outputs_current_gear(void) { return s_current; }

void outputs_pulse_shift(bool up, uint16_t duration_ms) {
  pulse_start(up ? &s_seq_up : &s_seq_down, duration_ms);
}

bool outputs_shift_active(bool up) { return (up ? &s_seq_up : &s_seq_down)->active; }

void outputs_set_handbrake_pwm(uint16_t value) {
  if (!s_hb_pwm_ok) return;
  __HAL_TIM_SET_COMPARE(&s_hb_tim, HB_PWM_CHANNEL, (uint32_t)(value >> 4)); // 16-bit -> 12-bit duty
}
