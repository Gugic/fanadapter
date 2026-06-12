// fanadapter — USB HID → Fanatec wheelbase adapter (ESP32-S3 N16R8)
//
// Reads USB HID joystick / gamepad / keyboard devices through the chip's
// native USB OTG port (configured as host) and drives three Fanatec shifter
// / handbrake DACs plus the CSL Elite V2 pedal UART. Input → output
// assignment is configured at runtime via JSON over UART0 (typically driven
// by the bundled webconfig WebSerial UI, which talks to the dev board's
// onboard USB-Serial bridge on the "UART" port), and persisted to NVS.
//
// This .ino file is just the orchestrator: USB host bring-up, pin / PWM /
// UART setup, the minimal serial CLI, and the loop dispatch. Everything
// else lives in the side modules:
//
//   device_pool.h/cpp   USB HID joystick slot pool (claims-any, 8 slots,
//                        EspUsbHost-backed)
//   mapping.h/cpp       Config schema, NVS I/O, input → output engine
//   protocol.h/cpp      JSON command parser, live event emitter
//   pedals.h/cpp        Fanatec pedal-port UART state machine (Serial1)
//
// Wiring summary (see mapping.h for the literal GPIO numbers and the
// firmware README for the corresponding RJ12 pinout):
//   PIN_X / PIN_Y           — H-pattern X/Y DACs (PWM via RC low-pass)
//   PIN_SEQ_UP / PIN_SEQ_DOWN — sequential up/down (open-drain, idle high)
//   PIN_HANDBRAKE           — handbrake DAC
//   PEDAL_TX / PEDAL_RX     — pedal-port UART (Serial1)
//   UART0 (Serial)          — config UART, surfaces as a COM port on the
//                             dev board's onboard USB-Serial bridge

#include <Arduino.h>

#include "device_pool.h"
#include "mapping.h"
#include "pedals.h"
#include "protocol.h"

// ---------------- Minimal serial CLI ----------------
// Everything beyond u / p / X / ? lives in the JSON protocol now.

static void printUsbStatus() {
  serialLockTake();
  Serial.println();
  Serial.println("Device pool (joystick HID slots):");
  for (uint8_t i = 0; i < devicePoolSize(); ++i) {
    GenericJoystickHID* d = devicePoolSlot(i);
    Serial.print("  slot ");
    Serial.print(i);
    Serial.print(": ");
    if (d->connected()) {
      Serial.print("addr=");
      Serial.print(d->address());
      Serial.print(" VID=0x");
      Serial.print(d->vid(), HEX);
      Serial.print(" PID=0x");
      Serial.print(d->pid(), HEX);
      Serial.print(" \"");
      Serial.print(d->productName());
      Serial.print("\" buttons=0x");
      Serial.print(d->buttons(), HEX);
      Serial.print(" axes=[");
      for (uint8_t j = 0; j < d->axisCount(); ++j) {
        if (j)
          Serial.print(',');
        Serial.print(d->axis(j));
      }
      Serial.print("] ac=");
      Serial.print(d->axisCount());
      Serial.print(" bc=");
      Serial.println(d->buttonCount());
    } else {
      Serial.println("idle");
    }
  }

  // EHCI channel count — ESP32-S3 has a hard ceiling of 8 channels shared
  // across every device's interrupt endpoint plus hub control pipes. Get
  // close to that ceiling and new device enumeration starts failing in
  // ways that look a lot like the Teensy hub bug we just left behind, so
  // make it visible at a glance.
  Serial.print("USB endpoint channels in use: ");
  Serial.print(devicePoolEndpointChannels());
  Serial.print(" / ");
  Serial.println(devicePoolMaxChannels());

  Serial.print("Pedals (Serial1): ");
  Serial.print(getPedalsStateName());
  Serial.print(" | T=");
  Serial.print(((uint32_t)getPedalThrottle() * 100) / 65535);
  Serial.print("% B=");
  Serial.print(((uint32_t)getPedalBrake() * 100) / 65535);
  Serial.print("% C=");
  Serial.print(((uint32_t)getPedalClutch() * 100) / 65535);
  Serial.print("% H=");
  Serial.print(((uint32_t)getPedalHandbrake() * 100) / 65535);
  Serial.println("%");
  Serial.println();

  // Per-device interface / endpoint / channel breakdown — the detail behind
  // the "channels in use" summary above. (Recursive lock: this nested
  // take/give keeps the whole `u` dump atomic against the USB task.)
  devicePoolDumpUsbDetail();
  serialLockGive();
}

