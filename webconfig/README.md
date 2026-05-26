# fanadapter webconfig

Browser-based WebSerial configuration UI for the [fanadapter firmware](../firmware/). Built with Vite + React + TypeScript + Tailwind + shadcn/ui. Talks to the Teensy over WebSerial; no native helper required.

## Live

When this repo is published to GitHub Pages, the app is served at the URL configured in your Pages settings (typically `https://<owner>.github.io/fanadapter/`).

WebSerial only works in Chromium-based browsers (Chrome, Edge, Brave, Opera) on desktop.

## Local dev

Requires Node 20+ (any LTS works).

```sh
cd webconfig
npm install
npm run dev
```

Vite will print a URL like `http://localhost:5173/fanadapter/`. Open it in Chrome/Edge/Brave, click **Connect**, pick the Teensy CDC port.

## Build

```sh
npm run build
```

Produces a static bundle in `dist/`. Default base path is `/fanadapter/` (matches the repo name); override with `VITE_BASE=/whatever/ npm run build` for custom hosting.

## Deploy to GitHub Pages

The repo includes [.github/workflows/pages.yml](../.github/workflows/pages.yml) which builds `webconfig/` and publishes `dist/` to Pages on every push to `main`. To turn it on:

1. Push the workflow file to `main` (it lives at the repo root).
2. In repository settings → **Pages**, set **Source** to **GitHub Actions**.
3. The first push to `main` after that triggers a deploy; the URL appears under Settings → Pages.

If you fork or rename the repo, update the `base` default in [vite.config.ts](vite.config.ts) to match the new path.

## Project layout

```
webconfig/
  src/
    App.tsx               Top-level state, connection lifecycle, tab routing
    components/ui/        shadcn-style primitives (button, card, slider, …)
    lib/
      serial.ts           WebSerial client + JSON request/response queue
      types.ts            TypeScript mirror of the firmware Config schema
      scaleAxis.ts        Client-side mirror of firmware scaleAxis() for live preview
      crc32.ts            CRC-32/ISO-HDLC (matches firmware) for future preset validation
      utils.ts            cn() helper for class merging
  public/
    presets/              Bundled JSON presets (loaded via fetch)
  tailwind.config.js
  vite.config.ts
  tsconfig*.json
```

## Presets

`public/presets/` holds JSON files matching the firmware's `get_config` response shape, so the same payload can be replayed against any fanadapter to recreate a known-good setup.

Each channel is an array of up to `MAX_BINDINGS_PER_CHANNEL` bindings (currently 4). Empty slots have `type: "none"`. Multiple bindings on the same channel are aggregated — buttons OR together, axes MAX together — so e.g. you can set Reverse to *either* a paddle button *or* a stick position.

Bundled today:

- `rs-shifter-rs-combo-spu-spro.json` — the original hardcoded firmware behavior (Logitech RS H-Shifter `046D:C26B` + 1–2× RS Shifter+Handbrake `046D:C278` + Simnet SP Pro `CAFE:A301`). Same gear DAC voltages as the pre-refactor `GEARS[]` table; pedal `rawMax` set to `4095` (matching the SP Pro's 12-bit reports) and a 2 % low deadzone on brake to absorb the noise floor.
