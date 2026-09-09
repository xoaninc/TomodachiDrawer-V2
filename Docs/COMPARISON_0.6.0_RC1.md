# 0.6.0-rc.1 comparison — 2026-09-09

**The user-approved focused audit passed 38/38 checks.** This is not completion of
the original 624-run matrix: the user shortened that scope because of its runtime.
There were 35 new generations and three explicitly reused, validated release
measurements. New generation time totaled 554.36 seconds; the initial eight-minute
estimate was optimistic. No further drawing benchmarks were started afterward.

## Versions and scope

| Version | Frozen revision | Production solver |
| --- | --- | --- |
| V2 0.5.0 release | `88b299f8b9731632793ebdfef8dbdd3659ed4d4c` | Parallel |
| V2 previous main | `ae5dbe35350e5354f0e7b0bb3565f52b31060f91` | Parallel |
| Official upstream 0.9.2 | `f3bc3895f1679e1b0e2efda21274a5ee6dd40ddb` + observation-only patch | Serial |
| Candidate | Frozen working-tree sources and DLL hashes in manifest | Parallel |

Upstream 0.9.4 was subsequently reviewed and deliberately deferred; 0.9.2 is the
comparison pin, not a claim about the latest available release. The observer patch
was separately checked against pristine upstream on two deterministic fixtures:
byte-identical TDLD, respectively 499 and 705 normalized events, and exact renders.

The focused scope was:

- Real local JPEG: 256×256, Arbitrary/16, Switch 2, auto-home, large brushes enabled,
  TSP 1 second, dynamic buckets OFF/ON, three production repetitions in all four versions: 24 checks.
- Candidate photo, serial/parallel-one-thread/parallel-two-threads, OFF/ON, one
  repetition each: 6 checks.
- Candidate production, grid64/rings128/mosaic256, TSP 1, OFF/ON, one repetition: 6 checks.
- Candidate production photo, TSP 5, OFF/ON, one repetition: 2 checks.

“Buckets OFF” disables dynamic region fills; the existing one-off full-canvas
background fill can still run. Historical V2 exposes its older experimental bucket
algorithm; ON does not imply identical implementation across versions.

## Correctness and performance

All 38 renders matched their quantized reference exactly, with zero missing,
wrong-colour or extra pixels and strict out-of-bounds checks. Source/reference
pixel hashes agreed across versions. Every saved stream decoded successfully and
ended with released buttons/DPad and neutral sticks. All 32 V2 checks had identical
v3/v4 payload bodies; the six upstream checks are v3-only. This establishes payload
equivalence, not equivalent hardware timing between milliseconds and HID polls.

The candidate ON production photo actually exercised 63 bucket fills, including
the background, and one erase-all. Candidate serial ON selected 51 fills and still
rendered the same image: candidate/route choices need not be identical for correct
coverage.

Photo production medians, three repetitions, TSP 1 (seconds):

| Version | Generate OFF | Generate ON | Nominal draw OFF | Nominal draw ON |
| --- | ---: | ---: | ---: | ---: |
| V2 release | 4.183 | 3.706 | 4659.925 | 4407.900 |
| V2 previous main | 4.232 | 3.712 | 4661.025 | 4402.550 |
| Upstream 0.9.2 | 27.296 | 44.241 | 4641.625 | 4288.325 |
| Candidate | 4.228 | 7.814 | 4651.525 | 4312.925 |

The agreed production-photo/OFF gate passes versus previous main: generation
median −0.09% and nominal draw median −0.20%, below the +20%/+5% regression limits.
Candidate generation range was 4.225–4.497 s versus main 4.218–4.472 s; nominal
draw range was 4650.575–4655.175 s versus main 4659.675–4661.475 s. No controlled
five-repeat follow-up was triggered for that scoped gate.

Candidate ON reduces nominal draw time by 7.28% versus its own OFF result, while
generation takes longer. It also takes longer to generate than historical V2 ON;
this is a measured tradeoff, not an across-the-board speedup. TSP 5's single
candidate samples took 12.524/28.028 s to generate and estimated
4594.875/4244.225 s of drawing (OFF/ON); these are individual samples, not medians
from a repeated performance study. Nominal drawing times are software estimates,
not measured Switch completion times.

## Streams and limits

Three additional read-only comparisons used the first production-photo/OFF
repetition; no image was regenerated. First differences use zero-based normalized
event indices:

| Compared with candidate | Shared prefix | First difference | Classification |
| --- | ---: | --- | --- |
| V2 release | 307 events | Candidate Y; historical X | Changed setup order: select colour before bucket |
| V2 previous main | 307 events | Candidate Y; historical X | Same intentional setup-order change |
| Upstream 0.9.2 | 4104 events | RIGHT versus UPRIGHT | Navigation/route divergence; exact layer is not encoded in TDLD |

The first differences are controller actions, not wait durations. Historical
toolbar waits also changed intentionally, so complete stream equality is not a
requirement across these versions. Equal renders do not prove that game menu
coordinates, UI waits or HID delivery work on a Switch. Hardware validation remains
pending, including a long drawing and the experimental poll-timing mode.

Separately, the repeated small-fixture suite covers **320 generations** (four
scenarios × two bucket modes × four solver arms × ten repeats), with both Switch
versions covered across scenarios. The parent verification recorded 222/222 Core
tests passing. These figures are distinct from the 38 focused comparisons.

## Reproduction and retained evidence

Run [the focused runner](../Comparison/run_focused.py) using the commands in
[Comparison/README.md](../Comparison/README.md). Local evidence is retained at
`/tmp/tdld-092-audit/focused-final/`: `manifest.json`, `summary.json`,
`performance-gates.json`, `reused-evidence.json`, `stream-differences/`, and every
per-repeat expected/rendered/diff PNG, TDLD, log and artifact-hash file. The private
JPEG and derived images are intentionally not committed.

The manifest embeds the immutable source/DLL manifest, whose SHA-256 is
`3c772a69f4a6cbb43b9c7cbe1151b2185ab388812dfffa95d04211c70cb70cf3`.
Candidate Core DLL SHA-256 is
`b59d420a2596d8af467abd1ac858e2fba9516e6d600fede0c338ce09eee50f19`.
Subsequent documentation/UI commits do not change the identity of these measured
binaries.

The earlier complete 48-run historical subset remains at `matrix-final/`.
Interrupted/sleep-affected batches are preserved under `quarantine/`, excluded
from accepted timing statistics. Their partial correct renders are not counted
toward either completed scope. The remaining full matrix is deferred.
