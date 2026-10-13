# AGENTS.md

This file provides guidance for AI coding agents working in this repository.

## What this repo is

`fanadapter` is a USB HID → Fanatec wheelbase adapter (STM32H743) plus a browser-based WebSerial UI for configuring it, and a SimHub plugin that drives the same protocol from the PC:

- **`webconfig/`** — Vite + React 19 + TypeScript + Tailwind + shadcn/ui app that talks to the firmware over WebSerial (line-based JSON). Also a one-click STM32 firmware updater: the firmware workflow publishes an immutable GitHub release, then Pages mirrors `firmware/fanadapter-stm32.{json,bin}` beside the app, and the header's Flash dialog reboots the board into its ROM bootloader (`{"cmd":"dfu"}`), flashes over WebUSB/DfuSe (`src/lib/dfu.ts`) via the persisted device grant, and reconnects over the persisted serial grant — picker only on the first update per machine. Deployed to GitHub Pages by `.github/workflows/pages.yml` on push to `main` (and after successful firmware-release workflow completion; Pages never compiles firmware).
- **`firmware-stm32/`** — **C** firmware for an **STM32H743** (WeAct MiniSTM32H743VITX): PlatformIO + STM32Cube HAL + TinyUSB. Speaks the WebSerial JSON protocol and `Config` schema mirrored by webconfig and the SimHub plugin, **hardware-validated end-to-end on a real wheelbase** (M0–M7): USB-host HID decode, the mapping evaluators, DAC H-pattern on PA4/PA5, sequential pulses on PC6/PC7, a TIM3 handbrake-PWM fallback on PC8, and the CSL Elite pedal-port UART emulator on USART2/PA2-PA3 (with wheelbase-power-cycle auto-recovery), plus a **direct-output** command set (`set_gear` / `set_outputs` / `pulse_shift` / `release_outputs`, and since protocol 6 `stream_axes` — a no-reply `set_outputs` twin for the 100 Hz PC pedal path) for PC/SimHub-driven output that overrides the USB-device mapping, and a **`dfu`** command that reboots into the ROM bootloader for a hands-free reflash — webconfig's Flash dialog drives it. Three USB-host diagnostics — **`usb_status`** (per-slot pipe state + raw dwc2 host-channel dump + patched-in counters), **`usb_kick`** (abort + re-arm every pipe; measured to NOT revive a wedged one), **`usb_stall`** (skip `tuh_task()` for N ms — the reproducer that settled it) — closed the "hub device goes silent while still enumerated" freeze: **the Simnet pedal's own firmware hangs when unpolled ~2 s mid-stream** (measured threshold 1.6–2.0 s), and old adapter firmware created such gaps; current firmware keeps polling gaps ≤~120 ms, and **nothing added to the main loop may ever stall it near 1 s** (the one residual offender is `save_config`'s ~1 s IRQ-off flash erase). Full investigation record — including three real-but-not-causal dwc2 bugs fixed via `apply_tinyusb_patches.py` (a build-time patch step that fails the build if it can't verify itself) and the cautionary tale of the "every code fact right, mechanism still impossible" queue-overflow theory — in PORT-STATUS. Reflashing preserves saved config regardless of path (config lives in bank 2; DFU and the UART flasher both erase only bank-1 sectors). **Both USB controllers are used at once: OTG_FS (the on-board USB-C) is the CDC device — webconfig + console + DFU + power — and OTG_HS (PB14/PB15 header) is the USB host for the powered hub. That split is forced by hardware: the USB-C is a device receptacle (UFP/Rd), so a hub plugged into it can never attach.** Two docs, don't conflate them: **`firmware-stm32/README.md`** is the user-facing build & wiring guide (board choice, pin map, wiring, flashing, troubleshooting), and **`firmware-stm32/PORT-STATUS.md`** is the development record (milestones, fixed bugs and why, gotchas) — **read PORT-STATUS before touching the STM32 firmware.**

