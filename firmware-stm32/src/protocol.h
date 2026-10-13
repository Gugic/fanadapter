// Line-based JSON protocol over the console (USART1 + CDC) — the WebSerial contract.
// Config, telemetry, output tests/overrides, DFU and diagnostics. Shapes are mirrored in
// webconfig/src/lib/{serial.ts,types.ts} and Fanadapter.Core/{Protocol,Model}.cs.
#pragma once
#include <stdint.h>

void protocol_init(void);
void protocol_handle_line(const char *line); // a complete '{...}' command line from the console
void protocol_tick(uint32_t now_ms);         // emit enabled attach/detach, live input and output events
