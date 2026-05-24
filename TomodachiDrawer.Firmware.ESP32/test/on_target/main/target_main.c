// SPDX-License-Identifier: GPL-3.0-or-later
// TomodachiDrawer V2 — Copyright (C) 2026 Xoan <github.com/xoaninc>
// Modified version of TomodachiDrawer (original (C) Lucas7yoshi, GPL-3.0).
//
// On-target test runner: flash this to the ESP32-S3 and read the
// USB-Serial-JTAG console. It runs the exact same tests as the host build
// and prints "ALL TESTS PASSED" / "TESTS FAILED" plus a check/failure count.

#include "tdld_tests.h"

#include "freertos/FreeRTOS.h"
#include "freertos/task.h"
#include "esp_log.h"

static const char *TAG = "tdld_test";

void app_main(void) {
    int failures = tdld_run_all_tests();
    ESP_LOGI(TAG, "on-target test run complete, failures=%d", failures);
    // Keep the task alive so the console stays attached for reading.
    while (1) {
        ESP_LOGI(TAG, "%s", failures == 0 ? "RESULT: PASS" : "RESULT: FAIL");
        vTaskDelay(pdMS_TO_TICKS(2000));
    }
}
