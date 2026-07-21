# AGENTS.md

This file provides guidance for AI coding agents working in this repository.

## What this repo is

`fanadapter` is a USB HID → Fanatec wheelbase adapter (Teensy 4.1 or STM32H743) plus a browser-based WebSerial UI for configuring it, and a SimHub plugin that drives the same protocol from the PC:

- **`firmware/`** — Arduino sketch (Teensyduino) that runs on the Teensy 4.1. Enumerates USB HID joysticks, gamepads, and multi-axis controllers via the native USB host port and drives Fanatec RJ12 ports (H-pattern PWM, sequential open-drain, handbrake PWM, pedal-port UART).
- **`webconfig/`** — Vite + React 19 + TypeScript + Tailwind + shadcn/ui app that talks to the firmware over WebSerial (line-based JSON). Also a one-click STM32 firmware updater: the Pages deploy builds the firmware and publishes `firmware/fanadapter-stm32.{json,bin}` beside the app, and the header's Flash dialog reboots the board into its ROM bootloader (`{"cmd":"dfu"}`), flashes over WebUSB/DfuSe (`src/lib/dfu.ts`) via the persisted device grant, and reconnects over the persisted serial grant — picker only on the first update per machine. Deployed to GitHub Pages by `.github/workflows/pages.yml` on push to `main` (which is why that workflow also triggers on `firmware-stm32/**`).
- **`firmware-stm32/`** — **C** port of the firmware to an **STM32H743** (WeAct MiniSTM32H743VITX): PlatformIO + STM32Cube HAL + TinyUSB. Speaks the same WebSerial JSON protocol and byte-identical `Config` schema as `firmware/`, so one `webconfig` serves both. Full feature parity with the Teensy, **hardware-validated end-to-end on a real wheelbase** (M0–M7): USB-host HID decode, the mapping evaluators, DAC H-pattern on PA4/PA5, sequential pulses on PC6/PC7, a TIM3 handbrake-PWM fallback on PC8, and the CSL Elite pedal-port UART emulator on USART2/PA2-PA3 (with wheelbase-power-cycle auto-recovery), plus a **direct-output** command set (`set_gear` / `set_outputs` / `pulse_shift` / `release_outputs`) for PC/SimHub-driven output that overrides the USB-device mapping, and a **`dfu`** command (also STM32-only) that reboots into the ROM bootloader for a hands-free reflash — webconfig's Flash dialog drives it. Reflashing preserves saved config regardless of path (config lives in bank 2; DFU and the UART flasher both erase only bank-1 sectors). **Both USB controllers are used at once: OTG_FS (the on-board USB-C) is the CDC device — webconfig + console + DFU + power — and OTG_HS (PB14/PB15 header) is the USB host for the powered hub. That split is forced by hardware: the USB-C is a device receptacle (UFP/Rd), so a hub plugged into it can never attach.** Two docs, don't conflate them: **`firmware-stm32/README.md`** is the user-facing build & wiring guide (board choice, pin map, wiring, flashing, troubleshooting), and **`firmware-stm32/PORT-STATUS.md`** is the development record (milestones, fixed bugs and why, gotchas) — **read PORT-STATUS before touching the STM32 firmware.**

- **`simhub-plugin/`** — **C#** ([SimHub](https://www.simhubdash.com/)) plugin, .NET Framework 4.8 + WPF, that speaks the same line-based JSON protocol over a COM port. Two projects: **`Fanadapter.Core`** holds the transport, protocol, schema, `ScaleAxis` and `CaptureEngine` and has **no SimHub references** (so its xunit tests run without SimHub installed), and **`Fanadapter.SimHub`** is the plugin itself. It splits by where the input device is plugged in: devices on the **PC** are captured and mapped by SimHub's own input system, and the plugin only turns the result into direct-output commands (`DriveController`); devices on the **adapter's USB hub** are invisible to the PC, so for those the plugin mirrors webconfig's configuration UI (Devices / Mappings / Outputs tabs). Also carries the one-click firmware updater (`FirmwareUpdateViewModel` + `Fanadapter.Core/DfuseFlasher.cs` over WinUSB — no device picker at all, unlike webconfig's WebUSB variant). Full feature parity with webconfig **except** the guided setup wizard and presets. Hardware-validated against the STM32 adapter. Build, install and usage: `simhub-plugin/README.md`.

