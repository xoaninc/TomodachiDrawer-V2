#!/usr/bin/env python3
"""Explicit reduced audit: 36 checks, plus two optional TSP-5 photo checks."""
import argparse
import datetime
import json
import signal
import statistics
import subprocess
import sys
from pathlib import Path

from run_matrix import SYNTHETICS, guard_directory, measured_process, sha, stop_child, validate_batch
from performance_gates import evaluate


def specifications(photo, include_five):
    specs = []
    for version in ('candidate', 'v2-main', 'upstream-observed', 'v2-release'):
        for buckets in ('off', 'on'):
            arm = 'serial' if version.startswith('upstream-') else 'parallel'
            specs.append(dict(version=version, buckets=buckets, budget='1', dataset='photo', arm=arm, repeats=3))
    for buckets in ('off', 'on'):
        for arm in ('serial', 'parallel-1thread', 'parallel-2threads'):
            specs.append(dict(version='candidate', buckets=buckets, budget='1', dataset='photo', arm=arm, repeats=1))
    for buckets in ('off', 'on'):
        specs.append(dict(version='candidate', buckets=buckets, budget='1', dataset='synthetic', arm='parallel', repeats=1))
    if include_five:
        for buckets in ('off', 'on'):
            specs.append(dict(version='candidate', buckets=buckets, budget='5', dataset='photo', arm='parallel', repeats=1))
    for spec in specs:
        spec['images'] = [photo.stem] if spec['dataset'] == 'photo' else list(SYNTHETICS)
        spec['arms'] = [spec['arm']]
        spec['label'] = f"{spec['version']}-bucket{spec['buckets']}-tsp{spec['budget']}-{spec['dataset']}-{spec['arm']}"
    return specs


def summarize(output, specs, completed):
    rows, references, source_pixels, missing = [], {}, {}, []
    for spec in specs:
        doc = completed.get(spec['label'])
        if doc is None:
            missing.append(spec['label'])
            continue
        for image in spec['images']:
            samples = [r for r in doc['Results'] if r['Image'] == image]
            row = dict(Configuration=spec['label'], Image=image, Arm=spec['arm'], Repeats=len(samples), AllPixelsCorrect=all(r['Passed'] for r in samples))
            for metric in ('GenerateSeconds', 'DrawSeconds', 'DPadTaps', 'PaintActions'):
                values = [r[metric] for r in samples]
                row[metric] = dict(Median=statistics.median(values), Min=min(values), Max=max(values))
            rows.append(row)
            for sample in samples:
                references.setdefault(image, set()).add(sample['ExpectedPixelSha256'])
                source_pixels.setdefault(image, set()).add(sample['SourcePixelSha256'])
    reference_differences = {k: sorted(v) for k, v in references.items() if len(v) > 1}
    source_differences = {k: sorted(v) for k, v in source_pixels.items() if len(v) > 1}
    result = dict(Scope='User-approved focused audit; the 624-run matrix was stopped and is not complete.',
                  ExpectedRuns=sum(len(s['images']) * s['repeats'] for s in specs),
                  Runs=sum(r['Repeats'] for r in rows), PendingBatches=missing,
                  ReferenceDifferences=reference_differences, SourcePixelDifferences=source_differences,
                  CompleteAndPassed=not (missing or reference_differences or source_differences), Summary=rows)
    (output / 'summary.json').write_text(json.dumps(result, indent=2))
    # The reduced performance gate is explicitly limited to the production photo, buckets off.
    gate_rows = [r for r in rows if r['Configuration'] in
                 ('candidate-bucketoff-tsp1-photo-parallel', 'v2-main-bucketoff-tsp1-photo-parallel')]
    gate = evaluate({'Summary': gate_rows, 'CompleteAndPassed': len(gate_rows) == 2})
    gate['FocusedProductionComparisonComplete'] = gate.pop('FullMatrixCompleteAndCorrect')
    gate['Scope'] = 'Production photo, TSP 1, buckets off, three repeats per version only.'
    (output / 'performance-gates.json').write_text(json.dumps(gate, indent=2))
    print(json.dumps({k: v for k, v in result.items() if k != 'Summary'}), flush=True)
    if gate['ControlledRepeatRequired']:
        print('PERFORMANCE GATE REQUIRES FOLLOW-UP', json.dumps(gate['ControlledRepeatRequired']), flush=True)
    return result


