# Upstream sync audit — 2026-07-29

Range audited: `6ce4683` (our sync marker, upstream tag 0.6.0) → `upstream/master`
(`a50213c`, upstream tag **0.8.3**).

**122 commits, +4721 / −1518 across 80 files, 15 releases (0.6.1 → 0.8.3).**

The real porting surface is much smaller than that diffstat suggests. Two large
subtractions: Daniel Gooden's ESP32-S3 port (a *collision* with ours, not an
improvement to port — see §0/§1), and upstream's csharpier/analyzer passes, which
account for most of the rest. Verified examples of the latter:

- `ImageProcessing/TomodachiLifeMask.cs` **+108** on a 126-line file — **100% csharpier**
  (`b8c3d1d`). No drawing-behaviour change.
- `OutputSinks/FileControllerSink.cs` **+6/−6** — this is the **`.tdld` format
  definition**, so it was checked specifically: the change is `sealed`, one removed
  unused `using`, and one reformatted early-return. **The `.tdld` binary format is
  unchanged**, so our ESP32 parser and firmware stay compatible. No action needed.
- `ColourPalette.cs` **+77** — `7086620` (+27, bucket A) plus csharpier, the analyzer
  pass, and the ColorQuant swap. Nothing unaccounted.

---

## 0. Attribution: the ESP32-S3 question — settled, and not in our favour

Recorded here because it has to stop costing us cycles.

| Fact | Evidence |
|---|---|
| Upstream PR **#46** "Add ESP32-S3 firmware port and Avalonia Integration" by **`dangoodie`** opened publicly | **2026-05-18 16:31 UTC** |
| First ESP32 commit in this repo (`13cfa62`) | **2026-05-24 19:51 +0200** — 6 days later |
| Juan commented on PR #46 announcing V2 | 2026-05-24 21:27 UTC |
| Upstream issues/PRs referencing `xoaninc` or `TomodachiDrawer-V2` | none, other than that comment |
| PR #46 merged as upstream 0.7.0 | 2026-06-03 |

Upstream's ESP32-S3 support was public before ours existed, and was contributed by a
third party (Daniel Gooden), not lifted from us. Nothing was taken from V2.

The two ports are also **independent implementations**, so no credit is owed either
way:

| | Upstream | V2 |
|---|---|---|
| Firmware layout | one 435-line `main/TomodachiDrawer.Firmware.c` | modular `tdld_parser.c` / `led_indicator.c` / `hid_gamepad.c` |
| Tests | none | host unit tests + on-target + HID enumeration integration test |
| Board config | Kconfig, 7 presets | fixed GPIO (DevKitC-1) |
| Flasher | bundled Python `esptool` binaries via `Process.Start` | C#-native `ESPTool` NuGet |
| Write integrity | none | `FLASH_END` + flash-MD5 verify + re-flash |

Both hit the 7-vs-8-byte HID report bug and fixed it **differently** — upstream
`63aef13` sends 8 while the descriptor still declares 7; ours `a6d4d90` declares 8 in
the descriptor. Divergent fixes to the same observation is what independent discovery
looks like, not copying.

What *is* ours and unclaimed by anyone: parallel route generation, MD5-verified
flashing, corruption-safe parsing, and the firmware test suite.

---

## 1. Decision that gates everything else