- **`simhub-plugin/`** — **C#** ([SimHub](https://www.simhubdash.com/)) plugin, .NET Framework 4.8 + WPF, that speaks the same line-based JSON protocol over a COM port. Two projects: **`Fanadapter.Core`** holds the transport, protocol, schema, `ScaleAxis` and `CaptureEngine` and has **no SimHub references** (so its xunit tests run without SimHub installed), and **`Fanadapter.SimHub`** is the plugin itself. It splits by where the input device is plugged in: devices on the **PC** are captured and mapped by SimHub's own input system, and the plugin only turns the result into direct-output commands (`DriveController`); devices on the **adapter's USB hub** are invisible to the PC, so for those the plugin mirrors webconfig's configuration UI (Devices / Mappings / Outputs tabs). Also carries the one-click firmware updater (`FirmwareUpdateViewModel` + `Fanadapter.Core/DfuseFlasher.cs` over WinUSB — no device picker at all, unlike webconfig's WebUSB variant). Full feature parity with webconfig **except** the guided setup wizard and presets. Hardware-validated against the STM32 adapter. Build, install and usage: `simhub-plugin/README.md`.

Build and board wiring: `firmware-stm32/README.md`. RJ12 pinouts and assembly: `schematics/README.md`. Canonical protocol, schema and calibration reference: `firmware-stm32/PROTOCOL.md`. Webconfig deploy + dev: `webconfig/README.md`. This file only covers the parts that need cross-file context.

## Commands

### Firmware (STM32)

```sh
python -m platformio run -d firmware-stm32 -e weact_h743

# Board must be in ROM DFU mode; use BOOT0/RST for first flash or {"cmd":"dfu"} thereafter.
python -m platformio run -d firmware-stm32 -e weact_h743 -t upload
```

Requires Python + PlatformIO. Browser and SimHub updaters consume the firmware published in GitHub Releases (mirrored on Pages for browser CORS) and preserve saved config in flash bank 2. `{"cmd":"reboot"}` restarts the application without reflashing. There are no firmware tests; build and hardware validation are the checks.

### Webconfig

```sh
cd webconfig
npm install
npm run dev         # Vite dev server at http://localhost:5173/fanadapter/
npm run build       # static bundle in dist/
npm run lint        # ESLint
```

Base path defaults to `/fanadapter/` (override with `VITE_BASE=/path/`). WebSerial only works in Chromium-based browsers on desktop.

Run `npm test` for the axis-scaling contract vectors; `npm run format:check`, `npm run lint`, and `npm run build` are the other CI checks.

### SimHub plugin

```sh
dotnet build simhub-plugin/FanadapterSimHub.sln -c Release
dotnet test  simhub-plugin/FanadapterSimHub.sln -c Release

# Core-only tests need no SimHub installation:
dotnet test simhub-plugin/tests/Fanadapter.Core.Tests/Fanadapter.Core.Tests.csproj -c Release

# Build and drop both DLLs into the SimHub install (close SimHub first — it
# holds loaded plugin assemblies open):
dotnet build simhub-plugin/src/Fanadapter.SimHub/Fanadapter.SimHub.csproj -c Release -p:InstallToSimHub=true
```

Needs the .NET SDK 8+ only; the net48 reference assemblies come from a NuGet package. SimHub's own DLLs are resolved by `HintPath` off the `$(SimHubDir)` MSBuild property (default `C:\Program Files (x86)\SimHub\`, overridable in an untracked `simhub-plugin/Directory.Build.props.user` — which is exactly what CI writes after installing the version pinned in `.simhub-version`).

The full solution also runs `Fanadapter.SimHub.Tests`: STA/WPF lifecycle tests using the configured SimHub assemblies and an in-memory adapter. They never open a physical COM port. Core tests remain independently runnable without SimHub.

## Architecture

### Firmware module layout (`firmware-stm32/src/`)

`main.c` dispatches the loop in this order — order matters:

```
tuh_task() → tud_task() → console_cli_poll() → usb_input_task(now)
  → mapping_tick() → protocol_tick(now) → pedals_update()
```

