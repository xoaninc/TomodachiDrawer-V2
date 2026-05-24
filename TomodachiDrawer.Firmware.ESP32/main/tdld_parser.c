#include "tdld_parser.h"

#include <string.h>

// gamepad_button_t encoding (from the RP2040 firmware): high byte = report
// byte index, low byte = bit mask. e.g. 0x0101 => report[1] |= 0x01.
typedef uint16_t gamepad_button_t;

#define BTN_Y       ((gamepad_button_t)0x0001)
#define BTN_B       ((gamepad_button_t)0x0002)
#define BTN_A       ((gamepad_button_t)0x0004)
#define BTN_X       ((gamepad_button_t)0x0008)
#define BTN_L       ((gamepad_button_t)0x0010)
#define BTN_R       ((gamepad_button_t)0x0020)
#define BTN_ZL      ((gamepad_button_t)0x0040)
#define BTN_ZR      ((gamepad_button_t)0x0080)
#define BTN_MINUS   ((gamepad_button_t)0x0101)
#define BTN_PLUS    ((gamepad_button_t)0x0102)
#define BTN_LCLICK  ((gamepad_button_t)0x0104)
#define BTN_RCLICK  ((gamepad_button_t)0x0108)
#define BTN_HOME    ((gamepad_button_t)0x0110)
#define BTN_CAPTURE ((gamepad_button_t)0x0120)

// nibble (== C# Button enum: A=0, B=1, X=2, Y=3, ...) -> report bits.
static const gamepad_button_t button_map[] = {
    BTN_A, BTN_B, BTN_X, BTN_Y, BTN_L, BTN_R,
    BTN_ZL, BTN_ZR, BTN_MINUS, BTN_PLUS, BTN_LCLICK,
    BTN_RCLICK, BTN_HOME, BTN_CAPTURE
};
#define BUTTON_COUNT (sizeof(button_map) / sizeof(button_map[0]))

// nibble (== C# Stick enum: LX=0, LY=1, RX=2, RY=3) -> report byte index.
static const uint8_t stick_axis_map[] = {
    STICK_LX, STICK_LY, STICK_RX, STICK_RY
};
#define STICK_COUNT (sizeof(stick_axis_map) / sizeof(stick_axis_map[0]))

static const uint8_t NEUTRAL_REPORT[8] = {
    0x00, 0x00, DPAD_NEUTRAL, STICK_CENTER, STICK_CENTER, STICK_CENTER, STICK_CENTER, 0x00
};

static inline void hid_press(uint8_t *report, gamepad_button_t btn) {
    report[btn >> 8] |= (btn & 0xFF);
}

static inline void hid_release(uint8_t *report, gamepad_button_t btn) {
    report[btn >> 8] &= ~(btn & 0xFF);
}

bool tdld_init(tdld_ctx_t *ctx, const uint8_t *data, size_t length) {
    if (length < 6) {
        return false;
    }
    if (data[0] != 'T' || data[1] != 'D' || data[2] != 'L' || data[3] != 'D') {
        return false;
    }
    if (data[4] != TDLD_VERSION) {
        return false;
    }

    ctx->data    = data;
    ctx->length  = length;
    ctx->cursor  = 6;  // 4-byte magic + 1-byte version + 1-byte padding
    memcpy(ctx->current_report, NEUTRAL_REPORT, sizeof(ctx->current_report));
    ctx->last_1byte_record = 0;
    ctx->repeat_remaining  = 0;
    ctx->has_pending       = false;
    ctx->pending_record    = 0;
    ctx->pending_phase     = 0;
    return true;
}

