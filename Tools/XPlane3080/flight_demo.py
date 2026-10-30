#!/usr/bin/env python3
"""Real-physics S-76 demo: native HDG/ALT; optional route and collective assist."""
import argparse
import base64
import json
import math
import os
from pathlib import Path
import signal
import sys
import tempfile
import time
from urllib.error import HTTPError
from urllib.parse import urlencode
from urllib.request import Request, urlopen

B = 'http://127.0.0.1:8086/api/v3'
AIRCRAFT = 'Aircraft/Laminar Research/Sikorsky S-76/S-76C.acf'
P = 'sim/flightmodel/position/'
# Stock S-76: main collective is handle0; handle1 is the independent tail control.
# prop_ratio_all combines handles and must never drive collective feedback/writes.
PROP_HANDLES = 'sim/cockpit2/engine/actuators/prop_ratio'
MAIN_COLLECTIVE_INDEX = 0
ROUTE = ((47.60, -122.50), (47.75, -122.60), (47.90, -122.50), (47.75, -122.40))
ROUTE_CENTER = (47.75, -122.50)
ROUTE_LIMIT_METERS = 35000
WAYPOINT_RADIUS_METERS = 1500
ROUTE_HEADING_RATE = .5
ROUTE_HEADING_LEAD = 5.
NAMES = tuple(P + n for n in ('latitude', 'longitude', 'elevation', 'y_agl',
    'indicated_airspeed', 'theta', 'phi', 'psi', 'mag_psi', 'vh_ind')) + (
    'sim/flightmodel2/misc/has_crashed', 'sim/cockpit2/autopilot/servos_on',
    'sim/cockpit/autopilot/autopilot_state', 'sim/time/paused',
    'sim/joystick/mouse_is_joystick', 'sim/cockpit2/engine/indicators/N2_percent',
    'sim/flightmodel2/engines/engine_is_burning_fuel',
    'sim/flightmodel/weight/m_fuel_total', 'sim/operation/failures/enable_random_failures',
    'sim/time/total_flight_time_sec', 'sim/cockpit2/engine/actuators/prop_ratio_all',
    'sim/cockpit2/autopilot/autothrottle_enabled')


class DemoError(RuntimeError):
    pass


class NativeApi:
    """Never retries an uncertain control mutation."""
    def __init__(self, base=B, timeout=5):
        self.base, self.timeout, self.ids = base.rstrip('/'), timeout, {}

    def request(self, path, payload=None, method=None, timeout=None):
        data = None if payload is None else json.dumps(payload, allow_nan=False).encode()
        req = Request(self.base + path, data=data,
            method=method or ('GET' if data is None else 'PATCH'),
            headers={'Content-Type': 'application/json'})
        with urlopen(req, timeout=self.timeout if timeout is None else timeout) as response:
            body = response.read()
            return json.loads(body) if body else None

    def identifier(self, name, timeout=None):
        if name not in self.ids:
            rows = self.request('/datarefs?' + urlencode({'filter[name]': name}), timeout=timeout)['data']
            match = next((r['id'] for r in rows if r['name'] == name), None)
            if match is None:
                raise DemoError('Native dataref unavailable: ' + name)
            self.ids[name] = match
        return self.ids[name]

    def read(self, name, timeout=None):
        try:
            return self.request('/datarefs/' + str(self.identifier(name, timeout)) + '/value', timeout=timeout)['data']
        except HTTPError as error:
            if error.code != 404:
                raise
            self.ids.pop(name, None)
            return self.request('/datarefs/' + str(self.identifier(name, timeout)) + '/value', timeout=timeout)['data']

    def write(self, name, value):
        self.request('/datarefs/' + str(self.identifier(name)) + '/value', {'data': value})

    def read_index(self, name, index, timeout=None):
        if type(index) is not int or index < 0:
            raise DemoError('Invalid native array index')
        def read_value():
            path = '/datarefs/' + str(self.identifier(name, timeout)) + '/value?' + urlencode({'index': index})
            return self.request(path, timeout=timeout)['data']
        try:
            data = read_value()
        except HTTPError as error:
            if error.code != 404:
                raise
            self.ids.pop(name, None)
            data = read_value()
        # X-Plane REST returns a one-element ARRAY even for one indexed member.
        if not isinstance(data, list) or len(data) != 1 or type(data[0]) not in (int, float):
            raise DemoError('Indexed native read must return one numeric element')
        return number(data[0], 'indexed ' + name)

    def write_index(self, name, index, value):
        if type(index) is not int or index < 0 or type(value) not in (int, float):
            raise DemoError('Invalid indexed native write')
        value = number(value, 'indexed native write')
        path = '/datarefs/' + str(self.identifier(name)) + '/value?' + urlencode({'index': index})
        # Never read/replace the whole array, or retry an uncertain mutation.
        self.request(path, {'data': value}, 'PATCH')

    def command(self, name, duration=.1, timeout=None):
        rows = self.request('/commands?' + urlencode({'filter[name]': name}), timeout=timeout)['data']
        match = next((r['id'] for r in rows if r['name'] == name), None)
        if match is None:
            raise DemoError('Native command unavailable: ' + name)
        self.request('/command/' + str(match) + '/activate', {'duration': duration}, 'POST', timeout)