- `usb_input.{c,h}` — 8-slot HID device pool; descriptor-derived axes/buttons/hat/keyboard capabilities, sanitized manufacturer/product strings, and report-pipe monitoring. Rejects input-less HID interfaces. Strict hat-direction bindings select 0..7; released is 0xFF. Keyboard bindings use HID scancodes and support six pressed keys.
- `hid_parse.{c,h}` — descriptor walker and native-resolution report decoder. `input_source.{c,h}` folds matching VID/PID devices through an input-source vtable (OR buttons/hat/key, MAX axes).
- `mapping.{c,h}` — `Config` schema, axis/button evaluators, hold/latch gear selection, tests and sticky direct-output overrides. `Config` is 1132 bytes with `_Static_assert`-locked layout; bump `CONFIG_VERSION` on field changes. `config_store.{c,h}` persists it in flash bank 2.
- `outputs.{c,h}` — DAC H-pattern on PA4/PA5, non-blocking neutral transit, open-drain sequential pulses on PC6/PC7, optional handbrake PWM on PC8.
- `pedals.{c,h}` — USART2 on PA2 (TX) / PA3 (RX), 8N1. INIT → STEP0 (250000 baud, 0x0A → 0x1A) → STEP1 (0x05 → 0x15) → STEP2 (115200 baud query/ack) → STREAMING (100 Hz pedal frames). Drains RX silently for 2 s on boot; detects wheelbase restarts via UART error bursts.
- `protocol.{c,h}` + `json_min.{c,h}` — line-based JSON dispatcher and telemetry over USB CDC / USART1. Inputs emit on change up to ~30 Hz; outputs stream ~30 Hz. `FW_VER` in `firmware_info.h` supplies protocol, USB labels, boot banner and the release manifest.
- `usb_descriptors.c` — native CDC identity VID 1209 / PID FA00. `main.c` owns clocks, hardware init, console TX policies and CLI (`dfu`, `reboot`, `?`).

### Channel model

There are **14 output channels**: 8 gears (`gear_R`, `gear_1`..`gear_7`), 2 sequential (`shift_up`, `shift_down`), `handbrake`, `throttle`, `brake`, `clutch`. Each carries up to `MAX_BINDINGS_PER_CHANNEL = 4` `InputBinding` slots. Buttons OR together across slots; axes MAX together. Cross-type aware: an axis can drive a button channel (post-scale threshold), a button can drive an axis channel (emits 0 / 65535). Empty slots have `type: INPUT_NONE`.

Same-VID/PID device aggregation across the 8 host pool slots is layered *under* the per-channel aggregation — i.e. multiple physical devices reporting the same VID/PID get their buttons OR'd and axes MAX'd before evaluation.

### H-pattern shifter modes

`update_shifter()` has two modes selected by `Config.gearMode`:

- **`hold`** (default, real H-shifter semantics): gear active only while its binding is held. 0 active → neutral, 1 → that gear, **2+ → neutral** (defensive: when two switches close simultaneously, staying in neutral is safer than picking a random gear). A binding on `gear_N` acts as a panic-neutral override.
- **`latch`** (keyboard / gamepad friendly): rising-edge on any gear binding *switches* the current gear; it stays there until another rising edge moves it elsewhere — including `gear_N` as the explicit "shift to neutral" key. First edge per tick wins on simultaneous presses. No defensive multi-press handling — the user opted into this mode deliberately.

