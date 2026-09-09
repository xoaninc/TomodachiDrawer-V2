#!/usr/bin/env python3
"""Compare matched candidate/baseline medians without discarding failed repetitions."""
import argparse
import json
from pathlib import Path


LIMITS = {'DrawSeconds': 0.05, 'GenerateSeconds': 0.20}


def evaluate(summary):
    baseline = {}
    candidates = []
    for row in summary['Summary']:
        version, separator, configuration = row['Configuration'].partition('-bucket')
        if not separator:
            raise ValueError('Unrecognized matrix configuration.')
        key = (configuration, row['Image'], row['Arm'])
        if version == 'v2-main':
            if key in baseline:
                raise ValueError('Duplicate baseline configuration.')
            baseline[key] = row
        elif version == 'candidate':
            candidates.append((key, row))
    comparisons, missing = [], []
    for key, candidate in candidates:
        previous = baseline.get(key)
        if previous is None:
            missing.append(key)
            continue
        if any(r['Repeats'] < 3 or not r['AllPixelsCorrect'] for r in (candidate, previous)):
            raise ValueError('Performance comparison needs every planned correct repetition.')
        gated = key[0].startswith('off-')
        metrics = {}
        for metric, limit in LIMITS.items():
            old = previous[metric]['Median']
            new = candidate[metric]['Median']
            ratio = new / old - 1 if old else (0 if new == 0 else None)
            exceeded = gated and new > old * (1 + limit)
            metrics[metric] = {'Baseline': previous[metric], 'Candidate': candidate[metric],
                               'ChangePercent': None if ratio is None else ratio * 100,
                               'LimitPercent': limit * 100 if gated else None,
                               'RequiresFiveControlledRepeats': exceeded}
        comparisons.append({'Configuration': key[0], 'Image': key[1], 'Arm': key[2],
                            'BucketsOffGateApplies': gated, 'Metrics': metrics})
    triggers = [row for row in comparisons if any(m['RequiresFiveControlledRepeats'] for m in row['Metrics'].values())]
    complete = summary.get('CompleteAndPassed') is True and not missing and bool(comparisons)
    return {'Baseline': 'ae5dbe35350e5354f0e7b0bb3565f52b31060f91',
            'FullMatrixCompleteAndCorrect': complete,
            'MissingBaselineConfigurations': missing,
            'Comparisons': comparisons, 'ControlledRepeatRequired': triggers,
            'GatePassed': complete and not triggers}


if __name__ == '__main__':
    parser = argparse.ArgumentParser()
    parser.add_argument('summary', type=Path)
    parser.add_argument('--out', type=Path, required=True)
    args = parser.parse_args()
    report = evaluate(json.loads(args.summary.read_text()))
    args.out.write_text(json.dumps(report, indent=2))
    print(json.dumps({k: v for k, v in report.items() if k not in ('Comparisons', 'ControlledRepeatRequired')}, indent=2))
    print(f"Matched configurations: {len(report['Comparisons'])}; requiring five controlled repeats: {len(report['ControlledRepeatRequired'])}")
    raise SystemExit(0 if report['GatePassed'] else 1)
