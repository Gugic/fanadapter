#ifndef PEDALS_H
#define PEDALS_H

#include <Arduino.h>

// Initialize the pedal emulator module on Serial3 (pins 14/15)
void pedalsInit();

// Non-blocking update to be called in loop()
void pedalsUpdate();

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
