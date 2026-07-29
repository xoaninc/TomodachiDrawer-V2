# V2 improvement plan — what to add, in order

Companion to `UPSTREAM_SYNC_AUDIT.md` (the analysis). This is the **execution spec**:
what to change, in which files, and how we know it worked. Intended to be run in plan
mode phase by phase.

Baseline verified 2026-07-29 on macOS arm64:

- .NET SDK **10.0.302** (`brew install dotnet`); `dotnet build TomodachiDrawer.slnx` →
  **0 warnings, 0 errors**, all 4 projects.
- Firmware parser host tests **already pass here**: `27 checks, 0 failures`, clang with
  `-Wall -Wextra` clean.
- Upstream range: `6ce4683` (0.6.0) → `a50213c` (0.8.3).

Phases 1–5 need **no decision about the fork's future** — they improve V2 as it stands.
Phases 6–8 do. Ordering matters; see §Dependencies.

---

## Chapter A — Can we drop the external hardware?

Short answer: **partly, and the useful part is very achievable.** But the question hides
two different dependencies, and they have opposite answers.

### A.1 What is actually impossible

You cannot make a Mac or a PC present itself to a Switch as a USB controller. USB is
role-asymmetric: a machine is either a **host** or a **device**, and a Mac's ports are
hard-wired as host. There is no gadget/peripheral mode on macOS, and Apple Silicon does
not expose one. On Windows, ViGEmBus (which `TomodachiDrawer.DebugTools` already uses)
creates a virtual pad that is only visible to *Windows software* — it never reaches a
wire, so a Switch cannot see it.

Emulating the ESP32 doesn't help either, and this is the key insight: **even a perfect
ESP32-S3 emulator gives you a virtual controller with no Switch to plug it into.** The
dependency that actually costs us iteration time is the *Switch*, not the board — and
Palette House is a commercial game on proprietary hardware, so that's out of scope.

For completeness: a **Raspberry Pi Zero / Pi 4 in USB gadget mode** (`libcomposite` HID)
*can* genuinely act as a controller to a Switch. That's a real option, but it's still
external hardware — just different hardware. It does not answer "without external
devices."

### A.2 What is already possible today

More than expected. The architecture is well set up for this — `ISwitchOutput` is a clean
92-line abstraction with `Press`/`Release`/`SetStick`/`Delay`, and Core already ships
`DummySink`, `TimingSink` and `FileControllerSink`. So the whole app-side pipeline
(quantization → layering → routing → input stream) runs with **no board and no Switch**.
`TimingSink` even gives accurate draw-time estimates.

**And — the strongest thing here — the `.tdld` contract is already verifiable offline on
both sides of the wire.** Core *writes* the format (`OutputSinks/FileControllerSink.cs`),
the ESP32 firmware *parses* it (`main/tdld_parser.c`), and the parser's host tests compile
and pass on this Mac right now (`27 checks, 0 failures`, clang `-Wall -Wextra` clean). The
audit also confirmed upstream did **not** change the format in the 0.6.0→0.8.3 range —
`FileControllerSink.cs` only gained `sealed` and reformatting.

So we already have a real safety net for any Phase 1–2 change to the emitted input stream:
write a `.tdld` from the app, feed it to the firmware parser on the host, assert it decodes
to the expected opcode sequence. No board, no Switch. That net exists today and just isn't
wired into CI.

### A.3 What to build — the actual win ⭐

**Render what the Switch *would* draw, and diff it against the source image.**

That catches exactly the class of bug that has cost this project the most: upstream's
`0ae89ff` (bucket fill degenerating to a single pixel) and `47331ff` (splotchy images)
would both have been visible instantly in a rendered output.

Two design questions had to be answered in the code before this was designable. Both now
are:

**Q1 — can a sink know where the cursor is?** No, not directly. `CanvasDrawer` keeps
`_cursorX` / `_cursorY` in **private fields** (`CanvasDrawer.cs:18-19`) and updates them as
it emits inputs. A sink sees only the input stream.

