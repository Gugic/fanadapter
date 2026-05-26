# fanadapter Firmware Reference

The `firmware/` directory contains the Arduino sketch (Teensyduino) that runs on the Teensy 4.1. It handles USB host HID polling, custom binding evaluation, EEPROM state storage, a serial JSON API, and emulates Fanatec gear over RJ12 outputs.

---

## Toolchain & Commands

### Prerequisites
* **Board:** Teensy 4.1
* **Core:** Teensyduino (installed via Arduino IDE or `arduino-cli` board manager)
* **Required Libraries:** **ArduinoJson** ≥ 7.0 (`arduino-cli lib install ArduinoJson`)

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
  Maintains an 8-slot array of `GenericJoystickHID` consumers. It claims any connected USB joystick, gamepad, or multi-axis controller (first-come, first-served) regardless of its VID/PID. It lazily discovers axis and button counts from incoming reports.
  
* **`mapping.{h,cpp}`**  
  Contains the `Config` schema, EEPROM read/write routines (secured by a CRC-32/ISO-HDLC checksum), axis/button evaluation algorithms, and per-output channel updating drivers.
  
* **`protocol.{h,cpp}`**  
  Line-based JSON command parser and event emitter using ArduinoJson 7. Listens on the Teensy CDC Serial port. Parses objects starting with `{`, and routes all other characters to the diagnostic CLI callback. Emits periodic telemetry events (`live`, `outputs`).
  
* **`pedals.{h,cpp}`**  
  CSL Elite V2 digital UART emulating driver. Runs on `Serial3` (RX=Pin 15, TX=Pin 14). Operates as a handshake and query state machine.
  
* **`name.c`**  
  Custom USB descriptor overrides (`usb_names.h`). Defines the board's USB Manufacturer Name as `"fanadapter"` and Product Name as `"Fanadapter v0.3.0"` to distinguish it from a generic serial interface.

---

## Internal Software Design

### 1. Channel Model
The firmware manages **14 virtual output channels**:
* **Gears (8):** `gear_R`, `gear_1` .. `gear_7`
* **Sequential (2):** `shift_up`, `shift_down`
* **Analog (4):** `handbrake`, `throttle`, `brake`, `clutch`

Each channel supports up to **4 `InputBinding` slots** (configured via webconfig). Button bindings are **OR'd** together across slots, and Axis bindings are **MAX'd** together. You can mix types: a button can drive an axis (outputting 0 or 65535), and an axis can drive a button (by checking if it exceeds a deadzone/threshold).

Physical inputs from different slots sharing the same VID/PID are automatically aggregated under the hood before final evaluation.

### 2. H-Pattern Defensive Neutral
`updateShifter()` tracks active gear bindings. To prevent conflicts and secure gear shifts on noisy sensors:
* 0 active gear buttons → **Neutral**
* 1 active gear button → **Actuates that gear**
* 2 or more active gear buttons → **Neutral**

When switching between gears, the firmware inserts a `NEUTRAL_TRANSIT_MS` (50 ms) window of pure neutral to allow the wheelbase's internal state machine to register a release before latching the next gear.

### 3. Sequential Pulse Handling
A successful upshift or downshift triggers a brief pull-to-ground pulse (using `OUTPUT_OPENDRAIN` mode to avoid fighting the wheelbase's 3.3V internal pull-ups). The duration is defined by `pulseMs` (default 50 ms).

### 4. EEPROM Versioning
The configuration layout in EEPROM is locked by a static assert to exactly **1060 bytes**.
```cpp
static_assert(sizeof(Config) == 1060, "Config struct size changed!");
```
* **IMPORTANT:** If you change any fields in the `Config` struct (`mapping.h`), you must adjust the layout validation and bump `CONFIG_VERSION`. Version mismatches will wipe user EEPROMs on startup, reverting to defaults.

---

## Serial Diagnostic CLI

When connecting with a terminal program, sending single characters will execute debugging actions:

* **`u`** — Prints the USB driver host status, connected device pool, and current pedal axis stream outputs.
* **`p`** — Forces a pedal handshake reset (re-enters Step 0 state).
* **`X`** — Software reset (reboots Teensy).
* **`?` or `h`** — Prints help menu.

---

## JSON API Reference

Send single-line JSON objects over the USB CDC serial port. Responses and events arrive formatted as single-line JSON objects.

### Commands

| Request Shape | Response / Effect |
|---|---|
| `{"cmd":"version"}` | `{"fw":"fanadapter","ver":"0.3.0","protocol":2,"max_bindings_per_channel":4}` |
| `{"cmd":"list_devices"}` | Returns a list of active USB host devices, showing VID, PID, and discovered button/axis counts. |
| `{"cmd":"get_config"}` | Returns the full `Config` JSON object (bindings, deadzones, gear DAC configurations). |
| `{"cmd":"set_binding","channel":"throttle","slot":0,"binding":{...}}` | Sets a specific binding slot. Channel keys match the virtual channel names. Binding matches the `InputBinding` shape. |
| `{"cmd":"set_gear_dac","channel":"gear_3","x":2163,"y":3430}` | Calibrates the X/Y PWM DAC targets for a specific gear. `gear_N` sets the Neutral column target. |
| `{"cmd":"set_pulse_ms","value":50}` | Configures the sequential gear shift pull pulse duration. |
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

* **`{"event":"device_attached","slot":0,"vid":1133,"pid":49771,...}`** — Sent when a USB device is enumerated.
* **`{"event":"device_detached","slot":0}`** — Sent when a device is disconnected.
* **`{"event":"live","slot":0,"buttons":0,"axes":[32768,...]}`** — Streamed input values (when `live_inputs` is active).
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
