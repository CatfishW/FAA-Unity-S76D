# Opt-in S-76 acquisition and sustained flight

The native HDG/IAS controller is an opt-in path for an already-airborne, paused
stock S-76. It uses X-Plane physics and actual datarefs. Native IAS holds 100 kt;
the external altitude/vertical-speed PD writes only main collective handle 0.
The tail handle and combined prop-handle value remain independent diagnostics.
No aircraft pose, physics override, synthetic telemetry or fuel refill is used.

The existing indexed legacy controller remains the default when no new flags
are supplied. Its gains, mode selection and waypoint behavior are unchanged.
The optional controllers have separate bounded phases and checkpoint admission;
their commands run explicitly rather than through a restarting service.

## Paired deployment

Deploy the complete Python set from one revision into the same directory:

- `Tools/XPlane3080/flight_demo.py`
- `Tools/XPlane3080/acquisition.py`
- `Tools/XPlane3080/conservative_control.py`
- `Tools/XPlane3080/sustain.py`
- `Tools/XPlane3080/checkpoint_state.py`
- `Tools/XPlane3080/start_s76.py`
- `Tools/XPlane3080/verify_flying.py`

The five controller files are paired dependencies for the opt-in commands.
The parked-start and verification helpers retain their existing imports.
Do not mix module revisions. The legacy three-file deployment remains usable
because optional controller dependencies load only when their flags are selected;
an incomplete opt-in deployment returns an error before sending controls.
All modules use Python's standard library. These phase commands target the
Linux simulator host used by SSH alias `3080`.

## Acquire an existing airborne state

Use a new output directory. The requested flight must already be paused, match
the installed stock S-76 identity, and pass native engine, clearance, attitude,
input-axis and override checks. The experimental path refuses `--start`:
fresh airborne initialization is outside its current native validation.

```sh
python3 flight_demo.py --ias-altitude-assist --seconds 30 \
  --state-dir /path/to/new-acquisition
```

Acquisition selects and verifies native HDG/IAS before applying the PD. Its
initial main command is a baseline, not an assumed level-flight trim. It ends
paused after the first observed native-time or wall-time bound. Its report
includes `handoffReady` only after three continuous observed native seconds
within 95–105 KIAS, pitch/bank ±10° and actual vertical speed ±1 m/s.

If the completed acquisition is not ready, review its report and explicitly
continue it with the checksum of that exact file. The original baseline,
altitude target and last main command are preserved; stable credit restarts.
Use another new output directory:

```sh
python3 flight_demo.py --ias-altitude-assist \
  --resume-acquisition /path/to/new-acquisition/latest.json \
  --resume-sha256 CHECKPOINT_SHA256 --seconds 60 \
  --state-dir /path/to/new-acquisition-continuation
```

The normalizer accepts the V5 acquisition protocol emitted by both initial and
resumed acquisition, and retains explicit V4 migration compatibility. A
nonstable checkpoint can continue acquisition but cannot enter sustained mode.
Preflight refusal leaves the existing flight untouched. Once a mode mutation
or unpause has been requested, an error or graceful stop requests and verifies
pause. Reports retain `flightAccepted=false`; operational readiness is distinct
from overall application acceptance.

## Sustain a reviewed stable checkpoint

After reviewing a completed stable acquisition, choose an explicit finite
duration and a new output directory:

```sh
python3 flight_demo.py --sustain --orbit \
  --resume-acquisition /path/to/reviewed-stable-state/latest.json \
  --resume-sha256 CHECKPOINT_SHA256 --seconds 1200 \
  --state-dir /path/to/new-sustained-run
```

The sustained phase restores the original target, baseline and last command.
It requires matching paused native time, float32 main readback, stock identity,
native modes, active-input/override ownership and a current tight stable state.
It does not reselect the AP modes. The unchanged sustained envelope is 90–110 KIAS,
pitch ±15°, bank ±20°, actual vertical speed ±3 m/s and altitude error ±80 m. The fixed
PD gains are 0.0008 collective/metre and 0.012 collective/(m/s), with 0.012/s slew
and no integral accumulator or automatic command rebase.

With `--orbit`, the existing heading is held until 30 continuously observed tight
stable native seconds. The reviewed 10 km left tangent circle is then created
once from actual position and true heading. Its complete circle, 2 km radial
envelope and 250 m margin must fit inside the unchanged 35 km Puget area. Heading
commands preserve the existing 0.5°/s and 5° lead limits, including wrap. The first
step has zero turn-rate budget and retains the held native dial. An infeasible
fit, mode response, ownership or limit check pauses the trial rather than
repositioning or retrying it. Without an orbit, a long straight flight can reach
the geographic guard earlier than the requested duration.

Sustained duration is capped at 7200 native or wall seconds;0 selects that finite
cap. Fuel state is preserved and the existing 50 kg guard can end a run earlier.
The cap is a software bound, not evidence that a two-hour flight has passed.
Normal completion, errors and graceful signals after requesting unpause attempt
and verify pause. Preflight rejection sends no control commands.

## Continue the same circle

V7 sustained reports persist the original local orbit frame and final observed
heading dial. A reviewed completed V7 checkpoint can resume with the same
command above and its own checksum. It preserves the existing circle and last
heading command, uses zero heading-step budget for the first resumed sample,
and does not repeat a 30-second straight segment. An active source orbit requires
explicit `--orbit`; it cannot silently be dropped or downgraded to acquisition.

Older V6 reports omitted the local orbit frame. Their first migration therefore
also requires the exact paired full journal and its checksum:

```sh
python3 flight_demo.py --sustain --orbit \
  --resume-acquisition /path/to/reviewed-v6/latest.json \
  --resume-sha256 CHECKPOINT_SHA256 \
  --resume-journal /path/to/reviewed-v6/journal.jsonl \
  --resume-journal-sha256 JOURNAL_SHA256 --seconds 1200 \
  --state-dir /path/to/new-v7-run
```

The migration streams and verifies the journal, recovers the historical
activation frame and cross-checks its center, counts, final commands and pause.
It does not build a replacement circle at the current pose. Later V7 resumes
need no historical journal. Checksum the actual chosen file; differently
formatted stdout and `latest.json` can contain the same data but have different
checksums. MAIN source readback must be within 2e-7 of its command, then the current
observed float32 value must match the source receipt. V6 heading migration keeps
the existing 0.001° readback contract; V7 uses its observed heading receipt.

## Evidence and qualification

Latest reports retain only 120 samples, while the line journal streams complete
sample/control/terminal events. Aggregate prop ratio, actual MAIN readback,
controller commands, source clocks and heading/orbit state remain distinguishable.
I/O failures stop the controller and cannot leave a successful terminal report.

A reviewed native-physics trial completed 1200.26 native seconds and one complete
10 km orbit, ended paused, and stayed within its configured flight and geographic
limits. Its independent audit measured about 386.9° of turn and a maximum
great-circle radial error of 225.6 m, inside the 2 km bound. The paired state/protocol
implementation has 134 private offline checks; the additive public integration
has 69 regressions, including the unchanged 50 indexed/legacy checks. Test fixtures
are synthetic and contain no raw captures or room video.

This evidence does not qualify a two-hour run or fresh airborne initialization.
It also does not resolve the separate Windows Unity/XR player crash or verify
headset optics and physical hand interaction.