def number(value, name):
    try:
        valid = isinstance(value, (int, float)) and math.isfinite(value)
    except OverflowError:
        valid = False
    if not valid:
        raise DemoError('Invalid/nonfinite native value: ' + name)
    return float(value)


def decoded(value):
    return base64.b64decode(value, validate=True).split(b'\0')[0].decode()


def aircraft_matches(api):
    return (decoded(api.read('sim/aircraft/view/acf_relative_path')) == AIRCRAFT
            and decoded(api.read('sim/aircraft/view/acf_ICAO')) == 'S76')


def distance_bearing(origin, target):
    lat1, lat2 = map(math.radians, (origin[0], target[0]))
    dlat, dlon = lat2 - lat1, math.radians(target[1] - origin[1])
    h = math.sin(dlat / 2) ** 2 + math.cos(lat1) * math.cos(lat2) * math.sin(dlon / 2) ** 2
    distance = 2 * 6371000 * math.asin(math.sqrt(max(0, min(1, h))))
    bearing = math.degrees(math.atan2(math.sin(dlon) * math.cos(lat2),
        math.cos(lat1) * math.sin(lat2) - math.sin(lat1) * math.cos(lat2) * math.cos(dlon))) % 360
    return distance, bearing


def magnetic_heading(true_bearing, true_heading, magnetic):
    variation = (true_heading - magnetic + 180) % 360 - 180
    return (true_bearing - variation) % 360


def angle_delta(target, source):
    return (target - source + 180) % 360 - 180


def limited_route_heading(previous, current, desired, elapsed):
    for name, value in [('previous heading', previous), ('current heading', current),
                        ('desired heading', desired), ('heading timestep', elapsed)]:
        number(value, name)
    if elapsed < 0:
        raise DemoError('Negative route heading timestep')
    target = (current + max(-ROUTE_HEADING_LEAD, min(ROUTE_HEADING_LEAD, angle_delta(desired, current)))) % 360
    step = ROUTE_HEADING_RATE * elapsed
    command = (previous + max(-step, min(step, angle_delta(target, previous)))) % 360
    if abs(angle_delta(command, current)) > ROUTE_HEADING_LEAD + .000001:
        raise DemoError('Native heading discontinuity exceeds gentle route command bounds')
    return command


def recent_route_index(path, position, now):
    try:
        report = json.loads(path.read_text())
        route, values = report['route'], report['values']
        index = route['waypointIndex']
        if (report.get('routeEnabled') is not True or report.get('status') not in ('running', 'paused', 'completed')
                or type(index) is not int or not 0 <= index < len(ROUTE)
                or tuple(route['waypoint']) != ROUTE[index]
                or not 0 <= now - number(report['utc'], 'route checkpoint time') <= 60):
            return None
        previous = (number(values[P + 'latitude'], 'checkpoint latitude'),
                    number(values[P + 'longitude'], 'checkpoint longitude'))
        return index if distance_bearing(position, previous)[0] <= 2000 else None
    except (OSError, ValueError, TypeError, KeyError, DemoError):
        return None


