# Where the project stands — 2026-08-02, after the first hardware night

Written so nothing depends on remembering the sessions. Start here.

## One-line status

`main` is at **0.5.0, unreleased and untagged**, 77 tests, 0 warnings, CI green.
**The release gate is a clean long draw on hardware** — the first attempt desynced, the cause was
found and removed, and the retry has not happened yet.

## What the hardware night established (2026-08-01 → 02)

The full record is [`FIRST_CONNECTION_OFFSET.md`](./FIRST_CONNECTION_OFFSET.md). The short version:

1. **The first long draw desynced after ~2 h** (shifted right from one moment on). Investigation
   produced two mitigations that night.
2. **One of them was wrong and caused the *next* failure itself**: the per-layer corner re-sync
   drove the cursor into the canvas edge — and Juan then established on hardware that the cursor
   **leaves the canvas onto the game's UI buttons** at the edge (neither clamp nor pan). Reverted
   (`a710541`). Edge-overdrive techniques are permanently unavailable to this project.
3. **The other stands**: the RP firmware re-asserts controller state before every Delay record
   (`4b8b9c9`), mirroring the ESP32 — the board with Juan's 10/10 clean record. Also kept: the
   6 s streaming settle on first connection (`e72f621`).
4. **Our generation has no other regression — proven, not assumed.** Upstream 0.8.3's Core was
   built from source at its tag and both programs ran the same photo through an identical harness:
   the decoded streams are **event-for-event identical for the first 1,892 events** (the entire
   handshake/homing/erase-all/bucket/colour prefix is the first ~220 and matches exactly), then
   diverge inside layer-1 routing by design. Ours: 80,178 taps vs upstream's 81,762 (−1.9%).
5. Upstream's repo update of 2026-07-31 is dependabot only (SkiaSharp 4.151, Avalonia bumps).
   Nothing to port.

## What to do next (in order)

1. **Restart the app** (the running binary may predate the revert) and **RE-EXPORT the drawing**:
   any `.tdld`/UF2 generated on the night of 2026-08-01 contains the reverted re-sync taps and
   will misbehave even though the code is fixed. The firmware on the board is fine if flashed
   after `63d0bca`.
2. **Retry the long draw** → [`HARDWARE_VALIDATION_0.5.0.md`](./HARDWARE_VALIDATION_0.5.0.md)
   plus: the draw must complete without visible desync. If it desyncs again, the A/B experiment
   that isolates board-vs-stream is written in `FIRST_CONNECTION_OFFSET.md`.
3. **If everything passes:** tag `0.5.0`, push the tag, CI publishes.

Two decisions are still open and are yours:

- **The upstream PR** is prepared but **not opened** — branch `pr/optimal-tsp-cut`, body in
  the session scratchpad (may be gone; it can be regenerated from §"The upstream PR" below).
  Opening it means forking `Lucas7yoshi/TomodachiDrawer` (you have no fork) and publishing on a
  third party's repo, so it was left for you.
- Whether that PR should also carry the parallel pre-solve. Recommendation: no — see below.

## What changed, in order of how much it matters

### Drawings are now shorter, and that is measured

The routing work is the headline. `PerformTSP` was writing the OrTools tour out starting at the
depot, so the arc it dropped was whichever one happened to sit there — 1 of n candidates, chosen
without reference to how long that arc was. The tour is a closed cycle, so its cost is
rotation-invariant and it can be cut anywhere; picking the best of all 2n candidates costs one
O(n) pass.

| Image | Pen travel | Estimated draw |
|---|---|---|
| 64² | 2503 → **2436** (−2.7%) | 245.3s → **241.7s** |
| 128² | 16696 → **15892** (−4.8%) | 1255.5s → **1215.1s** |
| 256² | 117464 → **113965** (−3.0%) | 7312.7s → **7136.4s** |

Also from upstream: Held-Karp exact solver for point sets ≤12 (optimal and instant, instead of
burning a time limit on a tiny instance), higher TSP time limits, and early-exit support
(default off).

Numbers and methodology: [`bench/README.md`](./bench/README.md).

### A drawing can now be checked without a Switch

New: the drawer emits an *intent trace* (what it means to paint), which gets replayed onto a
simulated canvas and diffed against the quantized source. All three bench images render at
**100.00% match** on all three solver arms — no dropped cells, no wrong colours.

This is why the routing changes are trustworthy without hardware. It does **not** validate that
"select colour C" lands on C in-game, because it records intent rather than watching the menus —
which is exactly why the hardware checklist still exists.

`--render-out <dir>` dumps PNGs. When something is off, the shape of the wrongness identifies the
phase faster than the percentage does.

### Ported from upstream (0.6.0 → 0.8.3, 122 commits audited)

Full ported/skipped breakdown with reasons: [`../SYNC.md`](../SYNC.md).
Full audit: [`UPSTREAM_SYNC_AUDIT.md`](./UPSTREAM_SYNC_AUDIT.md).

Highlights: colour merge for identical in-game HSV steps, erase-all before bucket fill on full-size
Switch 2 images, Switch 1 brush-menu lag mitigation, five crash fixes, RP firmware LED tweaks,
ImageSharp → ColorQuant, SkiaSharp 4, Avalonia 12.1, CI hygiene.

One correction, measured 2026-08-01: the colour merge is often described as "fewer redundant layers,
so shorter drawings". On all three bench images it collapses **nothing** — identical layer and tap
counts with it on and off (`--no-colour-merge`). Those images come from distinct HSV values, so there
is nothing to merge. It pays on real photos with near-duplicate RGB, by an **unmeasured** amount.

