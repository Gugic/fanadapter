// fanadapter — STM32H743 USB host enumerator (bring-up firmware)
//
// Goal of this build: prove that all USB devices behind a powered hub enumerate on the
// STM32H743's USB host (16 channels — the count that killed the ESP32 port), and print
// them to a serial console so we can eyeball that every device is visible.
//
// Topology — USB host on one controller, the serial console exposed TWO ways at once:
//   OTG_FS (USB2, PA11/PA12 = USB-C / A11-A12 header) -> USB HOST FS -> powered hub -> wheel devices
//   OTG_HS (USB1, PB14/PB15)                          -> USB CDC device = serial console (recommended)
//   USART1 (PA9=TX, PA10=RX, on the 8-pin header)     -> UART serial console (universal fallback)
//
// Why both consoles? The recommended open-source board has BOTH USB ports broken out: plug the
// hub into the OTG_FS port and a second cable from the PC into the OTG_HS port, and the device
// list shows up as a COM port — no extra hardware. But the FK743M3 board used for bring-up has
// OTG_HS unwired (PB15 = OTG_HS D+ is an unsolderable 0.1 mm via), so its CDC console can never
// enumerate; the parallel USART1 console (read via a USB-UART bridge) carries the same output
// there. console_printf() writes to both sinks, so one firmware serves both kinds of board.
// Host stays on OTG_FS because that's the only controller usable on the single-port boards.
//
// On STM32H7 TinyUSB's dwc2 driver maps rhport 0 = OTG_FS, rhport 1 = OTG_HS.

#include <stdarg.h>
#include <stdio.h>
#include <string.h>

#include "stm32h7xx_hal.h"
#include "tusb.h"

#include "mapping.h"
#include "outputs.h"
#include "pedals.h"
#include "protocol.h"
#include "usb_input.h"

// rhport is fixed by hardware regardless of role: OTG_FS is always rhport 0, OTG_HS always rhport 1.
#define OTGFS_RHPORT  0u // on-board USB-C (PA11/PA12)
#define OTGHS_RHPORT  1u // PB14/PB15 header
// Role assignment. The USB-C is a DEVICE receptacle (UFP/Rd) — a hub plugged into it never
// attaches — so the PC side lives there (console + webconfig + DFU + power, no adapter) and the
// host hangs off the PB14/PB15 breakout wired straight to a self-powered hub.
#define DEVICE_RHPORT OTGFS_RHPORT // USB-C -> PC: CDC console + webconfig (also the DFU port)
#define HOST_RHPORT   OTGHS_RHPORT // PB14/PB15 breakout -> powered hub -> wheel devices

// Heartbeat so we can CONFIRM the board is running our code at a glance, independent of the
// console. The user-LED pin varies across these H743 clones and is still unidentified, so
// toggle several candidates at ~0.5 Hz; whichever one is the LED blinks. None of these collide
// with USB (PA11/PA12, PB14/PB15) or the USART1 console (PA9/PA10).
typedef struct {
  GPIO_TypeDef *port;
  uint16_t      pin;
} heartbeat_pin_t;
static const heartbeat_pin_t HEARTBEAT_PINS[] = {
    {GPIOE, GPIO_PIN_3}, {GPIOB, GPIO_PIN_7}, {GPIOB, GPIO_PIN_0},
    {GPIOB, GPIO_PIN_1}, {GPIOE, GPIO_PIN_1}, {GPIOD, GPIO_PIN_13},
};
#define HEARTBEAT_COUNT (sizeof(HEARTBEAT_PINS) / sizeof(HEARTBEAT_PINS[0]))

#define LANGUAGE_ID 0x0409

static UART_HandleTypeDef huart1; // fallback console on USART1 (PA9/PA10)

// USART1 RX ring — filled by USART1_IRQHandler (RXNE), drained in the main loop. This is the
// inbound half of the console: typed/sent bytes (from the S3 bridge's PC->board direction, via
// PA10) land here. M0 echoes them back to prove the bidirectional path; the protocol layer (M1)
// will consume this ring as its line-JSON input (alongside tud_cdc_read() for the CDC console).
#define CONSOLE_RX_RING_SZ 256u // power of two -> mask instead of modulo
static volatile uint8_t  s_rx_ring[CONSOLE_RX_RING_SZ];
static volatile uint16_t s_rx_head; // advanced by the ISR
static volatile uint16_t s_rx_tail; // advanced by the main loop

static inline bool console_rx_available(void) { return s_rx_head != s_rx_tail; }
static inline int console_read_byte(void) {
  if (s_rx_head == s_rx_tail) return -1;
  uint8_t b  = s_rx_ring[s_rx_tail];
  s_rx_tail  = (uint16_t)((s_rx_tail + 1u) & (CONSOLE_RX_RING_SZ - 1u));
  return (int)b;
}

// USART1 TX ring — drained by USART1_IRQHandler (TXE). Console output MUST NOT block the main
// loop: `pedals_update()` runs last and needs a 10 ms cadence, and a blocking HAL_UART_Transmit
// costs ~5.5 ms for a `live` line and ~10.5 ms for an `outputs` line at 115200 8E1. With both
// telemetry streams on that was over half of every second spent blocked — the loop starved, the
// pedal stream collapsed, the wheelbase fell back to analog and gear changes arrived a minute
// late. Everything goes through this ring now; the ISR shifts bytes out in the background.
#define CONSOLE_TX_RING_SZ 2048u // power of two -> mask instead of modulo
static volatile uint8_t  s_tx_ring[CONSOLE_TX_RING_SZ];
static volatile uint16_t s_tx_head; // advanced by callers
static volatile uint16_t s_tx_tail; // advanced by the ISR

