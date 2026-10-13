# fanadapter — STM32H743 build & wiring guide

Firmware for the **STM32H743** (WeAct MiniSTM32H743VITX), written in **C** with **PlatformIO + STM32Cube HAL + TinyUSB**. USB-host HID mapping, PC/SimHub direct output, DFU updates and USB diagnostics are hardware-validated end to end on a real wheelbase, including full driving sessions.

The board has a two-channel DAC for the H-pattern outputs, 2 MB of flash for firmware and saved configuration, and two USB controllers for simultaneous PC and hub connections. Gear outputs drive the shifter port directly. The first flash requires BOOT0/RST; subsequent updates are hands-free through webconfig or SimHub.

See the [main README](../README.md), [RJ12 hardware reference](../schematics/README.md), and [protocol and calibration reference](PROTOCOL.md).

Prebuilt images and their SHA-256 manifests are published as **firmware-vX.Y.Z**
[GitHub Releases](https://github.com/Gugic/fanadapter/releases), independently of
the SimHub plugin. Firmware source/build changes trigger its own CI; webconfig
and plugin changes never rebuild it. Bump `FW_VER` in `src/firmware_info.h` for
each firmware release. That one value also supplies the JSON version, boot banner,
USB product and CDC interface labels (**Fanadapter vX.Y.Z**). USB VID/PID stays
`1209:FA00`, and the serial string/device revision stay stable so a version-label change alone does not invalidate existing COM assignments or browser grants. Clients filter
connections by that identity, so the name is for display rather than matching.

---

## 1. What you need

| Item | Notes |
|---|---|
| **WeAct MiniSTM32H743VITX** core board | Must be the **STM32H743VIT6**. ⚠️ See the warning below. |
| Powered USB 2.0 hub | Self-powered. See the [main README](../README.md#usb-hubs-important). |
| USB-C cable (**data**, not charge-only) | PC → board. Carries console, webconfig, DFU *and* board power. |
| USB breakout / sacrificial USB-A cable | Hub → the board's PB14/PB15 header. Needs D+, D−, GND **and VBUS**. |
| RJ12 cables + jacks, hookup wire | Wheelbase side. See [`schematics/`](../schematics/README.md). |

You do **not** need: an RC filter on the gear outputs (the STM32 has a real DAC), an ST-Link, or CubeProgrammer.

### ⚠️ Check the chip before you buy — and before you debug

The **WeAct MiniSTM32H7xx PCB is sold populated with different dies**, and they are visually
identical. The one you want is marked **STM32H743VIT6**.

The common look-alike is the **STM32H723VGT6**, which has **only one USB controller** (`OTG_HS`;
no `OTG_FS` at all) and therefore **cannot run this firmware** — it physically cannot be a USB
host and a USB device at the same time. On the H723, `PB14`/`PB15` aren't even USB pins; they're
a phantom of the shared PCB layout.

Worse, the failure is silent and misleading: H743-built code **hangs in clock init** on H723
silicon, giving you a dark LED, 0 V on the USB pins and no console — while DFU keeps working
(the ROM bootloader configures its own clock), so the board looks alive enough to send you
chasing firmware bugs. This cost a multi-hour debug session; don't repeat it.

**Verify:** read the chip marking, or check the device ID — **H743 = `0x450`**, **H723 = `0x483`**
(`DBGMCU_IDCODE` @ `0xE0042000`). A minimal LED blink built for the suspected target is the
fastest confirmation.

---

## 2. Pin map

Nothing here collides — the USB pins, output pins and console pins are all on separate
peripherals.

### USB (the part that surprises people)

| Controller | rhport | Pins | Connector | Role |
|---|---|---|---|---|
| **USB2_OTG_FS** | 0 | PA11 / PA12 | **on-board USB-C** | **DEVICE** — CDC console + webconfig, **and DFU**, and board power |
| **USB1_OTG_HS** | 1 | **PB14** (D−) / **PB15** (D+) | header pins | **HOST** — breakout → powered hub → wheel devices |

**This split is forced by hardware — do not "fix" it round the other way.** The on-board USB-C is
wired as a **device receptacle** (UFP: CC pulled down with Rd). That's precisely why the ROM DFU
bootloader enumerates through it, and precisely why **a USB hub plugged into it will never
attach** — both ends present as devices, CC negotiation never completes, and the board never sees
a D+ pull-up. No adapter or "OTG cable" fixes this; the board has no CC or role-switch logic.

The tested ROM DFU path uses the on-board USB-C, so flashing does not share a port with the
hub — **you never have to unplug the wheels to re-flash.**

### Wheelbase outputs

Port pinouts, RJ12 wiring and the voltage tables are in [`schematics/README.md`](../schematics/README.md).

| Function | STM32 pin | Peripheral |
|---|---|---|
| H-pattern gear X | **PA4** | DAC1 ch1 |
| H-pattern gear Y | **PA5** | DAC1 ch2 |
| Sequential up | **PC6** | GPIO open-drain |
| Sequential down | **PC7** | GPIO open-drain |
| Handbrake PWM (fallback) | **PC8** | TIM3_CH3, ~15.6 kHz |
| Pedal port UART TX | **PA2** | USART2_TX |
| Pedal port UART RX | **PA3** | USART2_RX |

**The gear outputs drive the shifter port directly — do not add an RC filter.** These are real
DAC outputs, not filtered PWM. A capacitor on a DAC output buffer can oscillate.

**The handbrake PWM on PC8 is a fallback, not the primary path.** On a modern base the handbrake
rides the pedal UART stream; leave the dedicated handbrake RJ12 unplugged. PC8 (plus an RC filter)
is an alternative for a rig not using the pedal-stream handbrake. The firmware writes both, so the wiring stays
forward-compatible.

### Console / misc

| Function | Pin | Notes |
|---|---|---|
| USART1 console fallback | PA9 (TX) / PA10 (RX) | **8E1**, 115200. Needs a USB-UART bridge. Unused in a normal build. |
| User LED | PE3 | Active-high. Heartbeat blinks at ~0.5 Hz when the firmware is running. |
| User button | PC13 | Unused by the firmware. |
| Additional heartbeat outputs | PB7, PB0, PB1, PE1, PD13 | Still toggled alongside PE3 by the bring-up heartbeat implementation; reserve these pins. |

---

## 3. Wiring

### PC side — one cable

**PC → the board's on-board USB-C.** That single pre-soldered connector gives you the CDC
console, webconfig, DFU flashing, and board power. Nothing to solder.

⚠️ Use a **data** USB-C cable. A charge-only cable powers the board and blinks the LED but
enumerates nothing — this looks exactly like a dead board or failed flash.

### Hub side — four wires

Wire a USB breakout (or a cut-open USB-A cable) to the **PB14/PB15 header**:

| Breakout | Board |
|---|---|
| **D+** | **PB15** |
| **D−** | **PB14** |
| **GND** | **GND** |
| **VBUS (5 V)** | **board 5 V** |

⚠️ **The VBUS wire is not optional.** A hub needs to *see* VBUS on its upstream port to detect
that a host is present. Without it the hub silently never attaches and the console just sits at
`waiting for devices on the hub...` forever. Feed upstream VBUS from the board's 5 V rail;
the hub's own supply powers its downstream devices. Verify that the chosen hub does not
backfeed its supply into the board through upstream VBUS.

Then: **powered hub → the breakout**, and your shifters / handbrake / pedals → the hub.

### Wheelbase side

Follow the RJ12 pinouts, assembly instructions and pre-flight multimeter checks in
[`schematics/README.md`](../schematics/README.md). Wire PA4/PA5 directly to the gear signals;
only the optional handbrake PWM fallback on PC8 needs an RC filter.

The pedal port's GND pins 1/2/3 all tie to common ground.

---

## 4. Build & flash

Requires Python with PlatformIO (`pip install platformio`). No separate ARM GCC, ST-Link or
CubeProgrammer needed — PlatformIO fetches the toolchain, and TinyUSB is pulled from upstream git
(SHA-pinned in `platformio.ini`), so there's nothing to vendor.

```sh
# build
python -m platformio run -d firmware-stm32 -e weact_h743

# flash (board must be in DFU — see below)
python -m platformio run -d firmware-stm32 -e weact_h743 -t upload
```

### Entering DFU

**First flash — the button dance.** Hold **BOOT0**, tap **RST**, release **BOOT0**. The board
enumerates as `STM32 BOOTLOADER` (VID `0483` PID `df11`).

This is ST's factory ROM bootloader; it works before fanadapter is installed, regardless of
the board's demo firmware. In Chrome/Edge, open [webconfig](https://gugic.github.io/fanadapter/)
and choose **Flash → Install**, without using Connect first. Pick **STM32 BOOTLOADER** when
prompted. Once programming finishes, **Connect to Fanadapter** grants the new serial port
permission and confirms the version. A cancelled connection leaves the firmware installed.

**Every flash after that — hands-free.** Once fanadapter firmware is on the board, type `dfu` at
the console (or send the JSON command `{"cmd":"dfu"}` — it's what webconfig's **Flash** button
uses) and the board reboots into the ROM bootloader on the same USB-C port. No buttons, no cable
swapping. `reboot` restarts the app the same way.

**No toolchain? Update from the browser.** webconfig's header has a **Flash** button opening a
one-click updater: the deployed site mirrors the released firmware selected by `FW_VER` on `main`, and one press reboots the
adapter into the bootloader, flashes over **WebUSB**, boots the result and reconnects — no
PlatformIO, no dfu-util, no buttons after the initial installation. Chromium remembers the
separate bootloader and serial permissions per browser/origin. CI also publishes the image as the `fanadapter-stm32-firmware`
artifact on the *Verify STM32 firmware* workflow for manual flashing. Windows caveat: WebUSB needs
the WinUSB driver bound to `STM32 BOOTLOADER` — installing STM32CubeProgrammer (or a one-time
Zadig run) can set that up. Check that the bound driver is **WinUSB**: dfu-util also supports other driver backends, so dfu-util working alone does not prove WebUSB/SimHub compatibility.

(Mechanism, because the obvious version does not work: the ROM serves **no** interfaces when an
app branches to it directly — the command instead parks a magic token in DTCM, does a real
`NVIC_SystemReset`, and the app branches to the ROM as the *first* statement of `main()`, giving
the ROM the reset-default chip it expects. Details in PORT-STATUS.)

After flashing, the `:leave` in the upload command boots straight back into the app — no reset
button needed.

### Saved config survives a reflash

Bindings live in **flash bank 2** while code lives in bank 1, and the flasher does a
**bank-1-only erase** — so re-flashing does **not** wipe your mappings. A `CONFIG_VERSION` bump
is the exception: the older blob is rejected and the firmware boots with defaults.

---

## 5. First connect

Plug the USB-C into the PC. The board appears as a CDC serial port:
**VID `1209` / PID `FA00`**, **Fanadapter vX.Y.Z** on firmware 0.6.1 and later.
Older native builds may show "Fanadapter STM32 enumerator"; clients still recognize the USB IDs.

**webconfig:** open the UI and hit Connect — it talks straight to that port, no bridge, no
adapter. See [`webconfig/README.md`](../webconfig/README.md).

**Console:** open the same port in any terminal (baud is ignored). You get a boot banner, an
attach/detach log for every device on the hub, and a tiny CLI: `dfu`, `reboot`, `?`. The banner
reprints when a terminal asserts DTR, so a late connection isn't left staring at a blank screen.

⚠️ **webconfig, SimHub and a terminal cannot share the serial port.** Close other serial clients before connecting. The built-in updaters send `dfu` and release their own serial connection automatically; a manual flashing procedure must release it too.

### Working checklist

1. **LED blinks** ~0.5 Hz → firmware is running.
2. **Banner prints** on the console → USB device side is up.
3. **Devices log as they attach** → USB host side and the hub are up. Slots represent usable HID interfaces, so a composite device can use more than one. Input-less interfaces are excluded; the tested four-device rig claims four slots. `list_devices` includes empty slots, marked `connected:false`.
4. **Live input moves in webconfig** → HID decode is up. Now bind and calibrate exactly as in the
   [firmware reference](PROTOCOL.md#calibration).
5. **Pedals enumerate on the base as "ClubSport Pedals V3"** → the pedal UART is up. This is the
   last and fussiest link.

---

## 6. JSON commands

The complete [JSON API reference](PROTOCOL.md#json-api-reference) covers protocol **6**: configuration, live telemetry, output tests, PC direct output (including no-reply `stream_axes`), DFU entry and USB diagnostics. It also documents the configuration schema and calibration procedure.

Direct-output overrides are **sticky with no firmware-side timeout**, so clients must send `release_outputs` when stopping or disconnecting.

---

## 7. Troubleshooting

**Dark LED, no console, 0 V on the USB pins — but DFU works.**
Check for an **STM32H723VGT6** before debugging firmware; it caused this exact symptom during bring-up. See §1. If the chip is H743, also verify the image target and board power/clock configuration.

**Board powers up but enumerates nothing.**
Try a known data-capable USB-C cable and another PC port. A charge-only cable powers the LED but carries no data. If ROM DFU enumerates but the application does not, verify the board and firmware target.

**The hub never attaches; console sits at `waiting for devices on the hub...`.**
Missing **VBUS** on the breakout's upstream (§3), or you plugged the hub into the on-board USB-C
(§2 — it can't host, ever).

**webconfig connects but every command times out.**
First disconnect and reconnect to reset the client session. If the problem persists, check the firmware version and logs. Builds predating the CDC fixes gated replies on `tud_cdc_connected()` (which requires DTR); current firmware uses `tud_mounted()`, because webconfig deasserts DTR. Update an affected old build.

**More devices listed than you own (e.g. 4 devices filling 7 slots).**
Also fixed. Composite controllers (the Logitech RS Shifter & Handbrake and RS H-Shifter) expose a
second HID interface with no axes, buttons, hat or keys; those are now rejected at mount. Re-flash.

**Pedals show on the base for ~2 s, then drop to analog and jitter — repeating.**
⚠️ **Suspect the wiring before the firmware.** The pedal port is unforgiving about line integrity.
A marginal jumper — especially **ground**, or TX (PA2 → pedal pin 5) — CRC-corrupts the 100 Hz
frames; the base rejects them and falls back to analog sensing. Re-seating a wire fixed this during bring-up. Also inspect the `[pedals]` trace: a `stream gap` before an `RX error burst` points to an adapter polling/streaming stall, as described below. Remaining in `STREAMING` alone does not prove every frame reached the wheelbase intact.

**Pedals appear as ClubSport Pedals V3, but values stay at 0%.**
Check live inputs, channel bindings and output values in webconfig or SimHub, then query
`pedals_status` to confirm `STREAMING_115K`. Verify pedal RJ12 pins 1, 2 and 3 all connect to
GND, PA2 (TX) goes to pin 5, and PA3 (RX) goes to pin 4. `reset_pedals` re-arms the handshake.

**Sequential shifts don't register.**
Check PC6 → Shifter 2 pin 4 and PC7 → pin 5, with common ground on pin 1. The signal pins
should idle near 3.3 V from the wheelbase's pull-ups and briefly pull low on a shift. Keep the
GPIOs in open-drain mode. Adjust `pulseMs` in the Outputs tab if shifts are missing or double-fire.

**Everything worked, then the wheelbase was power-cycled.**
Handled automatically — the firmware detects the resulting UART error burst and re-arms the
handshake. If it ever sticks, `reset_pedals` (webconfig's *Re-arm pedals handshake* button) forces
it.

**`[pedals] RX error burst` with no power cycle anywhere near the rig.**
The wheelbase's own link watchdog re-initiates the handshake whenever the adapter's 100 Hz stream
gaps for long enough — so the burst means "the base gave up on the stream", not "someone pulled
the plug". The log now says why: the burst line breaks down error types (`fe=` wrong-baud traffic
from the re-handshaking base, `ore=` the adapter's own UART overran), and a preceding
`[pedals] stream gap N ms — main loop stalled` line means the adapter caused it (e.g. `save_config`
blocks interrupts for roughly 1 s during the flash erase — a re-handshake right after hitting Save is possible).
A burst with *no* gap line before it points at the base side or the wiring instead.

**One device's inputs freeze at their last values, but it's still listed as connected.**
The investigation reproduced a device-side hang and fixed adapter-side stalls; a final long driving soak is still unrecorded (see [remaining validation](PORT-STATUS.md#remaining-validation-and-known-limitations)). The Simnet pedal's own
firmware hangs its USB endpoint if the adapter stops polling it for ~2 s mid-stream (measured:
survives 1.6 s, dies at 2.0 s), and older adapter firmware really did create such gaps under
load; the post-fix bench measured a worst gap of ~120 ms during connection, excluding Save. Full record in PORT-STATUS. Mid-session
recovery if it ever recurs: **replug the frozen device at the hub** (no reboot needed). One
known residual near-miss: **Save to adapter** pauses polling ~1 s for the flash write — a pedal
stutter right after a Save is that, and it recovers by itself.

**Gear voltages read slightly off.**
Recalibrate per gear with `set_gear_dac`, then `save_config`. Reverse's near-rail X reading ~0.2 V
low is expected (DAC output-buffer clamp) — the base normalizes it during shifter calibration.

---

## 8. Source layout

| File | Purpose |
|---|---|
| `main.c` | Clocks, USB hardware init, both TinyUSB stacks, console + CLI, main loop |
| `usb_input.{c,h}` | 8-slot HID device pool (the `InputSource` implementation) |
| `hid_parse.{c,h}` | Generic HID report-descriptor walker + report decode |
| `input_source.{c,h}` | `InputSource` vtable — the abstraction the mapping layer binds against |
| `mapping.{c,h}` | `Config` schema, evaluators, channel updaters |
| `config_store.{c,h}` | Config persistence in internal flash (bank 2) |
| `outputs.{c,h}` | DAC H-pattern + neutral-transit FSM, sequential pulse FSM, handbrake PWM |
| `pedals.{c,h}` | CSL Elite V2 pedal-port UART emulator (USART2) |
| `protocol.{c,h}` | Line-based JSON command dispatcher + async events |
| `json_min.{c,h}` | Zero-alloc JSON reader |
| `usb_descriptors.c` | CDC device descriptors |

Main-loop dispatch order matters — `pedals_update()` runs **last**, and long work added ahead of
it will slip the 100 Hz pedal cadence.

The `Config` struct, `scaleAxis` math, CRC-32 and the JSON command shapes are **shared contracts
with webconfig and the SimHub plugin**. Change a contract in all three consumers — see the cross-file invariants table in
[`AGENTS.md`](../AGENTS.md).

---

## 9. Port history

Development notes, milestone log, bugs already fixed (and why), and the abandoned bring-up
hardware live in **[`PORT-STATUS.md`](PORT-STATUS.md)**. Read that before changing firmware code;
you don't need it to build one of these.

---

## License

Apache 2.0, same as the rest of the software in this repo — see [`LICENSE.md`](../LICENSE.md).
