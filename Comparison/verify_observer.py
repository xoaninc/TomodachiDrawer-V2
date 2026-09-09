#!/usr/bin/env python3
"""Assert observation-only upstream copy emits identical TDLD on solver-free fixtures."""
import argparse
import json
import subprocess
from pathlib import Path

parser = argparse.ArgumentParser()
parser.add_argument('--root', type=Path, required=True)
parser.add_argument('--reuse', action='store_true', help='Check existing parity runs without regenerating them.')
args = parser.parse_args()
root = args.root.resolve()
results = []
for fixture in ['observer-sparse12', 'observer-solid256']:
    files = {}
    for version in ['upstream-clean', 'upstream-observed']:
        output = root / 'observer-parity' / version / fixture
        dll = root / 'adapters' / version / 'bin/Release/net10.0/Comparison.dll'
        if not args.reuse:
            cmd = ['dotnet', str(dll), '--fixture', fixture, '--arm', 'serial', '--no-large-brush', '--buckets', '--home', '--render-out', str(output)]
            if version == 'upstream-clean': cmd.append('--untraced')
            subprocess.run(cmd, check=True)
        files[version] = output / fixture / 'serial/01/drawing-v3.tdld'
    pristine = files['upstream-clean']
    observed = files['upstream-observed']
    assert pristine.read_bytes() == observed.read_bytes(), f'Observer changed TDLD on {fixture}'
    report = root / 'observer-parity' / f'{fixture}-stream-diff.json'
    subprocess.run(['dotnet', str(root / 'adapters/candidate/bin/Release/net10.0/Comparison.dll'), '--compare-stream', str(pristine), '--against-stream', str(observed), '--out', str(report)], check=True)
    results.append({'Fixture': fixture, 'ByteIdentical': True, **json.loads(report.read_text())})
(root / 'observer-parity/summary.json').write_text(json.dumps(results, indent=2))
print(json.dumps(results, indent=2))
