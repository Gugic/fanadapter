// CSL Elite V2 pedal-port UART emulator on USART2. C port of firmware/pedals.cpp. See pedals.h.
//
// Faithful port of the Teensy state machine — same handshake bytes, same lenient STEP2 collector,
// same 2 s warmup, same 100 Hz framing — with three platform swaps:
//   * Serial3            -> USART2 HAL UART on PA2 (TX) / PA3 (RX), 8N1.
//   * millis()           -> HAL_GetTick().
//   * Serial3.read/avail -> a register-level RXNE interrupt ring (USART2_IRQHandler), drained here.
// TX is a blocking HAL_UART_Transmit: the streaming frame is 12 bytes (~1 ms at 115200), gated to
// 100 Hz, and pedals_update() runs LAST in the loop, so the brief block doesn't slip the cadence —
// exactly what the Teensy did. (An IT/DMA TX with a tx-in-flight guard is a possible future
// optimization; blocking is the proven, simpler path and what this untested-on-HW port ships with.)
#include "pedals.h"

#include "stm32h7xx_hal.h"

extern int console_printf(const char *fmt, ...);
extern void Error_Handler(void);

// ---------------- State machine ----------------
typedef enum {
  STATE_INIT = 0,
  STATE_STEP0,    // 250000 baud, wait for 0x0A, send 0x1A
  STATE_STEP1,    // 250000 baud, wait for 0x05, send 0x15
  STATE_STEP2,    // 115200 baud, wait for framed queries, ack each
  STATE_STREAMING // 115200 baud, 100 Hz pedal frames
} pedals_state_t;

static pedals_state_t s_state = STATE_INIT;
static uint32_t       s_last_activity_ms;
static uint32_t       s_last_stream_ms;

// Wheelbase-restart auto-recovery. Once in STREAMING we ignore all RX (the wheelbase keeps emitting
// stale 0x7B queries that we must not act on). But a POWER-CYCLED wheelbase re-initiates its handshake
// by sending 0x0A at 250000 baud while we are still camped at 115200 — those bytes arrive as a burst of
// UART framing/overrun errors (a matched-baud stale query produces none). The RX ISR tallies those
// errors; on a burst we re-arm the handshake. Without this the adapter stays stuck in STREAMING, the
// wheelbase falls back to analog pedal sensing, and our 100 Hz TX reads as jitter on those analog pins.
// (This dead-end carried over from the Teensy, which only recovered via the manual re-arm button.)
#define RESTART_ERR_THRESHOLD 32u
static volatile uint32_t s_rx_err_count;
static uint32_t          s_err_decay_ms;

// Warmup: on boot, silently drain RX for ~2 s before engaging the handshake, giving the wheelbase a
// clean silence window to reset its end after our reboot (otherwise it can hang mid-retry). 0 once done.
static uint32_t       s_warmup_until;
#define WARMUP_MS 2000u

// STEP2 lenient any-order collector — the DD+ sends framed queries (cmd 0x00/0x02/0x03) repeatedly in
// arbitrary order; we ack each command ID once and transition on the 0x03 (version-identity) ack.
static uint8_t s_step2_buf[12];
static uint8_t s_step2_idx;
static bool    s_step2_got00, s_step2_got02, s_step2_got03;

// Per-query acks (sent individually per matching query — some firmware revs enforce a per-packet
// timeout that a single bulk 36-byte reply misses). Same bytes as the Teensy / proxy.go STEP2_TX.
static const uint8_t STEP2_TX_CMD_02[12] = {0x7B, 0x05, 0x06, 0x62, 0x00, 0x00,
                                            0x00, 0x00, 0x00, 0x00, 0x6D, 0x7D};
static const uint8_t STEP2_TX_CMD_00[12] = {0x7B, 0x07, 0x0B, 0x00, 0x00, 0x00,
                                            0x00, 0x00, 0x00, 0x00, 0x12, 0x7D};
static const uint8_t STEP2_TX_CMD_03[12] = {0x7B, 0x08, 0x01, 0x06, 0x07, 0x00,
                                            0x00, 0x00, 0x00, 0x00, 0xBF, 0x7D};

// Pedal levels (0..65535) — refreshed by mapping_tick(), streamed in each frame.
static volatile uint16_t s_throttle, s_brake, s_clutch, s_handbrake;

// CRC-8 Dallas/Maxim (poly 0x8C), table-driven — identical to the Teensy.
static uint8_t s_crc_table[256];
static void    crc_init(void) {
  for (int i = 0; i < 256; i++) {
    uint8_t crc = (uint8_t)i;
    for (int j = 0; j < 8; j++) {
      bool bit = (crc & 0x01u) != 0;
      crc >>= 1;
      if (bit) crc ^= 0x8Cu;
    }
    s_crc_table[i] = crc;
  }
}
static uint8_t crc8(const uint8_t *data, uint32_t len) {
  uint8_t crc = 0xFFu;
  for (uint32_t i = 0; i < len; i++) crc = s_crc_table[data[i] ^ crc];
  return crc;
}

