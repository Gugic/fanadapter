# Hardware & Schematics Reference

This directory contains the physical, mechanical, and electrical details for constructing the `fanadapter` hardware.

> [!NOTE]
> **Contributions Welcome:** This directory currently contains wiring documentation, not a KiCad project, Gerbers or enclosure models. PCB and enclosure designs are unscheduled ideas; contributions are welcome.

---

## High-Level System Block Diagram

```
PC ──USB-C──► STM32H743 (CDC console, configuration, DFU, power)
                 │
Powered USB hub ◄─┤ PB14 = D−, PB15 = D+, board 5 V = VBUS, GND
  ├─ H-pattern   │
  ├─ Sequential  ├─ PA4 (DAC X) ───────────────► Shifter 1 RJ12 Pin 4
  ├─ Handbrake   ├─ PA5 (DAC Y) ───────────────► Shifter 1 RJ12 Pin 5
  └─ USB pedals  ├─ PC6 (open-drain up) ───────► Shifter 2 RJ12 Pin 4
                 ├─ PC7 (open-drain down) ─────► Shifter 2 RJ12 Pin 5
                 ├─ PA2 (USART2 TX) ──────────► Pedal RJ12 Pin 5
                 ├─ PA3 (USART2 RX) ◄────────── Pedal RJ12 Pin 4
                 ├─ PC8 (optional PWM) ─[1kΩ]─┬─► Handbrake RJ12 Pin 5
                 │                          [1µF]
                 │                           GND
                 └─ GND ─────────────────────► All RJ12 GND pins
                                                Shifter 1: Pins 1, 2, 3
                                                Shifter 2: Pin 1
                                                Pedals: Pins 1, 2, 3
                                                Optional handbrake: Pins 1, 2
```

The board's on-board USB-C is a device port; the hub must use the PB14/PB15 header. Handbrake normally travels in the pedal UART stream, so leave the dedicated handbrake RJ12 unplugged in that setup. Gear outputs use real DACs with no RC filters. See the [board pin map](../firmware-stm32/README.md#2-pin-map).

---

## Fanatec Port Protocols & Pinouts

The usual build uses three 6P6C RJ12 cables: Shifter 1, Shifter 2 and Pedals. The separate Handbrake port is an optional alternative to the pedal-stream handbrake. Each port operates under a distinct hardware protocol.

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

**Voltage & DAC Levels (STM32 3.3 V reference, 12-bit DAC, 0–4095 range):**
* **X-Axis (Columns):**
  * **X1 (Reverse rail):** 3.30 V (DAC: 4095)
  * **X2 (1-2 gate):** 2.25 V (DAC: 2790)
  * **X3 (3-4 gate / Neutral):** 1.74 V (DAC: 2163)
  * **X4 (5-6 gate):** 1.42 V (DAC: 1766)
  * **X5 (7th gate):** 1.06 V (DAC: 1310)
* **Y-Axis (Rows):**
  * **Y1 (Up row / gears 1, 3, 5, 7, R):** 2.76 V (DAC: 3430)
  * **Y2 (Middle row / Neutral):** 1.65 V (DAC: 2048)
  * **Y3 (Down row / gears 2, 4, 6):** 0.63 V (DAC: 779)

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

These are nominal targets. The STM32 DAC output buffer can clamp near the supply rail;
Reverse's X output reading about 0.2 V below 3.3 V is expected. The wheelbase normalizes
the actual voltages during shifter calibration.

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
> *However*, if the wheelbase detects a cable plugged into the dedicated handbrake port (by sensing Pin 1 + Pin 2 tied to ground), **it will override the pedal-stream values** and lock the handbrake to whatever is measured on that physical port. Therefore, **if you are using the pedal port emulation, leave the dedicated handbrake RJ12 cable unplugged from the wheelbase.** On the tested DD+, the handbrake then uses the digital pedal stream.

