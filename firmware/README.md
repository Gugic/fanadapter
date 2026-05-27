# fanadapter Firmware Reference

The `firmware/` directory contains the Arduino sketch (Teensyduino) that runs on the Teensy 4.1. It handles USB host HID polling, custom binding evaluation, EEPROM state storage, a serial JSON API, and emulates Fanatec gear over RJ12 outputs.

---

## Toolchain & Commands

### Prerequisites
* **Board:** Teensy 4.1
* **Core:** Teensyduino (installed via Arduino IDE or `arduino-cli` board manager)
* **Required Libraries:** **ArduinoJson** ≥ 7.0 (`arduino-cli lib install ArduinoJson`)
* **USB host stack:** `USBHost_t36` ships with Teensyduino. Reliable **keyboard** input requires the **master** branch of `USBHost_t36` — the bundled `0.2` release does not deliver keypresses from some receivers (e.g. a Logitech Unifying Receiver). Logitech HID++ wireless keyboards remain unsupported (proprietary protocol); use a wired or standard-HID keyboard.

### Compilation & Flash Commands
Use `arduino-cli` to compile and upload from this directory:

```sh
# 1. Compile the sketch
arduino-cli compile --fqbn teensy:avr:teensy41 .

# 2. Flash to the Teensy (replace COM11 with your actual serial port)
arduino-cli upload -p COM11 --fqbn teensy:avr:teensy41 .
```

> [!IMPORTANT]
> **COM Port Exclusivity:** The flash uploader cannot acquire the serial port while the WebSerial UI is connected. You must click **Disconnect** in the webconfig app before uploading, or you will get an "Access is denied" error.
> 
> *Tip:* If you just need to reboot the Teensy, you can send the JSON command `{"cmd":"reboot"}` over WebSerial to perform a soft reset (SCB AIRCR reset) without re-flashing.

---

## Module Layout & Loop Dispatch

The firmware is broken down into five translation units. They are dispatched sequentially inside `firmware.ino`'s main `loop()`. **The order of dispatch is critical for maintaining the 100 Hz pedal UART streaming cadence:**

```
g_usb.Task() → pollUsbDriverStatus() → protocolTick() → mappingTick() → pedalsUpdate()
```

* `pedalsUpdate()` is executed last. If you add heavy blocking tasks to other ticks, you will introduce latency to the pedal loop, causing the wheelbase to drop packets and abort the handshake.

### Developer Notes (Things to Know Before Editing)

* **`CONFIG_VERSION` bumps wipe user EEPROMs on next flash:** The firmware boots with empty mappings when a version mismatch is detected (default H-pattern neutral DAC and pulse-width configurations are preserved). Keep this in mind when staging schema changes.
* **Pedal handshake desync survives soft reboots:** A Teensy reboot or standard code re-flash does not reset the wheelbase's UART hardware transceiver state. While a 2-second silent warmup delay helps clear line states, you may occasionally need to use the **Re-arm pedals handshake** button, send `{"cmd":"reset_pedals"}` over JSON, or physically cycle the USB connection to restore synchronization.
* **`0x7B`-framed bytes during STREAMING are normal:** The Fanatec wheelbase will occasionally emit remnants of Step 2 queries even after the digital handshake has successfully finalized. **Do not interpret these bytes as a request to restart the handshake.** The byte dispatcher inside `STATE_STREAMING` is designed to safely ignore them.
* **Multiple devices can share a single channel:** Conflict clearance features across virtual outputs were intentionally omitted. Multiple physical controllers can be mapped to trigger the same output channel (e.g., binding a shifter switch and a paddle button to the same output). Defensive neutral rules handle physical button overlaps cleanly.
* **Timing Cadence (Loop Order Warning):** The UART pedal protocol requires low latency to stay in sync. Because `pedalsUpdate()` is polled at the end of `loop()`, adding long-running, blocking operations to `mappingTick()` or `protocolTick()` will cause the 100 Hz cadence to slip, resulting in dropped packets and lost connections.

### Translation Units Summary

* **`firmware.ino`**  
  Orchestrates setup and execution. Initializes the physical pins, configures the 12-bit PWM timer (36 kHz frequency), sets up the `USBHost_t36` host parser instances, and runs the serial CLI callback dispatcher.
  
