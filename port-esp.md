# ESP32-S3 Port — Session Summary (June 2026)

> **STATUS: SHELVED (platform pivot).** The ESP32-S3 works through a hub but
> caps at **8 USB host channels** (the target device set needs ~10). The
> follow-up **ESP32-P4** attempt is a hard dead-end: its high-speed OTG can't
> drive full-speed devices behind a hub because ESP-IDF implements **no
> Transaction Translator** (split transactions) — Espressif marked it
> "Won't Do" (esp-idf#14682). Verified empirically on a Waveshare P4
> Module-DEV-KIT ("`HUB: ... transaction translator (TT) is not supported`",
> and a 2-device-through-hub test collapsed the root port).
> **Decision: move to STM32H7** — OTG_HS run in *full-speed* mode on its
> embedded FS PHY gives the full **16 host channels** with no TT needed (a
> full-speed upstream link makes any hub a pure repeater), using the
> **CherryUSB** (Apache-2.0) host stack; analog out stays PWM+RC (the H7's
> timers have no LEDC-style clock cap) or an external MCP4728 quad DAC.
> This `esp32` branch preserves all the ESP32-S3/P4 work; `main` stays on
> the Teensy 0.6.0 build.

Handoff doc for the Teensy 4.1 → ESP32-S3 firmware port. Captures what was
done, what works, what's still open, and the gotchas worth not re-discovering.

---

## TL;DR

The firmware was fully ported from Teensy 4.1 (USBHost_t36 + EEPROM +
FlexPWM @ 36 kHz + Serial3) to ESP32-S3 N16R8 (EspUsbHost over ESP-IDF
usb_host + Preferences/NVS + LEDC @ 5 kHz + Serial1). The webconfig JSON
wire shape is unchanged — same UI works against both firmwares.

**Status:** Firmware boots clean, USB host enumeration works through hub,
JSON protocol fully responsive over WebSerial. Hot-plug stress with
4 Logitech `RS` composite shifters reveals the **ESP32-S3 EHCI 8-channel
ceiling** — multi-interface HID devices eat ~2 channels each and saturate
the chip's hardware allocator. This is a different issue from the
USBHost_t36 cheap-hub cascade bug (see Open Item #1). The chip,
firmware, and config flow are all working within the channel budget.

**Outstanding hardware item:** A one-time USB-OTG VBUS solder-jumper mod
is required on the Lonely Binary "Gold Edition" board (and most generic
ESP32-S3 dev boards) — the native USB port ships wired as input-only.
Done on user's board, verified working.

---

## Why the port

Original task chain:
1. User hit `only 2/4 USB devices visible through a cheap 4-port powered
   hub` on the Teensy 4.1 build (USBHost_t36).
2. We investigated USBHost_t36 forks (kurte/master), the hub.cpp port
   state machine, even patched in an enumeration watchdog. Nothing fixed
   the cascade. KurtE's `1e2c91e Restructure enumeration` and `fde482e
   Improve error handling` commits don't address this specific failure.
3. Confirmed via cross-test: same hub + same devices work on PC; same
   devices work on a 10-port hub. The bug is `USBHost_t36 ↔ single-chip-4-
   port-hub interaction`.
4. User opted to port to ESP32-S3 rather than chase deeper into
   USBHost_t36 internals.

---

## Hardware

### Board
- **Lonely Binary ESP32-S3-DEVKITC-1 V1.6 "Gold Edition" (N16R8)**
- Amazon ASIN B0FNQVZJ6D
- Likely a relabel of the YD-ESP32-S3 / VCC-GND Studio reference design
- Two USB-C ports: `USB` (native OTG, GPIO 19/20) and `UART` (UART0 → CH340K bridge)
- 16 MB flash, 8 MB OPI PSRAM, no embedded flash

### The USB-OTG VBUS solder-jumper mod (REQUIRED)

The chip's USB OTG controller can act as host (IDF hardwires VBUSVALID=1
internally) BUT the dev board ships with the OTG port wired so VBUS is
input-only — downstream devices have no 5V to power their controllers,
no D+/D- pull-up, no attach signal.

Located on the back of the board: **two adjacent small SMD pads sitting
between the USB-C connectors, just above and slightly left of the gap
between the "USB" and "UART" silkscreen labels.** Unlabeled on V1.6 (the
YD reference design has it labeled `USB-OTG`).

**Procedure:**
1. Power the board via the UART USB-C port.
2. Continuity-check: the two pads should NOT beep between them (open
   jumper). One should beep to a 5V breakout pin; the other should beep
   to the OTG-port VBUS pin.
3. Flood solder across both pads.
4. Verify: ~5.0 V appears on the OTG-port VBUS pin.

**After bridging: NEVER plug the "USB" port into a PC.** Both sides
would push 5V → contention. The OTG port becomes host-only forever.

Sources / further reading: esp-dev-kits issue #71, EspUsbHost issue #12,
the AGENTS.md / firmware/README.md write-ups.

### Pin map (ESP32-S3 GPIO numbers — happen to align with Teensy build for outputs)

| Signal | GPIO | Notes |
|---|---|---|
| `PIN_X` H-pattern X DAC | 4 | LEDC PWM, 12-bit @ 5 kHz |
| `PIN_Y` H-pattern Y DAC | 5 | LEDC PWM, 12-bit @ 5 kHz |
| `PIN_SEQ_UP` | 6 | `OUTPUT_OPEN_DRAIN`, idle high |
| `PIN_SEQ_DOWN` | 7 | `OUTPUT_OPEN_DRAIN`, idle high |
| `PIN_HANDBRAKE` | 8 | LEDC PWM, 12-bit @ 5 kHz |
| `PEDAL_TX` Serial1 TX | 17 | → wheelbase pedal RJ12 RX (was Teensy Pin 14) |
| `PEDAL_RX` Serial1 RX | 18 | ← wheelbase pedal RJ12 TX (was Teensy Pin 15) |
| Config UART0 | 43 / 44 | via onboard CH340K bridge → UART USB-C port |
| USB OTG D-/D+ | 19 / 20 | hardwired in WROOM-1 module |

---

## Toolchain

### Versions pinned
- `arduino-cli` (whatever's on PATH; tested with current)
- `esp32:esp32@3.3.8` arduino-esp32 core
- `ArduinoJson@7.4.3`
- `EspUsbHost` master from git (Library Manager has only 1.0.1 — too old, lacks `onGamepad` parsed-fields API)

### Build & flash

```sh
# Install once
arduino-cli core install esp32:esp32@3.3.8
arduino-cli lib install ArduinoJson@7.4.3
arduino-cli config set library.enable_unsafe_install true
arduino-cli lib install --git-url https://github.com/tanakamasayuki/EspUsbHost.git

# Compile (from repo root)
arduino-cli compile --fqbn "esp32:esp32:esp32s3:PSRAM=opi,FlashSize=16M,USBMode=hwcdc,CDCOnBoot=default" firmware

# Flash (UART port; replace COM13 with whichever CH340 enumerates)
arduino-cli upload -p COM13 --fqbn "esp32:esp32:esp32s3:PSRAM=opi,FlashSize=16M,USBMode=hwcdc,CDCOnBoot=default" firmware
```

Final binary size: **455 KB / 1.28 MB program**, **79 KB / 320 KB RAM**.

`COM13` is the CH340K bridge (VID `1A86` PID `7522`). The native USB
port (VID `303A` PID `1001`) goes AWAY after first flash because OTG
switches to host mode and the native USB Serial/JTAG controller is
suppressed. Flash via UART port only.

---

## Architecture changes

### Module-by-module diff vs. Teensy build

| File | Teensy build | ESP32-S3 build |
|---|---|---|
| `firmware.ino` | USBHost_t36 instances, `g_usb.Task()` in loop, FlexPWM @ 36 kHz | EspUsbHost in own FreeRTOS task, LEDC via `ledcAttach()` @ 5 kHz |
| `device_pool.{h,cpp}` | `GenericJoystickHID : USBHIDInput` w/ `claim_collection` etc. | Same public interface; addr→slot lookup via EspUsbHost's `onGamepad` / `onKeyboard` / `onDeviceConnected` / `onDeviceDisconnected` callbacks |
| `mapping.{h,cpp}` | EEPROM byte-by-byte read/update, `analogWrite()` | `Preferences` (NVS blob namespace=`fanadapter`, key=`config`), `ledcWrite()`. Same 1132-byte `Config` schema, same `CONFIG_VERSION` 3 |
| `pedals.{h,cpp}` | `Serial3` on Teensy pins 14/15 | `Serial1.begin(baud, SERIAL_8N1, PEDAL_RX, PEDAL_TX)` on GPIO 18/17 — same state machine, same warmup, same CRC |
| `protocol.{h,cpp}` | SCB AIRCR write for reboot, `Serial` = USB CDC w/ DTR detection | `ESP.restart()` for reboot, `Serial` = UART0 (no DTR, no `!Serial` close detect) |
| `name.c` | USB descriptor overrides for Teensy CDC | **Deleted** — native USB CDC is suppressed in host mode on ESP32, no device-side strings to override |

### Key behavior changes (vs. Teensy)

1. **PWM frequency: 36 kHz → 5 kHz.** ESP32-S3's LEDC auto-selected clock
   can't sustain 36 kHz @ 12-bit — IDF rejects with `div_param=0` on every
   `ledcWrite`. We're WAY below the 19.5 kHz APB-clock ceiling for safety
   margin across whatever clock IDF picks. Still 30× the RC filter cutoff
   (159 Hz with the wheelbase's 1k+1µF input network) so DAC ripple is
   unchanged in practice.
2. **`OUTPUT_OPEN_DRAIN` vs `OUTPUT_OPENDRAIN`** — ESP32 Arduino has the
   underscored form. Wrong spelling silently compiles to push-pull.
3. **`Serial` is always truthy on ESP32** (UART, no DTR). The Teensy
   build's `if (!Serial)` drop-subscriptions logic is a no-op here.
4. **USB host runs on its own FreeRTOS task** spawned by EspUsbHost — no
   `g_usb.Task()` in `loop()`. Loop order is now just
   `protocolTick() → mappingTick() → pedalsUpdate()`.
5. **Version bumped: 0.6.0 → 0.7.0.** Wire protocol unchanged; added
   `"platform":"esp32s3"` to the `version` response so webconfig can
   display it.

### Webconfig changes (minimal)

- `webconfig/src/lib/serial.ts`: replaced single `TEENSY_VID = 0x16c0` filter
  with a list of common adapter VIDs: Teensy (`0x16C0`, legacy), CH340
  (`0x1A86`), CP2102 (`0x10C4`), FTDI (`0x0403`), Espressif native
  (`0x303A`). Without this fix, the port chooser shows "No compatible
  devices found" on the ESP32 build.
- `webconfig/src/App.tsx`: 5 user-facing "Teensy" strings → "adapter".
- `webconfig/src/lib/types.ts`, `webconfig/src/lib/serial.ts`: comment
  updates.

The `Config` schema, `scaleAxis`, `crc32`, and channel keys are all
identical — the four cross-file invariants in AGENTS.md still hold.

---

## Verification state

Flashed, booted, exercised on Lonely Binary board with USB-OTG VBUS
jumper bridged. Confirmed:

- ✅ Boot clean (no LEDC errors after PWM_FREQ_HZ = 5000)
- ✅ `[USB] host begin() OK` from EspUsbHost
- ✅ Pedal handshake state machine reaches `STATE_STEP0` and waits for wheelbase
- ✅ JSON protocol responsive over UART (CH340K → COM13 in our case)
- ✅ `{"cmd":"version"}` → `{"fw":"fanadapter","ver":"0.7.0","protocol":5,"platform":"esp32s3","max_bindings_per_channel":4}`
- ✅ NVS first-boot defaults seed correctly
- ✅ Device enumeration through hub: observed
  - `RS Shifter & Handbrake` (VID `0x46D` PID `0xC278`)
  - `RS H-Shifter` (VID `0x46D` PID `0xC26B`)
  - `SP Pro Pedal` (VID `0xCAFE` PID `0xA301`, `has_keyboard=true`)
  - Other devices enumerate on different boot timings — see Open Items
- ✅ EHCI channel count via `u` CLI shows 4 / ~8 used when 2 devices enumerated — channels available
- ✅ Webconfig connects, JSON commands round-trip in <100 ms after we
  flashed the clean (non-verbose) firmware

NOT yet verified (deferred — needs wheelbase actually plugged in):
- 🟡 Full pedal handshake (`STATE_STEP0 → STREAMING_115K`) against a real wheelbase
- 🟡 H-pattern DAC voltage output measured on PIN_X / PIN_Y
- 🟡 Sequential pulses on PIN_SEQ_UP / DOWN
- 🟡 Save → reboot → load NVS roundtrip
- 🟡 `listen` capture flow in webconfig with live device input

---

## Open items

### #1 — EHCI 8-channel ceiling saturates with multi-interface HID devices

**Updated diagnosis (supersedes earlier "same cheap-hub cascade bug"
theory):** the ESP32-S3 USB host has a hard cap of **8 HCD channels**
(`EspUsbHost::maxEndpointChannelCount() = 8`, `EspUsbHost.cpp:7083`).
Channels are consumed by: hub status/control pipes (~1 per hub) and
every interrupt-IN endpoint EspUsbHost claims on every interface of
every downstream device. The library claims **every HID interface
unconditionally** at `EspUsbHost.cpp:4672` — no filter.

**Measured (not theorised)** via the on-device `devicePoolDumpUsbDetail()`
channel dump (auto-printed to webconfig Logs on every attach):

| Device | ep0 (control) | interrupt eps | channels |
|---|---|---|---|
| `USB2.1 Hub` (Realtek 0xBDA:0x5411) | 1 | 1 status | **2** |
| `RS Shifter & Handbrake` (0x46D:0xC278) | 1 | **2** (two HID ifaces, both cls 0x03) | **3** |
| `RS H-Shifter` (0x46D:0xC26B) | 1 | **2** (two HID ifaces, both cls 0x03) | **3** |
| `SP Pro Pedal` (0xCAFE:0xA301) | 1 | 1 | **2** |

```
hub(2) + RS Shifter&HB(3) + RS H-Shifter(3) = 8 / 8   ← ceiling hit
                              3rd device → no control pipe → Port disabled
```

The earlier "composite device with a CDC/vendor interface" theory was
WRONG — the dump shows zero CDC. The cost is two legitimate HID
interfaces per RS device (shifter + handbrake on the combo unit), each
needing its own interrupt-endpoint channel, plus one persistent control
channel per device, plus the hub's 2. Nothing is wasted; the interfaces
are real. The user's full set (2 shifters + pedals via hub) wants
**10 channels** but the ESP32-S3 DWC2 has exactly **8** — a hard
silicon limit.

Symptoms in the serial log:
```
E (...) HCD DWC: No more HCD channels available
E (...) USBH: EP Alloc error: ESP_ERR_NOT_SUPPORTED
E (...) USB HOST: Claiming interface error: ESP_ERR_NOT_SUPPORTED
E (...) HUB: Failed to add new downstream device
E (...) EXT_PORT: [1:N] Port disabled, reset attempts=1
```

**UX trap:** when channel exhaustion hits *partway through* a
multi-interface device's enumeration, our `handleConnect()` still prints
`slot claimed addr=N` because the control pipe + first interface succeeded.
The interrupt endpoint of the second interface never opens, so the device
appears in `list_devices` but emits no input events. Looks like a broken
device, is actually a starved host.

**Budget planning (what fits in 8):** the hub is a fixed 2-channel tax.
That leaves 6 for devices. Some combos that fit:
- 1 RS shifter (3) + pedals (2) = 5  ✅ (room to spare)
- 2 RS shifters (3+3) = 6  ✅ (but then no pedals)
- pedals (2) + pedals (2) + 1 RS (3) = 7  ✅
- 2 RS shifters + pedals = 8 → **does NOT fit** (wants 10 incl. hub).

**Paths forward (in increasing effort):**
- **Plan the channel budget** (above). For most real rigs, one shifter +
  pedals fits comfortably.
- **Skip a redundant HID interface (only if one is actually unused).**
  Each RS device claims TWO HID ifaces. If — and only if — `iface#1` on a
  given device carries no inputs the user binds (e.g. the H-Shifter's 2nd
  iface is a stub), patching EspUsbHost to skip it frees 1 channel/device,
  enough to fit all three + pedals. **Unknown until the report descriptors
  are dumped** — for the Shifter&Handbrake combo both ifaces are almost
  certainly real (shifter + handbrake) and NOT skippable. Patch site:
  `EspUsbHost.cpp:4672` `handleDescriptor()` HID-claim branch (vendored
  lib — a local patch is fragile, would need to live in the repo).
- A multi-TT or 10-port hub will **not** help — channels are a chip limit,
  not a hub limit. Neither will a different/better hub.

**IDF log vs JSON corruption (separate issue):** ESP-IDF's own
`ESP_LOGE` (e.g. `HCD DWC: No more HCD channels`, `usbh_devs_open error`)
writes directly to UART0, bypassing the Arduino-level `serialLock` mutex,
so it can still splice into a JSON telemetry line (`"handbrake":E (...)
usbh_0d}...`). webconfig tolerates dropped telemetry frames, but a flood
of IDF errors will corrupt many. The repeating `usbh_devs_open error:
ESP_ERR_INVALID_STATE` (every ~500 ms) is the host stack **wedged after
rapid hot-plug churn** — a reboot clears it. If this becomes a problem in
normal use, options: `esp_log_level_set()` to quiet the noisy USB tags
(hides real diagnostics — our own channel dump replaces them), or move
the JSON protocol to a separate UART from IDF logging (bigger change).

(The earlier theory — same USBHost_t36 single-TT cascade bug — is
retired. The dump is unambiguous: it's the channel ceiling, hit by real
interfaces.)

### #2 — Pedal stream not yet verified against real wheelbase

User had wheelbase tests on Teensy but hasn't plugged into ESP32 build yet.
Logic is identical (CRC tables, state machine, 100 Hz timing). Risk: LEDC
PWM ripple on PIN_HANDBRAKE at 5 kHz vs Teensy's 36 kHz could be visible
in the dedicated handbrake port's analog read — but we already route
handbrake through the pedal UART stream too, so the wheelbase prefers
that on modern firmware.

### #3 — `arduino-cli monitor` in user's shell

In user's PowerShell session: `arduino-cli monitor` reports
`platform esp32:esp32 is not installed`. Likely a config-dir mismatch
(the install happened via Claude's shell, possibly different
`ARDUINO_CONFIG_DIR`). Workaround: webconfig serves as the
monitor for all practical purposes (Logs tab + JSON events).
If user wants a CLI monitor: `arduino-cli core list` first to confirm
state, then `arduino-cli core install esp32:esp32@3.3.8` in that shell,
then `arduino-cli monitor -p COM13 --fqbn esp32:esp32:esp32s3
-c baudrate=115200`.

### #4 — Old Teensy firmware build artifacts

The Teensy build is gone from `firmware/` but git history retains it
through v0.6.0. If someone needs to revert / cross-reference, check out
that tag.

---

## Files touched

### New / rewritten (ESP32-S3 firmware)

- `firmware/firmware.ino` — orchestrator
- `firmware/device_pool.{h,cpp}` — EspUsbHost-backed pool
- `firmware/mapping.{h,cpp}` — Preferences/NVS + LEDC
- `firmware/pedals.{h,cpp}` — Serial1
- `firmware/protocol.{h,cpp}` — ESP.restart, no DTR detect

### Deleted

- `firmware/name.c` — Teensy-specific USB descriptor overrides

### Docs rewritten / updated

- `firmware/README.md` — entire ESP32 reference + VBUS solder mod
  procedure
- `AGENTS.md` — module layout, build cmds, things-to-know (added VBUS
  jumper, PWM clock cap, EHCI channel ceiling, OPEN_DRAIN macro spelling)
- `README.md` (root) — block diagram, USB host stack note, VBUS
  warning, roadmap pruned (ESP32 port no longer pending), platform
  preamble
- `schematics/README.md` — GPIO numbering, pedal UART pins (17/18 not
  14/15), Step 1 = solder mod, BoM swap
- `webconfig/README.md` — de-Teensified text references
- `.github/workflows/firmware.yml` — CI now installs esp32 core + ArduinoJson + EspUsbHost from git

### Webconfig code (minimal)

- `webconfig/src/lib/serial.ts` — VID filter list expansion (CRITICAL — without this, port chooser shows nothing)
- `webconfig/src/App.tsx` — "Teensy" → "adapter" in 5 user-facing strings
- `webconfig/src/lib/types.ts` — comment
- `webconfig/src/lib/serial.ts` — comment

### Unchanged (deliberately)

- `webconfig/src/lib/scaleAxis.ts` — axis math
- `webconfig/src/lib/crc32.ts` — CRC algorithm
- `webconfig/src/lib/types.ts` — `Config` schema TS mirror
- The four cross-file invariants in AGENTS.md still hold

---

## Resume guide for next session

If picking up cold:

1. **Check task list state.** The TaskList tracks 17 tasks; the
   noteworthy still-open ones are #15 (Verify via WebSerial UI — partial,
   USB host works, pedal stream + DAC voltage not yet measured against
   real wheelbase) and #17 (User: solder-bridge USB-OTG jumper — DONE on
   their board, leave open in case future agent encounters fresh
   hardware).

