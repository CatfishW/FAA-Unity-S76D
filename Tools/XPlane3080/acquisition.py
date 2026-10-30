"""Opt-in bounded real-physics acquisition. No network calls occur at import.

Main collective is prop_ratio[0]; tail index1 and aggregate are diagnostics only.
The measured initial command is a baseline, NOT a validated level-flight trim.
"""
import json
import hashlib
import math
from pathlib import Path
import re
import signal
import struct
import time
import conservative_control as c
import checkpoint_state as state

PROP = 'sim/cockpit2/engine/actuators/prop_ratio'
MAX_SECONDS = 30.
RESUME_MAX_SECONDS = 60.


class PhaseEnd(Exception):
    pass


def load_resume_checkpoint(path, expected_sha256, state_dir):
    return state.normalize(path, expected_sha256, state_dir, 'acquire')


def restore_controller(checkpoint, current_main, native_before, native_after):
    before = c.finite(native_before, 'paused clock before')
    after = c.finite(native_after, 'paused clock after')
    main = c.finite(current_main, 'paused MAIN readback')
    if checkpoint.get('sourceVersion', 4) == 4:
        source = checkpoint['sourceNativeClockAfter']
        if before != after or not source <= after <= source+3:
            raise c.ControlError('Paused native clock does not match checkpoint lastAfter..lastAfter+3s')
        expected = checkpoint['lastCommand']
    else:
        if before != after or after != checkpoint['pausedNativeClock']:
            raise c.ControlError('Paused native clock does not match final source receipt')
        expected = checkpoint['observedMain']
    if struct.pack('<f', main) != struct.pack('<f', expected):
        raise c.ControlError('Paused MAIN readback differs from checkpoint observed state')
    ctl = AcquisitionCollective(checkpoint['targetFt'], checkpoint['baseline'])
    ctl.last_written = checkpoint['lastCommand']
    # There was an unobserved physical interval before v4 pause. Require new
    # observed stable native time; paused wall time never earns stable credit.
    ctl.diagnostic.update({'resumedLastCommand': ctl.last_written,
        'stableGateResetForPausedGap': True, 'sourceStableSince': checkpoint['sourceStableSince']})
    return ctl


class AcquisitionCollective:
    def __init__(self, target_ft, initial_main):
        self.target_m = c.finite(target_ft, 'target altitude ft') * c.FT_TO_M
        self.baseline = self.last_written = c.finite(initial_main, 'initial MAIN command')
        if not c.COLLECTIVE_MIN <= self.baseline <= c.COLLECTIVE_MAX:
            raise c.ControlError('Initial MAIN command outside control bounds')
        self.stable_since = None
        self.handoff_ready = False
        self.diagnostic = {'state': 'acquiring', 'initialMainBaselineNotLevelTrim': self.baseline}

    def update(self, measured, altitude_ft, vertical_speed, ias, pitch, bank, elapsed, native_now):
        for name, value in [('MAIN readback', measured), ('altitude ft', altitude_ft),
                            ('actual local_vy m/s', vertical_speed), ('IAS', ias), ('pitch', pitch),
                            ('bank', bank), ('native elapsed', elapsed), ('native clock', native_now)]:
            c.finite(value, name)
        error = self.target_m - altitude_ft * c.FT_TO_M
        if (not c.COLLECTIVE_MIN <= measured <= c.COLLECTIVE_MAX or elapsed < 0
                or abs(error) > 100 or not 60 <= ias <= 130 or abs(pitch) > 25
                or abs(bank) > 25 or abs(vertical_speed) > 12):
            raise c.ControlError('Bounded startup acquisition envelope exceeded')
        if abs(measured - self.last_written) > c.READBACK_TOLERANCE:
            raise c.ControlError('MAIN index0 ownership/readback mismatch; no aggregate rebase')
        stable = 95 <= ias <= 105 and abs(pitch) <= 10 and abs(bank) <= 10 and abs(vertical_speed) <= 1
        self.stable_since = native_now if stable and self.stable_since is None else self.stable_since if stable else None
        self.handoff_ready = self.stable_since is not None and native_now - self.stable_since >= c.ACQUIRE_SECONDS
        raw = self.baseline + c.ALT_GAIN * error - c.VS_GAIN * vertical_speed
        bounded = c.clamp(raw, c.COLLECTIVE_MIN, c.COLLECTIVE_MAX)
        step = c.COLLECTIVE_RATE * min(.5, elapsed)
        command = self.last_written + c.clamp(bounded - self.last_written, -step, step)
        self.last_written = command
        self.diagnostic = {'state': 'stable-for-handoff' if self.handoff_ready else 'acquiring',
            'initialMainBaselineNotLevelTrim': self.baseline, 'altitudeErrorM': error,
            'actualVerticalSpeedMps': vertical_speed, 'rawTarget': raw, 'boundedTarget': bounded,
            'commandMainIndex0': command, 'saturated': raw != bounded,
            'rateLimited': abs(bounded-command) > 1e-9, 'integralErrorAccumulator': False,
            'stableSinceNativeClock': self.stable_since, 'handoffReady': self.handoff_ready}
        return command

    def verify_write(self, measured):
        c.finite(measured, 'MAIN indexed write readback')
        if abs(measured-self.last_written) > c.READBACK_TOLERANCE:
            raise c.ControlError('MAIN indexed write did not retain command ownership')


