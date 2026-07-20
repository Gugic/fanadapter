# Fanadapter plugin for SimHub

A [SimHub](https://www.simhubdash.com/) plugin that talks to the fanadapter over
its serial JSON protocol — the same protocol [`webconfig/`](../webconfig) speaks
over WebSerial.

The plugin exists because the adapter can be driven two different ways, and the
split follows where a device is physically plugged in:

- **Devices plugged into the PC** — SimHub already enumerates, captures and maps
  every controller attached to the PC, so the plugin reuses all of it. You bind
  a shifter or pedal set with SimHub's own "click to configure" flow, and the
  plugin translates the result into the firmware's direct-output commands. The
  adapter's own mapping is bypassed for those channels.
- **Devices plugged into the adapter's USB hub** — Windows cannot see these at
  all, so configuring them means editing the mapping stored on the adapter. The
  plugin mirrors the webconfig UI for that: live device view, input capture,
  axis calibration, gear voltages, bench tests.

Those two halves are independent. Adapter-side mappings keep working with the PC
switched off; SimHub-side mappings only apply while SimHub is running and
driving.

## Requirements

- **SimHub 9.11.21** (the version pinned in [`.simhub-version`](.simhub-version)
  and used by CI). Other 9.x releases will probably work; that one is tested.
- **.NET SDK 8.0+** to build. The plugin targets .NET Framework 4.8 — SimHub's
  requirement — but the reference assemblies come from a NuGet package, so no
  Developer Pack install is needed.
- **STM32 firmware** for anything that drives outputs from the PC. The Teensy
  firmware answers `unknown_cmd` to the four direct-output commands, so the
  SimHub drive tab needs [`firmware-stm32/`](../firmware-stm32). Adapter-side
  configuration works against either firmware, and the plugin detects which one
  it is talking to and says so.

## Install

Drop `Fanadapter.SimHub.dll` and `Fanadapter.Core.dll` into the SimHub folder
and restart it. SimHub asks whether to enable the new plugin on first start; say
yes, and **Fanadapter** appears in the left menu.

From a build tree:

```sh
dotnet build simhub-plugin/src/Fanadapter.SimHub/Fanadapter.SimHub.csproj -c Release -p:InstallToSimHub=true
```

**Close SimHub first** — it loads plugin assemblies at startup and holds them
open, so installing over a running SimHub fails with a file lock.

## Using it

### Connecting

The port list ranks likely adapters first (the STM32's native CDC at
`1209:FA00`, a Teensy at `16C0`, a CH340 bridge at `1A86`) but never hides
anything, so an unrecognised USB-UART bridge is still selectable. Connect, and
the header shows which firmware answered. The chosen port is remembered and
reconnected on the next start.

If the port opens but nothing answers, something else is holding it — webconfig
in a browser tab is the usual culprit, and it holds the port exclusively.

### SimHub drive — for controllers on the PC

Bind gears, shifts and the release/re-arm actions with the Controls editors on
that tab. Gears appear twice:

- **Hold** bindings engage a gear while the control is held and return to
  neutral on release. This is real H-pattern behaviour and what you want for an
  actual H-shifter.
- **Select** bindings latch — the gear stays until another one is chosen. Use
  these for a button that should select a gear and stay there.

Pedals work differently: pick the **SimHub property** carrying each one. Any
property works — a controller axis published by SimHub's input plugins, or your
own NCalc expression. Set the range to match the property (SimHub's own axis
properties are 0–100) and watch the live readout to check the direction before
pressing Start.

> **Overrides are sticky.** The firmware has no timeout, so whatever was last
> sent stays applied until it is released. The plugin releases automatically
> when you stop streaming, when it disconnects, and when SimHub shuts it down —
> but if something ever ends up stuck, **Release outputs** (a button, and also a
> bindable action) hands every channel back to the adapter's own mapping.

### Devices, Mappings, Outputs — for the adapter's own hardware

**Devices** shows every slot of the adapter's device pool live. Values stay at
zero until something moves: the firmware only reports inputs when they change,
so an idle rig reading all zeros is correct, not broken.

**Mappings** binds those devices to wheelbase channels. Press **Listen**, hold
still while it measures the noise floor, then move the control you want. Buttons,
hat directions and keys commit immediately; an axis is measured until you
release it, so its full travel can be calibrated — including a pedal that rests
high and falls when pressed, which is captured inverted automatically. Each
channel takes up to four inputs: buttons across slots are OR'd and axes are
MAX'd by the firmware, so one gear can be reachable from two shifters.

*Shifter mode* switches between hold (a gear is engaged only while its binding
is held, like a real H-shifter) and latch (a press switches gear and stays).
Neutral is only bindable in latch mode — in hold mode it is simply the absence
of an engaged gear.

**Outputs** shows what the adapter is currently sending, exposes the H-pattern
gear voltages, and provides bench tests. The gear voltages are the thing to
reach for when a gear won't register: the port reads a pair of voltages rather
than a gear number, so each position is an (X, Y) DAC code, and **Test** holds
one for half a second so you can find the right pair alone at the rig.

Changes apply to the adapter immediately but live in its RAM. **Save to adapter**
is what makes them survive a power cycle.

## Build

```sh
dotnet build simhub-plugin/FanadapterSimHub.sln -c Release
dotnet test  simhub-plugin/FanadapterSimHub.sln -c Release
```

Tests cover `Fanadapter.Core`, which has no SimHub references — they run on a
machine with no SimHub installed.

If SimHub is not at `C:\Program Files (x86)\SimHub\`, create an untracked
`simhub-plugin/Directory.Build.props.user`:

```xml
<Project>
  <PropertyGroup>
    <SimHubDir>D:\Games\SimHub\</SimHubDir>
  </PropertyGroup>
</Project>
```

## Layout

| Path | What it is |
|---|---|
| `src/Fanadapter.Core/` | Transport, protocol, schema, `scaleAxis`, the capture engine. **No SimHub references** — the unit-testable half, and where the C# mirrors of the firmware contracts live. |
| `src/Fanadapter.SimHub/` | The plugin: SimHub interfaces, actions, properties, the drive controller and the WPF settings UI. |
| `tests/Fanadapter.Core.Tests/` | xunit tests for Core. |
| `.simhub-version` | The SimHub release CI builds against. |

Changing anything that mirrors a firmware contract — the config schema, the
`scaleAxis` math, the command shapes — means changing the firmware, webconfig
and this plugin together. See the cross-file invariants table in
[`AGENTS.md`](../AGENTS.md).

## Not done yet

- The guided setup wizard from webconfig. Per-slot capture works; the wizard is
  a convenience that walks the same flow across a whole shifter.
- Preset import/export. Neither client has it — see the CRC-32 note in
  `AGENTS.md` if you add one.

## License

MIT, same as the rest of the repo.
