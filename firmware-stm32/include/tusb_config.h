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
//   rhport 0 = OTG_FS (PA11/PA12 = the on-board USB-C) -> USB DEVICE = CDC console + webconfig
//   rhport 1 = OTG_HS (PB14/PB15 header)               -> USB HOST   = powered hub + wheel devices
// Why this way round: the on-board USB-C is wired as a DEVICE receptacle (UFP, CC pulled down with
// Rd) — that's why the ROM DFU enumerates through it. A USB-C hub plugged into it never attaches,
// because both ends present as devices. So the USB-C serves the PC (console + webconfig + DFU +
// board power, one pre-soldered connector, no adapter), and the host hangs off the PB14/PB15
// header as a raw D+/D-/GND breakout straight to a self-powered hub — no CC negotiation involved.
// Bonus: DFU (fixed to OTG_FS in silicon) no longer shares a port with the hub, so re-flashing
// doesn't mean unplugging the wheels.
#define CFG_TUH_RHPORT         1
#define CFG_TUSB_RHPORT0_MODE  (OPT_MODE_DEVICE | OPT_MODE_FULL_SPEED)
#define CFG_TUSB_RHPORT1_MODE  (OPT_MODE_HOST   | OPT_MODE_FULL_SPEED)

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

// Host event queue. The DEFAULT (16) LOSES DEVICES: hcd events (xfer complete, attach...) are
// queued from the ISR and consumed by tuh_task() in the main loop; when the queue is full,
// queue_event()'s TU_ASSERT silently DROPS the event (usbh.c:300, release build). An endpoint's
// busy flag is cleared only when its completion event is *processed* (usbh.c:553), so a dropped
// completion leaves that pipe busy FOREVER: the device stops being polled, its inputs freeze at
// their last values, and only re-enumeration recovers it. The Simnet pedal streams ~450
// reports/s, filling 16 slots in ~35 ms — any main-loop stall longer than that (a connect-time
// get_config is ~400 ms of CDC streaming) rolled these dice, which is why "SimHub connects and
// the pedals die" kept happening and why it was always the pedal, never the near-silent
// shifters. 512 covers a >1.1 s stall at that rate (~8 KB of RAM out of 1 MB). The one stall
// that can still exceed it is save_config's IRQ-off flash erase — but with IRQs off nothing is
// queued at all, and the pedal-stream gap it causes is already documented.
#define CFG_TUH_TASK_QUEUE_SZ      512

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