// ---------------- USART2 + RX ring ----------------
static UART_HandleTypeDef s_uart;

#define PEDALS_RX_RING_SZ 256u // power of two -> mask
static volatile uint8_t  s_rx_ring[PEDALS_RX_RING_SZ];
static volatile uint16_t s_rx_head; // ISR
static volatile uint16_t s_rx_tail; // pedals_update()

static inline bool rx_available(void) { return s_rx_head != s_rx_tail; }
static inline int  rx_read(void) {
  if (s_rx_head == s_rx_tail) return -1;
  uint8_t b = s_rx_ring[s_rx_tail];
  s_rx_tail = (uint16_t)((s_rx_tail + 1u) & (PEDALS_RX_RING_SZ - 1u));
  return (int)b;
}
static inline void rx_flush(void) { s_rx_tail = s_rx_head; }

static void uart_apply_init(uint32_t baud) {
  s_uart.Instance            = USART2;
  s_uart.Init.BaudRate       = baud;
  s_uart.Init.WordLength     = UART_WORDLENGTH_8B; // 8N1 — the Fanatec pedal-port format
  s_uart.Init.StopBits       = UART_STOPBITS_1;
  s_uart.Init.Parity         = UART_PARITY_NONE;
  s_uart.Init.Mode           = UART_MODE_TX_RX;
  s_uart.Init.HwFlowCtl      = UART_HWCONTROL_NONE;
  s_uart.Init.OverSampling   = UART_OVERSAMPLING_16;
  s_uart.Init.OneBitSampling = UART_ONE_BIT_SAMPLE_DISABLE;
  s_uart.Init.ClockPrescaler = UART_PRESCALER_DIV1;
  s_uart.AdvancedInit.AdvFeatureInit = UART_ADVFEATURE_NO_INIT;
  if (HAL_UART_Init(&s_uart) != HAL_OK) Error_Handler();
  HAL_UARTEx_SetRxFifoThreshold(&s_uart, UART_RXFIFO_THRESHOLD_1_8);
  HAL_UARTEx_SetTxFifoThreshold(&s_uart, UART_TXFIFO_THRESHOLD_1_8);
  HAL_UARTEx_EnableFifoMode(&s_uart);
  __HAL_UART_ENABLE_IT(&s_uart, UART_IT_RXNE);
}

// Re-init for a baud change (250000 <-> 115200). DeInit hits the weak (empty) MspDeInit, so the
// PA2/PA3 AF config and the USART2 clock survive; we just re-arm RX and drop any stale RX bytes from
// the switch window (the lenient STEP2 collector tolerates the loss).
static void uart_set_baud(uint32_t baud) {
  __HAL_UART_DISABLE_IT(&s_uart, UART_IT_RXNE);
  HAL_UART_DeInit(&s_uart);
  uart_apply_init(baud);
  rx_flush();
}

static inline void uart_tx(const uint8_t *data, uint16_t len) {
  HAL_UART_Transmit(&s_uart, (uint8_t *)data, len, 10); // ~1 ms for 12 B at 115200; 10 ms is ample
}
static inline void uart_tx_byte(uint8_t b) { uart_tx(&b, 1); }

// ---------------- handshake helpers ----------------
static void reset_step2(void) {
  s_step2_idx   = 0;
  s_step2_got00 = s_step2_got02 = s_step2_got03 = false;
}

static void reset_to_step0(void) {
  console_printf("[pedals] -> STEP0 (250000 baud)\r\n");
  uart_set_baud(250000);
  s_state            = STATE_STEP0;
  reset_step2();
  s_rx_err_count     = 0; // matched baud again — clear the restart-detection tally
  s_last_activity_ms = HAL_GetTick();
}

static void send_pedal_frame(void) {
  uint8_t p[12];
  p[0]  = 0x7B; // start
  p[1]  = 0x01; // pedals command
  p[2]  = (uint8_t)(s_throttle & 0xFF);
  p[3]  = (uint8_t)(s_throttle >> 8);
  p[4]  = (uint8_t)(s_brake & 0xFF);
  p[5]  = (uint8_t)(s_brake >> 8);
  p[6]  = (uint8_t)(s_clutch & 0xFF);
  p[7]  = (uint8_t)(s_clutch >> 8);
  p[8]  = (uint8_t)(s_handbrake & 0xFF);
  p[9]  = (uint8_t)(s_handbrake >> 8);
  p[10] = crc8(&p[1], 9); // CRC over packet[1..9]
  p[11] = 0x7D;           // end
  uart_tx(p, 12);
}

