# Bench baselines

Captured by `TomodachiDrawer.Bench` (see that project's `Program.cs`). Always run in
**Release**; wall-clock numbers from a Debug build are meaningless.

```sh
dotnet run --project TomodachiDrawer.Bench -c Release -- \
  --tsp 1.0 --colours 32 --repeats 3 --out Docs/bench/baseline-stage1.json
```

## `baseline-stage1.json` — pre-Stage-2 baseline

Captured 2026-07-29 on macOS arm64, 10 cores, `tsp=1.0s`, 32 colours, best of 3, after
Stage 0's dependency bumps (so the ColorQuant palette change is already in).

| Image | Route cost: parallel-1thread vs serial | Generation speedup (parallel vs serial) | Draw-time cost of going parallel |
|---|---|---|---|
| `grid64` | **+12.27 %** | 12.08 s → 1.54 s (**7.8×**) | 245.3 s → 260.9 s (+6.4 %) |
| `rings128` | **+3.05 %** | 33.63 s → 5.55 s (**6.1×**) | 1255.4 s → 1283.0 s (+2.2 %) |
| `mosaic256` | **+1.62 %** | 59.74 s → 11.19 s (**5.3×**) | 7311.2 s → 7456.1 s (+2.0 %) |

### What this establishes

- **The README's "~6-7× faster generation" claim holds** — measured 5.3× to 7.8×.
- **The README's "byte-identical output" claim does not**, and cannot: the parallel path
  pins the OR-Tools depot to index 0 because the pre-solve runs before emission and cannot
  know each layer's starting cursor. Its routes are genuinely different, and measurably
  worse — most on the stamp-heavy image.
- **The real trade is +2 % to +6 % draw time for 5–8× faster generation**, which is
  currently documented nowhere. Stage 6 replaces the byte-identity claim with these numbers.
- `PaintActions` is **identical across all three arms** on every image (1020 / 7457 /
  32068), which is the invariant that says no points are being dropped. It is the metric to
  watch during Stage 2, because a dropped point makes both route cost and draw time go
  *down*.

## `stage2.json` — after the routing work

Same parameters, same machine. The change under test is `CutCycleNearCursor` (optimal cycle cut
replacing the reverse-only `OrientFromCursor`), Held-Karp for small sets in the serial path, and
OR-Tools disposal.

| Image | Route cost vs serial: before → after | Parallel pen travel | Parallel draw time |
|---|---|---|---|
| `grid64` | +12.27 % → **−2.36 %** | 2817 → 2451 (−13.0 %) | 260.9 s → **242.3 s** (−7.1 %) |
| `rings128` | +3.05 % → **−4.98 %** | 17231 → 15901 (−7.7 %) | 1283.0 s → **1216.2 s** (−5.2 %) |
| `mosaic256` | +1.62 % → **−3.09 %** | 120332 → 114818 (−4.6 %) | 7456.1 s → **7180.3 s** (−3.7 %) |

**The parallel path now beats the serial one on both pen travel and drawing length**, on every
image, where before it was 1–12 % worse. That is the point of the optimal cut: the serial path
cuts the tour wherever the OR-Tools depot happened to land (1 of n positions, ignoring the dropped
arc), while the new code evaluates all 2n candidate cuts in one O(n) pass.

Generation stayed fast — 7.7× / 6.0× / 5.2× versus serial — and serial generation itself got
faster on the stamp-heavy image (12.08 s → 8.06 s) as Held-Karp took over the small point sets.

`PaintActions` held at 1020 / 7457 / 32068 across all nine runs, so no points were lost.

## `stage2-fixed.json` — after correcting the cut cost model

The first cut implementation used a *lexicographic* tie-break (any arc >= 2 beat any short arc
regardless of cost) and applied it to all three phases. Adversarial verification showed the real
penalty for splitting an A-hold run is exactly one extra press/release group, and that the stamp
and bucket phases have no hold runs at all — they emit one plain `Tap(A)` per point.

Re-measured after replacing it with the exact cost model: **-2.36 % / -4.98 % / -3.07 %**, i.e.
unchanged. The synthetic bench images do not contain the pathological shape (a near-complete
single-pixel outline), so this was a robustness fix rather than a measurable one. `CutCycleTests`
brute-forces all 2n candidates on ring, gapped-ring and scattered inputs so the regression cannot
return.

One coverage number moved from 32068 to 32069 on `mosaic256`, which is the Stage 3 colour merge
(`CanonicalKey`) changing which colours collapse and therefore the bucket-fill choice by one
action. It is identical across all three arms in that run, which is the invariant that matters.

## The optimal cut also improves the *serial* path

Measured 2026-07-30 (tsp=1.0s, 32 colours, best of 2). Applying the cut inside `PerformTSP`, with
no parallelism involved at all:

| Image | Serial, OrTools' own cut | Serial, best of 2n cuts | Pen travel | Draw time |
|---|---|---|---|---|
| `grid64` | 2503 | **2436** | −2.7 % | 245.3 s → 241.7 s |
| `rings128` | 16696 | **15892** | −4.8 % | 1255.5 s → 1215.1 s |
| `mosaic256` | 117464 | **113965** | −3.0 % | 7312.7 s → 7136.4 s |

Why it works: OrTools writes the cycle out starting at the depot, so the arc it drops is whichever
one happens to sit there — 1 of n candidates, chosen without reference to how long that arc is. The
objective is a closed cycle, so its cost is rotation-invariant and we are free to cut anywhere;
evaluating all 2n candidates costs one O(n) pass.

**This is separable from the parallel work**, which matters for contributing it upstream: it is a
~40-line change that helps any serial solve.

Note the side effect: with both paths now using the same cut, `parallel-1thread` vs `serial`
collapses to ~0 % (+0.33 % / −0.17 %). That is the expected result and a useful confirmation — the
earlier 1–12 % gap really was the cut, not the depot or the greedy seed.

## Render verification

Since 2026-07-30 every bench run also replays the drawing's *intent trace* onto a canvas and diffs
it against the quantized source:

```
render: 100.00% match (65536/65536) missing=0 wrongColour=0 extra=0 overdraw=52470
```

All three images match **100%** on all three solver arms — no dropped cells, no wrong colours, no
painting outside the image. That is independent confirmation that the Stage 2 routing changes did
not lose anything, on top of the `PaintActions` invariant.

`overdraw` is not a fault on its own: on `mosaic256` the full-canvas bucket fill legitimately paints
everything before the layers paint over it. It matters in the fine-detail phase, where a non-zero
count would mean the route is revisiting cells — `RenderFidelityTests` pins it at zero for a
stamp-free, bucket-free drawing.

Compare against the **quantized** source, never the raw image: the palette moves colours by design,
so diffing the original fails on almost every pixel and tells you nothing.

Dump PNGs to eyeball with `--render-out <dir>`. When something is off, the *shape* of the wrongness
usually identifies the phase faster than the percentage does.

### Reading it correctly

- The **parallel-1thread** arm is the one to compare against serial. Plain `parallel` shares
  cores, and OR-Tools is wall-clock limited, so part of its route penalty is CPU contention
  rather than cut quality. Stage 2.3 targets cut quality, so it must be judged against the
  1-thread arm.
- `DPadTaps` includes menu travel as well as canvas travel. The absolute number is therefore
  not pure route cost — but menu travel is identical across arms for a fixed image and
  settings, so **deltas** are pure route cost.
- Draw seconds come from `TimingSink`, the same estimator the app shows the user.
