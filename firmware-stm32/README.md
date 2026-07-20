# fanadapter — STM32H743 port (bring-up)

Work-in-progress port of the fanadapter firmware from Teensy 4.1 to an
**STM32H743VGT6** core board (WeAct MiniSTM32H743 clone). This directory currently
holds only the **USB host enumerator** — a smoke test that lists every device behind
the powered hub on a serial console, to confirm the H7's OTG_HS controller sees all of
them (the 16-host-channel reason we moved off ESP32 — see `../port-esp.md` on the
`esp32` branch).

Built with **PlatformIO + STM32Cube HAL + TinyUSB**, not Arduino.

## USB topology

The two USB controllers are split by role:

| Controller | Pins | Connector | Role |
|---|---|---|---|
| **OTG_HS** (USB1) — 16 host channels | PB14 (DM) / PB15 (DP) | onboard **USB-C** | **USB host**, full-speed, internal PHY → powered hub → wheel devices |
| **OTG_FS** (USB2) | PA11 (DM) / PA12 (DP) | flying leads → PC | **CDC serial console + DFU flashing** |

In TinyUSB on STM32H7, rhport 0 = OTG_FS and rhport 1 = OTG_HS.

### Wiring

- **Powered hub → USB-C** (OTG_HS host). The hub is self-powered.
- **PA11/PA12 flying leads → a USB cable to the PC**: D-→PA11, D+→PA12, GND→GND,
  cable 5V→board 5V. This one link does both DFU flashing and the serial console.

DFU flashing *must* be on OTG_FS (PA11/PA12) — that's the only port the STM32H743 ROM
bootloader exposes (AN2606).

## Build & flash

```sh
# from repo root
python -m platformio run -d firmware-stm32                 # build
# put the board in DFU mode: hold BOOT0, tap RST, release BOOT0
python -m platformio run -d firmware-stm32 -t upload       # flash via dfu-util
# tap RST to run
```

DFU mode = board enumerates as `STM32 BOOTLOADER` (VID `0483` PID `df11`).

## Console

After flashing and reset, the OTG_FS link enumerates on the PC as a CDC COM port
(VID `1209` PID `fa00`, "Fanadapter STM32 enumerator"). Open it (115200, any baud — CDC
ignores it) and **assert DTR**; the firmware prints a banner on DTR and then logs each
device as it attaches:

```
[+] device attached, address 5
    VID:PID = 046D:C26B  class=0  USB 2.00
    manufacturer: Logitech
    product:      G27 Racing Wheel
    HID iface mounted: addr=5 instance=0 protocol=0
```

`TinyUSB` enumeration tracing is on (`CFG_TUSB_DEBUG=2` in `include/tusb_config.h`);
drop it to `0` once things are stable.

## Status

Bring-up. Not yet verified on hardware. None of the fanadapter logic (mapping, pedals,
protocol, EEPROM) is ported yet — this is purely device discovery.