// ---------------- lifecycle ----------------
static void uart_gpio_init(void) {
  GPIO_InitTypeDef g = {0};
  __HAL_RCC_GPIOA_CLK_ENABLE();
  __HAL_RCC_USART2_CLK_ENABLE();

  g.Pin       = GPIO_PIN_2 | GPIO_PIN_3; // PA2 = USART2_TX, PA3 = USART2_RX
  g.Mode      = GPIO_MODE_AF_PP;
  g.Pull      = GPIO_PULLUP; // idle-high line
  g.Speed     = GPIO_SPEED_FREQ_HIGH;
  g.Alternate = GPIO_AF7_USART2;
  HAL_GPIO_Init(GPIOA, &g);

  // RX drained promptly even under USB host ISR load: priority 5 (above USB = 6), matching the
  // USART1 console. The ISR is a few register reads; preempting USB for it is harmless.
  HAL_NVIC_SetPriority(USART2_IRQn, 5, 0);
  HAL_NVIC_EnableIRQ(USART2_IRQn);
}

void pedals_init(void) {
  crc_init();
  uart_gpio_init();
  uart_apply_init(250000);
  s_warmup_until = HAL_GetTick() + WARMUP_MS;
  console_printf("[pedals] warmup: draining USART2 for %u ms before handshake\r\n", WARMUP_MS);
  reset_to_step0();
  // reset_to_step0 reinit'd the UART (a harmless DeInit/Init at the same baud); the warmup window
  // above stays armed across it.
}

void pedals_force_reset(void) {
  console_printf("[pedals] manual reset\r\n");
  s_warmup_until = 0; // user asked for it now; skip the warmup
  reset_to_step0();
}

// ---------------- per-tick ----------------
void pedals_update(void) {
  // Warmup: drop stale bytes from a prior session until the line goes quiet.
  if (s_warmup_until != 0) {
    if ((int32_t)(HAL_GetTick() - s_warmup_until) < 0) {
      rx_flush();
      return;
    }
    console_printf("[pedals] warmup complete — engaging handshake\r\n");
    s_warmup_until     = 0;
    s_last_activity_ms = HAL_GetTick();
  }

  // Handshake timeout: STEP0 (waits indefinitely for the wheelbase) and STREAMING (the wheelbase
  // keeps emitting stale queries with no expected reply) are exempt.
  if (s_state != STATE_STEP0 && s_state != STATE_STREAMING) {
    if ((uint32_t)(HAL_GetTick() - s_last_activity_ms) > 2000u) {
      console_printf("[pedals] handshake timeout -> STEP0\r\n");
      reset_to_step0();
      return;
    }
  }

  int c;
  while ((c = rx_read()) >= 0) {
    uint8_t b          = (uint8_t)c;
    s_last_activity_ms = HAL_GetTick();

    switch (s_state) {
      case STATE_STEP0:
        if (b == 0x0A) {
          console_printf("[pedals] STEP0: 0x0A -> 0x1A\r\n");
          uart_tx_byte(0x1A);
          s_state = STATE_STEP1;
        }
        break;

      case STATE_STEP1:
        if (b == 0x05) {
          console_printf("[pedals] STEP1: 0x05 -> 0x15, switch to 115200 (STEP2)\r\n");
          uart_tx_byte(0x15);      // blocking TX waits for TC, so it is fully out before the reinit
          uart_set_baud(115200);
          reset_step2();
          s_state = STATE_STEP2;
        } else {
          console_printf("[pedals] STEP1: expected 0x05, got 0x%02X -> STEP0\r\n", b);
          reset_to_step0();
        }
        break;

      case STATE_STEP2: {
        if (s_step2_idx == 0) {
          if (b == 0x7B) { s_step2_buf[0] = b; s_step2_idx = 1; }
          break; // ignore anything before the next start marker (baud-switch garbage)
        }
        s_step2_buf[s_step2_idx++] = b;
        if (s_step2_idx < 12) break;

        s_step2_idx = 0; // full 12-byte packet
        if (s_step2_buf[11] != 0x7D) break;          // bad end marker
        if (s_step2_buf[10] != crc8(&s_step2_buf[1], 9)) break; // CRC fail

        switch (s_step2_buf[1]) { // command ID
          case 0x00:
            if (!s_step2_got00) { uart_tx(STEP2_TX_CMD_00, 12); s_step2_got00 = true; }
            break;
          case 0x02:
            if (!s_step2_got02) { uart_tx(STEP2_TX_CMD_02, 12); s_step2_got02 = true; }
            break;
          case 0x03:
            if (!s_step2_got03) { uart_tx(STEP2_TX_CMD_03, 12); s_step2_got03 = true; }
            break;
          default:
            break;
        }

        // The 0x03 ack is the version-identity packet; once acked the wheelbase expects streaming
        // immediately (0x00/0x02 arrive less reliably, so we don't gate on them).
        if (s_step2_got03) {
          console_printf("[pedals] handshake complete (0x03 acked) -> STREAMING\r\n");
          s_state          = STATE_STREAMING;
          s_last_stream_ms = HAL_GetTick();
          s_rx_err_count   = 0; // arm restart detection from a clean slate
          s_err_decay_ms   = HAL_GetTick();
        }
        break;
      }

      case STATE_STREAMING:
        // The wheelbase keeps sending stale 0x7B-framed queries after STEP2 — ignore them; re-acking
        // or flapping back to STEP0 would only break the stream. (A baud-mismatch error BURST — a
        // power-cycled wheelbase re-handshaking at 250000 — is handled separately below, by tally.)
        break;

      default:
        break;
    }
  }

  // 100 Hz pedal frames once streaming. last set before the send so the ~1 ms TX doesn't drift cadence.
  if (s_state == STATE_STREAMING) {
    uint32_t now = HAL_GetTick();

    // Wheelbase-restart auto-recovery: a burst of RX framing/overrun errors means the wheelbase is
    // re-handshaking at 250000 while we stream at 115200. Re-arm via the known-good boot entry (2 s
    // warmup drains the noisy line, then STEP0 @ 250000 catches the wheelbase's fresh 0x0A).
    if (s_rx_err_count >= RESTART_ERR_THRESHOLD) {
      console_printf("[pedals] RX error burst (%lu) — wheelbase restart? re-arming handshake\r\n",
                     (unsigned long)s_rx_err_count);
      s_warmup_until = now + WARMUP_MS;
      reset_to_step0();
      return;
    }
    // Bleed off isolated glitches so a rare single error can't accumulate to the threshold over a long
    // session; a real restart floods errors far faster than this 1 Hz decay.
    if ((uint32_t)(now - s_err_decay_ms) >= 1000u) {
      s_err_decay_ms = now;
      if (s_rx_err_count) s_rx_err_count--;
    }

    if ((uint32_t)(now - s_last_stream_ms) >= 10u) {
      s_last_stream_ms = now;
      send_pedal_frame();
    }
  }
}