**Upstream now ships ESP32-S3 itself** — Kconfig board matrix with 7 presets
(DevKitC-1 r38/r48, DevKitM-1, S3-Zero, QT Py S3, Lolin S3 Mini, AtomS3), a board
picker in the UI, and CI-built per-board firmware. It already had RP2350 (their PR
#55, which we ported inward).

That was V2's reason to exist. So the question is no longer "which upstream commits do
we port" but **what V2 is for**. Three options:

### Option A — Fork stays a product; port Core only, freeze UI parity
Keep our ESP32 stack and our UI. Port upstream's Core fixes and drawing improvements.
Accept that the UI diverges permanently and stop chasing it.
- **Cost:** moderate and recurring. Core ports are cheap (see §2), UI features get
  re-implemented by hand or skipped.
- **Risk:** we fell 122 commits behind in 2 months once already. This is a treadmill.

### Option B — Adopt upstream's ESP32S3 stack, re-apply our differentiators on top
Delete `TomodachiDrawer.Firmware.ESP32/` + `EspFlasher.cs`, take upstream's, then
re-apply MD5 verify and corruption-safe parsing as patches.
- **Gain:** 7 board presets for free, one less subsystem to maintain.
- **Cost:** loses our test suite; adds bundled Python esptool binaries (bigger
  downloads, GPLv2 compliance paperwork we currently don't need).

### Option C — Contribute the differentiators upstream, keep V2 thin ⭐ recommended
Our strongest card is **parallel route generation** (`b3b1b80`): ~6–7× faster, output
byte-identical, self-contained in Core (`RouteSolver.cs` + `RoutingPhaseKind.cs` + one
block in `CanvasDrawer.cs`). Upstream has no equivalent, and their README explicitly
says *"the main areas for improvement are optimizations to the routing logic."*

MD5-verified flashing and corruption-safe `.tdld` parsing are similarly small,
obviously-correct PRs.

- **Gain:** real, attributed credit in the upstream history — which addresses the
  actual grievance far better than a fork nobody syncs. Kills the treadmill.
- **Cost:** V2 stops being a separate product. Upstream must accept the PRs.
- **Note:** the parallel-routing PR needs rebasing onto upstream's new
  `CanvasDrawer.Tsp.cs` (see §2, landmine).

**⚠ Know the headwind before writing that PR.** Upstream's README asks contributors to
*"refrain from using AI irresponsibly"*, and specifically about routing says: *"The main
areas for improvement are optimizations to the routing logic, I strongly discourage
letting AI go loose on this as well, as I found my prior ai-slop-proof-of-concept
version was actively slower than even the more simpler logic in the first iteration."*
Every commit in this repo is AI-assisted. A routing PR is precisely what that maintainer
is primed to reject on sight.

This does not make Option C wrong — it dictates how the PR is written. **Lead with the
verification, not the speedup.** The claim that sells it is *"output is byte-identical
to the sequential path"*, because that is objectively checkable and removes the entire
"AI made it worse" risk: it cannot be slower-per-quality, since the routes are the same
routes. Ship it with a reproducible A/B harness (same image, same seed, same TSP time
limit, diff the emitted `.tdld` bytes) and let the maintainer run it. Do not open the PR
without that harness.

Not mutually exclusive: C for the routing work + A for a slim V2 is coherent.

---

## 2. Porting cost, measured

Divergence from the sync marker `6ce4683`:

| Area | Our changes | Upstream changes | Verdict |
|---|---|---|---|
| `TomodachiDrawer.Core` | 4 files, +308/−22 | ~20 files | **Cheap** — Core is near-pristine upstream |
| `MainWindow.axaml.cs` | +486/−247 | +927/−274 | **Hand-work** — both rewrote it from the same base |
| `MainWindow.axaml` | +85/−16 | +457/−312 | **Hand-work** |
| `TomodachiDrawer.Firmware` (RP) | +33/−5 | +9/−5 | **Cheap** |

Our whole Core divergence is: new `RouteSolver.cs`, new `Models/RoutingPhaseKind.cs`,
a ~208-line parallel pre-solve block in `CanvasDrawer.cs`, and a 4-line cosmetic
`ColourLayer.cs` revert. Everything else is upstream verbatim.

### ⚠ The one landmine
Upstream 0.8.0 (`7b22452`) moved `PerformTSP`, `NearestNeighbourRoute`, `HeldKarpRoute`
and `GetRecommendedTSPSolveTime` out of `CanvasDrawer.cs` into a new
**`CanvasDrawer.Tsp.cs`**, and changed time-limit defaults, added early-exit, and made
fallbacks more consistent.

`RouteSolver.Solve` **duplicates the OR-Tools configuration of `PerformTSP`** (same
Chebyshev arc cost, PathCheapestArc + GuidedLocalSearch, time limit). If we port
upstream's TSP changes without mirroring them into `RouteSolver`, the parallel path
silently diverges from the sequential one and we lose the byte-identical-output
property V2 advertises. Treat these two files as a single change, always.

---

## 2b. Where upstream's work makes **V2 specifically** better

Three findings that are worth more to V2 than they are to upstream, because they compound
with our parallel pre-solve:

### ⭐ Held-Karp exact solver for ≤16 points (`7b22452`)
Upstream added `HeldKarpRoute` with `ExactMaxPoints = 16`: point sets of 16 or fewer are
solved **exactly and instantly**, bypassing OR-Tools entirely instead of burning the whole
time limit on a tiny instance.

A 256×256 image produces many small per-colour/per-stamp-size layers, and today we pay the
full OR-Tools limit on every one of them. Taking this gives **both** shorter draws (optimal
routes, not heuristic ones) **and** faster generation. It is the single highest-value item
in the whole audit for us.

### ⭐ We can afford upstream's more generous TSP time limits; they can't
Upstream raised the recommended solve times:

| Image | V2 today | Upstream 0.8.0 |
|---|---|---|
| ≤64² | 0.5 s | 1.0 s |
| ≤128² | 1.5 s | 3.0 s |
| ≤192² | 2.75 s | 4.0 s |
| ≤256² | 4.0 s | 5.0 s |

For upstream that is a straight ~2× slowdown in route generation, paid serially. For V2 it
is nearly free: we solve across ~80% of cores, so we can adopt the higher limits — **better
routes, shorter draws** — and still finish in less wall-clock time than upstream does. This
is a differentiator we can actually market, and it only exists because of the
parallelization.

Also worth taking: the **early-exit / improvement limit** (`EarlyExitRateCoefficient`,
`EarlyExitSolutionsDistance`) which stops a solve that has plateaued, and the TSP knobs
tool (`0c366c1`, defaulted off).

### 🐛 A real bug in our own parallel code, exposed by their diff
Upstream's new `PerformTSP` wraps the OR-Tools objects in `using var`:

```csharp
using var manager = new RoutingIndexManager(points.Length, 1, closestPointIndex);
using var routing = new RoutingModel(manager);
```

**We dispose neither**, in either of our two sites — `CanvasDrawer.cs:949-950` and
`RouteSolver.cs:51-52`. These are native (SWIG-wrapped) objects, so the memory is not
under the GC's control.

This is worse for V2 than for upstream: `RouteSolver.Solve` runs concurrently on ~80% of
cores, so peak undisposed native memory is multiplied by the thread count. Fix both sites.

### Bonus: the AI headwind is softer than it looks
`HeldKarpRoute` carries this comment from the upstream author himself:

> *"Full disclosure, this ONE function is written by AI as i was struggling to get it to
> work. However this is a fairly standard algorithm so i don't feel too bad about this
> particular case but I nonetheless believe in being transparent about it."*

So the maintainer's position is **transparency, not prohibition**. That materially improves
the odds for the Option C routing PR — disclose the method, lead with the byte-identical
verification, and it lands in a culture that has already accepted AI-written routing code
under exactly those terms.

## 3. Commit-by-commit buckets

### A. Port — drawing correctness & quality (highest value)

| Commit | What | Notes |
|---|---|---|
| `7086620` | Optimize out colours that map to the same in-game colour | Adds `ColourPickerRouter.CanonicalKey`; merges RGB codes with identical HSV steps → fewer redundant layers → shorter draws. Real time saving. |
| `0ae89ff` | Clear canvas ("erase all") on Switch 2 for full-size images | Fixes bucket fill filling a single pixel when a pixel lands top-left |
| `47331ff` | Mitigate Switch 1 brush-menu lag during homing | Fixes splotchy images (#120) |
| `4419bf6` | `DrawImageSettings` refactor + `ReverseColourOrder` | Cleans up construction, adds a user-facing option |
| `01074f2` + `1bba220` | RP firmware: neopixel blue for dpad-only input, brightness tweaks | Applies directly to our RP2040/RP2350 firmware |

### B. Port — crash fixes (cheap, clearly correct)

| Commit | What |
|---|---|
| `e5ba374` | Weezer crash — `MedianDenoiser` read past the alpha channel when source `ColorType` had none (#123) |
| `022d114` | 0.7.0 crash — `MedianDenoiser` + MainWindow |
| `14dd54a` | `SafeNumericUpDown` — guards null/empty numeric input (#119) |
| `a9224bd` | Crash on template confirm |
| `c2e6fa4` | Template tool default height |
| `725faf2` | Snapshot `_currentImage` for tdld/esp32 export — NRE when path decode fails |

### C. Port — UI features (hand-work, pick per value)

| Commit | What | Worth it? |
|---|---|---|
| `38ffcae` | Cancel in-progress draws | Yes — big UX win |
| `9de75cd` | Persist drawing option settings (#107) | Yes |
| `07aa8b9` | `settings.json` → AppData, logic into `AppSettings.cs` | Yes — we already have a thin `AppSettings.cs` (22 lines vs their 125) |
| `421991e` | Shrink default window, more generous min size (#133, #130) | Yes — cheap |
| `a1732a9` | "Export to .tdld" button | Yes — cheap, useful standalone |
| `6da20f1` | Switch 1 desync warning on selection | Yes — cheap |
| `88b0190` | Show image size in Preview header | Optional |
| `de36010` | uf2 export tooltip | Optional |
| `355e831` + `31371a5` + `fe3d6fd` | UI refactor into a state context, debug settings split | **Skip** — pure restructuring of a file we've diverged on; all cost, no user-visible gain |

### D. Decide explicitly

| Commit | What | Recommendation |
|---|---|---|
| `7b22452` (0.8.0) | TSP defaults, early-exit, `CanvasDrawer.Tsp.cs` split, consistent fallbacks | **Port, carefully** — adopt the split so future syncs are cheap, but mirror config into `RouteSolver` and re-verify parallel==sequential output |
| `0c366c1` (0.8.1) | TSP tweaking knobs + `EarlyTspExitTool`, defaulted **off** | Port after `7b22452`. Author says the values confuse him — low confidence, keep off |
| `31affe2` + `5dd17d1` + `b32975f` + `6d19f10` | **Sentry** crash logging, session tracking, symbol upload, compression | **Its own yes/no** — this is a third-party crash reporter, *not* the author's own server, so our PR #88 telemetry objection does **not** cover it. Genuinely useful for a released binary. Needs a privacy note in the README if adopted |
| `ef568b7` | macOS crash tracking + fail-state handling | Extract the non-Sentry fail-state handling even if Sentry is declined |
| `85ab810` | SixLabors.ImageSharp → JeremyAnsel.ColorQuant | **Port** — likely also a licensing improvement: ImageSharp 3.x moved off Apache-2.0 to the Six Labors Split License, while ColorQuant is MIT. Confirm both licences before citing this as the reason, but the dependency swap is worth taking regardless |

### E. Skip (extends existing SYNC.md rationale)

| Commit | Why |
|---|---|
| `009efcb` | Image telemetry (app version + microcontroller type) → already covered by our PR #88 skip |
| `11da70f` | Welcome message — upstream branding |
| `d182dd5`, `a6ff892` | esptool GPLv2 source-offer compliance — N/A, we don't bundle esptool |
| `832c9bd`, `2fbc76e`, `73ef84d`, `8f265a8` | Upstream README/doc wording — cherry-pick facts only (e.g. the Switch 1 undocked note from #97 is worth having) |

### F. Mechanical batch (do in one commit each, low thought)

- **Dependency bumps:** SkiaSharp 3.119.2 → **4.150.1** (+ the `SkiaSharp.NativeAssets.Linux`
  explicit pin, `04a26e7`/`70d4ca1` — upstream hit repeated Linux packaging weirdness,
  expect the same), Avalonia 12.0.3 → **12.1.0**, System.IO.Ports → **10.0.10**.
- **CI:** `actions/checkout` 6→7, `actions/cache` 5→6, `setup-dotnet` 5→6, pico-sdk
  caching (`81955cb`), `8400352` pin the ESP action to a hash (security — worth it).
- **Repo hygiene:** `e198d0e` dependabot config, `c359ca4` issue templates.
- **Formatting/analyzers:** `c60277e`, `b8c3d1d`, `8720417`, `1a84ec7`, `a50213c`.
  Do these **last** — they touch everything and would poison every other diff.

---

## 4. Suggested order of work

0. ~~Install the toolchain.~~ **Done 2026-07-29.** .NET SDK **10.0.302** installed via
   `brew install dotnet` (the bottled formula, not the `.pkg` cask — no `sudo`, lands in
   `/opt/homebrew`). `dotnet build TomodachiDrawer.slnx` on macOS arm64:
   **build succeeded, 0 warnings, 0 errors**, all four projects. So the pre-sync baseline
   is green and any breakage from here is ours.
   - `dotnet` resolves from `PATH` with no extra setup. Only if an IDE (Rider / the VS Code
     C# extension) fails to find the SDK, add
     `export DOTNET_ROOT="/opt/homebrew/opt/dotnet/libexec"` to `~/.zshrc`.
   - Firmware toolchains are **not** installed: ESP-IDF (ESP32-S3) and the Pico SDK +
     ARM GCC (RP2040/RP2350). CI builds both, so these are only needed for local firmware
     work. `cmake` is already present.
1. **Decide §1.** Nothing below is stable without it.
2. Dependency bumps + CI (bucket F, minus formatting). Establishes a green build —
   and is also the first real test of whether the app builds on macOS at all.
3. Bucket B crash fixes. Cheap, pure win.
4. Bucket A drawing correctness. The actual user-visible value.
5. `7b22452` TSP split + `RouteSolver` mirroring + verify parallel==sequential.
6. Bucket C UI features, highest value first (`38ffcae`, `9de75cd`, `07aa8b9`).
7. Sentry yes/no (bucket D).
8. Formatting/analyzer passes.
9. Bump version, update `SYNC.md`: marker → `upstream/master @ a50213c` (tag 0.8.3),
   date, ported list, skipped list.

## 5. Repo nits found while auditing

- `README.md:135` still calls the UI project **TomodachiDrawer.UI.Avalonia**; the
  folder is `TomodachiDrawer.V2/`. The C# namespace is still `TomodachiDrawer.UI.Avalonia`,
  so the line is half-true and confusing.
- `SYNC.md:50` lists "`ColourLayer` `new()`→`[]` syntax modernization" as skipped, but
  the sync point already had `[]` and we reverted to `new()`. Cosmetic; either revert
  the revert or fix the note.
- An `upstream` remote is now configured locally
  (`https://github.com/Lucas7yoshi/TomodachiDrawer.git`), so the `SYNC.md` diff
  procedure works as written.
