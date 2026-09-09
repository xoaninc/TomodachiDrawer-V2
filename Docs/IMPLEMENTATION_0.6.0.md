# 0.6.0 candidate: upstream buckets and parallel routing

Approved 2026-09-08. Delivery is **0.6.0-rc.1**, a GitHub pre-release downloaded locally for
Juan's Switch test. A stable release requires the subsequent hardware result.

## Locked decisions

- Port upstream 0.9.2 (`f3bc3895f1679e1b0e2efda21274a5ee6dd40ddb`), including its corrected
  bucket timings and dependency versions. Keep V2's routing cost, optimal cycle cut, exact-solver
  threshold of 12, and multithreading with buckets both on and off.
- Dynamic bucket fills have their own persisted setting, **off by default**. The obsolete
  experimental setting does not opt users into them. Minimum zone size is 36, internal only.
- Preserve v3/v4 serialization and the existing RP/ESP32 firmware. Explicit non-default tap
  durations remain milliseconds in either format. HID estimates remain nominal.
- Capture four independent version groups: released V2 0.5.0, main `ae5dbe3`, upstream 0.9.2,
  and the candidate. Historical snapshots remain unchanged.
- No image repair, resume feature, half-size feature, upstream PR or stable release in this task.

## Execution

1. Freeze baseline sources and repair the comparison tools before measuring. Every repeat must
   be retained; one incorrect render must cause failure, even if other repetitions pass.
2. Freeze per-layer plain/bucket geometry using the original preceding layers as the painted
   border mask. Run only independent route solves in parallel. Keep candidate command/trace
   buffers isolated and commit only the selected plan's state and output.
3. Fix parallel jobs ignoring the early-exit settings. Record solver options at the actual
   invocation boundary in regression tests.
4. Compare decoded inputs as well as intent renders. Exact byte equality is required when the
   same recorded route is serialized twice; it is not required for different valid TSP routes.
5. Exercise repeated real-image and synthetic benchmarks plus targeted bucket, concurrency,
   cancellation, cache and stream-format checks. Preserve raw artifacts and report all failures.
6. Build/test/package in CI, publish the candidate with reviewed notes, download the actual
   macOS arm64 asset and smoke-test that bundle. Close the app afterward.

### September 9 scope revision

Juan stopped the original long run and resumed with an explicit request to avoid a 45-minute
wait. The release gate is now a **focused 38-drawing comparison**, retaining the 222-test suite:

- Real photo, TSP 1, buckets off/on: three production-mode repetitions for each of the four
  frozen versions (parallel for V2, serial for upstream). Reuse the three already validated
  release/buckets-off results.
- Candidate photo, TSP 1, buckets off/on: one additional serial, parallel-1 and parallel-2 draw.
- Candidate photo, TSP 5, buckets off/on: one production-mode draw each.
- Candidate grid64/rings128/mosaic256, TSP 1, buckets off/on: one production-mode draw each.

The pixel, decoding, source-reference and same-route serialization requirements still apply.
The 5%/20% performance gate uses the three production-mode photo/buckets-off repetitions
against frozen main. Other configurations are correctness probes, not statistically repeated
performance claims. A triggered gate still requires controlled confirmation before acceptance.

The original 624-case matrix remains available as a future extended benchmark; **it is not
complete and must never be reported as passing**. Completed historical batches and quarantined
partial batches are retained separately. Do not restart this extended scope automatically.

### Original extended matrix (deferred)

The full matrix uses three repetitions, TSP budgets 1/5 seconds, the three synthetic benchmark
images and the actual previously failing photo. Each V2 version runs serial, parallel capped at
1, capped at 2, and production parallelism. Upstream runs serial. Both bucket settings are
included where supported. Performance runs must not overlap other CPU-intensive jobs.

Acceptance requires zero missing, wrong-colour or extra pixels on every candidate run, correct
candidate/route ownership, correct serialized timing and successful recovery after cancellation.
With buckets off, a median regression over 5% in nominal draw time or 20% in generation time
versus `ae5dbe3` requires five controlled repeats and a fix if it persists. With buckets on,
report the measured tradeoff without claiming every image becomes faster.

## Hardware handoff

First inspect the erase-all/background fill on a fully opaque 256×256 Switch 2 drawing, then
finish the full image. Change only one variable for later runs (dynamic buckets or HID polls).
Record console/board, settings, log and canvas photo. The application can cancel generation on
the computer; stopping a board already drawing requires disconnecting it.

Offline intent rendering validates image geometry, not game menu coordinates or actual HID
delivery. A clean A/B supports a hypothesis; it does not by itself prove or exclude all causes
of desynchronization.
