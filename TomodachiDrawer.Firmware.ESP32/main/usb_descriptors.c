// USB descriptors for the HORI Pokken Pad the Switch (1 & 2) accepts as a
// wired Pro Controller. Byte-for-byte the same identity as the RP2040
// firmware (VID 0x0F0D / PID 0x0092, "POKKEN CONTROLLER").
//
// IMPORTANT esp_tinyusb 2.x difference vs the RP2040 build: the component
// supplies tud_descriptor_device_cb, tud_descriptor_configuration_cb AND
// tud_descriptor_string_cb itself (it reads the arrays we hand it through
// tinyusb_config_t.descriptor.*). Defining any of those three here would be a
// duplicate symbol. Only the HID *report* descriptor callback is still ours.

#include "tusb.h"

#define USB_VID 0x0F0D
#define USB_PID 0x0092

// HID polling interval (bInterval) in ms. The RP2040 build kept this in its
// own tusb_config.h; here esp_tinyusb owns tusb_config.h, so we define it
// locally just for the descriptor below.
#define HID_BINTERVAL_MS 8

#define EPNUM_HID 0x81

// HORI Pokken Pad HID report descriptor: 16 buttons, 1 hat (4-bit), 4 axes.
uint8_t const desc_hid_report[] = {
    0x05, 0x01, 0x09, 0x05, 0xA1, 0x01, 0x15, 0x00,
    0x25, 0x01, 0x35, 0x00, 0x45, 0x01, 0x75, 0x01,
    0x95, 0x10, 0x05, 0x09, 0x19, 0x01, 0x29, 0x10,
    0x81, 0x02, 0x05, 0x01, 0x25, 0x07, 0x46, 0x3b,
    0x01, 0x75, 0x04, 0x95, 0x01, 0x65, 0x14, 0x09,
    0x39, 0x81, 0x42, 0x65, 0x00, 0x95, 0x01, 0x81,
    0x01, 0x26, 0xff, 0x00, 0x46, 0xff, 0x00, 0x09,
    0x30, 0x09, 0x31, 0x09, 0x32, 0x09, 0x35, 0x75,
    0x08, 0x95, 0x04, 0x81, 0x02, 0xc0
};

uint8_t const desc_device[] = {
    18, TUSB_DESC_DEVICE, 0x00, 0x02,
    0x00, 0x00, 0x00,               // Class/SubClass/Protocol: defined at interface level
    CFG_TUD_ENDPOINT0_SIZE,
    (uint8_t)(USB_VID & 0xff), (uint8_t)(USB_VID >> 8),
    (uint8_t)(USB_PID & 0xff), (uint8_t)(USB_PID >> 8),
    0x00, 0x01, 0x01, 0x02, 0x00, 0x01
};

uint8_t const desc_configuration[] = {
    // 1 interface (HID only)
    TUD_CONFIG_DESCRIPTOR(1, 1, 0, (TUD_CONFIG_DESC_LEN + TUD_HID_DESC_LEN), TUSB_DESC_CONFIG_ATT_REMOTE_WAKEUP, 500),
    TUD_HID_DESCRIPTOR(0, 1, HID_ITF_PROTOCOL_NONE, sizeof(desc_hid_report), EPNUM_HID, 64, HID_BINTERVAL_MS)
};

// index 0 must be the language descriptor (raw 2 bytes); 1+ are ASCII and are
// converted to UTF-16 by esp_tinyusb's tud_descriptor_string_cb.
char const *string_desc_arr[] = {
    (const char[]) { 0x09, 0x04 }, // 0: English (0x0409)
    "HORI CO., LTD.",              // 1: Manufacturer
    "POKKEN CONTROLLER",           // 2: Product
    "123456",                      // 3: Serial
    "Tomodachi Gamepad",           // 4: HID Interface Name
};

// The HID report descriptor callback stays ours (esp_tinyusb does not provide
// it — it is HID-class specific).
uint8_t const *tud_hid_descriptor_report_cb(uint8_t instance) {
    (void)instance;
    return desc_hid_report;
}
