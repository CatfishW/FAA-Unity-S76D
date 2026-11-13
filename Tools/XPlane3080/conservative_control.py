"""Private, opt-in controller candidate; no network access or flight-model override.

PD gains are proposed acquisition values, not validated helicopter tuning.
Native IAS owns pitch; this helper owns only the collective handle.
"""
import math

FT_TO_M = .3048
RADIUS_M = 10000.
LOOKAHEAD_M = 3000.
RADIAL_LIMIT_M = 2000.
FIT_MARGIN_M = 250.  # Extra room for local-ENU approximation and sampled-position checks.
COLLECTIVE_MIN, COLLECTIVE_MAX = .15, .85
COLLECTIVE_RATE = .012  # /s; stricter than the old .018/s speed-integrator limit.
ALT_GAIN = .0008       # collective ratio per metre of altitude error
VS_GAIN = .012         # collective ratio per m/s (damping, positive-up VS)
READBACK_TOLERANCE = .02
ACQUIRE_SECONDS = 3.
MAX_ALT_ERROR_M = 80.

REFS = (
    'sim/cockpit2/gauges/indicators/altitude_ft_pilot',
    'sim/cockpit2/autopilot/speed_status',
    'sim/cockpit2/autopilot/altitude_hold_status',
    'sim/cockpit2/autopilot/altitude_hold_armed',
    'sim/cockpit2/autopilot/autothrottle_on',
    'sim/cockpit2/autopilot/autothrottle_arm',
    'sim/joystick/joystick_axis_assignments',
    'sim/joystick/joy_mapped_axis_avail',
    'sim/joystick/joy_mapped_axis_value',
    'sim/operation/override/override_planepath',
    'sim/operation/override/override_control_surfaces',
    'sim/operation/override/override_flightcontrol',
    'sim/operation/override/override_joystick',
    'sim/operation/override/override_artstab',
    'sim/operation/override/override_throttles',
    'sim/operation/override/override_TCAS',
    'sim/operation/override/override_autopilot',
    'sim/operation/override/override_prop_pitch',
    'sim/operation/override/override_joystick_pitch',
    'sim/operation/override/override_joystick_roll',
    'sim/operation/override/override_joystick_heading',
)


class ControlError(RuntimeError):
    pass


def finite(value, label):
    if not isinstance(value, (float, int)) or not math.isfinite(value):
        raise ControlError('Invalid candidate native value: ' + label)
    return float(value)


def clamp(value, low, high):
    return max(low, min(high, value))


def input_ownership(values):
    """Saved assignments are not active hardware. Never unbind a user axis."""
    assigned = values['sim/joystick/joystick_axis_assignments']
    available = values['sim/joystick/joy_mapped_axis_avail']
    axes = values['sim/joystick/joy_mapped_axis_value']
    if not isinstance(assigned, list) or len(assigned) != 500:
        raise ControlError('Missing native saved axis assignments')
    if not isinstance(available, list) or len(available) != 81 or not isinstance(axes, list) or len(axes) != 81:
        raise ControlError('Missing native active axis ownership data')
    for a in assigned:
        if finite(a, 'axis assignment') < 0 or int(a) != a:
            raise ControlError('Invalid saved native axis assignment')
    for a, x in zip(available, axes):
        if a not in (0, 1):
            raise ControlError('Invalid native axis availability')
        finite(x, 'mapped axis value')
    # Conservative first acquisition: any active mapped axis can take the flight
    # from this automation. Stale saved pitch/throttle/PROP assignments may remain.
    if any(available):
        raise ControlError('Active mapped joystick axis; candidate refuses flight-control ownership')
    for name in REFS:
        if '/override/' not in name:
            continue
        raw = values[name]
        if name.endswith('override_planepath') and (not isinstance(raw, list) or len(raw) != 20):
            raise ControlError('Missing native physics override ownership array')
        items = raw if isinstance(raw, list) else [raw]
        if not items or any(finite(v, name) != 0 for v in items):
            raise ControlError('Native physics/control override active: ' + name)
    return {'savedCollectivePropAxisIndices': [i for i, a in enumerate(assigned) if a in (5, 8, 24, 25, 26, 27)],
            'activeMappedAxes': [], 'overridesZero': True}


def native_modes(values):
    raw_modes = values['sim/cockpit/autopilot/autopilot_state']
    finite(raw_modes, 'native modes')
    if raw_modes < 0 or raw_modes != int(raw_modes):
        raise ControlError('Invalid negative or fractional native mode bitmask')
    modes = int(raw_modes)
    if (values['sim/cockpit2/autopilot/servos_on'] != 1 or not modes & 2 or not modes & 8
            or modes & (32 | 16384)
            or values['sim/cockpit2/autopilot/speed_status'] != 2
            or values['sim/cockpit2/autopilot/altitude_hold_status'] != 0
            or values['sim/cockpit2/autopilot/altitude_hold_armed'] != 0):
        raise ControlError('Native HDG/IAS pitch ownership lost or ALT/ALT-arm active')
    if (values['sim/cockpit2/autopilot/autothrottle_enabled'] != -1
            or values['sim/cockpit2/autopilot/autothrottle_on'] != 0
            or values['sim/cockpit2/autopilot/autothrottle_arm'] != 0):
        raise ControlError('Autothrottle not fully disarmed; refusing collective ownership')


