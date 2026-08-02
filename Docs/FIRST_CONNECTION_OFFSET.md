# Canvas desyncs — investigation record

> Originally opened for the first-connection offset; widened 2026-08-01 when a second, mid-draw
> sighting arrived. Both fit the same mechanism family.

**Symptom (Juan, 2026-08-01):** first time a board is plugged into the Switch, the drawing
*sometimes* comes out shifted **down**, gap at the top, otherwise clean. Replug → correct.
**Also happened on 0.4.1 two months ago** (ESP32-S3 era), which had no erase-all code.

Investigated 2026-08-01 with four parallel deep-dives (stream forensics, upstream archaeology,
external research, ESP32 parity). This file is the durable record; the discriminating questions
at the bottom are what the next hardware session should answer.

## The key negative result

**No eaten leading prefix of the input stream can produce a clean, pure-downward shift.**
This was checked exhaustively for both 0.4.1 and HEAD (every drop length k, and every longer
contiguous dead-window cut point):

| Scenario | Effect |
|---|---|
| First 1–3 inputs eaten (the handshake A-taps) | nothing positional — they're registration/dialog fodder |
| Toolbar-opening X eaten (k≥4) | **+7 px RIGHT** (0.4.1) / **+9 px RIGHT** (HEAD), plus tool/menu desync that would visibly wreck the drawing |
| Dead window ending inside hotbar homing | **+2 px DOWN** — the only pure-vertical case — but necessarily also a **wrong background colour** (the colour-picker flow lands in the hotbar) |

The prefix simply contains **no vertical canvas motion** reachable without first producing a
rightward shift or visible chaos. So "the console ate the first inputs" — the story behind the
`e72f621` firmware fix — **cannot be the mechanism for this particular symptom**. (The fix is
still right for other reasons; see below.)

## The two mechanisms that DO fit

1. **Held-dpad auto-repeat from a delayed/lost RELEASE during early canvas navigation.**
   All menu navigation is immune by construction (homing slams into clamps); the canvas is not.
   The first canvas navigation from the top-left is exclusively DOWN / DOWNRIGHT / RIGHT, so any
   over-held dpad biases the error **downward**. A release report the console processes late =
   the game auto-repeats extra rows; the code's believed position never learns; everything from
   that point paints shifted down → gap at top. Stochastic, plausibly worse on a fresh
   enumeration. **Predicts:** gap of arbitrary height (several to tens of px), correct colours,
   possibly a small rightward component from DOWNRIGHT repeats.
2. **The 2 px hotbar window** (only if the observed gap was exactly ~2 px): a controller-connected
   overlay surviving ~9–10 s into the stream swallows the hotbar-open Y and the UP-slam.
   **Predicts:** exactly 2 px gap **and a wrong background/first-fill colour**.

## What upstream knows (they've been here three times)

- **#5, #10, #20, #44, #48**: the first-connect input-swallowing class, seen repeatedly since
  0.3.x. Never fully fixed. #44 is key intel: the connect-UI popup eats a **variable** number of
  A presses depending on console state at plug-in — which is exactly why replug "fixes" things.
- The firmware countdown comment ("otherwise it seemed to be off by one") is their sighting of
  the registration gap.
- **They tried constant report streaming (`30db939`) and reverted it (`b7f61c5`)** — but that was
  streaming during *playback* delays, where each stale queued report can delay the next real
  input by up to one 8 ms bInterval, accumulating over thousands of delays. Our `e72f621` streams
  only in the **pre-playback** window, which avoids that failure mode entirely (the handshake's
  first real press blocks on `tud_hid_ready()`, so worst case is one 8 ms skew, irrelevant at
  100 ms press times).

## What the rest of the field does (print-bots for Splatoon etc.)

- Everybody streams reports continuously from enumeration; nobody waits on a host event, because
  on the HORIPAD/Pokken descriptor path (our VID 0x0F0D/PID 0x0092) **there is no host-driven
  "registration done" signal** — the 0x80 handshake exists only for real Pro Controller VIDs.
- Warm-ups are 6.4–8 s **with registration presses spread across the window** (L+R and A pressed
  at several points), not one-shot taps after it.
