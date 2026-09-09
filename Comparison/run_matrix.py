#!/usr/bin/env python3
"""Sequential, resumable comparison matrix. Never select only the best correctness result."""
import argparse
import collections
import datetime
import math
import os
import re
import signal
import time
import hashlib
import json
import platform
import statistics
import subprocess
import sys
from pathlib import Path


def sha(path):
    return hashlib.sha256(path.read_bytes()).hexdigest()


ARMS = ('serial', 'parallel-1thread', 'parallel-2threads', 'parallel')
SYNTHETICS = ('grid64', 'rings128', 'mosaic256')


def expected_keys(spec, repeats):
    arms = spec.get('arms', ('serial',) if spec['version'].startswith('upstream-') else ARMS)
    return {(image, arm, repeat) for image in spec['images'] for arm in arms for repeat in range(1, repeats + 1)}


def matrix_specs(args):
    return [dict(version=version, buckets=buckets, budget=budget, dataset=dataset,
                 label=f'{version}-bucket{buckets}-tsp{budget}-{dataset}',
                 images=SYNTHETICS if dataset == 'synthetic' else (args.photo.stem,) if dataset == 'photo' else (dataset,))
            for version in args.versions.split(',') for buckets in args.buckets.split(',')
            for budget in args.budgets.split(',') for dataset in args.datasets.split(',')]


def validate_batch(directory, spec, repeats):
    """Validate persisted evidence before reuse or inclusion in performance summaries."""
    if list(directory.rglob('PERFORMANCE_INVALID.json')):
        raise ValueError('Performance invalidation marker present; quarantine and rerun this batch.')
    result = directory / 'results.json'
    if not result.is_file():
        raise ValueError('Nonempty/incomplete batch has no results.json; refusing overwrite.')
    if (directory / 'exit-code.txt').read_text().strip() != '0':
        raise ValueError('Recorded process did not exit successfully.')
    doc = json.loads(result.read_text())
    if doc.get('Passed') is not True:
        raise ValueError('Batch declares a correctness/export failure.')
    rows = doc.get('Results')
    if not isinstance(rows, list):
        raise ValueError('Missing results array.')
    keys = [(r.get('Image'), r.get('Arm'), r.get('Repeat')) for r in rows]
    if collections.Counter(keys) != collections.Counter(expected_keys(spec, repeats)):
        raise ValueError('Missing, duplicate or unexpected image/arm/repetition rows.')
    for row in rows:
        if row.get('Passed') is not True or row.get('V3V4BodiesEqual') is False:
            raise ValueError('Row failed coverage or v3/v4 parity.')
        if type(row.get('Repeat')) is not int:
            raise ValueError('Repetition index must be an integer.')
        neutral = {'Buttons': 0, 'DPad': 8, 'LX': 128, 'LY': 128, 'RX': 128, 'RY': 128}
        if row.get('FinalControllerState') != neutral:
            raise ValueError('Missing or non-neutral final controller state.')
        events = row.get('DecodedEventCount')
        if type(events) is not int or events <= 0:
            raise ValueError('Missing or empty decoded controller events.')
        for field in ('GenerateSeconds', 'DrawSeconds', 'DPadTaps', 'PaintActions'):
            value = row.get(field)
            if type(value) not in (int, float) or not math.isfinite(value) or value < 0:
                raise ValueError(f'Invalid numeric metric {field}.')
        if spec['version'] != 'upstream-clean':
            comparison = row.get('Comparison') or {}
            if row.get('TraceVerified') is not True or any(comparison.get(k) != 0 for k in ('Missing', 'WrongColour', 'Extra')):
                raise ValueError('Missing or failing rendered-pixel comparison.')
            if row.get('RenderedPixelSha256') != row.get('ExpectedPixelSha256'):
                raise ValueError('Rendered/reference pixel hashes differ.')
        for field in ('SourcePixelSha256', 'ExpectedPixelSha256', 'V3Sha256'):
            if not re.fullmatch('[0-9a-f]{64}', str(row.get(field, ''))):
                raise ValueError(f'Missing or invalid {field}.')
        run_dir = directory / row['Image'] / row['Arm'] / f"{row['Repeat']:02d}"
        if Path(row['ArtifactDirectory']).resolve() != run_dir.resolve():
            raise ValueError('Artifact directory does not match this row.')
        if sha(run_dir / 'drawing-v3.tdld') != row['V3Sha256']:
            raise ValueError('TDLD artifact missing or hash mismatch.')
        persisted = json.loads((run_dir / 'result.json').read_text())
        if persisted != row:
            raise ValueError('Batch and per-repeat result disagree.')
        hashes = json.loads((run_dir / 'artifact-hashes.json').read_text())
        required = {'drawing-v3.tdld', 'expected.png', 'generation.log'}
        if spec['version'] != 'upstream-clean':
            required |= {'rendered.png', 'diff.png'}
        if row.get('V3V4BodiesEqual') is True:
            required.add('drawing-v4.tdld')
            if (run_dir / 'drawing-v3.tdld').read_bytes()[6:] != (run_dir / 'drawing-v4.tdld').read_bytes()[16:]:
                raise ValueError('Persisted v3/v4 payloads differ.')
        if not required.issubset(hashes):
            raise ValueError('Missing required artifact hashes.')
        for name, digest in hashes.items():
            if Path(name).name != name or sha(run_dir / name) != digest:
                raise ValueError('Artifact hash mismatch or unsafe manifest path.')
    return doc