class WaypointRoute:
    def __init__(self, position, current_heading=None, preferred_index=None):
        if type(preferred_index) is int and 0 <= preferred_index < len(ROUTE):
            self.index, self.progress_source = preferred_index, 'recent report'
            return
        # Locate the closest forward leg, not its closest vertex: a vertex just
        # behind the aircraft must not become a new 180-degree return target.
        scale = math.cos(math.radians(ROUTE_CENTER[0]))
        def xy(point):
            return ((point[1] - ROUTE_CENTER[1]) * scale, point[0] - ROUTE_CENTER[0])
        px, py = xy(position)
        candidates = []
        for index, target in enumerate(ROUTE):
            origin = ROUTE[(index - 1) % len(ROUTE)]
            ax, ay = xy(origin)
            bx, by = xy(target)
            dx, dy = bx - ax, by - ay
            fraction = max(0, min(1, ((px-ax)*dx + (py-ay)*dy) / (dx*dx + dy*dy)))
            distance_squared = (px-ax-fraction*dx)**2 + (py-ay-fraction*dy)**2
            difference = 0 if current_heading is None else abs(angle_delta(distance_bearing(origin, target)[1], current_heading))
            candidates.append((difference > 90, distance_squared, difference, index))
        self.index = min(candidates)[3]
        self.progress_source = 'nearest forward segment'

    def target(self, position, true_heading, magnetic):
        if distance_bearing(position, ROUTE_CENTER)[0] > ROUTE_LIMIT_METERS:
            raise DemoError('outside bounded Puget route; explicit --start required to reposition')
        distance, bearing = distance_bearing(position, ROUTE[self.index])
        if distance <= WAYPOINT_RADIUS_METERS:
            self.index = (self.index + 1) % len(ROUTE)
            distance, bearing = distance_bearing(position, ROUTE[self.index])
        return magnetic_heading(bearing, true_heading, magnetic), {
            'waypointIndex': self.index, 'waypoint': ROUTE[self.index], 'distanceMeters': distance,
            'progressSource': self.progress_source}


def atomic_report(path, report):
    path = Path(path)
    path.parent.mkdir(parents=True, exist_ok=True)
    temporary = None
    try:
        with tempfile.NamedTemporaryFile(mode='w', dir=path.parent, prefix='.latest-', delete=False) as output:
            temporary = output.name
            json.dump(report, output, indent=2, allow_nan=False)
            output.flush()
            os.fsync(output.fileno())
        os.replace(temporary, path)
    finally:
        if temporary and os.path.exists(temporary):
            os.unlink(temporary)


def safe_pause(api):
    try:
        api.command('sim/operation/pause_on', timeout=2)
        return {'pauseRequested': True, 'pauseConfirmed': api.read('sim/time/paused', timeout=2) == 1}
    except Exception as error:
        return {'pauseRequested': True, 'pauseConfirmed': False, 'pauseError': str(error)}


def wait_until(predicate, timeout, clock=time, description='native readiness'):
    deadline = clock.monotonic() + timeout
    stable, last_error = 0, ''
    while clock.monotonic() < deadline:
        try:
            stable = stable + 1 if predicate() else 0
            if stable >= 2:
                return
        except (OSError, ValueError, KeyError) as error:
            stable, last_error = 0, str(error)
        clock.sleep(.25)
    raise DemoError('Timed out waiting for ' + description + (': ' + last_error if last_error else ''))


def safety_reasons(values):
    for name in NAMES:
        value = values[name]
        if name.endswith(('N2_percent', 'engine_is_burning_fuel')):
            if not isinstance(value, list) or len(value) < 2:
                raise DemoError('Missing two engine values: ' + name)
            for item in value[:2]:
                number(item, name)
        else:
            number(value, name)
    if not -90 <= values[P + 'latitude'] <= 90 or not -180 <= values[P + 'longitude'] <= 180:
        raise DemoError('Native coordinates outside valid range')
    if values['sim/time/total_flight_time_sec'] < 0:
        raise DemoError('Negative native simulation clock')
    if not 0 <= values['sim/cockpit2/engine/actuators/prop_ratio_all'] <= 1:
        raise DemoError('Native aggregate prop ratio outside valid range')
    if 'mainCollective' in values and not 0 <= number(values['mainCollective'], 'MAIN collective') <= 1:
        raise DemoError('Native MAIN collective outside valid range')
    for name in ('sim/time/paused', 'sim/cockpit2/autopilot/servos_on'):
        if values[name] not in (0, 1):
            raise DemoError('Invalid native state flag: ' + name)
    modes = values['sim/cockpit/autopilot/autopilot_state']
    if modes < 0 or modes != int(modes):
        raise DemoError('Invalid native autopilot state')
    checks = (
        (values['sim/flightmodel2/misc/has_crashed'] != 0, 'aircraft crashed'),
        (values[P + 'y_agl'] < 100, 'terrain clearance below 100m'),
        (abs(values[P + 'phi']) > 55 or abs(values[P + 'theta']) > 35, 'attitude limit'),
        (values['sim/joystick/mouse_is_joystick'] != 0, 'manual mouse-flight input'),
        (not all(85 <= n <= 115 for n in values['sim/cockpit2/engine/indicators/N2_percent'][:2]), 'engine RPM limit'),
        (values['sim/flightmodel2/engines/engine_is_burning_fuel'][:2] != [1, 1], 'engine not running'),
        (values['sim/flightmodel/weight/m_fuel_total'] < 50, 'fuel below 50kg'),
        (values['sim/operation/failures/enable_random_failures'] != 0, 'random failures enabled'),
        (values[P + 'indicated_airspeed'] <= 0, 'nonpositive indicated airspeed'))
    return [reason for failed, reason in checks if failed]


