"""Private finite native-physics trial, never imported by legacy controller paths.

Checkpoint restore preserves the original PD baseline/target/last command.
MAIN handle0 is the only collective write. Tail and aggregate are diagnostics.
"""
from collections import deque
import hashlib
import json
import math
from pathlib import Path
import re
import signal
import struct
import time

import conservative_control as c
import checkpoint_state as state
import acquisition as a
from parallel_snapshot import ParallelSnapshot

PROP = 'sim/cockpit2/engine/actuators/prop_ratio'
HEADING = 'sim/cockpit2/autopilot/heading_dial_deg_mag_pilot'
MAX_SECONDS = 1800.  # Qualified normal continuation only; finite native OR wall.
RECOVERY_TOTAL_SECONDS = 300.
RECOVERY_SECONDS = 15.
RECOVERY_MAX_VY = 6.
SAMPLE_CAPACITY = 120
ORBIT_STABLE_SECONDS = 30.
RESUME_MAIN_QUANTIZATION = 2e-7
HEADING_READBACK_DEGREES = .001


class PhaseEnd(Exception):
    pass


def numeric(value, label):
    if type(value) not in (int, float):
        raise c.ControlError('Invalid numeric ' + label)
    return c.finite(value, label)


def f32(value):
    return struct.pack('<f', numeric(value, 'float32 readback'))


def load_checkpoint(path, expected_sha256, state_dir, journal_path=None, journal_sha256=None, demo=None, recover=False):
    return state.normalize(path, expected_sha256, state_dir,
        'recover' if recover else 'sustain', journal_path, journal_sha256, demo)


class SustainedCollective:
    """Explicit wrapper around the original strict sustained PD update."""
    def __init__(self, checkpoint):
        self.inner = c.AltitudeCollective(checkpoint['targetFt'])
        self.inner.trim = checkpoint['baseline']
        self.inner.last_written = checkpoint['lastCommand']
        # Native time while paused earns no new stable credit; no control state is reseeded.
        self.inner.stable_since = None
        self.inner.diagnostic = {'state': 'restored sustained PD', 'trim': self.inner.trim,
            'command': self.inner.last_written, 'integralErrorAccumulator': False}

    @property
    def target_m(self):
        return self.inner.target_m

    @property
    def last_written(self):
        return self.inner.last_written

    @property
    def diagnostic(self):
        return {**self.inner.diagnostic, 'initialMainBaselineNotLevelTrim': self.inner.trim,
            'commandMainIndex0': self.inner.last_written, 'sustainedEnvelope': True}

    def update(self, *args):
        return self.inner.update(*args)

    def verify_write(self, measured):
        self.inner.verify_write(measured)


class GuardRecoveryCollective:
    """Original acquisition PD formula behind a distinct reviewed narrow guard."""
    def __init__(self, checkpoint):
        self.inner = a.AcquisitionCollective(checkpoint['targetFt'], checkpoint['baseline'])
        self.inner.last_written = checkpoint['lastCommand']

    @property
    def target_m(self):
        return self.inner.target_m

    @property
    def last_written(self):
        return self.inner.last_written

    @property
    def handoff_ready(self):
        return self.inner.handoff_ready

    @property
    def diagnostic(self):
        return {**self.inner.diagnostic, 'guardRecoveryEnvelope': True,
            'commandMainIndex0': self.last_written}

    def update(self, measured, altitude_ft, vertical_speed, ias, pitch, bank, elapsed, native_now):
        # The broad startup envelope in AcquisitionCollective is never admission.
        for value, name in [(altitude_ft, 'altitude'), (vertical_speed, 'vertical speed'),
                            (ias, 'IAS'), (pitch, 'pitch'), (bank, 'bank')]:
            numeric(value, 'guard recovery '+name)
        if (not 90 <= ias <= 110 or abs(pitch) > 15 or abs(bank) > 20
                or abs(vertical_speed) > RECOVERY_MAX_VY
                or abs(self.target_m-altitude_ft*c.FT_TO_M) > c.MAX_ALT_ERROR_M):
            raise c.ControlError('Reviewed finite guard recovery envelope exceeded')
        return self.inner.update(measured, altitude_ft, vertical_speed, ias, pitch, bank, elapsed, native_now)

    def verify_write(self, measured):
        self.inner.verify_write(measured)


