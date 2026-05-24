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

// HORI Pokken Pad HID report descriptor: 16 buttons, 1 hat (4-bit), 4 axes,
// 1 vendor-defined input byte, and an 8-byte vendor output report.
//
// This is the FULL canonical Pokken descriptor (matching the reference HORI
// pad and the FeralAI gist), giving an 8-byte INPUT report. The RP2040
// firmware shipped a truncated 7-byte variant that still sent 8 bytes - the
// Switch tolerates that mismatch, but it is a spec violation: a host that
// validates report length against the descriptor (e.g. Windows raw HID) will
// not surface the oversized reports. Declaring the full 8 bytes here makes
// declared == sent and matches the real device.
uint8_t const desc_hid_report[] = {
    0x05, 0x01,        // Usage Page (Generic Desktop)
    0x09, 0x05,        // Usage (Game Pad)
    0xA1, 0x01,        // Collection (Application)
    0x15, 0x00,        //   Logical Minimum (0)
    0x25, 0x01,        //   Logical Maximum (1)
    0x35, 0x00,        //   Physical Minimum (0)
    0x45, 0x01,        //   Physical Maximum (1)
    0x75, 0x01,        //   Report Size (1)
    0x95, 0x10,        //   Report Count (16)
    0x05, 0x09,        //   Usage Page (Button)
    0x19, 0x01,        //   Usage Minimum (Button 1)
    0x29, 0x10,        //   Usage Maximum (Button 16)
    0x81, 0x02,        //   Input (Data,Var,Abs)        -> 2 bytes (16 buttons)
    0x05, 0x01,        //   Usage Page (Generic Desktop)
    0x25, 0x07,        //   Logical Maximum (7)
    0x46, 0x3B, 0x01,  //   Physical Maximum (315)
    0x75, 0x04,        //   Report Size (4)
    0x95, 0x01,        //   Report Count (1)
    0x65, 0x14,        //   Unit (Eng Rot: Degrees)
    0x09, 0x39,        //   Usage (Hat switch)
    0x81, 0x42,        //   Input (Data,Var,Abs,Null)   -> 4 bits (hat)
    0x65, 0x00,        //   Unit (None)
    0x95, 0x01,        //   Report Count (1)
    0x81, 0x01,        //   Input (Const)               -> 4 bits padding (= 1 byte w/ hat)
    0x26, 0xFF, 0x00,  //   Logical Maximum (255)
    0x46, 0xFF, 0x00,  //   Physical Maximum (255)
    0x09, 0x30,        //   Usage (X)
    0x09, 0x31,        //   Usage (Y)
    0x09, 0x32,        //   Usage (Z)
    0x09, 0x35,        //   Usage (Rz)
    0x75, 0x08,        //   Report Size (8)
    0x95, 0x04,        //   Report Count (4)
    0x81, 0x02,        //   Input (Data,Var,Abs)        -> 4 bytes (axes)
    0x06, 0x00, 0xFF,  //   Usage Page (Vendor Defined 0xFF00)
    0x09, 0x20,        //   Usage (0x20)
    0x95, 0x01,        //   Report Count (1)
    0x81, 0x02,        //   Input (Data,Var,Abs)        -> 1 byte (vendor) = 8 bytes total
    0x0A, 0x21, 0x26,  //   Usage (0x2621)
    0x95, 0x08,        //   Report Count (8)
    0x91, 0x02,        //   Output (Data,Var,Abs)       -> 8-byte vendor output
    0xC0               // End Collection
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
