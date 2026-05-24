// SPDX-License-Identifier: GPL-3.0-or-later
// TomodachiDrawer V2 — Copyright (C) 2026 Xoan <github.com/xoaninc>
// Modified version of TomodachiDrawer (original (C) Lucas7yoshi, GPL-3.0).
//
// Shared test entry point, compiled for both the host (clang) and the
// ESP32-S3 (ESP-IDF). Returns the number of failed checks (0 == all passed).
#pragma once

int tdld_run_all_tests(void);