def reset_autopilot(api):
    # autopilot_state is a command bitfield: writing zero is a no-op and
    # X-Plane always retains a lateral/vertical mode. Flight-director mode 0
    # disables steering/servos without requiring those mode bits to vanish.
    # https://developer.x-plane.com/article/flight-director-and-autothrottle-datarefs/
    api.command('sim/autopilot/servos_off_any')
    for name in ('flight_director_mode', 'flight_director2_mode'):
        api.write('sim/cockpit2/autopilot/' + name, 0)
    if (api.read('sim/cockpit2/autopilot/servos_on') != 0
            or api.read('sim/cockpit2/autopilot/flight_director_mode') != 0
            or api.read('sim/cockpit2/autopilot/flight_director2_mode') != 0):
        raise DemoError('Previous autopilot servos/flight directors did not disengage')


def select_ias_target(api):
    api.command('sim/autopilot/level_change')
    # The installed S-76's Lua wrapper captures CURRENT IAS on this command.
    # Restore the desired target afterward, never from paused/stale sensors.
    api.write('sim/cockpit2/autopilot/airspeed_is_mach', 0)
    api.write('sim/cockpit2/autopilot/airspeed_dial_kts_mach', 100.)


def select_native_ias_holds(api, heading):
    """Opt-in native HDG/IAS ownership; optional controller dependency is lazy."""
    import conservative_control as conservative
    api.command('sim/autopilot/autothrottle_hard_off')
    modes = int(number(api.read('sim/cockpit/autopilot/autopilot_state'), 'native modes'))
    if not modes & 2:
        api.command('sim/autopilot/heading')
    api.write('sim/cockpit2/autopilot/heading_dial_deg_mag_pilot', heading)
    if not modes & 8:
        select_ias_target(api)
    else:
        api.write('sim/cockpit2/autopilot/airspeed_is_mach', 0)
        api.write('sim/cockpit2/autopilot/airspeed_dial_kts_mach', 100.)
    if api.read('sim/cockpit2/autopilot/altitude_hold_armed'):
        api.command('sim/autopilot/altitude_arm')
    check = {n: api.read(n) for n in (
        'sim/cockpit/autopilot/autopilot_state', 'sim/cockpit2/autopilot/servos_on',
        'sim/cockpit2/autopilot/speed_status', 'sim/cockpit2/autopilot/altitude_hold_status',
        'sim/cockpit2/autopilot/altitude_hold_armed', 'sim/cockpit2/autopilot/autothrottle_enabled',
        'sim/cockpit2/autopilot/autothrottle_on', 'sim/cockpit2/autopilot/autothrottle_arm')}
    conservative.native_modes(check)


def select_fresh_holds(api, heading, altitude, collective_assist=False):
    # Exact native S-76 selection sequence exercised in the installed simulator.
    # Generic speed_status remains 0 after ALT on this airframe; do not use it
    # as proof of a separate IAS axis. Target-response tests remain authoritative.
    api.command('sim/autopilot/heading')
    api.write('sim/cockpit2/autopilot/heading_dial_deg_mag_pilot', heading)
    if not collective_assist:
        select_ias_target(api)
    api.command('sim/autopilot/altitude_hold')
    api.write('sim/cockpit2/autopilot/altitude_dial_ft', altitude)


