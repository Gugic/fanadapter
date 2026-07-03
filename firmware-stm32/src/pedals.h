// CSL Elite V2 pedal-port UART emulator on USART2 (PA2 = TX, PA3 = RX). C port of the Teensy
// firmware/pedals.{h,cpp} (which ran on Serial3). This is the sink that makes the pedal/handbrake
// channel values ELECTRICALLY reach the Fanatec wheelbase: the H-pattern gears go out the DAC
// (outputs.c) and sequential shifts out the open-drain pins, but throttle/brake/clutch/handbrake
// only reach the wheelbase through this 100 Hz UART stream.
//
// The wheelbase enumerates the emulated device as "ClubSport Pedals V3". Protocol: a 250000-baud
// digital handshake (0x0A->0x1A, 0x05->0x15) then a switch to 115200 baud, a lenient any-order
// 12-byte query/ack exchange, and finally 100 Hz 12-byte 0x7B..0x7D pedal frames. See pedals.c for
// the full state-machine rationale (warmup window, lenient STEP2 collector, post-handshake quirk).
//
// 8N1, NOT the console's 8E1 — this peripheral talks to the wheelbase, not the ROM bootloader.
#pragma once
#include <stdbool.h>
#include <stdint.h>

// Init USART2 (PA2/PA3, 8N1) + the RX interrupt ring, arm the warmup drain, enter STEP0. Call once.
void pedals_init(void);

// Drive the handshake state machine and, once STREAMING, emit a pedal frame every 10 ms. Non-blocking
// (the 12-byte TX is a ~1 ms blocking UART write, gated to 100 Hz). Call LAST in the main loop, after
// mapping_tick() has refreshed the pedal levels — adding blocking work ahead of it slips the cadence.
void pedals_update(void);

// Re-arm the handshake from STEP0 right now, skipping the warmup (the `reset_pedals` command). For
// when the wheelbase desynced and isn't re-initiating on its own.
void pedals_force_reset(void);

// "INIT" / "HANDSHAKE_250K" / "HANDSHAKE_115K" / "STREAMING_115K" — for the `pedals_status` command.
const char *pedals_state_name(void);

// Latest pedal levels (0..65535) streamed in the next frame. Pushed by mapping_tick()'s update_axes.
void pedals_set_throttle(uint16_t v);
void pedals_set_brake(uint16_t v);
void pedals_set_clutch(uint16_t v);
void pedals_set_handbrake(uint16_t v);

// Current streamed levels (for `pedals_status`).
uint16_t pedals_get_throttle(void);
uint16_t pedals_get_brake(void);
uint16_t pedals_get_clutch(void);
uint16_t pedals_get_handbrake(void);