static inline uint16_t console_tx_used(void) {
  return (uint16_t)((s_tx_head - s_tx_tail) & (CONSOLE_TX_RING_SZ - 1u));
}

// Returns the number of bytes actually queued — short means the ring filled.
static uint32_t console_tx_push(const char *buf, uint32_t len) {
  uint32_t n = 0;
  for (; n < len; n++) {
    uint16_t next = (uint16_t)((s_tx_head + 1u) & (CONSOLE_TX_RING_SZ - 1u));
    if (next == s_tx_tail) break; // full
    s_tx_ring[s_tx_head] = (uint8_t)buf[n];
    s_tx_head            = next;
  }
  if (n) __HAL_UART_ENABLE_IT(&huart1, UART_IT_TXE);
  return n;
}

void Error_Handler(void);

//--------------------------------------------------------------------+
// Clock tree
//--------------------------------------------------------------------+
// SYSCLK = HSI 64 MHz (keeps us clear of VOS0/overdrive and flash-latency tuning).
// USB    = 48 MHz from PLL3Q, fed by the 25 MHz HSE crystal:
//          25 MHz / M=5 = 5 MHz -> xN=48 -> 240 MHz VCO -> /Q=5 = 48 MHz.
// An accurate crystal-derived 48 MHz is mandatory for host mode (HSI48 + CRS can't
// trim without an upstream SOF, which a host never receives).
static void SystemClock_Config(void) {
  RCC_OscInitTypeDef       osc    = {0};
  RCC_ClkInitTypeDef       clk    = {0};
  RCC_PeriphCLKInitTypeDef periph = {0};

  // STM32H7: the core-supply source MUST be configured before touching the voltage scaling.
  // Skip it and PWR_FLAG_VOSRDY never asserts, hanging the firmware here forever — before USB
  // (or anything) starts. This silent hang blocked the entire bring-up. WeAct/EC-Buying H743
  // core boards power VCORE from the internal LDO.
  HAL_PWREx_ConfigSupply(PWR_LDO_SUPPLY);

  // VOS1 supports up to 400 MHz — ample for 64 MHz core + 48 MHz USB.
  __HAL_PWR_VOLTAGESCALING_CONFIG(PWR_REGULATOR_VOLTAGE_SCALE1);
  {
    uint32_t t0 = HAL_GetTick();
    while (!__HAL_PWR_GET_FLAG(PWR_FLAG_VOSRDY)) {
      if (HAL_GetTick() - t0 > 100u) Error_Handler(); // fast-blink rather than hang silently
    }
  }

  osc.OscillatorType      = RCC_OSCILLATORTYPE_HSE | RCC_OSCILLATORTYPE_HSI;
  osc.HSEState            = RCC_HSE_ON;    // 25 MHz crystal -> PLL3 -> USB
  osc.HSIState            = RCC_HSI_DIV1;  // 64 MHz (HSIState encodes the divider on H7) -> SYSCLK
  osc.HSICalibrationValue = RCC_HSICALIBRATION_DEFAULT;
  osc.PLL.PLLState        = RCC_PLL_NONE; // PLL1 unused
  if (HAL_RCC_OscConfig(&osc) != HAL_OK) Error_Handler();

  // All PLLs share one input mux; force it to HSE so PLL3 (below) runs off the crystal.
  __HAL_RCC_PLL_PLLSOURCE_CONFIG(RCC_PLLSOURCE_HSE);

  clk.ClockType = RCC_CLOCKTYPE_SYSCLK | RCC_CLOCKTYPE_HCLK | RCC_CLOCKTYPE_D1PCLK1 |
                  RCC_CLOCKTYPE_PCLK1 | RCC_CLOCKTYPE_PCLK2 | RCC_CLOCKTYPE_D3PCLK1;
  clk.SYSCLKSource   = RCC_SYSCLKSOURCE_HSI;
  clk.SYSCLKDivider  = RCC_SYSCLK_DIV1;
  clk.AHBCLKDivider  = RCC_HCLK_DIV1;
  clk.APB3CLKDivider = RCC_APB3_DIV1;
  clk.APB1CLKDivider = RCC_APB1_DIV1;
  clk.APB2CLKDivider = RCC_APB2_DIV1;   // PCLK2 = 64 MHz -> USART1 kernel clock (default sel)
  clk.APB4CLKDivider = RCC_APB4_DIV1;
  if (HAL_RCC_ClockConfig(&clk, FLASH_LATENCY_1) != HAL_OK) Error_Handler();

  periph.PeriphClockSelection = RCC_PERIPHCLK_USB;
  periph.UsbClockSelection    = RCC_USBCLKSOURCE_PLL3;
  periph.PLL3.PLL3M           = 5;
  periph.PLL3.PLL3N           = 48;
  periph.PLL3.PLL3P           = 2;
  periph.PLL3.PLL3Q           = 5; // 240 MHz / 5 = 48 MHz USB
  periph.PLL3.PLL3R           = 2;
  periph.PLL3.PLL3RGE         = RCC_PLL3VCIRANGE_2;  // 4..8 MHz PLL3 input
  periph.PLL3.PLL3VCOSEL      = RCC_PLL3VCOWIDE;     // 192..836 MHz VCO
  periph.PLL3.PLL3FRACN       = 0;
  if (HAL_RCCEx_PeriphCLKConfig(&periph) != HAL_OK) Error_Handler();

  // Power the USB transceiver supply rail (VDD33USB) detector.
  HAL_PWREx_EnableUSBVoltageDetector();
}

