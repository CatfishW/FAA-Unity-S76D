#!/usr/bin/env python3
"""Verify fresh, advancing native-controller reports without changing X-Plane."""
import argparse
import json
import math
from pathlib import Path
import time

from flight_demo import DemoError, NAMES, P, distance_bearing, number, safety_reasons


def validate_report(report, now, max_age=3):
    if not isinstance(report, dict):
        raise DemoError('controller report must be an object')
    for field in ('utc', 'sampleStartedUtc', 'sampleCompletedUtc', 'sampleAgeSeconds'):
        number(report[field], field)
    for field in ('sequence', 'sampleSequence'):
        if type(report[field]) is not int or report[field] <= 0:
            raise DemoError('invalid report ' + field)
    if report.get('status') != 'running':
        raise DemoError('controller not running: ' + str(report.get('status')) + ': ' + str(report.get('reason')))
    generated, started, completed = (report[n] for n in ('utc', 'sampleStartedUtc', 'sampleCompletedUtc'))
    if max(generated, started, completed) > now + .5:
        raise DemoError('future-dated controller/native sample')
    if not started <= completed <= generated + .1:
        raise DemoError('invalid native sample time ordering')
    if now - generated > max_age or now - started > max_age or not 0 <= report['sampleAgeSeconds'] <= max_age:
        raise DemoError('stale controller/native sample')
    if abs(report['sampleAgeSeconds'] - (generated - started)) > .5:
        raise DemoError('inconsistent native sample age')
    values = report['values']
    if not isinstance(values, dict):
        raise DemoError('native values must be an object')
    reasons = safety_reasons(values)
    if report.get('collectiveAssist') and values['sim/cockpit2/autopilot/autothrottle_enabled'] != 0:
        reasons.append('autothrottle/collective-assist conflict')
    if values['sim/time/paused']:
        reasons.append('simulator paused')
    if not values['sim/cockpit2/autopilot/servos_on']:
        reasons.append('autopilot servos off')
    modes = int(values['sim/cockpit/autopilot/autopilot_state'])
    if not modes & 2 or not modes & 16384:
        reasons.append('heading/altitude hold not engaged')
    if values[P + 'y_agl'] < 500:
        reasons.append('clearance below 500m')
    if not 80 <= values[P + 'indicated_airspeed'] <= 120:
        reasons.append('airspeed outside 80..120kt')
    if abs(values[P + 'phi']) > 25 or abs(values[P + 'theta']) > 20:
        reasons.append('attitude outside demo bounds')
    if reasons:
        raise DemoError('; '.join(reasons))
    return values


def verify(seconds, path, clock=time, startup_grace=5, max_age=3):
    start = clock.monotonic()
    rows, errors, total_distance = [], set(), 0.
    last_progress = None
    while clock.monotonic() - start < seconds:
        elapsed = clock.monotonic() - start
        try:
            report = json.loads(path.read_text())
            values = validate_report(report, clock.time(), max_age)
            if rows:
                previous = rows[-1]
                if report['sequence'] < previous['sequence'] or report['sampleSequence'] < previous['sampleSequence']:
                    raise DemoError('controller/native sample sequence regressed')
                if report['sequence'] == previous['sequence']:
                    if report != previous:
                        raise DemoError('controller report changed without a new sequence')
                else:
                    if (report['sampleSequence'] <= previous['sampleSequence']
                            or report['sampleStartedUtc'] <= previous['sampleStartedUtc']):
                        raise DemoError('native sample did not advance')
                    if report['sampleStartedUtc'] - previous['sampleStartedUtc'] > max_age:
                        raise DemoError('native sample gap exceeded age limit')
                    if values['sim/time/total_flight_time_sec'] <= previous['values']['sim/time/total_flight_time_sec']:
                        raise DemoError('native simulation clock did not advance')
                    old_values = previous['values']
                    total_distance += distance_bearing(
                        (old_values[P + 'latitude'], old_values[P + 'longitude']),
                        (values[P + 'latitude'], values[P + 'longitude']))[0]
                    rows.append(report)
                    last_progress = clock.monotonic()
            else:
                rows.append(report)
                last_progress = clock.monotonic()
        except (OSError, json.JSONDecodeError, KeyError, TypeError, ValueError, DemoError) as error:
            # Missing/corrupt reports get a short startup grace, never the entire
            # verification duration and never a silent successful empty result.
            if rows or elapsed >= min(startup_grace, seconds):
                errors.add('invalid/unavailable controller report: ' + str(error))
                break
        if rows and clock.monotonic() - last_progress > max_age:
            errors.add('no advancing controller/native samples')
            break
        remaining = seconds - (clock.monotonic() - start)
        if remaining > 0:
            clock.sleep(min(.5, remaining))
    if not rows:
        errors.add('no valid controller/native samples')
    else:
        minimum_samples = max(2, int(seconds / max_age))
        if len(rows) < minimum_samples:
            errors.add('insufficient advancing native samples')
        span = rows[-1]['sampleStartedUtc'] - rows[0]['sampleStartedUtc']
        if span < max(.1, seconds - startup_grace - max_age):
            errors.add('native sample coverage shorter than verification window')
        if total_distance < max(1, span):
            errors.add('insufficient physical position movement')

    def extent(key, scale=1):
        return None if not rows else [min(r['values'][key] for r in rows) * scale,
                                     max(r['values'][key] for r in rows) * scale]
    endpoint_distance = None
    if rows:
        first, last = rows[0]['values'], rows[-1]['values']
        endpoint_distance = distance_bearing((first[P + 'latitude'], first[P + 'longitude']),
                                            (last[P + 'latitude'], last[P + 'longitude']))[0]
    return {'passed': not errors, 'seconds': round(clock.monotonic() - start, 3),
        'samples': len(rows), 'errors': sorted(errors), 'ias_kt': extent(P + 'indicated_airspeed'),
        'altitude_msl_ft': extent(P + 'elevation', 1 / .3048), 'agl_m': extent(P + 'y_agl'),
        'pitch_deg': extent(P + 'theta'), 'roll_deg': extent(P + 'phi'),
        'distance_m': total_distance, 'endpoint_distance_m': endpoint_distance}


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--seconds', type=float, default=120)
    parser.add_argument('--state-dir', type=Path, default=Path.home()/'.local/state/faa-s76-flight')
    parser.add_argument('--startup-grace', type=float, default=5)
    args = parser.parse_args(argv)
    if not math.isfinite(args.seconds) or args.seconds <= 0 or not math.isfinite(args.startup_grace) or args.startup_grace < 0:
        result = {'passed': False, 'samples': 0, 'errors': ['seconds must be finite and positive; startup grace must be finite and nonnegative']}
    else:
        result = verify(args.seconds, args.state_dir / 'latest.json', startup_grace=args.startup_grace)
    print(json.dumps(result, indent=2, allow_nan=False))
    return 0 if result['passed'] else 1


if __name__ == '__main__':
    raise SystemExit(main())