2. **Verify build environment.** `arduino-cli core list | grep esp32`
   should show `esp32:esp32 3.3.8`. `arduino-cli lib list | grep
   EspUsbHost` should show `EspUsbHost 1.0.2` (the git-master version
   reports as 1.0.2 in library.properties, not 1.0.1).

3. **Verify the chip can be flashed.** Look for a CH340 / CP2102 COM
   port (NOT VID 303A — that disappears in host mode). On our user's
   machine: COM13.

4. **Sanity probe.** `{"cmd":"version"}` should return immediately with
   `platform:"esp32s3"`. If it times out: verbose IDF logging may have
   been re-enabled — check `firmware.ino` setup() for any
   `esp_log_level_set(...)` calls and remove them.

5. **The hub issue is hardware, not software.** Do not waste time
   patching the IDF hub driver / hunting EspUsbHost bugs / tweaking
   enumeration timing for the 2/4 enumeration cascade. It's the same
   bug that bit Teensy. If user complains about partial enumeration:
   suggest a multi-TT hub (Microchip USB253x, GL852G) or the 10-port
   cascaded hub. Documented in `firmware/README.md` Troubleshooting and
   `schematics/README.md`.

6. **VBUS solder mod must already be in place on the user's board.** If
   the next session reports "list_devices shows all 8 slots empty even
   though I plugged a device in" — that's the VBUS jumper not bridged.
   `firmware/README.md` → Hardware setup has the procedure.

7. **No `Co-Authored-By` lines on commits** per AGENTS.md.