But reconstruction is cheap, not fiddly: `CanvasDrawer` derives the cursor from the very
DPad taps it emits, with a plain decrement model (`CanvasDrawer.cs:1160`,
`MeasureDistanceToFromCurrent` at `:1169` is just Chebyshev over those two fields). A
~20-line `switch` over DPad direction reproduces it exactly.

**Q2 — is `CanvasToolbar`'s menu geometry shareable or would we duplicate it?**
Shareable. It is already named constants, not inline literals: `ToolbarSelectIndex`,
`ToolbarBucketIndex`, `ToolbarBrushIndex`, `ToolbarItemCount`, `BrushSubmenuColumns/Rows`,
`BucketSubmenuColumns/Rows` (`CanvasToolbar.cs:22-35`), plus `BrushColumnBySize` which is
*already* `public static readonly`. Sharing them needs a visibility bump from
`private const` to `internal const` — **not** a 200-line duplicate that drifts.

So the risk I flagged earlier is smaller than it looked. Build it in two layers:

#### Layer 1 — intent trace + renderer (do this first)
`CanvasDrawer` already knows everything: cursor, active colour, active brush. So rather
than reverse-engineering its state from the wire, have it emit a structured trace
alongside the input stream — *"stamp colour C, brush B, at (x,y)"*, *"bucket fill at
(x,y)"*, *"erase all"* — and render that to an `SKBitmap` → PNG, plus a diff vs the source
and a % pixel-match metric.

- **Cost:** low. A few hook points in `CanvasDrawer` and a standalone renderer.
- **Validates:** routing, coverage, layer ordering, homing, bucket-fill geometry,
  brush-size selection. The whole geometric pipeline.
- **Does not validate:** that *"select colour C"* actually lands on C on a real Switch —
  it trusts the menu navigation.

#### Layer 2 — full stream simulation (optional, later)
A real `ISwitchOutput` implementation in `TomodachiDrawer.DebugTools/VirtualCanvasSink.cs`
that models the menu cursor too, driven off the shared `CanvasToolbar` constants and
`ColourPickerRouter`. This is what catches *wrong-colour* bugs — menu navigation
off-by-ones — which Layer 1 cannot see.

- **Cost:** moderate; the `ColourPalette` + `CanvasToolbar` menu state machines are the
  work.
- Only worth it if wrong-colour bugs actually show up. Layer 1 covers the bugs we have
  historically hit.

Payoff either way: routing and colour changes become verifiable in seconds instead of
needing a board, a Switch and a full draw — plus **golden-image regression tests** in CI,
which nobody upstream has.

### A.4 Cheap wins alongside it

1. **Port `run_host_tests.ps1` → `run_host_tests.sh`.** It's a single clang invocation
   with `.exe` hardcoded; the C is already portable and passes here. Keep both, or write
   one CMake target. ~15 minutes, and it puts the firmware parser under test on this Mac
   and in CI on Linux.
2. **Wire both into CI.** Host parser tests + virtual-canvas golden images on every push.
3. **ESP-IDF QEMU** (`idf.py qemu`) is worth a look for *firmware logic* iteration —
   it runs the app, UART and timers. But do not plan around USB-device emulation there;
   the host unit tests already cover the parser more cheaply, and per A.1 an emulated
   USB gamepad has nowhere to plug in. Low priority.

**What still needs real hardware:** final sign-off of a full drawing on a real Switch,
and anything touching USB enumeration or HID timing. Everything else can move offline.

### A.5 The Bluetooth route — real, but not the one we want

Using a computer as a Bluetooth Pro Controller instead of a microcontroller **is** possible,
and it is now a decided piece of work — but it does not remove the external hardware (it
needs Linux + a USB Bluetooth dongle), it regresses reliability, and it still needs a real
Switch. It removes the *board*, not the *console*.

Moved to its own document so it doesn't drift: **`Docs/BLUETOOTH_NXBT_PLAN.md`** — research,
the case against putting it on the production path, and why writing data into a real
controller cannot work.

## Chapter B — The plan