//--------------------------------------------------------------------+
// USB pins / clocks / NVIC — both controllers
//--------------------------------------------------------------------+
static void usb_hw_init(void) {
  GPIO_InitTypeDef g = {0};

  __HAL_RCC_GPIOA_CLK_ENABLE();
  __HAL_RCC_GPIOB_CLK_ENABLE();

  // OTG_FS host: PA11 = DM, PA12 = DP (AF10). VBUS sensing is off by default on H7
  // (GCCFG.VBDEN resets to 0 -> session always valid), so the host won't gate on a VBUS pin
  // and PA9 stays free for USART1.
  g.Pin       = GPIO_PIN_11 | GPIO_PIN_12;
  g.Mode      = GPIO_MODE_AF_PP;
  g.Pull      = GPIO_NOPULL;
  g.Speed     = GPIO_SPEED_FREQ_VERY_HIGH;
  g.Alternate = GPIO_AF10_OTG2_FS;
  HAL_GPIO_Init(GPIOA, &g);

  // OTG_HS device (CDC console): PB14 = DM, PB15 = DP (AF12, embedded FS PHY). Harmless on
  // boards where these pins are unwired (FK743M3) — the device stack just never enumerates.
  g.Pin       = GPIO_PIN_14 | GPIO_PIN_15;
  g.Alternate = GPIO_AF12_OTG1_FS;
  HAL_GPIO_Init(GPIOB, &g);

  // Peripheral clocks. Both controllers use their embedded FS PHY, so keep the ULPI clock
  // gated in low-power mode (H7 errata: leaving it on breaks FS USB after WFI/WFE).
  __HAL_RCC_USB2_OTG_FS_CLK_ENABLE();
  __HAL_RCC_USB1_OTG_HS_CLK_ENABLE();
  __HAL_RCC_USB1_OTG_HS_ULPI_CLK_SLEEP_DISABLE();

  HAL_NVIC_SetPriority(OTG_FS_IRQn, 6, 0);
  HAL_NVIC_EnableIRQ(OTG_FS_IRQn);
  HAL_NVIC_SetPriority(OTG_HS_IRQn, 6, 0);
  HAL_NVIC_EnableIRQ(OTG_HS_IRQn);
}

//--------------------------------------------------------------------+
// Fallback console: USART1 on PA9 (TX) / PA10 (RX), 115200 8N1
//--------------------------------------------------------------------+
static void console_init(void) {
  GPIO_InitTypeDef g = {0};

  __HAL_RCC_GPIOA_CLK_ENABLE();
  __HAL_RCC_USART1_CLK_ENABLE();

  g.Pin       = GPIO_PIN_9 | GPIO_PIN_10; // PA9 = USART1_TX, PA10 = USART1_RX
  g.Mode      = GPIO_MODE_AF_PP;
  g.Pull      = GPIO_PULLUP;              // idle-high line
  g.Speed     = GPIO_SPEED_FREQ_HIGH;
  g.Alternate = GPIO_AF7_USART1;
  HAL_GPIO_Init(GPIOA, &g);

  huart1.Instance                    = USART1;
  huart1.Init.BaudRate               = 115200;
  // 8E1 — even parity (WORDLENGTH_9B = 8 data + parity bit). Matches the ROM bootloader's fixed
  // USART1 format (AN2606), so ONE S3-bridge config (8E1) carries both the console JSON/CLI AND
  // UART-bootloader flashing through the same COM port. Parity is transparent to the byte stream.
  huart1.Init.WordLength             = UART_WORDLENGTH_9B;
  huart1.Init.StopBits               = UART_STOPBITS_1;
  huart1.Init.Parity                 = UART_PARITY_EVEN;
  huart1.Init.Mode                   = UART_MODE_TX_RX;
  huart1.Init.HwFlowCtl              = UART_HWCONTROL_NONE;
  huart1.Init.OverSampling           = UART_OVERSAMPLING_16;
  huart1.Init.OneBitSampling         = UART_ONE_BIT_SAMPLE_DISABLE;
  huart1.Init.ClockPrescaler         = UART_PRESCALER_DIV1;
  huart1.AdvancedInit.AdvFeatureInit = UART_ADVFEATURE_NO_INIT;
  if (HAL_UART_Init(&huart1) != HAL_OK) Error_Handler();

  // Enable the 16-byte hardware RX FIFO. Without it the single RDR overruns: while the USB host
  // ISRs (4 devices) preempt the lower-priority RX ISR, back-to-back inbound bytes are lost
  // (measured: ~80% loss on bursts, but spaced bytes fine). The FIFO buffers a burst until the ISR
  // drains it. (TX FIFO too — harmless.)
  HAL_UARTEx_SetRxFifoThreshold(&huart1, UART_RXFIFO_THRESHOLD_1_8);
  HAL_UARTEx_SetTxFifoThreshold(&huart1, UART_TXFIFO_THRESHOLD_1_8);
  HAL_UARTEx_EnableFifoMode(&huart1);

  // Inbound path: the RXFNE interrupt feeds s_rx_ring (register-level, not HAL's fixed-length
  // Receive_IT state machine — a console has no fixed length). Priority ABOVE USB (USB=6) so the
  // short RX ISR preempts host servicing and drains the FIFO promptly — otherwise long command
  // bursts (>16 B, the FIFO depth) overflow while a USB ISR holds the core. The ISR is a few
  // register reads, so preempting USB for it is harmless.
  __HAL_UART_ENABLE_IT(&huart1, UART_IT_RXNE);
  HAL_NVIC_SetPriority(USART1_IRQn, 5, 0);
  HAL_NVIC_EnableIRQ(USART1_IRQn);
}

