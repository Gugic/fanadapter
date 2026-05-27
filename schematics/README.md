# Hardware & Schematics Reference

This directory contains the physical, mechanical, and electrical details for constructing the `fanadapter` hardware.

> [!NOTE]
> **Contributions Welcome:** A structured PCB layout (KiCad project and Gerber files) and a 3D-printable enclosure are planned for the future, but are not yet available. If you design a custom PCB or enclosure for this project, contributions and pull requests are highly encouraged!

---

## High-Level System Block Diagram

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

---

## Fanatec Port Protocols & Pinouts

The adapter connects to the wheelbase using three 6P6C RJ12 cables. Each port operates under a distinct hardware protocol.

### 1. Shifter 1 Port (H-Pattern, Analog)
Encodes the X (column) and Y (row) position of the gear lever using analog voltages.

**RJ12 Pinout:**
| Pin | Name | Role | Notes |
|---|---|---|---|
| 1 | GND | Common ground | |
| 2 | Select | Mode selector | Drive to GND to force H-pattern mode |
| 3 | Detect | Shifter detection | Drive to GND (must match Select pin) |
| 4 | X-axis | Analog column voltage | Reverse, 1-2, 3-4, 5-6, or 7 |
| 5 | Y-axis | Analog row voltage | Up, Neutral/Middle, Down |
| 6 | VCC 3.3V | 3.3V Supply | Supplied by wheelbase (leave disconnected) |

**Voltage & PWM Levels (Teensy 3.3V Vref, 12-bit PWM, 0–4095 range):**
* **X-Axis (Columns):**
  * **X1 (Reverse rail):** 3.30 V (PWM: 4095)
  * **X2 (1-2 gate):** 2.25 V (PWM: 2790)
  * **X3 (3-4 gate / Neutral):** 1.74 V (PWM: 2163)
  * **X4 (5-6 gate):** 1.42 V (PWM: 1766)
  * **X5 (7th gate):** 1.06 V (PWM: 1310)
* **Y-Axis (Rows):**
  * **Y1 (Up row / gears 1, 3, 5, 7, R):** 2.76 V (PWM: 3430)
  * **Y2 (Middle row / Neutral):** 1.65 V (PWM: 2048)
  * **Y3 (Down row / gears 2, 4, 6):** 0.63 V (PWM: 779)

**Gear Mapping Matrix:**
| Gear | X-Axis Target | Y-Axis Target |
|---|---|---|
| **Reverse (R)** | X1 (3.30 V) | Y1 (2.76 V) |
| **Gear 1** | X2 (2.25 V) | Y1 (2.76 V) |
| **Gear 2** | X2 (2.25 V) | Y3 (0.63 V) |
| **Gear 3** | X3 (1.74 V) | Y1 (2.76 V) |
| **Gear 4** | X3 (1.74 V) | Y3 (0.63 V) |
| **Gear 5** | X4 (1.42 V) | Y1 (2.76 V) |
| **Gear 6** | X4 (1.42 V) | Y3 (0.63 V) |
| **Gear 7** | X5 (1.06 V) | Y1 (2.76 V) |
| **Neutral** | X3 (1.74 V) | Y2 (1.65 V) |

---

### 2. Shifter 2 Port (Sequential, Digital)
Uses switch-to-ground signals. Genuinely simple: pull the pin to ground briefly to trigger a shift. The wheelbase provides internal pull-up resistors (10kΩ to 3.3V).

**RJ12 Pinout:**
| Pin | Name | Role |
|---|---|---|
| 1 | GND | Common ground |
| 4 | Upshift | Pull to GND for upshift event |
| 5 | Downshift | Pull to GND for downshift event |
| 2, 3, 6 | Unused | Leave disconnected |

*Pins 4 and 5 idle at 3.3V. Pulling either to GND for ~50ms registers a shift. No presence-detection handshake is needed.*

---

### 3. Handbrake Port (Analog)
Expects an analog voltage. The wheelbase normalizes this range in calibration, but natively expects a 0–5V swing.

**RJ12 Pinout:**
| Pin | Name | Role |
|---|---|---|
| 1 | GND | Must be connected to ground |
| 2 | GND | Must be connected to ground (both Pin 1 and Pin 2 must be tied to GND) |
| 3, 4 | NC | Leave disconnected |
| 5 | Signal | Analog voltage input (0-5V range expected; idles at 3.3V via base pull-up) |
| 6 | +5V | 5V supply from wheelbase (leave disconnected) |

