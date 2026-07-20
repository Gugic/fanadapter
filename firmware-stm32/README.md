# fanadapter — STM32H743 build & wiring guide

The **STM32H743 build** of fanadapter: a full-featured alternative to the Teensy 4.1 build in
[`firmware/`](../firmware/README.md). Same WebSerial JSON protocol, byte-identical `Config`
schema, same [`webconfig`](../webconfig/README.md) UI — so everything in the
[main README](../README.md) and the [hardware reference](../schematics/README.md) applies here
too. Only the microcontroller and its wiring differ.

Written in **C** on **PlatformIO + STM32Cube HAL + TinyUSB** (not Arduino). Feature parity with
the Teensy build, hardware-validated end to end on a real wheelbase including a full driving
session.

> Building the Teensy version instead? Stop here — go to [`firmware/README.md`](../firmware/README.md).
> This directory is only for the STM32 variant.

**Why pick this over the Teensy?** The board is cheaper and easier to source, and it has a real
DAC — so the H-pattern gear outputs drive the shifter port directly and the **RC filter stage
disappears** from the build. Also 2 MB of flash and a second USB controller, so config storage and
a native console cost you nothing extra.

**Trade-offs, honestly:** the toolchain is fussier than Arduino, the first flash needs a manual
BOOT0/RST dance, and you still hand-wire a USB breakout to the host header (the Teensy wants a
soldered host header instead — call that a wash).

---

## 1. What you need

