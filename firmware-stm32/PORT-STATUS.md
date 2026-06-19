# STM32H743 port — status & resume guide

Handoff doc for the Teensy → STM32 firmware port. Read this to resume cold.

## TL;DR

Porting the `fanadapter` firmware to an **FK743M3‑VGT6** (STM32H743VGT6) core board, in
`firmware-stm32/` (**PlatformIO + STM32Cube HAL + TinyUSB 0.18**). The enumerator milestone is done;
the adapter-logic port is now underway, tracked as milestones M0–M7 (plan:
`C:\Users\Gugic\.claude\plans\shimmering-hatching-noodle.md`).

**Status: M0–M4 DONE and HARDWARE-VALIDATED (June 2026). Remaining: M5 handbrake-PWM, M6 pedal UART, M7 polish.**
- **M0 — plumbing/test-rig:** USART1 RX (interrupt + 16-byte FIFO, ISR drains it, priority **5 = above
  USB** so command bursts don't overrun); console switched to **8E1** to match the ROM bootloader;
  `lib/cherryusb` deleted. DAC + TIM HAL were already enabled in the active PlatformIO conf — no override.
- **M1 — HID decode + USB InputSource + protocol:** generic HID report-descriptor walker
  (`hid_parse.c`), 8-slot device pool (`usb_input.c`), the modular `InputSource` vtable
  (`input_source.c` — the cornerstone), and `protocol.c` (`version`, `list_devices`, `live_inputs` +
  `device_attached`/`device_detached`/`live` events, shapes matched to webconfig). **Verified on the
  4-device / 7-interface hardware:** `list_devices` correct; H-shifter buttons `0x3F`; sequential
  shifter buttons `0x3`; handbrake (C278 multi-mode, axis mode) axis 2; pedal axis tracks.
- **Axis fidelity (HW-validated):** `hid_parse.c` stores the **raw native** logical value (`decode_axis`,
  matching the Teensy `m_axes[i]=(uint16_t)value`) — a 10-bit handbrake reads 0..1023, a 12-bit pedal
  0..4095, clean step-of-1. (An earlier version normalized to 0..65535 → confusing 16/64-count jumps.)
  `scaleAxis` maps `[rawMin,rawMax]` (native, from Listen calibration) → 0..65535 at eval. Calibration is
  still needed — declared range ≠ physical travel, plus invert/deadzone.
- **M2 — Config + flash + get/set/save/reset (HW-validated):** `mapping.{c,h}` (Config byte-identical,
  **1132 B, CONFIG_VERSION 3**, `_Static_assert` locks; `crc32` verbatim = `webconfig/.../crc32.ts`;
  evaluators via `input_fold_*`), `json_min.{c,h}` (zero-alloc JSON reader, ArduinoJson replacement),
  `config_store.{c,h}` (internal flash). Commands `get_config` (streamed in small chunks — USART1 blocks
  until sent so no truncation), `set_binding`/`set_gear_dac`/`set_pulse_ms`/`set_gear_mode`/`save_config`/
  `reset_config`. Save→verify confirmed on HW. **Two flash-store bugs that cost a bench session (see below).**
- **M3 — DAC H-pattern + transit FSM (HW-validated):** `outputs.{c,h}` — **DAC1 ch1=PA4 (X), ch2=PA5 (Y)**,
  codes map 1:1 to the schematics voltage table (output buffer on; only Reverse X1=3.30V/4095 clamps ~3.1V).
  Non-blocking neutral-transit FSM (replaces the Teensy `delay(50)`); `update_shifter` hold+latch + test
  override. Commands `test_gear`, `live_outputs`, `outputs` event. **Sequential pulse added here too**
  (was M5): open-drain **PC6 (up)/PC7 (down)**, idle HIGH, non-blocking FSM (confirm those pins are
  broken out before wiring).
- **M4 — direct output control (HW-validated; REPLACED the earlier "serial virtual-device inject" idea):**
  the PC (SimHub / the web app) owns the input→action mapping and commands outputs directly; the adapter
  applies them. Commands: **`set_gear {gear}`**, **`set_outputs {throttle?,brake?,clutch?,handbrake?}`**
  (0..65535, the 100 Hz pedal/handbrake hot path), **`pulse_shift {direction}`**, **`release_outputs`**.
  Semantics = **command override**: a PC-set channel wins over the USB mapping, sticky until
  `release_outputs`. State in `mapping.c` (`g_ovr_*`). (Standalone mode — USB devices → in-firmware
  mapping → outputs — is unchanged.) `input_serial.{c,h}` + `inject_*` were removed.

USB host is on **OTG_FS**; the console is exposed **two ways at once** — a **USB CDC device on OTG_HS
(B14/B15)** for capable boards, plus the **USART1 UART** (8E1) fallback used here via the ESP32-S3
bridge on COM16. The original enumerator validation is preserved below.

## Hardware validation (June 2026, FK743M3 + ESP32-S3 bridge)

Read over the S3 bridge (S3 GPIO18 ← STM32 PA9, S3 powers the STM32 5V, S3 on its CH340 UART port =
COM16 @ 115200). Boot banner confirmed `OTG_FS=host  OTG_HS=device`, `host channels: OTG_FS=16 OTG_HS=16`.
Four devices behind the hub enumerated cleanly:

| Addr | VID:PID | Device |
|---|---|---|
| 1 | 046D:C278 | Logitech RS Shifter & Handbrake |
| 2 | 046D:C26B | Logitech RS H-Shifter |
| 3 | 046D:C278 | Logitech RS Shifter & Handbrake (2nd) |
| 4 | CAFE:A301 | Simnet SP Pro Pedal |

**Finding:** with `CFG_TUH_HID = 4`, only the first 4 HID *interfaces* (addr 1+2, two composite shifters)
mounted; addr 3/4 enumerated at the device level but their HID interfaces didn't claim (pool exhausted).
Bumped to **`CFG_TUH_HID = 8`** — devices stay visible either way, but the bump is needed to actually read
inputs from all of them. Each mounted HID interface arms an interrupt-IN channel, so this stays under 16.
**Confirmed on hardware:** with `=8`, all **7 HID interfaces** (instances 0–6 across the 4 devices) mount
— ~9 host channels in use, comfortably under 16. Full enumerator validation complete.

## Board facts (FK743M3‑VGT6 — all confirmed, some the hard way)

- **USB‑C = OTG_FS = PA11/PA12**, and those same pins are broken out on the **A11/A12 main‑header**
  (proven: a USB breakout wired to A11/A12 enumerated the *same* console COM port as the USB‑C).
  The board's "B15" silkscreen near the USB‑C is **not** the USB‑C data — that was a red herring.
- **OTG_HS (PB14/PB15) is UNUSABLE on this board.** PB14 is on the bottom header, but **PB15
  (OTG_HS D+) is only a 0.1 mm via with no pad — unsolderable.** OTG_HS needs both, so it's dead.
  ⇒ **Only ONE usable USB controller: OTG_FS.**
- HSE crystal = **25 MHz**. VCORE = internal **LDO**. ROM **DFU bootloader is on OTG_FS / the USB‑C**.
- **8‑pin header** near the USB‑C = SWD + serial + power: `CLK DIO RST TX RX 3V 5V GND`.
  **TX/RX = USART1 (PA9=TX, PA10=RX)** (per the FK‑family "SWD and USART1" schematic). PA9/PA10/PA11/PA12 are all adjacent on port A.
- Both OTG_FS and OTG_HS report **16 host channels** (GHWCFG2.NumHstChnl) — OTG_FS alone is plenty for the 4‑device target.

## What already works

- Boots & runs (multi‑pin GPIO heartbeat at ~0.5 Hz; user LED is one of the candidate pins).
- Confirmed clean: clocks (HSI 64 MHz sysclk + PLL3→48 MHz USB), DFU flash loop.
- Earlier proof: a USB **CDC console on OTG_FS / USB‑C** enumerated as a COM port (VID `1209`
  PID `FA00`) — that's how OTG_FS=rhport 0 and the 16‑channel count were confirmed. The CDC console
  now lives on OTG_HS instead (OTG_FS became the host); that history proves the OTG_FS link is good.

## THE bug that ate the early session (fixed, don't reintroduce)

`SystemClock_Config` MUST call `HAL_PWREx_ConfigSupply(PWR_LDO_SUPPLY)` **before**
`__HAL_PWR_VOLTAGESCALING_CONFIG`. Skip it and `PWR_FLAG_VOSRDY` never sets → the firmware hangs in
that `while` loop forever, before USB ever inits. There's now a timeout on that wait that calls
`Error_Handler` (fast LED blink) instead of hanging silently.

## Console architecture — DONE (host on OTG_FS; console on BOTH OTG_HS CDC and USART1)

Open‑source goal: a board with **both USB ports broken out should "just work" with two cables** — hub
on the host port, PC on the console port, no bridge hardware. The FK743M3 bring‑up board can't do that
(OTG_HS dead — PB15 is an unsolderable via), so it falls back to a UART console read over the ESP32‑S3.
**One firmware serves both:** `console_printf()` mirrors output to a USB CDC device AND USART1.
Implemented June 2026, **builds clean** (37.9 KB flash). What landed:

1. **Host on OTG_FS (rhport 0)** — the universally usable controller (works even on single‑port
   boards, incl. this one). `tusb_config.h`: `CFG_TUSB_RHPORT0_MODE = OPT_MODE_HOST|FS`, `CFG_TUH_ENABLED 1`,
   `CFG_TUH_RHPORT 0`. ISR `OTG_FS_IRQHandler → tusb_int_handler(0, true)`; loop runs `tuh_task()`.
2. **USB CDC console on OTG_HS (rhport 1 = PB14/PB15)** — `CFG_TUSB_RHPORT1_MODE = OPT_MODE_DEVICE|FS`,
   `CFG_TUD_ENABLED 1`, `CFG_TUD_CDC 1`. `src/usb_descriptors.c` restored (CDC IAD, VID `1209` PID `FA00`).
   ISR `OTG_HS_IRQHandler → tusb_int_handler(1, true)`; loop runs `tud_task()`. `tud_cdc_line_state_cb`
   reprints the banner on DTR so a late‑connecting PC isn't left blank. **Dormant on the FK743M3** (pins
   unwired) — inits harmlessly and never enumerates.
