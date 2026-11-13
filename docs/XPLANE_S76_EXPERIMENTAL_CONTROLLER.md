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
- `Tools/XPlane3080/parallel_snapshot.py`
- `Tools/XPlane3080/start_s76.py`
- `Tools/XPlane3080/verify_flying.py`

The six controller files are paired dependencies for the opt-in commands.
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

Qualified normal sustained duration is capped at 1800 native or wall seconds;
0 selects that finite cap. Explicit guard recovery has a separate 300-second
total cap. Fuel state is preserved and the existing 50 kg guard can end a run
earlier. A duration cap is a software bound, not a flight qualification.
Normal completion, errors and graceful signals after requesting unpause attempt
and verify pause. Preflight rejection sends no control commands.

## Continue the same circle

V7 and V8 sustained reports persist the original local orbit frame and final
observed heading dial. A reviewed completed V7 or V8 checkpoint can resume with the same
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
It does not build a replacement circle at the current pose. Later V7/V8 resumes
need no historical journal. Checksum the actual chosen file; differently
formatted stdout and `latest.json` can contain the same data but have different
checksums. MAIN source readback must be within 2e-7 of its command, then the current
observed float32 value must match the source receipt. V6 heading migration keeps
the existing 0.001° readback contract; V7/V8 uses its observed heading receipt.
The current controller keeps the V8 checkpoint protocol, with diagnostic
`privateCandidateVersion=9`. A failed or incompletely recovered V8 report is not
eligible for normal continuation.

## Independent read snapshots

Paused identity, checkpoint and input/mode admission stay serial. After those
checks, identifiers are warmed serially and one persistent eight-worker pool
reads the independent datarefs. Native clock reads bracket each snapshot on the
calling thread. MAIN writes/readbacks and heading writes/readbacks remain serial;
uncertain mutations are never retried. No additional dependency is required.

Snapshot width, native span and oldest field age are retained with each sample.
Full per-field read timestamps stream to the journal without expanding the
120-sample ring. Incomplete, failed, regressed or stale snapshots end the trial.
On an error or signal after unpause, the controller requests and verifies pause
before waiting for read workers to close. There is no slow serial fallback while
controlling an active flight. The PD gains, slew and 0.5-second timestep cap are
unchanged; shorter snapshots let the existing slew follow actual elapsed time.

## Explicit recovery from the reviewed vertical guard

Normal sustain continues to refuse failed checkpoints. `--recover-from-guard`
is a separate explicit phase for a SHA-pinned V7 error with the exact reason
`Original sustained control envelope exceeded`, a confirmed paused receipt and
a recorded vertical-only excursion above 3 and at most 6 m/s. It does not change
the failed source report's status or automatically select/retry a checkpoint.

```sh
python3 flight_demo.py --sustain --recover-from-guard --orbit \
  --resume-acquisition /path/to/reviewed-v7-error/latest.json \
  --resume-sha256 CHECKPOINT_SHA256 --seconds 300 \
  --state-dir /path/to/new-guard-recovery
```

The current aircraft, modes, axes, overrides, paused native clock, observed MAIN
and heading must still match. The original target, baseline, last command and
active orbit frame are preserved, including the first heading step's zero time
budget. Recovery uses the unchanged acquisition PD formula behind a narrower
phase guard: 90–110 KIAS, pitch ±15°, bank ±20°, altitude error ±80 m and actual
vertical speed ±6 m/s, plus the existing terrain, area, fuel and ownership guards.

Recovery has at most 15 native or wall seconds to observe three continuous native
seconds at 95–105 KIAS, angles within ±10° and vertical speed within ±1 m/s. It
then switches to the original strict sustained ±3 m/s guard without rebasing its
last command. Failure to earn that handoff before the bound ends with an error
and verified pause. The total invocation is capped at 300 native or wall seconds,
including later strict sustain;0 selects that finite cap. Recovery evidence and
handoff are recorded separately, and `flightAccepted` remains false.

## Evidence and qualification

Latest reports retain only 120 samples, while the line journal streams complete
sample/control/timing/phase/terminal events. Aggregate prop ratio, actual MAIN readback,
controller commands, source clocks and heading/orbit state remain distinguishable.
I/O failures stop the controller and cannot leave a successful terminal report.

A reviewed native-physics trial completed 1200.26 native seconds and one complete
10 km orbit, ended paused, and stayed within its configured flight and geographic
limits. Its independent audit measured about 386.9° of turn and a maximum
great-circle radial error of 225.6 m, inside the 2 km bound. The paired state/protocol
implementation originally had 134 private offline checks. Subsequent private
recovery/transport runs reported two completed 300-second phases with verified
pause and closed read workers. Their live snapshot width was about 0.200 s median
and 0.271 s maximum, compared with roughly 1.2 s for the earlier serial snapshots.

The paired private V9 controller later completed a normal 1800-second
continuation. An independent audit streamed all 13,567 journal lines: 4,523 samples,
4,522 timing events, 4,521 controls and one terminal event. Every recorded strict
flight envelope, mode/input ownership and override check passed. IAS ranged from
95.48 to 103.16 kt; vertical speed ranged from -1.59 to +2.49 m/s. Recomputed PD and
heading commands matched the frozen source, with heading rate at most 0.5 degrees
per native second and lead at most 4.11 degrees. The aircraft made about 1.65 left
turns, with maximum great-circle radial error 210.14 m. The final receipt recorded
pause at 1800.225 native seconds, including sampled stop latency, and closed read
workers. These checks cover recorded samples; independent GETs are timed and
bracketed rather than an atomic simulator snapshot. The earlier V7 continuation
that stopped after about 1419 seconds at its vertical guard remains a failed run.

The private V9 candidate has 160 offline checks; this public integration has 98
regressions, including the unchanged 50 indexed/legacy checks and a three-file
legacy deployment test. Fixtures are synthetic and contain no actual private
checkpoints, raw captures or room video.

This evidence does not qualify a two-hour run or fresh airborne initialization.
It also does not resolve the separate Windows Unity/XR player crash or verify
headset optics and physical hand interaction.