- The genre's real defence against shifted output is **positional self-correction**: overdrive
  the cursor against a canvas edge for longer than the maximum possible distance, so unknown
  early errors are absorbed by the clamp (Switch-Fightstick `SYNC_POSITION`, img2splat "cautious
  mode"). Our `HomeCanvasToTopLeft` is menu-based view homing and was **off** in Juan's runs.

## ESP32: same flaw, byte-identical, and it's where 0.4.1 was tested

`TomodachiDrawer.Firmware.ESP32/main/main.c:44-51` is a faithful port of the RP flaw: 3 s settle,
**6 reports**, `vTaskDelay` never reports. Identical at ba1fd2f and HEAD — so the 0.4.1-era
ESP32-S3 testing ran exactly this. It needs the mirror of `e72f621`:

- add `hid_stream_neutral(uint32_t ms)` to `main/hid_gamepad.c` (~125 reports/s; `hid_send_report`
  already blocks on `tud_hid_ready`, so pacing self-throttles),
- countdown at `main/main.c:44-51` → 6 × `{led; hid_stream_neutral(500);}` twice, mirroring
  `TomodachiDrawer.Firmware.c:346-351`.

**Blocker:** V2's CI has no ESP-IDF job (the ESP32 firmware ships as a committed binary), and the
toolchain is not installed locally — so this fix needs either a CI job or a local ESP-IDF build
before it can ship.

## Where this leaves the shipped fix (`e72f621`)

- **Right, but for a narrower reason than its commit message claims.** It addresses the real
  registration gap (field-standard practice) and the rightward-shift/chaos failure class
  (upstream #5/#20). It does **not** address mechanism 1, which happens *during playback*.
- Candidate hardening, deliberately not implemented yet (wait for the discriminators):
  1. Re-send the current report periodically (~every 50 ms) during **long** Delay opcodes only,
     with upstream's `2828ea3` drain trick — bounds how long a stale held-state can survive
     without recreating the slowdown they reverted.
  2. Spread redundant handshake presses across the settle window instead of one-shot taps after
     it (careful: A paints if the game is already listening; only safe under the erase-all path).
  3. Edge-overdrive homing for 256×256 drawings regardless of the checkbox (dpad against the
     top/left clamps; ~13 s, absorbs any accumulated early error). The strongest fix if
     mechanism 1 is confirmed.

## Second sighting (2026-08-01, same evening): mid-draw, rightward

A long draw (Switch 2, RP2040-Zero) ran **correctly for ~2 hours, then desynced: everything from
that point painted shifted to the RIGHT**. The session log was lost (the app was restarted while
it held the only copy — the reason `AppendLog` now also writes to a file under
`ApplicationData/TomodachiDrawerV2/logs/`).

What this sighting establishes:

- **It is not a first-connection phenomenon** — registration was hours in the past. The
  connect-window mechanisms (and the `e72f621` settle fix) are irrelevant to it.
- **Direction matched travel, not a fixed axis.** Down at the start of one run, right in the
  middle of another — exactly what mechanism 1 predicts (an over-held dpad repeats in whichever
  direction the route was moving when the stall hit) and what eaten-input mechanisms do not:
  dropped presses cause *under*-movement, shifting content the *opposite* way to travel, and the
  menu-desync signatures that come with eaten inputs were absent (2 hours of correct drawing).
- An overshoot is a one-time event: believed position never re-syncs, so **everything after it
  carries the same offset** — consistent with "the rest of the drawing was shifted", not smeared.

**Mechanism 1 (delayed dpad release → in-game auto-repeat overshoot) is now the primary
suspect for both sightings.** Open question that would falsify it: whether the failed runs'
colours were correct (they appear to have been).

### The mitigation that fits: periodic re-sync against the canvas clamp

The believed cursor position cannot be read back, so desyncs cannot be *detected* — but they can
be *absorbed*: navigate to a corner and overdrive past it (the clamp eats the extra), which
re-establishes a known position no matter what error has accumulated. This is the genre-standard
defence (Switch-Fightstick `SYNC_POSITION`, img2splat cautious mode).

Sketch for V2: between colour layers, instead of navigating directly to the next layer's first
point, navigate to (0,0) **plus a fixed overdrive margin** (~30 extra UPLEFT taps ≈ 1.5 s), then
proceed from a now-certain (0,0). Cost ≈ the corner detour + margin per layer — a few minutes on
a 2-hour draw — and it bounds any desync's blast radius to a single layer. Not implemented yet:
it changes what gets drawn, so it belongs after the 0.5.0 gate, or in it if Juan prefers.

## ⚠ The corner re-sync was WRONG and is reverted (`a710541`)

Shipped and reverted the same night. It drove the cursor into the top and left edges 32 taps per
axis per layer — and this program's own in-game setup text has always said that *"the cursor
[desyncs] as the canvas moves when the cursor gets on the edges"*
(`MainWindow.axaml.cs:1654`). **Edge behaviour can be a canvas PAN, not a clamp**, and to code
that cannot read the cursor back the two are indistinguishable. So the insurance manufactured the
exact desync it was meant to absorb: 960 edge collisions on a 15-layer drawing, and the reported
result was a drawing that went to a corner at the start of a layer and was shifted thereafter.

**The offline verification quoted for it proved nothing.** "paint constant, render 100,00%" cannot
see this class of change: the renderer replays paint *intents* in image coordinates and does not
model the in-game cursor, so anything that only alters where the physical cursor ends up is
invisible to it by construction. There is no offline test in this repo capable of catching it.

**ANSWERED on hardware (Juan, 2026-08-02): it is neither clamp nor pan — the cursor LEAVES the
canvas.** Pushing the dpad past the top-left edge moves the cursor slowly off the canvas and onto
the game's UI buttons above it. So edge-overdrive re-syncing is not merely risky, it is
**unavailable to this project, permanently**: overdrive taps walk the cursor onto UI controls,
which both desyncs the position AND risks clicking interface buttons. This matches the observed
failure exactly ("goes to a corner at the start, then does strange things"). Any future
re-sync must come from menu-driven navigation (e.g. the Move tool, as `HomeCanvasToTopLeft`
does), never from raw dpad travel against an edge.

## Mitigations shipped (2026-08-01 night), their audited limits

Both landed after Juan confirmed the 0.4.1-era clean record (10/10) was on the **ESP32-S3** —
board and app version changed together, so "0.5.0 regressed" is unproven; the board is the
stronger suspect. The story is now internally consistent: the **first-plug offset happened on
both boards** (the 6-reports settle flaw is shared; ESP32's port is task #20), while the
**mid-draw desync appeared only on the RP2040** — the board whose firmware never re-asserted
controller state.

1. **Per-layer corner re-sync** (`c7cd67c`). Audited: the wire order is correct even though the
   stamp phase records into a `TimingSink` and replays later (re-sync writes directly to the real
   output *before* the recording starts); the fine-detail dry-run's cursor save/restore is
   untouched; pre-solved route cuts adapt to the re-synced cursor by construction.
   **Known limits, on purpose:** (a) the overdrive absorbs at most `CursorResyncOverdrive` (32) px
   of rightward/downward error per layer — unbounded leftward/upward, since those clamp during
   navigation; (b) the blast radius is one *layer*, and a big fine-detail layer can run 10–20
   minutes, so a desync early in one still costs that layer. If that ever hurts in practice, the
   next step is a mid-layer re-sync every N estimated minutes, not a bigger margin.
2. **RP firmware re-asserts state before every Delay record** (`4b8b9c9`), mirroring the ESP32.
   Audited: HID reports carry absolute state, so a duplicate can never double-press; the RLE
   repeat opcodes only apply to 1-byte records, so the re-send cannot be amplified; and delays
   over 4095 ms are split into multiple records, which means the **4.25 s colour-slam hold gets
   periodic re-assertion of the held ZL/ZR+stick state** — an unplanned bonus that protects
   exactly the kind of long-held input a mis-latch would hurt most.

**If a long draw desyncs again despite both:** the clean discriminator is an A/B — generate the
same image's `.tdld` with 0.4.1 code (`git checkout ba1fd2f`, build, export) and replay it on the
RP2040-Zero. Desync ⇒ board/firmware, not 0.5.0's stream. Clean ⇒ the 0.5.0 stream changes come
back under suspicion.

## The generation itself is clean — verified against upstream directly (2026-08-02)

Upstream 0.8.3's Core was built from source at its tag and both Cores ran the same photo (Juan's
actual test image, Arbitrary/15, serial path) through an identical harness into
`FileControllerSink`. Decoding both streams opcode-by-opcode:

- **Event-for-event identical for the first 1,892 events.** The whole risk surface — handshake,
  homing, erase-all, bucket select, colour pick, bucket click — is the first ~220 events and
  matches exactly.
- First divergence is inside layer 1's stamp route: ours takes a different (shorter) path to the
  same points, which is the optimal cut + tap objective working as designed. Totals on that image:
  ours 80,178 taps, upstream 81,762 (−1.9%).
- The long straight-run profile (max consecutive UP/DOWN/LEFT/RIGHT) has the same shape in both,
  with upstream's runs slightly longer — so long runs are normal routing, not artifacts.

So after the re-sync revert there is **no stream-level regression versus upstream**, and the
remaining suspect for the original mid-draw desync is the board/firmware side — where the Delay
re-assert fix now matches the ESP32's behaviour. Harness recipe if it needs re-running: worktree
at `upstream-tags/0.8.3`, one shared `Program.cs` compiled against each Core, decoder in the
session scratchpad (`tdld_decode.py` — 6-byte header, opcodes in the high nibble, RLE 0xE/0xF,
0x0 = end marker).

## Discriminating questions for the next hardware session

1. **How tall was the gap?** ~2 px → mechanism 2. Several-to-tens of px → mechanism 1.
2. **Were the colours right on the failed run** (background fill especially)? Right colours →
   input drops fully excluded, mechanism 1 confirmed.
3. **Any clipping/shift at the right edge?** A small rightward component → DOWNRIGHT repeats →
   mechanism 1.

Incidental finding while comparing versions: 0.4.1's `SelectBucket` under-homed its submenu
(`BucketSubmenuRows - 1` = 1 LEFT tap where 6 were intended; already silently fixed at HEAD,
`CanvasToolbar.cs:225`). Unrelated to this symptom.
