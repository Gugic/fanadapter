# fanadapter webconfig

The browser-based configuration interface for the [fanadapter firmware](../firmware-stm32/README.md). Built with Vite, React 19, TypeScript, Tailwind, and shadcn/ui.

The webapp communicates with the STM32 adapter over WebSerial using a line-based JSON protocol. No native helper application or local daemon is required.

---

## Live Deployment

When this repository is pushed to `main`, a GitHub Actions workflow automatically builds and publishes the production build to GitHub Pages:

- **Canonical URL:** [gugic.github.io/fanadapter](https://gugic.github.io/fanadapter/)

For a fork, enable Pages in **Settings → Pages → Build and deployment → Source: GitHub Actions**.
Publish the firmware release first with `.github/workflows/firmware-stm32.yml`, then
run `.github/workflows/pages.yml`. App changes deploy Pages; a successful firmware
release also redeploys it. Pages copies the immutable release manifest and binary
without compiling firmware. Plugin changes trigger neither Pages nor firmware builds.

> [!NOTE]
> **Browser Compatibility:** WebSerial is currently only supported in desktop Chromium-based browsers (Google Chrome, Microsoft Edge, Brave, Opera). It will not work on mobile browsers or on desktop Firefox/Safari.

---

## Local Development & Commands

### Prerequisites

- **Node.js:** version 20 or higher (LTS recommended)
- **Package Manager:** `npm` (comes bundled with Node)

### Workflow Commands

Execute all commands from the `webconfig/` subdirectory:

```sh
# 1. Install dependencies
npm install

# 2. Start the local Vite development server
npm run dev

# 3. Create a optimized static production build
npm run build

# 4. Run ESLint rules check
npm run lint

# 5. Run the CI dependency audit
npm run audit
```

The audit fails on high and critical advisories. `audit-ci.jsonc` scopes one exception to
[GHSA-vfj7-8cjw-p6xm](https://github.com/advisories/GHSA-vfj7-8cjw-p6xm) through Tailwind 3:
its `braces` glob parser has no patched release and only processes this repo's fixed content
globs in `tailwind.config.js`. It is absent from the static browser bundle. Remove the exception
when an upstream patch or a Tailwind migration removes that dependency; keep content patterns
under repository control. `npm audit` shows the full unfiltered report; other high/critical
advisories still fail CI.

When running `npm run dev`, Vite will serve the application at `http://localhost:5173/fanadapter/`. Open this URL in Chrome, Edge, or Brave, click **Connect**, and choose the adapter's USB serial (CDC) device from the browser permission pop-up.

Connect and saved-grant reconnection accept only USB identity `1209:FA00`.
Firmware 0.6.1+ advertises **Fanadapter v0.6.1** (using its actual version).
Older native firmware works too; unrelated COM devices and USB-UART bridges are hidden.

### Custom Subfolder Deployment

By default, the Vite config maps assets to the base path `/fanadapter/`. If you are hosting the configurator elsewhere, override this base path during building:

```sh
VITE_BASE=/custom-path/ npm run build
```

---

## Project Structure

```
webconfig/
  src/
    App.tsx               # Entry point, top-level state, connection lifecycle, and tab routing
    components/ui/        # UI primitives (buttons, cards, sliders, inputs) via shadcn/ui
    lib/
      serial.ts           # WebSerial client containing the FIFO request/response queue
      types.ts            # TypeScript mirror of the firmware's Config schema
      scaleAxis.ts        # Client-side replica of the firmware's scaleAxis() math
      crc32.ts            # Client-side CRC-32/ISO-HDLC encoder (matches firmware)
      dfu.ts              # WebUSB DfuSe flasher for the STM32 ROM bootloader (the Flash dialog)
      utils.ts            # Tailwind CSS class merging helper
  tailwind.config.js      # Styling design tokens and theme settings
  vite.config.ts          # Vite build config
```

---

## Technical Design & Internal Invariants

To keep the web configurator aligned with the microcontroller's operation, three critical design architectures are enforced:

### 1. In-App Capture Flow State Machine

`webconfig/src/App.tsx` coordinates a three-phase "Listen" process for capture-mapping button, axis, hat (D-pad direction), or key (keyboard) inputs:

1. **`baseline` (Duration: ~400 ms):**  
   Samples active button bits and axis ranges to establish the sensor noise floor.  
   _Implementation detail:_ To avoid trigger-heavy React component re-renders on 50 Hz serial updates, this baseline data accumulates in a React `useRef` rather than state. A device that stays completely silent through this window (a keyboard, or a jitter-free gamepad) is lazily seeded with an at-rest baseline, so the very first Listen recognises it instead of ignoring it until timeout.
2. **`active`:**  
   Waits for input activity that crosses the noise floor.
   - Buttons, hats, and keys: Commit immediately when a new button bit, hat direction, or pressed key appears.
   - Axes: Latch and advance to Phase 3 when an axis value deviates from its baseline midpoint by more than `max(noise × 5, 500)`.
3. **`tracking`:**  
   Tracks high/low peaks on the latched axis. The configuration commits when the user releases the axis and it returns to within `max(noise × 2, 200)` of the baseline. The axis direction (rising vs falling) is determined by which peak traveled further; descending axes automatically check the `invert: true` configuration flag.

---

### 2. FIFO Serial Request/Response Queue

Because multiple UI modules can request details from the adapter simultaneously, `SerialClient` (`webconfig/src/lib/serial.ts`) implements a FIFO queue over WebSerial:

- Commands (non-event JSON strings) are pushed to the queue and executed sequentially. When a JSON reply is returned, it is matched with the oldest pending promise.
- Event packages (`{"event":"..."}`) are intercepted, bypassed, and fanned out to active UI subscribers.
- Non-JSON text lines are transformed into virtual log events (`{type: "log"}`) and rendered inside the configurator's **Logs** console.

---

### Firmware updates (STM32 adapters)

The header's **Flash** button opens a one-click updater: firmware is published separately in
**firmware-v** [GitHub Releases](https://github.com/Gugic/fanadapter/releases).
Pages mirrors that release as `firmware/fanadapter-stm32.{json,bin}` beside the app
(release download URLs don't support browser CORS). Size and SHA-256 are verified before flashing, so the dialog shows
"Installed vs Latest" from a same-origin fetch and a single **Update** press then runs the whole
cycle — `{"cmd":"dfu"}` reboots the adapter into its ROM bootloader, the DFU device (0483:df11) is
found through the **persisted WebUSB grant**, `src/lib/dfu.ts` erases/writes/boots it over the
DfuSe protocol, and the app reconnects through the **persisted WebSerial grant** and confirms the
new version. The only manual step that can exist is Chromium's device picker on the very first
update per machine (`requestDevice()` needs a fresh user gesture, so it lives on its own button);
every later update is hands-free. Saved mappings live in a different flash bank than the code and
survive. An expandable
"local .bin" path covers dev builds (drop one in `webconfig/public/firmware/` to test the deployed
flow locally — that dir is gitignored). On Windows the DFU device must have the WinUSB driver
bound (STM32CubeProgrammer's installer or one Zadig run does it).

---

### 3. Cross-File Code Invariants

The web app shares four strict boundaries with the STM32 C code in `firmware-stm32/src/`. If a change is made to one, its firmware and SimHub counterparts must be updated in the same commit:

| Webconfig Component    | Firmware Code                  | Matching Requirement                                               |
| ---------------------- | ------------------------------ | ------------------------------------------------------------------ |
| `src/lib/types.ts`     | `src/mapping.h`                | Must mirror exact property naming, field types, and channel lists. |
| `src/lib/scaleAxis.ts` | `src/mapping.c` (`scale_axis`) | Must replicate identical threshold, deadzone, and math operations. |
| `src/lib/crc32.ts`     | `src/mapping.c` (CRC-32)       | Same ISO-HDLC CRC polynomial.                                      |
| `src/lib/serial.ts`    | `src/protocol.c`               | JSON commands, channel keys, and async events must match.          |

> [!IMPORTANT]
> **Backward Compatibility:** `types.ts` is designed defensively to handle both the v2 schema (channels mapped as array slots) and the legacy v1 schema (single bindings) so the interface continues rendering when connected to older firmware versions. Keep this fallback structure intact.

---

## Front-End Troubleshooting

### Configurator refuses to connect

- Ensure you are running Chrome, Edge, or Brave. Safari and Firefox are incompatible.
- Ensure the adapter's serial port is not occupied by another utility, such as a serial monitor, SimHub, or another open WebSerial browser tab.

### The preview bars move in the UI, but the wheelbase does not react

- Check the live output values and the pedal handshake state. Mapping edits apply immediately in adapter RAM; **Save to adapter** stores them in flash so they survive a restart. See the [calibration reference](../firmware-stm32/PROTOCOL.md#calibration).
- Verify that your hardware cables match the pinouts described in the [Hardware & Schematics Reference](../schematics/README.md).
