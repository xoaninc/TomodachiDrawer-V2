# Upstream sync marker

TomodachiDrawer V2 is an independent repo with **no shared git ancestry** with
upstream (`Lucas7yoshi/TomodachiDrawer`) — history was rewritten, so `git merge`/
cherry-pick against `upstream` do not work. Upstream changes are ported by hand.

To find what's new since the last sync, diff from the marker below:

```sh
git fetch upstream
git diff <last-synced-commit> upstream/master
```

Read individual ports with `git show <hash> -- <path>` or
`git show upstream/master:<path>`. After porting, update the marker here.

### ⚠ Do not fetch upstream's tags into `refs/tags`

V2 and upstream use the same `X.Y.Z` tag scheme, so upstream's tags **collide with ours**. A
plain `git fetch upstream --tags` silently takes over the version numbers we have not released
yet — it is how `0.5.0` ended up pointing at one of Lucas7yoshi's commits, which would have made
tagging our own 0.5.0 fail.

The remote is configured to keep them apart, and it should stay that way:

```sh
git config remote.upstream.tagOpt --no-tags
git config remote.upstream.fetch '+refs/heads/*:refs/remotes/upstream/*'
git config --add remote.upstream.fetch '+refs/tags/*:refs/upstream-tags/*'
```

Upstream releases are then referenced as `upstream-tags/0.8.3`, and `refs/tags/` contains only
V2's own releases. Check with `git tag -l` — if you see anything past our latest release, the
namespace has been polluted again.

## Last synced

- **Through:** `upstream/master @ 8720417` ("CSharpier format pass", one commit past
  upstream tag **0.8.3** = `a50213c`, i.e. `upstream-tags/0.8.3`)
