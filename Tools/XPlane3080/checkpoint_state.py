"""Private checkpoint protocol adapter. No simulator calls or state writes."""
import hashlib
import json
import math
from pathlib import Path
import re

import conservative_control as c

STATE_BYTES = 2*1024*1024
JOURNAL_BYTES = 64*1024*1024
LINE_BYTES = 2*1024*1024
MAIN_QUANTIZATION = 2e-7


def number(value, label):
    if type(value) not in (int, float):
        raise c.ControlError('Invalid numeric checkpoint ' + label)
    return c.finite(value, label)


def checksum(path, expected, maximum):
    path = Path(path)
    if not isinstance(expected, str) or not re.fullmatch(r'[a-fA-F0-9]{64}', expected):
        raise c.ControlError('Explicit checkpoint/journal SHA256 is required')
    if not path.is_file() or path.stat().st_size > maximum:
        raise c.ControlError('Checkpoint/journal missing or oversized')
    digest = hashlib.sha256()
    with path.open('rb') as stream:
        for block in iter(lambda: stream.read(65536), b''):
            digest.update(block)
    if digest.hexdigest() != expected.lower():
        raise c.ControlError('Checkpoint/journal SHA256 mismatch')
    return path


def _center(value):
    if not isinstance(value, (list, tuple)) or len(value) != 2:
        raise c.ControlError('Orbit center must have two geographic coordinates')
    lat, lon = (number(value[0], 'center latitude'), number(value[1], 'center longitude'))
    if not -90 <= lat <= 90 or not -180 <= lon <= 180:
        raise c.ControlError('Orbit center geographic range')
    return lat, lon


def _same_center(left, right):
    return all(abs(a-b) <= 1e-10 for a, b in zip(_center(left), _center(right)))


def orbit_state(orbit):
    """Persist the existing local frame; never derive a new one from current pose."""
    return {'schema': 1, 'algorithm': 'TangentOrbit-v1', 'lat0': orbit.lat0,
        'lon0': orbit.lon0, 'cosLat': orbit.cos_lat, 'cx': orbit.cx, 'cy': orbit.cy,
        'center': list(orbit.center), 'routeCenter': list(orbit.route_center),
        'routeLimitMeters': orbit.route_limit, 'radiusMeters': c.RADIUS_M,
        'radialLimitMeters': c.RADIAL_LIMIT_M, 'lookaheadMeters': c.LOOKAHEAD_M,
        'fitMarginMeters': c.FIT_MARGIN_M}


def restore_orbit(state, demo):
    if not isinstance(state, dict) or state.get('schema') != 1 or state.get('algorithm') != 'TangentOrbit-v1':
        raise c.ControlError('Unsupported persisted orbit state')
    fixed = {'radiusMeters': c.RADIUS_M, 'radialLimitMeters': c.RADIAL_LIMIT_M,
        'lookaheadMeters': c.LOOKAHEAD_M, 'fitMarginMeters': c.FIT_MARGIN_M,
        'routeLimitMeters': demo.ROUTE_LIMIT_METERS}
    for key, expected in fixed.items():
        if number(state.get(key), key) != expected:
            raise c.ControlError('Persisted orbit configuration changed: ' + key)
    if _center(state.get('routeCenter')) != tuple(demo.ROUTE_CENTER):
        raise c.ControlError('Persisted orbit test boundary changed')
    lat0, lon0 = _center([state.get('lat0'), state.get('lon0')])
    cosine = number(state.get('cosLat'), 'orbit latitude cosine')
    if abs(cosine) < .1 or abs(cosine-math.cos(math.radians(lat0))) > 1e-14:
        raise c.ControlError('Persisted orbit local frame is inconsistent')
    # Deliberately bypass the tangent constructor: this is an existing frame.
    orbit = object.__new__(c.TangentOrbit)
    orbit.lat0, orbit.lon0, orbit.cos_lat = lat0, lon0, cosine
    orbit.cx, orbit.cy = number(state.get('cx'), 'orbit cx'), number(state.get('cy'), 'orbit cy')
    orbit.center = _center(state.get('center'))
    orbit.route_center, orbit.route_limit = tuple(demo.ROUTE_CENTER), demo.ROUTE_LIMIT_METERS
    orbit.distance_bearing = demo.distance_bearing
    if not _same_center(orbit.geo(orbit.cx, orbit.cy), orbit.center):
        raise c.ControlError('Persisted orbit center does not match its original frame')
    if demo.distance_bearing(orbit.center, orbit.route_center)[0]+c.RADIUS_M+c.RADIAL_LIMIT_M+c.FIT_MARGIN_M > orbit.route_limit:
        raise c.ControlError('Persisted entire orbit no longer fits bounded Puget area')
    return orbit