class AltitudeCollective:
    """Fixed trim + altitude/VS PD. No IAS-error integral or hidden actuator rebase."""
    def __init__(self, target_ft):
        self.target_m = finite(target_ft, 'target altitude ft') * FT_TO_M
        self.trim = self.last_written = None
        self.stable_since = None
        self.diagnostic = {'state': 'awaiting stable IAS acquisition'}

    def update(self, measured, altitude_ft, vertical_speed, ias, pitch, bank, elapsed, now):
        for label, value in [('measured collective', measured), ('altitude ft', altitude_ft),
                             ('vertical speed m/s', vertical_speed), ('IAS', ias), ('pitch', pitch),
                             ('bank', bank), ('elapsed', elapsed), ('monotonic time', now)]:
            finite(value, label)
        if not COLLECTIVE_MIN <= measured <= COLLECTIVE_MAX or elapsed < 0:
            raise ControlError('Candidate collective or timestep outside bounds')
        error = self.target_m - altitude_ft * FT_TO_M
        if abs(error) > MAX_ALT_ERROR_M:
            raise ControlError('Candidate altitude deviation exceeds 80m')
        if abs(pitch) > 15 or abs(bank) > 20 or abs(vertical_speed) > 3 or not 90 <= ias <= 110:
            raise ControlError('Candidate acquisition/control envelope exceeded')
        if self.trim is None:
            stable = 95 <= ias <= 105 and abs(pitch) <= 10 and abs(bank) <= 10 and abs(vertical_speed) <= 1
            self.stable_since = now if stable and self.stable_since is None else self.stable_since if stable else None
            if self.stable_since is None or now - self.stable_since < ACQUIRE_SECONDS:
                return None
            self.trim = self.last_written = measured
        if abs(measured - self.last_written) > READBACK_TOLERANCE:
            raise ControlError('Collective readback/ownership mismatch exceeds .02; no automatic rebase')
        raw = self.trim + ALT_GAIN * error - VS_GAIN * vertical_speed
        target = clamp(raw, COLLECTIVE_MIN, COLLECTIVE_MAX)
        maximum_step = COLLECTIVE_RATE * min(.5, elapsed)
        command = self.last_written + clamp(target - self.last_written, -maximum_step, maximum_step)
        self.last_written = command
        self.diagnostic = {'state': 'PD active', 'trim': self.trim, 'altitudeErrorM': error,
            'verticalSpeedMps': vertical_speed, 'rawTarget': raw, 'boundedTarget': target,
            'command': command, 'saturated': raw != target,
            'rateLimited': abs(target - command) > 1e-9, 'integralErrorAccumulator': False}
        return command

    def verify_write(self, measured):
        finite(measured, 'collective write readback')
        if self.last_written is not None and abs(measured - self.last_written) > READBACK_TOLERANCE:
            raise ControlError('Collective write did not retain native handle ownership')


class TangentOrbit:
    """10km left orbit tangent to initial true heading. Local ENU metres, geographic input."""
    def __init__(self, position, true_heading, route_center, route_limit, distance_bearing):
        for label, value in [('orbit latitude', position[0]), ('orbit longitude', position[1]),
                             ('initial true heading', true_heading), ('route limit metres', route_limit)]:
            finite(value, label)
        self.distance_bearing = distance_bearing
        self.lat0, self.lon0 = position
        self.cos_lat = math.cos(math.radians(self.lat0))
        if abs(self.cos_lat) < .1:
            raise ControlError('Orbit latitude not supported by local Puget guidance')
        a = math.radians(true_heading - 90)
        self.cx, self.cy = RADIUS_M * math.sin(a), RADIUS_M * math.cos(a)
        self.center = self.geo(self.cx, self.cy)
        self.route_center, self.route_limit = route_center, route_limit
        if distance_bearing(self.center, route_center)[0] + RADIUS_M + RADIAL_LIMIT_M + FIT_MARGIN_M > route_limit:
            raise ControlError('Tangent orbit cannot fit bounded Puget area; explicit fresh --start required')

    def geo(self, x, y):
        return self.lat0 + math.degrees(y / 6371000), self.lon0 + math.degrees(x / (6371000 * self.cos_lat))

    def xy(self, position):
        finite(position[0], 'orbit latitude');finite(position[1], 'orbit longitude')
        return (math.radians(position[1] - self.lon0) * 6371000 * self.cos_lat,
                math.radians(position[0] - self.lat0) * 6371000)

    def target(self, position, true_heading, magnetic, to_magnetic):
        if self.distance_bearing(position, self.route_center)[0] > self.route_limit:
            raise ControlError('Outside bounded Puget route')
        x, y = self.xy(position);dx, dy = x - self.cx, y - self.cy
        radial = math.hypot(dx, dy);error = radial - RADIUS_M
        if abs(error) > RADIAL_LIMIT_M:
            raise ControlError('Orbit radial error exceeds 2km')
        tangent = math.degrees(math.atan2(dx, dy)) - 90
        desired_true = (tangent - math.degrees(math.atan2(error, LOOKAHEAD_M))) % 360
        return to_magnetic(desired_true, true_heading, magnetic), {'kind': 'tangent left orbit',
            'center': self.center, 'radiusMeters': RADIUS_M, 'radialErrorMeters': error,
            'desiredHeadingTrue': desired_true, 'desiredHeadingMag': to_magnetic(desired_true, true_heading, magnetic)}