Deliberately skipped: Sentry, upstream telemetry, their UI state-context refactor, their ESP32-S3
stack (V2 has its own), and the csproj half of `5dd17d1` — that one would restore
`PublishTrimmed` on macOS, which is what broke ESP32 flashing back in `ae7f711`.

### New user-facing features

- **Cancel Generation** — and it actually works during the parallel pre-solve, which is where the
  time goes. Uses a per-draw token, not the window-lifetime one, so cancelling cannot kill
  serial-port detection for the session.
- **Export to .tdld** — the chip-agnostic input stream, no board needed.
- **Reverse Colour Order.**
- Drawing options persist between sessions; `settings.json` moved to the per-user app-data folder
  (the old working-directory file is migrated forward on first run).

### Three bugs found and fixed, two of them mine

1. **A wrong cut rule.** My first version preferred long arcs *lexicographically* — any arc ≥2 beat
   any shorter one regardless of cost — which pays unbounded travel to dodge what is really a
   1-tap penalty. Worst case measured at +25% on a single layer. Now priced exactly, and
   brute-forced against all 2n candidates by `CutCycleTests`.
2. **New settings did not invalidate the route cache.** `ReverseColourOrder` and the early-exit
   fields were added to `DrawImageSettings` but not to the fingerprint, so toggling them would
   have flashed the **previous** drawing to hardware while logging that the route was current.
   Fingerprint moved to Core, and a reflection-driven test now fails if any settings property is
   missing from it.
3. **The UF2 export could fail silently.** If the drive vanished between detection and writing,
   you waited out a full generation and got nothing — no log, no dialog. Also the write itself was
   unguarded, so a macOS permission denial surfaced as a bare "Access to the path is denied";
   it now explains it and retries once.

### One trap defused

`git fetch upstream --tags` had pulled **upstream's 41 tags into our `refs/tags`**. Since both
projects use the same `X.Y.Z` scheme, our `0.5.0` was pointing at one of Lucas7yoshi's commits —
so tagging the release would have failed confusingly. The remote now fetches upstream tags into
`refs/upstream-tags/*` instead. Nothing was ever published wrong. Details in `SYNC.md`.

## The upstream PR (prepared, not opened)

Branch `pr/optimal-tsp-cut`, based on `upstream/master`. Two separable commits, +298/−3:

1. The optimal cut — improves *their serial path* by 2.7–4.8%, no parallelism involved.
2. A minimal test project brute-forcing the cut against all 2n candidates. Separate so they can
   drop it; their CI already runs `dotnet test`.

**Scope was deliberately reduced** from the original plan, which also wanted the parallel
pre-solve. Reason came out of the measurements: once the cut is in the serial path,
parallel-vs-serial route quality converges to ~0%. So the parallel work's remaining benefit is
generation speed (5–8×), not better routes — a different and much bigger pitch, with a wider blast
radius. Bundling them risks losing both.

The PR body discloses the AI assistance up front, and argues on checkability rather than trust:
the claim is "the chosen cut equals the exhaustive optimum", which the tests verify mechanically.
Upstream's README warns against irresponsible AI use in routing specifically — but its own
`HeldKarpRoute` carries "this ONE function is written by AI… I believe in being transparent about
it", so the maintainer's position reads as transparency rather than prohibition.

## Bluetooth / NXBT — parked

Investigated in depth, then parked: NXBT documents skipping inputs on long macros, our drawings
run for hours, and we already fight desyncs over a wire. It also would not have removed the
external hardware (needs Linux + BlueZ + a USB dongle). **V2 stays wired-only.** Research kept in
[`BLUETOOTH_NXBT_PLAN.md`](./BLUETOOTH_NXBT_PLAN.md); revisit only if NXBT gains reliable
long-macro playback or confirmed Switch 2 support.

## Attribution, since it came up

Upstream's ESP32-S3 support is **not** taken from V2, and V2's is not taken from theirs. Their
PR #46 was opened publicly 2026-05-18; V2's first ESP32 commit is 2026-05-24. The two
implementations are structurally different, and both hit the 7-vs-8-byte HID bug and fixed it
*differently* — the signature of independent discovery. Dates and evidence in
`UPSTREAM_SYNC_AUDIT.md` §0.

## Local branches

| Branch | What |
|---|---|
| `main` | 0.5.0, everything merged, untagged, pushed |
| `sync/upstream-0.8.3` | the sync work; merged into main, safe to delete |
| `pr/optimal-tsp-cut` | based on `upstream/master`, ready to offer upstream. **Do not merge into main** — it is upstream's tree, not ours |

## Toolchain notes

- .NET SDK **10.0.302** via `brew install dotnet` (the bottled formula, not the `.pkg` cask —
  no sudo). Resolves from `PATH`; only an IDE needs
  `export DOTNET_ROOT="/opt/homebrew/opt/dotnet/libexec"`.
- csharpier pinned at **1.3.0** in `.config/dotnet-tools.json`, verified to reproduce upstream's
  formatting byte for byte. Run it before committing Core changes.
- Firmware toolchains (ESP-IDF, Pico SDK) are **not** installed locally. CI builds both. The
  ESP32 firmware ships as a committed binary and was not touched this sync.
- Bench: `dotnet run --project TomodachiDrawer.Bench -c Release -- --tsp 1.0 --colours 32 --repeats 3`.
  Always Release; Debug wall-clock is meaningless.