* **`device_pool.{h,cpp}`**  
  Maintains an 8-slot array of `GenericJoystickHID` consumers. It claims any connected USB joystick, gamepad, multi-axis controller, or **keyboard** HID collection (first-come, first-served) regardless of its VID/PID. It lazily discovers axis and button counts from incoming reports; `hasHat()` / `hasKeyboard()` flip to true on the first matching event. A gamepad **D-pad / Hat Switch** is exposed as a first-class direction value (`0..7` per the HID Usage Tables — N, NE, E, SE, S, SW, W, NW; `HAT_RELEASED = 0xFF` when centred), and `INPUT_HAT` bindings select a specific direction strictly (diagonals don't fire cardinal bindings — bind multiple slots on a channel for lenient matching). **Keyboards** keep up to `MAX_KEYS_PRESSED` (6) simultaneously-pressed scancodes; `INPUT_KEY` bindings carry the HID Keyboard/Keypad usage code in `index` (`0x04` = A, … `0xE0..0xE7` = modifiers).
  
* **`mapping.{h,cpp}`**  
  Contains the `Config` schema (**1132 bytes**, layout locked with `static_assert`), EEPROM read/write routines (secured by a CRC-32/ISO-HDLC checksum), the `evalAxis` / `evalButton` evaluators (cross-type aware: button ↔ axis ↔ hat ↔ key, with deadzones / threshold / invert), and per-output channel updating drivers. The H-pattern shifter has two modes (`hold` / `latch`) selected by `Config.gearMode`.
  
* **`protocol.{h,cpp}`**  
  Line-based JSON command parser and event emitter using ArduinoJson 7. Listens on the Teensy CDC Serial port. Parses objects starting with `{`, and routes all other characters to the diagnostic CLI callback. Emits periodic telemetry events (`live`, `outputs`).
  
* **`pedals.{h,cpp}`**  
  CSL Elite V2 digital UART emulating driver. Runs on `Serial3` (RX=Pin 15, TX=Pin 14). Operates as a handshake and query state machine.
  
* **`name.c`**  
  Custom USB descriptor overrides (`usb_names.h`). Defines the board's USB Manufacturer Name as `"fanadapter"` and Product Name as `"Fanadapter v0.3.0"` to distinguish it from a generic serial interface.

---

## Internal Software Design

### 1. Channel Model
The firmware manages **14 virtual output channels**, plus a bindable `gear_N` (neutral) for the H-pattern:
* **Gears (8 + Neutral):** `gear_R`, `gear_1` .. `gear_7`, and `gear_N` — 9 gear binding slots in total. `gear_N` is a panic-neutral override in `hold` mode and the explicit shift-to-neutral input in `latch` mode.
* **Sequential (2):** `shift_up`, `shift_down`
* **Analog (4):** `handbrake`, `throttle`, `brake`, `clutch`

Each channel supports up to **4 `InputBinding` slots** (configured via webconfig). Button bindings are **OR'd** together across slots, and Axis bindings are **MAX'd** together. Inputs come in four types — **button**, **axis**, **hat** (D-pad direction), and **key** (keyboard scancode) — and you can mix them: a button / hat / key can drive an axis channel (outputting 0 or 65535), and an axis can drive a button channel (by checking if it exceeds a deadzone/threshold).

Physical inputs from different slots sharing the same VID/PID are automatically aggregated under the hood before final evaluation.

### 2. H-Pattern Shifter Modes
`updateShifter()` has two modes, selected by `Config.gearMode` (set at runtime via `set_gear_mode`):

**`hold`** (default — real H-shifter semantics): a gear is active only while its binding is held.
* 0 active gear bindings → **Neutral**
* 1 active gear binding → **Actuates that gear**
* 2 or more active → **Neutral** (defensive: when two switches close simultaneously or a flaky binding double-fires, staying in neutral is safer than picking a random gear). A binding on `gear_N` acts as a panic-neutral override.

**`latch`** (keyboard / gamepad friendly): a rising edge on any gear binding *switches* the current gear; it stays there until another rising edge moves it elsewhere — including `gear_N` as the explicit "shift to neutral" input. First edge per tick wins on simultaneous presses; there is no defensive multi-press handling (the user opted into this mode deliberately).

When switching between non-neutral gears, the firmware inserts a `NEUTRAL_TRANSIT_MS` (50 ms) window of pure neutral so the wheelbase's internal state machine registers a release before latching the next gear.

### 3. Sequential Pulse Handling
A successful upshift or downshift triggers a brief pull-to-ground pulse (using `OUTPUT_OPENDRAIN` mode to avoid fighting the wheelbase's 3.3V internal pull-ups). The duration is defined by `pulseMs` (default 50 ms).

### 4. EEPROM Versioning
The configuration layout in EEPROM is locked by a static assert to exactly **1132 bytes** (current schema `CONFIG_VERSION` 3).
```cpp
static_assert(sizeof(Config) == 1132, "Config layout locked — bump CONFIG_VERSION on change");
```
* **IMPORTANT:** If you change any fields in the `Config` struct (`mapping.h`), you must adjust the layout validation and bump `CONFIG_VERSION`. Version mismatches will wipe user EEPROMs on startup, reverting to defaults.

---

## Serial Diagnostic CLI

When connecting with a terminal program, sending single characters will execute debugging actions:

* **`u`** — Prints the USB driver host status, connected device pool, and current pedal axis stream outputs.
* **`d`** — Dumps HID parser diagnostics (report descriptors) for attached devices, on demand.
* **`p`** — Forces a pedal handshake reset (re-enters Step 0 state).
* **`X`** — Software reset (reboots Teensy).
* **`?` or `h`** — Prints help menu.

---

## JSON API Reference

Send single-line JSON objects over the USB CDC serial port. Responses and events arrive formatted as single-line JSON objects.

### Commands

| Request Shape | Response / Effect |
|---|---|
| `{"cmd":"version"}` | `{"fw":"fanadapter","ver":"0.6.0","protocol":5,"max_bindings_per_channel":4}` |
| `{"cmd":"list_devices"}` | Returns a list of active USB host devices, showing VID, PID, and discovered button/axis counts. `has_hat` / `has_keyboard` flip to `true` once the device reports any Hat Switch or Keyboard usage. |
| `{"cmd":"get_config"}` | Returns the full `Config` JSON object (bindings, deadzones, gear DAC configurations, and `gearMode` = `"hold"` or `"latch"`). Each binding's `type` is one of `"none"`, `"button"`, `"axis"`, `"hat"`, `"key"`. For `"hat"`, `index` is the strict direction (`0`=N, `1`=NE, `2`=E, `3`=SE, `4`=S, `5`=SW, `6`=W, `7`=NW); for `"key"`, `index` is the HID Keyboard/Keypad scancode. |
| `{"cmd":"set_binding","channel":"throttle","slot":0,"binding":{...}}` | Sets a specific binding slot. Channel keys: `gear_R`, `gear_1`..`gear_7`, `gear_N`, `shift_up`, `shift_down`, `handbrake`, `throttle`, `brake`, `clutch`. Binding matches the `InputBinding` shape, including the `"hat"` and `"key"` types. |
| `{"cmd":"set_gear_dac","channel":"gear_3","x":2163,"y":3430}` | Calibrates the X/Y PWM DAC targets for a specific gear. `gear_N` sets the Neutral column target. |
| `{"cmd":"set_pulse_ms","value":50}` | Configures the sequential gear shift pull pulse duration. |
| `{"cmd":"set_gear_mode","value":"hold"\|"latch"}` | Selects the H-pattern shifter mode. `hold` = gear active only while its binding is held (real shifter); `latch` = a rising edge switches gear until another gear / Neutral fires (keyboard / gamepad friendly). |
| `{"cmd":"save_config"}` | Commits active RAM configuration to EEPROM. |
| `{"cmd":"reset_config"}` | Reloads compiled default values into RAM (does not touch EEPROM until `save_config`). |
| `{"cmd":"reboot"}` | Instantly soft-reboots the Teensy 4.1. |
| `{"cmd":"live_inputs","on":true}` | Enables continuous streaming of `live` input events when axes or buttons change state. |
| `{"cmd":"live_outputs","on":true}` | Enables continuous streaming of `outputs` events summarizing active virtual channels (~30 Hz). |
| `{"cmd":"test_axis","channel":"throttle","value":12345}` | Forces a specific axis channel to a value for 500 ms, then reverts. |
| `{"cmd":"test_pulse","direction":"up"}` | Triggers a 50 ms sequential up/down pulse. |
| `{"cmd":"test_gear","channel":"gear_3"}` | Forces the H-pattern DAC lines to a specific gear's voltage for 500 ms. |
| `{"cmd":"reset_pedals"}` | Forces the CSL Elite V2 emulation state machine to re-arm the handshake. |

### Asynchronous Events

* **`{"event":"device_attached","slot":0,"vid":1133,"pid":49771,"axis_count":...,"button_count":...,"has_hat":false,"has_keyboard":false}`** — Sent when a USB device is enumerated.
* **`{"event":"device_detached","slot":0}`** — Sent when a device is disconnected.
* **`{"event":"live","slot":0,"buttons":0,"axes":[32768,...],"hat":-1,"keys":[...]}`** — Streamed input values (when `live_inputs` is active). `hat` is omitted for devices without a hat, `-1` while released, `0..7` for an active direction; `keys` is omitted for non-keyboards, otherwise a 6-element zero-padded array of pressed HID scancodes.
* **`{"event":"outputs","gear":"gear_N","shift_up":false,...}`** — Streamed virtual channel outputs (when `live_outputs` is active).

---

## Calibration

Calibration coordinates the adapter's input mappings (configured on the Teensy via webconfig) and the wheelbase's internal mappings (configured via the Fanatec Control Panel on a PC).

### H-Pattern Calibration
1. In webconfig, map buttons to `gear_R` and `gear_1` through `gear_7`. Click **Save to EEPROM**.
2. Connect the adapter to the PC and the wheelbase. Open **Fanatec Control Panel**.
3. Launch the **Shifter Calibration Wizard**.
4. Follow the prompts on the screen (Neutral → Reverse → 1 → 2...): put your USB shifter in that gear, and click confirm. The wheelbase registers the custom DAC voltage mapping.
5. If a gear fails to map, go to the webconfig **Outputs** tab, adjust the X/Y DAC values in the table, and retry the wizard.

> [!NOTE]
> For keyboards or gamepad D-pads, switch the shifter to **latch** mode (the toggle in webconfig's **H-Pattern Shifter** card, or `set_gear_mode`) so a momentary input holds the selected gear, and optionally bind `gear_N` as an explicit shift-to-neutral input. The Fanatec wizard still maps the same gear DAC voltages regardless of mode.

### Sequential Calibration
1. Map buttons to **Shift Up** and **Shift Down** in webconfig.
2. If your shifts are double-firing or missing, navigate to **Outputs** and scale the **Pulse width** (default 50 ms) up or down.

### Pedals & Handbrake Calibration
1. In webconfig, map your USB pedals to **throttle**, **brake**, **clutch**, and **handbrake**.
2. Select **Capture min/max** for each pedal. Fully actuate each pedal so the UI observes the maximum and minimum raw values.
3. If a pedal sits at 1% when released, increment **Deadzone low** until the processed preview bar rests flat at 0%. Click **Save to EEPROM**.
4. Open the Fanatec Control Panel (which sees the emulated device as **ClubSport Pedals V3**).
5. Toggle **Manual Calibration** in the Fanatec UI, depress each pedal fully, and click **Set Max** / **Set Min** to finalize the wheelbase range.

---

## Troubleshooting

### Pedals show up as ClubSport V3, but values stay at 0%
* Ensure `pedals.cpp` shows active streaming. Run the `u` debug command in a serial monitor. If it reports `STREAMING_115K`, the UART loop is active.
* Verify that **Pins 1, 2, and 3** on the pedal breakout are all connected to GND. If any of these are floating, the digital handshake will fail.
* Click the **Re-arm pedals handshake** button in the Outputs tab of webconfig (or send `{"cmd":"reset_pedals"}`).

### Sequential shifts don't register
* Verify the Teensy pins 6 and 7 are configured correctly and that Shifter 2 Pin 4 idles at ~3.3V.
* Make sure you aren't configuring the pins in push-pull mode — they must remain `OUTPUT_OPENDRAIN` to prevent damaging the wheelbase lines.

---

## License

This software firmware is licensed under the Apache License 2.0. See the root [README.md](../README.md#license) for full terms, commercial manufacturing guidelines, and maintainer info.
