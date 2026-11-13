"""Pure stdlib regressions; no network or simulator mutations."""
import argparse
import base64
import contextlib
import copy
import io
import json
import math
from pathlib import Path
import sys
import tempfile
import unittest
from unittest.mock import patch

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
import flight_demo as demo
import start_s76
import verify_flying


class Clock:
    def __init__(self):
        self.now = 0.
    def monotonic(self):
        return self.now
    def time(self):
        return 1000. + self.now
    def sleep(self, seconds):
        self.now += seconds


def native_values():
    values = {name: 0 for name in demo.NAMES}
    values.update({
        demo.P + 'latitude': 47.60, demo.P + 'longitude': -122.50,
        demo.P + 'elevation': 2500., demo.P + 'y_agl': 2000.,
        demo.P + 'indicated_airspeed': 100., demo.P + 'mag_psi': 344.,
        'sim/cockpit2/autopilot/servos_on': 1, 'sim/cockpit/autopilot/autopilot_state': 16386,
        'sim/cockpit2/engine/indicators/N2_percent': [100., 100.],
        'sim/flightmodel2/engines/engine_is_burning_fuel': [1, 1],
        'sim/cockpit2/engine/actuators/prop_ratio_all': .5,
        demo.PROP_HANDLES: [.5] * 16,
        'sim/flightmodel/weight/m_fuel_total': 300., 'sim/time/total_flight_time_sec': 100.})
    return values


class Api:
    def __init__(self, clock):
        self.clock, self.values, self.actions = clock, native_values(), []
        self.failure = None
    def read(self, name, timeout=None):
        self.actions.append(('read', name))
        if self.failure == name:
            raise OSError('native API unavailable')
        if name == 'sim/aircraft/view/acf_relative_path':
            return base64.b64encode(demo.AIRCRAFT.encode()).decode()
        if name == 'sim/aircraft/view/acf_ICAO':
            return base64.b64encode(b'S76').decode()
        if name in ('sim/cockpit2/gauges/indicators/altitude_ft_pilot', 'sim/cockpit2/autopilot/altitude_dial_ft'):
            return 2500 / .3048
        if name == 'sim/cockpit2/autopilot/heading_dial_deg_mag_pilot':
            return 344.
        if name == 'sim/cockpit2/engine/actuators/prop_ratio_all':
            return self.values[name]
        if name == 'sim/time/total_flight_time_sec':
            return 100 + self.clock.now
        return copy.deepcopy(self.values[name])
    def write(self, name, value):
        self.actions.append(('write', name, value))
        # Native autopilot_state dispatches toggle commands, not assignment.
        # In particular, writing zero must leave the retained modes untouched.
        if name == 'sim/cockpit/autopilot/autopilot_state' and value == 0:
            return
        self.values[name] = copy.deepcopy(value)
        if name in ('sim/cockpit2/autopilot/flight_director_mode', 'sim/cockpit2/autopilot/flight_director2_mode') and value == 0:
            self.values['sim/cockpit2/autopilot/servos_on'] = 0
    def read_index(self, name, index, timeout=None):
        self.actions.append(('read_index', name, index))
        return self.read(name, timeout)[index]
    def write_index(self, name, index, value):
        self.actions.append(('write_index', name, index, value))
        self.values[name][index] = value
    def command(self, name, duration=.1, timeout=None):
        self.actions.append(('command', name))
        if name == 'sim/operation/pause_on':
            self.values['sim/time/paused'] = 1
        if name == 'sim/operation/pause_off':
            self.values['sim/time/paused'] = 0
        if name == 'sim/autopilot/servos_off_any':
            self.values['sim/cockpit2/autopilot/servos_on'] = 0
        if name == 'sim/autopilot/servos_on':
            self.values['sim/cockpit2/autopilot/servos_on'] = 1
        if name == 'sim/autopilot/autothrottle_off':
            self.values['sim/cockpit2/autopilot/autothrottle_enabled'] = 0
        if name == 'sim/autopilot/heading':
            self.values['sim/cockpit/autopilot/autopilot_state'] &= ~4
            self.values['sim/cockpit/autopilot/autopilot_state'] |= 2
        if name == 'sim/autopilot/altitude_hold':
            self.values['sim/cockpit/autopilot/autopilot_state'] &= ~(128 | 8)
            self.values['sim/cockpit/autopilot/autopilot_state'] |= 16384
        if name == 'sim/autopilot/attitude':
            self.values['sim/cockpit/autopilot/autopilot_state'] &= ~(2 | 8 | 16384)
            self.values['sim/cockpit/autopilot/autopilot_state'] |= 4 | 128
        if name == 'sim/autopilot/level_change':
            self.values['sim/cockpit/autopilot/autopilot_state'] &= ~(128 | 16384)
            self.values['sim/cockpit/autopilot/autopilot_state'] |= 8
            self.values['sim/cockpit2/autopilot/airspeed_dial_kts_mach'] = self.values[demo.P + 'indicated_airspeed']
        if name == 'sim/autopilot/wing_leveler':
            self.values['sim/cockpit/autopilot/autopilot_state'] &= ~2
            self.values['sim/cockpit/autopilot/autopilot_state'] |= 4
        if name == 'sim/autopilot/pitch_sync':
            self.values['sim/cockpit/autopilot/autopilot_state'] &= ~16384
            self.values['sim/cockpit/autopilot/autopilot_state'] |= 128
    def request(self, path, *args):
        self.actions.append(('request', path))
        if self.failure == path:
            raise OSError('flight load failed')


