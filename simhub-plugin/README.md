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
that tab. These standard SimHub controls already accept **Control Mapper
roles**, so mapped buttons can drive the adapter without binding each physical
controller again. Gears appear twice:

- **Hold** bindings engage a gear while the control is held and return to
  neutral on release. This is real H-pattern behaviour and what you want for an
  actual H-shifter.
- **Select** bindings latch — the gear stays until another one is chosen. Use
  these for a button that should select a gear and stay there.

Above the editors is a strip answering "did that actually happen": two lamps
that flash on each shift, the gear the adapter reports holding, and the last
command with its timestamp — in red, with the error, when one fails. The lamps
light on either of the two confirmations available: the adapter acknowledging
the command, and the outputs stream showing the pin driven. Neither is
sufficient alone (a 50 ms pulse can fall between two ~30 Hz telemetry frames),
and neither can prove the *wheelbase* registered the shift — nothing on that
link reports back.

Each pedal uses SimHub's standard axis picker. Move the pedal and confirm the
assignment. When Control Mapper has a role assigned to that axis, the native
picker prefers the role (for example `ControlMapperPlugin.Brake`) over the
underlying controller. Configure roles in SimHub's [Control Mapper](https://github.com/SHWotever/SimHub/wiki/Control-Mapper-plugin)
first. Throttle, brake, clutch and handbrake each have their own assignment.

The picker supplies a processed 0–1 value using its selected direction;
Control Mapper's calibration and filters are already applied to mapped roles.
The plugin converts that value to the adapter's 0–65535 range. Assignments
persist and work during automatic resume even if the settings pane has not
been opened. An unavailable or uninitialized axis shows in the live readout
and sends no new value; it never falls back to a physical controller.

The old property picker, Detect flow and plugin-specific range/inversion
controls have been removed. Previously saved property bindings need to be
assigned once through the native picker; existing native assignments remain.
Watch the live readout to check the direction before pressing Start.

On current firmware (protocol 6+) the pedal stream is **fire-and-forget at a
fixed 100 Hz**: each update is one-way with no acknowledgement round-trip in
the hot path and no change-suppression dead-band, so PC-attached pedals get
the same cadence the wheelbase itself is fed at. On older firmware the plugin
falls back to acknowledged commands (~8 ms per update, still ~100 Hz-class).

**Start driving pedals sticks.** The choice is saved and re-applied on every
connect, so it survives a SimHub restart — and, just as importantly, a game
change, because SimHub tears its plugins down and rebuilds them each time you
switch games. Two things end it, both deliberate: **Stop driving pedals** and
**Release outputs**. A release that the plugin does on its own (shutdown, game
change, disconnect) leaves the choice intact so the stream comes back. If the
choice can't be honoured on a connect — a Teensy answered, or no pedal has a
source — the plugin logs why and keeps it for next time rather than
quietly forgetting it. Stop and Release take effect even if saving settings
fails; that failure is logged, and the choice may need to be saved again before
restarting SimHub.

Live pedal readouts use the pane's Normal-priority timer, with unchanged values
suppressed. Closing and reopening the pane reattaches its session listeners and
re-enables input telemetry; output telemetry stays on for dashboard properties.
Reopening preserves mapping and pulse-width edits on the current connection.
Reconnects and explicit config reloads load the adapter's fresh settings instead.
The Start/Stop button also follows automatic pedal-stream resume.

> **Overrides are sticky.** The firmware has no timeout, so whatever was last
> sent stays applied until it is released. The plugin releases automatically
> when you stop streaming, when it disconnects, and when SimHub shuts it down —
> but if SimHub is killed outright, nothing runs to release, and the wheelbase
> keeps the last pedal values until something does. **Release outputs** (a
> button, and also a bindable action) hands every channel back to the adapter's
> own mapping; reconnecting with streaming enabled also takes the pedals back
> over, which clears a stuck state by overwriting it.

### Devices, Mappings, Outputs — for the adapter's own hardware

**Devices** shows every slot of the adapter's device pool live. Values stay at
zero until something moves: the firmware only reports inputs when they change,
so an idle rig reading all zeros is correct, not broken.

Next to the slot count are two diagnostics for a known, rare firmware bug where
one device's inputs freeze at their last values while it stays listed
(STM32 adapters only). Run them **while it is frozen** — results go to the Logs
tab, every line of which is timestamped: **USB status** logs each slot's report
pipe state (a growing `age_ms` on a device you're actively moving is the
signature), and **Kick USB pipes** aborts and re-arms every pipe. Whether a kick
revives the frozen device is the evidence needed to fix the bug for real, so
note what you saw.

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

### Firmware updates (STM32 adapters)

**Update firmware** in the ADAPTER section runs the whole cycle on its own:
downloads the latest published build (the same one webconfig serves, from the
project's GitHub Pages deploy), reboots the adapter into its ROM bootloader,
flashes it over WinUSB, waits for the serial port to come back and reconnects.
No buttons on the board, no device picker, no toolchain — saved mappings live
in a separate flash bank and survive. Also works with no connection when the
board is stuck in bootloader mode from an interrupted update — running the
update again is the recovery.

Windows needs the WinUSB driver bound to `STM32 BOOTLOADER` (installing
STM32CubeProgrammer or one Zadig run does it; any machine where `dfu-util`
works already has it). Forks hosting their own Pages deploy can point the
updater elsewhere via the `FirmwareUrl` plugin setting. Teensy adapters flash
with the PJRC loader instead — the button stays disabled there.

## Build

```sh
dotnet build simhub-plugin/FanadapterSimHub.sln -c Release
dotnet test  simhub-plugin/FanadapterSimHub.sln -c Release
```

The solution includes Core tests and SimHub/WPF lifecycle tests. The latter
require the configured SimHub installation and use an in-memory adapter without
opening a COM port. They cover stopping when settings persistence fails,
preserving edits across pane navigation, loading new config snapshots, and
refreshing WPF bindings after automatic stream resume.
Native axis tests also cover picker replacement/clear, saved and late-registered
Control Mapper assignments, current samples versus UI copies, and pedal command
payloads for both protocol paths.

Core has no SimHub references. To run its tests without SimHub installed:

```sh
dotnet test simhub-plugin/tests/Fanadapter.Core.Tests/Fanadapter.Core.Tests.csproj -c Release
```

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
| `tests/Fanadapter.SimHub.Tests/` | WPF and drive lifecycle regression tests against SimHub's assemblies, with an in-memory adapter. |
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
