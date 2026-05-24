// SPDX-License-Identifier: GPL-3.0-or-later
// TomodachiDrawer ESP32-S3 port — Copyright (C) 2026 Xoan <github.com/xoaninc>
// Modified version of TomodachiDrawer (original (C) Lucas7yoshi, GPL-3.0).

#pragma once

#include <stdint.h>

// Installs the TinyUSB device stack with the Pokken Pad descriptors.
void usb_init(void);

// Blocks until the USB host (PC or Switch) has enumerated and mounted us.
void hid_wait_mounted(void);

// Sends one 8-byte HID report, blocking until the endpoint is ready.
void hid_send_report(const uint8_t report[8]);
