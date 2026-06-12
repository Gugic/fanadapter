#ifndef PEDALS_H
#define PEDALS_H

#include <Arduino.h>

// Initialize the pedal emulator module on Serial1 (PEDAL_TX/PEDAL_RX, see
// mapping.h). The state machine starts at 250000 baud (STEP0) and switches
// to 115200 after the wheelbase's two-byte handshake. A 2 s boot warmup
// drains stale bytes from a prior session before engaging.
void pedalsInit();

// Non-blocking update to be called in loop()
void pedalsUpdate();

// Force the pedal handshake state machine back to Step 0 (250000 baud).
// Useful for re-syncing without a chip reset when the wheelbase end of the
// protocol has been desynced (e.g. by a prior debug session).
void pedalsForceReset();

// Set pedal values in range 0..65535 (16-bit resolution)
void setPedalThrottle(uint16_t val);
void setPedalBrake(uint16_t val);
void setPedalClutch(uint16_t val);
void setPedalHandbrake(uint16_t val);

// Get current pedal values
uint16_t getPedalThrottle();
uint16_t getPedalBrake();
uint16_t getPedalClutch();
uint16_t getPedalHandbrake();

// State name for debugging
const char* getPedalsStateName();

#endif // PEDALS_H