def recover_v6_orbit(data, journal_path, journal_sha256, demo):
    """Migrate the original frame from the exact paired, SHA-pinned journal."""
    if demo is None or not journal_path or not journal_sha256:
        raise c.ControlError('Active V6 orbit requires its explicit paired journal and SHA256')
    path = checksum(journal_path, journal_sha256, JOURNAL_BYTES)
    first, latest_sample, last_control, terminal = None, None, None, None
    counts = {'sample': 0, 'control': 0, 'terminal': 0, 'orbit': 0, 'lines': 0}
    center = _center(data.get('route', {}).get('center'))
    with path.open('rb') as stream:
        while True:
            line = stream.readline(LINE_BYTES+1)
            if not line:
                break
            if len(line) > LINE_BYTES or not line.endswith(b'\n'):
                raise c.ControlError('Journal has oversized or incomplete line')
            event = json.loads(line)
            if terminal is not None:
                raise c.ControlError('Journal has events after terminal pause')
            counts['lines'] += 1
            kind = event.get('event')
            if kind not in ('sample', 'control', 'terminal'):
                raise c.ControlError('Unexpected journal event')
            counts[kind] += 1
            if kind == 'sample':
                if event.get('sampleSequence') != counts['sample']:
                    raise c.ControlError('Journal sample sequence mismatch')
                latest_sample = event
            elif kind == 'control':
                if latest_sample is None or event.get('sampleSequence') != latest_sample.get('sampleSequence'):
                    raise c.ControlError('Journal control/sample pairing mismatch')
                last_control = event
                if event.get('orbitActive') is True:
                    counts['orbit'] += 1
                    route = event.get('route', {})
                    if not _same_center(route.get('center'), center) or route.get('radiusMeters') != c.RADIUS_M:
                        raise c.ControlError('Journal circle was changed/reseeded')
                    if first is None:
                        if event.get('nativeClock') != data.get('orbitActivatedNativeClock') or event.get('nativeClock') != latest_sample.get('nativeClockAfter'):
                            raise c.ControlError('Journal orbit activation clock mismatch')
                        values = latest_sample['values']
                        first = c.TangentOrbit((values[demo.P+'latitude'], values[demo.P+'longitude']),
                            values[demo.P+'psi'], demo.ROUTE_CENTER, demo.ROUTE_LIMIT_METERS, demo.distance_bearing)
                        if not _same_center(first.center, center):
                            raise c.ControlError('Original activation frame differs from checkpoint circle')
                        # Preserve the exact center serialized by the source; frame origin stays historical.
                        first.center = center
            else:
                terminal = event
    if (first is None or last_control is None or terminal is None or counts['terminal'] != 1
            or counts['sample'] != data.get('sampleSequence') or counts['lines'] != data.get('journalLines')
            or counts['orbit'] != data.get('headingWrites')
            or terminal.get('status') != 'completed' or terminal.get('pauseConfirmed') is not True
            or last_control.get('headingCommand') != data.get('headingLastCommand')
            or last_control.get('control', {}).get('commandMainIndex0') != data.get('control', {}).get('commandMainIndex0')):
        raise c.ControlError('Journal final state does not match the reviewed V6 checkpoint')
    state = orbit_state(first)
    state['recoveredFromV6Journal'] = {'sha256': journal_sha256.lower(), 'lines': counts['lines'],
        'activationNativeClock': data['orbitActivatedNativeClock']}
    restore_orbit(state, demo)
    return state


