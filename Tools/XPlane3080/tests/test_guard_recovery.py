"""Synthetic V7 guard-error recovery contracts; no hosts or physical flight claims."""
import base64
import contextlib
import copy
import io
import json
from pathlib import Path
import struct
import sys
import tempfile
import unittest
from unittest.mock import patch

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
import acquisition as a
import checkpoint_state as state
import conservative_control as c
import flight_demo as demo
import sustain as s
from test_reliability import options
from experimental_fixtures import FastClock,PROP,orbit_checkpoint,orbit_api,save


GUARD_ERROR = 'Original sustained control envelope exceeded'
ALT = 'sim/cockpit2/gauges/indicators/altitude_ft_pilot'


def f32(value):
    return struct.unpack('<f', struct.pack('<f', value))[0]


def recovery_fixture():
    """Protocol shape is inherited; all current values and commands are synthetic."""
    data, _, orbit, endpoint = orbit_checkpoint()
    last = .52000002
    observed = f32(last)
    values = {demo.P+'latitude': endpoint[0], demo.P+'longitude': endpoint[1],
        demo.P+'psi': 350., demo.P+'indicated_airspeed': 102., demo.P+'theta': 5.,
        demo.P+'phi': -2.3, demo.P+'local_vy': 3.2, ALT: data['targetAltitudeFt']-20.}
    data.update(privateSustainedV6=False, privateSustainedV7=True, privateSustainedV8=False,
        status='error', reason=GUARD_ERROR, error=GUARD_ERROR, unpauseRequested=True,
        flightAccepted=False, orbitState=state.orbit_state(orbit), values=copy.deepcopy(values))
    data['samples'][-1]['values'] = copy.deepcopy(values)
    data['control'].update(command=last, commandMainIndex0=last)
    data['finalPausedNativeReceipt'].update(mainCollective=observed, lastControllerCommand=last,
        headingDial=f32(data['headingLastCommand']))
    return data, orbit, endpoint


def recovery_api(clock, data, endpoint, profile=None, native_rate=1.):
    api = orbit_api(clock, endpoint)
    api.values[PROP][0] = data['finalPausedNativeReceipt']['mainCollective']
    api.values[s.HEADING] = data['finalPausedNativeReceipt'].get('headingDial', f32(data['headingLastCommand']))
    read = api.read
    def observed(name, *args, **kwargs):
        if name == 'sim/time/total_flight_time_sec':
            return 150.+native_rate*clock.now
        if name == ALT:
            return data['targetAltitudeFt']-20.
        if name == demo.P+'local_vy':
            return profile(clock.now) if profile is not None else 3.2 if clock.now < 1 else .5
        return read(name, *args, **kwargs)
    api.read = observed
    return api


