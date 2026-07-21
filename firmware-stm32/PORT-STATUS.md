# STM32H743 port — development notes

Engineering record for the Teensy → STM32 firmware port: what's implemented, what was hard, and
what not to re-derive. **Read this before changing firmware code.**

**If you just want to build or wire one, you're in the wrong file — see
[`README.md`](README.md).** Board choice, pin map, wiring, flashing and troubleshooting all live
there. This file deliberately doesn't repeat them.

## Status

**DONE — full feature parity with the Teensy build, hardware-validated end to end**, including a
real driving session on a ClubSport DD+. USB-host HID decode, mapping, DAC H-pattern gears,
sequential, handbrake, and the CSL Elite pedal-UART stream all drive the wheelbase, with webconfig
on the board's native USB CDC.

Current hardware: **WeAct MiniSTM32H743VITX** (STM32H743VIT6, 2 MB flash / 1 MB RAM, 25 MHz HSE,
LDO VCORE), both USB controllers live at once. Bring-up happened on a different board — see
[Historical](#historical-fk743m3-bring-up) at the end.

## Milestones (M0–M7, all hardware-validated)

- **M0 — plumbing:** USART1 RX (interrupt + 16-byte FIFO, ISR-drained, **priority 5 = above USB**
  so command bursts don't overrun); console at **8E1** to match the ROM bootloader.
- **M1 — HID decode + protocol:** generic report-descriptor walker (`hid_parse.c`), 8-slot device
  pool (`usb_input.c`), the `InputSource` vtable (`input_source.c` — the cornerstone), and
  `protocol.c` with shapes matched to webconfig. Verified against 4 devices / 7 HID interfaces.
- **Axis fidelity:** `hid_parse.c` stores the **raw native** logical value (matching the Teensy's
  `m_axes[i] = (uint16_t)value`) — a 10-bit handbrake reads 0..1023, a 12-bit pedal 0..4095, clean
  step-of-1. An earlier version normalized to 0..65535 and produced confusing 16/64-count jumps.
  `scaleAxis` maps `[rawMin,rawMax]` → 0..65535 at eval time.
- **M2 — Config + flash + get/set/save/reset:** `mapping.{c,h}` (Config byte-identical to the
  Teensy, **1132 B, CONFIG_VERSION 3**, `_Static_assert`-locked; `crc32` verbatim from
  `webconfig/src/lib/crc32.ts`), `json_min.{c,h}` (zero-alloc JSON reader replacing ArduinoJson),
  `config_store.{c,h}`. `get_config` streams in small chunks (USART1 blocks until sent, so no
  truncation). **Two flash bugs here cost a bench session — see below.**
- **M3 — DAC H-pattern:** `outputs.{c,h}` — **DAC1 ch1 = PA4 (X), ch2 = PA5 (Y)**, codes map 1:1
  to the schematics voltage table (output buffer on). Non-blocking neutral-transit FSM replaces the
  Teensy's `delay(50)`. Sequential pulse landed here too: open-drain **PC6/PC7**, idle HIGH,
  non-blocking FSM.
- **M4 — direct output control** (replaced an earlier "serial virtual-device inject" design): the
  PC (SimHub / the web app) owns the mapping and commands outputs directly. `set_gear`,
  `set_outputs` (the 100 Hz hot path), `pulse_shift`, `release_outputs`. Semantics = **command
  override**: a PC-set channel wins over the USB mapping, sticky until `release_outputs`. State in
  `mapping.c` (`g_ovr_*`). Standalone mode is unchanged.
- **M5 — handbrake PWM fallback:** TIM3_CH3 on **PC8** (AF2), ARR=4095 / PSC=0 → ~15.6 kHz for an
  RC low-pass. Init is non-fatal — a TIM fault must not brick the USB host. This is the *fallback*;
  the primary path is the pedal stream, and `mapping.c` dual-writes both.
- **M6 — CSL Elite pedal UART (the keystone):** `pedals.{c,h}` on **USART2 (PA2/PA3), 8N1** — a
  faithful port of the Teensy state machine (INIT→STEP0→STEP1→STEP2→STREAMING), same handshake
  bytes (0x0A→0x1A, 0x05→0x15), 250000↔115200 switch, 2 s warmup drain, lenient any-order STEP2
  collector, CRC-8 (poly 0x8C), 100 Hz 12-byte `0x7B..0x7D` frames. RX is a register-level RXNE
  ring (priority 5, above USB); TX is blocking `HAL_UART_Transmit` (12 B ≈ 1 ms at 115200, gated to
  100 Hz — the proven Teensy approach). **This is what makes `set_outputs` and USB-mapped pedals
  electrically reach the wheelbase** — before M6 those values stopped at the snapshot.
- **M7 — polish:** `test_axis`, `test_pulse`, `reboot`, `reset_pedals`, `pedals_status`. All shapes
  match the Teensy and the existing `webconfig/src/lib/serial.ts` wrappers — **no webconfig change
  was needed for the entire port.**

## Bugs already fixed — do not reintroduce

### The clock bug that ate the first session

`SystemClock_Config` **must** call `HAL_PWREx_ConfigSupply(PWR_LDO_SUPPLY)` **before**
`__HAL_PWR_VOLTAGESCALING_CONFIG`. Skip it and `PWR_FLAG_VOSRDY` never sets → the firmware hangs in
that `while` loop forever, before USB ever inits. There's now a timeout that calls `Error_Handler`
(fast LED blink) instead of hanging silently.

### Two flash-store bugs that ate a bench session

1. **The config sector must be in a DIFFERENT flash bank than the running code.** Code runs from
   bank 1; config is **bank 2, sector 0 @ `0x08100000`**. The H7 cannot fetch instructions from a
   bank while it's being erased — a bank-1 config sector **hard-faults the core** on the 128 KB
   erase. (Verified bank 2 is real and not aliased by reading `0x08000000` vs `0x08100000` over the
   ROM bootloader.) `DUAL_BANK` is defined for STM32H743xx, so the HAL unlocks both banks and waits
   on `QW_BANK2`. Erase ~1 s.
2. **Never call `SCB_InvalidateDCache_by_Addr()` with the D-cache DISABLED** (this build never
   enables it). That faulted the core right after a clean erase+program. Removed — with D-cache off,
   memory-mapped flash reads are already coherent. If D-cache is ever enabled, re-add it guarded.

The flash op is wrapped in `__disable_irq()`/`__enable_irq()` — defensive; `save_config` is rare so
the ~1 s USB/console pause is fine.

### Four latent USB bugs, all invisible until a board with two working USB controllers existed

Found the day the WeAct H743VIT6 arrived. The bring-up board had only one usable controller, so the
entire device-side path had never actually run.

1. **CDC RX was never implemented.** `console_cli_poll()` drained only the USART1 ring — there was
   no `tud_cdc_read()` anywhere, despite a comment promising it. The CDC could transmit but never
   read a byte, so every webconfig command vanished. Both sources now feed a shared
   `console_feed_byte()`.
2. **CDC output was gated on DTR.** `console_printf` wrote the CDC only `if (tud_cdc_connected())`,
   which additionally requires the host to assert DTR — and webconfig deliberately **deasserts** it
   (so opening a port can't reset an MCU sitting behind a CH340). Result: the firmware received and
   executed commands but never replied → `command timeout: version`. Now gated on **`tud_mounted()`**,
   which is DTR-independent and also works with terminals that never raise DTR.
3. **USB roles were the wrong way round.** The host cannot live on the on-board USB-C — it's a
   device receptacle. See the README's USB table for the full rationale.
4. **Phantom HID interfaces.** Composite controllers (Logitech RS Shifter & Handbrake, RS H-Shifter)
   each expose a **second HID interface with no axes/buttons/hat/keys**. Those were claimed anyway:
   **4 physical devices filled 7 of the 8 pool slots**, each holding an interrupt-IN host channel.
   `usb_input_on_mount()` now returns whether it claimed the interface and rejects input-less ones
   (the counts come from the report descriptor and are never revised from reports, so such an
   interface can never *become* bindable); `tuh_hid_mount_cb` only arms the pipe when claimed.

### Pedal-UART line integrity (a debug session that was never a code bug)

The pedal port is **unforgiving about wire integrity — suspect the wiring before the firmware.** A
marginal jumper (especially **ground**, or TX PA2 → pedal pin 5) CRC-corrupts the 100 Hz frames; the
wheelbase rejects them and falls back to analog sensing, and pre-fix the adapter kept streaming into
the void → jitter on the now-analog pins. Symptom: handshake completes, pedals show for ~2 s, drop
to analog, repeat. **The fix was re-seating a wire.** The `[pedals]` console trace is the
diagnostic — it showed the adapter reaching `STREAMING` and staying (so the drop was wheelbase-side)
plus a burst of RX framing errors, which is exactly what a flaky line produces.

Which is also why **wheelbase-restart auto-recovery** exists: once STREAMING we ignore RX, so a
power-cycled wheelbase (re-initiating its handshake at 250000 while we're camped at 115200) used to
leave us streaming into a base that had fallen back to analog. The mismatched baud arrives as a
burst of framing/overrun errors; the RX ISR tallies them and STREAMING re-arms the handshake on a
burst (`RESTART_ERR_THRESHOLD = 32`, 1 Hz decay so isolated glitches don't trip it). The Teensy only
ever recovered via the manual re-arm button.

### A device that goes silent but stays "connected" — the dropped report-pipe re-arm

Field symptom (found during SimHub-plugin testing, but client-independent): after a few minutes the
USB pedals stopped delivering input while still listed as connected; every other hub device kept
working; only a reboot recovered it — and only for a few minutes.

The HID report pipe is kept alive **solely** by the `tuh_hid_receive_report()` call at the end of
`tuh_hid_report_received_cb` — TinyUSB (0.18) does not auto-re-arm. That call can fail transiently
(`usbh_edpt_claim` or `usbh_edpt_xfer` → e.g. a host-channel allocation miss), and both call sites
ignored the return value. One dropped re-arm = that interface never polls again, while the pool
slot, `list_devices`, and the mount state all stay healthy. It hits the *pedals* first because
analog axes + ADC noise make them the chattiest device on the hub — orders of magnitude more
transfers than a shifter, so the most exposure to any transient.

Fix: both arm sites log a failure, and `usb_input_task()` (main loop, 250 ms) walks the claimed
pool slots and re-arms any whose interrupt-IN endpoint is idle (`tuh_hid_receive_ready`). The loop
is single-threaded through `tuh_task()`, so "claimed slot, idle pipe" is never a legitimate state —
an armed pipe shows busy even when the device NAKs, which is why this can't false-positive on idle
devices, and also why a *blind* silence-timeout would have been wrong. The `[usb] … report pipe was
dead` console line is the field confirmation of the transient actually firing. Not covered (no
evidence yet): a channel wedged *busy* forever — the watchdog would skip it; if silence recurs with
no watchdog lines, that's the next suspect (needs abort + re-arm, riskier).

## Gotchas — don't re-derive

- **rhport numbers are FIXED BY HARDWARE; roles are not.** OTG_FS is *always* rhport 0, OTG_HS
  *always* rhport 1, so the ISRs must pass those literals — which is why `main.c` keeps
  `OTGFS_RHPORT`/`OTGHS_RHPORT` separate from the role aliases `DEVICE_RHPORT`/`HOST_RHPORT`.
  Current roles: **rhport 0 = DEVICE (console), rhport 1 = HOST.** Wiring an ISR to a *role* alias
  breaks silently the moment roles swap.
- **`CFG_TUH_HID` must be 8, not 4.** With 4, only the first 4 HID *interfaces* mount — the 4-device
  target presents **7** (two composite shifters), so later devices enumerate at the device level but
  never claim. Each mounted interface arms one interrupt-IN channel; 7 of 16 is comfortable.
- **Host VBUS is a non-issue on H7** (verified in the dwc2 source): `GCCFG.VBDEN` resets to 0 (VBUS
  assumed valid), FS-PHY init only sets `PWRDWN`, and `hcd_dwc2.c` writes `HPRT_POWER`. The host
  doesn't gate on a VBUS pin, so PA9 stays free for USART1 and no GCCFG poking is needed. (The *hub*
  still needs to see VBUS on its own upstream — that's a hub-side requirement, not an MCU one.)
- **Feedback-storm risk:** `console_printf` writes the USB CDC, so routing TinyUSB debug to it
  (`CFG_TUSB_DEBUG > 0`) can storm (log → CDC write → USB activity → log). Keep `CFG_TUSB_DEBUG 0`,
  or point `CFG_TUSB_DEBUG_PRINTF` at a UART-only writer when you need enumeration tracing.
- **Don't gate CDC output on `tud_cdc_connected()`** — see bug 2 above. Use `tud_mounted()`. Same
  trap applies to any new inbound path: drain **both** the USART1 ring and `tud_cdc_read()`.
- **The WeAct PCB also ships with an STM32H723VGT6**, which has one USB controller and cannot run
  this firmware. Verify the chip before debugging a "dead" board — full detail in the README.
- **DAC gear outputs take no RC filter.** A cap on the DAC output buffer can oscillate.

## Remaining

Polish only: **DAC gear recalibration** against a specific wheelbase if any column reads off
(`set_gear_dac` per gear, then `save_config`). The defaults engaged every gear cleanly here.

## Historical: FK743M3 bring-up

M0–M7 were developed and first validated on an **FK743M3-VGT6** board, which turned out to have
**only one usable USB controller**: PB15 (OTG_HS D+) is a 0.1 mm via with no pad — unsolderable. So
OTG_FS carried the host and there was no device port at all; the console was read over **USART1 +
an ESP32-S3/CH340 bridge** (`tools/esp32s3_uart_bridge/`), and webconfig connected to the bridge's
COM port rather than the board. **That is why the four CDC/HID bugs above survived the entire
bring-up unnoticed.** On that board the USB-C was OTG_FS (PA11/PA12, also mirrored on the A11/A12
header) — the "B15" silkscreen near it was a red herring.

`tools/flash.ps1` + `tools/stm32_uart_flash.py` date from there: a DTR-safe AN3155 UART flasher that
flashes over the bridge (build → flash → Go), prompting you to hold BOOT0 + tap RST. Still useful on
any board without a usable second USB controller. Full hands-free UART entry is **not** possible on
these boards: the H7 ROM serves USART only via the *hardware* boot path, the option-byte `BOOT_ADD0`
trick is banned (it strands the board into the bootloader, DFU-only recovery), and BOOT0 has no pad
to wire to.

### Hands-free DFU entry: only the token + reset + early-branch works (July 2026)

The FK-era note above said a software jump comes up "USB-DFU-only" — **that was an assumption, and
it is false.** Bisected on the WeAct board: a **late branch from the running app serves NO ROM
interfaces at all.** Two variants tried and both left the bus permanently dark until an RST tap:

1. The original `jump_to_bootloader()` (clock-gate OTGs → `HAL_RCC_DeInit` → branch). Worse than
   dark: the clock gate froze the D+ pullup latched high, so the host never saw a detach — Windows
   kept a zombie COM port that failed opens with "device not functioning". Looked exactly like a
   wedged board.
2. Same, plus a proper `tud_disconnect()` + OTG force-reset first. Clean detach (no zombie port),
   but still no DFU enumeration — proving the dirty-peripheral theory insufficient. The ROM simply
   will not start its interfaces when entered by a branch from a running app.

**What works: park a magic token at DTCM base (`0x20000000` — all app RAM is in AXI `0x24000000`,
so startup never touches it, and DTCM survives `NVIC_SystemReset`), do a real reset, and branch to
`0x1FF09800` as the FIRST statement of `main()`** — before `HAL_Init`, on a reset-default chip,
which is the state the ROM actually expects. Validated end-to-end with zero button presses:
`dfu` command → reset → ROM DFU enumerates → `platformio -t upload` → `:leave` boots the app →
all hub devices re-enumerate. `check_bootloader_request()` / `request_bootloader_reboot()` in
`main.c`. The one-shot token clear means a crash after entry can't loop the board into the ROM.

The flasher's **bank-1-only erase** (AN3155 special code `0xFFFE`) is what preserves saved config
across reflashes — the ~58 KB app lives entirely in bank 1, so bank 2 @ `0x08100000` survives.
`flash.ps1 -MassErase` forces the old full wipe (use after a `CONFIG_VERSION` bump). The flasher
auto-falls back to a global erase if a bootloader ever rejects `0xFFFE`, so flashing can't break.

CherryUSB was briefly explored (`lib/cherryusb`) and abandoned — TinyUSB is fine.
