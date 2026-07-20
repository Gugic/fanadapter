// Internal-flash config store. See config_store.h.
#include "config_store.h"

#include <string.h>

#include "stm32h7xx_hal.h"

extern int console_printf(const char *fmt, ...);

// --- Sector choice: bank-2 sector 0 @ 0x08100000 (cross-bank from the executing code). -----------
//
// MUST be a DIFFERENT bank than the code. The H7 forbids reading a flash bank while it is being
// erased/programmed (read-while-write works only ACROSS the two banks). The firmware executes from
// bank 1 (image @ 0x08000000, ~55 KB), so the config sector MUST live in bank 2 — otherwise the
// CPU's instruction fetch during the 128 KB sector erase hits a busy bank and HARD-FAULTS. (An
// earlier bank-1 sector-3 placement did exactly that: every save_config froze the board.) With the
// sector in bank 2, instruction fetch from bank 1 continues throughout the erase.
//
// WHY bank-2 SECTOR 0 specifically:
//   * The chip is the 1 MB STM32H743VG, but the board json / linker declare the 2 MB VI part. The
//     CMSIS header hardcodes FLASH_BANK2_BASE = 0x08100000 (independent of size). On the VG, bank 2
//     is the 512 KB block 0x08100000..0x0817FFFF (sectors 0..3); sectors >= 4 (e.g. 0x08180000+,
//     the naive "top sector") do NOT exist on the VG. Sector 0 @ 0x08100000 is the FIRST, always-
//     present sector of bank 2 — the safe choice, verified live by reading 0x08100000 over the ROM
//     bootloader (distinct from the bank-1 image at 0x08000000, i.e. not aliased) before trusting it.
//   * Cross-bank from the image, so no overlap and no fetch stall.
//
// NOTE: the erase still BUSY-WAITS (FLASH_WaitForLastOperation), pausing the main loop for the erase
// duration — but the CPU keeps fetching, so it completes cleanly. save_config is rare/user-initiated;
// the wheelbase tolerates the few pedal frames skipped during it (measured erase ~ see PORT-STATUS).
#define CONFIG_FLASH_ADDR   0x08100000u
#define CONFIG_FLASH_BANK   FLASH_BANK_2
#define CONFIG_FLASH_SECTOR FLASH_SECTOR_0

bool config_store_load(void *dst, uint32_t len) {
  if (len > CONFIG_STORE_MAX) return false;
  memcpy(dst, (const void *)CONFIG_FLASH_ADDR, len);
  return true;
}

bool config_store_save(const void *src, uint32_t len) {
  if (len > CONFIG_STORE_MAX) return false;

  // Stage into a 32-byte-aligned buffer padded with 0xFF up to a whole flashword multiple — the H7
  // programs only complete 256-bit (32-byte) flashwords.
  static uint8_t __attribute__((aligned(32))) buf[CONFIG_STORE_MAX];
  memset(buf, 0xFF, sizeof(buf));
  memcpy(buf, src, len);
  const uint32_t words = (len + 31u) / 32u; // flashwords to program

  // Disable IRQs across the flash op: defensive against an ISR fetching/running mid-erase on the H7
  // (the erase busy-waits ~1 s — save_config is rare, so the brief USB/console pause is fine).
  __disable_irq();
  HAL_FLASH_Unlock();

  FLASH_EraseInitTypeDef ei = {
      .TypeErase    = FLASH_TYPEERASE_SECTORS,
      .Banks        = CONFIG_FLASH_BANK,
      .Sector       = CONFIG_FLASH_SECTOR,
      .NbSectors    = 1,
      .VoltageRange = FLASH_VOLTAGE_RANGE_3, // valid for any VDD >= 2.4 V; the board runs 3.3 V
  };
  uint32_t sector_err = 0;
  bool     ok         = (HAL_FLASHEx_Erase(&ei, &sector_err) == HAL_OK);

  for (uint32_t i = 0; ok && i < words; i++) {
    const uint32_t addr = CONFIG_FLASH_ADDR + i * 32u;
    // H7 FLASHWORD program: the 3rd argument is the ADDRESS of the 256-bit source (read as 8 words),
    // not a value. addr is 32-byte aligned (sector base is, and i*32 keeps it so).
    if (HAL_FLASH_Program(FLASH_TYPEPROGRAM_FLASHWORD, addr, (uint32_t)(uintptr_t)&buf[i * 32u]) !=
        HAL_OK)
      ok = false;
  }

  HAL_FLASH_Lock();
  __enable_irq();

  // No D-cache invalidate: this build runs with the D-cache DISABLED, so memory-mapped flash reads
  // are always coherent and an invalidate is unnecessary. (Calling SCB_InvalidateDCache_by_Addr()
  // with the cache off faulted the core right here — found via step traces during bring-up.) If
  // D-cache is ever enabled, re-add a guarded invalidate over [CONFIG_FLASH_ADDR, +words*32) here.
  if (ok && memcmp((const void *)CONFIG_FLASH_ADDR, buf, len) != 0) ok = false;
  return ok;
}