3. **USART1 UART console (PA9 TX / PA10 RX, AF7, 115200, PCLK2=64 MHz)** — always present. `cdc_printf`
   → renamed **`console_printf`** (blocking `HAL_UART_Transmit`, ISR‑guarded): writes the UART
   unconditionally, then the CDC when `tud_cdc_connected()`. This is what the S3 bridge reads here.
4. **`usb_hw_init`**: PA11/PA12 = AF10 (OTG_FS) + PB14/PB15 = AF12 (OTG_HS) + **both** controller clocks
   + ULPI‑sleep‑disable + both NVIC lines. `console_init()` adds PA9/PA10 = AF7 + USART1 clock.
5. **Host VBUS — non‑issue, verified in the dwc2 source.** On H7 `GCCFG.VBDEN` resets to 0 (VBUS
   assumed valid), FS‑PHY init only sets `PWRDWN`, and `hcd_dwc2.c` writes `HPRT_POWER`. So the host
   doesn't gate on a VBUS pin and PA9 is free for USART1; no GCCFG poking needed.

## NEXT STEP — M5/M6 (output hardware), M7 (polish)

M0–M4 are done (above). Remaining, per the plan (`shimmering-hatching-noodle.md`):
- **M5 — handbrake PWM fallback** (the sequential pulse already landed in M3). A TIM PWM channel on a
  free pin outside the heartbeat set + RC; handbrake's primary path is still the pedal stream.
