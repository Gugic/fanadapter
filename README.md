# USB HID → Fanatec Wheelbase Adapter

An open-source adapter that lets arbitrary USB HID sim racing peripherals (H-pattern shifter, sequential shifter, handbrake) connect to a Fanatec wheelbase as if they were native Fanatec gear. Particularly useful on consoles (PS4/PS5/Xbox) where third-party USB peripherals can't be plugged in directly.

This is a fixed-function adapter targeting one H-pattern shifter, one sequential shifter, one handbrake, and one set of USB pedals. The microcontroller (Teensy 4.1) reads USB HID input from each device and drives the wheelbase's RJ12 ports with the appropriate analog voltages, digital signals, and (for the pedal port) a UART-based protocol.

Shifter, pedal, and handbrake output stages are all verified working on a Fanatec ClubSport DD+. The pedal port speaks the CSL Elite V2 UART protocol (see [Pedal port section](#pedal-port-uart-csl-elite-v2-protocol)) and is the canonical handbrake source on current Fanatec firmware — the dedicated handbrake RJ12 port becomes optional/unused when the pedal port is connected.

> **Status:** All four signal paths (H-pattern, sequential, handbrake, pedals) verified end-to-end on a Fanatec ClubSport DD+. Calibration wizard accepts the adapter; all H-pattern gears, sequential shifts, throttle/brake/clutch, and handbrake register correctly. Pedals show up to the Fanatec app as ClubSport Pedals V3. PCB design, 3D-printed enclosure, and WebSerial configuration UI are future work.

---

## Table of Contents

- [What this is](#what-this-is)
- [Compatibility](#compatibility)
- [Architecture](#architecture)
- [Fanatec port protocols](#fanatec-port-protocols)
  - [Shifter 1 port (H-pattern, analog)](#shifter-1-port-h-pattern-analog)
  - [Shifter 2 port (sequential, digital)](#shifter-2-port-sequential-digital)
  - [Handbrake port (analog)](#handbrake-port-analog)
  - [Pedal port (UART, CSL Elite V2 protocol)](#pedal-port-uart-csl-elite-v2-protocol)
- [Bill of materials](#bill-of-materials)
- [Assembly](#assembly)
- [Wiring reference](#wiring-reference)
- [Firmware](#firmware)
- [Calibration](#calibration)
- [Troubleshooting](#troubleshooting)
- [Roadmap](#roadmap)
- [References and acknowledgments](#references-and-acknowledgments)
- [License](#license)

---

## What this is

A small box sitting between your USB peripherals and your Fanatec wheelbase:

```
USB H-pattern shifter ─┐
USB sequential shifter ┼──► [USB hub] ──► [Teensy 4.1] ──► RJ12 ──► Fanatec wheelbase
USB handbrake ─────────┘                                  RJ12        (3 ports used)
                                                          RJ12
```

The Teensy enumerates the four USB HID devices via its native USB host port, decodes their reports, and drives three RJ12 cables that plug into the wheelbase's **Shifter 1**, **Shifter 2**, and **Pedal** ports. (The dedicated **Handbrake** port is left unplugged on the wheelbase side — the handbrake value is routed through the Pedal port's protocol-level Handbrake field instead. See [Handbrake port section](#handbrake-port-analog) for why.) From the wheelbase's perspective, the adapter looks like genuine Fanatec hardware.

## Compatibility

**Verified:** Fanatec ClubSport DD+ (15 Nm)

**Expected to work** (same RJ12 protocols across generations, but untested here):
- CSL Elite 1.1 / V2
- Podium DD1 / DD2
- ClubSport DD
- CSL DD
- CSW V2.5

**Likely incompatible:**
- CSR Elite and older bases that predate the current Shifter 1/2 RJ12 layout
- Bases without dedicated handbrake ports (Pre-DD generation may share the handbrake with the pedal port)

If you test on another base, please open a PR to update this list.

### USB hub compatibility (important)

The Teensy 4.1 has a single USB host port. To attach multiple peripherals (H-pattern shifter + combo devices + pedals = 4 USB devices in our build) you need a **powered USB 2.0 hub**. Not all hubs work — the `USBHost_t36` library has known compatibility issues with some controllers, particularly under hot-plug load.

**Known incompatible:**

- **Sabrent 4-port USB 2.0 hub** (VID `0x5E3`, PID `0x610`, Genesys Logic GL850G class) — works fine on a PC but causes partial enumeration and mass-detach cascades on Teensy USB host. Documented independently on the PJRC forum: a community tester ran systematic experiments with the exact same hub and concluded "**HOT plug in / out generate potential random behaviour even on other ports**; **seems Teensy not able to manage port 4**." We hit identical symptoms. **Do not use this hub.**

**Known working:**

- _TODO: insert the verified-working hub model once selected._

If you build a unit and find another working/non-working hub, please open a PR with the VID/PID and brief notes — this list will grow over time.

**Recovery if devices stop responding:** unplug the hub from the Teensy, plug it into a PC for ~5 seconds (which renegotiates the hub's internal state with a more robust host stack), then plug back into the Teensy. Documented end-user workaround for transient hub-side wedges; no firmware-side fix is possible because the hub maintains its own state across our reboots.

## Architecture

The Teensy 4.1 has four relevant capabilities used here:

1. **Native hardware USB host** (separate from its programming USB port) for reading the connected HID devices through a powered hub
2. **PWM outputs** to generate analog voltages via simple RC filters — used for both shifter analog levels and the handbrake signal
3. **Open-drain GPIO** for the sequential shifter's switch-to-ground inputs
4. **Hardware UART peripheral** (Serial3 on pins 14/15) for the pedal port, emulating the CSL Elite V2 pedal control board protocol

The analog stages use no DAC chips, no level shifters, no transistors — just passive RC filters (one resistor + one capacitor per analog channel). The pedal port adds no analog stage at all: it's three wires direct from the Teensy UART pins to the RJ12.

Total active parts: one MCU.

## Fanatec port protocols

The four ports we drive each speak a different protocol.

### Shifter 1 port (H-pattern, analog)

A 6P6C RJ12 connector. The H-pattern shifter is read by the wheelbase as two analog voltages encoding the X (column) and Y (row) position of the gear lever, plus a few control pins.

**Pinout:**

| Pin | Name | Role |
|---|---|---|
| 1 | GND | Common ground |
| 2 | Select | LOW = H-pattern mode, HIGH = sequential mode |
| 3 | Detect | Wheelbase reads this and compares against Pin 2; matching = shifter present |
| 4 | X-axis | Analog voltage indicating column (Reverse, 1-2, 3-4, 5-6, or 7) |
| 5 | Y-axis | Analog voltage indicating row (Up, Middle/Neutral, Down) |
| 6 | VCC 3.3V | Wheelbase supplies 3.3V (used to power original shifter pots; we don't need it) |

**X-axis voltage levels** (5 discrete states from a resistor ladder on the original Fanatec ClubSport SQ V1.5):

| Label | Voltage | Column |
|---|---|---|
| X1 | 3.30 V | Reverse rail (leftmost) |
| X2 | 2.25 V | 1-2 gear gate |
| X3 | 1.74 V | 3-4 gear gate (Neutral column) |
| X4 | 1.42 V | 5-6 gear gate |
| X5 | 1.06 V | 7th gear rail (rightmost) |

**Y-axis voltage levels** (3 discrete states):

| Label | Voltage | Row |
|---|---|---|
| Y1 | 2.76 V | Up row |
| Y2 | 1.65 V | Middle row (Neutral) |
| Y3 | 0.63 V | Down row |

**Gear → (X, Y) lookup:**

| Gear | X | Y |
|---|---|---|
| R | X1 | Y1 |
| 1 | X2 | Y1 |
| 2 | X2 | Y3 |
| 3 | X3 | Y1 |
| 4 | X3 | Y3 |
| 5 | X4 | Y1 |
| 6 | X4 | Y3 |
| 7 | X5 | Y1 |
| Neutral | X3 | Y2 |

**12-bit PWM values** (used by the Teensy at 3.3V Vref, range 0–4095):

| Label | PWM duty | Label | PWM duty |
|---|---|---|---|
| X1 | 4095 | Y1 | 3430 |
| X2 | 2790 | Y2 | 2048 |
| X3 | 2163 | Y3 | 779 |
| X4 | 1766 | | |
| X5 | 1310 | | |

**Detect mechanism:** Pin 2 and Pin 3 must show the same level to the wheelbase for it to recognize a shifter. The original Fanatec shifter ties them together internally. In our adapter, we drive both pins to GND (forcing H-pattern mode with detect = matched).

### Shifter 2 port (sequential, digital)

A 6P6C RJ12 with internal pull-up resistors (10kΩ to 3.3V) on the shift signal pins. Genuinely simple: ground a pin briefly to register a shift.

**Pinout:**

| Pin | Name | Role |
|---|---|---|
| 1 | GND | Common ground |
| 4 | Upshift signal | Pull to GND for upshift event |
| 5 | Downshift signal | Pull to GND for downshift event |
| 2, 3, 6 | (unused) | Leave disconnected |

Pin 4 and Pin 5 idle at 3.3V (via the wheelbase's internal pull-ups). Pulling either to GND for ~50ms registers as a shift. No detect/select handshake required on this port.

### Handbrake port (analog)

A 6P6C RJ12. The wheelbase expects an analog signal in the 0–5V range. The wheelbase calibrates the range in software, so any voltage swing within those limits works.

**Pinout (verified empirically on DD+ with a test potentiometer — see note below):**

| Pin | Name | Role |
|---|---|---|
| 1 | GND | Must be connected |
| 2 | GND | Must be connected (both Pin 1 AND Pin 2 must be tied to ground) |
| 3 | NC | Not connected |
| 4 | NC | Not connected |
| 5 | Signal | Analog input, 0-5V range expected (idles at ~3.3V via internal pull-up) |
| 6 | +5V | Wheelbase supplies 5V (we don't need it) |

> **A note on pin numbering and the Oakheart guide.** This pinout was verified by hooking a real potentiometer to the port and finding which physical pins, when connected, made the Fanatec app's handbrake indicator move. The Oakheart guide referenced in [Acknowledgments](#references-and-acknowledgments) describes the same wiring correctly **in its text**, but uses an inverted pin numbering convention compared to the one in this README (and the Fanashifter, DIY-Sim, and Yin Zhong sources). Their "Pin 1" corresponds to our Pin 6 and vice versa. The wire mapping is the same physically; only the labels differ.

**Design choice in this build:** the Teensy outputs 0-3.3V instead of 0-5V via PWM + RC filter. After calibration, the wheelbase maps that reduced swing to full 0-100% handbrake range. In practice the app may report ~65% as the maximum from the adapter (since 3.3V/5V ≈ 66% of the rail) until you re-run handbrake calibration — at that point the wheelbase normalizes the new max to 100%. If you want native 0-5V swing without recalibration, add an op-amp scaler stage (see [Roadmap](#roadmap)).

> **Important: do not connect the dedicated handbrake RJ12 to the wheelbase if the pedal port is connected.** Modern Fanatec firmware treats the CSL Elite V2 pedal protocol's Handbrake field as the canonical handbrake source when the pedal port is present, but the wheelbase **prefers** the dedicated handbrake port if it detects one (i.e. if Pin 1 + Pin 2 are grounded). This means a wired-but-unused handbrake cable will *override* the live handbrake values coming from the pedal stream. Empirically: with the pedal port active, leaving the handbrake cable plugged into the wheelbase makes the handbrake stop responding entirely.
>
> **Recommended setup:** wire the pedal port and use the handbrake field of the pedal stream (already done by the firmware in `updateHandbrake()` — your USB handbrake's analog value gets routed into both the dedicated PWM pin *and* the pedal stream Handbrake channel). Leave the dedicated handbrake RJ12 cable unplugged from the wheelbase. The wiring on the breadboard / PCB side can stay in place for forward compatibility — it just doesn't go to the wheelbase.
>
> **If you don't have a pedal port wired** (e.g. shifter+handbrake-only build): the dedicated handbrake port works fine on its own. Use it as documented above.

### Pedal port (UART, CSL Elite V2 protocol)

A 6P6C RJ12 that, on modern Fanatec wheelbases (CSL Elite V2 era onwards including DD+, DD Pro, ClubSport DD, Podium), speaks a digital UART protocol — *not* analog like the shifter and handbrake ports. The wheelbase expects the connected device to emulate the Fanatec **CSL Elite V2 pedal control board**, which is based on a PIC18F26J53 microcontroller running a custom serial protocol.

On boot, the wheelbase initiates a UART handshake at 250000 baud with the connected device, then negotiates a switch to 115200 baud for the streaming data phase. Pedal positions (throttle, brake, clutch) are then transmitted as framed binary packets at the streaming baud.

**Pinout** (from the perspective of the *control board* — what our adapter emulates — sourced from [GeekyDeaks/fanatec-pedal-emulator](https://github.com/GeekyDeaks/fanatec-pedal-emulator)):

| Pin | Function (control board PoV) | Notes |
|---|---|---|
| 1 | GND | Common ground |
| 2 | GND | Probable GND (marked uncertain in upstream docs, safe to tie) |
| 3 | GND | Probable GND (same — recommend tying for safety) |
| 4 | RX | UART receive on our adapter (wheelbase's TX) |
| 5 | TX | UART transmit from our adapter (wheelbase's RX) |
| 6 | +5V | Wheelbase supplies 5V (we don't need it on our side) |

> **Why this is much simpler than the other ports.** The wheelbase's UART interface means our adapter only needs three wires (TX, RX, GND) and a single MCU UART peripheral — no DAC, no level shifter, no RC filter, no transistor. The Teensy 4.1's native UART logic is 3.3V, which is compatible with the wheelbase's TTL signaling on this port (the +5V on Pin 6 is the *power rail* for the original pedal control board, not the logic level — the data lines are 3.3V TTL).

**Why UART instead of analog emulation?**

The DD+ also supports an older analog pedal mode that identifies the connected pedals as "CSL Pedals with Clutch Kit" (Fanatec's pre-Elite pedal lineage). Third-party adapters such as the Simsonn SP Pro control box implement this analog mode and work fine on the DD+ — confirmed empirically by probing one such control box, which outputs 3 analog signals on pins 3 (Clutch), 4 (Brake), and 5 (Throttle), with each at ~4.1V idle and dropping when pressed.

This adapter targets the **UART (CSL Elite V2) path** instead because:

- **Hardware is dramatically simpler.** Three wires versus three RC filters + DAC + level shifter + transistor amplifiers.
- **Better pedal identification.** Pedals show up as CSL Elite V2 — same generation as modern Fanatec load cell pedals — unlocking load-cell-specific calibration features in the Fanatec Control Panel that the older "CSL Pedals" identity doesn't expose.
- **No analog signal chain.** Digital protocol with no ADC conversion losses, no temperature drift, no PWM ripple, no susceptibility to motor EMI from the rig.
- **Bit depth is preserved end-to-end.** The protocol carries full per-axis resolution; analog mode would be limited by the wheelbase's ADC sampling (estimated 10-12 effective bits).

Trade-off: this approach requires the wheelbase to speak the UART protocol. Bases predating the CSL Elite V2 era only support analog and would need an analog output stage instead (future work — see [Roadmap](#roadmap)).

**Teensy wiring:**

| Pedal RJ12 Pin | Teensy 4.1 |
|---|---|
| 1 | GND |
| 2 | GND (required — initial implementation had this floating and the handshake failed silently) |
| 3 | GND (required — same as Pin 2) |
| 4 (RX from control board PoV) | **Pin 15** (Serial3 RX) |
| 5 (TX from control board PoV) | **Pin 14** (Serial3 TX) |
| 6 (+5V) | Not connected |

Hardware additions on top of the shifter build: one RJ12 6P6C cable, one RJ12 breakout, and four jumper wires. No additional active or passive components.

**Firmware handshake implementation note:** the [GeekyDeaks Go reference](https://github.com/GeekyDeaks/fanatec-pedal-emulator) expects the wheelbase to send three Step-2 query packets in a strict order (`0x02 → 0x00 → 0x03`) and writes a single 36-byte response after collecting all three. **This does not match the DD+ firmware** we tested against (firmware revision contemporary with Fanatec Control Panel v1.4.2.3, base firmware 2.11.0.2). On our DD+ the wheelbase sends `0x00` and `0x03` repeatedly, sometimes 30+ times each, before finally sending `0x02`, with a different payload for the `0x02` command than what `proxy.go` expected. Strict-linear matching never completes. Our firmware follows the [community sketch posted in GeekyDeaks#4](https://github.com/GeekyDeaks/fanatec-pedal-emulator/issues/4) instead: collect 12-byte framed packets in any order, validate CRC, and send the matching 12-byte response immediately per query. Handshake completes after the `0x03` ack regardless of whether `0x00` or `0x02` have been seen yet.

## Bill of materials

| Item | Quantity | Approx. cost | Notes |
|---|---|---|---|
| Teensy 4.1 | 1 | $32 | PJRC store or Amazon |
| USB host cable for Teensy 4.1 | 1 | $5 | PJRC sells one ready-made |
| 5-pin header strip (0.1") | 1 | $0.10 | For the Teensy USB host pads |
| Powered USB hub, USB 2.0, 4-port | 1 | $15 | Must be **powered** (own wall wart) |
| RJ12 6P6C cable | 3 | $3 each | **Verify 6 conductors** — many "phone cables" are 4-wire. One for H-pattern shifter (Shifter 1), one for sequential shifter (Shifter 2), one for pedal port. The dedicated handbrake RJ12 is intentionally unused on builds that have the pedal port — handbrake is routed via the pedal stream instead (see [Handbrake port section](#handbrake-port-analog)). Add a 4th cable only if you want handbrake-only builds. |
| RJ12 6P6C breakout board | 3 | $3 each | One per cable above. |
| 1 kΩ resistor (1/4W) | 3 | $0.05 | For RC filters |
| 1 µF electrolytic capacitor (50V) | 3 | $0.10 | For RC filters; ceramic equivalent also works |
| Breadboard, 830-point | 1 | $5 | Prototyping only; final build uses perfboard or PCB |
| Jumper wires | assorted | $5 | M/M, M/F as needed |
| Micro-USB cable | 1 | — | For powering and programming the Teensy |
| Multimeter | 1 | — | Required for pre-flight checks |

**Total prototype cost:** ~$80-90 if buying everything new.

**Tools:** Soldering iron (only for the 5-pin host header on the Teensy), wire stripper, multimeter.

## Assembly

This section assumes the components above. PCB layout will be added later.

### Step 1 — Prepare the Teensy for USB host

Solder a 5-pin 0.1" male header strip into the underside USB host pads of the Teensy 4.1. The pads are the row of 5 small holes between the main side rails, near the micro-USB end. Once soldered, the PJRC USB host cable plugs onto these pins.

Verify continuity from each header pin to its corresponding underside pad with a multimeter.

### Step 2 — Build the RC filter stage on the breadboard

Three identical RC filters, one per analog output (Shifter 1 X, Shifter 1 Y, Handbrake):

```
Teensy PWM pin ───[ 1 kΩ ]───┬─── To RJ12 breakout signal pin
                             │
                            [1 µF, − to GND]
                             │
                            GND rail
```

Polarity matters on the electrolytic cap — long leg (+) to the resistor side, short leg (−) to GND. The white stripe down the side of the can marks the negative leg.

Pin assignments:

- Pin 4 (PWM) → RC filter → Shifter 1 RJ12 Pin 4 (X-axis)
- Pin 5 (PWM) → RC filter → Shifter 1 RJ12 Pin 5 (Y-axis)
- Pin 8 (PWM) → RC filter → Handbrake RJ12 Pin 5 (Signal)

### Step 3 — Wire the Shifter 2 (sequential) port

No RC filter needed — sequential is purely digital:

- Teensy pin 6 → Shifter 2 RJ12 Pin 4 (Upshift signal)
- Teensy pin 7 → Shifter 2 RJ12 Pin 5 (Downshift signal)
- Teensy GND → Shifter 2 RJ12 Pin 1 (GND)

The wheelbase's internal pull-ups handle the idle state. Firmware configures pins 6 and 7 as `OUTPUT_OPENDRAIN`.

### Step 4 — Wire the RJ12 breakouts to the cables

For each output cable, wire the corresponding breakout terminals:

**Shifter 1 (H-pattern):**

| Breakout terminal | Connects to |
|---|---|
| Pin 1 (GND) | Breadboard GND rail |
| Pin 2 (Select) | Breadboard GND rail (forces H-pattern mode) |
| Pin 3 (Detect) | Breadboard GND rail (mirrors Pin 2) |
| Pin 4 (X-axis) | RC filter output for Teensy pin 4 |
| Pin 5 (Y-axis) | RC filter output for Teensy pin 5 |
| Pin 6 (VCC 3.3V) | **Leave empty** — don't tie to anything |

**Shifter 2 (sequential):**

| Breakout terminal | Connects to |
|---|---|
| Pin 1 (GND) | Breadboard GND rail |
| Pin 4 (Up) | Teensy pin 6 |
| Pin 5 (Down) | Teensy pin 7 |
| Pin 2, 3, 6 | **Leave empty** |

**Handbrake (optional — only if you have a no-pedal-port build):**

If you have the pedal port wired (which we recommend), the handbrake RJ12 should **not** be plugged into the wheelbase — the wheelbase prefers the dedicated port over the pedal stream's Handbrake channel even when both are active. See the [warning in the Handbrake port section](#handbrake-port-analog). You can still wire the breadboard side as below to keep the option open; just don't run the cable to the wheelbase.

| Breakout terminal | Connects to |
|---|---|
| Pin 1 (GND) | Breadboard GND rail |
| Pin 2 (GND) | Breadboard GND rail (must be tied to ground too — wheelbase requires both) |
| Pin 3, 4 | **Leave empty** |
| Pin 5 (Signal) | RC filter output for Teensy pin 8 |
| Pin 6 (+5V) | **Leave empty** — don't tie to anything |

**Pedal port (recommended):**

| Breakout terminal | Connects to |
|---|---|
| Pin 1 (GND) | Breadboard GND rail |
| Pin 2 (GND) | Breadboard GND rail |
| Pin 3 (GND) | Breadboard GND rail |
| Pin 4 (RX from control board PoV) | Teensy **Pin 15** (Serial3 RX) |
| Pin 5 (TX from control board PoV) | Teensy **Pin 14** (Serial3 TX) |
| Pin 6 (+5V) | **Leave empty** — don't tie to anything |

### Step 5 — Multimeter pre-flight checks (wheelbase OFF, Teensy unpowered)

Before plugging anything into the DD+:

1. Continuity Teensy GND ↔ RJ12 Pin 1 on each cable — should beep on all three
2. Continuity Shifter 1 RJ12 Pin 2 ↔ Pin 3 ↔ GND — should beep through all three
3. No continuity between Teensy 3.3V/VIN and any RJ12 pin
4. No continuity between any two adjacent signal pins on any RJ12 cable
5. ~1 kΩ resistance (in resistance mode, not continuity) from Teensy pin 4 to Shifter 1 Pin 4 — confirms the resistor is in the path. Same for pin 5 ↔ Shifter 1 Pin 5, and pin 8 ↔ handbrake Pin 5.

All checks passing = harness is safe to plug into the wheelbase.

### Step 6 — Verify output voltages

Before adding any HID complexity, you can run the test commands from our integrated firmware (see [Firmware](#firmware)) that cycles through gear positions or sweeps the handbrake. With the wheelbase still disconnected, measure each filter output with a DC voltmeter:

For each gear position, verify the X and Y outputs match the [voltage table](#shifter-1-port-h-pattern-analog) within ±50 mV.

If voltages match, you can confidently plug into the wheelbase.

## Wiring reference

### Teensy 4.1 pin assignments

| Teensy pin | Function | Output destination |
|---|---|---|
| 4 | H-pattern X-axis (12-bit PWM, 36 kHz) | RC filter → Shifter 1 RJ12 Pin 4 |
| 5 | H-pattern Y-axis (12-bit PWM, 36 kHz) | RC filter → Shifter 1 RJ12 Pin 5 |
| 6 | Sequential UP (open-drain GPIO) | Shifter 2 RJ12 Pin 4 |
| 7 | Sequential DOWN (open-drain GPIO) | Shifter 2 RJ12 Pin 5 |
| 8 | Handbrake signal (12-bit PWM, 36 kHz) | RC filter → Handbrake RJ12 Pin 5 (breakout side only; cable typically not plugged into wheelbase — see [Handbrake port section](#handbrake-port-analog)) |
| 14 | Serial3 TX (UART, baud-switching 250000/115200) | Pedal RJ12 Pin 5 |
| 15 | Serial3 RX (UART, baud-switching 250000/115200) | Pedal RJ12 Pin 4 |
| GND | Common ground | All RJ12 GND pins, all RC filter cap negatives |
| USB host pads | USB host port | Powered hub → HID devices (4 verified: H-shifter, 2× RS combo, SP Pro pedals) |
| Micro-USB | Power + programming | USB charger or PC |

### Block diagram

```
              ┌──────────────────────────────────────┐
              │            Teensy 4.1                │
              │                                      │
USB Hub ──────┤ USB host port                        │
   │          │                                      │
   ├─► H-pat shifter            Pin 4 ──[1kΩ]──┬───────────► Shifter 1 RJ12 Pin 4
   ├─► Seq shifter                            [1µF]
   ├─► Handbrake                               GND
   └─► USB pedals               Pin 5 ──[1kΩ]──┬───────────► Shifter 1 RJ12 Pin 5
                                               [1µF]
                                                GND
                                Pin 6 ──────────────────────► Shifter 2 RJ12 Pin 4
                                Pin 7 ──────────────────────► Shifter 2 RJ12 Pin 5
                                Pin 8 ──[1kΩ]──┬───────────► Handbrake RJ12 Pin 5
                                              [1µF]
                                               GND
 
                                (pedal port: UART, no analog stage)
                                Pin 14 (TX3) ──────────────► Pedal RJ12 Pin 5
                                Pin 15 (RX3) ◄───────────── Pedal RJ12 Pin 4
 
                                GND   ─────────┬───────────► All RJ12 GND pins
                                                              (Handbrake: both Pin 1 AND Pin 2)
                                                              (Pedals: Pins 1, 2, and 3)
              └──────────────────────────────────────┘
```

## Firmware

The firmware lives in the root of this repository as a standard Arduino sketch:

- `shifter_test.ino` — top-level: USB host setup, HID consumer classes, role-specific update functions (H-pattern, sequential, handbrake, pedals-from-HID), serial CLI, manual soft-reset.
- `pedals.h` / `pedals.cpp` — CSL Elite V2 UART protocol emulator. Handshake state machine, CRC table, response packets, 100 Hz streaming.

### Core Features

1. **PWM & GPIO setup**: Configures pins 4, 5, and 8 as 12-bit PWM at 36 kHz (`analogWriteResolution(12)` & `analogWriteFrequency()`). Configures pins 6 and 7 as open-drain digital pins (`OUTPUT_OPENDRAIN`). Configures Serial3 on pins 14/15 for the pedal port UART.
2. **USB Host Driver (`USBHost_t36`)**: Enumerates USB HID devices through the hub, matching them by their PID/VID signatures:
   - **Logitech RS H-Shifter (VID `0x046D` PID `0xC26B`)**: Parses 8 H-pattern buttons, maps them to a gear index, and drives the corresponding X and Y PWM duty cycles.
   - **Logitech RS Shifter & Handbrake combo (VID `0x046D` PID `0xC278`)**: Supports two identical devices on the same bus (one used as sequential shifter, one as handbrake — the user picks via the physical mode switch on the device, firmware aggregates inputs across both regardless). Buttons 1/2 generate sequential up/down pulses on Pins 6/7. Z axis (and button 3 in digital mode) drive the handbrake, routed both to the dedicated PWM pin and into the pedal stream's Handbrake field.
   - **Simnet SP Pro Pedal (VID `0xCAFE` PID `0xA301`)**: X/Y/Z axes mapped to throttle/brake/clutch with configurable axis selection and inversion (constants at the top of the USB host section in `shifter_test.ino`).
3. **CSL Elite V2 pedal protocol emulator (`pedals.cpp`)**: Drives the wheelbase's pedal port over Serial3 at 250000→115200 baud, completing the three-step handshake (`0x0A`/`0x1A`, `0x05`/`0x15`, then per-query 12-byte responses on the Joystick collection) and streaming pedal positions at 100 Hz.
4. **Calibration & Smoothing**:
   - **H-pattern**: Uses a `NEUTRAL_TRANSIT_MS` (50 ms) switch delay so the wheelbase sees a neutral transit between gears (otherwise same-row shifts like 1→R get dropped).
   - **Handbrake**: Auto-calibrates the Z-axis range over the session, scales output smoothly.
5. **Serial Commands**: Manual diagnostic and control CLI over USB Serial (115200 baud).

### Serial Diagnostic CLI Commands

Open the Serial Monitor and type any of the following characters to interact with or debug the adapter:
* `R`, `1`–`7`, `N` : Manually set H-pattern gear (case-insensitive)
* `+` / `-` : Emit manual sequential UP / DOWN pulse
* `k` : Reset handbrake calibration
* `c` : Cycle through all H-pattern gears once (helps verify output voltages)
* `t` : Print the gear → DAC value lookup table
* `u` : Print USB host driver state + handbrake calibration + pedal state
* `p` : Force pedal handshake reset (back to Step 0)
* `q` / `w` : Decrease / increase pedal Throttle by ~1% (lowercase only)
* `a` / `s` : Decrease / increase pedal Brake by ~1%
* `z` / `x` : Decrease / increase pedal Clutch by ~1%
* `X` : Force CPU soft-reset (full restart)
* `?` : Show help menu

### Toolchain

Built using **Arduino IDE** with **Teensyduino**.
* **Board**: Teensy 4.1
* **USB Type**: Serial

---

## Calibration

After flashing the main firmware:

### H-pattern shifter

1. Open the Fanatec Control Panel
2. Navigate to the shifter calibration wizard
3. Follow the on-screen prompts: Neutral → Reverse → 1 → 2 → 3 → 4 → 5 → 6 → 7
4. For each position, move your USB shifter into that gear and click the corresponding button in the wizard
5. The wheelbase records the voltage levels and maps them to gears

### Sequential shifter

No calibration required — purely digital, the wheelbase recognizes the switch closures directly.

### Handbrake

If the dedicated handbrake RJ12 is wired to the wheelbase (no-pedal-port build):

1. In Fanatec Control Panel, navigate to the handbrake calibration screen
2. Release the handbrake fully → click "Set Min"
3. Pull the handbrake to maximum → click "Set Max"
4. The wheelbase normalizes the range to 0-100%

If the pedal port is wired and the dedicated handbrake is left unplugged from the wheelbase (recommended), the handbrake calibration happens through the Pedals V3 page instead — see Pedals below.

### Pedals (CSL Elite V2 emulation)

1. Open Fanatec Control Panel → **Pedals** section. The adapter shows up as **ClubSport Pedals V3**.
2. Press each pedal individually and watch the corresponding bar move in the app. Mappings (which of X/Y/Z drives Throttle/Brake/Clutch) and inversion are configurable via constants at the top of `shifter_test.ino` — edit and re-flash if needed.
3. Manual calibration: enable the "Manual Calibration" toggle in the app, press each pedal fully and click "Set Max", release fully and click "Set Min" for each axis.
4. The Handbrake field of the pedal stream is driven by the USB handbrake's analog output via `updateHandbrake()`. It shows up as the **Handbrake** bar on the Pedals V3 screen.

---

## Troubleshooting

**Wheelbase doesn't recognize the H-pattern shifter:**
- Verify Pin 2 ↔ Pin 3 are both at GND (continuity check)
- Verify the RJ12 cable is 6-conductor (6P6C), not 4-conductor (6P4C)
- Verify cable orientation — Pin 1 on the cable should reach Pin 1 on the wheelbase

**Gear changes are erratic / wheelbase reports wrong gear:**
- Measure each gear's X/Y voltages with the test sketch and the gear cycle running
- Voltages off by more than ±100 mV? Tune the DAC values in the gear table
- Voltages correct but gears still wrong? Check the wheelbase calibration

**Sequential shifts not registering:**
- Verify Shifter 2 Pin 4 idles at ~3.3V (the wheelbase's internal pull-up)
- Verify Teensy pins 6/7 are configured as `OUTPUT_OPENDRAIN`, not `OUTPUT` (which would conflict with the pull-up)
- Verify shift pulse duration is at least 30-50 ms (too short and the wheelbase may miss it)

**Handbrake doesn't respond at all (bar in Fanatec app static):**
- Verify the signal wire is on **RJ12 Pin 5**, not Pin 2 — Pin 2 is a *second* ground pin, not the signal pin. The Oakheart guide's text is correct (in its own numbering) but easy to misread; the verified mapping in our convention is signal=Pin 5, GND=Pin 1+Pin 2.
- Verify **both** Pin 1 and Pin 2 are connected to ground — the wheelbase requires both
- Once wiring is correct, run the handbrake calibration in Fanatec Control Panel to map the voltage range

**Handbrake doesn't reach full deflection (bar peaks at ~65%):**
- Expected behavior with the 0-3.3V output (vs. native 0-5V swing), since 3.3/5 ≈ 66% of the rail
- Re-run handbrake calibration; the wheelbase should normalize the new max to 100%
- If still insufficient or you want native 0-5V swing without recalibration, add an op-amp scaler (see [Roadmap](#roadmap))

**Handbrake works on Pedals V3 page but does nothing in-game / Fanatec app handbrake page shows zero movement:**
- Most likely the dedicated handbrake RJ12 is still plugged into the wheelbase. Current Fanatec firmware prefers the dedicated handbrake port over the pedal stream's Handbrake field, and if the port is wired with no movement (because our adapter's handbrake signal lives mainly on the pedal stream), the wheelbase locks the handbrake at "released" and ignores the live values. Unplug the handbrake RJ12 cable from the wheelbase. See the warning in the [Handbrake port section](#handbrake-port-analog).

**USB devices not enumerating, or some enumerate and others don't:**
- Confirm the USB hub is **powered** (own wall wart), not bus-powered.
- Try each device individually plugged directly into the Teensy host cable to isolate which one fails.
- **Check your hub model against the [USB hub compatibility list](#usb-hub-compatibility-important)** — known-bad hubs cause partial enumeration and cascade detach.
- Some devices have inrush spikes; an unpowered hub will fail intermittently.

**Devices were enumerating, then stopped (mass-detach):**
- Most likely the hub got into a wedged internal state. Unplug the hub from the Teensy, plug it into a PC for ~5 seconds, then plug back into the Teensy. PC-side enumeration cleans the hub's state.
- A Teensy reset alone (via `X` command or unplug/replug) does not fix this — the hub stays powered and keeps its broken state through our reboot.
- If this happens often: try a different hub model (see compatibility list).

**Pedals show up as ClubSport V3 but values don't reach the wheelbase / brake stays at zero:**
- Verify the pedal RJ12's Pin 1, Pin 2, **and** Pin 3 are all tied to ground. Upstream `GeekyDeaks` docs marked Pin 2 and Pin 3 as "GND?" with a question mark; empirically all three are required on DD+, otherwise the handshake fails silently.
- Check the firmware state via the `u` command — `Pedals (Serial3): STREAMING_115K` means the handshake completed. Anything else means the wheelbase isn't accepting our identity response.
- Try `p` to force a pedal handshake reset.

---

## Roadmap

Things explicitly *not* in the current scope, but planned or proposed for the future:

- **Analog pedal fallback** — for older Fanatec wheelbases that predate the UART pedal protocol (pre-CSL Elite V2 era), implement an analog output stage on the same physical port using a 4-channel DAC (MCP4728 + I²C level shifter, or PWM + RC + transistor amplifiers from the BoM kit). Runtime-selectable based on detected wheelbase. Pedal pinout in analog mode: Pin 3=Clutch, Pin 4=Brake, Pin 5=Throttle, ~4.1V idle, drops when pressed.
- **Software-controlled handbrake-port detect** — wire the handbrake RJ12 Pin 2's GND through a Teensy GPIO instead of the hardwired GND rail. Firmware switches the GPIO between OUTPUT_OPENDRAIN-LOW (handbrake port active) and INPUT (high-Z, port reads as disconnected to the wheelbase). Lets the same build automatically present or hide the dedicated handbrake port depending on whether the pedal port is streaming.
- **PCB design** — replace breadboard with a small custom PCB (KiCad/JLCPCB)
- **3D-printed enclosure** — panel-mount RJ12 jacks, USB-A inputs, micro-USB power
- **OLED display + buttons** — on-device status and configuration UI
- **WebSerial configuration interface** — browser-based device-to-role assignment for arbitrary HID devices
- **Universal HID mode** — accept any USB HID joystick, map buttons/axes to gear roles via the config UI
- **0-5V handbrake output** — add an op-amp scaler (MCP6001 with gain ~1.52) for native handbrake voltage range
- **Mode switching on Shifter 1** — software toggle between H-pattern and sequential mode (one-wire hardware change: Pin 2 from hardwired GND to a Teensy GPIO)
- **Cheaper MCU port** — RP2040 or ESP32-S3 alternative for community accessibility ($5 BoM vs $32)
- **Better USB hub compatibility** — survey of working hubs across price points; potentially a custom hub design built into the project's PCB to guarantee compatibility

---

## References and acknowledgments

This project would not have been possible without the prior reverse engineering work of:

- **[Universal Shifter Interface for Fanatec Wheelbase](https://www.diy-sim.com/guides/projects/item/universal-fanatec-shifter-interface)** by DIY-Sim — primary source for the H-pattern resistor ladder voltage values and the gear → (X, Y) mapping. Their published values are used as reference targets; the firmware and circuit are independently implemented.
- **[Fanashifter](https://github.com/juanmcasillas/Fanashifter)** by Juan M. Casillas — reference for sequential shifter protocol, RJ12 pinout documentation, and Shifter 2 internal pull-up behavior at 3.3V. Used as protocol documentation only; no code from this project was copied or adapted.
- **[Fanatec ClubSport Shifter SQ V1.5 USB Adapter DIY](https://hackaday.io/project/171155-fanatec-clubsport-shifter-sq-v15-usb-adapter-diy)** by Yin Zhong — original reverse engineering of the Fanatec shifter protocol
- **[Conversion of a Logitech Shifter for Fanatec Wheelbase Compatibility](https://www.gtplanet.net/forum/threads/conversion-of-a-logitech-shifter-for-fanatec-wheelbase-compatibility-enabling-7th-gear-other-mods.384099/)** by B-spec / Bob (GTPlanet forum, Dec 2018) — additional protocol documentation
- **[Guide to convert third-party (Hall sensor) handbrake to Fanatec base compatible](https://www.oakheartcustombuilds.com/handleidingen/GuideThirdPartyHandbrakeToFanatec_v1.0.pdf)** by Oakheart Custom Builds — handbrake port pinout and signal behavior. Note: their guide uses an inverted pin numbering convention compared to this README (their "Pin 1" corresponds to our Pin 6, their "Pin 5+6" to our Pin 1+2, etc.). The wire mapping is the same physically; if you reference their guide alongside this one, mentally flip the pin labels.
- **[fanatec-pedal-emulator](https://github.com/GeekyDeaks/fanatec-pedal-emulator)** by GeekyDeaks — referenced for future pedal port emulation work, includes full CSL Elite V2 pedal UART protocol reverse engineering

Special thanks to the broader sim racing DIY community for keeping reverse engineering documentation alive and accessible.

---

## License

**Software** (firmware, sketches, configuration utilities): [Apache License 2.0](https://www.apache.org/licenses/LICENSE-2.0)

**Hardware design** (schematics, BoM, PCB layouts, enclosure STLs as they get added): [CERN Open Hardware Licence Version 2 — Permissive (CERN-OHL-P v2)](https://ohwr.org/project/cernohl/wikis/Documents/CERN-OHL-version-2)

Both licenses are deliberately permissive. This project was built from scratch — the protocol documentation, pinouts, and voltage tables cited in [References and acknowledgments](#references-and-acknowledgments) informed the design, but no code or schematic files were copied from prior projects. Attribution to those projects (and to this one if you build on it) is the only obligation; no copyleft inheritance applies.

### Manufacturing and commercial reuse — explicitly welcome

If you have the capacity to turn this into a polished product — a small enclosed unit with an integrated powered USB hub, panel-mount RJ12 jacks, a clean PCB instead of the breadboard build, proper EMI considerations, and decent QC — **please do**. The permissive licenses are chosen precisely to make this easy.

The sim racing community has a real gap for affordable, well-built USB-HID-to-Fanatec adapters. The few existing commercial products are either expensive, awkward to use, or limited to specific peripheral brands. A polished version of this design — even at a reasonable markup for the work involved — would be a genuinely useful product.

If you take this to market, attribution to this repository and the prior reverse-engineering work cited in [References](#references-and-acknowledgments) is the only ask. Pull requests improving the design upstream are appreciated but not required.

---

**Maintainer:** [Gugic](https://github.com/Gugic)  
**Issues / contributions welcome:** [https://github.com/Gugic/fanadapter/issues](https://github.com/Gugic/fanadapter/issues)