static void led_init(void) {
  GPIO_InitTypeDef g = {0};
  __HAL_RCC_GPIOA_CLK_ENABLE();
  __HAL_RCC_GPIOB_CLK_ENABLE();
  __HAL_RCC_GPIOC_CLK_ENABLE();
  __HAL_RCC_GPIOD_CLK_ENABLE();
  __HAL_RCC_GPIOE_CLK_ENABLE();
  g.Mode  = GPIO_MODE_OUTPUT_PP;
  g.Pull  = GPIO_NOPULL;
  g.Speed = GPIO_SPEED_FREQ_LOW;
  for (size_t i = 0; i < HEARTBEAT_COUNT; i++) {
    g.Pin = HEARTBEAT_PINS[i].pin;
    HAL_GPIO_Init(HEARTBEAT_PINS[i].port, &g);
  }
}

//--------------------------------------------------------------------+
// Console printf — mirrored to BOTH the USART1 fallback and the USB CDC console
//--------------------------------------------------------------------+
// Two entry points, one sink: console_printf (intact) and console_event_printf (droppable).
// Both no-op in an ISR — TinyUSB occasionally logs from interrupt context at high verbosity, and
// the CDC leg pumps tud_task(), which must not re-enter. `console_printf` is referenced by
// CFG_TUSB_DEBUG_PRINTF.
//
// Shared sink. `lossy` selects the policy when a sink is congested:
//   false (responses, CLI, banners) — wait for room. These are low-rate and must arrive intact;
//          get_config alone is ~8 KB streamed in chunks and truncating it breaks every client.
//   true  (live/outputs events)     — drop. They are superseded ~30 times a second, so a dropped
//          frame costs nothing, while blocking for one costs the pedal stream its cadence.
// Waiting is bounded either way: a host that stopped reading must never wedge the loop.
static int console_emit(const char *buf, uint32_t len, bool lossy) {
  // 1) USART1 — always present (the universal fallback console), now interrupt-driven.
  if (lossy) {
    // Keep headroom so a burst of events can never squeeze out a command response — and drop
    // ALL-OR-NOTHING. A partially-queued line is malformed JSON on the wire (see the CDC leg for
    // the damage that does). The headroom gate already guarantees room for any current event line
    // (<=320 B vs >=1 KB free), but check explicitly so a longer future line can't be half-queued.
    uint16_t used = console_tx_used();
    if (used < CONSOLE_TX_RING_SZ / 2u && (uint32_t)(CONSOLE_TX_RING_SZ - 1u - used) >= len)
      console_tx_push(buf, len);
  } else if (tud_mounted()) {
    // A client is enumerated on the USB CDC — THAT copy is authoritative, and this leg is only a
    // debug mirror. NEVER apply backpressure here: this ring drains at ~11.5 KB/s, so waiting for
    // room while streaming an 8 KB get_config held the main loop ~720 ms — pedals_update() missed
    // ~70 consecutive frames, the wheelbase's link watchdog declared the stream dead and re-initiated
    // its handshake at 250000 baud, and the resulting framing-error burst got logged as
    // "wheelbase restart?" with no power cycle anywhere near the rig. Mirror what fits, drop the
    // rest; a terminal passively watching USART1 during CDC bursts sees torn lines, which is the
    // acceptable cost.
    console_tx_push(buf, len);
  } else {
    // No CDC client — USART1 IS the console (bring-up, CH340 bridge), so responses must arrive
    // intact: wait for room. Bounded by WALL-CLOCK, not iterations. An iteration count is
    // meaningless here: 20k spins take ~3 ms while draining this ring at 115200 takes ~175 ms, so
    // a count-based guard expired with the ring still full and silently dropped chunks — which
    // truncated get_config (~8 KB, streamed) and made every client time out on connect. One
    // chunk's worth of room appears in ~9 ms, so 50 ms is generous while still bounding a wedged
    // sink.
    uint32_t sent     = 0;
    uint32_t deadline = HAL_GetTick() + 50u;
    while (sent < len) {
      sent += console_tx_push(buf + sent, len - sent);
      if (sent >= len || (int32_t)(HAL_GetTick() - deadline) >= 0) break;
    }
  }

  // 2) USB CDC on OTG_FS (the on-board USB-C). Gate on tud_mounted() — "the host has enumerated
  //    us" — NOT tud_cdc_connected(), which ALSO requires the host to assert DTR. webconfig
  //    deliberately DEASSERTS DTR on connect (so opening the port can't reset an MCU sitting behind
  //    a CH340 bridge), and plenty of serial terminals never raise it either. Gating output on DTR
  //    made the firmware happily receive and execute commands but never transmit the reply, so
  //    every webconfig request died with "command timeout". Drain with backpressure so large
  //    multi-chunk responses (get_config ~8 KB) aren't truncated: write what fits, then flush +
  //    pump tud_task() to let the host drain the FIFO, and retry.
  //
  //    Also wall-clock bounded, and for the same reason: pumps elapse in microseconds while the
  //    host only polls the endpoint once a frame (1 ms at full speed), so a "consecutive stalls"
  //    counter bails long before the host has had any chance to drain. 20 ms is ~20 poll windows.
  //    The old guard was 1000 TOTAL iterations, which let a host that had stopped draining (port
  //    closed while still enumerated — a shut browser tab, an exited script) burn 1000 tud_task()
  //    calls on EVERY line: the second half of the starvation described above.
  if (tud_mounted()) {
    if (lossy) {
      // ALL-OR-NOTHING, checked before a single byte is written. This used to write what fit and
      // bail when the FIFO filled, which put a TRUNCATED event line on the wire — and the next
      // intact command response was appended straight onto it, so the client could parse NEITHER.
      // Field failure (2026-07-21): `{"event":"outputs",...,"shift_up":fals` glued to
      // `{"ok":true}` swallowed a reply, and because the clients match replies strictly FIFO, the
      // whole reply stream went permanently one-behind — every later request timed out and every
      // reply arrived "stale". A dropped event costs nothing (superseded ~30x/s); a partial one
      // poisons the stream. The FIFO is 2048 B (tusb_config.h) and event lines are <=320 B, so a
      // whole line always fits once the host drains.
      if (tud_cdc_write_available() >= len) {
        tud_cdc_write(buf, len);
        tud_cdc_write_flush();
      }
    } else {
      uint32_t sent     = 0;
      uint32_t deadline = HAL_GetTick() + 20u;
      while (sent < len) {
        uint32_t avail = tud_cdc_write_available();
        if (avail) {
          uint32_t w = (len - sent < avail) ? (len - sent) : avail;
          tud_cdc_write(buf + sent, w);
          sent += w;
          tud_cdc_write_flush();
        } else {
          tud_cdc_write_flush();
          tud_task(); // service the device so the host can empty the FIFO
          if ((int32_t)(HAL_GetTick() - deadline) >= 0) break;
        }
      }
    }
  }
  return (int)len;
}