def run(args):
    root = args.root.resolve()
    output = root / args.output
    output.mkdir(exist_ok=True)
    frozen_path = root / 'matrix-final/manifest.json'
    frozen = json.loads(frozen_path.read_text())
    if sha(args.photo) != frozen['photoSha256']:
        raise ValueError('Private photo changed since the frozen audit.')
    for version, hashes in frozen['assemblies'].items():
        for filename, digest in hashes.items():
            if sha(root / 'adapters' / version / 'bin/Release/net10.0' / filename) != digest:
                raise ValueError(f'Frozen DLL changed: {version}/{filename}')
    specs = specifications(args.photo, args.include_tsp5)
    manifest = dict(Scope=specs, FrozenManifestPath=str(frozen_path), FrozenManifestSha256=sha(frozen_path),
                    Frozen=frozen, ScriptSha256=sha(Path(__file__)), OptionalTsp5=args.include_tsp5)
    manifest_path = output / 'manifest.json'
    if manifest_path.exists() and json.loads(manifest_path.read_text()) != manifest:
        raise ValueError('Focused scope or frozen inputs changed; refusing stale resume.')
    if not manifest_path.exists():
        manifest_path.write_text(json.dumps(manifest, indent=2))
    completed = {}
    for spec in specs:
        if spec['version'] == 'v2-release' and spec['buckets'] == 'off':
            original = root / 'matrix-final/v2-release-bucketoff-tsp1-photo'
            old_spec = dict(version='v2-release', images=[args.photo.stem])
            previous = validate_batch(original, old_spec, 3)
            selected = [r for r in previous['Results'] if r['Arm'] == 'parallel']
            completed[spec['label']] = {**previous, 'Results': selected}
            (output / 'reused-evidence.json').write_text(json.dumps(dict(Source=str(original),
                SourceResultsSha256=sha(original / 'results.json'), SelectedArm='parallel', Repeats=3), indent=2))
        else:
            doc = guard_directory(output / spec['label'], spec, spec['repeats'])
            if doc is not None:
                completed[spec['label']] = doc
    summarize(output, specs, completed)
    caffeinate = subprocess.Popen(['caffeinate', '-i', '-w', str(__import__('os').getpid())]) if sys.platform == 'darwin' else None
    try:
        for spec in specs:
            if spec['label'] in completed:
                continue
            directory = output / spec['label']
            directory.mkdir(exist_ok=True)
            command = ['dotnet', str(root / 'adapters' / spec['version'] / 'bin/Release/net10.0/Comparison.dll'),
                       '--tsp', spec['budget'], '--repeats', str(spec['repeats']), '--arm', spec['arm'],
                       '--colours', '16' if spec['dataset'] == 'photo' else '32', '--switch', '2',
                       '--render-out', str(directory), '--out', str(directory / 'results.json')]
            if spec['dataset'] == 'photo':
                command += ['--image', str(args.photo), '--home']
            if spec['buckets'] == 'on':
                command += ['--buckets']
            (directory / 'command.json').write_text(json.dumps(command))
            print('RUN', spec['label'], datetime.datetime.now().isoformat(), flush=True)
            code = measured_process(command, directory)
            (directory / 'exit-code.txt').write_text(str(code))
            if code:
                raise RuntimeError(f'Focused batch failed: {spec["label"]}; evidence retained.')
            completed[spec['label']] = validate_batch(directory, spec, spec['repeats'])
            report = summarize(output, specs, completed)
            if report['SourcePixelDifferences'] or report['ReferenceDifferences']:
                raise RuntimeError('Cross-version source/reference mismatch; evidence retained.')
        return not summarize(output, specs, completed)['CompleteAndPassed']
    finally:
        if caffeinate is not None:
            stop_child(caffeinate)


if __name__ == '__main__':
    def terminate(signum, frame):
        raise KeyboardInterrupt(f'Focused coordinator received signal {signum}')
    signal.signal(signal.SIGTERM, terminate)
    parser = argparse.ArgumentParser()
    parser.add_argument('--root', type=Path, required=True)
    parser.add_argument('--photo', type=Path, required=True)
    parser.add_argument('--output', default='focused-final')
    parser.add_argument('--include-tsp5', action='store_true')
    raise SystemExit(run(parser.parse_args()))
