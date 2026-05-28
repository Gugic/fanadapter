// WebSerial JSON protocol layer.
//
// Drains bytes from the USB CDC Serial port and dispatches them to either
// the JSON command parser (line starts with '{', accumulate until '\n') or
// the single-char CLI callback (everything else, processed immediately).
// Emits async events (device_attached/detached, live, outputs) when
// subscribed via live_inputs / live_outputs commands.

#pragma once
#include <Arduino.h>

void protocolInit();
void protocolTick(); // call every loop iteration

// The protocol layer forwards non-JSON bytes to this callback so the main
// sketch can keep the minimal CLI (X / p / u / ? / h) alongside JSON.
typedef void (*CliCharCallback)(char c);
void protocolSetCliCallback(CliCharCallback cb);