def run(args, api, clock, demo):
    if args.start or args.route or args.collective_assist or bool(getattr(args, 'orbit', False)):
        raise demo.DemoError('Private v4 requires paused adoption; no --start/--route/--orbit/--collective-assist')
    if not math.isfinite(args.seconds) or args.seconds < 0 or not math.isfinite(args.max_sample_age) or args.max_sample_age <= 0:
        raise demo.DemoError('Invalid bounded acquisition duration or sample age')
    resume_path = getattr(args, 'resume_acquisition', None)
    resume_sha = getattr(args, 'resume_sha256', None)
    if bool(resume_path) != bool(resume_sha):
        raise demo.DemoError('Resume path and explicit checkpoint SHA256 require each other')
    resume = None
    limit = RESUME_MAX_SECONDS if resume_path else MAX_SECONDS
    seconds = min(limit, args.seconds if args.seconds > 0 else limit)
    if not math.isfinite(seconds) or seconds <= 0:
        raise demo.DemoError('Invalid bounded acquisition duration')
    path = args.state_dir / 'latest.json'
    rows, sequence = [], 0
    owns, selected, complete, can_write_report = False, False, False, False
    unpause_requested = False
    values, ctl, deadline, native_start, previous_native = {}, None, None, None, None
    stalled_since, last_sample = None, None
    report = {'privateAcquisitionV4': False, 'privateAcquisitionV5': True, 'flightAccepted': False, 'sustainedRouteStarted': False,
        'experimentalIasAltitudeAssist': True,
        'durationLimitSeconds': seconds, 'collectiveControlIndex': 0, 'tailWrites': 0,
        'aggregateWrites': 0, 'headingRouteControllerChanged': False}
    extra = [demo.P+'local_vy', demo.P+'true_airspeed',
             'sim/cockpit2/autopilot/heading_status', 'sim/cockpit2/autopilot/sync_hold_pitch_deg',
             'sim/cockpit/autopilot/flight_director_pitch',
             'sim/cockpit2/autopilot/flight_director_pitch_deg',
             'sim/cockpit2/gauges/indicators/altitude_ft_pilot']
    names = list(dict.fromkeys(demo.NAMES + c.REFS + tuple(extra)))

    def publish(status, reason=None, pause=None):
        nonlocal sequence
        sequence += 1
        result = {**report, 'utc': clock.time(), 'sequence': sequence, 'status': status, 'reason': reason,
            'sampleSequence': len(rows), 'nativeModesSelected': selected, 'values': values,
            'collectiveMain': values.get('mainCollective'), 'tailPropRatio': values.get('tailPropRatio'),
            'aggregatePropRatio': values.get('sim/cockpit2/engine/actuators/prop_ratio_all'),
            'control': ({'initialMainBaselineNotLevelTrim': ctl.baseline,
                'commandMainIndex0': ctl.last_written, 'integralErrorAccumulator': False,
                'stableSinceNativeClock': ctl.stable_since, 'handoffReady': ctl.handoff_ready,
                **ctl.diagnostic} if ctl else None), 'handoffReady': ctl.handoff_ready if ctl else False,
            'controlRequested': owns, 'unpauseRequested': unpause_requested,
            'samples': rows}
        if pause:
            result.update(pause)
        if can_write_report:
            demo.atomic_report(path, result)
        return result

    def snapshot(label, bounded=False):
        start = clock.time()
        before = demo.number(api.read('sim/time/total_flight_time_sec'), 'native clock before')
        out = {}
        for name in names:
            if bounded and clock.monotonic() >= deadline:
                raise PhaseEnd('Explicit finite wall acquisition bound')
            out[name] = api.read(name)
        out['mainCollective'] = api.read_index(PROP, 0)
        out['tailPropRatio'] = api.read_index(PROP, 1)
        after = demo.number(api.read('sim/time/total_flight_time_sec'), 'native clock after')
        if clock.time()-start > args.max_sample_age:
            raise demo.DemoError('Native acquisition sample exceeded age limit')
        if after < before:
            raise demo.DemoError('Native clock regressed during sample')
        rows.append({'label': label, 'startedUtc': start, 'completedUtc': clock.time(),
            'nativeClockBefore': before, 'nativeClockAfter': after, 'values': out})
        return out, after

    def guard(v, paused, modes):
        c.input_ownership(v)
        reasons = demo.safety_reasons(v)
        if reasons:
            raise demo.DemoError('; '.join(reasons))
        if v['sim/time/paused'] != paused:
            raise demo.DemoError('Unexpected acquisition pause state')
        if (not 60 <= v[demo.P+'indicated_airspeed'] <= 130 or abs(v[demo.P+'theta']) > 25
                or abs(v[demo.P+'phi']) > 25 or abs(v[demo.P+'local_vy']) > 12):
            raise demo.DemoError('Bounded startup acquisition envelope exceeded')
        if v[demo.P+'y_agl'] < 1000:
            raise demo.DemoError('Acquisition terrain clearance below1000m')
        if demo.distance_bearing((v[demo.P+'latitude'], v[demo.P+'longitude']), demo.ROUTE_CENTER)[0] > demo.ROUTE_LIMIT_METERS:
            raise demo.DemoError('outside bounded Puget route')
        if modes:
            c.native_modes(v)
            if v['sim/cockpit2/autopilot/heading_status'] != 2:
                raise demo.DemoError('Native heading hold not captured')
        if ctl and abs(ctl.target_m-v['sim/cockpit2/gauges/indicators/altitude_ft_pilot']*c.FT_TO_M) > 100:
            raise demo.DemoError('Acquisition altitude excursion exceeds100m')

    old_alarm = None
    try:
        resume = load_resume_checkpoint(resume_path, resume_sha, args.state_dir) if resume_path else None
        if resume:
            report.update({'resumedFromCheckpoint': resume, 'stableGateResetForPausedGap': True})
        if clock is time and not resume:
            old_alarm = signal.getsignal(signal.SIGALRM)
            def timed_out(signum, frame):
                raise demo.DemoError('45s hard acquisition deadline')
            signal.signal(signal.SIGALRM, timed_out)
            signal.alarm(45)
        if not demo.aircraft_matches(api):
            raise demo.DemoError('Unexpected aircraft; paused adoption requires stock S76')
        values, _ = snapshot('initial paused')
        guard(values, 1, bool(resume))
        if path.exists():
            raise demo.DemoError('Acquisition output directory must be fresh')
        path.parent.mkdir(parents=True, exist_ok=True)
        can_write_report = True
        if resume:
            row = rows[-1]
            ctl = restore_controller(resume, values['mainCollective'], row['nativeClockBefore'], row['nativeClockAfter'])
            selected = True
            native_start = row['nativeClockAfter']
            target_ft = resume['targetFt']
            guard(values, 1, True)  # Apply the original target's altitude cap before unpause.
        else:
            heading = demo.number(values[demo.P+'mag_psi'], 'initial magnetic heading') % 360
            # Ownership starts before the first requested mode mutation, not an identity read.
            owns = True
            demo.select_native_ias_holds(api, heading)
            selected = True
            values, native_start = snapshot('native IAS selected while paused')
            guard(values, 1, True)
            target_ft = demo.number(values['sim/cockpit2/gauges/indicators/altitude_ft_pilot'], 'initial altitude')
            ctl = AcquisitionCollective(target_ft, values['mainCollective'])
        report['targetAltitudeFt'] = target_ft
        report['initialMainBaselineNotLevelTrim'] = ctl.baseline
        previous_native = native_start
        deadline = clock.monotonic() + seconds
        if clock is time and resume:
            old_alarm = signal.getsignal(signal.SIGALRM)
            def continuation_timed_out(signum, frame):
                raise PhaseEnd('60s or requested hard wall continuation bound')
            signal.signal(signal.SIGALRM, continuation_timed_out)
            signal.alarm(max(1, math.ceil(seconds)))
        owns = unpause_requested = True
        api.command('sim/operation/pause_off')
        while clock.monotonic() < deadline:
            values, native_now = snapshot('acquisition active', True)
            guard(values, 0, True)
            if native_now < previous_native:
                raise demo.DemoError('Candidate native simulation clock regressed')
            if native_now == previous_native:
                stalled_since = clock.monotonic() if stalled_since is None else stalled_since
                if clock.monotonic()-stalled_since >= 2:
                    raise demo.DemoError('Candidate native physics clock stopped advancing')
            else:
                stalled_since = None
            if native_now-native_start >= seconds:
                raise PhaseEnd('Explicit finite native acquisition bound')
            elapsed = native_now-previous_native
            previous_native = native_now
            command = ctl.update(values['mainCollective'],
                values['sim/cockpit2/gauges/indicators/altitude_ft_pilot'], values[demo.P+'local_vy'],
                values[demo.P+'indicated_airspeed'], values[demo.P+'theta'], values[demo.P+'phi'], elapsed, native_now)
            api.write_index(PROP, 0, command)
            ctl.verify_write(api.read_index(PROP, 0))
            last_sample = native_now
            publish('stable-for-handoff' if ctl.handoff_ready else 'acquiring')
            clock.sleep(.1)
        complete = True
        report['termination'] = 'Explicit finite wall acquisition bound'
    except PhaseEnd as exc:
        complete = True
        report['termination'] = str(exc)
    except (Exception, KeyboardInterrupt) as exc:
        report['error'] = str(exc) or type(exc).__name__
    finally:
        if old_alarm is not None:
            signal.alarm(0)
            signal.signal(signal.SIGALRM, old_alarm)
        pause = demo.safe_pause(api) if owns else {'pauseRequested': False, 'pauseConfirmed': False}
        if owns and pause.get('pauseConfirmed'):
            try:
                final_before = demo.number(api.read('sim/time/total_flight_time_sec'), 'final paused clock before')
                final_main = api.read_index(PROP, 0)
                final_tail = api.read_index(PROP, 1)
                final_after = demo.number(api.read('sim/time/total_flight_time_sec'), 'final paused clock after')
                report['finalPausedNativeReceipt'] = {'clockBefore': final_before, 'clockAfter': final_after,
                    'twoEqualClocks': final_before == final_after, 'mainCollective': final_main,
                    'tailPropRatio': final_tail, 'lastControllerCommand': ctl.last_written if ctl else None}
            except Exception as exc:
                report['finalPausedReceiptError'] = str(exc)
        # A3s stable gate is only a handoff indication, never a sustained-flight claim.
        status = 'completed' if complete and pause.get('pauseConfirmed') and report.get('finalPausedNativeReceipt', {}).get('twoEqualClocks') is True else 'error'
        try:
            result = publish(status, report.get('error') or report.get('termination'), pause)
        except Exception as exc:
            result = {**report, 'status': 'error', 'reportError': str(exc), **pause}
        print(json.dumps(result, allow_nan=False), flush=True)
    return 0 if status == 'completed' else 1
