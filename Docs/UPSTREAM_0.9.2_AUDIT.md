# Upstream 0.9.2 integration — 2026-09-08

The September 1 audit stopped at `d413307`. GitHub's API reports 15 subsequent commits
through **0.9.2**, `f3bc3895f1679e1b0e2efda21274a5ee6dd40ddb`.
Unlike the previous dependency-only update, this range changes drawing behaviour.

Sources: [complete comparison](https://github.com/Lucas7yoshi/TomodachiDrawer/compare/d413307...f3bc3895f1679e1b0e2efda21274a5ee6dd40ddb),
[0.9.2 release](https://github.com/Lucas7yoshi/TomodachiDrawer/releases/tag/0.9.2).

### September 9 re-check: upstream is now 0.9.4

GitHub API reports **0.9.4**, published September 9 at 09:01 UTC, with 11 commits after
0.9.2. This candidate and its measured comparison retain the approved **0.9.2** baseline;
it is no longer the latest upstream release. The new range was reviewed, not ported.

The new drawing optimizations omit redundant outlines against the physical full-canvas
boundary and restore an unchanged brush without reopening its submenu. They require new
geometry and toolbar checks. Shorter background-fill waits reduce the current safety margin
for only 0.25/0.5 seconds saved per drawing. None establishes a necessary reliability fix.

Other changes reduce instruction/pixel-processing allocations, change timing APIs to float,
debounce previews, and update System.IO.Ports to 10.0.12. These are deferred from this frozen
integration. In particular, review found that the async preview code disposes a superseded
cancellation source while its worker can still access `cts.Token` on completion; V2's current
synchronous preview does not use that lifetime pattern. Do not copy that implementation blindly.

Sources: [0.9.4 release](https://github.com/Lucas7yoshi/TomodachiDrawer/releases/tag/0.9.4),
[0.9.2 to 0.9.4 diff](https://github.com/Lucas7yoshi/TomodachiDrawer/compare/0.9.2...0.9.4).

## Decisions implemented for V2

| Upstream change | V2 implementation |
|---|---|
| Longer brush-menu taps and explicit bucket delays, including Switch 1 | Port the final sequence, including restoring the brush before cursor travel between fills. Preserve explicit millisecond timings in both TDLD versions. |
| Compare ordinary painting with outlines plus bucket fills | Build both from the same entry cursor and toolbar state; emit only the cheaper plan under nominal timing. Keep their command and trace buffers separate. |
| Reuse earlier painted layers as flood boundaries; reorder layers to reduce outlines | Port the geometry and ordering. Respect explicit reverse order. Freeze geometry before parallel work; the initial background colour is not treated as a completed wall. |
| Dedicated bucket setting, enabled by default upstream | Add an independent persisted checkbox, **off by default**. The old experimental flag never enables it implicitly. Minimum interior size remains 36 pixels. |
| Exact solver threshold raised from 16 to 17 | Keep V2's threshold of 12 and its cursor-aware exact solve during sequential emission. Preserve V2's cost model and optimal cycle cut. |
| Avalonia 12.1.2, SkiaSharp 4.151.2 | Update application, Core, debug tools and matching Linux native assets together. Verify decoded inputs and quantized references across versions. |
| Sentry 6.10.0 | Omit: V2 does not include Sentry. |
| `TapNTimes` helper, `TotalPixels` logging field and comment changes | Retain equivalent explicit tap loops and V2's own logging. These do not require a protocol or firmware change. |

Upstream ports are manual because the repositories have no shared ancestry. Upstream tags
remain under `refs/upstream-tags/`; V2 release tags remain under `refs/tags/`.

GitHub API also showed five new V2 dependency PRs on September 8: **#9/#10** propose the
Avalonia/SkiaSharp versions included here. **#6** proposes Microsoft.NET.Test.Sdk 18.9.0 and
**#7/#8** propose xUnit runner/framework 4.0.0. Those three test-infrastructure updates remain
outside this drawing change; the verified test package versions are recorded in the project.

## Multithreading review

Each plain/bucket candidate has its own stable ID. The pre-solve key includes that ID, phase
and brush size; worker completion order cannot choose which layer receives a route. Workers
receive copied point lists and solver options. Cursor movement, toolbar state and choosing
the winning candidate remain sequential.

The review found that V2's parallel solver did not forward the early-exit settings. Both paths
now use one immutable settings object, with tests observing the actual OR-Tools invocation.
Cancellation propagates instead of becoming a fallback route; other optimizer failures retain
every point and report the fallback. Candidate state is restored even when planning throws.

The toolbar review also found an upstream edge case: restoring an unknown brush discarded
the requested slower timing. V2 forwards that setting through the fallback selection.

## Evidence and limits

The comparison freezes V2 0.5.0 (`88b299f8b9731632793ebdfef8dbdd3659ed4d4c`), pre-change main
(`ae5dbe35350e5354f0e7b0bb3565f52b31060f91`), pristine upstream 0.9.2 and this candidate.
An observation-only upstream copy records paint intent. Its exact patch is retained and its
TDLD output is checked against pristine upstream on deterministic fixtures before measuring.

The revised focused comparison passes **38/38 drawings**; see
[COMPARISON_0.6.0_RC1.md](COMPARISON_0.6.0_RC1.md). Juan stopped the original 624-run matrix
because of its duration; it remains incomplete. The reproducible procedure is in
[Comparison/README.md](../Comparison/README.md), and the revised acceptance scope is in
[IMPLEMENTATION_0.6.0.md](IMPLEMENTATION_0.6.0.md).

Different time-limited TSP runs can produce different valid routes. Rendered pixel equality,
decoded command validity and byte-identical serialization of the **same recorded route** are
separate checks. Earlier documents' claims that parallel and serial exports must be byte-identical
are not the contract used here.

Offline checks cannot establish real game-menu coordinates or successful HID delivery. The
candidate remains a pre-release pending the [Switch checklist](HARDWARE_VALIDATION_0.6.0.md).
