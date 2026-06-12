// WebSerial JSON protocol layer.
//
// Drains bytes from `Serial` (UART0 on ESP32-S3, routed through the
// onboard USB-Serial bridge on the dev board's UART port) and dispatches
// them to either the JSON command parser (line starts with '{',
// accumulate until '\n') or the single-char CLI callback (everything
// else, processed immediately). Emits async events (device_attached/
// detached, live, outputs) when subscribed via live_inputs / live_outputs
// commands.

#pragma once
#include <Arduino.h>

void protocolInit();
void protocolTick(); // call every loop iteration

// The protocol layer forwards non-JSON bytes to this callback so the main
// sketch can keep the minimal CLI (X / p / u / ? / h) alongside JSON.
typedef void (*CliCharCallback)(char c);
void protocolSetCliCallback(CliCharCallback cb);

// ---------------- Serial output lock ----------------
//
// `Serial` (UART0) is written from TWO FreeRTOS tasks: the main loop
// (JSON responses, live telemetry, CLI prints) and the USB host task
// (device_pool's connect/disconnect diagnostics, run from EspUsbHost's
// callbacks). Arduino's HardwareSerial only locks per individual write()
// call, so a multi-statement print SEQUENCE from one task can interleave
// mid-line with the other task's output — which both garbles the human
// log AND corrupts the JSON event lines webconfig parses (a spliced '{'
// turns a valid event into a dropped one).
//
// Take this recursive lock around any multi-statement Serial print block
// that can run concurrently with the other task. Null-safe before
// protocolInit() (the boot phase is single-task, so the mutex needn't
// exist yet). Recursive so a locked block that calls emit() doesn't
// self-deadlock.
void serialLockTake();
void serialLockGive();
