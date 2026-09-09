# Reproducible release comparison

The harness builds the same adapter against frozen V2 release, V2 main, pristine
upstream, an observation-only upstream copy, and the candidate. Keep upstream
sources immutable. `prepare.py` writes only adapters and the explicitly named
`upstream-observed` copy under the selected scratch root; it saves the exact
observation diff. It refuses mismatched patch anchors rather than guessing.

Prepare source archives under `<root>/v2-release`, `v2-main`, and `upstream-clean`
with their commit IDs recorded alongside the archives, then run:

```sh
python3 Comparison/prepare.py --root /tmp/tdld-092-audit
dotnet build /tmp/tdld-092-audit/adapters/upstream-observed/Comparison.csproj -c Release -m:1 -nodeReuse:false
# Build each remaining adapter identically before measuring.
python3 Comparison/verify_observer.py --root /tmp/tdld-092-audit
python3 Comparison/run_matrix.py --root /tmp/tdld-092-audit \
  --photo /path/to/local-test-photo.jpeg
```

Do not run benchmarks or substantial builds/tests concurrently. The matrix is
sequential and resumable with source/settings hashes checked before reuse. Use
`--versions`, `--budgets`, `--buckets`, `--datasets` and `--output` to run bounded
phases; the defaults select the complete matrix. All repeats are retained,
including failures, with median/min/max metrics. The local photo is never copied
into the repository. Outputs contain derived drawings and should stay local.

Bench flags: `--arm serial|parallel-1thread|parallel-2threads|parallel`, or
`--threads N`; `--buckets`, `--min-bucket-size 36`, `--reverse`,
`--no-large-brush`, `--early-exit`, `--home`, `--switch 1|2`, `--tsp`,
`--repeats`, `--colours`, `--quantizer`, `--image`, `--fixture`, `--render-out`,
`--out`, `--untraced`. Older V2 maps buckets to its experimental feature flag;
new versions use the dedicated setting. Unsupported parallelism in pristine
upstream selects its only serial implementation and records that arm honestly.

Each run saves the quantized reference, rendered intent, controller export,
generation log and result with SHA-256 hashes. Candidate/V2 exports replay the
**same** route to v3/v4 and compare payload bodies; separately generated routes
are never incorrectly required to match. Every render is checked and a failure
makes the benchmark exit nonzero. Pristine upstream with `--untraced` exports
real TDLD/reference pixels but explicitly makes no render claim.

The upstream observation patch only appends buffered paint events at existing
paint sites and replays the winning layer/snake/TSP buffer. Verify it against
pristine upstream using deterministic, solver-free small fixtures: its TDLD must
be byte-identical. Time-limited OR-Tools runs can legitimately choose different
routes when instrumentation changes elapsed CPU time; do not use those to assert
observer neutrality. The intent renderer validates geometric coverage, not the
game's real menus or hardware scheduling. Decoded input checks and a real Switch
drawing remain separate requirements.

`PaintActions` is a historical estimate based on default-duration taps, **not a
correctness invariant** for modern bucket strategies. Tool-switch menus and
explicit-duration A taps legitimately change it. Pixel comparisons are strict;
performance comparisons report variance instead of picking only the best run.

If final checks change the candidate before its first measured run, pause the
coordinator at a completed batch boundary, rebuild only the candidate adapter,
and run `python3 Comparison/refresh_candidate.py --root /tmp/tdld-092-audit`.
It refuses existing candidate results and records the old/new freeze metadata;
it does not rewrite historical source, assemblies or drawing artifacts. Resume
the coordinator only after all builds/tests have ended. Documented comment-only
harness amendments retain the original hashes alongside an explicit old/new
chain; unrecorded harness changes still prevent fresh resume.

### Fail-closed resume and sleep handling

A nonempty batch directory without a completed `results.json` is **never reused or
overwritten**. Quarantine it first; partial rows remain evidence, not completed
measurements. Before reuse, the runner requires a successful exit, every expected
image/arm/repetition exactly once, nonempty decoded-event counts, a neutral final controller state, matching
per-repeat records, required artifacts and hashes, v3/v4 payload parity where
supported, and exact rendered/reference pixels. `--validate-only` checks existing
batches without drawing; `CompleteAndPassed` remains false until all 624 requested
runs are present and valid. A `PERFORMANCE_INVALID.json` marker excludes the
entire batch from performance statistics and blocks reuse.

On macOS, the runner checks the lid before launch and polls it during each batch.
It also compares wall time with monotonic time to detect suspension or clock
jumps. Detection stops only its own drawing child, records the invalidation and
power evidence, preserves every file, then exits. A temporary `caffeinate -i`
assertion is scoped to the coordinator and cleaned up on exit. It cannot prevent
clamshell sleep: keep the laptop awake with the lid open while measuring.

For a safe pause, create `<output>/pause.request`. The current batch finishes;
`paused.json` then records the next batch and coordinator PID. Remove the request
to continue. No process signals or changes to system power preferences are needed.
If interrupted mid-batch, its artifacts stay in place and require quarantine.

Guard checks are independent of Core and take milliseconds:

```sh
python3 -m unittest discover -s Comparison -p 'test_*.py'
python3 Comparison/run_matrix.py --root /tmp/tdld-092-audit \
  --photo /path/to/local-test-photo.jpeg --output matrix-final --validate-only
```

After validation, compare matching candidate/main medians with the agreed gates:

```sh
python3 Comparison/performance_gates.py /tmp/tdld-092-audit/matrix-final/summary.json \
  --out /tmp/tdld-092-audit/matrix-final/performance-gates.json
```

With buckets off, more than 5% nominal draw-time or 20% generation-time regression
requires five controlled repeats of both matched versions. The report preserves
median/min/max for both versions; bucket-enabled tradeoffs are reported without
applying those thresholds. Exit status stays nonzero until the full matrix is
complete and correct and no unresolved threshold is exceeded. The command reads
the validated summary; it does not replace artifact validation or generate runs.

### Reduced photo-focused audit

The user shortened the September audit because the full matrix took too long.
`run_focused.py` records an independent scope and completion count; its success
does **not** complete the original 624-run matrix. It reuses the three validated
release photo/production/buckets-off repetitions with explicit provenance.

```sh
python3 Comparison/run_focused.py --root /tmp/tdld-092-audit \
  --photo /path/to/local-test-photo.jpeg --include-tsp5
```

This performs 38 checks (35 new): photo × four versions × buckets off/on × three
production repetitions; six additional candidate serial/one-thread/two-thread
photo checks; six candidate synthetic checks; and two candidate production photo
checks at TSP 5. Without `--include-tsp5`, the last two checks are omitted.
The 5%/20% gate covers only the matched production photo, buckets off, TSP 1,
with three repetitions per candidate/main version. All frozen DLL hashes are
checked before measuring, and the same artifact/sleep/resume guards apply.
