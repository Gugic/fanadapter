// Persistent config storage — the STM32H743 internal-flash replacement for the Teensy's EEPROM.
//
// One dedicated 128 KB flash sector holds the serialized Config blob. Load is a plain memcpy from
// the memory-mapped sector; save erases the sector and reprograms it in 256-bit flashwords. See
// config_store.c for the deliberate sector choice (bank-1 sector 3) and its rationale.
#pragma once
#include <stdbool.h>
#include <stdint.h>

// Largest blob the store can hold: 36 × 32-byte flashwords. Config is 1132 B, padded up to 1152.
#define CONFIG_STORE_MAX 1152u

// Copy `len` bytes from the config flash sector into `dst`. Returns false if len exceeds the store.
// The caller validates magic/version/CRC — a never-written sector reads back as all-0xFF.
bool config_store_load(void *dst, uint32_t len);

// Erase the config sector and program `len` bytes (padded to a flashword multiple with 0xFF),
// then verify by read-back. Returns true only on a clean erase + program + verify. `len` must be
// <= CONFIG_STORE_MAX. Blocking; intended for rare, user-initiated save_config.
bool config_store_save(const void *src, uint32_t len);
