<#
.SYNOPSIS
  Build the STM32H743 firmware and flash it over the ESP32-S3 UART bootloader bridge.

.DESCRIPTION
  The middle-ground "hands-free-ish" flow: run this, and when it says so, press BOOT0+RST
  on the board. It auto-detects the ROM bootloader (--wait), flashes + verifies over the S3,
  then Go's into the freshly-flashed app. No CubeProgrammer, no DTR juggling, no USB-C swap.

  (Full hands-free isn't possible on this board: the H7 ROM only serves USART over the
  hardware boot path, the option-byte trick is banned, and BOOT0 has no pad to wire the S3 to.)

.EXAMPLE
  .\tools\flash.ps1                 # build + flash on COM16, press BOOT0+RST when prompted
  .\tools\flash.ps1 -NoBuild        # flash the existing build without rebuilding
  .\tools\flash.ps1 -Port COM7      # different bridge port
#>
param(
  [string]$Port = "COM16",
  [switch]$NoBuild,
  [switch]$MassErase,   # wipe the saved config too (default: bank-1 erase, config preserved)
  [int]$Wait = 30
)
$ErrorActionPreference = "Stop"

$toolsDir = $PSScriptRoot
$root     = Split-Path -Parent $toolsDir          # firmware-stm32/
$build    = Join-Path $root ".pio\build\weact_h743"
$elf      = Join-Path $build "firmware.elf"
$raw      = Join-Path $build "firmware_raw.bin"

if (-not $NoBuild) {
  Write-Host "[build] platformio run..." -ForegroundColor Cyan
  python -m platformio run -d $root -e weact_h743
  if ($LASTEXITCODE -ne 0) { throw "build failed" }
}

# Flash the CLEAN raw image from the .elf — NOT .pio/.../firmware.bin, which PlatformIO
# appends a 16-byte dfu-suffix to (fine for dfu-util, wrong for raw UART writes).
$objcopy = (Get-ChildItem "$env:USERPROFILE\.platformio\packages\toolchain-gccarmnoneeabi\bin\arm-none-eabi-objcopy.exe" |
            Select-Object -First 1).FullName
& $objcopy -O binary $elf $raw

Write-Host "[flash] >>> PRESS BOOT0 + RST on the board now <<<" -ForegroundColor Yellow
$eraseArg = if ($MassErase) { @("--mass-erase") } else { @() }
python (Join-Path $toolsDir "stm32_uart_flash.py") --port $Port --bin $raw --enter manual --wait $Wait --go @eraseArg