int console_printf(const char *fmt, ...) {
  if (__get_IPSR() != 0u) return 0;
  char    buf[256];
  va_list ap;
  va_start(ap, fmt);
  int n = vsnprintf(buf, sizeof(buf), fmt, ap);
  va_end(ap);
  if (n <= 0) return n;
  return console_emit(buf, (n < (int)sizeof(buf)) ? (uint32_t)n : (sizeof(buf) - 1), false);
}

// Emits a pre-built string on the droppable path. Callers that assemble a line in pieces MUST use
// this rather than several console_event_printf() calls: each call drops independently, so a
// partially-dropped line would put malformed JSON on the wire.
int console_event_write(const char *s) {
  if (__get_IPSR() != 0u) return 0;
  return console_emit(s, (uint32_t)strlen(s), true);
}

// Same sink, drop-on-congestion. For the telemetry events ONLY (protocol.c) — see console_emit.
int console_event_printf(const char *fmt, ...) {
  if (__get_IPSR() != 0u) return 0;
  char    buf[256];
  va_list ap;
  va_start(ap, fmt);
  int n = vsnprintf(buf, sizeof(buf), fmt, ap);
  va_end(ap);
  if (n <= 0) return n;
  return console_emit(buf, (n < (int)sizeof(buf)) ? (uint32_t)n : (sizeof(buf) - 1), true);
}

static void print_banner(void) {
  console_printf("\r\n========================================\r\n");
  console_printf("  fanadapter STM32H743 USB host enumerator\r\n");
  console_printf("  host    = OTG_HS / rhport1 (PB14/PB15 header -> powered hub -> devices)\r\n");
  console_printf("  console = USB CDC on OTG_FS (on-board USB-C, also DFU)  +  USART1 PA9/PA10 8E1\r\n");
  console_printf("  CLI: 'dfu' -> ROM bootloader (hands-free flash) | 'reboot' | '?'\r\n");
  console_printf("  core %lu MHz / USB 48 MHz / TinyUSB %d.%d.%d\r\n",
                 (unsigned long)(HAL_RCC_GetSysClockFreq() / 1000000u),
                 TUSB_VERSION_MAJOR, TUSB_VERSION_MINOR, TUSB_VERSION_REVISION);
  // Ground-truth host-channel count from each core's GHWCFG2.NumHstChnl (bits [17:14], value+1).
  // This is THE number every platform decision hinges on — both controllers are now clocked.
  console_printf("  host channels: OTG_FS=%lu  OTG_HS=%lu\r\n",
                 (unsigned long)(((USB2_OTG_FS->GHWCFG2 >> 14) & 0xF) + 1),
                 (unsigned long)(((USB1_OTG_HS->GHWCFG2 >> 14) & 0xF) + 1));
  console_printf("  current mode: OTG_FS=%s  OTG_HS=%s\r\n",
                 (USB2_OTG_FS->GINTSTS & 1u) ? "host" : "device",
                 (USB1_OTG_HS->GINTSTS & 1u) ? "host" : "device");
  console_printf("  waiting for devices on the hub...\r\n");
  console_printf("========================================\r\n");
}

//--------------------------------------------------------------------+
// CDC device callbacks
//--------------------------------------------------------------------+
// Reprint the banner when a terminal opens the USB CDC port (asserts DTR) — a PC that connects
// after boot would otherwise have missed the boot banner (USB TX before connect is discarded).
void tud_cdc_line_state_cb(uint8_t itf, bool dtr, bool rts) {
  (void)itf;
  (void)rts;
  if (dtr) print_banner();
}