def wait_for_airborne_readiness(api, clock=time, timeout=12):
    # Paused air-starts have correct local velocity but zero/stale IAS and N2.
    # Hold native attitude and the explicit startup collective until fresh
    # advancing physics frames meet the stricter initialization bounds.
    previous = number(api.read('sim/time/total_flight_time_sec'), 'startup native clock')
    deadline = clock.monotonic() + timeout
    while clock.monotonic() < deadline:
        ias = number(api.read(P + 'indicated_airspeed'), 'startup IAS')
        pitch = number(api.read(P + 'theta'), 'startup pitch')
        roll = number(api.read(P + 'phi'), 'startup roll')
        rpm = api.read('sim/cockpit2/engine/indicators/N2_percent')
        if not isinstance(rpm, list) or len(rpm) < 2:
            raise DemoError('Missing two startup engine values')
        rpm = [number(n, 'startup N2') for n in rpm[:2]]
        native_clock = number(api.read('sim/time/total_flight_time_sec'), 'startup native clock')
        if (abs(pitch) > 25 or abs(roll) > 35 or max(rpm) > 115
                or native_clock < previous
                or api.read('sim/flightmodel2/misc/has_crashed')
                or api.read('sim/joystick/mouse_is_joystick')
                or api.read('sim/time/paused')
                or api.read('sim/cockpit2/autopilot/autothrottle_enabled') != 0
                or number(api.read(P + 'y_agl'), 'startup clearance') < 100):
            raise DemoError('Unsafe airborne startup state')
        if (native_clock > previous and 80 < ias < 120 and all(95 < n < 110 for n in rpm)
                and api.read('sim/flightmodel2/engines/engine_is_burning_fuel')[:2] == [1, 1]):
            return (number(api.read(P + 'mag_psi'), 'startup magnetic heading') % 360,
                    number(api.read('sim/cockpit2/gauges/indicators/altitude_ft_pilot'), 'startup altitude'))
        previous = native_clock
        api.write_index(PROP_HANDLES, MAIN_COLLECTIVE_INDEX, .5)
        clock.sleep(.2)
    raise DemoError('Timed out waiting for fresh airborne IAS, engines, and advancing native clock')


def disable_autothrottle(api):
    # Only explicit --start may change AT state. Never arm/enable AT: this S-76
    # maps its collective drive incompatibly with the external assist.
    api.command('sim/autopilot/autothrottle_off')
    if api.read('sim/cockpit2/autopilot/autothrottle_enabled') != 0:
        raise DemoError('Autothrottle did not switch off; refusing collective control')


def assisted_collective(current, indicated_airspeed, elapsed):
    number(current, 'commanded collective')
    number(indicated_airspeed, 'assist IAS')
    number(elapsed, 'assist timestep')
    if not .15 <= current <= .85 or elapsed < 0:
        raise DemoError('Collective assist state outside bounds')
    rate = min(.018, max(-.018, (100. - indicated_airspeed) * .0007))
    return min(.85, max(.15, current + rate * min(.5, elapsed)))


def initialize_flight(api, route, clock=time, collective_assist=False):
    if api.read('sim/joystick/mouse_is_joystick'):
        raise DemoError('Release mouse-flight control before starting the demo')
    api.command('sim/operation/pause_on')
    reset_autopilot(api)
    disable_autothrottle(api)
    latitude, longitude = ROUTE[0] if route else (47.46, -122.3)
    api.request('/flight', {'aircraft': {'path': AIRCRAFT},
        'lle_air_start': {'latitude': latitude, 'longitude': longitude, 'elevation_in_meters': 2500,
            'heading_true': 0, 'speed_in_meters_per_second': 60, 'pitch_in_degrees': 2},
        'engine_status': {'all_engines': {'running': True}}, 'time_enum': 'day',
        'failures': {'fix_everything': True, 'mean_time_between_failures_in_hours': 10000}}, 'POST', 60)

    def loaded():
        if not aircraft_matches(api):
            return False
        position = (number(api.read(P + 'latitude'), 'latitude'), number(api.read(P + 'longitude'), 'longitude'))
        return distance_bearing(position, (latitude, longitude))[0] < 1500 and abs(number(api.read(P + 'elevation'), 'elevation') - 2500) < 200

    wait_until(loaded, 60, clock, 'requested S-76 airborne flight')
    api.command('sim/operation/pause_on')
    reset_autopilot(api)
    disable_autothrottle(api)
    api.write('sim/operation/failures/enable_random_failures', 0)
    api.command('sim/operation/fix_all_systems')
    if api.read('sim/operation/failures/enable_random_failures') != 0:
        raise DemoError('Random failures were not disabled')
    if api.read('sim/joystick/mouse_is_joystick'):
        raise DemoError('Mouse-flight input became active during startup')
    api.command('sim/operation/pause_off')
    api.command('sim/operation/quick_start')
    # quick_start resets engine/control settings on this airframe. Let its
    # command complete, then restore the explicit flight controls in the same
    # order used by the previously verified native demo.
    clock.sleep(.2)
    disable_autothrottle(api)
    api.write('sim/cockpit2/switches/rotor_brake_ratio', 0)
    api.write('sim/cockpit2/engine/actuators/throttle_ratio_all', 1)
    api.write('sim/cockpit2/engine/actuators/governor_on', [1, 1] + [0] * 14)
    api.write('sim/cockpit2/controls/gear_handle_down', 0)
    api.write('sim/cockpit2/controls/parking_brake_ratio', 0)
    api.write_index(PROP_HANDLES, MAIN_COLLECTIVE_INDEX, .5)
    api.write('sim/cockpit2/autopilot/autopilot_electric_master', 1)
    api.write('sim/cockpit2/switches/yaw_damper_on', 1)
    api.command('sim/autopilot/attitude')
    api.command('sim/autopilot/servos_on')
    heading, altitude = wait_for_airborne_readiness(api, clock)
    select_fresh_holds(api, heading, altitude, collective_assist)


