#!/usr/bin/env python3
"""Explicitly reset to a parked S-76C at KSEA, then start and check its engines."""
import argparse
import json
from pathlib import Path
import time

from flight_demo import AIRCRAFT, B, DemoError, MAIN_COLLECTIVE_INDEX, NativeApi, P, PROP_HANDLES, aircraft_matches, decoded, number, reset_autopilot, safe_pause, wait_until


def reset_parked(api, clock=time):
    if api.read('sim/joystick/mouse_is_joystick'):
        raise DemoError('Release mouse-flight control before resetting; no flight changed.')
    changed = False
    try:
        changed = True
        api.command('sim/operation/pause_on')
        reset_autopilot(api)
        # One explicit native flight load replaces the old reload + second load
        # race. Do not unpause until the requested aircraft/ground state is read back.
        api.request('/flight', {'aircraft': {'path': AIRCRAFT},
            'runway_start': {'airport_id': 'KSEA', 'runway': '16L'},
            'engine_status': {'all_engines': {'running': False}}, 'time_enum': 'day',
            'failures': {'fix_everything': True, 'mean_time_between_failures_in_hours': 10000}}, 'POST', 60)

        def loaded():
            return (aircraft_matches(api)
                and abs(number(api.read(P + 'latitude'), 'latitude') - 47.45) < .1
                and abs(number(api.read(P + 'longitude'), 'longitude') + 122.31) < .1
                and 0 <= number(api.read(P + 'y_agl'), 'clearance') < 10
                and abs(number(api.read(P + 'indicated_airspeed'), 'airspeed')) < 10)

        wait_until(loaded, 60, clock, 'parked S-76 at KSEA')
        api.command('sim/operation/pause_on')
        reset_autopilot(api)
        api.write('sim/operation/failures/enable_random_failures', 0)
        api.write('sim/operation/failures/mean_time_between_failure_hrs', 10000.)
        api.command('sim/operation/fix_all_systems')
        if api.read('sim/operation/failures/enable_random_failures') != 0:
            raise DemoError('Random failure configuration not confirmed')
        if api.read('sim/joystick/mouse_is_joystick'):
            raise DemoError('Mouse-flight input became active during reset')
        def parked_controls():
            api.write('sim/cockpit2/controls/parking_brake_ratio', 1.)
            api.write_index(PROP_HANDLES, MAIN_COLLECTIVE_INDEX, 0.)
            api.write('sim/cockpit2/switches/rotor_brake_ratio', 0.)
            api.write('sim/cockpit2/engine/actuators/governor_on', [1, 1] + [0] * 14)
            api.write('sim/cockpit2/engine/actuators/throttle_ratio_all', 1.)
        parked_controls()
        api.command('sim/operation/pause_off')
        api.command('sim/operation/quick_start', .2)
        clock.sleep(.3)
        parked_controls()

        def stable(require_engines):
            roll = number(api.read(P + 'phi'), 'roll')
            rpm, burning = api.read('sim/cockpit2/engine/indicators/N2_percent'), api.read('sim/flightmodel2/engines/engine_is_burning_fuel')
            if not isinstance(rpm, list) or len(rpm) < 2 or not isinstance(burning, list) or len(burning) < 2:
                raise DemoError('Missing two engine values')
            rpm = [number(n, 'N2') for n in rpm[:2]]
            if (abs(roll) > 8 or max(rpm) > 115 or min(rpm) < 0
                    or api.read('sim/flightmodel2/misc/has_crashed')
                    or api.read('sim/joystick/mouse_is_joystick')
                    or api.read('sim/time/paused')
                    or number(api.read(P + 'y_agl'), 'clearance') > 10):
                raise DemoError('Instability, pause, or manual-input detected')
            return not require_engines or min(rpm) >= 90 and burning[:2] == [1, 1]

        wait_until(lambda: stable(True), 20, clock, 'running parked S-76 engines')
        print('S-76C reset at KSEA; engines running, rotor brake released, parking brake on. Checking stability...', flush=True)
        for _ in range(15):
            if not stable(True):
                raise DemoError('Engine stability check failed')
            clock.sleep(1)
    except (Exception, KeyboardInterrupt):
        if changed:
            safe_pause(api)
        raise


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--xplane-root', type=Path, default=Path.home()/'.steam/debian-installation/steamapps/common/X-Plane 12')
    parser.add_argument('--api', default=B)
    parser.add_argument('--reset', action='store_true', help='Start a new flight; replaces the current simulator flight')
    args = parser.parse_args(argv)
    try:
        root = args.xplane_root.resolve(strict=True)
        if not (root / AIRCRAFT).is_file():
            raise DemoError('Installed S-76C not found')
        api = NativeApi(args.api)
        if not args.reset:
            loaded = decoded(api.read('sim/aircraft/view/acf_relative_path'))
            print(json.dumps({'ready': loaded == AIRCRAFT, 'installed': True, 'aircraft': loaded,
                'requested_aircraft': AIRCRAFT, 'reset_requested': False,
                'mouse_flight_active': api.read('sim/joystick/mouse_is_joystick')}))
            return 0
        reset_parked(api)
        print('Parked stability check passed. This launcher provides no takeoff or automatic flight control.')
        return 0
    except (Exception, KeyboardInterrupt) as error:
        print(json.dumps({'ready': False, 'reset_requested': args.reset, 'error': str(error) or type(error).__name__}), flush=True)
        return 1


if __name__ == '__main__':
    raise SystemExit(main())
