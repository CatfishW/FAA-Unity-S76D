"""Private V9 duration-only change; synthetic snapshots, no host actions."""
import contextlib
import io
import json
from pathlib import Path
import sys
import tempfile
import unittest
from unittest.mock import patch

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
import checkpoint_state as state
import flight_demo as demo
import sustain as s
from test_reliability import options
from experimental_fixtures import FastClock,orbit_checkpoint,orbit_api,save


def completed_normal_v8():
    data, _, orbit, position = orbit_checkpoint()
    data.update(privateSustainedV6=False, privateSustainedV7=False,
        privateSustainedV8=True, orbitState=state.orbit_state(orbit),
        recoveryRequested=False, recoverySucceeded=False, controlPhase='sustained')
    data['finalPausedNativeReceipt']['headingDial'] = data['headingLastCommand']
    return data, position


class V9DurationTests(unittest.TestCase):
    def run_normal(self, seconds, clock, native_rate=1., source_change=None):
        data, position = completed_normal_v8()
        if source_change:
            source_change(data)
        api = orbit_api(clock, position)
        read = api.read
        api.read = lambda name,*a,**kw:150.+native_rate*clock.now if name=='sim/time/total_flight_time_sec' else read(name,*a,**kw)
        with tempfile.TemporaryDirectory() as directory, contextlib.redirect_stdout(io.StringIO()) as output:
            path, checksum = save(Path(directory)/'source', data)
            args = options(Path(directory)/'next', sustain=True, orbit=True, seconds=seconds)
            args.resume_acquisition, args.resume_sha256 = path, checksum
            code = demo.run_demo(args, api, clock)
            result = json.loads(output.getvalue())
        return code, result, api

    def test_completed_normal_v8_source_and_v8_protocol_remain_compatible(self):
        code, result, api = self.run_normal(2., FastClock())
        self.assertEqual(code, 0)
        self.assertEqual(result['resumedFromCheckpoint']['sourceVersion'], 8)
        self.assertTrue(result['privateSustainedV8'])
        self.assertEqual(result['privateCandidateVersion'], 9)
        self.assertFalse(result['recoveryRequested'])
        self.assertTrue(result['pauseConfirmed'])
        self.assertFalse(result['flightAccepted'])

    def test_normal_runtime_over1800_refuses_before_controls_or_workers(self):
        with patch.object(s.ParallelSnapshot, 'start', side_effect=AssertionError('Must reject before workers')):
            code, result, api = self.run_normal(1800.001, FastClock())
        self.assertEqual(code, 1)
        self.assertIn('1800s', result['reason'])
        self.assertFalse(result['unpauseRequested'])
        self.assertFalse(any(x[0] in ('command','write','write_index') for x in api.actions))

    def test_normal_zero_default_is_finite1800_native_or_wall_seconds(self):
        class WideClock(FastClock):
            def sleep(self, seconds):
                self.now += 100.
        for native_rate, expected_wall in [(1.,1800.), (20.,100.), (.001,1800.)]:
            with self.subTest(native_rate=native_rate):
                clock = WideClock()
                code, result, _ = self.run_normal(0., clock, native_rate)
                self.assertEqual(code, 0)
                self.assertEqual(result['durationLimitSeconds'], 1800.)
                self.assertEqual(clock.now, expected_wall)
                self.assertTrue(result['pauseConfirmed'])

    def test_unqualified_recovery_source_is_not_upgraded_by_normal_longer_cap(self):
        def incomplete(data):
            data.update(recoveryRequested=True, recoverySucceeded=False, controlPhase='recovery')
        code, result, api = self.run_normal(1800., FastClock(), source_change=incomplete)
        self.assertEqual(code, 1)
        self.assertIn('never qualified', result['reason'])
        self.assertFalse(result['unpauseRequested'])
        self.assertFalse(any(x[0] in ('command','write','write_index') for x in api.actions))

    def test_completed_v8_recovery_handoff_can_enter_normal_continuation(self):
        def qualified(data):
            data.update(recoveryRequested=True, recoverySucceeded=True, controlPhase='sustained')
        code, result, _ = self.run_normal(2., FastClock(), source_change=qualified)
        self.assertEqual(code, 0)
        self.assertEqual(result['resumedFromCheckpoint']['sourceVersion'], 8)
        self.assertFalse(result['recoveryRequested'])
        self.assertTrue(result['pauseConfirmed'])

    def test_field_times_stream_to_journal_without_expanding_retained_samples(self):
        data, position = completed_normal_v8()
        clock = FastClock()
        api = orbit_api(clock, position)
        with tempfile.TemporaryDirectory() as directory, contextlib.redirect_stdout(io.StringIO()):
            path, checksum = save(Path(directory)/'source', data)
            args = options(Path(directory)/'next', sustain=True, orbit=True, seconds=2.)
            args.resume_acquisition, args.resume_sha256 = path, checksum
            code = demo.run_demo(args, api, clock)
            result = json.loads((args.state_dir/'latest.json').read_text())
            journal = [json.loads(line) for line in (args.state_dir/'journal.jsonl').read_text().splitlines()]
        self.assertEqual(code, 0)
        timing = [row for row in journal if row['event']=='snapshot-timing']
        self.assertTrue(timing)
        self.assertTrue(all(len(row['perFieldReadTimes'])==54 for row in timing))
        self.assertTrue(all('perFieldReadTimes' not in row for row in result['samples']))
        active = [row for row in result['samples'] if row['label']=='sustained active']
        self.assertTrue(active)
        self.assertTrue(all(row['readWorkerLimit']==8 and row['captureWidthSeconds']>=0 for row in active))
        self.assertTrue(result['readWorkersClosed'])

    def test_cli_normal1800_and_recovery300_caps_differ_before_api(self):
        paired = ['--sustain','--orbit','--resume-acquisition','source.json','--resume-sha256','0'*64]
        for argv in [[*paired,'--seconds','1800.001'],
                     [*paired,'--recover-from-guard','--seconds','300.001']]:
            with (self.subTest(argv=argv), patch.object(demo, 'NativeApi') as api,
                  contextlib.redirect_stderr(io.StringIO()), self.assertRaises(SystemExit) as exc):
                demo.main(argv)
            self.assertEqual(exc.exception.code, 2)
            api.assert_not_called()
        for flags, expected in [([],1800.), (['--recover-from-guard'],300.)]:
            with patch.object(demo, 'NativeApi'), patch.object(demo, 'run_demo', return_value=0) as run:
                self.assertEqual(demo.main([*paired,*flags,'--seconds',str(expected)]), 0)
                self.assertEqual(run.call_args.args[0].seconds, expected)
        self.assertEqual(s.RECOVERY_SECONDS, 15.)
        self.assertEqual(s.RECOVERY_TOTAL_SECONDS, 300.)


if __name__ == '__main__':
    unittest.main()
