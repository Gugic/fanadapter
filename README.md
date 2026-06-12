# fanadapter

USB HID → Fanatec wheelbase adapter built on an **ESP32-S3** (N16R8), combined with a browser-based WebSerial configuration UI.

An open-source adapter that lets arbitrary USB HID sim racing peripherals (H-pattern shifters, sequential shifters, handbrakes, and USB pedals — plus gamepads, D-pad/Hat controllers, and keyboards) connect to a Fanatec wheelbase as if they were native Fanatec gear. This is particularly valuable on game consoles (PS4/PS5/Xbox Series X|S) where third-party USB peripherals cannot be plugged directly into the console.

From the perspective of the wheelbase, the adapter looks like genuine Fanatec hardware (supporting a digital UART pedal stream and analog/digital shifter and handbrake signals).

> **Note on platform:** This project originally ran on a Teensy 4.1 (through version 0.6.0). Starting with **v0.7.0**, the firmware was ported to ESP32-S3 to lower the MCU cost and use a more available board. The webconfig WebSerial protocol is identical across both — the JSON wire shape didn't change. If you specifically need the Teensy build, check out a tag before v0.7.0 from git history.

---

## Architecture Block Diagram

```
USB H-pattern shifter ─┐
USB sequential shifter ┼──► [Powered USB Hub] ──► [ESP32-S3] ──► RJ12 ──► Fanatec Wheelbase
USB handbrake ─────────┘                                  RJ12        (up to 3 ports)
USB pedals ────────────┘                                  RJ12
```

---

## Compatibility

### Wheelbase Bases
* **Verified:** Fanatec ClubSport DD+ (15 Nm)
* **Expected to work** (same RJ12 protocols across generations, but untested here):
  * CSL Elite 1.1 / V2
  * Podium DD1 / DD2
  * ClubSport DD
  * CSL DD
  * CSW V2.5
* **Likely Incompatible:**
  * CSR Elite and older bases predating the current Shifter 1/2 RJ12 layout.
  * Bases without dedicated handbrake ports (Pre-DD generation may share the handbrake with the pedal port).

### USB Hubs (Important)
The ESP32-S3 has a single native USB OTG port, so a **powered USB 2.0 hub** is required to connect multiple peripherals. The ESP-IDF USB host stack supports external hubs but is subject to a hard ~8-channel EHCI ceiling on this chip — see `firmware/README.md` for limits.

> [!IMPORTANT]
> Generic ESP32-S3 dev boards (Lonely Binary Gold Edition, YD-ESP32-S3, etc.) ship with the native USB port wired so VBUS is **input-only**. A one-time solder-jumper mod is required before any USB host operation will work — see `firmware/README.md` → Hardware setup. Without this mod, the firmware boots cleanly and the host stack initializes, but no downstream device ever enumerates because they have no 5V to power their controllers.

