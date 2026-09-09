# Hardware validation — 0.6.0 candidate

Use the downloaded **0.6.0-rc.1** pre-release. Its offline comparison report is separate from
this checklist: exact rendered pixels do not verify game-menu coordinates or controller input
delivery on a Switch.

The RP firmware is the same as 0.5.0 and accepts TDLD v3/v4. An RP still using pre-0.5.0 base
firmware needs that update before HID polling can be tried. ESP32 remains v3-only; the app's
ESP32 export forces that format. Generate a **new drawing**, never reuse August exports.

## Record the setup once

- App version, board model, Switch generation, canvas type/dimensions and docked/undocked.
- Source image, colour matcher/limit, denoiser, reverse order, auto-home, bucket checkbox,
  HID checkbox and TSP time limit.
- Use the same conditions in comparisons. Prefer undocked for the initial test; changing the
  dock and timing mode together would make the result hard to interpret.

## 1. Initial fill and complete default drawing

Keep **Enable Bucket Fills off** and **HID Polls off**. Load the previously failing photo with
Arbitrary, 16 colours, no denoising, 256×256, Switch 2, auto-home and TSP 5 seconds.

The background fill is distinct from optional per-layer bucket fills. For a full opaque image,
watch the start: erase-all should clear the canvas, the predominant colour should fill it, and
the first layer should then draw normally. A short clip of this menu sequence is useful if it
selects the wrong tool. Let the drawing finish and inspect the complete image for a shifted
section, gaps or wrong colours.

**Cancel Generation stops calculation on the computer only.** To interrupt a drawing already
running on a disconnected board, unplug the board from the Switch. Cancelling route generation
and stopping a drawing are separate tests.

## 2. Optional dynamic buckets

Repeat the same conditions with **Enable Bucket Fills on**, leaving HID off. If the image has no
selected dynamic fills, use [nested-zone64.png](bench/nested-zone64.png), with Arbitrary and
4 colours. It has a transparent outer border and nested red, blue and green regions, matching
the bucket-planning regression fixture. A bucket option
being enabled does not mean every layer uses it: the app compares each candidate's nominal cost.

Watch the cursor travel with a brush selected, switch to the bucket for a fill, then restore the
brush before travelling to the next point. Check the finished image against the preview,
especially shared colour boundaries and holes. Record actual duration separately from the
nominal estimate.

## 3. HID timing on RP only

If testing HID polls, repeat the same image and bucket setting, changing only the HID checkbox.
Default taps then count host reports; explicitly slower menu taps retain their millisecond
durations. The displayed duration is nominal and may differ from the real drawing time.

A clean result in one mode and a desync in another is useful evidence, not a proof of the sole
cause. A desync in both modes likewise does not exclude lost inputs. Save both logs and photos.

## 4. Application and board connection

Cancel while the computer is calculating, then immediately generate again. The cancelled run
must not become a reusable cached drawing. Reconnect the board and confirm detection still
works. Change the bucket checkbox and verify that an old cached drawing is not exported.

Switch 1's additional waits and erase-all/background fill require their own real-console check.
A successful Switch 2 drawing does not validate Switch 1.

## Results

No hardware outcomes recorded yet. Keep candidate status until the tested configuration and
result are written here. Untested optional modes must remain explicitly unverified in the notes.

Logs are saved under `~/Library/Application Support/TomodachiDrawerV2/logs/` on macOS. Keep the
full log, source/settings and final canvas photo together; record the first visible problem and
rough elapsed time if a run fails.