//--------------------------------------------------------------------+
// Host callbacks — enumeration printout
//--------------------------------------------------------------------+
static tusb_desc_device_t s_desc_device;
static uint16_t           s_str_buf[128];

static void print_utf16(uint16_t *buf, size_t maxlen) {
  // buf[0] is the descriptor header (len/type); chars follow.
  size_t total = buf[0] & 0xFF;          // bLength in bytes
  size_t count = (total >= 2) ? (total - 2) / 2 : 0;
  if (count > maxlen - 1) count = maxlen - 1;
  for (size_t i = 0; i < count; i++) {
    uint16_t c = buf[1 + i];
    console_printf("%c", (c < 0x80) ? (char)c : '?'); // ASCII-only console
  }
}

void tuh_mount_cb(uint8_t daddr) {
  console_printf("\r\n[+] device attached, address %u\r\n", daddr);

  if (tuh_descriptor_get_device_sync(daddr, &s_desc_device, 18) != XFER_RESULT_SUCCESS) {
    console_printf("    ! failed to read device descriptor\r\n");
    return;
  }

  console_printf("    VID:PID = %04X:%04X  class=%u  USB %x.%02x\r\n",
                 s_desc_device.idVendor, s_desc_device.idProduct, s_desc_device.bDeviceClass,
                 s_desc_device.bcdUSB >> 8, s_desc_device.bcdUSB & 0xFF);

  if (s_desc_device.iManufacturer &&
      tuh_descriptor_get_manufacturer_string_sync(daddr, LANGUAGE_ID, s_str_buf, sizeof(s_str_buf)) ==
          XFER_RESULT_SUCCESS) {
    console_printf("    manufacturer: ");
    print_utf16(s_str_buf, sizeof(s_str_buf) / 2);
    console_printf("\r\n");
  }

  if (s_desc_device.iProduct &&
      tuh_descriptor_get_product_string_sync(daddr, LANGUAGE_ID, s_str_buf, sizeof(s_str_buf)) ==
          XFER_RESULT_SUCCESS) {
    console_printf("    product:      ");
    print_utf16(s_str_buf, sizeof(s_str_buf) / 2);
    console_printf("\r\n");
  }
}

void tuh_umount_cb(uint8_t daddr) {
  console_printf("\r\n[-] device detached, address %u\r\n", daddr);
}

// HID interface mount/unmount/report — claim the interface into the device pool (usb_input),
// which parses its report descriptor and decodes its reports. The structured device_attached /
// device_detached / live events come from protocol_tick polling the pool; the console line here is
// just a human-readable trace. (Stage-0 descriptor dump removed — captured to
// tools/hid_descriptor_capture.txt and folded into hid_parse.c.)
void tuh_hid_mount_cb(uint8_t daddr, uint8_t instance, uint8_t const *report_desc, uint16_t len) {
  uint16_t vid = 0, pid = 0;
  tuh_vid_pid_get(daddr, &vid, &pid);
  console_printf("    HID mounted: addr=%u inst=%u proto=%u %04X:%04X desc_len=%u\r\n", daddr,
                 instance, tuh_hid_interface_protocol(daddr, instance), vid, pid, len);
  if (usb_input_on_mount(daddr, instance, report_desc, len)) {
    // Arm the interrupt pipe. On failure the usb_input_task watchdog re-arms within 250 ms.
    if (!tuh_hid_receive_report(daddr, instance)) {
      console_printf("      ! report pipe arm failed — watchdog will retry\r\n");
    }
  } else {
    // Rejected: pool full, or the descriptor exposes nothing bindable (a composite controller's
    // spare HID interface). Leave the pipe unarmed so it doesn't hold a host channel either.
    console_printf("      -> not claimed (no bindable inputs)\r\n");
  }
}

void tuh_hid_umount_cb(uint8_t daddr, uint8_t instance) {
  console_printf("    HID unmounted: addr=%u inst=%u\r\n", daddr, instance);
  usb_input_on_umount(daddr, instance);
}

void tuh_hid_report_received_cb(uint8_t daddr, uint8_t instance, uint8_t const *report,
                                uint16_t len) {
  usb_input_on_report(daddr, instance, report, len);
  // Re-arm to keep polling. This CAN fail transiently (endpoint claim / host channel allocation),
  // and nothing else reschedules it, so usb_input_task re-arms a dropped pipe within 250 ms.
  // Deliberately NOT logged: this is the hottest path in the loop and console_printf blocks until
  // the sink drains — logging here stalls the loop and collapses the 100 Hz pedal cadence.
  tuh_hid_receive_report(daddr, instance);
}

//--------------------------------------------------------------------+
// Jump to the STM32H7 ROM system bootloader (USART1 8E1 + USB DFU) — AN2606 Table 135
//--------------------------------------------------------------------+
// Hands-free re-flash: token in DTCM + NVIC_SystemReset + branch at the TOP of main(), before any
// clock/peripheral init. The ROM then starts on a chip at reset defaults — the state it actually
// expects — and brings up USB DFU on OTG_FS. Start address per AN2606 = 0x1FF09800.
//
// Why this shape and not a direct jump from the running app: that was bisected twice and the ROM
// serves NO interfaces on a late software branch — not USART (June, FK743M3) and not USB DFU
// (July, this board: clean detach, then permanently dark bus until an RST tap). Only entry from a
// reset-default chip works. The token lives at DTCM base — every app section (.data/.bss/stack)
// lives in AXI SRAM @0x24000000, so startup code never touches it, and DTCM survives
// NVIC_SystemReset (verified June 2026).
#define SYSTEM_BOOTLOADER_ADDR 0x1FF09800u
#define BOOT_TOKEN_ADDR  ((volatile uint32_t *)0x20000000u)
#define BOOT_TOKEN_MAGIC 0xB007F1A5u

