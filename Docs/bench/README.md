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

### Reading it correctly

- The **parallel-1thread** arm is the one to compare against serial. Plain `parallel` shares
  cores, and OR-Tools is wall-clock limited, so part of its route penalty is CPU contention
  rather than cut quality. Stage 2.3 targets cut quality, so it must be judged against the
  1-thread arm.
- `DPadTaps` includes menu travel as well as canvas travel. The absolute number is therefore
  not pure route cost — but menu travel is identical across arms for a fixed image and
  settings, so **deltas** are pure route cost.
- Draw seconds come from `TimingSink`, the same estimator the app shows the user.
