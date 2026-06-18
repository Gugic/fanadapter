// USB *device* descriptors for the CDC serial console exposed on the OTG_HS port
// (PB14/PB15 = rhport 1). On a board where OTG_HS is broken out as a real USB connector,
// plugging the host PC into it makes the firmware's device-enumeration log show up as a
// COM port — the recommended, bridge-free console for the open-source build. (On the
// FK743M3 bring-up board OTG_HS is unwired, so this never enumerates and the parallel
// USART1 console in main.c carries the same output instead.)
//
// The wheel devices themselves are handled by the *host* stack on OTG_FS (rhport 0).

#include <string.h>
#include "tusb.h"

// Open-source VID/PID block (pid.codes). Fine for a dev console.
#define USB_VID 0x1209
#define USB_PID 0xFA00
#define USB_BCD 0x0200

//--------------------------------------------------------------------
// Device descriptor
//--------------------------------------------------------------------
tusb_desc_device_t const desc_device = {
    .bLength            = sizeof(tusb_desc_device_t),
    .bDescriptorType    = TUSB_DESC_DEVICE,
    .bcdUSB             = USB_BCD,
    // Use IAD so Windows binds the CDC-ACM (usbser) driver automatically.
    .bDeviceClass       = TUSB_CLASS_MISC,
    .bDeviceSubClass    = MISC_SUBCLASS_COMMON,
    .bDeviceProtocol    = MISC_PROTOCOL_IAD,
    .bMaxPacketSize0    = CFG_TUD_ENDPOINT0_SIZE,
    .idVendor           = USB_VID,
    .idProduct          = USB_PID,
    .bcdDevice          = 0x0100,
    .iManufacturer      = 0x01,
    .iProduct           = 0x02,
    .iSerialNumber      = 0x03,
    .bNumConfigurations = 0x01,
};

uint8_t const *tud_descriptor_device_cb(void) {
  return (uint8_t const *)&desc_device;
}

//--------------------------------------------------------------------
// Configuration descriptor
//--------------------------------------------------------------------
enum {
  ITF_NUM_CDC = 0,
  ITF_NUM_CDC_DATA,
  ITF_NUM_TOTAL,
};

#define EPNUM_CDC_NOTIF 0x81
#define EPNUM_CDC_OUT   0x02
#define EPNUM_CDC_IN    0x82

#define CONFIG_TOTAL_LEN (TUD_CONFIG_DESC_LEN + TUD_CDC_DESC_LEN)

uint8_t const desc_fs_configuration[] = {
    // config number, interface count, string index, total length, attribute, power (mA)
    TUD_CONFIG_DESCRIPTOR(1, ITF_NUM_TOTAL, 0, CONFIG_TOTAL_LEN, 0x00, 100),
    // interface number, string index, notif EP, notif size, data OUT EP, data IN EP, data EP size
    TUD_CDC_DESCRIPTOR(ITF_NUM_CDC, 4, EPNUM_CDC_NOTIF, 8, EPNUM_CDC_OUT, EPNUM_CDC_IN, 64),
};

uint8_t const *tud_descriptor_configuration_cb(uint8_t index) {
  (void)index;
  return desc_fs_configuration;
}

//--------------------------------------------------------------------
// String descriptors
//--------------------------------------------------------------------
enum { STRID_LANGID = 0, STRID_MANUFACTURER, STRID_PRODUCT, STRID_SERIAL, STRID_CDC_ITF };

static char const *string_desc_arr[] = {
    [STRID_LANGID]       = (const char[]){0x09, 0x04}, // English (0x0409)
    [STRID_MANUFACTURER] = "fanadapter",
    [STRID_PRODUCT]      = "Fanadapter STM32 enumerator",
    [STRID_SERIAL]       = "0001",
    [STRID_CDC_ITF]      = "fanadapter console",
};

static uint16_t _desc_str[32];

uint16_t const *tud_descriptor_string_cb(uint8_t index, uint16_t langid) {
  (void)langid;
  size_t chr_count;

  if (index == STRID_LANGID) {
    memcpy(&_desc_str[1], string_desc_arr[STRID_LANGID], 2);
    chr_count = 1;
  } else {
    if (index >= sizeof(string_desc_arr) / sizeof(string_desc_arr[0])) return NULL;
    const char *str = string_desc_arr[index];
    chr_count = strlen(str);
    if (chr_count > 31) chr_count = 31;
    for (size_t i = 0; i < chr_count; i++) _desc_str[1 + i] = (uint16_t)str[i];
  }

  _desc_str[0] = (uint16_t)((TUSB_DESC_STRING << 8) | (2 * chr_count + 2));
  return _desc_str;
}
