// SPDX-License-Identifier: GPL-3.0-or-later
// TomodachiDrawer ESP32-S3 port — Copyright (C) 2026 Xoan <github.com/xoaninc>
// Modified version of TomodachiDrawer (original (C) Lucas7yoshi, GPL-3.0).
//
// TomodachiDrawer ESP32-S3 firmware entry point.
//
// Mirrors the RP2040 firmware's main(): bring up USB as a Pokken Pad, wait for
// the host, send a short neutral-input countdown so the Switch locks onto us,
// then stream the .tdld program stored in the dedicated flash partition as HID
// reports. The LED reflects state (idle/active/error/done) like the original.
//
// Note: once usb_init() runs, the ESP32-S3 USB peripheral becomes the HID
// device, so the USB-Serial-JTAG console is no longer available - logs before
// that point are visible, after it the device shows up as the Pokken Pad.

#include <string.h>

#include "freertos/FreeRTOS.h"
#include "freertos/task.h"
#include "esp_log.h"
#include "esp_partition.h"

#include "led_indicator.h"
#include "hid_gamepad.h"
#include "tdld_parser.h"

#define LED_GPIO 48  // ESP32-S3-DevKitC-1 onboard NeoPixel (rev v1.1+)

static const char *TAG = "tomodachi";

void app_main(void) {
    ESP_LOGI(TAG, "TomodachiDrawer ESP32-S3 starting");
    led_init(LED_GPIO);
    led_idle();
    usb_init();

    // Wait for the host (Switch or PC) to enumerate and mount us.
    hid_wait_mounted();

    // Three-blink countdown sending neutral inputs, same as the RP2040 fw:
    // gives the Switch time to acknowledge us so playback isn't off by one.
    const uint8_t neutral[8] = {0x00, 0x00, DPAD_NEUTRAL, STICK_CENTER,
                                STICK_CENTER, STICK_CENTER, STICK_CENTER, 0x00};
    for (int i = 0; i < 3; i++) {
        led_set_rgb(127, 127, 0);
        hid_send_report(neutral);
        vTaskDelay(pdMS_TO_TICKS(500));
        led_set_rgb(0, 0, 0);
        hid_send_report(neutral);
        vTaskDelay(pdMS_TO_TICKS(500));
    }

    // Locate the 1MB tdld data partition and memory-map it (equivalent to the
    // RP2040 reading XIP_BASE + 1MB).
    const esp_partition_t *part = esp_partition_find_first(
        ESP_PARTITION_TYPE_DATA, ESP_PARTITION_SUBTYPE_ANY, "tdld");
    if (part == NULL) {
        led_error_flash(2000);  // very slow blink: partition missing
    }

    const void *mapped = NULL;
    esp_partition_mmap_handle_t map_handle;
    if (esp_partition_mmap(part, 0, part->size, ESP_PARTITION_MMAP_DATA,
                           &mapped, &map_handle) != ESP_OK) {
        led_error_flash(2000);
    }

    tdld_ctx_t ctx;
    if (!tdld_init(&ctx, (const uint8_t *)mapped, part->size)) {
        led_error_flash(250);   // fast blink: bad magic / version / no data
    }

    // Playback loop: pull one step at a time, send it, hold for its delay.
    tdld_step_t step;
    while (true) {
        tdld_next(&ctx, &step);
        if (step.error) {
            led_error_flash(5000);  // very slow: mid-stream corruption
        }
        if (step.eof) {
            break;
        }
        hid_send_report(step.report);
        // Green while any button is held, dim white when idle (mirrors RP2040).
        if (step.report[0] != 0 || step.report[1] != 0) {
            led_active();
        } else {
            led_idle();
        }
        if (step.delay_ms) {
            vTaskDelay(pdMS_TO_TICKS(step.delay_ms));
        }
    }

    // Done: rainbow forever until unplug/replug (same as the RP2040 fw).
    led_rainbow_forever();
}
