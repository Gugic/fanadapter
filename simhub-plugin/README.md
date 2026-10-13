# Fanadapter plugin for SimHub

A [SimHub](https://www.simhubdash.com/) plugin that talks to the fanadapter over
its serial JSON protocol — the same protocol [`webconfig/`](../webconfig) speaks
over WebSerial.

The plugin exists because the adapter can be driven two different ways, and the
split follows where a device is physically plugged in:

- **Devices plugged into the PC** — The plugin reuses SimHub's input and Control Mapper support for controllers that SimHub recognizes. You bind
  a shifter or pedal set with SimHub's own "click to configure" flow, and the
  plugin translates the result into the firmware's direct-output commands. The
  adapter's own mapping is bypassed for those channels.
- **Devices plugged into the adapter's USB hub** — Windows cannot see these at
  all, so configuring them means editing the mapping stored on the adapter. The
  plugin mirrors the webconfig UI for that: live device view, input capture,
  axis calibration, gear voltages, bench tests.

Adapter-side mappings run without SimHub, provided the adapter and hub remain powered and no sticky PC override is left active. SimHub-side mappings apply while SimHub is connected and driving; see the override behavior below.

## Requirements

- **SimHub 9.11.21** (the version pinned in [`.simhub-version`](.simhub-version)
  and used by CI). Other 9.x releases will probably work; that one is tested.
- **.NET SDK 8.0+** to build. Build and run the full solution on Windows. The plugin targets .NET Framework 4.8 — SimHub's
  requirement — but the reference assemblies come from a NuGet package, so no
  Developer Pack install is needed.
- **[STM32 firmware](../firmware-stm32/README.md)** on the adapter. The plugin detects firmware capabilities before offering direct output or updates.

## Install

Download the ZIP from the latest **simhub-v** [GitHub release](https://github.com/Gugic/fanadapter/releases).
Drop `Fanadapter.SimHub.dll` and `Fanadapter.Core.dll` into the SimHub folder
and restart it. SimHub asks whether to enable the new plugin on first start; say
yes, and **Fanadapter** appears in the left menu.

From a build tree:

```sh
dotnet build simhub-plugin/src/Fanadapter.SimHub/Fanadapter.SimHub.csproj -c Release -p:InstallToSimHub=true
```

**Close SimHub before a manual copy/build install.** The in-app updater has a separate staged replacement/rollback path and asks for a restart after installation.

## Using it

### Connecting

The port list shows only the adapter's native USB-C CDC device (`1209:FA00`).
Firmware 0.6.1+ supplies the label **Fanadapter v0.6.1 (COMx)** (with its actual
firmware version); older native firmware remains selectable by USB identity.
Unrelated serial devices and USB-UART bridges are excluded. Connect, and
the header shows which firmware answered. The chosen port is remembered and
reconnected on the next start.

If the port cannot open, close webconfig, serial terminals or another SimHub instance holding it exclusively. If it opens but requests time out, inspect the logs and firmware version and reconnect; an already-open port is not evidence that another client owns it.

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
Each pedal also has SimHub's two-handle percentage range slider: the left
handle sets **Released at**, mapping to 0% output, and the right handle sets
**Full travel at**, mapping to 100%, with values outside that range
clamped. Defaults are 0–100%. To get full braking at roughly the old 45,000
out of 65,535 input, drag the brake's right handle to **68.7%**. This
adjustment applies to the processed SimHub input, so leave it at 0–100% if
Control Mapper already supplies the range you want. Direction stays in the
native picker. The live input and output readouts both show percentages.

The plugin converts the adjusted value to the adapter's 0–65535 range.
Assignments and ranges persist and work during automatic resume even if the settings pane has not
been opened. An unavailable or uninitialized axis shows in the live readout
and sends no new value; it never falls back to a physical controller.

The old property picker and Detect flow have been removed. Previously saved
property bindings need to be assigned once through the native picker; existing
native assignments remain.
Watch the live readout to check the direction before pressing Start.

On current firmware (protocol 6+) the pedal stream is **fire-and-forget at a
fixed 100 Hz**: each update is one-way with no acknowledgement round-trip in
the hot path and no change-suppression dead-band, so PC-attached pedals get
the same cadence the wheelbase itself is fed at. On older firmware the plugin
falls back to acknowledged commands with change suppression and a 1 s keep-alive. The recorded round-trip was 7.9 ms after the console fixes (13 ms before); actual cadence also depends on scheduling and link load.

**Start driving pedals sticks.** The choice is saved and re-applied on every
connect, so it survives a SimHub restart — and, just as importantly, a game
change, because SimHub tears its plugins down and rebuilds them each time you
switch games. Two things end it, both deliberate: **Stop driving pedals** and
**Release outputs**. A release that the plugin does on its own (shutdown, game
change, disconnect) leaves the choice intact so the stream comes back. If the
choice can't be honoured on a connect — unsupported firmware answered, or no pedal has a
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

**Devices** shows the connected interfaces from the adapter's eight-slot pool. Live values update on change, up to ~30 Hz, and may remain at their initial display until a new input event arrives. An idle device need not emit a fresh frame; move it when checking reception.

Next to the slot count are **USB status** and **Kick USB pipes** diagnostics.
If a device freezes while still listed, run USB status while moving it; results
go to the timestamped Logs tab. A growing `age_ms` identifies a silent report
pipe. Kick aborts and re-arms the pipes, but did not revive the frozen pedal
in bench tests; replug the affected device at the hub to recover. Current
firmware avoids the long polling gaps that caused the freeze. See the
[firmware troubleshooting guide](../firmware-stm32/README.md#7-troubleshooting).

**Mappings** binds those devices to wheelbase channels. Press **Listen**, hold
still while it measures the noise floor, then move the control you want. Buttons,
hat directions and keys commit immediately; an axis is measured until you
release it, so its full travel can be calibrated — including a pedal that rests
high and falls when pressed, which is captured inverted automatically. Each
channel takes up to four inputs: buttons across slots are OR'd and axes are
MAX'd by the firmware, so one gear can be reachable from two shifters.

*Shifter mode* switches between hold (a gear is engaged only while its binding
is held, like a real H-shifter) and latch (a press switches gear and stays).
The UI shows Neutral bindings only in latch mode. The firmware also honors a saved `gear_N` binding as a panic-neutral override in hold mode; hiding that editor does not clear an existing binding.

**Outputs** shows what the adapter is currently sending, exposes the H-pattern
gear voltages, and provides bench tests. The gear voltages are the thing to
reach for when a gear won't register: the port reads a pair of voltages rather
than a gear number, so each position is an (X, Y) DAC code, and **Test** holds
one for half a second so you can find the right pair alone at the rig.

Changes apply to the adapter immediately but live in its RAM. **Save to adapter**
is what makes them survive a power cycle.

### Firmware updates

**Update firmware** in the ADAPTER section runs the whole cycle on its own:
downloads the latest **firmware-v** GitHub release directly, verifies its size
and SHA-256 (webconfig mirrors the identical release on Pages), reboots the adapter into its ROM bootloader,
flashes it over WinUSB, waits for the serial port to come back and reconnects.
With working firmware already connected, no board buttons, browser picker or toolchain are needed. Saved mappings live in bank 2 and survive flashing unless the new firmware changes the configuration schema. The updater also works while disconnected if the board is already in ROM DFU: for a fresh board or recovery, connect USB-C, hold BOOT0, tap RST, then release BOOT0 before starting the update. Automatic recovery reconnect selects the sole detected Fanadapter; if multiple adapters are attached, reconnect the intended one manually.

Windows needs **WinUSB** bound to `STM32 BOOTLOADER`; use STM32CubeProgrammer's driver installer or Zadig and verify the binding. dfu-util can use other backends, so its success alone does not prove compatibility. `FirmwareUrl` is an optional mirror **base URL** containing `firmware/fanadapter-stm32.{json,bin}`; the default reads GitHub Releases directly. While connected, Update requires firmware supporting ROM DFU entry. While disconnected, a valid image source and a board already in DFU suffice. A picked local `.bin` is checked for plausible vectors and size but has no release checksum to compare against.

### Plugin updates

The **PLUGIN UPDATES** section checks stable **simhub-v** releases on startup
and every six hours. Automatic checks can be disabled. **Install update** downloads
and validates both DLLs (GitHub asset SHA-256, assembly name and version) before
replacing them, then asks you to restart SimHub. Checks and downloads run outside
the pedal loop; game changes and pane navigation don't discard a pending update.
Backups allow a failed second replacement to restore the first DLL. If the process
stops midway, the next startup restores the previous pair and asks for another restart.
An unwritable plugin folder requires a manual install or an elevated SimHub process.
The first installation of this updater is manual, using the release ZIP above.

Firmware and plugin versions are independent. Their workflows publish on `main`
only after the firmware build or plugin build/tests pass, using version tags and assets that the workflows never overwrite:

- Firmware: bump `FW_VER` in `firmware-stm32/src/firmware_info.h` when changing firmware.
- Plugin: bump `VersionPrefix` in `simhub-plugin/Directory.Build.props` when changing the plugin.

Existing version tags are never overwritten; CI rejects changed production code
without its component version bump. Markdown documentation and test changes outside production source paths can keep the version; the guards compare file contents, so even comment-only edits inside guarded source paths currently require a bump.
A plugin-only change triggers no
firmware build; app changes only rebuild Pages and reuse the released binary.
Release packages contain the two DLLs plus README/license, with standalone DLLs
and `SHA256SUMS` also available. SimHub dependencies come from its installation.

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

## Feature limits

- The guided setup wizard from webconfig. Per-slot capture works; the wizard is
  a convenience that walks the same flow across a whole shifter.
- Webconfig's timed live-range resampling control is absent; use Listen capture or edit raw endpoints in the plugin.
- Preset import/export is not implemented in either client. The unused web CRC-32 helper does not constitute a preset feature.

These are current omissions, not scheduled work. The custom PC property detector from [#36](https://github.com/Gugic/fanadapter/pull/36) was removed by the native axis picker in [#40](https://github.com/Gugic/fanadapter/pull/40); its pending Detect validation no longer applies. Current native-picker tests cover simulated inputs and lifecycle behavior, not physical pedal travel or wheelbase output.

## License

Apache License 2.0 for the plugin, consistent with the repository's software license. See [LICENSE.md](../LICENSE.md).