- **Fork point:** upstream `d5f64bc` (merge of PR #38, virtual-gamepad-sink) =
  our pre-V2 baseline.
- **Date:** 2026-07-29
- **Previous marker:** `6ce4683` (upstream tag 0.6.0), 2026-05-26 — 122 commits ago.
- Upstream has already moved past this marker (dependabot bumps). Nothing behavioural, but
  re-diff before assuming this list is still current.

Full analysis of this sync: [`Docs/UPSTREAM_SYNC_AUDIT.md`](./Docs/UPSTREAM_SYNC_AUDIT.md).

## Ported in this sync (0.6.0 → 0.8.3)

### Dependencies
- **SixLabors.ImageSharp → JeremyAnsel.ColorQuant** (`85ab810`, `4bfef74`). Changes palette
  selection for "Arbitrary" mode, so it landed before the measurement baseline. Losing the
  Floyd-Steinberg dither path cost nothing — `GetQuantizerSettings()` passed `default` for
  `useDithering` in both branches, so it was already dead code.
- **SkiaSharp 3.119.2 → 4.150.1** plus an explicit `SkiaSharp.NativeAssets.Linux` pin (V2 had
  none and publishes both `linux-x64` and `linux-arm64`).
- **Avalonia 12.0.3 → 12.1.0** together with `a50213c`, the `Bitmap.Save(path)` deprecation fix.
  `DebugTools` was still on 12.0.3, so the solution had two Avalonia versions coexisting.

### Routing (upstream `7b22452`, `0c366c1`)
- `PerformTSP` and friends split into `CanvasDrawer.Tsp.cs`, matching upstream's layout.
  **Kept V2's nullable signature** — upstream's internalises the fallback and can never report
  failure, which would make `tspHasSolution` permanently true and silently change the
  snake-vs-TSP decision.
- OR-Tools native objects are now disposed (`using var`) at both sites, including
  `RouteSolver.Solve`, which runs on ~80% of cores.
- **Held-Karp exact solver** for small point sets, in the **serial path only**: it starts from
  the live cursor, which the parallel pre-solve cannot know. `ExactMaxPoints` is **12**, not
  upstream's 16 — n=16 allocates 8 MB of LOH and runs ~8.4M inner iterations per call, to beat
  a 0.25-0.5s OrTools solve.
- Recommended TSP times raised to upstream's (1/3/4/5s). Stamp and bucket limits deliberately
  unchanged — upstream did not change those either.
- Early-exit / improvement-limit support, **default off** (as upstream ships it).
- Solver configuration now has exactly one definition, `RouteSolver.BuildSearchParameters`,
  shared by both paths so they cannot drift.

### Drawing correctness
- **Colour merge** (`7086620`): colours reaching the same in-game HSV steps collapse into one
  layer via `ColourPickerRouter.CanonicalKey`.
- **Erase-all before bucket fill** on full-size Switch 2 images (`0ae89ff`), so a pixel on the
  top-left cell can't make the bucket fill just that pixel. Includes upstream's fix to the
  bucket submenu homing, which looped rows for the horizontal move.
- **Switch 1 brush-menu lag mitigation** (`47331ff`, upstream #120 — splotchy images).
- **`ReverseColourOrder`** setting (`4419bf6`).
- **RP2040/RP2350 firmware**: neopixel blue for dpad-only input, brightness tweaks
  (`01074f2`, `1bba220`).

### Crash fixes
- `e5ba374` + `022d114` — the "Weezer" crash (#123): a source image with no alpha channel in
  its `ColorType` made `MedianDenoiser` read past the pixel span. Ported in the final form of
  both commits: the first version disposed the *caller's* bitmap when no conversion was needed,
  which was upstream's 0.7.0 crash.
- `14dd54a` — `SafeNumericUpDown` (#119): with a screen reader active, Avalonia's ComMarshaller
  throws on the decimal value. See Avalonia#21491.
- `c2e6fa4` — template tool default height.

### CI / hygiene
- `actions/checkout` v6→v7, `actions/setup-dotnet` v5→v6, `actions/cache` →v6.
- pico-sdk caching (`81955cb`).
- Dependabot config (`e198d0e`, `f5e0bd9`, `228289d`) and a bug-report template (`c359ca4`),
  adapted for V2.

### V2-only work in this release (not from upstream)
- **`CutCycleNearCursor`** replaces `OrientFromCursor`. The old one only reversed, considering 2
  of 2n candidate cuts. An OrTools tour is a closed cycle whose cost is rotation-invariant, so
  the best cut is found over all 2n candidates in one O(n) pass. Cutting a Chebyshev-1 arc splits
  an A-hold run, which is priced at exactly **+1 tap** (its real cost) and only for the
  fine-detail phase — the stamp and bucket phases emit one plain `Tap(A)` per point and have no
  hold runs at all. **Result: the parallel path now beats the serial one on both pen travel and
  drawing length**, where before it was 1-12% worse.
- **`PreSolvedRoute`** tags each pre-solved route as a closed cycle or an open path, so the
  nearest-neighbour fallback (which is open, with no closing arc) is only ever emitted from one of
  its two real ends instead of being cut mid-way.
- **`RouteFingerprint`** moved the route-cache key into Core, where a reflection-driven test
  asserts every `DrawImageSettings` property invalidates the cache. Missing one means Export
  silently flashes the *previous* drawing while logging that the route is current.
- **`TomodachiDrawer.Bench`** — route cost, coverage and generation wall-clock across three arms
  (serial / parallel / parallel pinned to one thread). See [`Docs/bench/`](./Docs/bench/).
- **`TomodachiDrawer.Core.Tests`** — 47 tests. The permutation invariant is the important one: a
  dropped point makes route cost *and* draw time go down, so it is the one regression the
  headline metrics would report as an improvement. `CutCycleTests` additionally brute-forces all
  2n candidate cuts to prove the chosen one is optimal.
- csharpier pinned at 1.3.0 in `.config/dotnet-tools.json`, verified to reproduce upstream's
  formatting byte for byte.

## Intentionally skipped

- **Telemetry (PR #88, `009efcb`)** — phones home to `telemetry.l7y.media` (upstream author's
  own server). Not appropriate for an independent fork.
- **Sentry crash reporting** (`31affe2`, `5dd17d1`, `b32975f`, `6d19f10`, `06e009a`) — decided
  against for V2. Note this is a *separate* decision from the telemetry skip above: Sentry is a
  third-party crash reporter, not the author's own server. The **useful** half of `ef568b7` was
  taken separately: not its dialog (V2 already had one, inherited from Podter's pre-fork macOS
  work) but its field finding — that passing the drive-access pre-check does not guarantee the
  write succeeds. The UF2 write is now guarded and retries once. The rest of that commit is
  Sentry instrumentation its own author labels temporary.
- **`5dd17d1`'s property-group unification** (the non-Sentry half) — upstream's `BundleMacOSApp`
  group carries `<PublishTrimmed>true</PublishTrimmed>`, and V2 deliberately disabled trimming
  (`ae7f711`: it stripped ESPTool's chip detection and broke ESP32 flashing).
- **UI state-context refactor** (`355e831`, `31371a5`, `fe3d6fd`) — pure restructuring of
  `MainWindow.axaml.cs`, the file the two forks have diverged on most. All cost, no user-visible
  gain.
- **`8400352`** (pin the ESP-IDF action to a commit hash) — V2's workflow has no ESP-IDF job;
  the ESP32 firmware ships as a committed binary, so there is nothing to pin.
- **Upstream's ESP32-S3 port** (`dangoodie`'s PR #46 and follow-ups) — V2 has its own,
  independently written ESP32-S3 stack with host unit tests and MD5-verified flashing. See the
  audit's attribution section for the timeline.
- **Welcome-message and branding commits** — V2 has its own.
- **`NoImagePlaceholder.png`** swap — cosmetic.
