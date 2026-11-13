# XR-3 Classic remote validation — 2026-10-30

The complete repaired source at commit
`54fc2c0351c092e5ab5501992985b07a07646312` was rebuilt and tested remotely on
`4090` with its Varjo XR-3 connected. The user requested remote tests only.
This note reports bounded results; it does not claim every function, wearer
acceptance or resolution of the earlier native corruption writer.

## Source and build

The [full-profile recipe](XR3_REPRODUCIBLE_PROFILE.md) reads committed Git/LFS
inputs and the pinned official Cesium archive into a new project. Independent
verification matched all **10,348 files, 812 directories, 4,007,105,954 bytes,
SHA-256 hashes and executable flags**. All 26 reviewed source/test paths matched
the candidate; private baseline probes were excluded. Unity Library was a
separate import cache, outside that source inventory.

Unity **6000.3.27f1** built the authored Experiment scene through
`FAA.Headset.Editor.XR3ReleaseBuild.BuildWindows64`: Windows x64, Mono,
Direct3D11, non-Development, **zero errors and 257 warnings**. The deployed
Assembly-CSharp MVID is `a206fb54-1c22-46c2-a7c2-48c021e46d56`; AircraftControl
MVID is `07bf598a-ef82-4f04-974a-443524b8eab4`.

## Automated checks

- Freshly materialized full profile: **1,632 passed, zero failed, 11 explicit
  GPU/benchmark skips**, exit 0. There were no missing optional-profile skips.
- Focused lifecycle, desktop Settings/input and traffic-boundary checks:
  **49/49 passed**. Seven separately selected existing normal-render GPU methods
  passed.
- The added Editor Settings raster capture was **inconclusive** because its
  independent known screen-camera marker was absent. It supplies no product
  pixel verdict; the real Windows evidence below is separate.
- Materializer safety/integration tests: **24/24 passed**, resource warnings
  treated as errors. Python flight-controller tests: **98 passed**.

## Windows runtime and desktop UI evidence

The 30-minute ordinary-user run started at **17:32:58.599 UTC** and closed normally
at **18:02:59.537 UTC**, exit `00000000`. It used zero application arguments,
no debugger and no forced termination. The first ten minutes were quiet before
the functional UI workflow. At the final sample the Varjo loader/display were
running, passthrough was accepted, the live flight feed was healthy and the hand
service was connected. All 1,652 fresh process-bound runtime samples agreed that the XR
display/passthrough and hand service were active. The full 1,657-row observer
stream recorded no debugger attachment and ended with normal close. Log review
found **zero native access violations, zero managed exception lines and zero
camera-parent/sibling teardown warnings**. Sampled application FPS after warm-up
had a median of **82.54**, range **50.96–90.20**; these are application estimates, not a
90 Hz headset presentation claim.

The log was not warning-free: **13 LiberationSans SDF ellipsis fallback warnings**
switched text to Truncate, **14 Curl28 request timeouts** and one no-microphone
warning remained. The originating UI object and font-selection path were not established; the
audited visible labels showed no corresponding defect. The log lines lack
timestamps, so individual timeouts cannot all be attributed to a simulator
pause. The runtime feed was ineligible in 121 bound samples: ten at startup and
111 during the finite flight handoff; the other bound samples reported healthy
live data. The headset reported no wearer and neither physical hand was
detected; those are not hand-recognition acceptance results.

A separate **600-second functional run of the same installed build** completed
with ordinary-user, zero-argument startup and normal close at
**18:27:13.258 UTC**, exit `00000000`. It produced no managed null-reference or
camera-parent/sibling errors. This is a functional follow-up, not another
30-minute endurance result.

The primary desktop recording completes the **planned core UI workflow**:
**115.300 seconds, 3,459 frames at 30 fps**, SHA-256
`c9ec6a57484c359c223075c2114e174775492d04c1b04b0f12f3847b938a9d33`.
All 28 scheduled input actions occurred within the recording. Strict complete
file decode reached EOF. Rendered frames show Help/Commands, Brief open/close,
HUD OFF/ON, real mouse Settings, Escape, F9, actual CLOSE, DATA LIVE opening
front Data Sources, its closing Escape, and restoration of the final baseline.
From **78.2667 seconds through EOF**, the baseline is LIVE, Classic, HUD ON,
CUES ON, with flyouts closed. The instrument Settings page remained steady for
**506 consecutive frames / 16.8667 seconds**; all four measured label/control
regions retained 100% strong reference pixels.