- **M6 — pedal UART (USART2):** the CSL Elite emulator. This is what makes `set_outputs` and USB-mapped
  pedals/handbrake *electrically* reach the wheelbase — today those values stop at the `outputs`
  snapshot/event. Port the handshake state machine, 2 s warmup, 100 Hz framing, lenient STEP2 collector.
- **M7 — polish:** `reboot`/`reset_pedals`/`pedals_status`/`test_axis`/`test_pulse` commands, DAC gear
  recal against the real wheelbase, doc/README sync.

### Flash config store — TWO bugs that cost a bench session (FIXED; do not reintroduce)
1. **The config sector MUST be in a DIFFERENT flash bank than the running code.** Code runs from bank 1;
   config is **bank 2, sector 0 @ `0x08100000`** (`FLASH_BANK_2`/`FLASH_SECTOR_0`). The H7 can't fetch
   instructions from a bank while it's being erased — a bank-1 config sector HARD-FAULTS the core on the
   128 KB erase. Bank 2 sector 0 physically exists on the 1 MB VG and is NOT aliased to bank 1 (verified
   by reading `0x08000000` vs `0x08100000` over the ROM bootloader: `stm32_uart_flash.py --read
   0x08100000:32` → all `0xFF`, distinct from the bank-1 vector table). `DUAL_BANK` is defined for
   STM32H743xx, so the HAL unlocks both banks + waits on `QW_BANK2`. Erase ~1 s; 36 × 256-bit flashwords.