def guard_directory(directory, spec, repeats):
    if directory.exists() and any(directory.iterdir()):
        return validate_batch(directory, spec, repeats)
    return None


def clamshell_closed():
    if sys.platform != 'darwin':
        return False
    result = subprocess.run(['ioreg', '-r', '-k', 'AppleClamshellState'], capture_output=True, text=True, check=True, timeout=10)
    return bool(re.search(r'"AppleClamshellState"\s*=\s*Yes', result.stdout))


class SleepGuard:
    """Wall/monotonic disagreement catches suspend and clock jumps; lid checks catch clamshell early."""
    def __init__(self):
        self.wall = time.time()
        self.monotonic = time.monotonic()

    def check(self, wall=None, monotonic=None, lid_closed=None):
        wall = time.time() if wall is None else wall
        monotonic = time.monotonic() if monotonic is None else monotonic
        discrepancy = (wall - self.wall) - (monotonic - self.monotonic)
        self.wall, self.monotonic = wall, monotonic
        closed = clamshell_closed() if lid_closed is None else lid_closed
        if closed:
            return 'Laptop lid closed; clamshell sleep can interrupt measurement.'
        if abs(discrepancy) > 2:
            return f'Suspend or wall-clock discontinuity detected ({discrepancy:.3f}s wall/monotonic disagreement).'
        return None


def stop_child(child):
    if child.poll() is None:
        child.terminate()
        try:
            child.wait(timeout=10)
        except subprocess.TimeoutExpired:
            child.kill()
            child.wait()


def mark_invalid(directory, reason):
    marker = {'PerformanceValid': False, 'Reason': reason,
              'Utc': datetime.datetime.now(datetime.timezone.utc).isoformat(),
              'Action': 'Own drawing child stopped; artifacts preserved. Quarantine this batch before rerunning.'}
    (directory / 'PERFORMANCE_INVALID.json').write_text(json.dumps(marker, indent=2))
    if sys.platform == 'darwin':
        try:
            result = subprocess.run(['pmset', '-g', 'log'], capture_output=True, text=True, timeout=30)
            lines = [line for line in result.stdout.splitlines() if any(word in line for word in ('Entering Sleep', 'Wake from', 'DarkWake from'))]
            (directory / 'power-events.log').write_text('\n'.join(lines[-200:]))
        except (OSError, subprocess.TimeoutExpired) as exc:
            (directory / 'power-events-error.txt').write_text(str(exc))


def measured_process(command, directory):
    guard = SleepGuard()
    try:
        reason = guard.check()
    except Exception as exc:
        reason = f'Sleep monitor failed before launch: {exc}'
    if reason:
        mark_invalid(directory, reason)
        return 1
    with (directory / 'stdout.log').open('x') as stdout:
        child = subprocess.Popen(command, stdout=stdout, stderr=subprocess.STDOUT)
        try:
            while True:
                try:
                    code = child.wait(timeout=5)
                except subprocess.TimeoutExpired:
                    code = None
                try:
                    reason = guard.check()
                except Exception as exc:
                    reason = f'Sleep monitor failed: {exc}'
                if reason:
                    stop_child(child)
                    mark_invalid(directory, reason)
                    return child.returncode or 1
                if code is not None:
                    return code
        except BaseException:
            stop_child(child)
            mark_invalid(directory, 'Comparison runner interrupted during an unfinished batch.')
            raise