### Phase 1 — Routing: correctness, speed, and a bug of ours
No fork decision needed. Highest value in the audit.

| # | Change | Files | Accept when |
|---|---|---|---|
| 1.1 | **Dispose the OR-Tools native objects.** Wrap `RoutingIndexManager`/`RoutingModel` in `using var`. Ours leaks in *both* sites and it's worse for us — `RouteSolver.Solve` runs on ~80% of cores, so peak undisposed native memory multiplies by thread count | `CanvasDrawer.cs:949-950`, `RouteSolver.cs:51-52` | Route generation on a 256² image shows no growth in peak native memory across repeated runs |
| 1.2 | **Adopt upstream's `CanvasDrawer.Tsp.cs` split** (`7b22452`) — move `PerformTSP`, `GetRecommendedTSPSolveTime`, `Chebyshev`, `NearestIndexToCursor` out of `CanvasDrawer.cs`. Take the split even though it's restructuring: it makes every future sync cheap, and this is the file we most need to keep syncable | new `Core/CanvasDrawer.Tsp.cs`, `CanvasDrawer.cs` | Build green; output unchanged (see 1.5) |
| 1.3 | ⭐ **Held-Karp exact solver for ≤16 points** (`ExactMaxPoints = 16`). Solves tiny instances optimally and instantly instead of burning the full time limit. A 256² image has many small per-colour/per-stamp layers, so this gives *both* shorter draws and faster generation. Mirror into `RouteSolver` so the parallel path benefits too | `CanvasDrawer.Tsp.cs`, `RouteSolver.cs` | Draw-time estimate drops on a multi-colour 256² image; routes for ≤16-point sets are provably optimal |
| 1.4 | ⭐ **Adopt upstream's higher TSP time limits + early-exit.** 0.5/1.5/2.75/4.0 → 1.0/3.0/4.0/5.0 s, plus `EarlyExitRateCoefficient`/`EarlyExitSolutionsDistance`. For upstream this is ~2× slower serially; for us it's nearly free because we parallelize — better routes, still faster wall-clock than upstream | `CanvasDrawer.Tsp.cs`, `RouteSolver.cs` | Shorter draw-time estimate at equal or better generation wall-clock vs today |
| 1.5a | **Parallel ≡ sequential byte check** (permanent invariant). Same image + same TSP time limit → emit `.tdld` sequentially and in parallel, assert byte equality. **Capture the baseline against current `HEAD` *before* 1.2–1.4 touch anything**, and also against **unmodified upstream** — the Option C PR's claim is "byte-identical to *upstream's* sequential path", which becomes unprovable once our own TSP changes land | new test/tool project or a DebugTools entry point | Same bytes on ≥3 images, incl. one 256² multi-colour, on both baselines |
| 1.5b | **Draw-time benchmark** (gates 1.3 and 1.4). 1.5a **cannot** validate these: Held-Karp and higher time limits are *supposed* to change the routes, so byte equality still passes while output changes completely. Use the existing `TimingSink` to measure total estimated draw time on a fixed image set, before vs after | uses `Core/OutputSinks/TimingSink.cs` | Estimated draw time drops (or holds) on every image in the set; generation wall-clock stays acceptable |

> **Invariant for this phase:** `RouteSolver.Solve` duplicates `PerformTSP`'s OR-Tools
> configuration. **Any change to one must be mirrored in the other in the same commit**,
> or the parallel path silently diverges from the sequential one.

### Phase 2 — Drawing correctness

> **2.0 first: run csharpier on `TomodachiDrawer.Core` only.** One commit, formatter only
> — *not* the analyzer pass. Reason: upstream's Phase-2 commits are already
> csharpier-formatted and our `Core` is not, so without this every port below is a
> hand-transcription against differently-formatted surrounding code, which Phase 8 would
> then reformat all over again. Formatting Core *first* makes upstream's Core diffs apply
> close to cleanly and shrinks Phase 8 to the UI plus the analyzer work.
>
> "Formatting last" still holds for the **UI**, where the diffs won't apply either way.
> Do this after 1.5a has captured its byte baseline (formatting cannot change emitted
> bytes, but the baseline should predate all Core edits).