Detailed hardware docs, port pinouts, protocol references, calibration guides: `firmware/README.md`. Webconfig deploy + dev: `webconfig/README.md`. This file only covers the parts that need cross-file context.

## Commands

### Firmware

```sh
# Compile
arduino-cli compile --fqbn teensy:avr:teensy41 firmware

# Flash (Teensy presents as a serial port; replace COM11 with the actual port)
arduino-cli upload -p COM11 --fqbn teensy:avr:teensy41 firmware
```

Required external library: **ArduinoJson** ≥ 7.0 — `arduino-cli lib install ArduinoJson`.

The flash uploader can't acquire the port while WebSerial is connected — the UI's **Disconnect** button must be hit first, or you get an "Access is denied" error (Windows). The firmware also exposes a `{"cmd":"reboot"}` JSON command (SCB AIRCR soft reset) so re-flashing isn't needed for a restart.

There are no firmware tests.

### Webconfig

```sh
cd webconfig
npm install
npm run dev         # Vite dev server at http://localhost:5173/fanadapter/
npm run build       # static bundle in dist/
npm run lint        # ESLint
```

Base path defaults to `/fanadapter/` (override with `VITE_BASE=/path/`). WebSerial only works in Chromium-based browsers on desktop.

There are no webconfig tests.

### SimHub plugin

```sh
dotnet build simhub-plugin/FanadapterSimHub.sln -c Release
dotnet test  simhub-plugin/FanadapterSimHub.sln -c Release

# Build and drop both DLLs into the SimHub install (close SimHub first — it
# holds loaded plugin assemblies open):
dotnet build simhub-plugin/src/Fanadapter.SimHub/Fanadapter.SimHub.csproj -c Release -p:InstallToSimHub=true
```

Needs the .NET SDK 8+ only; the net48 reference assemblies come from a NuGet package. SimHub's own DLLs are resolved by `HintPath` off the `$(SimHubDir)` MSBuild property (default `C:\Program Files (x86)\SimHub\`, overridable in an untracked `simhub-plugin/Directory.Build.props.user` — which is exactly what CI writes after installing the version pinned in `.simhub-version`).

## Architecture

### Firmware module layout (`firmware/`)

Five translation units, loop-dispatched in `firmware.ino` in this order — order matters:

```
g_usb.Task() → pollUsbDriverStatus() → protocolTick() → mappingTick() → pedalsUpdate()
```

