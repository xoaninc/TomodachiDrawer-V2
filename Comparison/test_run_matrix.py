"""Lightweight persisted-result and suspension guards; no Core builds or drawings."""
import contextlib
import copy
import importlib.util
import io
import json
import tempfile
import unittest
from pathlib import Path
from unittest.mock import patch

loader = importlib.util.spec_from_file_location('matrix', Path(__file__).with_name('run_matrix.py'))
matrix = importlib.util.module_from_spec(loader)
loader.loader.exec_module(matrix)


class MatrixGuards(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name)
        self.spec = dict(label='upstream-observed-bucketoff-tsp1-grid64', version='upstream-observed', images=('grid64',))
        self.directory = self.root / self.spec['label']
        self.directory.mkdir()

    def valid_batch(self, repeats=2):
        rows = []
        for repeat in range(1, repeats + 1):
            run = self.directory / 'grid64/serial' / f'{repeat:02d}'
            run.mkdir(parents=True)
            files = {'drawing-v3.tdld': b'TDLD\x03\x00\x50\x00', 'expected.png': b'fixture', 'rendered.png': b'fixture', 'diff.png': b'fixture', 'generation.log': b'fixture'}
            for name, content in files.items(): (run / name).write_bytes(content)
            hashes = {name: matrix.sha(run / name) for name in files}
            (run / 'artifact-hashes.json').write_text(json.dumps(hashes))
            row = dict(Image='grid64', Arm='serial', Repeat=repeat, Passed=True, TraceVerified=True,
                       Comparison=dict(Missing=0, WrongColour=0, Extra=0), DecodedEventCount=1,
                       FinalControllerState=dict(Buttons=0, DPad=8, LX=128, LY=128, RX=128, RY=128),
                       SourcePixelSha256='0'*64, ExpectedPixelSha256='1'*64, RenderedPixelSha256='1'*64,
                       V3Sha256=hashes['drawing-v3.tdld'], V3V4BodiesEqual=None,
                       GenerateSeconds=1, DrawSeconds=2, DPadTaps=0, PaintActions=0,
                       ArtifactDirectory=str(run))
            (run / 'result.json').write_text(json.dumps(row))
            rows.append(row)
        doc = dict(Passed=True, Results=rows, Settings={'QuantizerSettings': {'ColourCount':32}})
        (self.directory / 'results.json').write_text(json.dumps(doc))
        (self.directory / 'exit-code.txt').write_text('0')
        return doc

    def write_doc(self, doc):
        (self.directory / 'results.json').write_text(json.dumps(doc))

    def test_empty_directory_can_start(self):
        self.assertIsNone(matrix.guard_directory(self.directory, self.spec, 2))

    def test_partial_directory_fails_without_modifying_previous_bytes(self):
        file = self.directory / 'stdout.log'
        file.write_bytes(b'existing evidence')
        with self.assertRaises(ValueError): matrix.guard_directory(self.directory, self.spec, 2)
        self.assertEqual(b'existing evidence', file.read_bytes())
        self.assertEqual(['stdout.log'], [p.name for p in self.directory.iterdir()])

    def test_complete_batch_reuses_every_repeat(self):
        self.valid_batch()
        self.assertEqual(2, len(matrix.guard_directory(self.directory, self.spec, 2)['Results']))

    def test_missing_or_duplicate_repeat_is_rejected(self):
        doc = self.valid_batch()
        doc['Results'].pop()
        self.write_doc(doc)
        with self.assertRaises(ValueError): matrix.validate_batch(self.directory, self.spec, 2)
        doc['Results'].append(copy.deepcopy(doc['Results'][0]))
        self.write_doc(doc)
        with self.assertRaises(ValueError): matrix.validate_batch(self.directory, self.spec, 2)

    def test_missing_empty_or_boolean_event_count_is_rejected(self):
        original = self.valid_batch()
        for events in [None, 0, True]:
            doc = copy.deepcopy(original)
            doc['Results'][0]['DecodedEventCount'] = events
            self.write_doc(doc)
            with self.assertRaises(ValueError): matrix.validate_batch(self.directory, self.spec, 2)

    def test_missing_or_held_final_controller_state_is_rejected(self):
        original = self.valid_batch()
        for state in [None, {**original['Results'][0]['FinalControllerState'], 'Buttons':4}]:
            doc = copy.deepcopy(original)
            doc['Results'][0]['FinalControllerState'] = state
            self.write_doc(doc)
            with self.assertRaises(ValueError): matrix.validate_batch(self.directory, self.spec, 2)

    def test_malformed_or_changed_artifact_is_rejected(self):
        self.valid_batch()
        (self.directory / 'grid64/serial/01/drawing-v3.tdld').write_bytes(b'changed')
        with self.assertRaises(ValueError): matrix.validate_batch(self.directory, self.spec, 2)

    def test_invalidated_batch_never_contributes_performance_metrics(self):
        self.valid_batch()
        (self.directory / 'PERFORMANCE_INVALID.json').write_text('{}')
        with contextlib.redirect_stdout(io.StringIO()):
            report = matrix.summarize(self.root, [self.spec], 2)
        self.assertEqual(0, report['Runs'])
        self.assertFalse(report['CompleteAndPassed'])
        self.assertEqual(1, len(report['InvalidBatches']))
        self.assertEqual([], report['Summary'])

    def test_incomplete_batch_cannot_make_summary_pass(self):
        self.valid_batch()
        with contextlib.redirect_stdout(io.StringIO()):
            report = matrix.summarize(self.root, [self.spec], 3)
        self.assertFalse(report['CompleteAndPassed'])
        self.assertEqual(0, report['Runs'])

    def test_missing_whole_batch_cannot_make_matrix_pass(self):
        self.valid_batch()
        missing = {**self.spec, 'label':'missing'}
        with contextlib.redirect_stdout(io.StringIO()):
            report = matrix.summarize(self.root, [self.spec, missing], 2)
        self.assertEqual(2, report['Runs'])
        self.assertFalse(report['CompleteAndPassed'])
        self.assertEqual(['missing'], report['PendingBatches'])

    def test_only_complete_evidence_passes(self):
        self.valid_batch()
        with contextlib.redirect_stdout(io.StringIO()):
            report = matrix.summarize(self.root, [self.spec], 2)
        self.assertTrue(report['CompleteAndPassed'])

    def test_sleep_and_clock_discontinuity_are_detected(self):
        guard = matrix.SleepGuard()
        guard.wall, guard.monotonic = 100, 100
        self.assertIsNone(guard.check(wall=105, monotonic=105, lid_closed=False))
        self.assertIn('Suspend', guard.check(wall=705, monotonic=110, lid_closed=False))
        self.assertIn('lid closed', guard.check(wall=710, monotonic=115, lid_closed=True))

    def test_child_is_stopped_and_artifacts_preserved_on_sleep(self):
        class Child:
            returncode = None
            killed = False
            def wait(self, timeout=None):
                if self.returncode is None: raise matrix.subprocess.TimeoutExpired('fixture', timeout)
                return self.returncode
            def poll(self): return self.returncode
            def terminate(self): self.returncode = -15
            def kill(self): self.killed = True; self.returncode = -9
        child = Child()
        def mark(directory, reason): (directory / 'PERFORMANCE_INVALID.json').write_text(reason)
        with patch.object(matrix.subprocess, 'Popen', return_value=child), patch.object(matrix.SleepGuard, 'check', side_effect=[None, 'suspend detected']), patch.object(matrix, 'mark_invalid', side_effect=mark):
            code = matrix.measured_process(['unused'], self.directory)
        self.assertEqual(-15, code)
        self.assertTrue((self.directory / 'PERFORMANCE_INVALID.json').exists())
        self.assertTrue((self.directory / 'stdout.log').exists())


    def test_interruption_stops_own_child_and_invalidates_partial_artifacts(self):
        class Child:
            returncode = None
            def wait(self, timeout=None):
                if self.returncode is None: raise KeyboardInterrupt('test interruption')
                return self.returncode
            def poll(self): return self.returncode
            def terminate(self): self.returncode = -15
            def kill(self): self.returncode = -9
        child = Child()
        def mark(directory, reason): (directory / 'PERFORMANCE_INVALID.json').write_text(reason)
        with patch.object(matrix.subprocess, 'Popen', return_value=child), patch.object(matrix.SleepGuard, 'check', return_value=None), patch.object(matrix, 'mark_invalid', side_effect=mark):
            with self.assertRaises(KeyboardInterrupt): matrix.measured_process(['unused'], self.directory)
        self.assertEqual(-15, child.returncode)
        self.assertTrue((self.directory / 'PERFORMANCE_INVALID.json').exists())


if __name__ == '__main__': unittest.main()