def options(path, **kwargs):
    args = argparse.Namespace(start=False, route=False, collective_assist=False, seconds=1.2,
        state_dir=Path(path), max_sample_age=3)
    for key, value in kwargs.items():
        setattr(args, key, value)
    return args


def report(clock, sequence=None, position=None):
    sequence = int(round(clock.now * 2)) + 1 if sequence is None else sequence
    values = native_values()
    values['sim/time/total_flight_time_sec'] += clock.now
    if position:
        values[demo.P + 'latitude'], values[demo.P + 'longitude'] = position
    return {'utc': clock.time(), 'sequence': sequence, 'sampleSequence': sequence,
        'sampleStartedUtc': clock.time(), 'sampleCompletedUtc': clock.time(),
        'sampleAgeSeconds': 0., 'status': 'running', 'values': values}


class ReportPath:
    def __init__(self, factory):
        self.factory = factory
    def read_text(self):
        result = self.factory()
        if isinstance(result, Exception):
            raise result
        return json.dumps(result)


class ReliabilityTests(unittest.TestCase):
    def run_controller(self, api, clock, **kwargs):
        with tempfile.TemporaryDirectory() as directory, contextlib.redirect_stdout(io.StringIO()):
            status = demo.run_demo(options(directory, **kwargs), api, clock)
            output = json.loads((Path(directory) / 'latest.json').read_text())
            return status, output

    def test_true_to_magnetic_heading_wrap(self):
        self.assertEqual(demo.magnetic_heading(0, 0, 344), 344)
        self.assertEqual(demo.magnetic_heading(90, 0, 344), 74)
        self.assertEqual(demo.magnetic_heading(0, 359, 15), 16)

    def test_waypoint_advance_and_outside_route_guard(self):
        route = demo.WaypointRoute(demo.ROUTE[0])
        heading, info = route.target(demo.ROUTE[0], 0, 344)
        self.assertEqual(info['waypointIndex'], 1)
        self.assertTrue(0 <= heading < 360)
        with self.assertRaisesRegex(demo.DemoError, 'outside bounded'):
            route.target((49.98, -122.25), 0, 344)

    def test_route_heading_wrap_rate_and_lead_limits(self):
        self.assertAlmostEqual(demo.limited_route_heading(359, 359, 3, 1), 359.5)
        self.assertAlmostEqual(demo.limited_route_heading(1, 1, 359, 1), .5)
        self.assertEqual(demo.limited_route_heading(0, 0, 90, 20), 5)
        command = 359.
        for _ in range(40):
            previous = command
            command = demo.limited_route_heading(command, previous, 90, .5)
            self.assertLessEqual(abs(demo.angle_delta(command, previous)), .250001)
            self.assertLessEqual(abs(demo.angle_delta(command, previous)), 5)
        with self.assertRaisesRegex(demo.DemoError, 'discontinuity'):
            demo.limited_route_heading(0, 90, 180, 1)

    def test_route_recovery_uses_forward_leg_not_vertex_behind(self):
        position = (47.63, -122.50)
        self.assertEqual(min(range(4), key=lambda i: demo.distance_bearing(position, demo.ROUTE[i])[0]), 0)
        route = demo.WaypointRoute(position, current_heading=0)
        _, info = route.target(position, 0, 344)
        self.assertEqual(info['waypointIndex'], 1)
        self.assertEqual(info['progressSource'], 'nearest forward segment')

    def test_recent_route_checkpoint_is_validated_and_restores_progress(self):
        clock = Clock()
        position = demo.ROUTE_CENTER
        output = report(clock, position=position)
        output.update(status='completed', routeEnabled=True,
            route={'waypointIndex': 2, 'waypoint': demo.ROUTE[2]})
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / 'latest.json'
            demo.atomic_report(path, output)
            index = demo.recent_route_index(path, position, clock.time())
            self.assertEqual(index, 2)
            route = demo.WaypointRoute(position, 0, index)
            self.assertEqual(route.index, 2)
            self.assertEqual(route.progress_source, 'recent report')
            self.assertIsNone(demo.recent_route_index(path, position, clock.time() + 61))
            self.assertIsNone(demo.recent_route_index(path, (47.6, -122.5), clock.time()))
            output['route']['waypoint'] = demo.ROUTE[0]
            demo.atomic_report(path, output)
            self.assertIsNone(demo.recent_route_index(path, position, clock.time()))

    def test_route_handoff_seeds_current_heading_and_limits_all_later_writes(self):
        class OldDialApi(Api):
            def __init__(self, clock):
                super().__init__(clock)
                self.heading_writes = []
            def read(self, name, timeout=None):
                if name == 'sim/cockpit2/autopilot/heading_dial_deg_mag_pilot':
                    return 164.
                return super().read(name, timeout)
            def write(self, name, value):
                super().write(name, value)
                if name == 'sim/cockpit2/autopilot/heading_dial_deg_mag_pilot':
                    self.heading_writes.append((self.clock.now, value))
        clock = Clock()
        api = OldDialApi(clock)
        api.values[demo.P + 'latitude'] = 47.63
        status, output = self.run_controller(api, clock, route=True, seconds=3)
        self.assertEqual(status, 0)
        self.assertEqual(api.heading_writes[0][1], 344.)
        self.assertEqual(output['route']['waypointIndex'], 1)
        for (t0, h0), (t1, h1) in zip(api.heading_writes, api.heading_writes[1:]):
            self.assertLessEqual(abs(demo.angle_delta(h1, h0)), .5 * (t1 - t0) + .000001)
            self.assertLessEqual(abs(demo.angle_delta(h1, 344)), 5.)

    def test_nonfinite_and_malformed_telemetry_rejected(self):
        for key, bad in [(demo.P + 'phi', math.nan), (demo.P + 'y_agl', math.inf),
                         ('sim/cockpit2/engine/indicators/N2_percent', [100]),
                         (demo.P + 'latitude', '47.6'), (demo.P + 'theta', 10 ** 500)]:
            with self.subTest(key=key, bad=repr(bad)):
                values = native_values()
                values[key] = bad
                with self.assertRaises(demo.DemoError):
                    demo.safety_reasons(values)

    def test_guard_publishes_atomic_fresh_terminal_report(self):
        clock = Clock()
        api = Api(clock)
        api.values[demo.P + 'y_agl'] = 99
        status, output = self.run_controller(api, clock)
        self.assertEqual(status, 0)
        self.assertEqual(output['status'], 'stopped')
        self.assertIn('clearance', output['reason'])
        self.assertTrue(output['pauseConfirmed'])
        self.assertEqual(output['utc'], clock.time())
        self.assertEqual(output['sampleSequence'], 1)

    def test_outside_route_is_safe_stop_without_service_restart(self):
        clock = Clock()
        api = Api(clock)
        api.values[demo.P + 'latitude'] = 49.98
        status, output = self.run_controller(api, clock, route=True)
        self.assertEqual(status, 0)
        self.assertEqual(output['status'], 'stopped')
        self.assertIn('outside bounded', output['reason'])
        self.assertTrue(output['pauseConfirmed'])

    def test_invalid_telemetry_publishes_json_without_nan_and_stops_writes(self):
        clock = Clock()
        api = Api(clock)
        api.values[demo.P + 'phi'] = math.nan
        status, output = self.run_controller(api, clock)
        self.assertEqual(status, 1)
        self.assertEqual(output['status'], 'error')
        self.assertIsNone(output['values'][demo.P + 'phi'])
        self.assertFalse(any(a[0] in ('write', 'write_index') for a in api.actions))
        self.assertTrue(output['pauseConfirmed'])

    def test_sample_timeout_is_terminal_and_fresh(self):
        clock = Clock()
        api = Api(clock)
        api.failure = demo.P + 'y_agl'
        status, output = self.run_controller(api, clock)
        self.assertEqual(status, 1)
        self.assertEqual(output['status'], 'error')
        self.assertIn('native API unavailable', output['reason'])
        self.assertEqual(output['values'], {})
        self.assertTrue(output['pauseConfirmed'])

    def test_unwritable_report_still_pauses_and_prints_terminal_json(self):
        clock = Clock()
        api = Api(clock)
        output = io.StringIO()
        with patch.object(demo, 'atomic_report', side_effect=PermissionError('state unavailable')), contextlib.redirect_stdout(output):
            status = demo.run_demo(options('/unused'), api, clock)
        self.assertEqual(status, 1)
        final = json.loads(output.getvalue())
        self.assertTrue(final['pauseConfirmed'])
        self.assertIn('state unavailable', final['reportError'])

    def test_completion_report_and_no_physics_override(self):
        clock = Clock()
        api = Api(clock)
        status, output = self.run_controller(api, clock)
        self.assertEqual(status, 0)
        self.assertEqual(output['status'], 'completed')
        self.assertGreater(output['sequence'], 1)
        self.assertTrue(all('override' not in a[1] for a in api.actions))

    def test_route_is_opt_in_and_manual_attach_keeps_heading(self):
        for enabled in (False, True):
            clock = Clock()
            api = Api(clock)
            status, output = self.run_controller(api, clock, route=enabled)
            writes = [a for a in api.actions if a[:2] == ('write', 'sim/cockpit2/autopilot/heading_dial_deg_mag_pilot')]
            self.assertEqual(bool(writes), enabled)
            self.assertEqual(output['routeEnabled'], enabled)
            self.assertEqual(status, 0)

    def test_only_missing_autopilot_mode_is_armed(self):
        clock = Clock()
        api = Api(clock)
        api.values['sim/cockpit/autopilot/autopilot_state'] = 2
        self.run_controller(api, clock)
        commands = [a[1] for a in api.actions if a[0] == 'command']
        self.assertIn('sim/autopilot/altitude_hold', commands)
        self.assertNotIn('sim/autopilot/heading', commands)

    def test_preflight_mouse_refusal_does_not_pause_current_flight(self):
        clock = Clock()
        api = Api(clock)
        api.values['sim/joystick/mouse_is_joystick'] = 1
        status, output = self.run_controller(api, clock, start=True)
        self.assertEqual(status, 1)
        self.assertFalse(output['pauseRequested'])
        self.assertFalse(any(a[0] in ('command', 'request', 'write', 'write_index') for a in api.actions))

    def test_startup_load_failure_is_paused_and_reported(self):
        clock = Clock()
        api = Api(clock)
        api.failure = '/flight'
        status, output = self.run_controller(api, clock, start=True)
        self.assertEqual(status, 1)
        self.assertTrue(output['pauseConfirmed'])

    def test_air_start_resets_retained_ap_and_reapplies_controls_after_quick_start(self):
        class OverwriteApi(Api):
            def request(self, path, *args):
                super().request(path, *args)
                if path == '/flight':
                    self.values['sim/cockpit2/autopilot/servos_on'] = 1
                    self.values['sim/cockpit/autopilot/autopilot_state'] = 16418
            def command(self, name, duration=.1, timeout=None):
                super().command(name, duration, timeout)
                if name == 'sim/operation/quick_start':
                    self.values['sim/cockpit2/engine/actuators/throttle_ratio_all'] = .4
                    self.values['sim/cockpit2/engine/actuators/governor_on'] = [0] * 16
                    self.values[demo.PROP_HANDLES][0] = 1.
                    self.values[demo.PROP_HANDLES][1] = .61
        clock = Clock()
        api = OverwriteApi(clock)
        demo.initialize_flight(api, True, clock)
        flight = api.actions.index(('request', '/flight'))
        quick_start = api.actions.index(('command', 'sim/operation/quick_start'))
        clear_mode = ('write', 'sim/cockpit2/autopilot/flight_director_mode', 0)
        self.assertIn(clear_mode, api.actions[:flight])
        self.assertIn(clear_mode, api.actions[flight:quick_start])
        for name, expected in [('throttle_ratio_all', 1), ('governor_on', [1, 1] + [0] * 14)]:
            key = 'sim/cockpit2/engine/actuators/' + name
            self.assertIn(('write', key, expected), api.actions[quick_start:])
            self.assertEqual(api.values[key], expected)
            self.assertFalse(any(a[:2] == ('write', key) for a in api.actions[:quick_start]))
        self.assertIn(('write_index', demo.PROP_HANDLES, 0, .5), api.actions[quick_start:])
        self.assertEqual(api.values[demo.PROP_HANDLES][:2], [.5, .61])
        self.assertFalse(any(a[0] == 'write_index' for a in api.actions[:quick_start]))
        self.assertEqual(api.values['sim/cockpit/autopilot/autopilot_state'] & (2 | 16384), 16386)
        self.assertEqual(api.values['sim/cockpit2/autopilot/servos_on'], 1)
        self.assertFalse(any(a[:2] == ('write', 'sim/cockpit/autopilot/autopilot_state') for a in api.actions))
        arm = api.actions.index(('command', 'sim/autopilot/servos_on'))
        self.assertIn(('command', 'sim/autopilot/attitude'), api.actions[quick_start:arm])
        self.assertIn(('command', 'sim/autopilot/heading'), api.actions[arm:])
        self.assertIn(('command', 'sim/autopilot/altitude_hold'), api.actions[arm:])
        ias = api.actions.index(('command', 'sim/autopilot/level_change'))
        self.assertFalse(any(a[0] == 'write_index' for a in api.actions[ias:]))

    def test_stale_zero_ias_prevents_native_hold_selection(self):
        clock = Clock()
        api = Api(clock)
        api.values[demo.P + 'indicated_airspeed'] = 0
        status, output = self.run_controller(api, clock, start=True, route=True)
        self.assertEqual(status, 1)
        self.assertIn('fresh airborne IAS', output['reason'])
        self.assertTrue(output['pauseConfirmed'])
        self.assertFalse(any(a == ('command', 'sim/autopilot/level_change') for a in api.actions))
        self.assertFalse(any(a == ('command', 'sim/autopilot/altitude_hold') for a in api.actions))

    def test_stale_native_clock_prevents_native_hold_selection(self):
        class FrozenClockApi(Api):
            def read(self, name, timeout=None):
                if name == 'sim/time/total_flight_time_sec':
                    return 100
                return super().read(name, timeout)
        clock = Clock()
        api = FrozenClockApi(clock)
        status, output = self.run_controller(api, clock, start=True, route=True)
        self.assertEqual(status, 1)
        self.assertIn('advancing native clock', output['reason'])
        self.assertNotIn(('command', 'sim/autopilot/level_change'), api.actions)

    def test_native_collective_is_observed_without_controller_writes(self):
        clock = Clock()
        api = Api(clock)
        api.values[demo.PROP_HANDLES][0] = .431
        status, output = self.run_controller(api, clock)
        self.assertEqual(status, 0)
        self.assertEqual(output['collective'], .431)
        self.assertFalse(any(a[0] == 'write_index' for a in api.actions))

    def test_assist_is_explicit_bounded_and_never_enables_autothrottle(self):
        clock = Clock()
        api = Api(clock)
        api.values[demo.P + 'indicated_airspeed'] = 90
        status, output = self.run_controller(api, clock, collective_assist=True)
        writes = [a[3] for a in api.actions if a[:3] == ('write_index', demo.PROP_HANDLES, 0)]
        self.assertEqual(status, 0)
        self.assertTrue(output['collectiveAssist'])
        self.assertIn('rate-limited collective', output['controller'])
        self.assertGreater(writes[-1], writes[0])
        self.assertTrue(all(.15 <= x <= .85 for x in writes))
        self.assertTrue(all(abs(b-a) <= .00900001 for a,b in zip(writes,writes[1:])))
        self.assertFalse(any('autothrottle' in a[1] for a in api.actions if a[0] in ('write','command')))

    def test_assist_stops_before_control_writes_if_autothrottle_is_not_zero(self):
        for enabled in (-1, 1, 2, 3):
            clock = Clock()
            api = Api(clock)
            api.values['sim/cockpit2/autopilot/autothrottle_enabled'] = enabled
            status, output = self.run_controller(api, clock, collective_assist=True)
            self.assertEqual(status, 0)
            self.assertEqual(output['status'], 'stopped')
            self.assertIn('AT must remain off', output['reason'])
            self.assertFalse(any(a[0] in ('write', 'write_index') for a in api.actions))
            self.assertTrue(output['pauseConfirmed'])

    def test_assist_only_writes_during_unpaused_safe_flight(self):
        for change in ({'sim/time/paused': 1}, {demo.P + 'y_agl': 99},
                       {'sim/cockpit2/engine/indicators/N2_percent': [84, 100]}):
            clock = Clock()
            api = Api(clock)
            api.values.update(change)
            self.run_controller(api, clock, collective_assist=True)
            self.assertFalse(any(a[0] == 'write_index' for a in api.actions))

    def test_collective_assist_rate_and_saturation(self):
        self.assertAlmostEqual(demo.assisted_collective(.5, 0, 10), .509)
        self.assertAlmostEqual(demo.assisted_collective(.5, 200, 10), .491)
        self.assertEqual(demo.assisted_collective(.85, 0, 1), .85)
        self.assertEqual(demo.assisted_collective(.15, 200, 1), .15)
        with self.assertRaises(demo.DemoError):
            demo.assisted_collective(.01, 100, .1)

    def test_explicit_start_disables_at_but_assist_uses_only_hdg_alt(self):
        clock = Clock()
        api = Api(clock)
        api.values['sim/cockpit2/autopilot/autothrottle_enabled'] = 1
        status, output = self.run_controller(api, clock, start=True, route=True, collective_assist=True)
        self.assertEqual(status, 0)
        commands = [a[1] for a in api.actions if a[0] == 'command']
        self.assertIn('sim/autopilot/autothrottle_off', commands)
        self.assertNotIn('sim/autopilot/autothrottle_on', commands)
        self.assertNotIn('sim/autopilot/autothrottle_arm', commands)
        self.assertNotIn('sim/autopilot/level_change', commands)
        self.assertEqual(api.values['sim/cockpit2/autopilot/autothrottle_enabled'], 0)

    def test_ias_target_is_written_after_s76_capture_wrapper(self):
        clock = Clock()
        api = Api(clock)
        api.values[demo.P + 'indicated_airspeed'] = 105
        demo.select_fresh_holds(api, 344, 8200)
        capture = api.actions.index(('command', 'sim/autopilot/level_change'))
        self.assertIn(('write', 'sim/cockpit2/autopilot/airspeed_dial_kts_mach', 100.), api.actions[capture:])
        self.assertEqual(api.values['sim/cockpit2/autopilot/airspeed_dial_kts_mach'], 100.)

    def test_failed_ap_disengagement_aborts_before_flight_load(self):
        class StuckApi(Api):
            def command(self, name, duration=.1, timeout=None):
                super().command(name, duration, timeout)
                if name == 'sim/autopilot/servos_off_any':
                    self.values['sim/cockpit2/autopilot/servos_on'] = 1
            def write(self, name, value):
                if name in ('sim/cockpit2/autopilot/flight_director_mode', 'sim/cockpit2/autopilot/flight_director2_mode'):
                    return
                super().write(name, value)
        api = StuckApi(Clock())
        with self.assertRaisesRegex(demo.DemoError, 'did not disengage'):
            demo.initialize_flight(api, True, api.clock)
        self.assertNotIn(('request', '/flight'), api.actions)

    def test_atomic_report_failure_preserves_previous_json(self):
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / 'latest.json'
            demo.atomic_report(path, {'status': 'running'})
            with self.assertRaises(ValueError):
                demo.atomic_report(path, {'bad': math.nan})
            self.assertEqual(json.loads(path.read_text()), {'status': 'running'})
            self.assertEqual(list(path.parent.glob('.latest-*')), [])

    def test_empty_or_corrupt_report_fails_within_startup_grace(self):
        for failure in (FileNotFoundError('no report'), json.JSONDecodeError('bad', '', 0)):
            clock = Clock()
            result = verify_flying.verify(120, ReportPath(lambda: failure), clock, startup_grace=1)
            self.assertFalse(result['passed'])
            self.assertEqual(result['samples'], 0)
            self.assertLessEqual(result['seconds'], 1.5)

    def test_frozen_single_sample_cannot_pass(self):
        clock = Clock()
        frozen = report(clock)
        result = verify_flying.verify(2, ReportPath(lambda: frozen), clock)
        self.assertFalse(result['passed'])
        self.assertEqual(result['samples'], 1)

    def test_future_sample_fails_boundedly(self):
        clock = Clock()
        future = report(clock)
        future.update(utc=100000, sampleStartedUtc=100000, sampleCompletedUtc=100000)
        result = verify_flying.verify(120, ReportPath(lambda: future), clock, startup_grace=1)
        self.assertFalse(result['passed'])
        self.assertTrue(any('future' in e for e in result['errors']))
        self.assertLessEqual(result['seconds'], 1.5)

    def test_schema_nonfinite_and_terminal_reports_fail(self):
        for change in ({'values': {}}, {'status': 'stopped'}, {'utc': math.nan}):
            clock = Clock()
            invalid = report(clock)
            invalid.update(change)
            result = verify_flying.verify(1, ReportPath(lambda: invalid), clock, startup_grace=0)
            self.assertFalse(result['passed'])

    def test_loop_route_uses_cumulative_distance(self):
        clock = Clock()
        positions = [(47.6, -122.5), (47.6002, -122.5),
                     (47.6002, -122.4998), (47.6, -122.5)]
        path = ReportPath(lambda: report(clock, position=positions[min(3, int(round(clock.now * 2)))]))
        result = verify_flying.verify(2, path, clock)
        self.assertTrue(result['passed'], result)
        self.assertGreater(result['distance_m'], 50)
        self.assertLess(result['endpoint_distance_m'], 1)

    def test_simulation_clock_freeze_cannot_pass(self):
        clock = Clock()
        def frozen_clock():
            output = report(clock, position=(47.6 + clock.now * .0004, -122.5))
            output['values']['sim/time/total_flight_time_sec'] = 100
            return output
        result = verify_flying.verify(2, ReportPath(frozen_clock), clock)
        self.assertFalse(result['passed'])
        self.assertTrue(any('simulation clock' in e for e in result['errors']))

    def test_invalid_verifier_duration_returns_failure_json(self):
        output = io.StringIO()
        with contextlib.redirect_stdout(output):
            self.assertEqual(verify_flying.main(['--seconds', 'nan']), 1)
        self.assertFalse(json.loads(output.getvalue())['passed'])

    def test_readiness_timeout_is_bounded(self):
        clock = Clock()
        with self.assertRaisesRegex(demo.DemoError, 'Timed out'):
            demo.wait_until(lambda: False, 1, clock)
        self.assertEqual(clock.now, 1)

    def test_parked_reset_failure_pauses_and_mouse_refusal_does_not(self):
        clock = Clock()
        api = Api(clock)
        api.failure = '/flight'
        with self.assertRaises(OSError):
            start_s76.reset_parked(api, clock)
        self.assertEqual(api.values['sim/time/paused'], 1)
        api = Api(clock)
        api.values['sim/joystick/mouse_is_joystick'] = 1
        with self.assertRaises(demo.DemoError):
            start_s76.reset_parked(api, clock)
        self.assertFalse(any(a[0] == 'command' for a in api.actions))

    def test_parked_reset_confirms_ground_before_unpause(self):
        clock = Clock()
        api = Api(clock)
        api.values.update({demo.P + 'latitude': 47.45, demo.P + 'longitude': -122.31,
                           demo.P + 'y_agl': .1, demo.P + 'indicated_airspeed': 0})
        with contextlib.redirect_stdout(io.StringIO()):
            start_s76.reset_parked(api, clock)
        unpause = api.actions.index(('command', 'sim/operation/pause_off'))
        self.assertGreaterEqual(api.actions[:unpause].count(('read', 'sim/aircraft/view/acf_relative_path')), 2)
        self.assertIn(('write_index', demo.PROP_HANDLES, 0, 0.), api.actions[:unpause])
        self.assertEqual([a for a in api.actions if a == ('request', '/flight')], [('request', '/flight')])
        self.assertGreaterEqual(clock.now, 15)

    def test_parked_controls_reapplied_after_quick_start(self):
        class OverwriteApi(Api):
            def command(self, name, duration=.1, timeout=None):
                super().command(name, duration, timeout)
                if name == 'sim/operation/quick_start':
                    self.values['sim/cockpit2/controls/parking_brake_ratio'] = 0.
                    self.values[demo.PROP_HANDLES][0] = 1.
                    self.values[demo.PROP_HANDLES][1] = .61
                    self.values['sim/cockpit2/engine/actuators/throttle_ratio_all'] = .4
        clock = Clock()
        api = OverwriteApi(clock)
        api.values.update({demo.P + 'latitude': 47.45, demo.P + 'longitude': -122.31,
                           demo.P + 'y_agl': .1, demo.P + 'indicated_airspeed': 0})
        with contextlib.redirect_stdout(io.StringIO()):
            start_s76.reset_parked(api, clock)
        self.assertEqual(api.values['sim/cockpit2/controls/parking_brake_ratio'], 1.)
        self.assertEqual(api.values[demo.PROP_HANDLES][:2], [0., .61])
        self.assertEqual(api.values['sim/cockpit2/engine/actuators/throttle_ratio_all'], 1.)
        self.assertEqual(api.values['sim/cockpit2/autopilot/servos_on'], 0)
        self.assertNotEqual(api.values['sim/cockpit/autopilot/autopilot_state'], 0)
        self.assertEqual(api.values['sim/cockpit2/autopilot/flight_director_mode'], 0)
        self.assertEqual(api.values['sim/cockpit2/autopilot/flight_director2_mode'], 0)

    def test_fd_off_accepts_retained_mode_bits_and_zero_state_write_is_noop(self):
        api = Api(Clock())
        api.values['sim/cockpit/autopilot/autopilot_state'] = 16418
        api.write('sim/cockpit/autopilot/autopilot_state', 0)
        self.assertEqual(api.values['sim/cockpit/autopilot/autopilot_state'], 16418)
        demo.reset_autopilot(api)
        self.assertEqual(api.values['sim/cockpit/autopilot/autopilot_state'], 16418)
        self.assertEqual(api.values['sim/cockpit2/autopilot/servos_on'], 0)


if __name__ == '__main__':
    unittest.main()