| Item | Notes |
|---|---|
| **WeAct MiniSTM32H743VITX** core board | Must be the **STM32H743VIT6**. ⚠️ See the warning below. |
| Powered USB 2.0 hub | Self-powered. Same hub caveats as the Teensy build — see the [main README](../README.md#usb-hubs-important). |
| USB-C cable (**data**, not charge-only) | PC → board. Carries console, webconfig, DFU *and* board power. |
| USB breakout / sacrificial USB-A cable | Hub → the board's PB14/PB15 header. Needs D+, D−, GND **and VBUS**. |
| RJ12 cables + jacks, hookup wire | Wheelbase side. Identical to the Teensy build — see [`schematics/`](../schematics/README.md). |

You do **not** need: an RC filter on the gear outputs (the STM32 has a real DAC), a USB host
header, an ST-Link, or CubeProgrammer.

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

Happy side effect: DFU is fixed to OTG_FS in silicon, so flashing no longer shares a port with the
hub — **you never have to unplug the wheels to re-flash.**

### Wheelbase outputs

Identical in function to the Teensy build; only the pin numbers change. Port pinouts, RJ12
wiring and the voltage tables are in [`schematics/README.md`](../schematics/README.md).

| Function | STM32 pin | Peripheral | Teensy equivalent |
|---|---|---|---|
| H-pattern gear X | **PA4** | DAC1 ch1 | Pin 4 (PWM + RC) |
| H-pattern gear Y | **PA5** | DAC1 ch2 | Pin 5 (PWM + RC) |
| Sequential up | **PC6** | GPIO open-drain | Pin 6 |
| Sequential down | **PC7** | GPIO open-drain | Pin 7 |
| Handbrake PWM (fallback) | **PC8** | TIM3_CH3, ~15.6 kHz | Pin 8 |
| Pedal port UART TX | **PA2** | USART2_TX | Pin 14 |
| Pedal port UART RX | **PA3** | USART2_RX | Pin 15 |

**The gear outputs drive the shifter port directly — do not add an RC filter.** These are real
DAC outputs, not filtered PWM. A capacitor on a DAC output buffer can oscillate.

**The handbrake PWM on PC8 is a fallback, not the primary path.** On a modern base the handbrake
rides the pedal UART stream; leave the dedicated handbrake RJ12 unplugged. PC8 (plus an RC filter)
is only for a rig with no pedal port. The firmware writes both, so the wiring stays
forward-compatible.

### Console / misc

| Function | Pin | Notes |
|---|---|---|
| USART1 console fallback | PA9 (TX) / PA10 (RX) | **8E1**, 115200. Needs a USB-UART bridge. Unused in a normal build. |
| User LED | PE3 | Active-high. Heartbeat blinks at ~0.5 Hz when the firmware is running. |
| User button | PC13 | Unused by the firmware. |

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
`waiting for devices on the hub...` forever. Feeding it from the board's 5 V rail is safe — a
self-powered hub only senses that line, it doesn't draw from it.

Then: **powered hub → the breakout**, and your shifters / handbrake / pedals → the hub.

### Wheelbase side

Unchanged from the Teensy build apart from the pin numbers in the table above — same RJ12
pinouts, same protocols, same pre-flight multimeter checks. Follow
[`schematics/README.md`](../schematics/README.md), substituting STM32 pins for Teensy pins, and
**skip the RC filter step** for the gear outputs.

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

**Every flash after that — hands-free.** Once fanadapter firmware is on the board, type `dfu` at
the console (see below) and it jumps straight to the ROM bootloader on the same USB-C port. No
buttons, no cable swapping. `reboot` restarts the app the same way.

After flashing, tap **RST** (or let dfu-util's reset do it) to run.

### Saved config survives a reflash

Bindings live in **flash bank 2** while code lives in bank 1, and the flasher does a
**bank-1-only erase** — so re-flashing does **not** wipe your mappings. A `CONFIG_VERSION` bump
is the exception: the older blob is rejected and the firmware boots with defaults.

---

## 5. First connect

Plug the USB-C into the PC. The board appears as a CDC serial port:
**VID `1209` / PID `FA00`**, "Fanadapter STM32 enumerator".

**webconfig:** open the UI and hit Connect — it talks straight to that port, no bridge, no
adapter. See [`webconfig/README.md`](../webconfig/README.md).

**Console:** open the same port in any terminal (baud is ignored). You get a boot banner, an
attach/detach log for every device on the hub, and a tiny CLI: `dfu`, `reboot`, `?`. The banner
reprints when a terminal asserts DTR, so a late connection isn't left staring at a blank screen.

⚠️ **webconfig and a terminal can't both hold the port**, and neither can share it with the
flasher. Disconnect webconfig before flashing or opening a terminal.

### Working checklist

1. **LED blinks** ~0.5 Hz → firmware is running.
2. **Banner prints** on the console → USB device side is up.
3. **Devices log as they attach** → USB host side and the hub are up. Each physical device should
   claim exactly one slot; `list_devices` in webconfig should show *your* device count, no
   phantoms.
4. **Live input moves in webconfig** → HID decode is up. Now bind and calibrate exactly as in the
   [firmware reference](../firmware/README.md#calibration).
5. **Pedals enumerate on the base as "ClubSport Pedals V3"** → the pedal UART is up. This is the
   last and fussiest link.

---

## 6. Troubleshooting

**Dark LED, no console, 0 V on the USB pins — but DFU works.**
You almost certainly have an **STM32H723VGT6**, not an H743. See §1. Check the chip before
touching anything else.

**Board powers up but enumerates nothing.**
Charge-only USB-C cable. Swap it — this one wastes a lot of time.

**The hub never attaches; console sits at `waiting for devices on the hub...`.**
Missing **VBUS** on the breakout's upstream (§3), or you plugged the hub into the on-board USB-C
(§2 — it can't host, ever).

**webconfig connects but every command times out.**
Firmware predating the CDC fixes. Current firmware gates CDC output on `tud_mounted()`, not
`tud_cdc_connected()` — the latter also requires the host to assert DTR, and webconfig
deliberately *deasserts* DTR. Re-flash.

**More devices listed than you own (e.g. 4 devices filling 7 slots).**
Also fixed. Composite controllers (the Logitech RS Shifter & Handbrake and RS H-Shifter) expose a
second HID interface with no axes, buttons, hat or keys; those are now rejected at mount. Re-flash.

**Pedals show on the base for ~2 s, then drop to analog and jitter — repeating.**
⚠️ **Suspect the wiring before the firmware.** The pedal port is unforgiving about line integrity.
A marginal jumper — especially **ground**, or TX (PA2 → pedal pin 5) — CRC-corrupts the 100 Hz
frames; the base rejects them and falls back to analog sensing. This has been a re-seated wire
every time, never a code change. The `[pedals]` console trace is the diagnostic: if it reaches
`STREAMING` and stays there, the drop is base-side, i.e. electrical.

**Everything worked, then the wheelbase was power-cycled.**
Handled automatically — the firmware detects the resulting UART error burst and re-arms the
handshake. If it ever sticks, `reset_pedals` (webconfig's *Re-arm pedals handshake* button) forces
it.

**Gear voltages read slightly off.**
Recalibrate per gear with `set_gear_dac`, then `save_config`. Reverse's near-rail X reading ~0.2 V
low is expected (DAC output-buffer clamp) — the base normalizes it during shifter calibration.

---

## 7. Source layout

| File | Purpose |
|---|---|
| `main.c` | Clocks, USB hardware init, both TinyUSB stacks, console + CLI, main loop |
| `usb_input.{c,h}` | 8-slot HID device pool (the `InputSource` implementation) |
| `hid_parse.{c,h}` | Generic HID report-descriptor walker + report decode |
| `input_source.{c,h}` | `InputSource` vtable — the abstraction the mapping layer binds against |
| `mapping.{c,h}` | `Config` schema, evaluators, channel updaters (mirrors the Teensy `mapping.cpp`) |
| `config_store.{c,h}` | Config persistence in internal flash (bank 2) |
| `outputs.{c,h}` | DAC H-pattern + neutral-transit FSM, sequential pulse FSM, handbrake PWM |
| `pedals.{c,h}` | CSL Elite V2 pedal-port UART emulator (USART2) |
| `protocol.{c,h}` | Line-based JSON command dispatcher + async events |
| `json_min.{c,h}` | Zero-alloc JSON reader (the ArduinoJson replacement) |
| `usb_descriptors.c` | CDC device descriptors |

Main-loop dispatch order matters — `pedals_update()` runs **last**, and long work added ahead of
it will slip the 100 Hz pedal cadence.

The `Config` struct, `scaleAxis` math, CRC-32 and the JSON command shapes are **shared contracts
with webconfig**. Change one side, change the other — see the cross-file invariants table in
[`AGENTS.md`](../AGENTS.md).

---

## 8. Port history

Development notes, milestone log, bugs already fixed (and why), and the abandoned bring-up
hardware live in **[`PORT-STATUS.md`](PORT-STATUS.md)**. Read that before changing firmware code;
you don't need it to build one of these.

---

## License

Apache 2.0, same as the rest of the software in this repo — see [`LICENSE.md`](../LICENSE.md).
