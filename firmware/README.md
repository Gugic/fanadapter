# fanadapter Firmware Reference

The `firmware/` directory contains the Arduino sketch that runs on an **ESP32-S3** (N16R8 variant) — a port of the original Teensy 4.1 build. It handles USB host HID polling, custom binding evaluation, NVS state storage, a serial JSON API, and emulates Fanatec pedals + gear DACs over RJ12 outputs.

> **Heads-up — this is a port.** The Teensy 4.1 build lived at version 0.6.0; the ESP32-S3 build starts at **0.7.0** and replaces the entire `firmware/` tree. The webconfig UI is unchanged (the JSON protocol round-trips identically). If you need the Teensy build, check out a tag from before this port — git history has it.

---

## Toolchain & Commands

### Prerequisites

* **Board:** ESP32-S3 N16R8 dev board with two USB-C ports. Reference design: YD-ESP32-S3 / VCC-GND Studio (Lonely Binary's "Gold Edition" is a relabel of this). Any board with the same ESP32-S3-WROOM-1 module + separate UART-bridge port + USB-OTG port should work.
* **Core:** [arduino-esp32](https://github.com/espressif/arduino-esp32) v3.3.x, installed via:
  ```sh
  arduino-cli core install esp32:esp32@3.3.8
  ```
* **Required Libraries:**
  * **ArduinoJson** ≥ 7.0 — `arduino-cli lib install ArduinoJson`
  * **EspUsbHost** (master from git, not the Library Manager version 1.0.1):
    ```sh
    arduino-cli config set library.enable_unsafe_install true
    arduino-cli lib install --git-url https://github.com/tanakamasayuki/EspUsbHost.git
    ```

### Hardware setup — required one-time solder mod

The ESP32-S3 OTG port on generic N16R8 boards **does not supply 5V outbound** — the connector's VBUS pin is wired through a diode (or a normally-open solder jumper) to the board's 5V rail as an *input* only. Downstream USB devices need 5V to power their controllers and assert attach, so without this mod no device will enumerate even though the USB host stack starts cleanly.

**Procedure (YD-ESP32-S3 layout — what the Lonely Binary board is):**

1. Power the board via the **UART** USB-C port (this provides the 5V rail).
2. Flip the board over. On the back side, behind the USB-C connectors / WROOM-1 module, find the solder jumper labeled **`USB-OTG`** (sometimes just `OTG`). It ships open — two adjacent exposed-copper pads.
3. With a multimeter in continuity mode, confirm the 5V rail and the OTG-port VBUS pin are **not** already connected. (If they already beep, your unit shipped pre-bridged — skip step 4.)
4. Flood solder across the two `USB-OTG` pads until they form a solid blob. A 0Ω 0402 resistor across the pads works too.
5. Power on, measure ~5.0 V on the OTG port's VBUS pin.

**For boards without the labeled jumper:** identify the SOD-123 diode in series between the OTG USB-C connector's VBUS pin and the 5V rail (small black two-terminal SMD, ~1.6 × 3.5 mm, band on the cathode), bridge across it with solder.

> [!CRITICAL]
> **After this mod, never plug the "USB" (OTG) port into a PC or any other 5V source.** That port now sources 5V from your board; plugging a PC into it creates a 5V-vs-5V contention that can damage either side. The OTG port is host-only forever — always plugs into your USB hub / downstream devices. The **UART** port remains normal (use for power, flashing, and WebSerial config).

### Compilation & Flash Commands

Use `arduino-cli` from the repo root:

```sh
# 1. Compile (sketch directory is named firmware/, matching the .ino name)
arduino-cli compile --fqbn "esp32:esp32:esp32s3:PSRAM=opi,FlashSize=16M,USBMode=hwcdc,CDCOnBoot=default" firmware

# 2. Flash (board enumerates as a CH340 / CP2102 COM port via the UART USB-C port)
arduino-cli upload -p COM13 --fqbn "esp32:esp32:esp32s3:PSRAM=opi,FlashSize=16M,USBMode=hwcdc,CDCOnBoot=default" firmware
```

Replace `COM13` with your board's UART-bridge COM port. The native USB OTG port is in host mode after the firmware boots, so it doesn't show up as a COM device — find the right port via `arduino-cli board list` and look for the CH340 / CP2102 VID (`1A86` / `10C4`).

> [!IMPORTANT]
> **COM Port Exclusivity:** The flash uploader can't acquire the UART port while the WebSerial UI is connected. Click **Disconnect** in the webconfig app before uploading. (The JSON `{"cmd":"reboot"}` command does an `ESP.restart()` if you just need a soft reset.)

---

## Pin Map

All five output GPIOs are clear of strapping pins (0, 3, 45, 46), the USB OTG D+/D- pair (19, 20), the integrated octal PSRAM bus (33–37), and UART0 (43, 44):

| Signal | GPIO | Notes |
|---|---|---|
| `PIN_X` (H-pattern X DAC) | 4 | LEDC PWM, 12-bit @ 5 kHz |
| `PIN_Y` (H-pattern Y DAC) | 5 | LEDC PWM, 12-bit @ 5 kHz |
| `PIN_SEQ_UP` | 6 | `OUTPUT_OPEN_DRAIN`, idle high |
| `PIN_SEQ_DOWN` | 7 | `OUTPUT_OPEN_DRAIN`, idle high |
| `PIN_HANDBRAKE` | 8 | LEDC PWM, 12-bit @ 5 kHz |
| `PEDAL_TX` (Serial1 TX) | 17 | to wheelbase RX |
| `PEDAL_RX` (Serial1 RX) | 18 | from wheelbase TX |
| Config Serial (UART0) | 43 / 44 | via onboard USB-Serial bridge → UART USB-C port |
| USB OTG D-/D+ | 19 / 20 | hardwired in the WROOM-1 module |

**PWM frequency note:** The Teensy build ran 12-bit PWM at 36 kHz. ESP32-S3's LEDC peripheral can't actually achieve that — its auto-selected clock isn't APB at 80 MHz the way the math expects, and you get the IDF spamming `requested frequency 36000 and duty resolution 12 can not be achieved, div_param=0` on every `analogWrite`. **5 kHz at 12-bit works reliably** across whichever clock source IDF picks, and 5 kHz is still 30× the RC filter cutoff (159 Hz with the wheelbase's 1k+1µF input network) so DAC ripple stays small. The 12-bit gear-calibration values in `Config.gearOut[]` are unchanged.

---

## Module Layout & Loop Dispatch

Five translation units, loop-dispatched in `firmware.ino` in this order:

```
protocolTick() → mappingTick() → pedalsUpdate()
```

USB host work happens on its own FreeRTOS task spawned by EspUsbHost (no `g_usb.Task()` call in `loop()` like the Teensy build had). Callbacks from that task update the device pool slots asynchronously; `mappingTick()` reads them every loop pass.

`pedalsUpdate()` runs **last** in the main loop. If you add long-running work to `mappingTick()` or `protocolTick()`, the 100 Hz pedal stream cadence can slip and the wheelbase drops pedals.

### Translation Units Summary

* **`firmware.ino`** — Orchestrates setup and execution. Initializes the physical pins via `ledcAttach()` (LEDC PWM at 12-bit / 5 kHz), pins 6/7 as `OUTPUT_OPEN_DRAIN` for the sequential lines, spins up the protocol layer and EspUsbHost task, and runs the serial CLI callback dispatcher.

* **`device_pool.{h,cpp}`** — 8-slot pool of `GenericJoystickHID` consumers, registered against EspUsbHost's `onGamepad` / `onKeyboard` / `onDeviceConnected` / `onDeviceDisconnected` callbacks. Claims any non-hub device EspUsbHost surfaces; aggregates buttons, axes (parsed from `EspUsbHostHIDFieldValue` arrays), hat switch (HID Usage 0x10039 → 0..7), and keyboard scancodes (incl. modifiers 0xE0..0xE7 derived from the modifier mask). Lazy `axisCount` / `buttonCount` discovery from observed reports. Address-to-slot lookup via the device's USB address (assigned by the host stack at enumeration).

* **`mapping.{h,cpp}`** — `Config` schema (**1132 bytes**, layout locked with `static_assert`), **Preferences-backed (NVS) blob storage** (replacing the Teensy build's EEPROM), CRC-32/ISO-HDLC integrity check, the `evalAxis` / `evalButton` evaluators (cross-type aware: button ↔ axis ↔ hat ↔ key with deadzones / threshold / invert), and per-output channel updating drivers. The H-pattern shifter has two modes (`hold` / `latch`) selected by `Config.gearMode`. DACs are written via `ledcWrite()`.

* **`protocol.{h,cpp}`** — Line-based JSON command parser and event emitter using ArduinoJson 7. Listens on `Serial` (UART0 → onboard USB-Serial bridge → UART USB-C port). Parses objects starting with `{`, routes other characters to the CLI callback. Emits periodic telemetry events (`live`, `outputs`). The `{"cmd":"reboot"}` handler calls `ESP.restart()` (vs. the Teensy build's SCB AIRCR write).

* **`pedals.{h,cpp}`** — CSL Elite V2 digital UART emulator. Runs on `Serial1` (RX=GPIO 18, TX=GPIO 17). Handshake state machine: **STEP0** (250000 baud, expects `0x0A` → sends `0x1A`) → **STEP1** (`0x05` → `0x15`) → **STEP2** (switches to 115200, 12-byte framed query/response) → **STREAMING** (100 Hz pedal packets). 2-second boot warmup drains stale bytes from prior sessions before engaging.

---

## Channel Model

The firmware manages **14 virtual output channels**, plus a bindable `gear_N` (neutral) for the H-pattern:

* **Gears (8 + Neutral):** `gear_R`, `gear_1` .. `gear_7`, `gear_N`. 9 binding slots total. `gear_N` is a panic-neutral override in `hold` mode and the explicit shift-to-neutral input in `latch` mode.
* **Sequential (2):** `shift_up`, `shift_down`
* **Analog (4):** `handbrake`, `throttle`, `brake`, `clutch`

Each channel supports up to **4 `InputBinding` slots** (configured via webconfig). Button bindings are **OR'd** across slots, axes **MAX'd**. Four input types — **button**, **axis**, **hat** (D-pad direction), **key** (keyboard scancode) — and you can mix them: a button / hat / key can drive an axis channel (outputting 0 or 65535), and an axis can drive a button channel (threshold against the post-scale value).

Physical inputs from different slots sharing the same VID/PID are automatically aggregated before per-channel evaluation.

## H-Pattern Shifter Modes

Selected by `Config.gearMode` (runtime via `set_gear_mode`):

**`hold`** (default — real H-shifter semantics): gear active only while its binding is held.
* 0 active gear bindings → **Neutral**
* 1 active → **Actuates that gear**
* 2+ active → **Neutral** (defensive — staying in neutral is safer than picking a random gear when two switches close simultaneously). A binding on `gear_N` acts as a panic-neutral override.

**`latch`** (keyboard / gamepad friendly): a rising edge on any gear binding switches the current gear; stays there until another rising edge moves it elsewhere — including `gear_N` as the explicit "shift to neutral" input. First edge per tick wins. No defensive multi-press handling.

Between non-neutral gears, the firmware inserts a `NEUTRAL_TRANSIT_MS` (50 ms) window of pure neutral so the wheelbase sees a release before the next gear latches.

## Sequential Pulse Handling

A successful upshift / downshift triggers a brief pull-to-ground pulse on `PIN_SEQ_UP` / `PIN_SEQ_DOWN`. Pins are `OUTPUT_OPEN_DRAIN` (note: ESP32 Arduino's macro has an underscore; the Teensy build's `OUTPUT_OPENDRAIN` is the same idea on a different core) to avoid fighting the wheelbase's 3.3V internal pull-ups. Pulse duration defaults to 50 ms (`pulseMs`).

## Axis Channels

`evalAxis` linearly remaps a raw axis from `[rawMin, rawMax]` to `[0, 65535]`, applies `invert`, then snaps values inside the deadzone bands flat. Threshold and deadzone are compared against the **post-scale** value so `32768` always means 50% regardless of the source device's native bit depth.

## NVS Versioning

The configuration layout is locked by a static assert to exactly **1132 bytes** (`CONFIG_VERSION` 3):

```cpp
static_assert(sizeof(Config) == 1132, "Config layout locked — bump CONFIG_VERSION on change");
```

* If you change any fields in `Config` (`mapping.h`), bump `CONFIG_VERSION`. Version mismatches make the firmware boot with all bindings unmapped (pre-seeding `gearOut[]` from compile-time defaults so the DACs rest at neutral X/Y voltages on a fresh chip).
* The stored blob is guarded by `magic` (`'FANA'` / `0x46414E41`), the version, and a CRC-32/ISO-HDLC. On any mismatch the firmware boots with defaults.
* **Storage backend:** ESP32 Preferences API (NVS partition), namespace `"fanadapter"`, key `"config"`. The Teensy build used `EEPROM.read` / `EEPROM.update` byte-by-byte — same semantics, different backing store.

---

## Serial Diagnostic CLI

Single characters over the UART port:

* **`u`** — USB device pool state + pedal stream status + EHCI endpoint channel count (ESP32-S3 has a hard ~8-channel ceiling shared across all devices' interrupt endpoints; watch this near the limit when running through hubs).
* **`p`** — Force pedal handshake reset.
* **`X`** — Soft reset (`ESP.restart()`).
* **`?` / `h`** — Help.

(The Teensy build had a `d` command for HID descriptor dumps; not implemented in the ESP32 port — EspUsbHost hides per-collection descriptors behind its parsed callbacks, so there's no equivalent to surface.)

---

## JSON API Reference

Send single-line JSON objects over `Serial` (UART). Responses and events arrive as single-line JSON objects.

### Commands

| Request Shape | Response / Effect |
|---|---|
| `{"cmd":"version"}` | `{"fw":"fanadapter","ver":"0.7.0","protocol":5,"platform":"esp32s3","max_bindings_per_channel":4}` |
| `{"cmd":"list_devices"}` | List of pool slots with VID, PID, USB string descriptors (`manufacturer`, `product`), `axis_count`, `button_count`, `has_hat`, `has_keyboard`. |
| `{"cmd":"get_config"}` | Full `Config` JSON (bindings, deadzones, gear DAC table, `gearMode` = `"hold"` \| `"latch"`). Binding `type`: `"none"`, `"button"`, `"axis"`, `"hat"`, `"key"`. |
| `{"cmd":"set_binding","channel":"throttle","slot":0,"binding":{...}}` | Sets a binding slot. Channels: `gear_R`, `gear_1`..`gear_7`, `gear_N`, `shift_up`, `shift_down`, `handbrake`, `throttle`, `brake`, `clutch`. |
| `{"cmd":"set_gear_dac","channel":"gear_3","x":2163,"y":3430}` | Calibrate gear DAC X/Y. `gear_N` sets neutral. |
| `{"cmd":"set_pulse_ms","value":50}` | Sequential pulse duration. |
| `{"cmd":"set_gear_mode","value":"hold"\|"latch"}` | H-pattern mode. |
| `{"cmd":"save_config"}` | Commit current RAM state to NVS. |
| `{"cmd":"reset_config"}` | Reload defaults into RAM (NVS untouched until `save_config`). |
| `{"cmd":"reboot"}` | Soft restart via `ESP.restart()`. |
| `{"cmd":"live_inputs","on":true}` | Stream `live` events on input state changes. |
| `{"cmd":"live_outputs","on":true}` | Stream `outputs` events (~30 Hz). |
| `{"cmd":"test_axis","channel":"throttle","value":12345}` | Force an axis to a value for 500 ms. |
| `{"cmd":"test_pulse","direction":"up"}` | Trigger a sequential pulse. |
| `{"cmd":"test_gear","channel":"gear_3"}` | Force a gear's DAC voltages for 500 ms. |
| `{"cmd":"reset_pedals"}` | Re-arm CSL Elite V2 handshake. |
| `{"cmd":"pedals_status"}` | Pedal state + current pedal values. |

### Asynchronous Events

* **`{"event":"device_attached","slot":0,"vid":...,"pid":...,"manufacturer":"...","product":"...","axis_count":...,"button_count":...,"has_hat":false,"has_keyboard":false}`** — USB device enumerated.
* **`{"event":"device_detached","slot":0}`** — Device disconnected.
* **`{"event":"live","slot":0,"buttons":0,"axes":[...],"hat":-1,"keys":[...]}`** — Streamed inputs (`live_inputs` active). `hat` omitted for devices without a hat, `-1` while released; `keys` omitted for non-keyboards.
* **`{"event":"outputs","gear":"gear_N","shift_up":false,...}`** — Streamed virtual channel outputs.

---

## Calibration

Coordinates input mappings on the adapter (configured via webconfig) and the wheelbase's internal mappings (configured via the Fanatec Control Panel on a PC).

### H-Pattern Calibration

1. In webconfig, map buttons to `gear_R` and `gear_1` through `gear_7`. Click **Save**.
2. Connect the adapter to the PC and the wheelbase. Open **Fanatec Control Panel**.
3. Launch the **Shifter Calibration Wizard**.
4. Follow the prompts (Neutral → Reverse → 1 → 2…). Put your USB shifter in that gear and click confirm.
5. If a gear fails to map, go to the webconfig **Outputs** tab, adjust the X/Y DAC values, and retry.

> For keyboards or gamepad D-pads, switch the shifter to **latch** mode (toggle in webconfig's **H-Pattern Shifter** card, or `set_gear_mode`) so a momentary input holds the selected gear. Optionally bind `gear_N` as an explicit shift-to-neutral.

### Sequential Calibration

1. Map buttons to **Shift Up** and **Shift Down** in webconfig.
2. If shifts double-fire or miss, adjust **Pulse width** (default 50 ms) in **Outputs**.

### Pedals & Handbrake Calibration

1. In webconfig, map your USB pedals to **throttle**, **brake**, **clutch**, **handbrake**.
2. **Capture min/max** for each pedal. Fully actuate each pedal so the UI observes the full raw range.
3. If a pedal sits at 1% when released, raise **Deadzone low** until the processed bar rests flat at 0%. Click **Save**.
4. Open the Fanatec Control Panel (it sees the emulated device as **ClubSport Pedals V3**).
5. Toggle **Manual Calibration**, depress each pedal fully, click **Set Max** / **Set Min**.

---

## Troubleshooting

### `list_devices` returns all 8 slots empty / no device events on attach

Almost always the USB-OTG VBUS jumper hasn't been bridged. Symptoms: firmware boots cleanly, `[USB] host begin() OK` appears, but no `device_attached` event ever fires even with a known-good device plugged into the OTG port (verified working on another host). The chip's USB host stack is fine; downstream devices just have no 5V to power their controllers. See **Hardware setup** at the top of this README.

### Pedals show up as ClubSport V3, but values stay at 0%

* Send `u` over Serial — if it reports `STREAMING_115K`, the UART loop is active. If it's stuck on `HANDSHAKE_*`, the handshake hasn't completed.
* Verify pins **1, 2, and 3** of the pedal breakout are all connected to GND. Floating pins make the handshake fail.
* Click **Re-arm pedals handshake** in the Outputs tab (or send `{"cmd":"reset_pedals"}`).

### Sequential shifts don't register

* Verify GPIO 6 and 7 idle at ~3.3V (open-drain, internal pull-up to 3.3V from the wheelbase side).
* Make sure the pins are configured as `OUTPUT_OPEN_DRAIN`, not push-pull — push-pull can damage the wheelbase lines.

### `requested frequency … duty resolution 12 can not be achieved, div_param=0`

LEDC clock-source ambiguity in arduino-esp32 — the auto-selected clock isn't APB at 80 MHz and the divider underflows. We work around it by capping `PWM_FREQ_HZ` at 5 kHz; if you bump it back up, you'll see these spam messages on every `ledcWrite`. See the comment in `mapping.h`.

### EHCI channel ceiling — running out of host slots through a hub

ESP32-S3 USB host has a hard ~8 channel limit shared across all downstream devices' interrupt endpoints plus hub control pipes. Heavy multi-interface devices (composite gamepads with consumer-control collections, keyboard receivers with HID++ vendor pages) can eat 2–3 channels each. The `u` CLI prints current channel count; watch it as you add devices.

---

## License

The firmware is licensed under the **Apache License 2.0**. Full terms and the project's dual-license details are in [LICENSE.md](../LICENSE.md); the root [README](../README.md#license) covers the commercial-reuse invitation.
