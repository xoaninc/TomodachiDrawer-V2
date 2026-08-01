# Ideas after 0.5.0 — findings, and what measuring them showed

Found by reading the code on 2026-07-31, after the 0.8.3 sync shipped to `main`, then measured on
2026-08-01. Two of them were implemented; one survived measurement and one did not.

Two of the four are TODOs upstream wrote themselves and never did. All four are now *measurable*,
which is the difference from before: `TomodachiDrawer.Bench` prices any routing change in DPad taps,
and the intent-trace renderer proves a change did not drop or misplace a single cell.

| # | Finding | Outcome |
|---|---|---|
| 1 | The TSP objective minimises travel, not taps | ✅ **Done and kept** (`995608e`) — hold-run breaks −2,2% to −7,4% on 9/9 arms |
| 2 | Layer order is unoptimised (upstream's own TODO) | ❌ **Tried, measured, reverted.** It made drawings *longer*. See below |
| 3 | The colour grid navigates Manhattan, not Chebyshev | **Pending** one observation on hardware; currently looks unfixable |
| 4 | The 4250 ms colour-homing delay | **Risky half rejected.** Cheap half is lowest priority |

Also measured on 2026-08-01, and worth recording because it corrects a claim: **upstream's colour
merge collapses nothing on any of the three bench images.** With `--no-colour-merge` the layer counts
and tap counts are identical. Those images are built from distinct HSV values, so there is nothing to
collapse — the merge pays on real photos with near-duplicate RGB, and by how much is **unmeasured**.
Any statement that 0.5.0 is shorter "thanks to the colour merge" is unsupported.

---

## 1. The TSP objective minimised travel, but what you pay is taps — DONE, KEPT

Landed as `995608e`. `MoveCost` charges `d + (d > 1 ? 1 : 0)` for the fine-detail phase, because
`FineDetailTsp` holds A across Chebyshev-1 neighbours: a `d == 1` arc is one DPad tap, and a `d > 1`
arc additionally breaks the hold run for one release plus one press. Total emission is
`travel + groups` where `groups = n - L1`, so for a fixed point count the objective should reward
adjacency — and plain Chebyshev did not do that at all.

**Upstream had tried it and recorded a null result**, in the comment this replaced: *"the lowest
value this can return is 1 so there was no gain"*. That names the trap and then walks into it: with
integer costs an adjacent arc cannot be *discounted* below 1, so the preference is inexpressible as
a discount. As a penalty on the other side it is expressible.

### Measured (A/B, same machine, `--tsp 1.0 --colours 32 --repeats 2`)

Hold-run breaks — the deterministic metric, no wall-clock component:

| Image | arm | before | after | Δ |
|---|---|---|---|---|
| grid64 | serial | 112 | 107 | −4,5% |
| grid64 | parallel | 108 | 100 | −7,4% |
| grid64 | parallel-1thread | 108 | 100 | −7,4% |
| rings128 | serial | 1008 | 954 | −5,4% |
| rings128 | parallel | 992 | 961 | −3,1% |
| rings128 | parallel-1thread | 991 | 955 | −3,6% |
| mosaic256 | serial | 3797 | 3594 | −5,3% |
| mosaic256 | parallel | 3742 | 3658 | −2,2% |
| mosaic256 | parallel-1thread | 3786 | 3672 | −3,0% |

Down on **9 of 9**. `paint` exactly constant, `render: 100,00%` with `missing=0 wrongColour=0
extra=0` on 9 of 9.

**Total DPad taps moved only within noise** (−2,9% to +0,4%), which was predicted in advance: the
`--tsp` budget is wall-clock so OrTools is nondeterministic under load, and the noise band (~0,7% on
mosaic256, ~800 taps) is wider than the whole effect. That is precisely why the gate was set on hold
runs rather than on taps.

**Worth offering upstream** alongside the optimal cut — it improves their serial path too.

## 2. Layer ordering — TRIED, MEASURED, REVERTED

Implemented (greedy + 2-opt over layer centroids and colour-picker deltas), tested, benched, and then
**reverted**. Recording it properly so it does not get retried the same way.

### Why it was reverted

The bench runs the **Arbitrary** palette, where every colour selection homes the HSV sliders to a
corner, so picker cost is order-independent and only centroid travel varies. Measured there: no
change. That alone was only a coverage gap — so it was re-measured on `--quantizer CieLab`, the grid
palette, which is where the picker term is actually live:

| Image | arm | §1 only | §1 + ordering | Δ |
|---|---|---|---|---|
| rings128 | serial | 989,0s | 999,5s | **+1,06%** |
| rings128 | parallel | 991,1s | 1002,2s | **+1,12%** |
| rings128 | parallel-1thread | 986,5s | 997,7s | **+1,14%** |
| mosaic256 | serial | 6746,2s | 6754,5s | +0,12% |
| grid64 | serial | 175,2s | 174,7s | −0,29% |

Consistent across all three arms on rings128, so **not noise: it made drawings longer.**

### The reason, which is the useful part

The ordering was *provably* never worse **under its own cost model** — there is a test for that. It
was worse in reality, so **the cost model was wrong**, and specifically: it used each layer's
**centroid** as a proxy for where that layer's work begins. The real inter-layer travel runs from
where the previous layer's route *ended* to where the next one's *starts*, and both are chosen by
`ChooseCut` from the live cursor at emission time. Reordering changes the cursor each layer is
entered from, which changes `ChooseCut`, which changes every per-layer route — an effect a centroid
model cannot see and can easily lose more to than the ordering gains.

**So layer ordering cannot be evaluated independently of per-layer entry points.** Any future attempt
needs the two solved together, or at least an ordering cost that uses real route endpoints rather
than centroids. That is a substantially bigger piece of work than it looked, and the *guarantee*
being satisfied while the *outcome* got worse is the lesson worth keeping.

Upstream's TODO at `CanvasDrawer.cs:99-100` therefore stays open, and now with a reason.

## 3. The colour grid navigates Manhattan, not Chebyshev — pending

**Blocked on one observation, deliberately left pending 2026-07-31.**

`ColourPalette.cs:337` is another upstream TODO: `// TODO: Optimize with diagonals.` Below it,
lines 340-343 tap all of Y and then all of X, which costs `|Δx| + |Δy|`. The canvas path *does* use
diagonals (`CanvasDrawer.cs:1154-1155` emit `DPad.UPRIGHT` / `DOWNLEFT`) — which is precisely why
Chebyshev is the routing cost metric. Same Manhattan-vs-Chebyshev distinction, different menu.

Saving if it works: `min(|Δx|, |Δy|)` taps per colour change.

**The blocker is hardware, not code.** The `DPad` enum has all eight directions
(`OutputSinks/SinkEnums.cs:23-33`) and the protocol carries them; what is unknown is whether the
in-game colour grid **accepts** diagonal input. The canvas does; a menu need not. It cannot be
tested through the app, because the app does not emit diagonals there.

**The observation that settles it** — with a real controller, in the colour grid, hold the dpad
up-right and watch the cursor:

- moves **one cell diagonally** → the TODO is fixable, worth `min(|Δx|,|Δy|)` per colour change
- moves in **one axis only**, or steps a staircase → the TODO is not fixable, drop it

Current read (unconfirmed, 2026-07-31): it looks like a staircase, i.e. probably **not** fixable.

## 4. The 4250 ms colour-homing delay

**The clever version is rejected. The cheap version survives but is the lowest priority here.**

On the Arbitrary path every colour change homes all three sliders to a corner by holding ZL/ZR plus
the stick, with a fixed `Delay(4250)` (`ColourPalette.cs:375`), then taps out from there.

### Rejected: incremental navigation

Navigating straight from the previous colour would cost `|Δhue| + |Δsat| + |Δval|` taps and no
homing at all. Combined with §2 it sounds excellent. **It is the wrong trade for this project.**

Slider position **cannot be read**, so homing is the *only* resync point in colour selection.
Incremental navigation turns any dropped tap into an **accumulating** error that corrupts every
remaining colour, in a drawing that already runs for hours and already fights desyncs over a wire.
Roughly 2% is not worth that.

### Kept: size the delay per colour instead of for the worst case

The comment on that line says it: *"as low as it can be for handling the worst case (black)"*.
Black is the longest slew on all three sliders. A mid-range colour finishes homing well before
4250 ms and the delay burns the remainder waiting.

Worth **~1–2%** at most (32 changes × up to 4.25 s against a 7136 s draw), and less than that now
that upstream's colour merge has cut the layer count. It also is **not free**: it needs one
hardware calibration of the slider slew rate, and the failure mode of under-waiting is *the wrong
colour*, which ruins the whole drawing. So it needs real margin, and it ranks below §1 and §2.

---

## Not routing

- **Resume from a checkpoint.** A 256² draw is ~2 hours. A desync or an interruption means starting
  over. `.tdld` is a linear stream replayed by the firmware, so "start at offset N" is cheap on both
  sides, and the app already knows which layer it reached. For multi-hour drawings this is worth
  more than any 3% of routing.
- **"Draw this in under X" mode.** Today you pick settings and *then* see a 2-hour estimate. Inverting
  it — give a target time, let the app search colour count and denoiser to meet it — is a search over
  the existing pipeline, and it only became practical because generation is now 5–8× faster.

## What the other projects in this space do

Looked at 2026-08-01, prompted by Juan finding them.

### RiiDraw (`riidraw.com`) — the real comparable

Same target as us: Tomodachi Life on an unmodded Switch, no system modification. **Closed source**,
no repository. Requires **two** RP2040/RP2350 boards (one presenting to the PC, one emulating the Pro
Controller) wired together or joined by their own adapter board.

Where we are ahead: **one** microcontroller, ESP32-S3 support, and GPL-3.0 source.

Where they are ahead, and each of these is a real gap:

| Their feature | Why it matters here |
|---|---|
| **Draw at half size, let the game's smoothing scale it up** | Potentially the largest single win available to us, and it is not a routing change at all. A 128² draw is ~1215s against ~7112s for 256² — call it **6×**. If in-game upscaling is visually acceptable, no amount of TSP work competes with that |
| Automatic cursor calibration | We hard-code menu coordinates and hope. This is exactly the class of thing item §1 of the hardware checklist exists to catch |
| Dithering controls | Ours died as dead code in the ImageSharp → ColorQuant move. With only 32 in-game colours, dithering would visibly help gradients — at the cost of more points, so it is a user-facing trade |
| Virtual on-screen gamepad | Manual nudging during setup, without unplugging the board |
| Matte options for items and clothing | We only do canvas drawings |

**The half-size trick is worth investigating before any further routing work.** It needs one
experiment on hardware: draw something at 128², scale it in-game, and judge whether the result is
acceptable. If it is, the right feature is "draw at half size" as a checkbox, not another 3% of route.

### tmdc (`tmdc.iwannasun.cn`) — different thing entirely

Chinese-language web tool for **3DS** Tomodachi Life, plus a moderated community **gallery** of
downloadable designs (food, pets, clothing, wallpapers). Currently in maintenance with generation and
sharing disabled "due to abuse". Not a competitor — different console, and the interesting part is the
gallery model rather than the input path.

The takeaway is the gallery, not the tool: a way to share a *drawing definition* (our `.tdld` is
already a portable, board-agnostic file) would cost little and is the kind of thing that makes a tool
spread. Filed as an idea, not a plan.

## Order

1. **Try the half-size trick first.** One hardware experiment, and if it works it dwarfs everything
   below it.
2. §4's cheap half — needs a hardware calibration of the slider slew rate, modest payoff.
3. §3 — only if the diagonal observation comes back positive.
4. Layer ordering (§2), but only with a cost model built on real route endpoints rather than
   centroids. Not worth attempting before that.

Everything here is post-0.5.0.
