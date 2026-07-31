# Ideas after 0.5.0 — four findings, with decisions

Found by reading the code on 2026-07-31, after the 0.8.3 sync shipped to `main`. All four are
**post-release**: `main` at `90eec34` is what the 0.5.0 hardware gate validates, so nothing here
gets touched until 0.5.0 is tagged.

Two of the four are TODOs upstream wrote themselves and never did. All four are now *measurable*,
which is the difference from before: `TomodachiDrawer.Bench` prices any routing change in DPad taps,
and the intent-trace renderer proves a change did not drop or misplace a single cell.

| # | Finding | Decision |
|---|---|---|
| 1 | The TSP objective minimises travel, not taps | **Do first.** Keep only if the bench measures it |
| 2 | Layer order is unoptimised (upstream's own TODO) | **Do second.** Feasible and safe |
| 3 | The colour grid navigates Manhattan, not Chebyshev | **Pending** one observation on hardware |
| 4 | The 4250 ms colour-homing delay | **Risky half rejected.** Cheap half is lowest priority |

---

## 1. The TSP objective minimises travel, but what you pay is taps

**Do this first.** Smallest change, mechanically checkable, and it improves upstream's serial path
too — so if it measures, it joins the `pr/optimal-tsp-cut` PR rather than needing its own pitch.

The arc cost handed to OrTools is plain Chebyshev at all three sites:
`RouteSolver.cs:111`, `CanvasDrawer.Tsp.cs:259`, and Held-Karp's inner loop at `CanvasDrawer.Tsp.cs:124`.

But the cut work already derived the real emission cost. `FineDetailTsp` **holds A** across
Chebyshev-1 neighbours, so:

- an arc with `d == 1` costs **1 tap** (the hold run continues)
- an arc with `d > 1` costs **`d + 1`** (the run breaks: one release, one press)

So the correct callback is `d + (d > 1 ? 1 : 0)`. This is the same insight as the optimal cut,
applied to the *objective* instead of the *cut point*: the solver currently has no reason to prefer
keeping hold runs contiguous, because it cannot see that breaking one costs anything.

**Two constraints, or this reintroduces the bug the cut already had once:**

- **Fine-detail phase only.** Stamps and bucket clicks emit one plain `Tap(A)` per point and have
  no hold runs, so the `+1` is meaningless there. Applying it globally is exactly the error the
  lexicographic cut rule made.
- **`HeldKarpRoute` returns an open path**, not a cycle. Changing its objective is fine, but check
  it does not interact with the open-path branch in `OrientPreSolved`.

**This may not measure, and that is an acceptable outcome.** The bias is systematic but small.
Gate: run `--tsp 1.0 --colours 32 --repeats 2`, require `paint` constant and `render: 100,00%` on
all nine arms, and require the tap counts to move by more than run-to-run noise (which is ~0.01–0.7%,
since `--tsp` is a wall-clock budget and OrTools is nondeterministic under load). If it does not
move, drop it — do not ship a correct-but-immeasurable change on the strength of the derivation.

## 2. Layer order is unoptimised, and it is upstream's own TODO

**Feasible, and the renderer is what makes it safe to try.** `CanvasDrawer.cs:99-100`, verbatim:

> *"Color Pass Order Optimizations for both stamp pass and fine detail pass to minimize travel
> distance. Need to look at the ai-slop version to try and figure out how that works there."*

Today layers come out of `ColourPalette.BuildFineLayers` in whatever order the distinct-colour walk
produces, and the only control is `layers.Reverse()` behind `ReverseColourOrder`
(`CanvasDrawer.cs:121`). With 32 layers that is **2 of 32! orderings**.

Ordering ≤32 layers is a tiny TSP — OrTools solves it in milliseconds, negligible beside the
per-layer routing.

**Why reordering is safe:** each pixel belongs to exactly one colour, and a stamp is only placed
where its whole footprint is already that one colour, so layers do not overwrite each other and the
final image is order-independent. Two things to respect:

- The **bucket fill must stay first** — it paints the whole canvas, which is where mosaic256's
  `overdraw=61634` comes from.
- The renderer is the check, not the argument: if reordering did disturb the image, `wrongColour`
  and `extra` would stop being 0.

**Where it pays, and where it does not** — state this or the estimate looks inflated:

- **Grid palette:** pays twice. The picker navigates from `_lastGridX/_lastGridY`
  (`ColourPalette.cs:334-335`), so order genuinely determines inter-colour travel. Plus travel to
  the next layer's first point.
- **Arbitrary palette:** pays once. The homing in §4 makes picker cost order-independent, so the
  only gain is travel to the next layer's first point.

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

## Order

1. §1 — smallest, provable, and joins the upstream PR if it measures
2. §2 — feasible, safe, upstream's own TODO
3. §4's cheap half — needs a hardware calibration, modest payoff
4. §3 — only if the diagonal observation comes back positive

Everything after 0.5.0 is tagged.
