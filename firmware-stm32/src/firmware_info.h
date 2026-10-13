// Firmware and native USB identity shared by the protocol, descriptors, and boot banner.
// The release workflow reads FW_VER; Pages uses it to select the released image.
#pragma once

#define FW_NAME "fanadapter-stm32"
#define FW_VER  "0.6.2"
#define USB_PRODUCT_NAME "Fanadapter v" FW_VER
#define USB_VID 0x1209
#define USB_PID 0xFA00
