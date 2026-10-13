# fanadapter protocol, configuration, and calibration reference

The STM32 firmware is the source of truth for the line-based JSON protocol used by [webconfig](../webconfig/README.md) and the [SimHub plugin](../simhub-plugin/README.md). This reference describes protocol 6 and configuration schema 3. Build, flashing and board pin assignments are in the [firmware guide](README.md); RJ12 pinouts and electrical details are in the [hardware reference](../schematics/README.md).

## Internal Software Design

### 1. Channel Model
The protocol exposes **15 binding channel keys**: 14 active outputs plus `gear_N` (neutral) for the H-pattern:
* **Gears (8 + Neutral):** `gear_R`, `gear_1` .. `gear_7`, and `gear_N` — 9 gear binding slots in total. `gear_N` is a panic-neutral override in `hold` mode and the explicit shift-to-neutral input in `latch` mode.
* **Sequential (2):** `shift_up`, `shift_down`
* **Analog (4):** `handbrake`, `throttle`, `brake`, `clutch`

Each channel supports up to **4 `InputBinding` slots** (configured via webconfig or SimHub). Button bindings are **OR'd** together across slots, and Axis bindings are **MAX'd** together. Inputs come in four types — **button**, **axis**, **hat** (D-pad direction), and **key** (keyboard scancode) — and you can mix them: a button / hat / key can drive an axis channel (outputting 0 or 65535), and an axis can drive a button channel (by checking if it exceeds a deadzone/threshold).

Physical inputs from different slots sharing the same VID/PID are automatically aggregated under the hood before final evaluation.

### InputBinding fields

Each channel is an array of four bindings. Clearing a slot sets its `type` to `"none"`. A binding has these JSON fields:

| Field | Meaning |
|---|---|
| `vid`, `pid` | USB vendor/product IDs, each 0..65535; used to match source devices. |
| `type` | `"none"`, `"button"`, `"axis"`, `"hat"`, or `"key"`. |
| `index` | Button bit 0..31, axis index 0..7, hat direction 0..7, or HID keyboard scancode. |
| `rawMin`, `rawMax` | Native axis values defining the captured input range. |
| `threshold` | Post-scale axis-as-button cutoff, 0..65535. |
| `deadzoneLow`, `deadzoneHigh` | Post-scale output endpoints, 0..65535. |
| `invert` | Boolean; reverses the scaled axis direction, or negates a button/hat/key match. |

`get_config` returns `version`, `pulseMs`, `gearMode`, `max_bindings_per_channel`, `gear` (channel-keyed binding arrays), `gearOut` (channel-keyed `{x,y}` DAC codes), and binding arrays for `shift_up`, `shift_down`, `handbrake`, `throttle`, `brake`, and `clutch`. Internal magic, padding and CRC fields are not exposed over JSON. Both clients also accept legacy single-binding objects when reading older configuration data.

### 2. H-Pattern Shifter Modes
`update_shifter()` has two modes, selected by `Config.gearMode` (set at runtime via `set_gear_mode`):

**`hold`** (default — real H-shifter semantics): a gear is active only while its binding is held.
* 0 active gear bindings → **Neutral**
* 1 active gear binding → **Actuates that gear**
* 2 or more active → **Neutral** (defensive: when two switches close simultaneously or a flaky binding double-fires, staying in neutral is safer than picking a random gear). A binding on `gear_N` acts as a panic-neutral override.

**`latch`** (keyboard / gamepad friendly): a rising edge on any gear binding *switches* the current gear; it stays there until another rising edge moves it elsewhere — including `gear_N` as the explicit "shift to neutral" input. First edge per tick wins on simultaneous presses; there is no defensive multi-press handling (the user opted into this mode deliberately).

When switching between non-neutral gears, the firmware inserts a `NEUTRAL_TRANSIT_MS` (50 ms) window of pure neutral so the wheelbase's internal state machine registers a release before latching the next gear.

