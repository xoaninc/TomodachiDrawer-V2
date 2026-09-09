#!/usr/bin/env python3
"""Freeze final candidate hashes before its first measured drawing; retain historical provenance."""
import argparse
import datetime
import hashlib
import json
from pathlib import Path

parser = argparse.ArgumentParser()
parser.add_argument('--root', type=Path, required=True)
parser.add_argument('--output', default='matrix-final')
args = parser.parse_args()
root = args.root.resolve()
output = root / args.output
if list(output.glob('candidate-*/**/result.json')):
    raise RuntimeError('Candidate drawings already exist. Use a new result directory after code changes.')
manifest_file = output / 'manifest.json'
manifest = json.loads(manifest_file.read_text())
source = Path(json.loads((root / 'adapter-sources.json').read_text())['candidate'])
old = {'versions': manifest['versions']['candidate'], 'assemblies': manifest['assemblies']['candidate']}
sha = lambda p: hashlib.sha256(p.read_bytes()).hexdigest()
files = sorted((source / 'TomodachiDrawer.Core').rglob('*.cs'))
files += sorted((source / 'TomodachiDrawer.Core').glob('*.csproj'))
manifest['versions']['candidate'] = {str(p.relative_to(source)): sha(p) for p in files if not set(p.parts) & {'bin', 'obj'}}
manifest['assemblies']['candidate'] = {p.name: sha(p) for p in (root / 'adapters/candidate/bin/Release/net10.0').glob('*.dll')}
history = output / 'candidate-freeze-history.json'
records = json.loads(history.read_text()) if history.exists() else []
records.append({'Utc': datetime.datetime.now(datetime.timezone.utc).isoformat(), 'Previous': old, 'Final': {'versions': manifest['versions']['candidate'], 'assemblies': manifest['assemblies']['candidate']}, 'Reason': 'Final tested candidate rebuilt before any candidate measurements; historical source, DLL and output hashes unchanged.'})
history.write_text(json.dumps(records, indent=2))
manifest_file.write_text(json.dumps(manifest, indent=2))
print('Candidate-only source/assembly hashes refreshed. Historical provenance preserved.')
