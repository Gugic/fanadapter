#include <avr/pgmspace.h>
#include <usb_names.h>

#define MANUFACTURER_NAME {'f','a','n','a','d','a','p','t','e','r'}
#define MANUFACTURER_NAME_LEN 10

#define PRODUCT_NAME {'F','a','n','a','d','a','p','t','e','r',' ','v','0','.','3','.','0'}
#define PRODUCT_NAME_LEN 17

// Override the default weak structures in the Teensyduino core
PROGMEM struct usb_string_descriptor_struct usb_string_manufacturer_name = {
  2 + MANUFACTURER_NAME_LEN * 2,
  3,
  MANUFACTURER_NAME
};

PROGMEM struct usb_string_descriptor_struct usb_string_product_name = {
  2 + PRODUCT_NAME_LEN * 2,
  3,
  PRODUCT_NAME
};
