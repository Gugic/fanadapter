#ifndef TUSB_CONFIG_H_
#define TUSB_CONFIG_H_

#ifdef __cplusplus
extern "C" {
#endif

#ifndef CFG_TUSB_MCU
#error CFG_TUSB_MCU must be defined
#endif

//--------------------------------------------------------------------
// Common
//--------------------------------------------------------------------
#define CFG_TUSB_OS            OPT_OS_NONE

// Verbosity: 0=off 1=err 2=warn/info 3=verbose. Debug routes to console_printf, which
// ALSO writes to the USB CDC console — so bumping this >0 risks a feedback storm
// (debug log -> CDC write -> more USB activity -> more debug log). Keep at 0; if you need
// enumeration tracing, temporarily point CFG_TUSB_DEBUG_PRINTF at a UART-only writer.
#define CFG_TUSB_DEBUG         0
extern int console_printf(const char *fmt, ...);
#define CFG_TUSB_DEBUG_PRINTF  console_printf

// Two USB controllers, two roles (dwc2 maps rhport 0 = OTG_FS, rhport 1 = OTG_HS on STM32H7):
//   rhport 0 = OTG_FS (PA11/PA12 = USB-C / A11-A12 header) -> USB HOST   = hub + wheel devices
//   rhport 1 = OTG_HS (PB14/PB15)                          -> USB DEVICE = CDC serial console
// The CDC console is the recommended path for open-source boards where BOTH USB ports are
// broken out: plug a second cable into the OTG_HS port and the device list shows up as a COM
// port — no UART bridge needed. On boards where OTG_HS is unwired (like the FK743M3 used for
// bring-up, where PB15 is a dead via) the CDC stack just sits dormant and the parallel USART1
// console (PA9/PA10, see main.c) carries the same output. Host stays on OTG_FS so that the
// single-controller boards keep working.
#define CFG_TUH_RHPORT         0
#define CFG_TUSB_RHPORT0_MODE  (OPT_MODE_HOST   | OPT_MODE_FULL_SPEED)
#define CFG_TUSB_RHPORT1_MODE  (OPT_MODE_DEVICE | OPT_MODE_FULL_SPEED)

#ifndef CFG_TUSB_MEM_SECTION
#define CFG_TUSB_MEM_SECTION
#endif
#ifndef CFG_TUSB_MEM_ALIGN
#define CFG_TUSB_MEM_ALIGN     __attribute__((aligned(4)))
#endif

//--------------------------------------------------------------------
// Device: CDC serial console on OTG_HS / rhport 1 (PB14/PB15)
//--------------------------------------------------------------------
#define CFG_TUD_ENABLED        1
#define CFG_TUD_ENDPOINT0_SIZE 64

#define CFG_TUD_CDC            1
#define CFG_TUD_MSC            0
#define CFG_TUD_HID            0
#define CFG_TUD_MIDI           0
#define CFG_TUD_VENDOR         0

#define CFG_TUD_CDC_RX_BUFSIZE 512
#define CFG_TUD_CDC_TX_BUFSIZE 2048   // roomy so log lines aren't dropped when the PC is slow
#define CFG_TUD_CDC_EP_BUFSIZE 64

//--------------------------------------------------------------------
// Host (hub + HID on OTG_FS / rhport 0)
//--------------------------------------------------------------------
#define CFG_TUH_ENABLED            1
#define CFG_TUH_MAX_SPEED          OPT_MODE_FULL_SPEED

#define CFG_TUH_ENUMERATION_BUFSIZE 256

#define CFG_TUH_HUB                1   // one external hub
#define CFG_TUH_DEVICE_MAX         8   // wheel devices behind the hub (headroom over the 4 we need)

#define CFG_TUH_HID                8   // total HID *interfaces* across all devices (pool, not per-device).
                                       // Composite wheels expose 2+ each, so 4 devices need ~7-8; at the
                                       // old value of 4 the 3rd/4th device's HID interfaces never mounted.
                                       // Each mounted interface keeps an interrupt-IN channel armed, so
                                       // this must stay under the 16 host channels (8 + control + hub ~= 10).
#define CFG_TUH_HID_EPIN_BUFSIZE   64
#define CFG_TUH_HID_EPOUT_BUFSIZE  64

#ifdef __cplusplus
}
#endif

#endif // TUSB_CONFIG_H_