static void printHelp() {
  serialLockTake();
  Serial.println();
  Serial.println("Minimal serial CLI (everything else is JSON / WebSerial):");
  Serial.println("  u    print USB host + device pool status");
  Serial.println("  p    force pedal handshake reset (back to Step 0)");
  Serial.println("  X    CPU soft-reset (ESP.restart)");
  Serial.println("  ?    this help");
  Serial.println();
  Serial.println("JSON commands: send a single line starting with '{' to the");
  Serial.println("same UART. See protocol.cpp for the full list, or use the");
  Serial.println("webconfig WebSerial UI.");
  Serial.println();
  serialLockGive();
}

// CliCharCallback registered with the protocol layer. The layer forwards
// any non-'{' byte here.
static void handleCliChar(char c) {
  switch (c) {
    case 'u':
    case 'U':
      printUsbStatus();
      break;
    case 'p':
    case 'P':
      pedalsForceReset();
      break;
    case 'X':
      serialLockTake();
      Serial.println("[CPU] Manual soft-reset.");
      Serial.flush();
      serialLockGive();
      delay(50);
      ESP.restart();
      break;
    case '?':
    case 'h':
    case 'H':
      printHelp();
      break;
    default:
      serialLockTake();
      Serial.print("Unknown CLI char '");
      Serial.print(c);
      Serial.println("' (type ? for help, or send a JSON command)");
      serialLockGive();
      break;
  }
}

// ---------------- Setup / loop ----------------

static void setupPwmPin(uint8_t pin) {
  // Use the explicit LEDC API rather than analogWrite() — the v3
  // analogWrite path silently no-ops analogWriteFrequency calls before
  // attach and ends up running channels at unintended frequencies. With
  // ledcAttach we get a definitive bool back and can log if the requested
  // freq/resolution combo is infeasible on whatever clock IDF picks.
  if (!ledcAttach(pin, PWM_FREQ_HZ, PWM_BITS)) {
    Serial.print("[PWM] ledcAttach FAILED on pin ");
    Serial.print(pin);
    Serial.print(" (freq=");
    Serial.print(PWM_FREQ_HZ);
    Serial.print("Hz, bits=");
    Serial.print(PWM_BITS);
    Serial.println(")");
    return;
  }
  ledcWrite(pin, 0);
}

static void printBanner() {
  serialLockTake();
  Serial.println();
#if defined(CONFIG_IDF_TARGET_ESP32P4)
  Serial.println("=== fanadapter — USB HID → Fanatec wheelbase (ESP32-P4) ===");
#else
  Serial.println("=== fanadapter — USB HID → Fanatec wheelbase (ESP32-S3) ===");
#endif
  Serial.print("=== fw 0.8.0  protocol 5  config v");
  Serial.print(CONFIG_VERSION);
  Serial.println(" ===");
  Serial.println("Configure via WebSerial (see webconfig/). '?' for the CLI.");
  Serial.println();
  serialLockGive();
}

void setup() {
  Serial.begin(115200);
  // No DTR equivalent on UART; small fixed wait so the host's terminal /
  // WebSerial client catches the banner even if it opens the port a beat
  // after boot.
  delay(500);

  // H-pattern X/Y DACs (PWM through 1k+1µF RC) and handbrake DAC.
  setupPwmPin(PIN_X);
  setupPwmPin(PIN_Y);
  setupPwmPin(PIN_HANDBRAKE);

  // Sequential pins — open-drain, idle high (wheelbase has internal
  // pull-up). NB: ESP32 Arduino uses OUTPUT_OPEN_DRAIN (underscore),
  // distinct from Teensyduino's OUTPUT_OPENDRAIN — wrong macro silently
  // gives push-pull and the sequential output never floats high again.
  pinMode(PIN_SEQ_UP, OUTPUT_OPEN_DRAIN);
  pinMode(PIN_SEQ_DOWN, OUTPUT_OPEN_DRAIN);
  digitalWrite(PIN_SEQ_UP, HIGH);
  digitalWrite(PIN_SEQ_DOWN, HIGH);

  // Order: pedals first (initializes Serial1 with the 2 s warmup),
  // mapping next (loads NVS and snaps DACs / pedal stream to neutral),
  // protocol after, then USB host. We start the host last so the
  // EspUsbHost task is the last thing spawned — fewer race windows
  // before the main loop is ticking.
  pedalsInit();
  mappingInit();
  protocolInit();
  protocolSetCliCallback(&handleCliChar);
  devicePoolBegin();

  printBanner();
}

void loop() {
  // EspUsbHost runs its own FreeRTOS task — no need to call a Task()-like
  // tick here. The device pool's callbacks update slot state asynchronously
  // and mappingTick() reads it on each loop pass.
  protocolTick(); // drain Serial → dispatch JSON / CLI, emit live events
  mappingTick();  // read device pool → drive DACs / sequential / pedal stream
  pedalsUpdate(); // Fanatec pedal-port UART state machine
}