**Output level (this build):** PC8 drives 0–3.3 V (PWM + RC filter), not the native 0–5 V. After handbrake calibration the wheelbase normalizes that reduced swing to full range; until you recalibrate, the Fanatec app may cap the handbrake at ~65% (3.3 V / 5 V ≈ 66% of the rail). A 0–5 V op-amp scaler is an [unimplemented extension](../README.md#implemented-features-and-possible-extensions), not part of the wiring below.

> [!NOTE]
> **Oakheart guide pin numbering.** This pinout was verified empirically — a potentiometer on the port while watching the Fanatec app's handbrake indicator move. The Oakheart handbrake guide in [References](../README.md#references-and-acknowledgments) describes the same wiring correctly in its text but uses an **inverted** pin-numbering convention vs. this README (and the Fanashifter / DIY-Sim / Yin Zhong sources): their "Pin 1" is our Pin 6, etc. The physical wire mapping is identical — only the labels differ.

---

### 4. Pedal Port (UART, CSL Elite V2 Emulation)
Speaks a digital UART protocol to emulate a Fanatec **CSL Elite V2 pedal control board**. It performs a handshake at 250,000 baud and switches to 115,200 baud to stream 12-byte framed pedal values (throttle, brake, clutch, handbrake) at 100 Hz.

**Advantages over analog pedal emulators:**
* **Simple Hardware:** Requires only 3 wires (TX, RX, GND) directly connected to PA2/PA3 and GND. No DAC, level-shifter, or RC filters needed. (The wheelbase data lines operate at 3.3V TTL logic, matching the STM32).
* **Load Cell Features:** Enriches the software interface by unlocking load-cell specific calibration settings in the Fanatec Control Panel.
* **Digital transport:** Avoids an extra analog conversion and PWM-filter ripple on the pedal path. UART still depends on signal integrity and grounding; interference can corrupt frames and drop the link.
* **16-bit axis fields:** The packet carries each mapped axis as a 0–65535 value. Effective resolution still depends on the source controller, calibration and wheelbase processing.

**RJ12 Pinout:**
| Pin | Function (Control Board PoV) | STM32 Connection | Notes |
|---|---|---|---|
| 1 | GND | GND | Common ground |
| 2 | GND | GND | Ground (must be tied) |
| 3 | GND | GND | Ground (must be tied) |
| 4 | RX (receive) | **PA3 (USART2 RX)** | Receives data from wheelbase TX |
| 5 | TX (transmit) | **PA2 (USART2 TX)** | Transmits data to wheelbase RX |
| 6 | +5V | NC | 5V supply from wheelbase (leave disconnected) |

> [!IMPORTANT]
> **Grounding:** Pins 1, 2, and 3 on the RJ12 breakout must all be connected to the common GND rail. If Pin 2 or Pin 3 is left floating, the digital UART handshake will fail silently.

> [!NOTE]
> **DD+ handshake ordering quirk.** The [GeekyDeaks Go reference](https://github.com/GeekyDeaks/fanatec-pedal-emulator) expects the wheelbase to send three Step-2 query packets in a strict `0x02 → 0x00 → 0x03` order and replies once after collecting all three. The DD+ firmware tested here (base FW `2.11.0.2`, Fanatec Control Panel `1.4.2.3`) does **not** behave that way — it sends `0x00` and `0x03` repeatedly (sometimes 30+ times each) before `0x02`, so strict-linear matching never completes. This firmware instead follows the [community sketch in GeekyDeaks#4](https://github.com/GeekyDeaks/fanatec-pedal-emulator/issues/4): collect 12-byte framed packets in any order, validate CRC, and reply per query immediately; the handshake completes on the `0x03` ack regardless of whether `0x00` / `0x02` have been seen yet.

> [!NOTE]
> **Analog pedal mode (not used here).** The DD+ also supports an older analog pedal identity ("CSL Pedals with Clutch Kit") that some third-party control boxes (e.g. the Simsonn SP Pro) drive. In that mode the pedal RJ12 carries three analog signals — **Pin 3 = Clutch, Pin 4 = Brake, Pin 5 = Throttle**, each idling ~4.1 V and dropping when pressed. This adapter targets the UART path instead; an analog output stage is an [unimplemented extension](../README.md#implemented-features-and-possible-extensions).

---

## Bill of Materials (BoM)

| Item | Quantity | Notes |
|---|---|---|
| **WeAct MiniSTM32H743VITX** | 1 | Verify the chip is **STM32H743VIT6**, not H723; see the [board guide](../firmware-stm32/README.md#1-what-you-need). |
| **USB-C data cable** | 1 | PC connection, power, configuration and DFU. |
| **USB breakout / USB-A cable** | 1 | Four wires to PB14/PB15, GND and board 5 V. |
| **Powered USB 2.0 hub** | 1 | External supply; upstream VBUS must also be connected. |
| **RJ12 6P6C cables and breakout boards** | 3 | Shifter 1, Shifter 2 and Pedals; all six conductors present. |
| **1 kΩ resistor + 1 µF capacitor** | 1 pair, optional | Only for the PC8 handbrake fallback; add a fourth RJ12 cable/breakout if used. |
| **Breadboard + jumper wires** | As needed | Prototyping and common ground rail. |

---

## Step-by-Step Assembly

### Step 1: Wire the USB Host

Connect the hub's upstream USB breakout to **PB15 (D+)**, **PB14 (D−)**, **GND**, and **board 5 V (VBUS)**. Power the hub from its own supply. Use the on-board USB-C for the PC connection. Verify continuity at each header connection.

### Step 2: Wire the Analog Outputs

* **PA4 (DAC)** → Shifter 1 breakout **Pin 4 (X-axis)**.
* **PA5 (DAC)** → Shifter 1 breakout **Pin 5 (Y-axis)**.
* Common GND → Shifter 1 breakout **Pins 1, 2, and 3** (ground, select, detect).

Do not place an RC filter or capacitor on the gear DAC outputs: the output buffer can oscillate with a capacitive load.

For the optional dedicated handbrake fallback, build one low-pass filter:

```
PC8 (PWM) ──[ 1 kΩ ]──┬──► Handbrake breakout Pin 5
                      │
                    [1 µF]
                      │
                     GND
```

If the capacitor is electrolytic, its negative leg goes to GND. Tie handbrake breakout **Pins 1 and 2** to GND. Leave this RJ12 unplugged when using the pedal-stream handbrake.

### Step 3: Wire Shifter 2 (Sequential)
Sequential connections are purely digital. No filters are needed.
* STM32 **PC6 (open-drain GPIO)** → Shifter 2 Breakout **Pin 4 (Upshift)**
* STM32 **PC7 (open-drain GPIO)** → Shifter 2 Breakout **Pin 5 (Downshift)**
* Common GND rail → Shifter 2 Breakout **Pin 1 (GND)**

### Step 4: Wire the Pedal UART
No filters are needed. Tie the serial lines directly:
* STM32 **PA2 (USART2 TX)** → Pedal Breakout **Pin 5 (wheelbase RX)**
* STM32 **PA3 (USART2 RX)** → Pedal Breakout **Pin 4 (wheelbase TX)**
* Common GND rail → Pedal Breakout **Pins 1, 2, and 3**

### Step 5: Multimeter Pre-Flight Checks (Power Unplugged!)
Perform these safety checks with your multimeter before connecting the adapter to your expensive Fanatec wheelbase:
1. **GND Check:** Check continuity between STM32 GND and Pin 1 on all RJ12 breakouts. It should beep.
2. **Select/Detect Check:** Check continuity between Shifter 1 Breakout Pin 2, Pin 3, and GND. All three must be connected.
3. **Shorts Check:** Check that there is no continuity (no short) between board 3.3 V / 5 V and any RJ12 output pins.
4. **Adjacent Pin Check:** Verify there are no unintended shorts. The ground/select/detect connections listed above intentionally join adjacent pins; signal and supply pins must remain separate.
5. **Signal Check:** PA4/PA5 connect directly to Shifter 1 Pins 4/5. If the handbrake fallback is installed, PC8 to its Pin 5 should read ~1 kΩ.

### Step 6: Output Voltage Verification
Before plugging the cables into the wheelbase:
1. Power up the STM32 via USB-C.
2. Open the [webconfig configuration utility](../webconfig/README.md) or use the Serial CLI.
3. In the **Outputs** tab, trigger the test voltages for the gears (or use the JSON command `{"cmd":"test_gear","channel":"gear_3"}`).
4. Measure the Shifter 1 X/Y pins with a DC voltmeter against the [target table](#1-shifter-1-port-h-pattern-analog). Allow the near-rail DAC buffer clamp described above for Reverse; adjust other mismatches with the per-gear DAC controls, then calibrate the shifter on the wheelbase.

If the wiring and voltage checks pass, power down before connecting the RJ12 cables to the wheelbase.

---

## Troubleshooting

> Firmware-level symptoms (pedals enumerate but read 0%, sequential shifts dropping) are covered in [the firmware guide](../firmware-stm32/README.md#7-troubleshooting). The entries below are wiring / hardware.

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
* Verify D+/D− and upstream VBUS wiring on the PB14/PB15 breakout, then test one device at a time on the powered hub.
* Check the [USB hub guidance](../README.md#usb-hubs-important) and [firmware diagnostics](../firmware-stm32/README.md#7-troubleshooting).

**Devices were working, then mass-detached:**
* The hub is likely wedged. Unplug it from the adapter, plug it into a PC for ~5 s to renegotiate its state, then reconnect. An adapter reset alone won't fix it — the hub keeps its state across the reboot.

---

## License

The hardware design is licensed under the **CERN Open Hardware Licence Version 2 — Permissive (CERN-OHL-P v2)**. License details and the link to the full terms are in [LICENSE.md](../LICENSE.md); the root [README](../README.md#license) covers the commercial-reuse invitation.