`gear_N` is bindable in both modes (it's slot 8 of `Config.gear[9]`). Gear transitions between non-neutral positions insert a `NEUTRAL_TRANSIT_MS` (50 ms) delay so the wheelbase sees a release before the next latch.

### Handbrake routing

`update_handbrake()` writes the same value to **both** the PWM pin and the pedal stream's handbrake field. Modern Fanatec firmware (DD+) prefers the pedal-stream value when the pedal port is present; the dedicated handbrake RJ12 is left unplugged in this build. The dual write exists so the wiring stays forward-compatible.

### Cross-file invariants (firmware ↔ webconfig ↔ SimHub plugin)

**`firmware-stm32/` is the source of truth.** Contract changes start there and move to TypeScript and C# in the same commit. Missing a consumer is silent: nothing fails to compile, but a client can misread the device. `firmware-stm32/PROTOCOL.md` is the canonical prose reference.

| Firmware (`firmware-stm32/`) | Webconfig | SimHub plugin | What must match |
|---|---|---|---|
| `src/mapping.h` (`Config`, `InputBinding`, `ChannelBindings`) | `webconfig/src/lib/types.ts` | `Fanadapter.Core/Model.cs` | Field names, types, channel keys. JSON wire shape must round-trip; firmware asserts the 1132-byte stored layout. |
| `src/mapping.c` (`scale_axis`) | `webconfig/src/lib/scaleAxis.ts` | `Fanadapter.Core/ScaleAxis.cs` | Identical math; processed previews must mirror firmware outputs. |
| `src/mapping.c` (CRC-32/ISO-HDLC) | `webconfig/src/lib/crc32.ts` | — | Same polynomial / init / final-XOR; JS reserved for preset validation. |
| — | `App.tsx` capture flow | `Fanadapter.Core/CaptureEngine.cs` | Same three-phase thresholds and commit math. |
| — | `src/lib/dfu.ts` | `Fanadapter.Core/DfuseFlasher.cs` | Same DfuSe sequence, suffix strip, plausibility checks and bank-1-only erase policy. Both verify the same GitHub release SHA-256. Webconfig uses Pages' mirror; SimHub downloads the release directly. |
| `src/protocol.c` (command shapes, `PROTO_VERSION`) | `webconfig/src/lib/serial.ts`, `types.ts` (`CHANNELS`) | `Fanadapter.Core/Protocol.cs`, `Model.cs` | Channel/command names and payloads. Current protocol is 6; keep capability checks for older or unrecognized firmware (`SupportsDirectOutput`, `SupportsAxisStreaming`, `SupportsDfu`). |

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

- **`CONFIG_VERSION` bumps invalidate saved flash configuration on next boot.** The firmware boots with empty bindings on version mismatch (defaults preserved for gear DAC voltages and pulse width). Mention this in commit messages when applicable.
- **Pedal handshake desync survives soft reboots.** A `{"cmd":"reboot"}` (or any firmware re-flash) doesn't reset the wheelbase's UART state. The 2-second boot warmup helps, but recovery sometimes needs the **Re-arm pedals handshake** button in the Outputs tab (calls `pedals_force_reset` → `cmd: reset_pedals`) or a physical USB replug.
- **`0x7B`-framed bytes during STREAMING are normal.** The wheelbase keeps emitting stale STEP 2 queries even after handshake completes — don't interpret them as a re-handshake request. The `STATE_STREAMING` byte handler ignores them.
- **Multiple devices can share a channel.** Conflict-clearing across channels was tried and removed — one physical input can deliberately drive multiple outputs (e.g. one button bound to both `gear_R` and `shift_down`). Defensive neutral handles the H-pattern multi-active case.
- **Loop dispatch order.** `pedals_update()` runs last in the main loop. If you add new long-running work to `mapping_tick()` or `protocol_tick()`, the pedal stream's 100 Hz cadence can slip and the wheelbase will drop pedals.
- **WebSerial holds COM exclusively.** The firmware uploader and any other serial tool can't talk to the adapter while the UI is connected. Disconnect first, or use the JSON `reboot` command if you just need a restart.
- **(STM32) Never gate CDC output on `tud_cdc_connected()`.** That also requires the host to assert DTR, and `webconfig` deliberately *deasserts* DTR on connect (so opening a port can't reset an MCU sitting behind a CH340 bridge). Gating on it makes the firmware receive and execute commands but never reply — every request dies with `command timeout`. Use **`tud_mounted()`**. Same trap for any new inbound path: the console must drain **both** the USART1 ring and `tud_cdc_read()`.
- **(STM32) rhport numbers are fixed by hardware; roles are not.** OTG_FS is *always* rhport 0 and OTG_HS *always* rhport 1, so the ISRs must pass those literals — hence `OTGFS_RHPORT`/`OTGHS_RHPORT` kept separate from the role aliases `DEVICE_RHPORT`/`HOST_RHPORT` in `main.c`. Wiring an ISR to a *role* alias breaks silently the moment roles swap.
- **(STM32) The on-board USB-C can only ever be a USB *device*.** It's wired as a UFP receptacle (CC pulled down with Rd) — which is why DFU works through it, and why a hub plugged into it never attaches (both ends present as devices; no adapter fixes it). The host must hang off the PB14/PB15 header. And **a hub needs VBUS on its upstream port to detect a host** — feed it from the board's 5 V or it silently never attaches.
- **(STM32) Composite controllers expose input-less HID interfaces.** The Logitech RS Shifter & Handbrake and RS H-Shifter each present a second HID interface declaring no axes/buttons/hat/keys. `usb_input_on_mount()` rejects those (returning `false` so `tuh_hid_mount_cb` doesn't arm their pipe) — otherwise 4 physical devices consume 7 of the 8 pool slots and hold host channels for interfaces nothing can bind to.
- **(STM32) Console output is split into two policies and the split is load-bearing.** Console writes used to transmit inline, so the `live` / `outputs` events blocked the main loop for ~5.5 / ~10.5 ms a line at 115200 8E1 — with both streams on (what every client enables on connect) **over half of every second**, which collapsed `pedals_update()`'s 10 ms cadence: inputs froze at their last values and gear changes arrived a minute late. Now USART1 output goes through an interrupt-driven TX ring, and `console_printf` (responses, CLI — must arrive intact) waits for room while `console_event_printf` / `console_event_write` (telemetry only — superseded 30×/s) drop when the ring is over half full. **Keep events on the droppable path and never put a blocking write on a hot path.** Three traps if you touch this: bound sink waits by *wall-clock*, never iteration count (a count-based guard silently truncated `get_config` and timed out every client); a line assembled in pieces must be emitted atomically via `console_event_write` (each call drops independently); and **a lossy sink must drop whole lines, never partial** — the CDC leg once wrote what fit and bailed, putting a truncated event glued to the next intact reply on the wire, which destroyed both and permanently desynced the clients' strictly-FIFO reply matching (every request timed out, every reply arrived "stale"; `SerialClient.cs` now self-heals via a `version` probe after two consecutive stale absorptions, but the firmware must still never emit a partial line). A fourth trap wore a disguise: the non-lossy path also mirrored `get_config` (~8 KB) to USART1 *with backpressure* even when the client was on CDC — an 11.5 KB/s mirror nobody was reading held the loop ~720 ms, the wheelbase's link watchdog gave up, and its 250000-baud re-handshake registered as a spurious "RX error burst / wheelbase restart?". When `tud_mounted()`, the USART1 leg must mirror-what-fits and never wait. Two USB-side "fixes" were tried before the cause was found and both were wrong — do-not-repeat list in PORT-STATUS.
- **(STM32) Check the chip before debugging a "dead" WeAct board.** The MiniSTM32H7xx PCB ships with an H743VIT6 *or* a visually identical **STM32H723VGT6**, which has only **one** USB controller (OTG_HS, no OTG_FS) and cannot run this firmware — H743-built code hangs in clock init, giving a dark LED, 0 V on the USB pins and no console, while DFU still works (the ROM sets its own clock). Device ID: H743 = `0x450`, H723 = `0x483`.
- **(SimHub) `live` events only fire on change; `outputs` events stream continuously.** Verified against the board: with nothing moving, `live_inputs` produces *zero* frames while `live_outputs` keeps sending. So "I enabled telemetry and saw nothing" is not evidence of a broken client — it's the expected idle state, and a client must not treat silence as a dropped link.
- **(SimHub) Pedal streaming uses `stream_axes` (protocol ≥ 6) — a no-reply `set_outputs` twin — NOT the acked path.** The acked round-trip measured 13 ms pre-#33 and 7.9 ms after (100 sequential `set_outputs`), which caps a reply-waiting streamer at ~77–127 Hz and injects the ack wait as jitter. `stream_axes` answers NOTHING: `DriveController` fires latest-values at a fixed 100 Hz (matching the wheelbase pedal-frame rate) with no change suppression, and because no reply exists it never touches the clients' strictly-FIFO reply matching (`SerialClient.Post` / `serial.ts streamAxes` — a *replying* command must never be sent fire-and-forget, its reply would surface unmatched). Old firmware falls back to the acked path with change suppression + 1 s keep-alive. Don't write "100 Hz" into a comment without measuring it.
- **(SimHub) A timed-out request must still consume its reply.** The firmware answers strictly in order, so dropping a timed-out entry from the queue (which `serial.ts` does) means a late reply gets matched to the *next* request and every response after it is off by one. `Fanadapter.Core/SerialClient.cs` deliberately keeps the entry queued and marked abandoned so the stale reply is absorbed. Any new client should do the same.
- **(SimHub) "Driving pedals" is a persisted *intent*, and the two kinds of release are not interchangeable.** `DriveSettings.AxisStreamingEnabled` records that the user wants PC pedals, and `DriveController.ResumeIfEnabled()` re-applies it after **every** successful connect — not once per SimHub start, because SimHub rebuilds its plugins on every game change (`End`→`Init`), so a resume that only ran at startup would drop the pedals the first time the user switched games. That makes the split load-bearing: a release the *plugin* performs (shutdown, game change, disconnect) must leave the flag set or the feature evaporates, while a release the *user* asked for must clear it or the stream silently returns on the next game change. Hence `ReleaseAll()` never touches settings and `FanadapterPlugin.ReleaseOutputsByUser()` (the pane button + the bindable action) exists to revoke. Don't "simplify" the flag-clearing into `ReleaseAll` — `Dispose()` calls it, and `End()` saves settings *before* disposing, so it would half-persist a false the user never chose.
- **(SimHub) `DispatcherPriority.Background` is not a cadence, it's a maybe.** Inside an app as busy as SimHub a Background timer is starved well below its own interval — the pedal readout on a 200 ms Background timer was reported in the field as "2-3 fps". Anything the user watches move belongs on the Normal-priority live timer in `MainViewModel` (16 ms, sub-sampled per consumer); Background is only for work nobody is staring at, like the pedal-link poll. Pair a faster tick with change-suppressed setters — an idle pedal then costs nothing per tick, which is how ~30 Hz ends up cheaper than the 5 Hz it replaced.
- **(SimHub) Stop/Release must work even when settings persistence fails.** Revoke the in-memory intent and release outputs before attempting the save; log a save failure instead of allowing it to skip the release. Streaming-state notifications come from `DriveController`, so automatic resume updates an already-open pane too. `MainViewModel` subscribes only while attached and marshals those notifications to WPF.
- **(SimHub) Reattaching a pane is not a config reload.** Capture/clear replace editor bindings and pulse-width edits go straight to the adapter without updating every field of `AdapterSession.Config`. Rebuilding from that same snapshot on every `Loaded` silently undoes the displayed edits. Preserve editors while the config object is unchanged; rebuild when a reconnect or explicit reload supplies a new snapshot.
- **(SimHub) Pedals use SimHub's native axis picker, independently per channel.** The standard `AxisPickerControl` prefers Control Mapper roles; its public `AxisAssignment.GetValue()` supplies the latest processed 0–1 value, including picker direction and mapper filters. `RegisteredAxis.CurrentValue` is a slower UI copy, and `PluginManager.GetAxis` / `GetAllAxis` are internal APIs: use the native assignment rather than reflection or a custom picker. `AxisSourceReader` initializes assignments on the application dispatcher during plugin Init, shares them with the UI via weak-keyed storage, and follows late role registration even before a pane exists. Native assignments must be shared when replaced by the picker; Clear mutates the existing assignment. The old property picker and Detect flow are removed; old property bindings need reassignment through the native picker. Keep Core free of SimHub types by persisting only axis and movement names there, then apply the pedal's `InputMinPercent`/`InputMaxPercent` range (default 0–100%) before converting to 0–65535. The UI uses SimHub's `TitledRangeSlider` with two handles and percentage endpoint labels. This local adjustment is deliberate: a brake full-travel endpoint of 68.7% lowers the input needed for full output, even for a directly selected controller axis. It operates on the already processed SimHub value; do not reinterpret it as raw controller calibration or reapply the picker's direction. Invalid percentage ranges send no value and show a UI error. Preview and streaming must use the same `AxisSource.Scale` calculation.
- **(SimHub) Input telemetry follows the settings pane; output telemetry follows the connection.** The pane owns `live_inputs` for Devices and Listen capture, while dashboard properties need `live_outputs` even with the pane closed. Keep input enable/disable commands in pane-event order: wrapping each send in `Task.Run` lets a quick close/reopen transmit "off" after "on" and freeze the reopened pane. Track the protocol instance as well as the desired flag so a new connection is always re-enabled, and invalidate failed requests without undoing newer state.
- **(SimHub) Marshal to the dispatcher explicitly; do not rely on an await resuming on the UI thread.** `AdapterSession` awaits with `ConfigureAwait(false)` internally, so continuations in the view model land on the thread pool. WPF then throws from two different places — raising `CanExecuteChanged` (it reads the bound Button's `Command`) and mutating a bound `ObservableCollection`. This bit three times during the port, each time in a new spot, which is why `MainViewModel` routes property notifications, command state, log appends **and** whole rebuild methods through `RunOnUi`. Note a tab whose contents WPF hasn't realised yet has no collection view attached, so this failure hides until someone opens that tab. Two related traps, both of which produce the *same* field symptom — a pane that still takes button presses (WPF invokes commands directly on the view model) while every live readout sits frozen, and nothing in the log: (1) `Unloaded` must be paired with `Loaded`. WPF raises Unloaded whenever the control leaves the visual tree, navigating to another SimHub page does exactly that, and what comes back is the *same instance* — so `Detach()` without `Attach()` permanently stops the timers and unsubscribes the session events. Keep both idempotent, and re-`SyncFromSession()` on attach since everything that happened while detached was missed. (2) Take the dispatcher from `Application.Current`, never `Dispatcher.CurrentDispatcher` — the latter *creates* one for the calling thread if it has none, and a thread that never pumps runs nothing queued against it. Bind `DispatcherTimer`s to it explicitly for the same reason.
- **(webconfig) The firmware updater's device picker MUST stay on its own button.** Chromium requires a *fresh* user gesture for `navigator.usb.requestDevice()`, and by the time the update flow discovers the grant is missing (after the serial reboot + bus wait), the original click's activation is spent — calling the picker there throws. Both grants persist per origin+device (`usb.getDevices()` / `serial.getPorts()`), which is what makes every update after the first fully hands-free; don't "simplify" the granted-device paths away. Related: entering the bootloader from software works only via the token + reset + early-branch path in `main.c` (a late branch serves no ROM interfaces — twice-bisected; see PORT-STATUS), and on Windows WebUSB only sees the DFU device when WinUSB is bound to it.
- **(SimHub) Newtonsoft comes from NuGet with `ExcludeAssets="runtime"`, pinned to the version SimHub ships (13.0.4).** Referencing SimHub's copy by `HintPath` instead would make `Fanadapter.Core` — and therefore its tests — unbuildable without SimHub installed. Excluding the runtime asset keeps the DLL out of our output so the CLR loads SimHub's single copy. If SimHub bumps Newtonsoft, bump the pin.
- **No agent attribution on commits.** Do not add Co-Authored-By or similar attribution lines for AI assistants on commits in this repo.
- **Repo root is the directory containing this file.** `firmware-stm32/`, `webconfig/`, `schematics/` and `simhub-plugin/` are siblings.
- **Keep docs in sync with the code.** When a change affects something documented here or in a README, update both in the same commit. Things that warrant a doc edit:
  - Schema bumps (`CONFIG_VERSION`, new fields, layout changes) → `AGENTS.md` cross-file invariants table, `firmware-stm32/PROTOCOL.md` JSON command reference, preset shape in `webconfig/README.md`, **and `Fanadapter.Core/Model.cs`**.
  - New / renamed / removed JSON commands or events → JSON command reference in `firmware-stm32/PROTOCOL.md`, the serial client in `webconfig/src/lib/serial.ts`, **`Fanadapter.Core/Protocol.cs`**, and the cross-file table here.
  - New channel keys or output behavior → channel model section here, the channel list in `firmware-stm32/PROTOCOL.md`, the `CHANNELS` table in `webconfig/src/lib/types.ts`, and `Schema` in `Fanadapter.Core/Model.cs`.
  - Module additions / renames / loop-order changes → firmware module layout section here and the README's firmware section.
  - Hardware wiring or pin changes → `schematics/README.md` RJ12 wiring reference and `firmware-stm32/README.md` board pin map; mention the pin in the firmware module layout here if it's surfaced as a constant.
  - Behavior gotchas that you debugged through (handshake quirks, edge cases) → add a bullet to "Things to know before editing" so the next agent doesn't re-derive it.

  If unsure whether a change is doc-worthy, ask: "would the next agent waste time re-discovering this?" If yes, document it.
