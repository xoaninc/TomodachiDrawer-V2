# Hardware validation — 0.5.0

**This is the release gate.** 0.5.0 changes *what gets drawn*, and no amount of CI can see that.
Everything else in this release is verified offline (47 unit tests, firmware parser tests, and the
route bench in `Docs/bench/`), but the four items below need a real Switch.

Do not tag 0.5.0 until §1 and §2 pass. If something fails, the fix is cheap — say so and it gets
fixed before release rather than after.

Build to test with: `main` after the 0.8.3 sync merge, or the `sync/upstream-0.8.3` branch.

## Which board?

**Any of them — §1–§4 are chip-agnostic.** What changed this release is the *drawing* (which
colours, which order, which route), and that is identical whichever microcontroller replays it.
So testing on an **ESP32-S3** validates §1–§4 just as well as a Pico would.

Two chip-specific notes:

- **ESP32-S3:** the firmware and `EspFlasher` were **not touched** this release. The `.tdld` binary
  format is also unchanged — upstream did not alter it, and neither did we, which the firmware's
  host parser tests confirm. So an existing flashed board needs no re-flash of the base firmware;
  just export a new drawing.
- **RP2040 / RP2350:** these got two changes the ESP32 did not. The firmware LED now goes blue for
  dpad-only input (cosmetic, visible immediately), and the UF2 write path was hardened — it now
  retries once after a permission denial and, importantly, no longer fails *silently* when the
  drive vanishes between detection and writing. If you only test on ESP32, note in the release
  notes that the UF2 retry path is unverified.

---

## 1. Erase-all before bucket fill — ⚠ highest risk

**What changed:** on Switch 2, a full-size opaque image now clears the canvas ("erase all") before
using the bucket for the most prevalent colour. Without it, a pixel landing on the top-left cell
made the bucket fill *just that pixel* instead of the canvas (upstream `0ae89ff`).

**Why it is the riskiest item:** it is pure menu navigation. Nothing offline can check it, and it
depends on hard-coded eraser-submenu coordinates that were ported, not measured on hardware:
`EraserSubmenuColumns = 6`, `EraserSubmenuRows = 6`, `EraserSubmenuEraseAllRow = 4`
(`TomodachiDrawer.Core/CanvasToolbar.cs`). If the submenu layout differs at all, the wrong item
gets clicked.

**Test:** a **256×256 fully opaque** image, Switch **2**.

| Watch for | Pass | Fail |
|---|---|---|
| Right after the countdown | Toolbar opens, eraser submenu opens, cursor slams to top-left then down, one confirm, canvas goes blank | It clicks something that is not Erase All — undo, a different tool, or an effect |
| Then | Bucket fills the **whole canvas** with the most common colour | Only a single pixel changes, or the fill is the wrong colour |
| Then | Drawing proceeds normally over the filled canvas | The tool is left in the wrong state and the first layer misbehaves |

**If it fails:** note exactly which submenu item got selected. The three constants above are the
fix, and getting it wrong is recoverable — nothing is written to the console permanently.

## 2. Colour merge

**What changed:** colours that reach the *same* in-game HSV steps now collapse into one layer
(upstream `7086620`, via `ColourPickerRouter.CanonicalKey`). Near-identical RGB values stopped
producing duplicate layers, which shortens drawings.

**Test:** any image with the **Arbitrary** colour matcher and a decent colour limit (say 32+).

- **In the log:** the layer count should be **lower** than the colour limit you asked for. That is
  expected and is the whole point — the log lines naming each colour are the count.
- **On screen:** no colour should visibly go missing. Blacks and near-blacks collapsing into one
  black is correct; two clearly different colours becoming one is not.

## 3. Switch 1 brush-menu lag mitigation

**What changed:** on Switch 1 the taps out of the sphere-brush area are slower (75ms vs 25ms) and
there is an extra confirm, to work around lag that produced splotchy images (upstream `47331ff`,
their issue #120).

**Test:** requires a **Switch 1**. Draw anything with large brush areas and check the result is not
splotchy.

**If you do not have a Switch 1:** that is fine — but the release notes must say this is
**unverified** rather than implying it was tested. Upstream's own author could not reproduce the
original lag on their Switch 1 either.

## 4. Routing and cancel — low risk, quick to confirm

**Routing:** the bench says routes are now 2–5% shorter than solving serially, and the
`PaintActions` invariant already proves offline that no points are dropped. A single real draw of
any image confirms it end to end. Nothing specific to watch beyond "the image comes out right".

**Cancel:** press **Cancel Generation** part-way through a generation, then:

1. The log should say "Cancelling route generation…" then "Generation cancelled." — **not** an
   error dialog.
2. **Then unplug and replug the board.** Port detection must still work. This is the specific thing
   the per-draw `CancellationTokenSource` exists to protect: cancelling through the window-lifetime
   one would have killed serial-port polling for the rest of the session.

## 5. Also worth a quick look (changed, but low risk)

- **Settings moved to AppData.** First launch of 0.5.0 should carry your old preferences over from
  the working-directory `settings.json` and then keep using
  `~/Library/Application Support/TomodachiDrawerV2/settings.json` (or `%APPDATA%` on Windows).
- **Drawing options now persist** — colour matcher, colour limit, denoiser, auto-home, reverse
  colour order. Set them, restart, confirm they stuck.
- **Export to .tdld** writes a file with no board connected.
- **Reverse Colour Order** — toggling it must actually change the drawing. If the log says
  "Reusing cached route" after toggling it, that is a fingerprint bug; report it (there is a test
  guarding this, so it should not happen).

---

## Reporting

Whatever the outcome, the useful thing to capture is the **log pane contents** plus a photo of the
canvas. For §1 specifically, a clip of the toolbar navigation is worth more than a description —
the failure mode is "it clicked the wrong thing" and that is only diagnosable visually.
