// ESP32-S3 USB <-> UART bridge for the STM32H743 port — console AND hands-free re-flashing.
//
// The STM32 (firmware-stm32/) prints/receives its console on USART1 (PA9=TX, PA10=RX). Its only
// usable USB controller (OTG_FS) is taken by the USB host, so the console rides USART1, bridged to
// a PC COM port by this sketch. This is a fully transparent byte pipe in BOTH directions, so the
// same COM port also carries:
//   1. the line-JSON / CLI console (the device list, live inputs, config), and
//   2. UART-bootloader FLASHING — send the CLI command `dfu` to the STM32 and it jumps to its ROM
//      bootloader (AN2606), then STM32CubeProgrammer/stm32flash re-flashes over THIS bridge with no
//      BOOT0/RST buttons and without unplugging the hub from OTG_FS.
//
// 8E1, NOT 8N1: the STM32 ROM bootloader's USART1 format is fixed at 8 data bits / EVEN parity / 1
// stop. So the STM32 app console ALSO runs 8E1 (parity is transparent to the byte stream) and this
// bridge runs 8E1 on both links — one config serves console + flashing. Open the PC COM port at
// 115200 8E1 (PowerShell: `Parity=Even, DataBits=8, StopBits=One`).
//
// Wiring (BOTH directions are required now — the inbound leg carries commands + the flash stream):
//   STM32 PA9  (USART1 TX) -> ESP32-S3 RX_PIN   (board -> PC)
//   STM32 PA10 (USART1 RX) <- ESP32-S3 TX_PIN   (PC -> board: CLI + bootloader flashing)
//   GND <-> GND                                  (required common ground)
//   The S3's 5V can power the STM32 board's 5V rail.
//
// Flash THIS sketch onto the S3 over its CH340 UART port (default config: `Serial` = UART0 = CH340):
//   arduino-cli compile --fqbn esp32:esp32:esp32s3 tools/esp32s3_uart_bridge
//   arduino-cli upload  --fqbn esp32:esp32:esp32s3 -p COMxx tools/esp32s3_uart_bridge
//
// Hands-free STM32 re-flash (once this sketch + a `dfu`-capable STM32 firmware are installed):
//   1) write "dfu\n" to the COM port (115200 8E1)               -> STM32 enters ROM bootloader
//   2) STM32_Programmer_CLI -c port=COMxx br=115200 P=EVEN -w firmware.bin 0x08000000 -v -g 0x08000000
// (Note: opening the CH340 COM port asserts DTR, which resets the S3; harmless — the bridge resumes
//  in a few hundred ms before the flasher's autobaud retries. If a tool's reset sequence is too
//  aggressive, switch the S3 to its NATIVE USB port and mirror CDC line coding instead.)

// Pick two FREE GPIOs on your S3 board — avoid the USB D+/D- pins (19/20), the strapping pins
// (0, 3, 45, 46) and anything wired to onboard peripherals. 17/18 are free on most S3 devkits.
static const int RX_PIN = 18; // <- connect to STM32 PA9 (USART1 TX)
static const int TX_PIN = 17; // -> connect to STM32 PA10 (USART1 RX)

static const unsigned long BAUD = 115200;

void setup() {
  // Roomy RX buffers so CubeProgrammer's block writes don't overflow during a flash.
  Serial.setRxBufferSize(2048);
  Serial1.setRxBufferSize(2048);
  Serial.begin(BAUD, SERIAL_8E1);                  // CH340 UART0 -> PC (the COM port you open)
  Serial1.begin(BAUD, SERIAL_8E1, RX_PIN, TX_PIN); // hardware UART <-> STM32 USART1
}

void loop() {
  // STM32 -> PC (console output, bootloader responses)
  while (Serial1.available()) Serial.write(Serial1.read());
  // PC -> STM32 (CLI commands, bootloader command/data stream)
  while (Serial.available()) Serial1.write(Serial.read());
}