def restore(checkpoint, current_main, native_before, native_after, current_heading=None, recover=False):
    before = numeric(native_before, 'current paused clock before')
    after = numeric(native_after, 'current paused clock after')
    if before != after or after != checkpoint['pausedNativeClock']:
        raise c.ControlError('Current paused native clock differs from final v5 receipt')
    if f32(current_main) != f32(checkpoint['observedMain']):
        raise c.ControlError('Current MAIN differs from recorded observed float32 receipt')
    if checkpoint.get('headingLastCommand') is not None:
        current = numeric(current_heading, 'current paused heading dial')
        observed = checkpoint.get('headingObserved')
        if observed is not None:
            if f32(current % 360) != f32(observed % 360):
                raise c.ControlError('Current heading differs from recorded observed float32 receipt')
        elif abs((current-checkpoint['headingLastCommand']+180)%360-180) > HEADING_READBACK_DEGREES:
            raise c.ControlError('Current heading dial differs from source command; no rebase')
    return GuardRecoveryCollective(checkpoint) if recover else SustainedCollective(checkpoint)


class Evidence:
    """At most120 retained sample objects; full history goes only to a line stream."""
    def __init__(self, path):
        self.rows = deque(maxlen=SAMPLE_CAPACITY)
        self.samples = self.lines = 0
        self.path = Path(path)
        self.stream = self.path.open('x', encoding='utf-8', buffering=1)

    def event(self, kind, data):
        line = json.dumps({'event': kind, **data}, allow_nan=False, separators=(',', ':'))
        self.stream.write(line+'\n')
        self.lines += 1

    def sample(self, row):
        self.samples += 1
        row['sampleSequence'] = self.samples
        self.rows.append(row)
        self.event('sample', row)

    def close(self):
        self.stream.close()


def tight_stable(values, demo):
    return (95 <= values[demo.P+'indicated_airspeed'] <= 105
        and abs(values[demo.P+'theta']) <= 10 and abs(values[demo.P+'phi']) <= 10
        and abs(values[demo.P+'local_vy']) <= 1)


