# Function-test recording and source audit

The recording has two distinct environments: native XR on the Windows headset PC, and an explicitly selected desktop simulator for controls that normally require a physical head turn. Desktop footage does not verify XR-3 optics, headset tracking, hand recognition or worn-headset interaction.

## Reliable desktop audit mode

Launch the Windows player with `--xr3-sim` (or `-xr3-sim`). This explicit choice takes precedence over automatic native detection and a serialized native default. Before scene routing, the app deinitializes its own XR manager, whose lifecycle stops its subsystems. It does not stop Varjo Base or Windows services. The app uses a mono desktop camera, desktop layout and mouse input; delayed native detection cannot reverse that explicit choice. A normal launch without this flag retains the existing native behavior.

This corrects two prior problems: automatic native detection won before an explicit simulator request, and simulator point-and-click capture could disable the Windows player's mouse input. Managed tests cover activation precedence, loader lifecycle, mono projection and pointer policy. Actual Windows loader teardown and GUI recording still require the target PC to be available.

`--xr3-ui-audit` can be combined with either mode to log mouse-press hit details. Record a final ordinary launch without diagnostic flags after testing.

## Source-lifetime corrections

- Weather cues now clear when their provider is destroyed or disabled. A replacement provider is subscribed exactly once, with the old provider detached. Cached texture and display power/freshness metadata cannot leak from the previous provider into the replacement.
- Healthy ownship data with only expired traffic tracks now reports `TRAFFIC DATA STALE`, instead of asserting `NO TRAFFIC IN RANGE`. The existing ten-second traffic freshness rule remains unchanged.

Five of the ten new bridge regressions fail against the previous bridge source and pass with these corrections. Fresh rain, dry scans, radar power-off, genuine empty traffic and recovery retain distinct states.

## Validation, 2026-10-27

The full integration suite passes 1,295 cases. The public source profile passes 688 cases with two expected optional-profile skips. Fifty new cases cover explicit simulator behavior and indicator source lifetimes. The Windows x64 player builds with zero errors.

These are local managed tests and build checks. The new version has not been installed or recorded on the headset PC while that PC is unavailable.

## Recording coverage

Record normal startup and live source identity; flight instruments and conformal attitude; true traffic in-view/edge cues; cue visibility/range/style controls; help/commands/brief; settings and radar drawers; supported source loss/recovery and saved preferences. Distinguish unavailable external services from successful positive tests. Label renderer-key samples as examples. Verify context-eye bounds separately from the cropped Base mirror or narrower focus views.

Keep baseline footage, a timestamped action journal, runtime/simulator reports, an audit with timecodes, and a final verification recording after fixes. Check complete video playback and decoding, not only a thumbnail. Record physical-only and source-dependent exclusions explicitly.