### USB Host Stack
The current firmware uses [EspUsbHost](https://github.com/tanakamasayuki/EspUsbHost) (master branch), an Arduino-friendly wrapper over ESP-IDF's `usb_host` library. It handles enumeration, hub support, and parses HID gamepad / keyboard reports into per-usage field values that we consume directly. The Teensy build used `USBHost_t36`; the wire-level behavior of the adapter is identical.

---

## Repository Layout

The project is structured into three main directories:

1. **[`firmware/`](firmware/README.md)**  
   The Arduino sketch (arduino-esp32 v3.3+) for the ESP32-S3. It reads USB HID inputs from the device pool via `EspUsbHost`, evaluates custom user mappings, manages calibration saved to NVS (Preferences), and emulates CSL Elite V2 pedals along with driving shifter and handbrake ports.
   
2. **[`webconfig/`](webconfig/README.md)**  
   A beautiful, modern React 19 + TypeScript + Tailwind + shadcn/ui configuration interface. It runs directly in desktop Chromium-based browsers (Chrome, Edge, Brave) and communicates with the firmware over WebSerial (line-based JSON) to capture binds, calibrate axis deadzones, and test outputs.
   
3. **[`schematics/`](schematics/README.md)**  
   Detailed hardware pinouts, port protocols, wiring diagrams, Bill of Materials (BoM), and step-by-step physical assembly and pre-flight multimeter tests.

---

## Project Roadmap

The following features and improvements are planned for future updates:

* **Analog Pedal Fallback** — Supporting older bases predating CSL Elite V2 by implementing a 4-channel analog DAC output stage (MCP4728) on the pedal RJ12.
* **Software-Controlled Handbrake Detect** — Controlling Shifter 1 / Handbrake port detection via a Teensy GPIO rather than hardwired ground, allowing the adapter to show or hide ports dynamically.
* **Custom PCB Design** — A small, custom-routed KiCad PCB to replace the breadboard.
* **3D-Printed Enclosure** — A robust case with panel-mounted RJ12 jacks and a secure powered USB hub integration.
* **OLED Display & Buttons** — On-board configuration interface for status reports, calibration, and profile-switching without a computer.
* **SimHub Integration** — Driving the same WebSerial JSON protocol from SimHub.
* **0-5V Handbrake Scaler** — Incorporating an op-amp scaling stage (MCP6001 with ~1.52 gain) for native 0-5V analog voltage swing.
* **Mode Switching on Shifter 1** — Software toggle between H-pattern and sequential modes on a single physical RJ12 port.
* **Built-in USB Hub** — Designing a USB hub circuit directly onto the project's custom PCB to ensure maximum stability and compatibility.
* **Onboard VBUS Supply** — Custom PCB rev would integrate a 5V-to-OTG-VBUS switching circuit so users don't need the solder-jumper mod on a generic ESP32-S3 dev board.

---

## References and Acknowledgments

This project relies on the incredible reverse-engineering efforts of the sim racing DIY community:

* **[Universal Shifter Interface for Fanatec Wheelbase](https://www.diy-sim.com/guides/projects/item/universal-fanatec-shifter-interface)** by DIY-Sim — H-pattern resistor ladder voltages and column/row maps.
* **[Fanashifter](https://github.com/juanmcasillas/Fanashifter)** by Juan M. Casillas — Sequential shifter RJ12 pinout and pull-up behaviors.
* **[Fanatec ClubSport Shifter SQ V1.5 USB Adapter DIY](https://hackaday.io/project/171155-fanatec-clubsport-shifter-sq-v15-usb-adapter-diy)** by Yin Zhong — Shifter protocol structures.
* **[Conversion of a Logitech Shifter for Fanatec Wheelbase Compatibility](https://www.gtplanet.net/forum/threads/conversion-of-a-logitech-shifter-for-fanatec-wheelbase-compatibility-enabling-7th-gear-other-mods.384099/)** by B-spec / Bob — 7th gear logic and mappings.
* **[Guide to convert third-party (Hall sensor) handbrake to Fanatec base compatible](https://www.oakheartcustombuilds.com/handleidingen/GuideThirdPartyHandbrakeToFanatec_v1.0.pdf)** by Oakheart Custom Builds — Handbrake signal and pinout references.
* **[fanatec-pedal-emulator](https://github.com/GeekyDeaks/fanatec-pedal-emulator)** by GeekyDeaks — UART framing, CSL Elite V2 emulation state machines, and CRC-8 tables.

---

## License

`fanadapter` is **dual-licensed**, and both licenses are deliberately permissive: the software (firmware + webconfig) under **Apache 2.0**, the hardware design under **CERN-OHL-P v2**. They were chosen to make building on, modifying, and *commercializing* this project as frictionless as possible — attribution is the only obligation and no copyleft applies. Full terms, SPDX identifiers, and the build-from-scratch provenance note are in **[LICENSE.md](LICENSE.md)**.

### Manufacturing and commercial reuse — explicitly welcome

If you have the capacity to turn this into a polished product — a small enclosed unit with an integrated powered USB hub, panel-mount RJ12 jacks, a clean PCB instead of the breadboard build, proper EMI considerations, and decent QC — **please do**. The permissive licenses are chosen precisely to make this easy.

The sim racing community has a real gap for affordable, well-built USB-HID-to-Fanatec adapters. The few existing commercial products are either expensive, awkward to use, or limited to specific peripheral brands. A polished version of this design — even at a reasonable markup for the work involved — would be a genuinely useful product.

If you take this to market, attribution to this repository and the prior reverse-engineering work cited in [References and Acknowledgments](#references-and-acknowledgments) is the only ask. Pull requests improving the design upstream are appreciated but not required.

---

**Maintainer:** [Gugic](https://github.com/Gugic)  
**Issues / contributions welcome:** [https://github.com/Gugic/fanadapter/issues](https://github.com/Gugic/fanadapter/issues)