### 3. Sequential Pulse Handling
A successful upshift or downshift triggers a brief pull-to-ground pulse (using GPIO open-drain mode to avoid fighting the wheelbase's 3.3V internal pull-ups). The duration is defined by `pulseMs` (default 50 ms).

### 4. Axis Channels
`scale_axis`, called by `eval_axis`, linearly remaps a raw axis from its captured `[rawMin, rawMax]` to `[0, 65535]`, applies `invert`, then snaps values at or below `deadzoneLow` to 0 and at or above `deadzoneHigh` to 65535. The middle interval is not stretched. When both raw endpoints are zero, the raw value passes through before inversion/deadzones. Axis-as-button bindings compare the result with `>= threshold`; threshold and deadzone values use **post-scale** units, so `32768` means approximately 50% regardless of native bit depth. Raising `deadzoneLow` a couple percent removes at-rest brake jitter.

### 5. Flash Storage and Versioning
Configuration is stored in bank 2, sector 0 at `0x08100000`; application code and firmware updates use bank 1. The configuration layout is locked by a static assert to exactly **1132 bytes** (current schema `CONFIG_VERSION` 3).
```c
_Static_assert(sizeof(Config) == 1132, "Config layout locked — bump CONFIG_VERSION on change");
```
* **IMPORTANT:** If you change any fields in the `Config` struct (`mapping.h`), you must adjust the layout validation and bump `CONFIG_VERSION`. Version mismatches reject the saved configuration on startup, reverting to defaults.
* The stored blob is guarded by a magic tag (`'FANA'` / `0x46414E41`), the version, and a CRC-32/ISO-HDLC. On any magic / version / CRC mismatch the firmware boots with all bindings unmapped but pre-seeds `gearOut[]` from compile-time defaults, so the H-pattern DACs rest at the neutral X/Y voltages (not 0 V) on a fresh chip.

---

## JSON API Reference

Send single-line JSON objects over USB CDC or the USART1 console. Responses and events arrive as single-line JSON objects. Commands reply in FIFO order with a payload, `{"ok":true}`, or `{"err":"reason"}`; `stream_axes` sends no reply. Input events are emitted on change, up to ~30 Hz; output events stream at ~30 Hz while enabled. Non-JSON lines are console logs.

### Commands

| Request Shape | Response / Effect |
|---|---|
| `{"cmd":"version"}` | `{"fw":"fanadapter-stm32","ver":"0.6.2","protocol":6,"max_bindings_per_channel":4}` |
| `{"cmd":"list_devices"}` | Returns `{"devices":[...]}` for all eight pool slots, including disconnected slots. Each has `slot`, `connected`, VID/PID, `manufacturer` / `product` strings, descriptor-derived `axis_count`, `button_count`, `has_hat` and `has_keyboard`. Filter by `connected` to count attached interfaces. |
| `{"cmd":"get_config"}` | Returns the full `Config` JSON object (bindings, deadzones, gear DAC configurations, and `gearMode` = `"hold"` or `"latch"`). Each binding's `type` is one of `"none"`, `"button"`, `"axis"`, `"hat"`, `"key"`. For `"hat"`, `index` is the strict direction (`0`=N, `1`=NE, `2`=E, `3`=SE, `4`=S, `5`=SW, `6`=W, `7`=NW); for `"key"`, `index` is the HID Keyboard/Keypad scancode. |
| `{"cmd":"set_binding","channel":"throttle","slot":0,"binding":{...}}` | Sets a specific binding slot. Channel keys: `gear_R`, `gear_1`..`gear_7`, `gear_N`, `shift_up`, `shift_down`, `handbrake`, `throttle`, `brake`, `clutch`. Binding matches the `InputBinding` shape, including the `"hat"` and `"key"` types. |
| `{"cmd":"set_gear_dac","channel":"gear_3","x":2163,"y":3430}` | Calibrates the X/Y 12-bit DAC targets for a specific gear. Use 0..4095; `gear_N` sets both neutral X and Y targets. |
| `{"cmd":"set_pulse_ms","value":50}` | Configures the sequential gear shift pull pulse duration. |
| `{"cmd":"set_gear_mode","value":"hold"\|"latch"}` | Selects the H-pattern shifter mode. `hold` = gear active only while its binding is held (real shifter); `latch` = a rising edge switches gear until another gear / Neutral fires (keyboard / gamepad friendly). |
| `{"cmd":"save_config"}` | Commits active RAM configuration to flash bank 2. |
| `{"cmd":"reset_config"}` | Reloads compiled default values into RAM (does not touch flash until `save_config`). |
| `{"cmd":"reboot"}` | Acks, then soft-reboots the adapter. |
| `{"cmd":"live_inputs","on":true}` | Enables on-change `live` input events (axes, buttons, hat or keys) and device attach/detach events. Does not request a full initial snapshot; use `list_devices` for the current pool. |
| `{"cmd":"live_outputs","on":true}` | Enables continuous streaming of `outputs` events summarizing active virtual channels (~30 Hz). |
| `{"cmd":"test_axis","channel":"throttle","value":12345}` | Forces a specific axis channel to a value for 500 ms, then reverts. |
| `{"cmd":"test_pulse","direction":"up"}` | Triggers a sequential up/down pulse using the configured `pulseMs`. |
| `{"cmd":"test_gear","channel":"gear_3"}` | Forces the H-pattern DAC lines to a specific gear's voltage for 500 ms. |
| `{"cmd":"reset_pedals"}` | Forces the CSL Elite V2 emulation state machine to re-arm the handshake. |

### Direct output, updates, and diagnostics

| Request | Response / effect |
|---|---|
| `{"cmd":"set_gear","gear":"gear_3"}` | Direct output: force the H-pattern gear, overriding the USB-device mapping. Sticky until `release_outputs`. Values: `gear_R`, `gear_1`..`gear_7`, `gear_N`. Unlike `test_gear`, this command uses the field **`gear`**. |
| `{"cmd":"set_outputs","throttle":40000,"brake":0,...}` | Direct output: set any subset of the four pedal axes (0..65535). Acked. Absent channels keep their current override. |
| `{"cmd":"stream_axes","throttle":40000,...}` | **No reply.** The pedal hot path (protocol ≥ 6): identical effect to `set_outputs`' axis half, but answers nothing, so a client streams it at rate without a round-trip and it never touches FIFO reply matching. |
| `{"cmd":"pulse_shift","direction":"up"\|"down"}` | Direct output: one sequential shift pulse. |
| `{"cmd":"release_outputs"}` | Drops all direct-output overrides; the adapter's own USB-device mapping takes the wheelbase back. |
| `{"cmd":"pedals_status"}` | `{"state":"STREAMING_115K",...}` — pedal-port link state + last-sent axis values. |
| `{"cmd":"dfu"}` | Acks, then reboots into the ROM bootloader for a hands-free reflash ([build guide](README.md#4-build--flash)). The CDC port disappears and a DFU device takes its place. |
| `{"cmd":"usb_status"}` | USB-host diagnostics: per claimed slot `{mounted,busy,reports,age_ms,idle_rearms,da}`, plus a raw dwc2 host-channel dump and request-queue counters. See the [input-freeze troubleshooting guide](README.md#7-troubleshooting). |
| `{"cmd":"usb_kick"}` | Aborts + re-arms every claimed HID pipe; returns the aborted-slot bitmask. Measured NOT to revive a wedged pipe — a diagnostic, not a fix. |
| `{"cmd":"usb_stall","ms":2000}` | Skips `tuh_task()` for `ms` (default 1000, clamped to 1..10000), while the rest of the loop and interrupts continue. Returns `{"ok":true,"ms":2000}`. Diagnostic reproducer; a 2 s gap can leave the tested pedal unresponsive until replugged. |

Direct-output overrides are **sticky with no firmware-side timeout**. The PC command wins over the adapter's USB-device mapping for each channel it touches until `release_outputs`; clients must release when stopping or disconnecting. `stream_axes` is the only no-reply command: never send a replying command through a fire-and-forget transport, or its reply will desynchronize FIFO matching. Older protocol versions need the acknowledged `set_outputs` path.

### Asynchronous Events

`device_attached`, `device_detached` and `live` require `live_inputs` to be enabled. A client connecting after enumeration must call `list_devices`; enabling events does not replay earlier attachments. `outputs` requires `live_outputs`. The `live`/`outputs` streams may drop whole frames when console buffers are congested.

* **`{"event":"device_attached","slot":0,"vid":1133,"pid":49771,"manufacturer":"Logitech","product":"G29 Driving Force","axis_count":...,"button_count":...,"has_hat":false,"has_keyboard":false}`** — Sent when a USB device is enumerated. `manufacturer` / `product` are the device's USB string descriptors (sanitised to printable ASCII; empty string when the device reports none).
* **`{"event":"device_detached","slot":0}`** — Sent when a device is disconnected.
* **`{"event":"live","slot":0,"buttons":0,"axes":[32768,...],"hat":-1,"keys":[...]}`** — Streamed input values (when `live_inputs` is active). `hat` is omitted for devices without a hat, `-1` while released, `0..7` for an active direction; `keys` is omitted for non-keyboards, otherwise a 6-element zero-padded array of pressed HID scancodes.
* **`{"event":"outputs","gear":"gear_N","shift_up":false,...}`** — Streamed virtual channel outputs (when `live_outputs` is active).

---

## Calibration

Calibration coordinates the adapter's input mappings (configured on the adapter via webconfig or SimHub) and the wheelbase's internal mappings (configured via the Fanatec Control Panel on a PC).

### H-Pattern Calibration
1. In webconfig, map buttons to `gear_R` and `gear_1` through `gear_7`. Click **Save to adapter**.
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
2. Use **Listen** (or **Remap** for an existing binding), hold still through the baseline, then fully press and release the pedal. For an existing axis binding, its expanded editor also offers **Recalibrate from live (4 s)** to resample `rawMin`/`rawMax`; this resampling preserves the current Invert setting.
3. If a pedal sits at 1% when released, raise the lower handle of **Deadzone** until the processed preview bar rests flat at 0%. Click **Save to adapter**.
4. Open the Fanatec Control Panel (which sees the emulated device as **ClubSport Pedals V3**).
5. Toggle **Manual Calibration** in the Fanatec UI, depress each pedal fully, and click **Set Max** / **Set Min** to finalize the wheelbase range.

---

## License

Apache License 2.0. See [LICENSE.md](../LICENSE.md).
