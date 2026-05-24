// Pure, hardware-free parser for the .tdld binary format.
//
// Mirrors the playback logic of the RP2040 firmware
// (TomodachiDrawer.Firmware.c, run_single_byte_opcode + the main switch)
// but with zero TinyUSB / Pico / ESP-IDF dependencies so it can be unit
// tested on the host. The caller drives it: each tdld_next() yields one
// HID report to send plus the delay to wait after sending it, exactly
// matching the original's push_report() + delay_ms_usb() pairing.

#pragma once

#include <stdint.h>
#include <stddef.h>
#include <stdbool.h>

#define TDLD_VERSION 0x03

// Opcodes live in the high nibble. (See FileControllerSink.cs / RP2040 fw.)
#define OPCODE_INVALID        0x0  // repurposed as EOF
#define OPCODE_PRESS_BUTTON   0x1
#define OPCODE_RELEASE_BUTTON 0x2
#define OPCODE_PRESS_DPAD     0x3
#define OPCODE_RELEASE_DPAD   0x4
#define OPCODE_RELEASE_ALL    0x5
#define OPCODE_DELAY          0x6  // 2-byte record, 12-bit ms
#define OPCODE_SET_STICK      0x7  // 2-byte record, axis + value
#define OPCODE_TAP_BUTTON     0x8  // press, 25ms, release, 25ms
#define OPCODE_TAP_DPAD       0x9  // set dpad, 25ms, neutral, 25ms
#define OPCODE_REPEAT_LAST_1  0xE  // replay last 1-byte record (4-bit count)
#define OPCODE_REPEAT_LAST_2  0xF  // replay last 1-byte record (12-bit count)

// DPad states (report byte 2).
#define DPAD_UP        0
#define DPAD_UPRIGHT   1
#define DPAD_RIGHT     2
#define DPAD_DOWNRIGHT 3
#define DPAD_DOWN      4
#define DPAD_DOWNLEFT  5
#define DPAD_LEFT      6
#define DPAD_UPLEFT    7
#define DPAD_NEUTRAL   8

// Stick axis indices into the report, and the neutral centre value.
#define STICK_LX     3
#define STICK_LY     4
#define STICK_RX     5
#define STICK_RY     6
#define STICK_CENTER 128

#define TDLD_TAP_DELAY_MS 25

// Result of one parser step.
typedef struct {
    uint8_t  report[8];  // HID report to send: [Btn1,Btn2,DPad,LX,LY,RX,RY,Pad]
    uint16_t delay_ms;   // wait this long after sending the report (0 = none)
    bool     eof;        // stream finished (report/delay are not meaningful)
    bool     error;      // corrupt/unknown opcode (caller should error-flash)
} tdld_step_t;

typedef struct {
    const uint8_t *data;
    size_t         length;
    size_t         cursor;
    uint8_t        current_report[8];

    // Run-length-encoding replay state (REPEAT_LAST_1 / REPEAT_LAST_2).
    uint8_t        last_1byte_record;
    int            repeat_remaining;

    // Multi-step expansion of a single record (TAP opcodes emit 2 steps).
    bool           has_pending;
    uint8_t        pending_record;
    int            pending_phase;
} tdld_ctx_t;

// Validates the 6-byte header ("TDLD" + version + padding) and initialises
// the context to a neutral report. Returns false on bad magic or version.
bool tdld_init(tdld_ctx_t *ctx, const uint8_t *data, size_t length);

// Produces the next step. After eof is set, do not call again.
void tdld_next(tdld_ctx_t *ctx, tdld_step_t *step);