// Applies one phase of a single-byte record to ctx->current_report and fills
// step.report/delay_ms. Returns true if the record is fully consumed, false if
// another phase remains (TAP opcodes have two phases).
static bool execute_phase(tdld_ctx_t *ctx, uint8_t record, int phase, tdld_step_t *step) {
    uint8_t opcode = record >> 4;
    uint8_t nibble = record & 0x0F;
    bool done = true;

    step->delay_ms = 0;

    switch (opcode) {
        case OPCODE_PRESS_BUTTON:
            if (nibble >= BUTTON_COUNT) { step->error = true; break; }
            hid_press(ctx->current_report, button_map[nibble]);
            break;
        case OPCODE_RELEASE_BUTTON:
            if (nibble >= BUTTON_COUNT) { step->error = true; break; }
            hid_release(ctx->current_report, button_map[nibble]);
            break;
        case OPCODE_PRESS_DPAD:
            ctx->current_report[2] = nibble;
            break;
        case OPCODE_RELEASE_DPAD:
            ctx->current_report[2] = DPAD_NEUTRAL;
            break;
        case OPCODE_RELEASE_ALL:
            memcpy(ctx->current_report, NEUTRAL_REPORT, sizeof(ctx->current_report));
            break;
        case OPCODE_TAP_BUTTON:
            if (nibble >= BUTTON_COUNT) { step->error = true; break; }
            if (phase == 0) {
                hid_press(ctx->current_report, button_map[nibble]);
                step->delay_ms = TDLD_TAP_DELAY_MS;
                done = false;
            } else {
                hid_release(ctx->current_report, button_map[nibble]);
                step->delay_ms = TDLD_TAP_DELAY_MS;
            }
            break;
        case OPCODE_TAP_DPAD:
            if (phase == 0) {
                ctx->current_report[2] = nibble;
                step->delay_ms = TDLD_TAP_DELAY_MS;
                done = false;
            } else {
                ctx->current_report[2] = DPAD_NEUTRAL;
                step->delay_ms = TDLD_TAP_DELAY_MS;
            }
            break;
        default:
            step->error = true;
            break;
    }

    memcpy(step->report, ctx->current_report, sizeof(step->report));
    return done;
}

void tdld_next(tdld_ctx_t *ctx, tdld_step_t *step) {
    step->eof   = false;
    step->error = false;
    step->delay_ms = 0;

    while (true) {
        // 1. Finish any in-progress multi-phase record (e.g. the release half
        //    of a tap).
        if (ctx->has_pending) {
            bool done = execute_phase(ctx, ctx->pending_record, ctx->pending_phase, step);
            if (done) {
                ctx->has_pending = false;
            } else {
                ctx->pending_phase++;
            }
            return;
        }

        // 2. Replay the last single-byte record for an active REPEAT opcode.
        if (ctx->repeat_remaining > 0) {
            ctx->repeat_remaining--;
            ctx->has_pending    = true;
            ctx->pending_record = ctx->last_1byte_record;
            ctx->pending_phase  = 0;
            continue;
        }

        // 3. Fetch the next record from the stream.
        if (ctx->cursor >= ctx->length) {
            step->eof = true;  // ran off the end without an explicit EOF byte
            return;
        }

        uint8_t record = ctx->data[ctx->cursor++];
        uint8_t opcode = record >> 4;
        uint8_t nibble = record & 0x0F;

        switch (opcode) {
            case OPCODE_INVALID:
                step->eof = true;
                return;

            case OPCODE_DELAY: {
                if (ctx->cursor >= ctx->length) { step->error = true; return; }
                uint8_t low = ctx->data[ctx->cursor++];
                step->delay_ms = (uint16_t)((nibble << 8) | low);
                memcpy(step->report, ctx->current_report, sizeof(step->report));
                return;
            }

            case OPCODE_SET_STICK: {
                if (ctx->cursor >= ctx->length) { step->error = true; return; }
                uint8_t value = ctx->data[ctx->cursor++];
                if (nibble >= STICK_COUNT) { step->error = true; return; }
                ctx->current_report[stick_axis_map[nibble]] = value;
                memcpy(step->report, ctx->current_report, sizeof(step->report));
                return;
            }

            case OPCODE_REPEAT_LAST_1:
                ctx->repeat_remaining = nibble;
                continue;

            case OPCODE_REPEAT_LAST_2: {
                if (ctx->cursor >= ctx->length) { step->error = true; return; }
                uint8_t low = ctx->data[ctx->cursor++];
                ctx->repeat_remaining = (nibble << 8) | low;
                continue;
            }

            default:
                // Single-byte opcode (press/release/dpad/tap/...): record it
                // for future REPEATs, then expand it (possibly multi-phase).
                ctx->last_1byte_record = record;
                ctx->has_pending       = true;
                ctx->pending_record    = record;
                ctx->pending_phase     = 0;
                continue;
        }
    }
}