def run(args, api, clock, demo):
    values, ctl, evidence, checkpoint = {}, None, None, None
    sampler = None
    sequence, deadline, previous_native = 0, None, None
    native_start, stalled_since, old_alarm = None, None, None
    orbit, orbit_info, orbit_stable_since = None, None, None
    heading, heading_time, heading_writes = None, None, 0
    complete, can_write_report, unpause_requested = False, False, False
    first_resumed_orbit_step = False
    recovery_requested = bool(getattr(args, 'recover_from_guard', False))
    recovery_active = recovery_requested
    recovery_wall_deadline = None
    report = {'privateSustainedV6': False, 'privateSustainedV7': False, 'privateSustainedV8': True,
        'privateCandidateVersion': 9,
        'flightAccepted': False, 'sustainedRouteStarted': False,
        'collectiveControlIndex': 0, 'tailWrites': 0, 'aggregateWrites': 0,
        'headingLimiterUnchanged': True, 'orbitRequested': bool(getattr(args, 'orbit', False)),
        'orbitStableRequiredNativeSeconds': ORBIT_STABLE_SECONDS,
        'sampleCapacity': SAMPLE_CAPACITY, 'stableCreditResetForPausedGap': True,
        'recoveryRequested': recovery_requested, 'recoverySucceeded': False,
        'recoveryLimitSeconds': RECOVERY_SECONDS if recovery_requested else None,
        'controlPhase': 'recovery' if recovery_requested else 'sustained',
        'activeReadTransport': 'persistent-independent-GET-pool8', 'readWorkersClosed': False}
    state_dir = Path(args.state_dir)
    path = state_dir/'latest.json'
    extra = [demo.P+'local_vy', demo.P+'true_airspeed', HEADING,
        'sim/cockpit2/autopilot/heading_status',
        'sim/cockpit2/autopilot/airspeed_is_mach', 'sim/cockpit2/autopilot/airspeed_dial_kts_mach',
        'sim/cockpit2/autopilot/sync_hold_pitch_deg', 'sim/cockpit/autopilot/flight_director_pitch',
        'sim/cockpit2/autopilot/flight_director_pitch_deg']
    names = list(dict.fromkeys(demo.NAMES+c.REFS+tuple(extra)))

    def check_deadline():
        if recovery_active and recovery_wall_deadline is not None and clock.monotonic() >= recovery_wall_deadline:
            raise c.ControlError('Guard recovery did not earn stable handoff within15 wall seconds')
        if deadline is not None and clock.monotonic() >= deadline:
            raise PhaseEnd('Explicit finite sustained wall bound')

    def snapshot(label, bounded):
        if sampler is not None:
            out, info = sampler.capture(check_deadline if bounded else None)
            timings = info.pop('perFieldReadTimes')
            if info['captureWidthSeconds'] > args.max_sample_age:
                raise demo.DemoError('Sustained sample age or native clock invalid')
            row = {'label': label, **info, 'values': out,
                'maxFieldAgeSeconds': max(t['oldestObservationAgeSeconds'] for t in timings.values()),
                'controlPhase': 'recovery' if recovery_active else 'sustained'}
            evidence.sample(row)
            # Full per-field timing is streamed; the retained120-row ring stays bounded.
            evidence.event('snapshot-timing', {'sampleSequence': evidence.samples,
                'perFieldReadTimes': timings, 'captureWidthSeconds': info['captureWidthSeconds']})
            return out, row
        start = clock.time()
        before = demo.number(api.read('sim/time/total_flight_time_sec'), 'native clock before')
        out = {}
        for name in names:
            if bounded:
                check_deadline()
            out[name] = api.read(name)
        if bounded:
            check_deadline()
        out['mainCollective'] = api.read_index(PROP, 0)
        out['tailPropRatio'] = api.read_index(PROP, 1)
        after = demo.number(api.read('sim/time/total_flight_time_sec'), 'native clock after')
        if clock.time()-start > args.max_sample_age or after < before:
            raise demo.DemoError('Sustained sample age or native clock invalid')
        row = {'label': label, 'startedUtc': start, 'completedUtc': clock.time(),
            'nativeClockBefore': before, 'nativeClockAfter': after, 'values': out}
        evidence.sample(row)
        return out, row

    def guard(v, paused, recovering=False):
        c.input_ownership(v)
        reasons = demo.safety_reasons(v)
        if reasons:
            raise demo.DemoError('; '.join(reasons))
        if v['sim/time/paused'] != paused:
            raise demo.DemoError('Unexpected sustained pause state')
        c.native_modes(v)
        if v['sim/cockpit2/autopilot/heading_status'] != 2:
            raise demo.DemoError('Native heading hold lost')
        if (v['sim/cockpit2/autopilot/airspeed_is_mach'] != 0
                or abs(numeric(v['sim/cockpit2/autopilot/airspeed_dial_kts_mach'], 'native IAS target')-100) > .01):
            raise demo.DemoError('Native IAS100 target changed')
        if (not 90 <= v[demo.P+'indicated_airspeed'] <= 110 or abs(v[demo.P+'theta']) > 15
                or abs(v[demo.P+'phi']) > 20 or abs(v[demo.P+'local_vy']) > (RECOVERY_MAX_VY if recovering else 3)):
            raise demo.DemoError('Reviewed finite guard recovery envelope exceeded' if recovering
                else 'Original sustained control envelope exceeded')
        if v[demo.P+'y_agl'] < 1000:
            raise demo.DemoError('Sustained terrain clearance below1000m')
        if demo.distance_bearing((v[demo.P+'latitude'], v[demo.P+'longitude']), demo.ROUTE_CENTER)[0] > demo.ROUTE_LIMIT_METERS:
            raise demo.DemoError('Outside unchanged bounded Puget area')
        if ctl and abs(ctl.target_m-v['sim/cockpit2/gauges/indicators/altitude_ft_pilot']*c.FT_TO_M) > c.MAX_ALT_ERROR_M:
            raise demo.DemoError('Original sustained altitude deviation exceeds80m')

    def publish(status, reason=None, pause=None):
        nonlocal sequence
        sequence += 1
        result = {**report, 'utc': clock.time(), 'sequence': sequence, 'status': status, 'reason': reason,
            'sampleSequence': evidence.samples if evidence else 0,
            'samplesRetained': len(evidence.rows) if evidence else 0,
            'journalLines': evidence.lines if evidence else 0,
            'journalPath': str(evidence.path) if evidence else None,
            'nativeModesSelected': bool(ctl), 'values': values,
            'control': ctl.diagnostic if ctl else None,
            'collectiveMain': values.get('mainCollective'), 'tailPropRatio': values.get('tailPropRatio'),
            'aggregatePropRatio': values.get('sim/cockpit2/engine/actuators/prop_ratio_all'),
            'orbitActive': orbit is not None, 'route': orbit_info,
            'orbitGateStableSinceNativeClock': orbit_stable_since,
            'headingLastCommand': heading, 'headingWrites': heading_writes,
            'samples': list(evidence.rows) if evidence else []}
        if pause:
            result.update(pause)
        if can_write_report:
            demo.atomic_report(path, result)
        return result

    try:
        if not bool(getattr(args, 'sustain', False)) or args.start or args.route or args.collective_assist:
            raise demo.DemoError('Explicit --sustain requires paused adoption, no start/route/legacy assist')
        duration_cap = RECOVERY_TOTAL_SECONDS if recovery_requested else MAX_SECONDS
        if not math.isfinite(args.seconds) or not 0 <= args.seconds <= duration_cap:
            raise demo.DemoError('Private V9 duration must be finite and at most'+str(int(duration_cap))+'s')
        if not math.isfinite(args.max_sample_age) or args.max_sample_age <= 0:
            raise demo.DemoError('Invalid sustained sample age')
        seconds = args.seconds or duration_cap
        checkpoint = load_checkpoint(args.resume_acquisition, args.resume_sha256, state_dir,
            getattr(args, 'resume_journal', None), getattr(args, 'resume_journal_sha256', None), demo,
            recovery_requested)
        report.update({'resumedFromCheckpoint': checkpoint, 'durationLimitSeconds': seconds,
            'targetAltitudeFt': checkpoint['targetFt'], 'initialMainBaselineNotLevelTrim': checkpoint['baseline']})
        if not demo.aircraft_matches(api):
            raise demo.DemoError('Sustained controller requires installed stock S76')
        state_dir.mkdir(parents=True, exist_ok=True)
        if path.exists():
            raise demo.DemoError('Sustained output directory must be fresh')
        evidence = Evidence(state_dir/'journal.jsonl')
        can_write_report = True
        values, row = snapshot('initial paused', False)
        guard(values, 1, recovery_active)
        ctl = restore(checkpoint, values['mainCollective'], row['nativeClockBefore'], row['nativeClockAfter'], values[HEADING],
            recovery_requested)
        guard(values, 1, recovery_active)
        if not recovery_active and not tight_stable(values, demo):
            raise demo.DemoError('Current paused state no longer satisfies the tight stable handoff gate')
        heading = numeric(values[HEADING], 'held heading dial') % 360
        if checkpoint.get('headingLastCommand') is not None:
            heading = checkpoint['headingLastCommand']
        if checkpoint.get('orbitActive'):
            if not report['orbitRequested']:
                raise demo.DemoError('An active source orbit requires explicit --orbit continuation')
            orbit = state.restore_orbit(checkpoint['orbitState'], demo)
            desired, orbit_info = orbit.target((values[demo.P+'latitude'], values[demo.P+'longitude']),
                values[demo.P+'psi'], values[demo.P+'mag_psi'], demo.magnetic_heading)
            demo.limited_route_heading(heading, values[demo.P+'mag_psi'], desired, 0)
            orbit_stable_since = checkpoint.get('orbitGateStableSinceNativeClock')
            first_resumed_orbit_step = True
            report.update({'orbitRestoredFromCheckpoint': True, 'sustainedRouteStarted': True,
                'stableCreditResetForPausedGap': False,
                'orbitActivatedNativeClock': checkpoint.get('orbitActivatedNativeClock'),
                'orbitState': state.orbit_state(orbit)})
        native_start = previous_native = row['nativeClockAfter']
        sampler = ParallelSnapshot(api, names, PROP, clock)
        sampler.start()  # IDs warm serially after complete paused ownership admission.
        publish('ready-paused')
        deadline = clock.monotonic()+seconds
        recovery_wall_deadline = clock.monotonic()+RECOVERY_SECONDS if recovery_active else None
        if clock is time:
            old_alarm = signal.getsignal(signal.SIGALRM)
            def ended(signum, frame):
                raise PhaseEnd('Explicit finite sustained hard wall bound')
            signal.signal(signal.SIGALRM, ended)
            signal.alarm(max(1, math.ceil(min(seconds, RECOVERY_SECONDS) if recovery_active else seconds)))
        unpause_requested = True
        api.command('sim/operation/pause_off')
        last_publish = -math.inf
        while clock.monotonic() < deadline:
            values, row = snapshot('sustained active', True)
            guard(values, 0, recovery_active)
            native_now = row['nativeClockAfter']
            if native_now < previous_native:
                raise demo.DemoError('Sustained native clock regressed')
            if native_now == previous_native:
                stalled_since = clock.monotonic() if stalled_since is None else stalled_since
                if clock.monotonic()-stalled_since >= 2:
                    raise demo.DemoError('Sustained native physics clock stopped advancing')
            else:
                stalled_since = None
            if native_now-native_start >= seconds:
                raise PhaseEnd('Explicit finite sustained native bound')
            if recovery_active and native_now-native_start >= RECOVERY_SECONDS:
                raise c.ControlError('Guard recovery did not earn stable handoff within15 native seconds')
            elapsed = native_now-previous_native
            previous_native = native_now
            if abs(demo.angle_delta(numeric(values[HEADING], 'heading dial readback'), heading)) > HEADING_READBACK_DEGREES:
                raise demo.DemoError('Native heading dial ownership changed; no automatic rebase')
            command = ctl.update(values['mainCollective'],
                values['sim/cockpit2/gauges/indicators/altitude_ft_pilot'], values[demo.P+'local_vy'],
                values[demo.P+'indicated_airspeed'], values[demo.P+'theta'], values[demo.P+'phi'], elapsed, native_now)
            check_deadline()
            api.write_index(PROP, 0, command)
            main_readback = api.read_index(PROP, 0)
            ctl.verify_write(main_readback)
            check_deadline()
            if recovery_active and ctl.handoff_ready:
                stable_since = ctl.inner.stable_since
                restored = {'targetFt': checkpoint['targetFt'], 'baseline': checkpoint['baseline'],
                    'lastCommand': ctl.last_written}
                strict = SustainedCollective(restored)
                # Zero elapsed preserves the exact last recovery command; this
                # original strict PD update also proves the handoff sample fits.
                strict.update(main_readback, values['sim/cockpit2/gauges/indicators/altitude_ft_pilot'],
                    values[demo.P+'local_vy'], values[demo.P+'indicated_airspeed'],
                    values[demo.P+'theta'], values[demo.P+'phi'], 0, native_now)
                ctl = strict
                recovery_active = False
                report.update({'recoverySucceeded': True, 'controlPhase': 'sustained',
                    'recoveryStableSinceNativeClock': stable_since, 'recoveryHandoffNativeClock': native_now,
                    'recoveryNativeSeconds': native_now-native_start,
                    'recoveryWallSeconds': clock.monotonic()-(recovery_wall_deadline-RECOVERY_SECONDS)})
                evidence.event('phase', {'phase': 'strict-sustained-handoff', 'nativeClock': native_now,
                    'stableSinceNativeClock': stable_since, 'preservedLastMainCommand': ctl.last_written})
                if clock is time:
                    signal.alarm(max(1, math.ceil(deadline-clock.monotonic())))
            if report['orbitRequested'] and orbit is None:
                if tight_stable(values, demo):
                    orbit_stable_since = native_now if orbit_stable_since is None else orbit_stable_since
                else:
                    orbit_stable_since = None
                if orbit_stable_since is not None and native_now-orbit_stable_since >= ORBIT_STABLE_SECONDS:
                    orbit = c.TangentOrbit((values[demo.P+'latitude'], values[demo.P+'longitude']),
                        values[demo.P+'psi'], demo.ROUTE_CENTER, demo.ROUTE_LIMIT_METERS, demo.distance_bearing)
                    # No thirty-second heading budget, seed jump, or repeated reseeding.
                    heading_time = native_now
                    report['sustainedRouteStarted'] = True
                    report['orbitActivatedNativeClock'] = native_now
                    report['orbitState'] = state.orbit_state(orbit)
            if orbit is not None:
                desired, orbit_info = orbit.target((values[demo.P+'latitude'], values[demo.P+'longitude']),
                    values[demo.P+'psi'], values[demo.P+'mag_psi'], demo.magnetic_heading)
                heading_elapsed = 0 if first_resumed_orbit_step else native_now-heading_time
                next_heading = demo.limited_route_heading(heading, values[demo.P+'mag_psi'], desired, heading_elapsed)
                check_deadline()
                api.write(HEADING, next_heading)
                readback = numeric(api.read(HEADING), 'post-write native heading dial')
                if abs(demo.angle_delta(readback, next_heading)) > HEADING_READBACK_DEGREES:
                    raise demo.DemoError('Native heading write readback differs; no automatic rebase')
                heading, heading_time = next_heading, native_now
                first_resumed_orbit_step = False
                heading_writes += 1
            row['control'] = ctl.diagnostic
            row['mainWriteReadback'] = main_readback
            row['headingCommand'] = heading
            evidence.event('control', {'sampleSequence': evidence.samples, 'nativeClock': native_now,
                'control': ctl.diagnostic, 'mainWriteReadback': main_readback,
                'headingCommand': heading, 'orbitActive': orbit is not None, 'route': orbit_info})
            if clock.monotonic()-last_publish >= 1:
                publish('running')
                last_publish = clock.monotonic()
            clock.sleep(.1)
        if recovery_active:
            raise c.ControlError('Guard recovery did not earn stable handoff before finite end')
        complete = True
        report['termination'] = 'Explicit finite sustained wall bound'
    except PhaseEnd as exc:
        if recovery_active:
            report['error'] = 'Guard recovery did not earn stable handoff before finite end: '+str(exc)
        else:
            complete = True
            report['termination'] = str(exc)
    except (Exception, KeyboardInterrupt) as exc:
        report['error'] = str(exc) or type(exc).__name__
    finally:
        if old_alarm is not None:
            signal.alarm(0)
            signal.signal(signal.SIGALRM, old_alarm)
        report['unpauseRequested'] = unpause_requested
        pause = demo.safe_pause(api) if unpause_requested else {'pauseRequested': False, 'pauseConfirmed': False}
        # Pausing must happen before joining any outstanding read workers.
        if sampler is not None:
            try:
                sampler.close()
                report['readWorkersClosed'] = True
            except Exception as exc:
                complete = False
                report['error'] = 'Snapshot worker cleanup failed: '+str(exc)
        if pause.get('pauseConfirmed'):
            try:
                before = demo.number(api.read('sim/time/total_flight_time_sec'), 'final paused clock before')
                main, tail = api.read_index(PROP, 0), api.read_index(PROP, 1)
                heading_observed = numeric(api.read(HEADING), 'final paused heading dial')
                if heading is not None and abs(demo.angle_delta(heading_observed, heading)) > HEADING_READBACK_DEGREES:
                    raise demo.DemoError('Final paused heading ownership changed')
                after = demo.number(api.read('sim/time/total_flight_time_sec'), 'final paused clock after')
                report['finalPausedNativeReceipt'] = {'clockBefore': before, 'clockAfter': after,
                    'twoEqualClocks': before == after, 'mainCollective': main, 'tailPropRatio': tail, 'headingDial': heading_observed,
                    'lastControllerCommand': ctl.last_written if ctl else None}
            except Exception as exc:
                report['finalPausedReceiptError'] = str(exc)
        receipt = report.get('finalPausedNativeReceipt', {})
        status = 'completed' if complete and pause.get('pauseConfirmed') and receipt.get('twoEqualClocks') is True else 'error'
        try:
            if evidence:
                evidence.event('terminal', {'utc': clock.time(), 'status': status, **pause,
                    'reason': report.get('error') or report.get('termination')})
                evidence.close()
            result = publish(status, report.get('error') or report.get('termination'), pause)
        except Exception as exc:
            result = {**report, 'status': 'error', 'reportError': str(exc), **pause}
            status = 'error'
            if can_write_report:
                try:
                    demo.atomic_report(path, result)
                except Exception:
                    pass
        finally:
            if evidence and not evidence.stream.closed:
                try:
                    evidence.close()
                except Exception as exc:
                    result = {**report, 'status': 'error', 'journalCloseError': str(exc), **pause}
                    status = 'error'
        print(json.dumps(result, allow_nan=False), flush=True)
    return 0 if status == 'completed' else 1
