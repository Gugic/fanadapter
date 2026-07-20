# Fanadapter plugin for SimHub

A [SimHub](https://www.simhubdash.com/) plugin that talks to the fanadapter over
its serial JSON protocol — the same protocol [`webconfig/`](../webconfig) speaks
over WebSerial.

The plugin exists because the adapter can be driven two different ways, and only
one of them is a browser's job:

- **Devices plugged into the PC** — SimHub already enumerates, captures and maps
  every controller attached to the PC. The plugin reuses that: you bind a
  shifter or pedal set with SimHub's own "click to configure" flow, and the
  plugin translates the result into the firmware's direct-output commands
  (`set_gear`, `set_outputs`, `pulse_shift`, `release_outputs`). No adapter-side
  mapping is involved.
- **Devices plugged into the adapter's USB host hub** — only the firmware can
  see these, so configuring them means editing the firmware's own mapping. The
  plugin mirrors the webconfig UI for that: live device view, input capture,
  axis calibration, gear DAC voltages.

> **Status: early.** The project skeleton builds and loads in SimHub; the
> feature work is landing milestone by milestone. See the top-level
> [`AGENTS.md`](../AGENTS.md) for where this sits relative to the rest of the repo.

## Requirements

- **SimHub 9.11.21** (the version pinned in [`.simhub-version`](.simhub-version)
  and used by CI). Other 9.x releases will probably work; that one is tested.
- **.NET SDK 8.0+** to build. The plugin itself targets .NET Framework 4.8 —
  SimHub's requirement — but the reference assemblies come from a NuGet package,
  so no Developer Pack install is needed.
- **STM32 firmware** for the direct-output features. The Teensy firmware answers
  `unknown_cmd` to the four direct-output commands, so PC-driven output needs
  [`firmware-stm32/`](../firmware-stm32). Adapter-side configuration works
  against either firmware.

## Build

```sh
dotnet build simhub-plugin/FanadapterSimHub.sln -c Release
dotnet test  simhub-plugin/FanadapterSimHub.sln -c Release
```

Tests cover `Fanadapter.Core` only, which has no SimHub references — they run on
a machine with no SimHub installed.

If SimHub is not at `C:\Program Files (x86)\SimHub\`, create an untracked
`simhub-plugin/Directory.Build.props.user`:

```xml
<Project>
  <PropertyGroup>
    <SimHubDir>D:\Games\SimHub\</SimHubDir>
  </PropertyGroup>
</Project>
```

## Install

```sh
dotnet build simhub-plugin/src/Fanadapter.SimHub/Fanadapter.SimHub.csproj -c Release -p:InstallToSimHub=true
```

This copies `Fanadapter.SimHub.dll` and `Fanadapter.Core.dll` into the SimHub
directory. **Close SimHub first** — it loads plugin assemblies at startup and
holds them open, so a rebuild-over-a-running-SimHub fails with a file lock.

To install a release build by hand, drop those two DLLs into the SimHub folder.
On first start SimHub asks whether to enable the new plugin; say yes, and
"Fanadapter" appears in the left menu.

## Layout

| Path | What it is |
|---|---|
| `src/Fanadapter.Core/` | Transport, protocol and schema. **No SimHub references** — this is the unit-testable half, and it holds the C# mirrors of the firmware contracts. |
| `src/Fanadapter.SimHub/` | The plugin: SimHub interfaces, actions, properties, and the WPF settings UI. |
| `tests/Fanadapter.Core.Tests/` | xunit tests for Core. |
| `.simhub-version` | The SimHub release CI builds against. |

## License

MIT, same as the rest of the repo.