> [!WARNING]
> **Handbrake Connection Routing Choice:** On modern Fanatec firmware (CSL Elite V2 era onwards including DD+), the wheelbase listens to the digital CSL Elite V2 pedal UART protocol stream, which carries its own handbrake field. The firmware writes the handbrake channel value to **both** the PWM pin (for dedicated setups) and the pedal stream's handbrake field.
> 
> *However*, if the wheelbase detects a cable plugged into the dedicated handbrake port (by sensing Pin 1 + Pin 2 tied to ground), **it will override the pedal-stream values** and lock the handbrake to whatever is measured on that physical port. Therefore, **if you are using the pedal port emulation, leave the dedicated handbrake RJ12 cable unplugged from the wheelbase.** The handbrake will route digitally and perfectly over the pedal stream.

**Output level (this build):** the Teensy drives 0–3.3 V (PWM + RC filter), not the native 0–5 V. After handbrake calibration the wheelbase normalizes that reduced swing to full range; until you recalibrate, the Fanatec app may cap the handbrake at ~65% (3.3 V / 5 V ≈ 66% of the rail). For a native 0–5 V swing without recalibration, add an op-amp scaler stage (see the root [Roadmap](../README.md#project-roadmap)).

> [!NOTE]
> **Oakheart guide pin numbering.** This pinout was verified empirically — a potentiometer on the port while watching the Fanatec app's handbrake indicator move. The Oakheart handbrake guide in [References](../README.md#references-and-acknowledgments) describes the same wiring correctly in its text but uses an **inverted** pin-numbering convention vs. this README (and the Fanashifter / DIY-Sim / Yin Zhong sources): their "Pin 1" is our Pin 6, etc. The physical wire mapping is identical — only the labels differ.

---

### 4. Pedal Port (UART, CSL Elite V2 Emulation)
Speaks a digital UART protocol to emulate a Fanatec **CSL Elite V2 pedal control board**. It performs a handshake at 250,000 baud and switches to 115,200 baud to stream 12-byte framed pedal values (throttle, brake, clutch, handbrake) at 100 Hz.

**Advantages over analog pedal emulators:**
* **Simple Hardware:** Requires only 3 wires (TX, RX, GND) directly connected to Teensy pins. No DAC, level-shifter, or RC filters needed. (The wheelbase data lines operate at 3.3V TTL logic, matching the Teensy).
* **Load Cell Features:** Enriches the software interface by unlocking load-cell specific calibration settings in the Fanatec Control Panel.
* **No EMI Noise:** Immunity to motor EMI, thermal drift, and PWM ripple.
* **Full Bit Depth:** The digital protocol carries full per-axis resolution end-to-end — no ADC quantization loss (the analog path is limited by the wheelbase's ~10–12 effective bits).

**RJ12 Pinout:**
| Pin | Function (Control Board PoV) | Teensy 4.1 Connection | Notes |
|---|---|---|---|
| 1 | GND | GND | Common ground |
| 2 | GND | GND | Ground (must be tied) |
| 3 | GND | GND | Ground (must be tied) |
| 4 | RX (receive) | **Pin 15 (Serial3 RX)** | Receives data from wheelbase TX |
| 5 | TX (transmit) | **Pin 14 (Serial3 TX)** | Transmits data to wheelbase RX |
| 6 | +5V | NC | 5V supply from wheelbase (leave disconnected) |

> [!IMPORTANT]
> **Grounding:** Pins 1, 2, and 3 on the RJ12 breakout must all be connected to the common GND rail. If Pin 2 or Pin 3 is left floating, the digital UART handshake will fail silently.

> [!NOTE]
> **DD+ handshake ordering quirk.** The [GeekyDeaks Go reference](https://github.com/GeekyDeaks/fanatec-pedal-emulator) expects the wheelbase to send three Step-2 query packets in a strict `0x02 → 0x00 → 0x03` order and replies once after collecting all three. The DD+ firmware tested here (base FW `2.11.0.2`, Fanatec Control Panel `1.4.2.3`) does **not** behave that way — it sends `0x00` and `0x03` repeatedly (sometimes 30+ times each) before `0x02`, so strict-linear matching never completes. This firmware instead follows the [community sketch in GeekyDeaks#4](https://github.com/GeekyDeaks/fanatec-pedal-emulator/issues/4): collect 12-byte framed packets in any order, validate CRC, and reply per query immediately; the handshake completes on the `0x03` ack regardless of whether `0x00` / `0x02` have been seen yet.

> [!NOTE]
> **Analog pedal mode (not used here).** The DD+ also supports an older analog pedal identity ("CSL Pedals with Clutch Kit") that some third-party control boxes (e.g. the Simsonn SP Pro) drive. In that mode the pedal RJ12 carries three analog signals — **Pin 3 = Clutch, Pin 4 = Brake, Pin 5 = Throttle**, each idling ~4.1 V and dropping when pressed. This adapter targets the UART path instead; an analog output stage is future work (see the root [Roadmap](../README.md#project-roadmap)).

---

## Bill of Materials (BoM)

| Item | Quantity | Approx. Cost | Notes |
|---|---|---|---|
| **Teensy 4.1** | 1 | $32.00 | PJRC or authorized distributors |
| **USB Host Cable** | 1 | $5.00 | 5-pin JST to USB-A female (PJRC sells these) |
| **5-pin Male Header Strip** | 1 | $0.10 | 0.1" pitch, to solder to Teensy host pads |
| **Powered USB 2.0 Hub** | 1 | $15.00 | **Must be powered** (external power supply) |
| **RJ12 6P6C Cables** | 3 | $3.00/each | Must have all 6 wires connected. One for Shifter 1, Shifter 2, and Pedals. |
| **RJ12 6P6C Breakout Boards**| 3 | $3.00/each | Screw terminals for easy prototyping |
| **1 kΩ Resistor (1/4W)** | 3 | $0.05/each | For RC filters |
| **1 µF Capacitor** | 3 | $0.10/each | For RC filters (electrolytic or ceramic) |
| **Breadboard (830-point)** | 1 | $5.00 | For prototyping |
| **Jumper Wires** | 1 pack | $5.00 | Male/Male and Male/Female as needed |
| **Micro-USB Cable** | 1 | — | For Teensy power and programming |

*Total cost to prototype:* **~$80 - $90**.

---

## Step-by-Step Assembly

### Step 1: Teensy USB Host Header
Solder a 5-pin 0.1" pitch male header strip onto the USB host pads located on the underside of the Teensy 4.1 (near the Micro-USB port). Once soldered, plug the Teensy USB Host cable onto these pins. Use a multimeter to verify continuity from each pin to its corresponding copper pad.

### Step 2: Build the RC Analog Filters
Construct three identical low-pass RC filters on your breadboard for the analog signals (Shifter 1 X-axis, Shifter 1 Y-axis, Handbrake):

```
Teensy PWM Pin ────[ 1 kΩ Resistor ]────┬────► To RJ12 Breakout Signal Pin
                                        │
                                  [ 1 µF Cap ] (+ leg if electrolytic)
                                        │
                                     GND Rail
```
*If using electrolytic capacitors, the negative stripe leg must connect to the GND rail.*

**Analog pin routing:**
* Teensy **Pin 4 (PWM)** → Filter → Shifter 1 Breakout **Pin 4 (X-axis)**
* Teensy **Pin 5 (PWM)** → Filter → Shifter 1 Breakout **Pin 5 (Y-axis)**
* Teensy **Pin 8 (PWM)** → Filter → Handbrake Breakout **Pin 5 (Signal)**

### Step 3: Wire Shifter 2 (Sequential)
Sequential connections are purely digital. No filters are needed.
* Teensy **Pin 6 (GPIO)** → Shifter 2 Breakout **Pin 4 (Upshift)**
* Teensy **Pin 7 (GPIO)** → Shifter 2 Breakout **Pin 5 (Downshift)**
* Common GND rail → Shifter 2 Breakout **Pin 1 (GND)**

### Step 4: Wire the Pedal UART
No filters are needed. Tie the serial lines directly:
* Teensy **Pin 14 (TX3)** → Pedal Breakout **Pin 5 (RX/TX line)**
* Teensy **Pin 15 (RX3)** → Pedal Breakout **Pin 4 (RX/TX line)**
* Common GND rail → Pedal Breakout **Pins 1, 2, and 3**

### Step 5: Multimeter Pre-Flight Checks (Power Unplugged!)
Perform these safety checks with your multimeter before connecting the adapter to your expensive Fanatec wheelbase:
1. **GND Check:** Check continuity between Teensy GND and Pin 1 on all RJ12 breakouts. It should beep.
2. **Select/Detect Check:** Check continuity between Shifter 1 Breakout Pin 2, Pin 3, and GND. All three must be connected.
3. **Shorts Check:** Check that there is no continuity (no short) between Teensy 3.3V/5V/VIN and any RJ12 output pins.
4. **Adjacent Pin Check:** Verify there are no adjacent shorts on any of the RJ12 breakout terminals.
5. **Resistor Check:** Measure resistance from Teensy Pin 4 to Shifter 1 Breakout Pin 4, from Pin 5 to Shifter 1 Breakout Pin 5, and from Pin 8 to Handbrake Breakout Pin 5. Each path should read ~1 kΩ.

### Step 6: Output Voltage Verification
Before plugging the cables into the wheelbase:
1. Power up the Teensy via Micro-USB.
2. Open the [webconfig configuration utility](../webconfig/README.md) or use the Serial CLI.
3. In the **Outputs** tab, trigger the test voltages for the gears (or use the JSON command `{"cmd":"test_gear","channel":"gear_3"}`).
4. Measure the voltage at the Shifter 1 Breakout X and Y pins with a DC voltmeter. Verify they match the X/Y target voltages in the [Shifter 1 target table](#1-shifter-1-port-h-pattern-analog) within ±50 mV.

If everything checks out, it is safe to plug the cables into the Fanatec wheelbase.

---

## Troubleshooting

> Firmware-level symptoms (pedals enumerate but read 0%, sequential shifts dropping) are covered in [firmware/README.md](../firmware/README.md#troubleshooting). The entries below are wiring / hardware.

**Wheelbase doesn't recognize the H-pattern shifter:**
* Verify Shifter 1 Pin 2 and Pin 3 are both pulled to GND (continuity) — the wheelbase needs Select and Detect to match.
* Verify the RJ12 cable is 6-conductor (6P6C), not 4-conductor.
* Verify cable orientation — Pin 1 on the cable reaches Pin 1 on the wheelbase.

**Gear changes are erratic / the wheelbase reports the wrong gear:**
* Measure each gear's X/Y output voltages (per-gear **Test** button, or `{"cmd":"test_gear","channel":"gear_3"}`) against the [Shifter 1 target table](#1-shifter-1-port-h-pattern-analog).
* Off by more than ±100 mV? Tune the per-gear DAC values in the webconfig **Outputs** tab.
* Voltages correct but gears still wrong? Re-run the wheelbase's shifter calibration wizard.

**Handbrake doesn't respond at all (Fanatec app bar static):**
* The signal wire must be on **Pin 5**, not Pin 2 — Pin 2 is a second ground, not the signal.
* **Both** Pin 1 and Pin 2 must be tied to ground; the wheelbase requires both.
* Once wiring is correct, run handbrake calibration in the Fanatec Control Panel.

**Handbrake works on the Pedals page but does nothing in-game (or the app's handbrake page reads zero):**
* The dedicated handbrake RJ12 is almost certainly still plugged into the wheelbase. Current firmware prefers that physical port over the pedal-stream handbrake field and locks it at "released". Unplug the dedicated handbrake cable from the wheelbase (see the Handbrake routing warning above).

**USB devices don't enumerate, or some attach and others don't:**
* Confirm the hub is **powered** (own supply), not bus-powered — inrush spikes make bus-powered hubs fail intermittently.
* Plug each device directly into the Teensy host cable to isolate which one fails.
* Check the hub against the [compatibility list](../README.md#usb-hubs-important) — known-bad hubs cause partial enumeration and detach cascades.

**Devices were working, then mass-detached:**
* The hub is likely wedged. Unplug it from the Teensy, plug it into a PC for ~5 s to renegotiate its state, then reconnect. A Teensy reset alone won't fix it — the hub keeps its state across the reboot.

---

## License

The hardware design is licensed under the **CERN Open Hardware Licence Version 2 — Permissive (CERN-OHL-P v2)**. Full terms and the project's dual-license details are in [LICENSE.md](../LICENSE.md); the root [README](../README.md#license) covers the commercial-reuse invitation.
