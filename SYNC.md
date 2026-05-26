# Upstream sync marker

TomodachiDrawer V2 is an independent repo with **no shared git ancestry** with
upstream (`Lucas7yoshi/TomodachiDrawer`) — history was rewritten, so `git merge`/
cherry-pick against `upstream` do not work. Upstream changes are ported by hand.

To find what's new since the last sync, diff from the marker below:

```sh
git fetch upstream
git diff <last-synced-commit> upstream/master
```

After porting, update the marker here.

## Last synced

- **Through:** `upstream/master @ 6ce4683` (upstream tag **0.6.0**)
- **Fork point:** upstream `d5f64bc` (merge of PR #38, virtual-gamepad-sink) =
  our pre-V2 baseline.
- **Date:** 2026-05-26

## Ported in this sync

- **RP2350 (Raspberry Pi Pico 2) support — full** (upstream PR #55):
  - Firmware: per-board output suffix in `TomodachiDrawer.Firmware/CMakeLists.txt`
    (`PICO_PLATFORM` → `TomodachiDrawer.Firmware.{rp2040,rp2350}.uf2`) and the
    RP2350 RGB vs RP2040 GRB neopixel byte-order conditional.
  - Flasher: `UF2Flasher` gains `RPChipType`, RP2350 family ID `0xe48bff57`,
    `FindRP2350Drive`/`FindDriveForChip`.
  - UI: `MainWindow` output panel is now a RP2040/RP2350 `TabControl`; click
    handlers dispatch on chip (`ExportToDeviceButton_Click`, generic
    `ExportUF2Button_Click`/`FlashFirmwareButton_Click`, `UpdateChipUI`).
  - Packaging: firmware `.uf2`s are **CI-built per board** (not committed; see
    `.gitignore`) and bundled at publish time; `.csproj` globs
    `TomodachiDrawer.Firmware.*.uf2`.
- **Button-behaviour reliability mitigation** (upstream `a5f8703`): try/finally
  around `ExportUF2Button_Click` and a resilient try/catch around the Pico polling
  loop so a transient error never leaves the UI locked. (Our
  `ExportToDeviceButton_Click` already had this from V2.)

## Intentionally skipped

- **Telemetry (PR #88)** — phones home to `telemetry.l7y.media` (upstream author's
  own server). Not appropriate for an independent fork. This also covers the
  bundled "TSP time limit" commit (pure telemetry instrumentation, no drawing
  change) and "Address trimming errors" (moot — V2 disables IL trimming).
- **Welcome-message tweaks** (`d98757d`, `6ce4683`) — upstream-branding/version
  specific; V2 has its own welcome message.
- **`ColourLayer` `new()`→`[]`** syntax modernization — cosmetic.
- **`NoImagePlaceholder.png`** swap — cosmetic.