2. **NEVER call `SCB_InvalidateDCache_by_Addr()` with the D-cache DISABLED** (this build never enables it).
   That faulted the core right after a clean erase+program. Removed — with D-cache off, memory-mapped
   flash reads are already coherent. (If D-cache is ever enabled, re-add it guarded.) The flash op is
   wrapped in `__disable_irq()`/`__enable_irq()` (defensive; the erase busy-waits ~1 s, but save_config is
   rare so the brief USB/console pause is fine).

### webconfig now talks to THIS board (over the bridge)
Native USB CDC is dead here, so the web UI connects over the **S3 bridge / CH340 = COM16**. `serial.ts`:
**no port-picker VID filter** (show all ports — bridge chips vary; the CH340K here is `0x1a86/0x7522`),
`port.open` at **8E1** + a `setSignals` DTR/RTS-low + ~2 s settle (the bridge resets on open; expect a
one-time garbled boot-noise burst in the Logs tab — that's the S3's ROM chatter, not a framing problem).
New wrappers `setGear/setOutputs/pulseShift/releaseOutputs` + a **"Direct output control"** card in the
Outputs tab. Dev server: `$env:PATH="...fnm\aliases\default;$env:PATH"; npm run dev` →
`http://localhost:5173/fanadapter/` (Chromium); WebSerial holds COM16 exclusively, so **Disconnect before
flashing**. (A future board with OTG_HS wired uses the STM32's own CDC directly — VID `1209` PID `FA00`,
nicely named — no bridge.)

## Reproduce — flash & read (current workflow, 8E1)

The console is now **8E1** (115200, even parity) — the S3 bridge sketch and any PowerShell reader must
use even parity. On the FK743M3 the OTG_HS CDC console is dormant, so use the USART1 + S3 path:

1. **S3 bridge** (`tools/esp32s3_uart_bridge/`, already 8E1): `arduino-cli compile --upload -p COM16
   --fqbn esp32:esp32:esp32s3 tools/esp32s3_uart_bridge` (I can run this — S3 = CH340 = **COM16**).
