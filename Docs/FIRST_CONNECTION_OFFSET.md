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

## The failing image itself renders perfectly (2026-09-01)

The audit above had a hole nobody noticed at the time: the **bench images** were rendered
(100.00%) but never deep-stream-compared, and **Juan's photo** was stream-compared but never
rendered. The one image that actually desynced on hardware had never had its painted output
checked.

It has now. `ab6761610000e5ebdf36dc25c19fb55f6100761f-2.jpeg` (the file named in
`logs/tomodachidrawer-20260802-003405.log`, the session that flashed the RP2040 at 00:34:22) run
back through `TraceRenderer` with that session's settings — Arbitrary, colour limit 16, Switch 2,
auto-home, 256x256, colour merge on:

```sh
dotnet run --project TomodachiDrawer.Bench -c Release -- \
  --image ~/Downloads/ab6761610000e5ebdf36dc25c19fb55f6100761f-2.jpeg \
  --quantizer Arbitrary --colours 16 --switch 2 --home --tsp 5.0 --render-out /tmp/r
```

`--tsp 5.0` is not a guess: the app does not use the `DrawImageSettings` default of 1.0s, it
overwrites the box on image load with `CanvasDrawer.GetRecommendedTSPSolveTime(w, h)`, which
returns **5.0s** for anything above 192² (`CanvasDrawer.Tsp.cs:28`). Quote tap counts from this
table only against runs at the same limit — the limit is in the route fingerprint for a reason.

| Arm | dpad taps | paint actions | colours | render |
|---|---|---|---|---|
| serial | 76,546 | 33,669 | 16 | **100.00%** (65536/65536) |
| parallel | 77,986 | 33,669 | 16 | **100.00%** |
| parallel-1thread | 77,513 | 33,669 | 16 | **100.00%** |

`missing=0 wrongColour=0 extra=0` on all three. Stronger than the percentage: the three rendered
PNGs and the quantized reference are **byte-identical** (same MD5). Identical `paint=33669` across
arms means the parallel path — the one the app actually ships — changes the route and not the
picture.

The same run at `--tsp 1.0` produces different tap counts (78,331 / 79,271 / 78,726) and **the
same PNG, to the byte**. Two different TSP budgets and three solver arms, six routes, one picture:
route quality and painted output are independent, which is the property the whole optimisation
effort has been assuming.

`colours=16` against the log's 15 scanned layers is the documented off-by-one: the bucket-fill
colour is a picker trip but not a layer.

**What this rules out.** The drawing generated for that photo is not wrong, not partially wrong,
and not wrong only on the parallel path. Combined with the stream comparison above, the desync had
no cause on our side of the wire. The suspicion stays on board/firmware, where `4b8b9c9` now
matches the ESP32's behaviour — and now that rests on evidence for *this* image, not inference
from `mosaic256`.

**What it does not rule out**, and this is the reason the hardware gate exists: the trace records
what the drawer *intends*, and both programs share the menu-navigation constants. `CanvasToolbar.cs`
— eraser submenu geometry included — is byte-identical to upstream's modulo comments
(`git diff -w upstream/master HEAD -- TomodachiDrawer.Core/CanvasToolbar.cs` is two comment lines
and a `using`). Two programs agreeing that "erase all" is row 4 is not evidence that it is row 4
in-game. That question is only answerable on a Switch.

## Upstream has a desync corpus, and we had never read it (2026-09-01)

Everything above was reasoned from our own two nights. Upstream's tracker has been collecting
other people's failures for months, and one of their abandoned branches attacks the mechanism
directly. None of it was in these docs.

### The lead worth acting on: poll-count sync instead of wall-clock delays

Branch `upstream/experimental-hid-tweaks`, tip `ca2f156` (2026-05-06), one commit ahead of master
and never merged. It replaces the tap timing in the RP firmware:

```c
// before: press, send, sleep 25ms, release, send, sleep 25ms
hid_press(...);  push_report();  delay_ms_usb(25);
// after: press, then wait for the console to actually poll us N times
hid_press(...);  wait_reports(tap_hold_polls);
```

`wait_reports` spins on `tud_hid_ready()` and re-sends `current_report` once per poll, so a tap
lasts a number of **host polls** rather than a number of **milliseconds**. That is exactly the
shape of fix a slow accumulating desync wants: a 25 ms sleep assumes the console polled during it,
and a console that is busy rendering may not have. Dropped taps accumulate; the drawing shifts.

The author shelved it with the commit message "**NOTE: Is slower..?**" — which is a verdict on
*throughput*, and our problem is a 2-hour draw surviving, not finishing sooner. Worth re-testing on
its own terms.

Costs, in fairness: it bumps `TDLD_VERSION` to `0x04` and widens the header to 16 bytes carrying
the hold/release poll counts (defaults 22 and 3, credited in-comment to "wigreal's findings"), so
it is a format change on both sides, and our `.tdld` writer and both firmwares would have to move
together.

### Field reports, and how they line up with our failure

| Issue | Report | Fit with ours |
|---|---|---|
| [#173](https://github.com/Lucas7yoshi/TomodachiDrawer/issues/173) | Desyncs track the **in-game canvas type**: full square canvases fail, TV-cropped ones are reliable, regardless of colour count. Theory: the game cannot hold framerate on a full square canvas | **Ours was 256x256, a full square canvas.** Fits |
| [#97](https://github.com/Lucas7yoshi/TomodachiDrawer/issues/97) | Docked desyncs 100% of the time at 20-30 min; undocked ran 1.5 h clean (Switch 1) | Untested by us. Costs nothing to control for |
| [#115](https://github.com/Lucas7yoshi/TomodachiDrawer/issues/115) | Switch 2, stops around a 1.5 h estimate | Different symptom — theirs *stops*, ours kept drawing shifted |
| [#153](https://github.com/Lucas7yoshi/TomodachiDrawer/issues/153) | Proposes periodic re-homing to the top-left corner as a desync fix | **We tried this and reverted it** (`a710541`). Juan established on hardware that the cursor leaves the canvas onto the game's UI buttons at the edge — neither clamps nor pans. That is knowledge this thread does not have |

The two cheap controls for the next attempt, neither of which is a code change: run **undocked**,
and consider proving the mechanism on a **TV-cropped canvas** before spending another two hours on
a square one. If #173 is right, the square canvas is the variable, not our stream.

## Discriminating questions for the next hardware session

1. **How tall was the gap?** ~2 px → mechanism 2. Several-to-tens of px → mechanism 1.
2. **Were the colours right on the failed run** (background fill especially)? Right colours →
   input drops fully excluded, mechanism 1 confirmed.
3. **Any clipping/shift at the right edge?** A small rightward component → DOWNRIGHT repeats →
   mechanism 1.

Incidental finding while comparing versions: 0.4.1's `SelectBucket` under-homed its submenu
(`BucketSubmenuRows - 1` = 1 LEFT tap where 6 were intended; already silently fixed at HEAD,
`CanvasToolbar.cs:225`). Unrelated to this symptom.