- `device_pool.{h,cpp}` — 8-slot pool of `GenericJoystickHID` consumers. Claims any joystick, gamepad, multi-axis controller, or keyboard HID collection (any VID/PID), first-come-first-served. Axis/button counts are discovered lazily from observed reports — they start at 0 and grow. Each slot also snapshots the device's USB **manufacturer / product** string descriptors at claim time (sanitised to printable ASCII so the protocol's JSON stays valid UTF-8), exposed via `manufacturerName()` / `productName()` and surfaced in `list_devices` / `device_attached`. The strings are read straight from `dev->strbuf` — populated before `claim_collection()` fires, so `mydevice = dev` plus the inherited `product()` accessor is enough. **Hat Switch (D-pad)** is a first-class input type alongside button and axis: each device tracks `m_hat` (0..7 direction, or `HAT_RELEASED = 0xFF`) and `m_hasHat` (set true on first hat report). Bindings of type `INPUT_HAT` match a specific direction strictly — diagonals don't fire cardinal bindings. For lenient matching (e.g. "N or NE" → shift up), bind multiple slots on the same channel. **Keyboards** are tracked similarly: up to `MAX_KEYS_PRESSED = 6` simultaneously-pressed scancodes are kept in `m_keys[]`, `m_hasKeyboard` set on first key event. Bindings of type `INPUT_KEY` carry the HID Keyboard/Keypad scancode in `index` (0x04 = A, …, 0xE0..0xE7 = modifiers) and fire while that scancode is in the pressed set.
- `mapping.{h,cpp}` — `Config` schema, EEPROM I/O, `evalAxis` / `evalButton` evaluators, per-channel updaters that drive PWM pins and the pedal stream. **`Config` is 1132 bytes with `static_assert`-locked layout** (the STM32 port asserts the same size — that's the "byte-identical schema" guarantee); any field change without a matching size update is a compile error. Bump `CONFIG_VERSION` (`mapping.h`) on schema changes — EEPROMs from older versions are rejected and the firmware boots with defaults.
- `protocol.{h,cpp}` — Line-based JSON command dispatcher over USB CDC Serial (`ArduinoJson v7`). Lines starting with `{` are JSON commands; other characters go to the CLI callback. Emits async events (`device_attached`, `device_detached`, `live`, `outputs`) — rate-limited to ~50 Hz inputs / ~30 Hz outputs.
- `pedals.{h,cpp}` — CSL Elite V2 UART emulator on Serial3 (pins 14/15). State machine: STEP0 (250000 baud, expects `0x0A` → sends `0x1A`) → STEP1 (`0x05` → `0x15`) → STEP2 (switches to 115200, 12-byte framed query/response) → STREAMING (100 Hz pedal packets). **Boot warmup**: on startup we silently drain Serial3 for 2 s before engaging the handshake — gives the wheelbase a clean silence window to reset its end after a Teensy reboot (otherwise it hangs sending `0x0A` without following with `0x05`).
- `name.c` — Custom USB descriptor overrides (`usb_names.h`). Declares the board's USB Manufacturer Name as `"fanadapter"` and Product Name as `"Fanadapter v0.6.0"` to replace the generic `"USB Serial"` device string. The version here must track the firmware version reported by `protocol.cpp` (`ver`) and the `firmware.ino` boot banner — bump all three together. `PRODUCT_NAME_LEN` is the character count and must match if the string length changes.
- `firmware.ino` — orchestrator only. USB host instances (`USBHIDParser × 8`, `USBHub × 2`), pin/PWM setup (PIN_X=4, PIN_Y=5, PIN_SEQ_UP=6, PIN_SEQ_DOWN=7, PIN_HANDBRAKE=8, 12-bit PWM @ 36 kHz), minimal CLI (`u`/`p`/`X`/`?`).

### Channel model

There are **14 output channels**: 8 gears (`gear_R`, `gear_1`..`gear_7`), 2 sequential (`shift_up`, `shift_down`), `handbrake`, `throttle`, `brake`, `clutch`. Each carries up to `MAX_BINDINGS_PER_CHANNEL = 4` `InputBinding` slots. Buttons OR together across slots; axes MAX together. Cross-type aware: an axis can drive a button channel (post-scale threshold), a button can drive an axis channel (emits 0 / 65535). Empty slots have `type: INPUT_NONE`.

Same-VID/PID device aggregation across the 8 host pool slots is layered *under* the per-channel aggregation — i.e. multiple physical devices reporting the same VID/PID get their buttons OR'd and axes MAX'd before evaluation.

### H-pattern shifter modes

`updateShifter()` has two modes selected by `Config.gearMode`:

- **`hold`** (default, real H-shifter semantics): gear active only while its binding is held. 0 active → neutral, 1 → that gear, **2+ → neutral** (defensive: when two switches close simultaneously, staying in neutral is safer than picking a random gear). A binding on `gear_N` acts as a panic-neutral override.
- **`latch`** (keyboard / gamepad friendly): rising-edge on any gear binding *switches* the current gear; it stays there until another rising edge moves it elsewhere — including `gear_N` as the explicit "shift to neutral" key. First edge per tick wins on simultaneous presses. No defensive multi-press handling — the user opted into this mode deliberately.

`gear_N` is bindable in both modes (it's slot 8 of `Config.gear[9]`). Gear transitions between non-neutral positions insert a `NEUTRAL_TRANSIT_MS` (50 ms) delay so the wheelbase sees a release before the next latch.

### Handbrake routing

`updateHandbrake()` writes the same value to **both** the PWM pin and the pedal stream's handbrake field. Modern Fanatec firmware (DD+) prefers the pedal-stream value when the pedal port is present; the dedicated handbrake RJ12 is left unplugged in this build. The dual write exists so the wiring stays forward-compatible.

### Cross-file invariants (firmware ↔ webconfig ↔ SimHub plugin)

The firmware contracts now have **three** consumers, and a schema change has to move all of them together — the firmware, the TypeScript, and the C#. Missing one is silent: nothing fails to compile, the client just misreads the device.

| Firmware | Webconfig | SimHub plugin | What must match |
|---|---|---|---|
| `mapping.h` (`Config`, `InputBinding`, `ChannelBindings`) | `webconfig/src/lib/types.ts` | `Fanadapter.Core/Model.cs` | Field names, types, channel keys. JSON wire shape must round-trip. |
| `mapping.cpp` (`scaleAxis`) | `webconfig/src/lib/scaleAxis.ts` | `Fanadapter.Core/ScaleAxis.cs` | Identical math. Every client's "processed" preview must mirror what the firmware actually writes. |
| `mapping.cpp` (CRC-32/ISO-HDLC) | `webconfig/src/lib/crc32.ts` | — | Same polynomial / init / final-XOR. Currently unused on the JS side but reserved for preset validation. |
| — | `App.tsx` capture flow | `Fanadapter.Core/CaptureEngine.cs` | Same three-phase thresholds and commit maths. Not a firmware contract, but a divergence means the two clients calibrate the same pedal differently. |
| — | `src/lib/dfu.ts` (DfuSe flasher) | `Fanadapter.Core/DfuseFlasher.cs` | Same DfuSe sequence (set-address before every block, wBlockNum=2, leave = zero-length DNLOAD), suffix strip, plausibility check, sector-erase policy. The contract is ST's ROM (AN3156), not our firmware — but a divergence means the two clients flash differently. Both consume the Pages deploy's `firmware/fanadapter-stm32.{json,bin}`. |
| `protocol.cpp` (`CHANNEL_NAMES`, JSON command shapes) | `webconfig/src/lib/serial.ts`, `types.ts` (`CHANNELS`) | `Fanadapter.Core/Protocol.cs`, `Model.cs` | Channel name strings, command names, request/response shapes. |

Both clients defensively accept the v2 (array) and legacy v1 (single object) channel shapes so they keep rendering against older firmware — `getChannelBindings()` in types.ts, `ChannelBindingsConverter` in Model.cs. Keep both fallbacks when changing the schema.

`scaleAxis` is the one contract with executable tripwires on the client side: `webconfig/src/lib/scaleAxis.test.ts` and `simhub-plugin/tests/.../ScaleAxisTests.cs` assert the same vectors. Change the firmware math and both suites should be updated in the same commit.

### Capture flow (webconfig)

`webconfig/src/App.tsx` implements a three-phase Listen capture for each binding slot:

1. **`baseline`** (~400 ms) — sample idle button mask + axis min/max per slot to learn the noise floor.
2. **`active`** — wait for the first new button bit or an axis deviating from baseline midpoint by `max(noise × 5, 500)`. Buttons commit immediately; axes latch and move to phase 3.
3. **`tracking`** — accumulate `peakHigh` / `peakLow` on the latched axis. Commit when the axis returns to within `max(noise × 2, 200)` of baseline. Direction (rising vs falling) is decided by whichever side travelled further; the descending case sets `invert: true`.

Baseline accumulation lives in a `useRef` (not React state) to avoid render loops on every live event. `setCapturing` is only called at discrete phase transitions / commit.

### Serial protocol queue (webconfig)

`SerialClient` (in `webconfig/src/lib/serial.ts`) is a FIFO request/response queue over WebSerial. Events (`{"event":"..."}`) fan out to subscribers; non-event JSON is matched to the next pending request. Non-JSON lines surface as `{type: "log"}` events that the UI's Logs tab renders.

## Things to know before editing

- **`CONFIG_VERSION` bumps wipe user EEPROMs on next flash.** The firmware boots with empty bindings on version mismatch (defaults preserved for gear DAC voltages and pulse width). Mention this in commit messages when applicable.
- **Pedal handshake desync survives soft reboots.** A `{"cmd":"reboot"}` (or any firmware re-flash) doesn't reset the wheelbase's UART state. The 2-second boot warmup helps, but recovery sometimes needs the **Re-arm pedals handshake** button in the Outputs tab (calls `pedalsForceReset` → `cmd: reset_pedals`) or a physical USB replug.
- **`0x7B`-framed bytes during STREAMING are normal.** The wheelbase keeps emitting stale STEP 2 queries even after handshake completes — don't interpret them as a re-handshake request. The `STATE_STREAMING` byte handler ignores them.
- **Multiple devices can share a channel.** Conflict-clearing across channels was tried and removed — one physical input can deliberately drive multiple outputs (e.g. one button bound to both `gear_R` and `shift_down`). Defensive neutral handles the H-pattern multi-active case.
- **Loop dispatch order.** `pedalsUpdate()` runs last in the main loop. If you add new long-running work to `mappingTick()` or `protocolTick()`, the pedal stream's 100 Hz cadence can slip and the wheelbase will drop pedals.
- **WebSerial holds COM exclusively.** The firmware uploader and any other serial tool can't talk to the Teensy while the UI is connected. Disconnect first, or use the JSON `reboot` command if you just need a restart.
- **(STM32) Never gate CDC output on `tud_cdc_connected()`.** That also requires the host to assert DTR, and `webconfig` deliberately *deasserts* DTR on connect (so opening a port can't reset an MCU sitting behind a CH340 bridge). Gating on it makes the firmware receive and execute commands but never reply — every request dies with `command timeout`. Use **`tud_mounted()`**. Same trap for any new inbound path: the console must drain **both** the USART1 ring and `tud_cdc_read()`.
- **(STM32) rhport numbers are fixed by hardware; roles are not.** OTG_FS is *always* rhport 0 and OTG_HS *always* rhport 1, so the ISRs must pass those literals — hence `OTGFS_RHPORT`/`OTGHS_RHPORT` kept separate from the role aliases `DEVICE_RHPORT`/`HOST_RHPORT` in `main.c`. Wiring an ISR to a *role* alias breaks silently the moment roles swap.
- **(STM32) The on-board USB-C can only ever be a USB *device*.** It's wired as a UFP receptacle (CC pulled down with Rd) — which is why DFU works through it, and why a hub plugged into it never attaches (both ends present as devices; no adapter fixes it). The host must hang off the PB14/PB15 header. And **a hub needs VBUS on its upstream port to detect a host** — feed it from the board's 5 V or it silently never attaches.
- **(STM32) Composite controllers expose input-less HID interfaces.** The Logitech RS Shifter & Handbrake and RS H-Shifter each present a second HID interface declaring no axes/buttons/hat/keys. `usb_input_on_mount()` rejects those (returning `false` so `tuh_hid_mount_cb` doesn't arm their pipe) — otherwise 4 physical devices consume 7 of the 8 pool slots and hold host channels for interfaces nothing can bind to.
- **(STM32) Console output is split into two policies and the split is load-bearing.** Console writes used to transmit inline, so the `live` / `outputs` events blocked the main loop for ~5.5 / ~10.5 ms a line at 115200 8E1 — with both streams on (what every client enables on connect) **over half of every second**, which collapsed `pedals_update()`'s 10 ms cadence: inputs froze at their last values and gear changes arrived a minute late. Now USART1 output goes through an interrupt-driven TX ring, and `console_printf` (responses, CLI — must arrive intact) waits for room while `console_event_printf` / `console_event_write` (telemetry only — superseded 30×/s) drop when the ring is over half full. **Keep events on the droppable path and never put a blocking write on a hot path.** Two traps if you touch this: bound sink waits by *wall-clock*, never iteration count (a count-based guard silently truncated `get_config` and timed out every client), and a line assembled in pieces must be emitted atomically via `console_event_write` or a partial drop puts malformed JSON on the wire. Two USB-side "fixes" were tried before the cause was found and both were wrong — do-not-repeat list in PORT-STATUS.
- **(STM32) Check the chip before debugging a "dead" WeAct board.** The MiniSTM32H7xx PCB ships with an H743VIT6 *or* a visually identical **STM32H723VGT6**, which has only **one** USB controller (OTG_HS, no OTG_FS) and cannot run this firmware — H743-built code hangs in clock init, giving a dark LED, 0 V on the USB pins and no console, while DFU still works (the ROM sets its own clock). Device ID: H743 = `0x450`, H723 = `0x483`.
- **(SimHub) `live` events only fire on change; `outputs` events stream continuously.** Verified against the board: with nothing moving, `live_inputs` produces *zero* frames while `live_outputs` keeps sending. So "I enabled telemetry and saw nothing" is not evidence of a broken client — it's the expected idle state, and a client must not treat silence as a dropped link.
- **(SimHub) The command round-trip is ~13 ms, so request/response streaming tops out near 77 Hz.** Measured over the STM32's native USB CDC: 100 sequential `set_outputs` took 1299 ms. That's the ceiling for anything that waits for each reply, and it is well above human pedal bandwidth, so `DriveController` self-limits rather than pipelining — pipelining would trade the queue's back-pressure for unbounded growth whenever the adapter fell behind. Don't write "100 Hz" into a comment without measuring it.
- **(SimHub) A timed-out request must still consume its reply.** The firmware answers strictly in order, so dropping a timed-out entry from the queue (which `serial.ts` does) means a late reply gets matched to the *next* request and every response after it is off by one. `Fanadapter.Core/SerialClient.cs` deliberately keeps the entry queued and marked abandoned so the stale reply is absorbed. Any new client should do the same.
- **(SimHub) Marshal to the dispatcher explicitly; do not rely on an await resuming on the UI thread.** `AdapterSession` awaits with `ConfigureAwait(false)` internally, so continuations in the view model land on the thread pool. WPF then throws from two different places — raising `CanExecuteChanged` (it reads the bound Button's `Command`) and mutating a bound `ObservableCollection`. This bit three times during the port, each time in a new spot, which is why `MainViewModel` routes property notifications, command state, log appends **and** whole rebuild methods through `RunOnUi`. Note a tab whose contents WPF hasn't realised yet has no collection view attached, so this failure hides until someone opens that tab.
- **(webconfig) The firmware updater's device picker MUST stay on its own button.** Chromium requires a *fresh* user gesture for `navigator.usb.requestDevice()`, and by the time the update flow discovers the grant is missing (after the serial reboot + bus wait), the original click's activation is spent — calling the picker there throws. Both grants persist per origin+device (`usb.getDevices()` / `serial.getPorts()`), which is what makes every update after the first fully hands-free; don't "simplify" the granted-device paths away. Related: entering the bootloader from software works only via the token + reset + early-branch path in `main.c` (a late branch serves no ROM interfaces — twice-bisected; see PORT-STATUS), and on Windows WebUSB only sees the DFU device when WinUSB is bound to it.
- **(SimHub) Newtonsoft comes from NuGet with `ExcludeAssets="runtime"`, pinned to the version SimHub ships (13.0.4).** Referencing SimHub's copy by `HintPath` instead would make `Fanadapter.Core` — and therefore its tests — unbuildable without SimHub installed. Excluding the runtime asset keeps the DLL out of our output so the CLR loads SimHub's single copy. If SimHub bumps Newtonsoft, bump the pin.
- **No agent attribution on commits.** Do not add Co-Authored-By or similar attribution lines for AI assistants on commits in this repo.
- **Repo root is the directory containing this file.** `firmware/`, `webconfig/`, `firmware-stm32/` and `simhub-plugin/` are siblings. The Arduino sketch convention requires `firmware/firmware.ino` (folder name = .ino name) — don't rename one without the other.
- **Keep docs in sync with the code.** When a change affects something documented here or in a README, update both in the same commit. Things that warrant a doc edit:
  - Schema bumps (`CONFIG_VERSION`, new fields, layout changes) → `AGENTS.md` cross-file invariants table, `firmware/README.md` JSON command reference, preset shape in `webconfig/README.md`, **and `Fanadapter.Core/Model.cs`**.
  - New / renamed / removed JSON commands or events → JSON command reference in `firmware/README.md`, the serial client in `webconfig/src/lib/serial.ts`, **`Fanadapter.Core/Protocol.cs`**, and the cross-file table here.
  - New channel keys or output behavior → channel model section here, the channel list in `firmware/README.md`, the `CHANNELS` table in `webconfig/src/lib/types.ts`, and `Schema` in `Fanadapter.Core/Model.cs`.
  - Module additions / renames / loop-order changes → firmware module layout section here and the README's firmware section.
  - Hardware wiring or pin changes → `firmware/README.md` wiring reference (Teensy) or `firmware-stm32/README.md` pin map (STM32); mention the pin in the firmware module layout here if it's surfaced as a constant.
  - Behavior gotchas that you debugged through (handshake quirks, edge cases) → add a bullet to "Things to know before editing" so the next agent doesn't re-derive it.

  If unsure whether a change is doc-worthy, ask: "would the next agent waste time re-discovering this?" If yes, document it.