def normalize(path, expected_sha256, state_dir, phase, journal_path=None, journal_sha256=None, demo=None):
    if phase not in ('acquire', 'sustain', 'recover'):
        raise c.ControlError('Unsupported checkpoint continuation phase')
    path = checksum(path, expected_sha256, STATE_BYTES)
    if path.resolve().parent == Path(state_dir).resolve():
        raise c.ControlError('Continuation state directory must differ from checkpoint source directory')
    data = json.loads(path.read_bytes())
    if not isinstance(data, dict):
        raise c.ControlError('Checkpoint must be an object')
    versions = [(4, 'privateAcquisitionV4'), (5, 'privateAcquisitionV5'),
                (6, 'privateSustainedV6'), (7, 'privateSustainedV7'),
                (8, 'privateSustainedV8')]
    matches = [version for version, key in versions if data.get(key) is True]
    if len(matches) != 1:
        raise c.ControlError('Checkpoint protocol version is ambiguous or unknown')
    version = matches[0]
    recovery = phase == 'recover'
    if recovery and version != 7:
        raise c.ControlError('Guard recovery admits only the explicitly reviewed V7 error protocol')
    if (phase == 'sustain' and version == 4) or (phase == 'acquire' and version >= 6):
        raise c.ControlError('Checkpoint cannot change controller phase or discard an active orbit')
    required = {'status': 'error' if recovery else 'completed', 'pauseRequested': True, 'pauseConfirmed': True,
        'nativeModesSelected': True, 'collectiveControlIndex': 0, 'tailWrites': 0, 'aggregateWrites': 0}
    for key, expected in required.items():
        value = data.get(key)
        if type(value) is not type(expected) or value != expected:
            raise c.ControlError('Incomplete reviewed checkpoint contract: ' + key)
    if recovery:
        reason = 'Original sustained control envelope exceeded'
        if (data.get('reason') != reason or data.get('error') != reason
                or data.get('flightAccepted') is not False or data.get('unpauseRequested') is not True):
            raise c.ControlError('Guard recovery requires the exact reviewed failed sustained envelope reason')
    if version == 8 and data.get('recoveryRequested') is True:
        if data.get('recoverySucceeded') is not True or data.get('controlPhase') != 'sustained':
            raise c.ControlError('V8 recovery checkpoint never qualified for strict sustain')
    if version < 6 and (data.get('experimentalIasAltitudeAssist') is not True
            or data.get('sustainedRouteStarted') is not False or data.get('headingRouteControllerChanged') is not False):
        raise c.ControlError('Acquisition checkpoint cannot contain an active route')
    control, samples = data.get('control'), data.get('samples')
    if not isinstance(control, dict) or not isinstance(samples, list) or not samples:
        raise c.ControlError('Checkpoint lacks control state or samples')
    target = number(data.get('targetAltitudeFt'), 'original target ft')
    baseline = number(data.get('initialMainBaselineNotLevelTrim'), 'original baseline')
    last = number(control.get('commandMainIndex0'), 'last MAIN command')
    last_clock = number(samples[-1].get('nativeClockAfter'), 'last sampled clock')
    if (control.get('initialMainBaselineNotLevelTrim') != baseline
            or control.get('integralErrorAccumulator') is not False
            or not all(c.COLLECTIVE_MIN <= value <= c.COLLECTIVE_MAX for value in (baseline, last))):
        raise c.ControlError('Checkpoint baseline/controller state is inconsistent')
    sequence = data.get('sampleSequence')
    if type(sequence) is not int or sequence < len(samples) or last_clock < 0 or last_clock < number(samples[-1].get('nativeClockBefore'), 'sample clock before'):
        raise c.ControlError('Checkpoint sample clock or sequence is inconsistent')
    if version < 6 and sequence != len(samples):
        raise c.ControlError('Acquisition checkpoint sample history is incomplete')
    if version >= 6 and (data.get('sampleCapacity') != 120 or data.get('samplesRetained') != len(samples) or len(samples) > 120 or samples[-1].get('sampleSequence') != sequence):
        raise c.ControlError('Sustained checkpoint retained ring is inconsistent')
    stable_since = control.get('stableSinceNativeClock')
    handoff = data.get('handoffReady') is True and control.get('handoffReady') is True
    if version < 6 and phase == 'sustain':
        stable_since = number(stable_since, 'stable native clock')
        if not handoff or stable_since < 0 or last_clock-stable_since < c.ACQUIRE_SECONDS or control.get('state') != 'stable-for-handoff':
            raise c.ControlError('Acquisition checkpoint did not complete stable handoff')
    if version >= 6 and (control.get('sustainedEnvelope') is not True or control.get('state') != 'PD active'):
        raise c.ControlError('Checkpoint did not retain the sustained controller envelope')
    if version >= 6 and (number(control.get('trim'), 'sustained trim') != baseline or number(control.get('command'), 'sustained command') != last):
        raise c.ControlError('Sustained trim/command differs from preserved state')
    result = {'sha256': expected_sha256.lower(), 'sourceVersion': version, 'sourcePath': str(path.resolve()),
        'sourcePhase': 'sustained' if version >= 6 else 'acquiring', 'targetFt': target,
        'baseline': baseline, 'lastCommand': last, 'sourceLastSampleClock': last_clock,
        'sourceStableSince': stable_since, 'sourceHandoffReady': handoff, 'orbitState': None,
        'orbitActive': False, 'headingLastCommand': None, 'headingObserved': None}
    if recovery:
        # This is an explicitly admitted failed source, never a rewritten success.
        values = samples[-1].get('values')
        if not isinstance(values, dict) or values != data.get('values') or demo is None:
            raise c.ControlError('Guard recovery source must preserve its actual offending sample')
        ias, pitch, bank, vy = [number(values.get(demo.P+n), 'source '+n)
            for n in ('indicated_airspeed', 'theta', 'phi', 'local_vy')]
        altitude = number(values.get('sim/cockpit2/gauges/indicators/altitude_ft_pilot'), 'source altitude')
        if (not 90 <= ias <= 110 or abs(pitch) > 15 or abs(bank) > 20
                or not 3 < abs(vy) <= 6 or abs((target-altitude)*c.FT_TO_M) > c.MAX_ALT_ERROR_M):
            raise c.ControlError('Guard recovery source exceeds the reviewed vertical-only recovery envelope')
        result.update({'sourceStatus': 'error', 'sourceError': data['error'],
            'sourcePhase': 'sustained-error-recovery', 'recoverySourceVerticalSpeedMps': vy})
    if version == 4:
        result.update({'sourceNativeClockAfter': last_clock, 'sourceWallUtcDiagnosticOnly': data.get('utc')})
        return result
    receipt = data.get('finalPausedNativeReceipt')
    if not isinstance(receipt, dict):
        raise c.ControlError('Checkpoint lacks final native paused receipt')
    before, after = number(receipt.get('clockBefore'), 'receipt clock before'), number(receipt.get('clockAfter'), 'receipt clock after')
    observed = number(receipt.get('mainCollective'), 'receipt observed MAIN')
    if receipt.get('twoEqualClocks') is not True or before < 0 or before != after or not last_clock <= after <= last_clock+3:
        raise c.ControlError('Checkpoint final paused clock is inconsistent')
    if number(receipt.get('lastControllerCommand'), 'receipt last command') != last:
        raise c.ControlError('Checkpoint final command differs from controller state')
    if not c.COLLECTIVE_MIN <= observed <= c.COLLECTIVE_MAX or abs(observed-last) > MAIN_QUANTIZATION:
        raise c.ControlError('Checkpoint MAIN readback exceeds float quantization allowance')
    result.update({'observedMain': observed, 'pausedNativeClock': after})
    if version >= 6:
        heading = number(data.get('headingLastCommand'), 'source heading command')
        if not 0 <= heading < 360:
            raise c.ControlError('Source heading command range')
        result['headingLastCommand'] = heading
        if receipt.get('headingDial') is not None:
            result['headingObserved'] = number(receipt['headingDial'], 'source observed heading dial')
            if abs((result['headingObserved']-heading+180)%360-180) > .001:
                raise c.ControlError('Source observed heading differs from its last command')
        elif version >= 7:
            raise c.ControlError('V7/V8 checkpoint lacks observed final heading dial')
        result['orbitActive'] = data.get('orbitActive') is True
        result['orbitActivatedNativeClock'] = data.get('orbitActivatedNativeClock')
        result['orbitGateStableSinceNativeClock'] = data.get('orbitGateStableSinceNativeClock')
        if result['orbitActive']:
            if data.get('sustainedRouteStarted') is not True or data.get('orbitRequested') is not True:
                raise c.ControlError('Active orbit checkpoint contract is inconsistent')
            state = recover_v6_orbit(data, journal_path, journal_sha256, demo) if version == 6 else data.get('orbitState')
            if state is None or demo is None:
                raise c.ControlError('Active orbit checkpoint requires complete original frame')
            orbit = restore_orbit(state, demo)
            if not _same_center(orbit.center, data.get('route', {}).get('center')):
                raise c.ControlError('Checkpoint route and persisted original circle differ')
            result['orbitState'] = orbit_state(orbit)
            result['orbitStateProvenance'] = state.get('recoveredFromV6Journal')
    return result
