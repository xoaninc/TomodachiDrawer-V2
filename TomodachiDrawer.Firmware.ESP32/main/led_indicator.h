#pragma once

#include <stdint.h>

void led_init(int gpio);

void led_set_rgb(uint8_t r, uint8_t g, uint8_t b);

void led_idle(void);

void led_active(void);

void led_error_flash(int interval_ms);

void led_rainbow_forever(void);