def _run_optional_controller(module_name, args, api, clock):
    try:
        module = __import__(module_name)
    except ImportError as error:
        print(json.dumps({'status': 'error', 'reason':
            'Experimental controller modules are unavailable; deploy flight_demo.py, acquisition.py, '
            'sustain.py, conservative_control.py and checkpoint_state.py together: ' + str(error),
            'pauseRequested': False, 'pauseConfirmed': False}), flush=True)
        return 1
    return module.run(args, api, clock, sys.modules[__name__])


def run_demo(args, api, clock=time):
    if bool(getattr(args, 'sustain', False)):
        return _run_optional_controller('sustain', args, api, clock)
    if bool(getattr(args, 'ias_altitude_assist', False)):
        return _run_optional_controller('acquisition', args, api, clock)
    path = args.state_dir / 'latest.json'
    sequence = sample_sequence = 0
    values, sample_started, sample_completed = {}, None, None
    owns_flight = False
    heading = altitude = collective = None
    commanded_collective = None
    route_state, route_info = None, None
    route_seed_pending = False
    last_report = last_log = last_arm = last_heading_write = -math.inf

    def publish(status, reason=None, pause=None):
        nonlocal sequence
        sequence += 1
        now = clock.time()
        report = {'utc': now, 'sequence': sequence, 'sampleSequence': sample_sequence,
            'sampleStartedUtc': sample_started, 'sampleCompletedUtc': sample_completed,
            'sampleAgeSeconds': None if sample_started is None else max(0, now - sample_started),
            'status': status, 'reason': reason, 'routeEnabled': args.route, 'route': route_info,
            'controller': ('native HDG/ALT + rate-limited collective speed assist'
                if args.collective_assist else 'native S-76 hold selection; no external collective assist'),
            'collectiveAssist': args.collective_assist, 'commandedCollective': commanded_collective,
            'targetSpeedKt': 100., 'targetAltitudeFt': altitude, 'targetHeadingMag': heading,
            'collective': collective, 'collectiveDataref': PROP_HANDLES,
            'collectiveIndex': MAIN_COLLECTIVE_INDEX,
            'aggregatePropRatio': values.get('sim/cockpit2/engine/actuators/prop_ratio_all'),
            'values': values}
        if pause:
            report.update(pause)
        def clean(item):
            if isinstance(item, float) and not math.isfinite(item):
                return None
            if isinstance(item, dict):
                return {k: clean(v) for k, v in item.items()}
            if isinstance(item, (list, tuple)):
                return [clean(v) for v in item]
            return item
        report = clean(report)
        atomic_report(path, report)
        return report

    try:
        if args.start:
            if api.read('sim/joystick/mouse_is_joystick'):
                raise DemoError('Release mouse-flight control before starting; no flight changed')
            owns_flight = True
            initialize_flight(api, args.route, clock, args.collective_assist)
        if not aircraft_matches(api):
            raise DemoError('Flight demo only controls the installed S76 aircraft')
        owns_flight = True
        heading = number(api.read(P + 'mag_psi'), 'magnetic heading') % 360
        altitude = number(api.read('sim/cockpit2/gauges/indicators/altitude_ft_pilot'), 'altitude')
        if not args.start and api.read('sim/cockpit2/autopilot/servos_on'):
            heading = number(api.read('sim/cockpit2/autopilot/heading_dial_deg_mag_pilot'), 'heading dial') % 360
            altitude = number(api.read('sim/cockpit2/autopilot/altitude_dial_ft'), 'altitude dial')
        collective = number(api.read_index(PROP_HANDLES, MAIN_COLLECTIVE_INDEX), 'MAIN collective')
        if not 0 <= collective <= 1:
            raise DemoError('Collective outside native range')
        if args.collective_assist:
            if not .15 <= collective <= .85:
                raise DemoError('Initial collective outside assist bounds')
            commanded_collective = collective
        start = last_control = clock.monotonic()
        while args.seconds <= 0 or clock.monotonic() - start < args.seconds:
            now = clock.monotonic()
            control_dt, last_control = max(0, now - last_control), now
            sample_started = clock.time()
            values, sample_completed = {}, None
            values = {name: api.read(name) for name in NAMES}
            values['mainCollective'] = api.read_index(PROP_HANDLES, MAIN_COLLECTIVE_INDEX)
            sample_completed = clock.time()
            sample_sequence += 1
            reasons = safety_reasons(values)
            collective = number(values['mainCollective'], 'native MAIN collective')
            if args.collective_assist and values['sim/cockpit2/autopilot/autothrottle_enabled'] != 0:
                reasons.append('autothrottle/collective-assist conflict; AT must remain off')
            if sample_completed - sample_started > args.max_sample_age:
                reasons.append('native sample exceeded age limit')
            if args.route and distance_bearing((values[P + 'latitude'], values[P + 'longitude']), ROUTE_CENTER)[0] > ROUTE_LIMIT_METERS:
                reasons.append('outside bounded Puget route; explicit --start required to reposition')
            if reasons:
                print(json.dumps(publish('stopped', '; '.join(reasons), safe_pause(api)), allow_nan=False), flush=True)
                return 0
            if args.route:
                position = (values[P + 'latitude'], values[P + 'longitude'])
                if route_state is None:
                    checkpoint = None if args.start else recent_route_index(path, position, clock.time())
                    route_state = WaypointRoute(position, values[P + 'psi'], checkpoint)
                    heading = values[P + 'mag_psi'] % 360
                    route_seed_pending = True
                desired_heading, route_info = route_state.target(position, values[P + 'psi'], values[P + 'mag_psi'])
                route_info['desiredHeadingMag'] = desired_heading
            if not values['sim/time/paused']:
                modes = int(values['sim/cockpit/autopilot/autopilot_state'])
                if now - last_arm >= 4:
                    if not values['sim/cockpit2/autopilot/servos_on'] or not (modes & 2) or not (modes & 16384):
                        if not 80 < values[P + 'indicated_airspeed'] < 120:
                            raise DemoError('Fresh airborne IAS required before native hold re-engagement')
                        api.write('sim/cockpit2/autopilot/autopilot_electric_master', 1)
                        api.write('sim/cockpit2/switches/yaw_damper_on', 1)
                        if not values['sim/cockpit2/autopilot/servos_on']:
                            api.command('sim/autopilot/attitude')
                            api.command('sim/autopilot/servos_on')
                            select_fresh_holds(api, heading, altitude, args.collective_assist)
                        else:
                            if not modes & 2:
                                api.command('sim/autopilot/heading')
                            if not modes & 16384:
                                if not args.collective_assist:
                                    select_ias_target(api)
                                api.command('sim/autopilot/altitude_hold')
                        api.write('sim/cockpit2/autopilot/heading_dial_deg_mag_pilot', heading)
                        api.write('sim/cockpit2/autopilot/altitude_dial_ft', altitude)
                    last_arm = now
                if args.route and (route_seed_pending or now - last_heading_write >= .5):
                    if route_seed_pending:
                        heading = values[P + 'mag_psi'] % 360
                        route_seed_pending = False
                    else:
                        heading = limited_route_heading(heading, values[P + 'mag_psi'], desired_heading, now - last_heading_write)
                    api.write('sim/cockpit2/autopilot/heading_dial_deg_mag_pilot', heading)
                    last_heading_write = now
                if args.collective_assist:
                    commanded_collective = assisted_collective(commanded_collective, values[P + 'indicated_airspeed'], control_dt)
                    api.write_index(PROP_HANDLES, MAIN_COLLECTIVE_INDEX, commanded_collective)
            if now - last_report >= 1:
                report = publish('paused' if values['sim/time/paused'] else 'running')
                last_report = now
                if now - last_log >= 10:
                    print(json.dumps(report, allow_nan=False), flush=True)
                    last_log = now
            clock.sleep(.1)
        print(json.dumps(publish('completed', 'requested duration elapsed'), allow_nan=False), flush=True)
        return 0
    except (Exception, KeyboardInterrupt) as error:
        pause = safe_pause(api) if owns_flight else {'pauseRequested': False, 'pauseConfirmed': False}
        reason = str(error) or type(error).__name__
        try:
            report = publish('error', reason, pause)
        except Exception as report_error:
            # A broken/unwritable state directory cannot prevent safe pause or
            # the structured terminal error reaching the service journal.
            report = {'utc': clock.time(), 'status': 'error', 'reason': reason,
                'reportError': str(report_error), **pause}
        print(json.dumps(report, allow_nan=False), flush=True)
        return 1


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--start', action='store_true')
    parser.add_argument('--route', action='store_true', help='Follow bounded Puget Sound waypoints using native heading hold')
    parser.add_argument('--collective-assist', action='store_true', help='Rate-limited external collective assist for 100kt; requires native autothrottle off')
    parser.add_argument('--ias-altitude-assist', action='store_true', help='Opt-in paused S76 acquisition using native HDG/IAS100 and bounded MAIN0 altitude/VS PD')
    parser.add_argument('--sustain', action='store_true', help='Opt-in finite sustained MAIN0 PD from an explicitly reviewed stable checkpoint')
    parser.add_argument('--recover-from-guard', action='store_true', help='Explicit SHA-pinned V7 vertical guard error recovery:15s phase,300s total; never automatically retry')
    parser.add_argument('--resume-acquisition', type=Path, help='Explicit SHA-pinned checkpoint; never automatically select a recent file')
    parser.add_argument('--resume-sha256', help='Required SHA256 of --resume-acquisition')
    parser.add_argument('--resume-journal', type=Path, help='Paired full journal needed only to migrate an active V6 orbit')
    parser.add_argument('--resume-journal-sha256', help='Required SHA256 of --resume-journal')
    parser.add_argument('--orbit', action='store_true', help='Opt-in10km tangent orbit; sustained mode requires stable admission and unchanged geographic limits')
    parser.add_argument('--seconds', type=float, default=0)
    parser.add_argument('--api', default=B)
    parser.add_argument('--max-sample-age', type=float, default=3)
    parser.add_argument('--state-dir', type=Path, default=Path.home()/'.local/state/faa-s76-flight')
    args = parser.parse_args(argv)
    experimental = args.ias_altitude_assist or args.sustain
    if experimental and (args.start or args.route or args.collective_assist):
        parser.error('Experimental controllers require paused adoption and exclude --start/--route/--collective-assist')
    sustain_limit = 300 if args.recover_from_guard else 1800
    if args.sustain and (not args.resume_acquisition or not args.resume_sha256 or args.seconds > sustain_limit):
        parser.error('--sustain requires a reviewed checkpoint/hash and is limited to'+str(sustain_limit)+'s')
    if args.recover_from_guard and not args.sustain:
        parser.error('--recover-from-guard requires explicit --sustain and its SHA-pinned source')
    if bool(args.resume_acquisition) != bool(args.resume_sha256) or args.resume_acquisition and not experimental:
        parser.error('--resume-acquisition and --resume-sha256 require each other and an experimental controller')
    if bool(args.resume_journal) != bool(args.resume_journal_sha256) or args.resume_journal and not args.sustain:
        parser.error('--resume-journal and its SHA require each other and --sustain')
    acquisition_limit = 60 if args.resume_acquisition else 30
    if args.ias_altitude_assist and not args.sustain and (args.orbit or args.seconds > acquisition_limit):
        parser.error('Acquisition is limited to30s, or60s for an explicit resume, and excludes --orbit')
    if args.orbit and not args.sustain:
        parser.error('--orbit requires --sustain')
    if not math.isfinite(args.seconds) or args.seconds < 0 or not math.isfinite(args.max_sample_age) or args.max_sample_age <= 0:
        parser.error('--seconds must be finite and nonnegative; --max-sample-age must be finite and positive')
    def terminate(signum, frame):
        raise KeyboardInterrupt('termination requested')
    previous = signal.signal(signal.SIGTERM, terminate)
    try:
        return run_demo(args, NativeApi(args.api))
    finally:
        signal.signal(signal.SIGTERM, previous)


if __name__ == '__main__':
    raise SystemExit(main())
