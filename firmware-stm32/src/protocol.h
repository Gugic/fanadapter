// Line-based JSON protocol over the console (USART1 + CDC) — the WebSerial contract.
// M1 scope: version, list_devices, live_inputs toggle, and the device_attached/device_detached/live
// events. Commands/event shapes match webconfig/src/lib/{serial.ts,types.ts} exactly.
#pragma once
#include <stdint.h>

void protocol_init(void);
void protocol_handle_line(const char *line); // a complete '{...}' command line from the console
void protocol_tick(uint32_t now_ms);         // emit device attach/detach + rate-limited live events