// First statement of main(). Chip state here: HSI, caches off, no IRQs armed, SystemInit done —
// nothing the ROM minds.
static void check_bootloader_request(void) {
  if (*BOOT_TOKEN_ADDR != BOOT_TOKEN_MAGIC) return;
  *BOOT_TOKEN_ADDR = 0u; // one-shot: the next reset boots the app normally
  __DSB();
  SCB->VTOR = SYSTEM_BOOTLOADER_ADDR;
  __set_MSP(*(volatile uint32_t *)SYSTEM_BOOTLOADER_ADDR);
  ((void (*)(void))(*(volatile uint32_t *)(SYSTEM_BOOTLOADER_ADDR + 4u)))();
  while (1) {
  } // never returns
}

// Non-static: also reachable as the JSON command {"cmd":"dfu"} (protocol.c), which is how
// webconfig's firmware flasher enters the bootloader without the user finding a console.
void request_bootloader_reboot(void) {
  // Detach cleanly so the host drops the CDC port before the reset, not on a timeout after it.
  tud_disconnect();
  HAL_Delay(100);
  *BOOT_TOKEN_ADDR = BOOT_TOKEN_MAGIC;
  __DSB();
  NVIC_SystemReset();
}

//--------------------------------------------------------------------+
// Bring-up CLI — line-based, drained from the USART1 RX ring (later also the CDC console)
//--------------------------------------------------------------------+
// M1 folds JSON line-commands ('{...}') into this same path; for now it serves the few bring-up
// verbs we need — most importantly 'dfu' to enter the ROM bootloader for a hands-free re-flash.
static char     s_line[256]; // room for the largest JSON command (set_binding etc.)
static uint16_t s_line_len;
static bool     s_line_json; // first char was '{' -> protocol line: do NOT echo (would corrupt the wire)

static void cli_dispatch(const char *line) {
  if (line[0] == '{') { // JSON command line -> the WebSerial protocol
    protocol_handle_line(line);
    return;
  }
  if (strcmp(line, "dfu") == 0 || strcmp(line, "bootloader") == 0) {
    console_printf("\r\n[bootloader] reset -> ROM bootloader (USB DFU on the USB-C)...\r\n");
    for (volatile uint32_t d = 0; d < 400000u; d++) __NOP(); // let the line + any CDC FIFO drain
    request_bootloader_reboot();
  } else if (strcmp(line, "reboot") == 0) {
    console_printf("\r\n[reboot] NVIC_SystemReset\r\n");
    for (volatile uint32_t d = 0; d < 400000u; d++) __NOP();
    NVIC_SystemReset();
  } else if (strcmp(line, "?") == 0) {
    print_banner();
  } else {
    console_printf("\r\n? unknown: '%s' (try: dfu, reboot, ?)\r\n", line);
  }
}

// One inbound console byte -> the line assembler. Shared by BOTH inbound sources (the USART1 RX
// ring and the USB CDC console) so a command works identically from either.
static void console_feed_byte(int c) {
  if (c == '\r' || c == '\n') {
    if (s_line_len) {
      s_line[s_line_len] = '\0';
      cli_dispatch(s_line);
      s_line_len = 0;
    }
    s_line_json = false;
  } else if (c == 0x08 || c == 0x7F) { // backspace / delete
    if (s_line_len) {
      s_line_len--;
      if (!s_line_json) console_printf("\b \b");
    }
  } else if (s_line_len < sizeof(s_line) - 1) {
    if (s_line_len == 0 && c == '{') s_line_json = true;
    s_line[s_line_len++] = (char)c;
    if (!s_line_json) console_printf("%c", (char)c); // echo interactive typing (not protocol lines)
  }
}

static void console_cli_poll(void) {
  // 1) USART1 RX ring — the universal fallback console.
  while (console_rx_available()) {
    int c = console_read_byte();
    if (c < 0) break;
    console_feed_byte(c);
  }

  // 2) USB CDC console (OTG_FS, the on-board USB-C). WITHOUT THIS the CDC is transmit-only:
  //    console_printf() writes to it, but commands sent from webconfig/WebSerial are read by
  //    nobody, so every request times out ("command timeout: version"). The gap went unnoticed
  //    through the whole M0-M7 bring-up because the FK743M3 board's second USB controller was
  //    unusable (PB15 a dead via) — the CDC never enumerated, so this inbound half was never
  //    exercised until a board with two working USB controllers showed up.
  while (tud_cdc_available()) {
    uint8_t b;
    if (tud_cdc_read(&b, 1) != 1) break;
    console_feed_byte((int)b);
  }
}

//--------------------------------------------------------------------+
// Interrupt handlers
//--------------------------------------------------------------------+
// rhport is fixed by hardware regardless of role: OTG_FS = rhport 0, OTG_HS = rhport 1.
void OTG_FS_IRQHandler(void) { tusb_int_handler(OTGFS_RHPORT, true); }
void OTG_HS_IRQHandler(void) { tusb_int_handler(OTGHS_RHPORT, true); }

