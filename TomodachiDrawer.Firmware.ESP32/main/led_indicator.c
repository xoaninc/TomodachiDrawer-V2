// SPDX-License-Identifier: GPL-3.0-or-later
// TomodachiDrawer ESP32-S3 port — Copyright (C) 2026 Xoan <github.com/xoaninc>
// Modified version of TomodachiDrawer (original (C) Lucas7yoshi, GPL-3.0).

#include "led_indicator.h"

#include "freertos/FreeRTOS.h"
#include "freertos/task.h"
#include "led_strip.h"
#include "esp_err.h"

// Match the RP2040 firmware's visual constants for parity.
#define NEOPIXEL_BRIGHT 127
#define RAINBOW_DIVISOR 4

static led_strip_handle_t s_strip;

void led_init(int gpio)
{
    led_strip_config_t strip_config = {
        .strip_gpio_num   = gpio,
        .max_leds         = 1,
        .led_model        = LED_MODEL_WS2812,
        .color_component_format = LED_STRIP_COLOR_COMPONENT_FMT_GRB,
        .flags = {
            .invert_out = 0,
        },
    };
    led_strip_rmt_config_t rmt_config = {
        .clk_src       = RMT_CLK_SRC_DEFAULT,
        .resolution_hz = 10 * 1000 * 1000,
        .flags = {
            .with_dma = 0,
        },
    };
    ESP_ERROR_CHECK(led_strip_new_rmt_device(&strip_config, &rmt_config, &s_strip));
    ESP_ERROR_CHECK(led_strip_clear(s_strip));
}

void led_set_rgb(uint8_t r, uint8_t g, uint8_t b)
{
    led_strip_set_pixel(s_strip, 0, r, g, b);
    led_strip_refresh(s_strip);
}

void led_idle(void)
{
    led_set_rgb(10, 10, 10);
}

void led_active(void)
{
    led_set_rgb(0, NEOPIXEL_BRIGHT, 0);
}

void led_error_flash(int interval_ms)
{
    while (1) {
        led_set_rgb(NEOPIXEL_BRIGHT, 0, 0);
        vTaskDelay(pdMS_TO_TICKS(interval_ms));
        led_set_rgb(0, 0, 0);
        vTaskDelay(pdMS_TO_TICKS(interval_ms));
    }
}

// Same hue-to-rgb mapping as TomodachiDrawer.Firmware.c (lines 184-200)
// so the rainbow looks identical to the RP2040 version.
static void hue_to_rgb(uint8_t hue, uint8_t *r, uint8_t *g, uint8_t *b)
{
    if (hue < 85) {
        *r = 255 - hue * 3;
        *g = hue * 3;
        *b = 0;
    } else if (hue < 170) {
        hue -= 85;
        *r = 0;
        *g = 255 - hue * 3;
        *b = hue * 3;
    } else {
        hue -= 170;
        *r = hue * 3;
        *g = 0;
        *b = 255 - hue * 3;
    }
}

void led_rainbow_forever(void)
{
    uint8_t hue = 0;
    while (1) {
        uint8_t r, g, b;
        hue_to_rgb(hue, &r, &g, &b);
        led_set_rgb(r / RAINBOW_DIVISOR, g / RAINBOW_DIVISOR, b / RAINBOW_DIVISOR);
        vTaskDelay(pdMS_TO_TICKS(10));
        hue++;
    }
}
