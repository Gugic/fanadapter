// ESP32-S3 UART bridge retained for STM32H743 recovery and historical FK743M3 bring-up.
// The current WeAct build exposes native CDC + ROM DFU on USB-C and needs no bridge.
// This sketch forwards bytes in both directions between its CH340 port and USART1:
//   STM32 PA9  (TX) -> S3 RX_PIN
//   STM32 PA10 (RX) <- S3 TX_PIN
//   common GND
// Both links use 115200 8E1, matching the fallback app console and ROM USART bootloader.
//
// From firmware-stm32/:
//   arduino-cli compile --fqbn esp32:esp32:esp32s3 tools/esp32s3_uart_bridge
//   arduino-cli upload --fqbn esp32:esp32:esp32s3 -p COMxx tools/esp32s3_uart_bridge
//
// For STM32 UART recovery, use tools/flash.ps1 and hardware BOOT0/RST entry. The current
// token/reset software-entry path is validated for USB DFU, not hands-free UART flashing.
// Some flash tools assert DTR/RTS and reset the S3 bridge; stm32_uart_flash.py avoids that.
// Modern webconfig/SimHub port pickers intentionally exclude this generic UART bridge.

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