def run(args):
    root = args.root.resolve()
    output = root / args.output
    output.mkdir(parents=True, exist_ok=True)
    repo = Path(__file__).resolve().parents[1]
    source_dirs = json.loads((root / 'adapter-sources.json').read_text())
    versions = args.versions.split(',')
    manifest = {'platform': platform.platform(), 'python': platform.python_version(), 'photoSha256': sha(args.photo), 'versions': {}, 'budgets': args.budgets, 'repeats': args.repeats, 'buckets': args.buckets}
    for version in versions:
        source = Path(source_dirs[version])
        files = sorted((source / 'TomodachiDrawer.Core').rglob('*.cs'))
        files += sorted((source / 'TomodachiDrawer.Core').glob('*.csproj'))
        manifest['versions'][version] = {str(p.relative_to(source)): sha(p) for p in files if not set(p.parts) & {'bin', 'obj'}}
    manifest['assemblies'] = {version: {p.name: sha(p) for p in (root / 'adapters' / version / 'bin/Release/net10.0').glob('*.dll')} for version in versions}
    manifest['commitIds'] = {'v2-release': '88b299f8b9731632793ebdfef8dbdd3659ed4d4c', 'v2-main': 'ae5dbe35350e5354f0e7b0bb3565f52b31060f91', 'upstream-clean': 'f3bc3895f1679e1b0e2efda21274a5ee6dd40ddb', 'upstream-observed': 'f3bc3895f1679e1b0e2efda21274a5ee6dd40ddb+observer', 'candidateHead': subprocess.check_output(['git', '-C', str(repo), 'rev-parse', 'HEAD'], text=True).strip()}
    manifest['harness'] = {str(p.relative_to(repo)): sha(p) for p in (repo / 'TomodachiDrawer.Bench').glob('*.cs')}
    manifest_path = output / 'manifest.json'
    if manifest_path.exists():
        previous = json.loads(manifest_path.read_text())
        amendments = previous.get('harnessDocumentationAmendments', [])
        amended_hashes = dict(previous['harness'])
        for amendment in amendments:
            file = amendment['File']
            if amended_hashes.get(file) != amendment['OldSha256']:
                raise RuntimeError('Broken documented harness amendment chain.')
            amended_hashes[file] = amendment['NewSha256']
        if manifest['harness'] != amended_hashes:
            raise RuntimeError('Unrecorded harness change; refusing stale resume.')
        # Keep original source hashes attached to historical DLLs. Only explicitly documented
        # comment changes may explain why the current source has a newer hash.
        manifest['harness'] = previous['harness']
        if amendments: manifest['harnessDocumentationAmendments'] = amendments
        if previous != manifest:
            raise RuntimeError('Sources/settings changed: use a new --output directory; refusing stale resume.')
    if not manifest_path.exists():
        manifest_path.write_text(json.dumps(manifest, indent=2))
    specs = matrix_specs(args)
    # Validate every existing directory before launching anything. A half-written batch is
    # evidence to preserve, not permission to overwrite its previous artifacts.
    for spec in specs:
        guard_directory(output / spec['label'], spec, args.repeats)
    report = summarize(output, specs, args.repeats)
    if args.validate_only:
        return bool(report['InvalidBatches'])
    if clamshell_closed():
        raise RuntimeError('Laptop lid is closed; open it before starting measured drawings.')
    caffeinate = None
    if sys.platform == 'darwin':
        caffeinate = subprocess.Popen(['caffeinate', '-i', '-w', str(os.getpid())])
    try:
        for spec in specs:
            directory = output / spec['label']
            if guard_directory(directory, spec, args.repeats) is not None:
                print('RESUME', spec['label'], flush=True)
                continue
            while (output / 'pause.request').exists():
                (output / 'paused.json').write_text(json.dumps({'Pid': os.getpid(), 'NextBatch': spec['label']}))
                time.sleep(2)
            (output / 'paused.json').unlink(missing_ok=True)
            if clamshell_closed():
                raise RuntimeError('Laptop lid closed before next batch; no drawing started.')
            directory.mkdir(exist_ok=True)
            dll = root / 'adapters' / spec['version'] / 'bin/Release/net10.0/Comparison.dll'
            result = directory / 'results.json'
            command = ['dotnet', str(dll), '--tsp', spec['budget'], '--repeats', str(args.repeats), '--colours', '16' if spec['dataset'] == 'photo' else '32', '--switch', '2', '--render-out', str(directory), '--out', str(result)]
            if spec['dataset'] == 'photo': command += ['--image', str(args.photo), '--home']
            elif spec['dataset'] != 'synthetic': command += ['--fixture', spec['dataset']]
            if spec['buckets'] == 'on': command += ['--buckets']
            if spec['version'] == 'upstream-clean': command += ['--untraced']
            print('RUN', spec['label'], flush=True)
            (directory / 'command.json').write_text(json.dumps(command))
            code = measured_process(command, directory)
            (directory / 'exit-code.txt').write_text(str(code))
            report = summarize(output, specs, args.repeats)
            if code or report['InvalidBatches']:
                raise RuntimeError(f"Batch {spec['label']} failed validation; artifacts retained, no further drawing started.")
        report = summarize(output, specs, args.repeats)
        return not report['CompleteAndPassed']
    finally:
        if caffeinate is not None:
            stop_child(caffeinate)


