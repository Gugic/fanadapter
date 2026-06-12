# fanadapter webconfig

The browser-based configuration interface for the [fanadapter firmware](../firmware/). Built with Vite, React 19, TypeScript, Tailwind, and shadcn/ui.

The webapp communicates with the adapter (ESP32-S3 from v0.7.0; Teensy 4.1 through v0.6.0) over WebSerial using a line-based JSON protocol. No native helper application or local daemon is required.

---

## Live Deployment

When this repository is pushed to `main`, a GitHub Actions workflow automatically builds and publishes the production build to GitHub Pages:

- **Canonical URL:** `https://<username>.github.io/fanadapter/`

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
```

When running `npm run dev`, Vite will serve the application at `http://localhost:5173/fanadapter/`. Open this URL in Chrome, Edge, or Brave, click **Connect**, and choose the adapter's USB-Serial bridge device from the browser permission pop-up (CH340 or CP2102 on the ESP32-S3 build's UART USB-C port; native USB CDC on older Teensy builds).

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

### 3. Cross-File Code Invariants

The web app shares four strict boundaries with the firmware C++ code. If a change is made to one, its twin file must be updated in the same commit:

| Webconfig Component    | Firmware Code               | Matching Requirement                                               |
| ---------------------- | --------------------------- | ------------------------------------------------------------------ |
| `src/lib/types.ts`     | `mapping.h`                 | Must mirror exact property naming, field types, and channel lists. |
| `src/lib/scaleAxis.ts` | `mapping.cpp` (`scaleAxis`) | Must replicate identical threshold, deadzone, and math operations. |
| `src/lib/crc32.ts`     | `mapping.cpp` (CRC-32)      | Same ISO-HDLC CRC polynomial.                                      |
| `src/lib/serial.ts`    | `protocol.cpp`              | JSON commands, channel keys, and async events must match.          |

> [!IMPORTANT]
> **Backward Compatibility:** `types.ts` is designed defensively to handle both the v2 schema (channels mapped as array slots) and the legacy v1 schema (single bindings) so the interface continues rendering when connected to older firmware versions. Keep this fallback structure intact.

---

## Front-End Troubleshooting

### Configurator refuses to connect

- Ensure you are running Chrome, Edge, or Brave. Safari and Firefox are incompatible.
- Ensure the adapter's serial port is not occupied by another utility (serial monitor, `arduino-cli upload`, another open WebSerial browser tab).

### The preview bars move in the UI, but the wheelbase does not react

- Verify that you clicked **Save** after completing mappings. Unsaved mappings live only in adapter RAM and revert on reboot.
- Verify that your hardware cables match the pinouts described in the [Hardware & Schematics Reference](../schematics/README.md).