All **23,848 eligible fixed-feature ROI-frame measurements** had a unique best
integer translation of **0 pixels horizontally and vertically**, with no
reference-feature disappearance or adjacent-position jump. The hand hint was
present across all 3,459 frames with 100% reference recall. FMA was present in
all **3,371 HUD-ON frames**; its only complete absence was the **88 intentional
HUD-OFF frames**. The audit measured Classic outer rims, FMA, Settings labels and
CLOSE, and fixed toolbar/hint features. Needles, numeric values and moving
conformal/traffic geometry were excluded; intentional modal coverage was
qualified. This is an integer-pixel desktop result, not a claim about subpixel
motion or headset presentation timing.

The recording shows CUES control-state changes, but the traffic glyph was
**decluttered during its OFF/ON sequence**. Actual glyph and label visibility
**ON → OFF → ON** is proved by the three separate signed Windows stills from the
earlier ordinary run, not attributed to the video. The restored still has
Classic, HUD ON and CUES ON with menus closed. Individual Brief actions, every
traffic-boundary state and every application feature were not exhaustively
tested by this planned core workflow.

A post-stop UI automation verification error occurred after the final video file
had already closed: its last write was **18:23:46.791 UTC**, before the reported
error at **18:23:51 UTC**. Exclusive-access SHA-256 verification and full EOF
decode independently confirmed the closed, complete recording; capture was not
left running. The earlier short partial clip remains private historical
evidence and is not the primary recording described here.

## Actual S-76 flight

The exact eight public controller modules from the same source commit completed
a stock S-76 normal orbit trial on `3080`. Independent offline audit streamed
all **13,504 journal lines**: 4,502 samples, 4,501 timing records, **4,500 control
commands** and one terminal record. It checked 5,275,922 finite journal numbers,
all recorded safety/mode/input/override bounds, exact checkpoint/orbit
continuation, read timing, the complete retained 120-row ring and matching final
checkpoint/response. Launch and post-run source hashes matched the committed
modules. Every collective command matched the independent PD formula exactly.

The final paused receipt spans **1,800.2109375 native seconds**, with **1.6544
left turns**. IAS was **97.38–102.41 kt**, vertical speed **−1.59 to +1.54 m/s**,
and indicated altitude error **−6.03 to +10.32 m**. Heading command rate stayed
at or below **0.5 degrees per native second**; maximum lead was **3.10 degrees**.
All audit checks passed. Independent GETs are timed observations rather than an
atomic simulator snapshot; between-sample excursions cannot be excluded.
A separate normal continuation from that completed checkpoint also passed:
**360.2529296875 native seconds**, all **2,500 journal lines / 832 control
commands**, matching eight-module source hashes and an exact preserved orbit.
It ended paused at native clock **8690.7724609375**, with two equal receipt clocks
and no audit discrepancies. This was a second bounded phase with a paused
handoff, not one uninterrupted 36-minute flight.

A third normal phase supported the complete follow-up recording and also passed
independent audit: **600.46875 native seconds**, all **4,585 journal lines,
1,529 samples and 1,527 control commands**, unchanged eight-module source hashes
and no recorded ownership discrepancies. Its final two-equal-clock pause was at
**9291.2412109375 native seconds**, with **76.97 kg fuel**. These were three
separate bounded phases with paused handoffs. The simulator is now paused with
all controllers closed, and the FAA player has closed normally.

## Remaining limits

The earlier normal-user build reproduced a native access violation without a
debugger or optional audit flags. Its corruption writer remains unresolved.
A successful bounded 30-minute candidate run is evidence of that run, not a
universal crash fix or longer endurance qualification. The completed shutdown
review found no reproduced exception or camera teardown warning in this run;
the residual font and network warnings above remain visible limitations.

Physical headset centering, optical alignment, comfort, physical hand/pinch/gaze
recognition and wearer interaction were not tested. Private memory dumps, room
captures and raw runtime evidence are excluded from GitHub source payloads.