def summarize(output, specs, repeats):
    grouped = {}
    expected = {}
    correctness = []
    unobserved = 0
    source_pixels = {}
    invalid = []
    pending = []
    for spec in specs:
        directory = output / spec['label']
        if not directory.exists() or not any(directory.iterdir()):
            pending.append(spec['label'])
            continue
        try:
            doc = validate_batch(directory, spec, repeats)
        except (ValueError, OSError, KeyError, TypeError) as exc:
            invalid.append({'Batch': spec['label'], 'Reason': str(exc)})
            continue
        file = directory / 'results.json'
        for row in doc['Results']:
            label = file.parent.name
            key = (label, row['Image'], row['Arm'])
            grouped.setdefault(key, []).append(row)
            reference_key = (row['Image'], doc['Settings']['QuantizerSettings'].__str__())
            source_pixels.setdefault(row['Image'], set()).add(row['SourcePixelSha256'])
            expected.setdefault(reference_key, set()).add(row['ExpectedPixelSha256'])
            if not row['Passed']: correctness.append((label,row['Image'],row['Arm'],row['Repeat']))
            if not row['TraceVerified']: unobserved += 1
    rows = []
    for (label,image,arm), results in grouped.items():
        row = {'Configuration': label, 'Image': image, 'Arm': arm, 'Repeats': len(results), 'AllPixelsCorrect': all(r['Passed'] for r in results)}
        for name in ['GenerateSeconds', 'DrawSeconds', 'DPadTaps', 'PaintActions']:
            values = [r[name] for r in results]
            row[name] = {'Median': statistics.median(values), 'Min': min(values), 'Max': max(values)}
        rows.append(row)
    report = {'ExpectedRuns': sum(len(expected_keys(spec, repeats)) for spec in specs), 'PendingBatches': pending, 'InvalidBatches': invalid, 'CompleteAndPassed': False, 'Runs': sum(len(v) for v in grouped.values()), 'UnobservedRuns': unobserved, 'CorrectnessFailures': correctness, 'SourcePixelDifferences': {k: sorted(v) for k,v in source_pixels.items() if len(v)>1}, 'FailedProcesses': [str(p.parent) for p in output.glob('*/exit-code.txt') if p.read_text().strip() != '0'], 'ReferenceDifferences': {str(k): sorted(v) for k,v in expected.items() if len(v)>1}, 'Summary': rows}
    report['CompleteAndPassed'] = not (pending or invalid or correctness or report['ReferenceDifferences'] or report['SourcePixelDifferences'] or report['FailedProcesses'] or unobserved) and report['Runs'] == report['ExpectedRuns']
    (output / 'summary.json').write_text(json.dumps(report, indent=2))
    print(json.dumps({k:v for k,v in report.items() if k!='Summary'}, indent=2), flush=True)
    return report

if __name__ == '__main__':
    def terminate_requested(signum, frame):
        raise KeyboardInterrupt(f'Coordinator received signal {signum}.')
    signal.signal(signal.SIGTERM, terminate_requested)
    parser = argparse.ArgumentParser()
    parser.add_argument('--root', type=Path, required=True)
    parser.add_argument('--photo', type=Path, required=True)
    parser.add_argument('--versions', default='v2-release,v2-main,upstream-observed,candidate')
    parser.add_argument('--budgets', default='1,5')
    parser.add_argument('--buckets', default='off,on')
    parser.add_argument('--datasets', default='synthetic,photo')
    parser.add_argument('--repeats', type=int, default=3)
    parser.add_argument('--output', default='matrix')
    parser.add_argument('--validate-only', action='store_true')
    sys.exit(run(parser.parse_args()))
