// Shared test entry point, compiled for both the host (clang) and the
// ESP32-S3 (ESP-IDF). Returns the number of failed checks (0 == all passed).
#pragma once

int tdld_run_all_tests(void);
