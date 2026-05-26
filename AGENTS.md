# AGENTS.md

This file provides guidance for AI coding agents working in this repository.

## What this repo is

`fanadapter` is a USB HID → Fanatec wheelbase adapter (Teensy 4.1) plus a browser-based WebSerial UI for configuring it. Two halves that ship together:

- **`firmware/`** — Arduino sketch (Teensyduino) that runs on the Teensy 4.1. Enumerates USB HID joysticks, gamepads, and multi-axis controllers via the native USB host port and drives Fanatec RJ12 ports (H-pattern PWM, sequential open-drain, handbrake PWM, pedal-port UART).
- **`webconfig/`** — Vite + React 19 + TypeScript + Tailwind + shadcn/ui app that talks to the firmware over WebSerial (line-based JSON). Deployed to GitHub Pages by `.github/workflows/pages.yml` on push to `main`.

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

## Architecture

### Firmware module layout (`firmware/`)

Five translation units, loop-dispatched in `firmware.ino` in this order — order matters:

```
g_usb.Task() → pollUsbDriverStatus() → protocolTick() → mappingTick() → pedalsUpdate()
```

- `device_pool.{h,cpp}` — 8-slot pool of `GenericJoystickHID` consumers. Claims any joystick, gamepad, or multi-axis controller HID collection (any VID/PID), first-come-first-served. Axis/button counts are discovered lazily from observed reports — they start at 0 and grow.
- `mapping.{h,cpp}` — `Config` schema, EEPROM I/O, `evalAxis` / `evalButton` evaluators, per-channel updaters that drive PWM pins and the pedal stream. **`Config` is 1060 bytes with `static_assert`-locked layout**; any field change without a matching size update is a compile error. Bump `CONFIG_VERSION` (`mapping.h`) on schema changes — EEPROMs from older versions are rejected and the firmware boots with defaults.
- `protocol.{h,cpp}` — Line-based JSON command dispatcher over USB CDC Serial (`ArduinoJson v7`). Lines starting with `{` are JSON commands; other characters go to the CLI callback. Emits async events (`device_attached`, `device_detached`, `live`, `outputs`) — rate-limited to ~50 Hz inputs / ~30 Hz outputs.
- `pedals.{h,cpp}` — CSL Elite V2 UART emulator on Serial3 (pins 14/15). State machine: STEP0 (250000 baud, expects `0x0A` → sends `0x1A`) → STEP1 (`0x05` → `0x15`) → STEP2 (switches to 115200, 12-byte framed query/response) → STREAMING (100 Hz pedal packets). **Boot warmup**: on startup we silently drain Serial3 for 2 s before engaging the handshake — gives the wheelbase a clean silence window to reset its end after a Teensy reboot (otherwise it hangs sending `0x0A` without following with `0x05`).
- `name.c` — Custom USB descriptor overrides (`usb_names.h`). Declares the board's USB Manufacturer Name as `"fanadapter"` and Product Name as `"Fanadapter v0.3.0"` (the current firmware version) to replace the generic `"USB Serial"` device string.
- `firmware.ino` — orchestrator only. USB host instances (`USBHIDParser × 8`, `USBHub × 2`), pin/PWM setup (PIN_X=4, PIN_Y=5, PIN_SEQ_UP=6, PIN_SEQ_DOWN=7, PIN_HANDBRAKE=8, 12-bit PWM @ 36 kHz), minimal CLI (`u`/`p`/`X`/`?`).

### Channel model

There are **14 output channels**: 8 gears (`gear_R`, `gear_1`..`gear_7`), 2 sequential (`shift_up`, `shift_down`), `handbrake`, `throttle`, `brake`, `clutch`. Each carries up to `MAX_BINDINGS_PER_CHANNEL = 4` `InputBinding` slots. Buttons OR together across slots; axes MAX together. Cross-type aware: an axis can drive a button channel (post-scale threshold), a button can drive an axis channel (emits 0 / 65535). Empty slots have `type: INPUT_NONE`.

Same-VID/PID device aggregation across the 8 host pool slots is layered *under* the per-channel aggregation — i.e. multiple physical devices reporting the same VID/PID get their buttons OR'd and axes MAX'd before evaluation.

### H-pattern defensive neutral

`updateShifter()` counts how many gear bindings are active. 0 → neutral, 1 → that gear, **2+ → neutral**. This is intentional — when two switches close simultaneously (or a flaky binding double-fires), staying in neutral is safer than picking a random gear. Gear transitions between non-neutral positions insert a `NEUTRAL_TRANSIT_MS` (50 ms) delay so the wheelbase sees a release before the next latch.

### Handbrake routing

`updateHandbrake()` writes the same value to **both** the PWM pin and the pedal stream's handbrake field. Modern Fanatec firmware (DD+) prefers the pedal-stream value when the pedal port is present; the dedicated handbrake RJ12 is left unplugged in this build. The dual write exists so the wiring stays forward-compatible.

### Cross-file invariants (firmware ↔ webconfig)

The two halves share four data contracts — change one, you must change the other:

| Firmware | Webconfig | What must match |
|---|---|---|
| `mapping.h` (`Config`, `InputBinding`, `ChannelBindings`) | `webconfig/src/lib/types.ts` | Field names, types, channel keys. JSON wire shape must round-trip. |
| `mapping.cpp` (`scaleAxis`) | `webconfig/src/lib/scaleAxis.ts` | Identical math. UI's "processed" bar must mirror what the firmware actually writes. |
| `mapping.cpp` (CRC-32/ISO-HDLC) | `webconfig/src/lib/crc32.ts` | Same polynomial / init / final-XOR. Currently unused on the JS side but reserved for preset validation. |
| `protocol.cpp` (`CHANNEL_NAMES`, JSON command shapes) | `webconfig/src/lib/serial.ts`, `types.ts` (`CHANNELS`) | Channel name strings, command names, request/response shapes. |

`webconfig/src/lib/types.ts` defensively accepts both the v2 (array) and legacy v1 (single object) channel shapes so the UI keeps rendering against older firmware. Keep that fallback when changing the schema.

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
- **No agent attribution on commits.** Do not add Co-Authored-By or similar attribution lines for AI assistants on commits in this repo.
- **Repo root is the directory containing this file.** `firmware/` and `webconfig/` are siblings. The Arduino sketch convention requires `firmware/firmware.ino` (folder name = .ino name) — don't rename one without the other.
- **Keep docs in sync with the code.** When a change affects something documented here or in a README, update both in the same commit. Things that warrant a doc edit:
  - Schema bumps (`CONFIG_VERSION`, new fields, layout changes) → `AGENTS.md` cross-file invariants table, `firmware/README.md` JSON command reference, preset shape in `webconfig/README.md`.
  - New / renamed / removed JSON commands or events → JSON command reference in `firmware/README.md`, the serial client in `webconfig/src/lib/serial.ts`, and the cross-file table here.
  - New channel keys or output behavior → channel model section here, the channel list in `firmware/README.md`, the `CHANNELS` table in `webconfig/src/lib/types.ts`.
  - Module additions / renames / loop-order changes → firmware module layout section here and the README's firmware section.
  - Hardware wiring or pin changes → `firmware/README.md` wiring reference; mention the pin in the firmware module layout here if it's surfaced as a constant.
  - Behavior gotchas that you debugged through (handshake quirks, edge cases) → add a bullet to "Things to know before editing" so the next agent doesn't re-derive it.

  If unsure whether a change is doc-worthy, ask: "would the next agent waste time re-discovering this?" If yes, document it.