// ---------------- ISR ----------------
// USART2 RXNE -> ring buffer. Drops on overflow; clears error flags so a glitch can't wedge RXNE.
void USART2_IRQHandler(void) {
  while (__HAL_UART_GET_FLAG(&s_uart, UART_FLAG_RXNE)) {
    uint8_t  b    = (uint8_t)(s_uart.Instance->RDR & 0xFFu);
    uint16_t next = (uint16_t)((s_rx_head + 1u) & (PEDALS_RX_RING_SZ - 1u));
    if (next != s_rx_tail) {
      s_rx_ring[s_rx_head] = b;
      s_rx_head            = next;
    }
  }
  // Tally framing/overrun/noise errors before clearing — a baud mismatch (a power-cycled wheelbase
  // re-handshaking at 250000 while we stream at 115200) produces a burst of these; STREAMING uses the
  // tally to auto-re-arm. Matched-baud traffic produces none, so this never false-triggers in steady state.
  if (s_uart.Instance->ISR & (USART_ISR_ORE | USART_ISR_FE | USART_ISR_NE)) {
    if (s_rx_err_count < 0xFFFFFFFFu) s_rx_err_count++;
  }
  __HAL_UART_CLEAR_FLAG(&s_uart, UART_CLEAR_OREF | UART_CLEAR_FEF | UART_CLEAR_NEF | UART_CLEAR_PEF);
}

// ---------------- accessors ----------------
const char *pedals_state_name(void) {
  switch (s_state) {
    case STATE_INIT: return "INIT";
    case STATE_STEP0:
    case STATE_STEP1: return "HANDSHAKE_250K";
    case STATE_STEP2: return "HANDSHAKE_115K";
    case STATE_STREAMING: return "STREAMING_115K";
    default: return "UNKNOWN";
  }
}

void pedals_set_throttle(uint16_t v) { s_throttle = v; }
void pedals_set_brake(uint16_t v) { s_brake = v; }
void pedals_set_clutch(uint16_t v) { s_clutch = v; }
void pedals_set_handbrake(uint16_t v) { s_handbrake = v; }

uint16_t pedals_get_throttle(void) { return s_throttle; }
uint16_t pedals_get_brake(void) { return s_brake; }
uint16_t pedals_get_clutch(void) { return s_clutch; }
uint16_t pedals_get_handbrake(void) { return s_handbrake; }
