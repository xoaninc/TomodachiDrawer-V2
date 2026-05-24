// Unit tests for the pure .tdld parser. Self-contained (no Unity / no IDF),
// so the exact same file compiles and runs on the host with clang AND on the
// ESP32-S3 under ESP-IDF. printf goes to stdout on host and to the
// USB-Serial-JTAG console on target.

#include "tdld_tests.h"
#include "tdld_parser.h"

#include <stdio.h>
#include <string.h>

static int g_checks = 0;
static int g_failures = 0;

#define CHECK(cond, name)                                                   \
    do {                                                                    \
        g_checks++;                                                         \
        if (!(cond)) {                                                      \
            g_failures++;                                                   \
            printf("  FAIL [%s] line %d: %s\n", (name), __LINE__, #cond);   \
        }                                                                   \
    } while (0)

#define CHECK_EQ(actual, expected, name)                                    \
    do {                                                                    \
        g_checks++;                                                         \
        long _a = (long)(actual);                                           \
        long _e = (long)(expected);                                         \
        if (_a != _e) {                                                     \
            g_failures++;                                                   \
            printf("  FAIL [%s] line %d: %s == %ld, expected %ld\n",        \
                   (name), __LINE__, #actual, _a, _e);                      \
        }                                                                   \
    } while (0)

// --- Helpers ---------------------------------------------------------------

// Header bytes: "TDLD", version 3, padding 0.
#define TDLD_HEADER 'T', 'D', 'L', 'D', 0x03, 0x00

// --- Tests -----------------------------------------------------------------

static void test_bad_magic(void) {
    const char *name = "bad_magic";
    uint8_t data[] = {'X', 'X', 'X', 'X', 0x03, 0x00};
    tdld_ctx_t ctx;
    CHECK(tdld_init(&ctx, data, sizeof(data)) == false, name);
}

static void test_wrong_version(void) {
    const char *name = "wrong_version";
    uint8_t data[] = {'T', 'D', 'L', 'D', 0x02, 0x00};
    tdld_ctx_t ctx;
    CHECK(tdld_init(&ctx, data, sizeof(data)) == false, name);
}

static void test_too_short(void) {
    const char *name = "too_short";
    uint8_t data[] = {'T', 'D', 'L'};
    tdld_ctx_t ctx;
    CHECK(tdld_init(&ctx, data, sizeof(data)) == false, name);
}

static void test_valid_header_inits_neutral(void) {
    const char *name = "neutral_init";
    uint8_t data[] = {TDLD_HEADER, 0x00};
    tdld_ctx_t ctx;
    CHECK(tdld_init(&ctx, data, sizeof(data)) == true, name);
    CHECK_EQ(ctx.current_report[0], 0x00, name);
    CHECK_EQ(ctx.current_report[2], DPAD_NEUTRAL, name);
    CHECK_EQ(ctx.current_report[3], STICK_CENTER, name);
}

static void test_tap_button_a(void) {
    const char *name = "tap_button_a";
    // TAP_BUTTON A == opcode 0x8, nibble 0 (A) -> byte 0x80, then EOF (0x00)
    uint8_t data[] = {TDLD_HEADER, 0x80, 0x00};
    tdld_ctx_t ctx;
    CHECK(tdld_init(&ctx, data, sizeof(data)) == true, name);

    tdld_step_t s;
    tdld_next(&ctx, &s);                 // press half
    CHECK_EQ(s.report[0], 0x04, name);   // BTN_A bit
    CHECK_EQ(s.delay_ms, 25, name);
    CHECK(!s.eof && !s.error, name);

    tdld_next(&ctx, &s);                 // release half
    CHECK_EQ(s.report[0], 0x00, name);
    CHECK_EQ(s.delay_ms, 25, name);

    tdld_next(&ctx, &s);                 // EOF
    CHECK(s.eof, name);
}

static void test_press_then_release_button(void) {
    const char *name = "press_release_button";
    // PRESS B (0x11), RELEASE B (0x21), EOF.  B == nibble 1.
    uint8_t data[] = {TDLD_HEADER, 0x11, 0x21, 0x00};
    tdld_ctx_t ctx;
    tdld_init(&ctx, data, sizeof(data));

    tdld_step_t s;
    tdld_next(&ctx, &s);
    CHECK_EQ(s.report[0], 0x02, name);   // BTN_B
    CHECK_EQ(s.delay_ms, 0, name);
    tdld_next(&ctx, &s);
    CHECK_EQ(s.report[0], 0x00, name);   // released
}

static void test_delay_12bit(void) {
    const char *name = "delay_12bit";
    // DELAY opcode 0x6, high nibble of count = 1, low byte 0x23 -> 0x123
    uint8_t data[] = {TDLD_HEADER, 0x61, 0x23, 0x00};
    tdld_ctx_t ctx;
    tdld_init(&ctx, data, sizeof(data));
    tdld_step_t s;
    tdld_next(&ctx, &s);
    CHECK_EQ(s.delay_ms, 0x123, name);
}

static void test_set_stick_lx(void) {
    const char *name = "set_stick_lx";
    // SET_STICK 0x7, axis 0 (LX), value 200
    uint8_t data[] = {TDLD_HEADER, 0x70, 200, 0x00};
    tdld_ctx_t ctx;
    tdld_init(&ctx, data, sizeof(data));
    tdld_step_t s;
    tdld_next(&ctx, &s);
    CHECK_EQ(s.report[STICK_LX], 200, name);
}

static void test_press_dpad(void) {
    const char *name = "press_dpad";
    // PRESS_DPAD 0x3, nibble 1 (UPRIGHT) -> 0x31
    uint8_t data[] = {TDLD_HEADER, 0x31, 0x00};
    tdld_ctx_t ctx;
    tdld_init(&ctx, data, sizeof(data));
    tdld_step_t s;
    tdld_next(&ctx, &s);
    CHECK_EQ(s.report[2], DPAD_UPRIGHT, name);
}

static void test_repeat_last_1(void) {
    const char *name = "repeat_last_1";
    // TAP A (0x80), then REPEAT_LAST_1 count 3 (0xE3), then EOF.
    // Expect 1 original tap + 3 replays = 4 A-presses total.
    uint8_t data[] = {TDLD_HEADER, 0x80, 0xE3, 0x00};
    tdld_ctx_t ctx;
    tdld_init(&ctx, data, sizeof(data));
    int presses = 0;
    tdld_step_t s;
    for (int i = 0; i < 100; i++) {
        tdld_next(&ctx, &s);
        if (s.eof || s.error) break;
        if (s.report[0] == 0x04) presses++;
    }
    CHECK_EQ(presses, 4, name);
}

static void test_truncated_delay_errors(void) {
    const char *name = "truncated_delay";
    // DELAY opcode but missing its second byte -> error, not OOB read.
    uint8_t data[] = {TDLD_HEADER, 0x61};
    tdld_ctx_t ctx;
    tdld_init(&ctx, data, sizeof(data));
    tdld_step_t s;
    tdld_next(&ctx, &s);
    CHECK(s.error, name);
}

static void test_unknown_opcode_errors(void) {
    const char *name = "unknown_opcode";
    // Opcode 0xA is undefined -> error.
    uint8_t data[] = {TDLD_HEADER, 0xA0, 0x00};
    tdld_ctx_t ctx;
    tdld_init(&ctx, data, sizeof(data));
    tdld_step_t s;
    tdld_next(&ctx, &s);
    CHECK(s.error, name);
}

static void test_release_all(void) {
    const char *name = "release_all";
    // PRESS A (0x10), then RELEASE_ALL (0x50): report should be neutral again.
    uint8_t data[] = {TDLD_HEADER, 0x10, 0x50, 0x00};
    tdld_ctx_t ctx;
    tdld_init(&ctx, data, sizeof(data));
    tdld_step_t s;
    tdld_next(&ctx, &s);
    CHECK_EQ(s.report[0], 0x04, name);   // A pressed
    tdld_next(&ctx, &s);
    CHECK_EQ(s.report[0], 0x00, name);   // all cleared
    CHECK_EQ(s.report[2], DPAD_NEUTRAL, name);
}

int tdld_run_all_tests(void) {
    g_checks = 0;
    g_failures = 0;

    printf("=== tdld_parser tests ===\n");
    test_bad_magic();
    test_wrong_version();
    test_too_short();
    test_valid_header_inits_neutral();
    test_tap_button_a();
    test_press_then_release_button();
    test_delay_12bit();
    test_set_stick_lx();
    test_press_dpad();
    test_repeat_last_1();
    test_truncated_delay_errors();
    test_unknown_opcode_errors();
    test_release_all();

    printf("=== %d checks, %d failures ===\n", g_checks, g_failures);
    if (g_failures == 0) {
        printf("ALL TESTS PASSED\n");
    } else {
        printf("TESTS FAILED\n");
    }
    return g_failures;
}