class RecoveryV8Tests(unittest.TestCase):
    def run_fixture(self, api, clock, data=None, seconds=8., change=None, orbit=True):
        if data is None:
            data, _, _ = recovery_fixture()
        with tempfile.TemporaryDirectory() as directory, contextlib.redirect_stdout(io.StringIO()) as output:
            source, checksum = save(Path(directory)/'source', data)
            original = source.read_bytes()
            args = options(Path(directory)/'v8', seconds=seconds, sustain=True,
                recover_from_guard=True, orbit=orbit)
            args.resume_acquisition, args.resume_sha256 = source, checksum
            if change:
                change(args)
            code = demo.run_demo(args, api, clock)
            self.assertEqual(source.read_bytes(), original, 'Failed source must never be rewritten')
            report = json.loads(output.getvalue())
            journal_path = args.state_dir/'journal.jsonl'
            journal = [json.loads(line) for line in journal_path.read_text().splitlines()] if journal_path.exists() else []
        return code, report, journal

    def assert_no_control(self, api, report):
        self.assertFalse(report['unpauseRequested'])
        self.assertFalse(report['pauseRequested'])
        self.assertFalse(any(action[0] in ('command', 'write', 'write_index', 'request') for action in api.actions))

    def test_only_sha_pinned_exact_v7_vertical_guard_error_is_admitted(self):
        data, _, _ = recovery_fixture()
        with tempfile.TemporaryDirectory() as directory:
            source, checksum = save(Path(directory)/'source', data)
            cp = s.load_checkpoint(source, checksum, Path(directory)/'state', demo=demo, recover=True)
            self.assertEqual(cp['sourceStatus'], 'error')
            self.assertEqual(cp['sourceError'], GUARD_ERROR)
            self.assertEqual(cp['sourcePhase'], 'sustained-error-recovery')
            self.assertEqual(cp['lastCommand'], data['control']['commandMainIndex0'])
            self.assertEqual(cp['baseline'], .5)
            self.assertEqual(cp['targetFt'], data['targetAltitudeFt'])
            self.assertEqual(cp['orbitState'], data['orbitState'])
            self.assertEqual(cp['pausedNativeClock'], 150.)
            with self.assertRaises(c.ControlError):
                s.load_checkpoint(source, checksum, Path(directory)/'state', demo=demo)
            for checksum in (None, '0'*64):
                with self.assertRaises(c.ControlError):
                    s.load_checkpoint(source, checksum, Path(directory)/'state', demo=demo, recover=True)

    def test_other_reason_version_status_type_or_incomplete_receipt_is_read_only(self):
        def convert_version(data, version):
            data['privateSustainedV7'] = False
            data['privateSustainedV'+str(version)] = True
        changes = [lambda d: d.update(reason='Unrelated failure'),
            lambda d: d.update(error='Unrelated failure'), lambda d: d.update(status='completed'),
            lambda d: d.update(pauseConfirmed=1), lambda d: d.update(pauseRequested=False),
            lambda d: d.update(unpauseRequested=False), lambda d: d.update(flightAccepted=True),
            lambda d: convert_version(d, 6), lambda d: convert_version(d, 8),
            lambda d: d['finalPausedNativeReceipt'].update(twoEqualClocks=False),
            lambda d: d['finalPausedNativeReceipt'].update(mainCollective=.5201),
            lambda d: d['finalPausedNativeReceipt'].pop('headingDial'),
            lambda d: d['values'].update({demo.P+'local_vy': 3.3})]
        for index, change in enumerate(changes):
            with self.subTest(index=index):
                data, _, endpoint = recovery_fixture()
                change(data)
                clock = FastClock()
                api = recovery_api(clock, data, endpoint)
                code, report, _ = self.run_fixture(api, clock, data)
                self.assertEqual(code, 1)
                self.assert_no_control(api, report)

    def test_source_vertical_only_narrow_recovery_envelope_is_not_general_error_admission(self):
        for name, value in [(demo.P+'local_vy', 3.), (demo.P+'local_vy', 6.001),
                (demo.P+'indicated_airspeed', 89.99), (demo.P+'indicated_airspeed', 110.01),
                (demo.P+'theta', 15.01), (demo.P+'phi', 20.01),
                (ALT, (2500.+80.01)/c.FT_TO_M)]:
            with self.subTest(name=name, value=value):
                data, _, endpoint = recovery_fixture()
                data['values'][name] = value
                data['samples'][-1]['values'][name] = value
                clock = FastClock()
                api = recovery_api(clock, data, endpoint)
                code, report, _ = self.run_fixture(api, clock, data)
                self.assertEqual(code, 1)
                self.assertIn('vertical-only', report['reason'])
                self.assert_no_control(api, report)

    def test_bad_hash_foreign_unpaused_axes_override_mode_or_current_receipt_refuses_without_mutation(self):
        for kind in ('hash', 'foreign', 'unpaused', 'axis', 'override', 'mode', 'main', 'heading', 'clock', 'current-vy', 'no-orbit'):
            with self.subTest(kind=kind):
                data, _, endpoint = recovery_fixture()
                clock = FastClock()
                api = recovery_api(clock, data, endpoint)
                read = api.read
                change = None
                if kind == 'hash':
                    change = lambda args: setattr(args, 'resume_sha256', '0'*64)
                elif kind == 'foreign':
                    api.read = lambda name, *args, **kwargs: base64.b64encode(b'C172').decode() if name == 'sim/aircraft/view/acf_ICAO' else read(name, *args, **kwargs)
                elif kind == 'unpaused':
                    api.values['sim/time/paused'] = 0
                elif kind == 'axis':
                    api.values['sim/joystick/joy_mapped_axis_avail'][0] = 1
                elif kind == 'override':
                    api.values['sim/operation/override/override_prop_pitch'] = 1
                elif kind == 'mode':
                    api.values['sim/cockpit2/autopilot/speed_status'] = 0
                elif kind == 'main':
                    api.values[PROP][0] = f32(api.values[PROP][0]+1e-7)
                elif kind == 'heading':
                    api.values[s.HEADING] += .0001
                elif kind == 'clock':
                    api.read = lambda name, *args, **kwargs: 150.01 if name == 'sim/time/total_flight_time_sec' else read(name, *args, **kwargs)
                elif kind == 'current-vy':
                    api.read = lambda name, *args, **kwargs: 6.001 if name == demo.P+'local_vy' else read(name, *args, **kwargs)
                with patch.object(s.ParallelSnapshot, 'start', side_effect=AssertionError('Preflight started workers')):
                    code, report, _ = self.run_fixture(api, clock, data, change=change, orbit=kind != 'no-orbit')
                self.assertEqual(code, 1)
                self.assert_no_control(api, report)

    def test_recovery_wrapper_preserves_original_pd_formula_slew_and_baseline(self):
        checkpoint = {'targetFt': 2500/c.FT_TO_M, 'baseline': .49, 'lastCommand': .52}
        wrapper = s.GuardRecoveryCollective(checkpoint)
        original = a.AcquisitionCollective(checkpoint['targetFt'], checkpoint['baseline'])
        original.last_written = checkpoint['lastCommand']
        for dt, vy in [(.2, 3.2), (.3, 2.), (.25, .5), (.5, -.5)]:
            sample = (wrapper.last_written, 2495/c.FT_TO_M, vy, 102., 5., -2.3, dt, 150.)
            self.assertEqual(wrapper.update(*sample), original.update(*sample))
        self.assertEqual(wrapper.inner.baseline, checkpoint['baseline'])
        self.assertEqual(wrapper.target_m, 2500.)
        self.assertFalse(wrapper.diagnostic['integralErrorAccumulator'])
        for index, value in [(1, 2580.01/c.FT_TO_M), (2, 6.001), (3, 89.9), (3, 110.1), (4, 15.1), (5, 20.1)]:
            sample = [wrapper.last_written, 2500/c.FT_TO_M, 0., 100., 2., 0., .2, 151.]
            sample[index] = value
            with self.subTest(index=index), self.assertRaisesRegex(c.ControlError, 'recovery envelope'):
                wrapper.update(*sample)

    def test_three_continuous_native_seconds_handoff_same_orbit_and_exact_first_heading(self):
        data, orbit, endpoint = recovery_fixture()
        clock = FastClock()
        # A disturbance at t3 destroys credit earned at t1/t2. Handoff is t7.
        api = recovery_api(clock, data, endpoint, profile=lambda t: 3.2 if t < 1 else 1.5 if t == 3 else .5)
        with patch.object(c.TangentOrbit, '__init__', side_effect=AssertionError('Must not reseed orbit')):
            code, report, journal = self.run_fixture(api, clock, data, seconds=9)
        self.assertEqual(code, 0)
        self.assertTrue(report['privateSustainedV8'])
        self.assertFalse(report['privateSustainedV7'])
        self.assertTrue(report['recoverySucceeded'])
        self.assertEqual(report['controlPhase'], 'sustained')
        self.assertEqual(report['recoveryStableSinceNativeClock'], 154.)
        self.assertEqual(report['recoveryHandoffNativeClock'], 157.)
        self.assertEqual(report['initialMainBaselineNotLevelTrim'], data['initialMainBaselineNotLevelTrim'])
        self.assertEqual(report['targetAltitudeFt'], data['targetAltitudeFt'])
        self.assertEqual(report['orbitState'], state.orbit_state(orbit))
        self.assertEqual(api.heading_writes[0], (0., data['headingLastCommand']))
        writes = [action for action in api.actions if action[0] == 'write_index']
        self.assertEqual(writes[0], ('write_index', s.PROP, 0, f32(data['control']['commandMainIndex0'])))
        self.assertTrue(all(action[1:3] == (s.PROP, 0) for action in writes))
        self.assertEqual(api.values[PROP][1], .64)
        self.assertFalse(any(action[0] == 'command' and action[1].startswith('sim/autopilot/') for action in api.actions))
        phases = [row for row in journal if row['event'] == 'phase']
        self.assertEqual(len(phases), 1)
        self.assertEqual(phases[0]['phase'], 'strict-sustained-handoff')
        self.assertEqual(phases[0]['nativeClock'], 157.)
        handoff = next(row for row in journal if row['event'] == 'control' and row['nativeClock'] == 157.)
        self.assertEqual(phases[0]['preservedLastMainCommand'], handoff['control']['commandMainIndex0'])
        self.assertTrue(report['pauseConfirmed'])
        self.assertTrue(report['readWorkersClosed'])
        self.assertFalse(report['flightAccepted'])

    def test_strict_three_mps_guard_returns_immediately_after_recovery_handoff(self):
        data, _, endpoint = recovery_fixture()
        clock = FastClock()
        api = recovery_api(clock, data, endpoint, profile=lambda t: 3.2 if t < 1 or t >= 5 else .5)
        code, report, journal = self.run_fixture(api, clock, data, seconds=10)
        self.assertEqual(code, 1)
        self.assertEqual(report['reason'], GUARD_ERROR)
        self.assertEqual(clock.now, 5.)
        self.assertTrue(report['recoverySucceeded'])
        self.assertEqual(report['recoveryHandoffNativeClock'], 154.)
        self.assertEqual(report['controlPhase'], 'sustained')
        self.assertTrue(report['pauseConfirmed'])
        self.assertEqual(len([row for row in journal if row['event'] == 'phase']), 1)

    def test_unrecovered_phase_hits_native_or_wall_bound_and_never_succeeds(self):
        for native_rate, reason, maximum_wall in [(2., '15 native', 8.), (.1, '15 wall', 15.)]:
            with self.subTest(native_rate=native_rate):
                data, _, endpoint = recovery_fixture()
                clock = FastClock()
                api = recovery_api(clock, data, endpoint, profile=lambda _: 2., native_rate=native_rate)
                code, report, journal = self.run_fixture(api, clock, data, seconds=30)
                self.assertEqual(code, 1)
                self.assertIn(reason, report['reason'])
                self.assertLessEqual(clock.now, maximum_wall)
                self.assertFalse(report['recoverySucceeded'])
                self.assertEqual(report['controlPhase'], 'recovery')
                self.assertEqual(report['status'], 'error')
                self.assertTrue(report['pauseConfirmed'])
                self.assertFalse(any(row['event'] == 'phase' for row in journal))

    def test_total_phase_end_before_handoff_is_error_even_when_duration_requested(self):
        for native_rate, seconds in [(1., 1.), (2., 2.)]:
            data, _, endpoint = recovery_fixture()
            clock = FastClock()
            api = recovery_api(clock, data, endpoint, profile=lambda _: .5, native_rate=native_rate)
            code, report, _ = self.run_fixture(api, clock, data, seconds=seconds)
            self.assertEqual(code, 1)
            self.assertIn('before finite end', report['reason'])
            self.assertFalse(report['recoverySucceeded'])
            self.assertTrue(report['pauseConfirmed'])

    def test_worker_failure_pauses_before_closing_or_joining_the_pool(self):
        data, _, endpoint = recovery_fixture()
        clock = FastClock()
        api = recovery_api(clock, data, endpoint)
        read = api.read
        def failed(name, *args, **kwargs):
            if name == demo.P+'local_vy' and clock.now >= 1:
                raise OSError('Synthetic active read failure')
            return read(name, *args, **kwargs)
        api.read = failed
        close = s.ParallelSnapshot.close
        observations = []
        def observed_close(sampler):
            observations.append((api.values['sim/time/paused'], list(api.actions)))
            return close(sampler)
        with patch.object(s.ParallelSnapshot, 'close', observed_close):
            code, report, _ = self.run_fixture(api, clock, data)
        self.assertEqual(code, 1)
        self.assertIn('Synthetic active read failure', report['reason'])
        self.assertEqual(len(observations), 1)
        self.assertEqual(observations[0][0], 1)
        self.assertIn(('command', 'sim/operation/pause_on'), observations[0][1])
        self.assertTrue(report['pauseConfirmed'])
        self.assertTrue(report['readWorkersClosed'])

    def test_runtime_300_second_cap_refuses_read_only_and_zero_defaults_to_300(self):
        data, _, endpoint = recovery_fixture()
        clock = FastClock()
        api = recovery_api(clock, data, endpoint)
        code, report, _ = self.run_fixture(api, clock, data, seconds=300.01)
        self.assertEqual(code, 1)
        self.assert_no_control(api, report)
        # Fail one active read immediately so the default cap can be observed
        # without running hundreds of synthetic steps.
        clock = FastClock()
        api = recovery_api(clock, data, endpoint)
        command = api.command
        def interrupted(name, *args, **kwargs):
            command(name, *args, **kwargs)
            if name == 'sim/operation/pause_off':
                raise KeyboardInterrupt('Synthetic interruption')
        api.command = interrupted
        code, report, _ = self.run_fixture(api, clock, data, seconds=0)
        self.assertEqual(code, 1)
        self.assertEqual(report['durationLimitSeconds'], 300.)
        self.assertTrue(report['pauseConfirmed'])

    def test_invalid_recovery_cli_refuses_before_constructing_native_api(self):
        paired = ['--sustain', '--recover-from-guard', '--resume-acquisition', 'source.json', '--resume-sha256', '0'*64]
        for argv in [['--recover-from-guard'], ['--sustain', '--recover-from-guard'],
                [*paired, '--seconds', '300.01'], [*paired, '--seconds', 'nan'],
                [*paired, '--start'], [*paired, '--route'], [*paired, '--collective-assist']]:
            with self.subTest(argv=argv), contextlib.redirect_stderr(io.StringIO()), patch.object(demo, 'NativeApi') as api:
                with self.assertRaises(SystemExit) as exc:
                    demo.main(argv)
                self.assertEqual(exc.exception.code, 2)
                api.assert_not_called()


if __name__ == '__main__':
    unittest.main()
