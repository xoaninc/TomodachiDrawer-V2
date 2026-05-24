// SPDX-License-Identifier: GPL-3.0-or-later
// TomodachiDrawer ESP32-S3 port — Copyright (C) 2026 Xoan <github.com/xoaninc>
// Modified version of TomodachiDrawer (original (C) Lucas7yoshi, GPL-3.0).

#include "hid_gamepad.h"

#include <string.h>

#include "freertos/FreeRTOS.h"
#include "freertos/task.h"
#include "tinyusb.h"
#include "tinyusb_default_config.h"
#include "class/hid/hid_device.h"

// Descriptor arrays defined in usb_descriptors.c.
extern uint8_t const desc_device[];
extern uint8_t const desc_configuration[];
extern char const  *string_desc_arr[];

#define STRING_DESC_COUNT 5

// Neutral report mirrors the parser's neutral state:
// [Btn1, Btn2, DPad(neutral=8), LX, LY, RX, RY, Pad] with sticks centred.
static uint8_t s_last_report[8] = {0x00, 0x00, 0x08, 128, 128, 128, 128, 0x00};

void usb_init(void) {
    // TINYUSB_DEFAULT_CONFIG() fills in port/phy/task defaults that the 2.x
    // driver requires; we then patch in our descriptors. Field names verified
    // against managed_components/espressif__esp_tinyusb/include/tinyusb.h
    // (tinyusb_desc_config_t).
    tinyusb_config_t tusb_cfg = TINYUSB_DEFAULT_CONFIG();
    tusb_cfg.descriptor.device            = (const tusb_desc_device_t *)desc_device;
    tusb_cfg.descriptor.full_speed_config = desc_configuration;
    tusb_cfg.descriptor.string            = string_desc_arr;
    tusb_cfg.descriptor.string_count      = STRING_DESC_COUNT;
    ESP_ERROR_CHECK(tinyusb_driver_install(&tusb_cfg));
}

void hid_wait_mounted(void) {
    // esp_tinyusb runs tud_task() in its own FreeRTOS task, so we just poll
    // the mounted flag rather than pumping the stack ourselves.
    while (!tud_mounted()) {
        vTaskDelay(pdMS_TO_TICKS(10));
    }
}

void hid_send_report(const uint8_t report[8]) {
    while (!tud_hid_ready()) {
        vTaskDelay(pdMS_TO_TICKS(1));
    }
    memcpy(s_last_report, report, 8);
    tud_hid_report(0, report, 8);
}

// --- TinyUSB HID class callbacks (ours; esp_tinyusb does not provide these) ---

uint16_t tud_hid_get_report_cb(uint8_t itf, uint8_t id, hid_report_type_t type,
                               uint8_t *buf, uint16_t len) {
    (void)itf;
    (void)id;
    if (type == HID_REPORT_TYPE_INPUT) {
        uint16_t size = (uint16_t)sizeof(s_last_report);
        if (size > len) size = len;
        memcpy(buf, s_last_report, size);
        return size;
    }
    return 0;
}

void tud_hid_set_report_cb(uint8_t itf, uint8_t id, hid_report_type_t type,
                           uint8_t const *buf, uint16_t len) {
    (void)itf;
    (void)id;
    (void)type;
    (void)buf;
    (void)len;
}