2. **Flash:** normally over the S3 with `tools/flash.ps1` (see "UART flashing — WORKING" below). USB DFU
   is a fallback only — cable to the board's
   USB‑C, **hub off A11/A12**, hold BOOT0 + tap RST + release BOOT0, then
   `python -m platformio run -d ... -e weact_h743 -t upload`. (One USB‑C cable is shared between the
   board's DFU port and the S3 — move it to the board to flash, back to the S3 to read.)
3. **Read/command COM16 @ 115200 8E1** from PowerShell with `Parity=Even, DataBits=8, StopBits=One`
   and **`DtrEnable=$false; RtsEnable=$false`** (DTR-true resets the S3 via CH340 auto-reset). Commands:
   `{"cmd":"version"}`, `{"cmd":"list_devices"}`, `{"cmd":"live_inputs","on":true}`. **Send commands at
   full speed** now (RX priority fix landed); on firmware predating that fix, send byte-spaced (~12 ms)
   and prefix a `\n` to flush any stale line buffer. **The user is NOT watching the live session — ping
   and get an explicit "I'm on it" before any timed capture that needs them to actuate controls.**

### UART flashing — WORKING (`tools/flash.ps1` + `tools/stm32_uart_flash.py`)
The DTR-safe AN3155 flasher flashes over the S3 bridge — no CubeProgrammer, no DTR juggling, no USB-C swap.
**You press the button:** run `flash.ps1` (build -> flash -> boot the app), and when it prints
`>>> PRESS BOOT0 + RST <<<`, **HOLD BOOT0 down + tap RST** (keep holding a beat). Now that
`BOOT_CM7_ADD0` = flash, a *plain* RST boots the app, so BOOT0 must be high through the reset to reach the
bootloader (it boots `BOOT_ADD1` = system memory). The flasher (`stm32_uart_flash.py`) does autobaud/Get/
Get-ID/Extended-Erase/Write/Go + `--wait N` (poll for the bootloader), `--probe`, `--read-ob`.
**Full hands-free auto-entry is NOT possible on this board:** the H7 ROM serves USART only via the
*hardware* boot path (a software jump comes up USB-DFU-only), the option-byte `BOOT_ADD0` trick is BANNED
(it strands the board into the bootloader — DFU-only recovery), and BOOT0 has no pad to wire the S3 to.
**USB DFU fallback** (recovery / option bytes, board USB-C to PC, BOOT0+RST -> DFU chime):
`STM32_Programmer_CLI -c port=usb1 -w firmware.elf -v -ob BOOT_CM7_ADD0=0x0800`. Full saga (incl. the
"garbled console = S3 boot-noise while stuck in the bootloader" ghost): `[[reference-stm32-uart-bootloader]]`.

(On a board where OTG_HS **is** wired, skip the S3 entirely: plug the PC into the OTG_HS port and open
the CDC COM port — VID `1209` PID `FA00` — directly.)

### Wiring (FK743M3 / this board)
- **Hub → A11/A12** (OTG_FS host): D−→PA11, D+→PA12, GND→GND, hub VBUS→board 5V (self‑powered hub: its own supply too).
- **Console → S3** (USART1): STM32 **PA9 (TX) → S3 RX**, optional **PA10 (RX) ← S3 TX**, **GND↔GND**; the S3's 5V can power the board.
- **USB‑C = DFU flashing only** — disconnect the hub from A11/A12 first (hub + DFU share OTG_FS).

## Build / flash / read

```sh
python -m platformio run -d "C:/Users/Gugic/teensy/firmware-stm32" -e weact_h743            # build
# DFU: hold BOOT0, tap RST, release BOOT0 — ONLY the USB-C connected (a 2nd active USB blocks DFU)
python -m platformio run -d "C:/Users/Gugic/teensy/firmware-stm32" -e weact_h743 -t upload   # flash
```
Read the console: on the FK743M3 it's the S3's COM port at 115200 (plain UART, banner at boot). On a
board with OTG_HS wired, it's the CDC COM port VID `1209` PID `FA00` (banner reprints on DTR connect).

## Gotchas (don't re-derive)
- **rhport map**: OTG_FS = rhport 0 (host), OTG_HS = rhport 1 (CDC console). Both ISRs wired:
  `OTG_FS_IRQHandler→tusb_int_handler(0, true)`, `OTG_HS_IRQHandler→tusb_int_handler(1, true)`.
- **USB‑C = OTG_FS = A11/A12** (not B14/B15); DFU bootloader is here too.
- **OTG_HS CDC is dormant on the FK743M3** (PB15 dead) but the device stack still inits harmlessly —
  on a board where OTG_HS is wired it becomes the primary, bridge‑free console.
- **Feedback‑storm risk is back**: `console_printf` writes the USB CDC, so routing TinyUSB debug to it
  (`CFG_TUSB_DEBUG > 0`) can storm (log → CDC write → USB activity → log). Keep `CFG_TUSB_DEBUG 0`, or
  point `CFG_TUSB_DEBUG_PRINTF` at a UART‑only writer when you need enumeration tracing.
- `lib/cherryusb` was cloned during a detour but is **not needed** — can be deleted.