// Inbound console bytes (USART1 RXNE) -> ring buffer. Drops on overflow rather than clobbering
// unread data. Error flags (ORE/FE/NE/PE) are cleared so a glitch can't wedge the RXNE.
void USART1_IRQHandler(void) {
  // TX: feed the FIFO from the ring while it has room, then disable the interrupt once drained
  // (TXE stays asserted forever otherwise and this ISR would spin).
  if (__HAL_UART_GET_IT_SOURCE(&huart1, UART_IT_TXE)) {
    while (__HAL_UART_GET_FLAG(&huart1, UART_FLAG_TXE)) {
      if (s_tx_head == s_tx_tail) {
        __HAL_UART_DISABLE_IT(&huart1, UART_IT_TXE);
        break;
      }
      huart1.Instance->TDR = s_tx_ring[s_tx_tail];
      s_tx_tail            = (uint16_t)((s_tx_tail + 1u) & (CONSOLE_TX_RING_SZ - 1u));
    }
  }

  // Drain the whole RX FIFO each entry (RXFNE stays set while the FIFO has data).
  while (__HAL_UART_GET_FLAG(&huart1, UART_FLAG_RXNE)) {
    uint8_t  b    = (uint8_t)(huart1.Instance->RDR & 0xFFu);
    uint16_t next = (uint16_t)((s_rx_head + 1u) & (CONSOLE_RX_RING_SZ - 1u));
    if (next != s_rx_tail) {
      s_rx_ring[s_rx_head] = b;
      s_rx_head            = next;
    }
  }
  __HAL_UART_CLEAR_FLAG(&huart1, UART_CLEAR_OREF | UART_CLEAR_FEF | UART_CLEAR_NEF | UART_CLEAR_PEF);
}

void SysTick_Handler(void) { HAL_IncTick(); }

// TinyUSB 0.18 time source (host enumeration delays depend on it).
uint32_t tusb_time_millis_api(void) { return HAL_GetTick(); }

//--------------------------------------------------------------------+
// main
//--------------------------------------------------------------------+
int main(void) {
  check_bootloader_request(); // MUST be first: branches to the ROM bootloader on a 'dfu' reboot
  HAL_Init();
  led_init();           // configure heartbeat pins first so Error_Handler can signal a clock failure
  SystemClock_Config();
  usb_hw_init();
  console_init();
  usb_input_init(); // register the USB HID device pool as the input source (USB devices in standalone mode)
  protocol_init();
  mapping_init();   // load Config from flash (or seed defaults on magic/version/CRC mismatch)
  outputs_init();   // DAC1 on PA4/PA5 for the H-pattern X/Y; snaps to neutral (needs gearOut[])
  pedals_init();    // CSL Elite pedal-port UART emulator on USART2 (PA2/PA3) — the throttle/brake/
                    // clutch/handbrake sink. Arms a 2 s warmup, then drives the handshake.

  tusb_rhport_init_t host_init = {.role = TUSB_ROLE_HOST, .speed = TUSB_SPEED_FULL};
  tusb_rhport_init_t dev_init  = {.role = TUSB_ROLE_DEVICE, .speed = TUSB_SPEED_FULL};
  tusb_rhport_init(HOST_RHPORT, &host_init); // USB host on rhport 0 = OTG_FS = USB-C / A11-A12
  tusb_rhport_init(DEVICE_RHPORT, &dev_init); // CDC console on rhport 1 = OTG_HS = PB14/PB15

  print_banner();       // to USART1 now; the CDC console reprints it on DTR connect

  uint32_t last_blink = 0;
  for (;;) {
    tuh_task(); // service the USB host (enumeration, HID polling)
    tud_task(); // service the USB CDC console device

    console_cli_poll(); // drain inbound console -> CLI (dfu/reboot/?) + JSON protocol commands

    uint32_t now = HAL_GetTick();
    usb_input_task(now); // interrupt-pipe watchdog: re-arm any claimed slot whose IN pipe went idle
    mapping_tick();     // read inputs, evaluate bindings, drive DAC/sequential/handbrake-PWM + refresh pedal levels
    protocol_tick(now); // emit device attach/detach + rate-limited live events when streaming
    pedals_update();    // CSL Elite UART handshake + 100 Hz pedal stream. LAST: it relies on the
                        // levels mapping_tick() just set, and blocking work ahead of it slips cadence.

    if (now - last_blink >= 1000u) { // slow 0.5 Hz heartbeat
      last_blink = now;
      for (size_t i = 0; i < HEARTBEAT_COUNT; i++)
        HAL_GPIO_TogglePin(HEARTBEAT_PINS[i].port, HEARTBEAT_PINS[i].pin);
    }
  }
}

void Error_Handler(void) {
  // Fast (~10 Hz) blink — distinct from the slow main-loop heartbeat — means we failed
  // before the main loop, almost always SystemClock_Config (e.g. the HSE crystal not starting).
  for (;;) {
    for (size_t i = 0; i < HEARTBEAT_COUNT; i++)
      HAL_GPIO_TogglePin(HEARTBEAT_PINS[i].port, HEARTBEAT_PINS[i].pin);
    for (volatile uint32_t d = 0; d < 2000000u; d++) {
      __NOP();
    }
  }
}

#ifdef USE_FULL_ASSERT
void assert_failed(uint8_t *file, uint32_t line) {
  (void)file;
  (void)line;
}
#endif