| # | Change | Upstream | Files |
|---|---|---|---|
| 2.1 | Merge colours that map to the same in-game colour (canonical HSV-step key) → fewer redundant layers → shorter draws | `7086620` | `ColourPalette.cs`, `ColourPickerRouter.cs` (adds `CanonicalKey`) |
| 2.2 | Clear canvas ("erase all") on Switch 2 for full-size images, so bucket fill can't degenerate to a single pixel | `0ae89ff` | `CanvasDrawer.cs`, `CanvasToolbar.cs`, `MainWindow.axaml` |
| 2.3 | Mitigate Switch 1 brush-menu lag during homing (fixes splotchy images, #120) | `47331ff` | `CanvasDrawer.cs`, `CanvasToolbar.cs` |
| 2.4 | `DrawImageSettings` construction refactor + `ReverseColourOrder` option | `4419bf6` | `DrawImageSettings.cs`, `CanvasDrawer.cs`, UI |
| 2.5 | RP firmware: neopixel blue for dpad-only input (button takes priority), brightness tweaks, `static` fix | `01074f2`, `1bba220` | `TomodachiDrawer.Firmware/TomodachiDrawer.Firmware.c` |

Accept when: a rendered virtual-canvas run (A.3) matches the source image at least as
well as before, and layer count drops measurably on an arbitrary-palette image for 2.1.

### Phase 3 — Crash fixes (cheap, uncontroversial)

| Upstream | What |
|---|---|
| `e5ba374` | Weezer crash — `MedianDenoiser` read past the alpha channel when the source `ColorType` had none (#123) |
| `022d114` | 0.7.0 crash — `MedianDenoiser` + MainWindow |
| `14dd54a` | `SafeNumericUpDown` guards null/empty numeric input (#119) |
| `a9224bd` | Crash on template confirm |
| `c2e6fa4` | Template tool default height |
| `725faf2` | Snapshot `_currentImage` for tdld/ESP32 export — NRE when path decode fails |

### Phase 4 — UX features (hand-ported; both sides rewrote `MainWindow`)

Ranked by value. Stop wherever the value runs out.

| # | Change | Upstream |
|---|---|---|
| 4.1 | **Cancel an in-progress draw** — biggest single UX win | `38ffcae` |
| 4.2 | Persist drawing option settings | `9de75cd` |
| 4.3 | `settings.json` → AppData, logic into `AppSettings.cs` (ours is 22 lines vs their 125) | `07aa8b9` |
| 4.4 | Smaller default window, more generous min size (#133, #130) | `421991e` |
| 4.5 | "Export to .tdld" button | `a1732a9` |
| 4.6 | Switch 1 desync warning on version selection | `6da20f1` |
| 4.7 | Show image size in the Preview header; uf2 export tooltip | `88b0190`, `de36010` |

**Explicitly skip** `355e831` / `31371a5` / `fe3d6fd` (UI refactor into a state context).
Pure restructuring of the file we've diverged on most — all cost, zero user-visible gain.

### Phase 5 — Offline testing (Chapter A)

| # | Change |
|---|---|
| 5.1 | `run_host_tests.sh` (or a CMake target) so the firmware parser tests run on macOS/Linux, not just PowerShell. Verified: the C already compiles and passes here unchanged |
| 5.2 | ⭐ Intent trace + renderer (Layer 1, A.3) — render what the Switch would draw; PNG output + diff vs source + % pixel match |
| 5.3 | Golden-image regression tests over 5.2, wired into CI |
| 5.4 | **`.tdld` round-trip test**: app writes a `.tdld` → firmware's host parser decodes it → assert the opcode sequence. Both halves already work offline (A.2); this just connects them and puts it in CI |
| 5.5 | *(optional, later)* `VirtualCanvasSink` (Layer 2) — full input-stream + menu simulation, only if wrong-colour bugs actually appear |

### Phase 5B — "Computer as controller" (NXBT) — ⏸ PARKED

Decided and then parked on 2026-07-29: **V2 stays wired-only.** NXBT documents skipping inputs
on long macros, which is the wrong trade for hour-long drawings on a project that already fights
desyncs over a wire.

Research preserved in **`Docs/BLUETOOTH_NXBT_PLAN.md`**. Revisit only if NXBT gains reliable
long-macro playback or confirmed Switch 2 support.

### Phase 6 — Dependencies & CI

- SkiaSharp **3.119.2 → 4.150.1**, plus the explicit `SkiaSharp.NativeAssets.Linux`
  4.150.1 pin. Upstream hit repeated Linux packaging weirdness here (`04a26e7`,
  `70d4ca1`, `d11d799`) — expect the same and budget for it.
- Avalonia **12.0.3 → 12.1.0**; System.IO.Ports → **10.0.10**.
- Replace **SixLabors.ImageSharp → JeremyAnsel.ColorQuant** (`85ab810`). Confirm the
  licences before citing that as the reason, but take the swap regardless.
- CI: `actions/checkout` 6→7, `actions/cache` 5→6, `setup-dotnet` 5→6, pico-sdk caching
  (`81955cb`), and `8400352` (pin the ESP action to a commit hash — security, worth it).
- Repo hygiene: dependabot config (`e198d0e`), issue templates (`c359ca4`).

### Phase 7 — Decide: Sentry

`31affe2` + `5dd17d1` + `b32975f` + `6d19f10` + `ef568b7`.

This is **not** covered by our existing telemetry veto — Sentry is a third-party crash
reporter, not the upstream author's own server (`telemetry.l7y.media`), which is what
`SYNC.md` objects to. For a binary shipped to strangers, real crash reports are how the
Weezer-class bugs get found at all.

- **If yes:** add a privacy note to the README and make it opt-out at minimum.
- **If no:** still extract the non-Sentry macOS fail-state handling from `ef568b7`.
- Either way, keep skipping `009efcb` (image telemetry — author's own server).

### Phase 8 — Formatting and bookkeeping (last, deliberately)

Do these **after** everything else; they touch every file and would poison every other
diff: `c60277e` (analyzers), `b8c3d1d` + `8720417` (csharpier — **UI only now**, Core was
done in 2.0), `1a84ec7`, `a50213c`.

Then:
- Bump version (suggest **0.5.0** — new drawing behaviour + routing changes).
- Update `SYNC.md`: marker → `upstream/master @ a50213c` (tag 0.8.3), date, ported list,
  skipped list.
- Fix `README.md:135` (still says `TomodachiDrawer.UI.Avalonia`; the folder is
  `TomodachiDrawer.V2/`, only the namespace kept the old name).
- Fix the `SYNC.md:50` `ColourLayer` note (the sync point already had `[]`; we reverted
  to `new()`).

---

## Dependencies

```
1.5a (byte baseline)   ──> MUST run before 1.2/1.3/1.4 edit anything
1.2  (Tsp split)       ──> 1.3, 1.4        (both live in the new file)
1.5b (draw-time bench) ──> gates 1.3, 1.4  (1.5a cannot — routes change by design)
2.0  (csharpier Core)  ──> 2.1-2.5         (makes upstream's Core diffs apply)
5.2  (renderer L1)     ──> best acceptance test for Phase 2
5.2  (renderer L1)     ──> REQUIRED by 5B.1 (it is how the macro gets verified offline)
5B.1 (macro exporter)  ──> 5B.2 (live sink reuses its mapping)
8    (formatting)      ──> last, and UI-only (Core was done in 2.0)
```

Phase 3 is independent of everything — a safe warm-up. Otherwise start at **1.5a**: it is
the one step that becomes impossible to do correctly once anything else has moved.

## Out of scope

- Adopting upstream's ESP32S3 stack, or winding V2 down — those are the fork-identity
  options in `UPSTREAM_SYNC_AUDIT.md` §1, still undecided.
- Emulating the Switch, or making a Mac act as a USB device (Chapter A.1).
- Upstream's telemetry, welcome-message and branding commits.
