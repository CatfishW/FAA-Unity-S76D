# FAA Varjo XR-3 Windows release

## Run on the headset PC

Extract the entire release ZIP to a writable local folder on the Windows PC
connected to the Varjo XR-3. Keep the executable, `FAA-XR3_Data`, `UnityPlayer.dll`,
and `MonoBleedingEdge` together. Start Varjo Base, confirm that the headset is
connected and tracking, then run `Launch-XR3.cmd`. The launch script selects
native XR-3 routing and Direct3D 11 and writes diagnostics to
`%LOCALAPPDATA%\FAA-XR3\Player-XR3.log`.

Use **Varjo Base 4.14** for the XR-3. The bundled Varjo XR plugin 3.7.3 requires
Base 4.6 or newer, but XR-3 support ends at the 4.14 release family. Base 4.15
and later target XR-4 instead; do not update the XR-3 PC indiscriminately.
The headset PC needs the Varjo-compatible GPU, drivers, cabling, and tracking
setup. See the vendor's release notes and system requirements for that PC.

Run `Check-Terrain.cmd` to check the independent terrain connection. When the
source is on the simulator host, run `Start-Terrain-Tunnel.cmd` with the Windows
PC's configured SSH alias and leave that window open. The host must already run
the read-only terrain service on port 8767. No passwords or keys are bundled.
For another trusted endpoint, edit `FAA-XR3_Data/StreamingAssets/FAA/TerrainConnection.json`,
set `FAA_TERRAIN_URL`, or pass `--terrain-url URL` to the launcher. See
`TERRAIN-CONNECTION.md` and `HUD-TERRAIN-UPDATE.md`. Flight telemetry and terrain
use separate connections; a working HUD does not imply terrain is connected.

The current FAA source scene, HUD, traffic/weather interfaces and X-Plane terrain
code are included. X-Plane, terrain/data services, local datasets and network
endpoints remain external dependencies; the executable does not install them.
This release retains the project's opaque VR presentation. Passthrough/MR
composition, controllers and other optional headset functions require their
own configuration and physical validation.

This update includes the in-game Digital/Classic Analog selector, protected
side-mounted radars/settings, right-button panel dragging, and the native
Ultraleap hand-input path. The optional laptop-webcam recognizer is separately
packaged per operating system; a Mac helper does not run in this Windows build.

## Build profile and provenance

The release uses Unity **6000.5.10f1**, Windows x86-64, the Mono scripting backend,
Direct3D 11, Linear color space, and the native **Varjo XR plugin 3.7.3**. It is
not a Development Build. The existing single-pass/foveated Varjo settings are
retained. The validated FAA ExperimentScene is the only scene in this release.

The build scene processor removes both XR Interaction Simulator implementations
from the serialized player scene and disables automatic simulator activation.
It edits only the build's in-memory scene, not the source scene. Explicit
`--xr3-sim` developer functionality remains in code but is not the release default.

Legacy Post Processing Stack v1 camera effects are disabled in the build scene;
their intermediate render textures are not a validated Varjo quad-view path.
Desktop scene settings are unchanged. Depth texture declarations in the legacy
shader resources use Unity's stereo-aware declaration macros, and lightning
soft-particle depth sampling receives the correct stereo eye index. These fixes
remove Direct3D stereo shader compilation errors without claiming that the old
post-processing stack has been completely ported to native XR.

The source workspace's embedded Cesium 1.20.0 has custom Unity compatibility
changes but lacks the matching native binaries. The isolated release snapshot
therefore uses the **complete official Cesium 1.25.1 package**, which includes
Unity 6.5 compatibility and matching generated bindings/native libraries.
The original embedded package and uncommitted application changes are preserved.

`build-report.json` records Unity's actual build result. `SHA256SUMS.txt` covers
the release files; the adjacent `.zip.sha256` verifies the archive. A successful
build and binary/package validation do **not** prove physical XR-3 operation.

## Rebuild on the Mac

Close the source Unity Editor first. From the FAA project root, run:

```bash
bash Tools/XR3/build-release-macos.sh
```

Install the exact Unity version and its Windows Build Support (Mono) module.
The build runs in batch mode with host graphics enabled: ExperimentScene has
4097-sample terrain assets whose serialization exceeds the null-graphics
device's 4096-pixel texture limit. Build errors are not ignored even when
Unity reports an overall Succeeded result.
The script uses APFS copy-on-write clones of the current Assets, Packages,
ProjectSettings and optional Library into `_artifacts/xr3/<UTC timestamp>/project`.
It pins and verifies the Cesium package, builds the release, validates x64 native
plugins and ZIP integrity, and writes `Builds/FAA-XR3-<UTC timestamp>-Windows-x64.zip`.
Logs and source-diff evidence remain in the timestamped artifacts directory.
Set `UNITY_EDITOR` to an alternate path for the same installed editor version.
No commit, push, server deployment or source-package replacement is performed.

## Physical acceptance check

Before a pilot demonstration, verify both-eye rendering, stable head pose,
eye calibration/foveated output, HUD angular placement and readability, input
behavior, tracking loss/recovery, and sustained frame timing on the actual PC.
Check the real X-Plane/data connection and stale-feed behavior separately.
This is research/prototyping software, not a certified flight instrument.

## Vendor references

- Varjo compatibility: https://developer.varjo.com/docs/unity-xr-sdk/compatibility
- Varjo Base 4.14: https://support.varjo.com/hc/en-us/release-notes-varjo-base-4.14
- XR-3 support boundary: https://support.varjo.com/hc/en-us/end-of-support-for-older-generation-models-xr-3-vr-3-and-varjo-aero
- Cesium 1.25.1: https://github.com/CesiumGS/cesium-unity/releases/tag/v1.25.1

## Windows startup serialization fix (2026-10-23)

A Windows player can crash at scene load with `level0 is corrupted` and
`Position out of bounds` when a component uses a scene-embedded MonoScript
instead of an imported source asset. Such components can appear to work in the
Editor while their fields are omitted from the built scene.

The affected flight-data provider and six radar/UI components now live in
matching `.cs` files with committed `.meta` GUIDs. The Experiment scene uses
those persistent script assets instead of its 22 embedded script records;
56 component references were repaired without changing their serialized values.

`WindowsPlayerScriptValidator` runs for every Windows x64 player build,
including ordinary Build Settings builds. It rejects missing scripts and
nonpersistent or mismatched script references with the scene and object name
before player scene data is written. Editor regression tests cover the repaired
scene and deliberately invalid references.

The corresponding installed Windows release was rebuilt and verified on the
4090 PC with direct EXE startup and the Windows file-open action, without
launcher arguments. The HUD rendered and the Varjo XR output initialized.
Physical headset image quality and calibration were not tested. This source
repair preserves this repository's existing component implementations.

The repository now resolves the official Cesium **1.25.1** registry package with
its generated/native artifacts, instead of importing the incomplete 1.20 source
checkout. That historical source is preserved under
`ThirdPartySource~/cesium-unity-1.20.0`; see the adjacent README before using its
vendor build instructions. Keep the registry package active when running the
Unity 6000.5 editor or building the Windows player.

Run the source regression suite with Unity Test Runner's EditMode filter
`FAA.Customization.Tests.WindowsPlayerScriptValidatorTests`. It validates the
seven script assets, ExperimentScene, enabled Build Settings scenes, and
deliberately broken references.

The configured Main scene also repairs a stale geographic-projection script GUID
and removes an unreferenced, fieldless missing-script slot. Its working traffic
data manager component is retained. The simulator UI source dependency graph is
tracked under `Assets/ThirdParty/XRInteractionSimulator`, with original GUIDs and
license information, so a fresh checkout retains its script and prefab bindings.
Legacy post-processing and lightning depth-texture declarations use Unity's
stereo-aware shader macros for Direct3D 11.

Validation: Unity **6000.5.10f1** compiled the repaired source and passed all
**16 EditMode regression cases**, including configured build scenes and
intentional invalid-script fixtures.

A Windows x64 Mono/Direct3D 11 build of ExperimentScene also completed with
**zero build errors**. Legacy SDK/deprecation warnings remain. Temporary build
verification scripts, player binaries, and local test logs are excluded from
source control.

The checked-in Windows player defaults match the verified profile: Mono,
Direct3D 11 only, and Graphics Jobs disabled for native XR output.

## XR-3 real-world passthrough and HUD size

Native Windows Varjo launches now enable mixed reality automatically. The
runtime waits for the native Varjo session, disables opaque rendering, starts
video passthrough, and clears the headset camera to transparent RGBA zero.
Only HUD canvas layers are rendered into that camera, so virtual terrain, sky,
and legacy camera effects cannot hide the real room. Render-to-texture cameras
used by instruments remain active.

Headlocked HUD canvases use a 1920 x 1080 reference layout spanning 60 degrees
horizontally (approximately 36 degrees vertically) at a 1.5 metre optical
presentation distance. This avoids scaling the HUD to the headset eye-buffer
resolution. Existing world-space cockpit panels retain their poses. The
presentation restores camera, layer, canvas, and scaler state when disabled.

`--xr3-vr` explicitly retains opaque immersive rendering. `--xr3-sim` opts out
of mixed reality for desktop simulator runs. Video passthrough requires an
XR-3 connected to a running Varjo Base session. The Unity desktop mirror can
show black behind the HUD because Varjo composites the camera video separately;
check the headset or Varjo Base headset view for the real-world image.

Run the EditMode filter `XR3MixedRealityPresentationTests` for angular size,
invalid-input handling, camera alpha/culling, world-panel preservation, and
state restoration. Startup logs prefixed `[FAA MR]` report whether the native
video renderer started and the active HUD geometry. A physical headset check
is still required to assess comfort, alignment, and real-world visibility.

Varjo setup reference:
https://developer.varjo.com/docs/v3.8.0/unity-xr-sdk/mixed-reality-with-varjo-xr-plugin

Validation on 2026-10-23: the Windows x64 Mono/Direct3D 11 release built with
zero errors. All 10 mixed-reality regression cases and all 16 Windows startup
regression cases passed in the GitHub source tree. On the 4090 PC, the native
Varjo API accepted video passthrough, the camera reported alpha zero and opaque
false, and Varjo Base's headset view showed the real room beneath the HUD.
Headset comfort and final apparent size still need a wearer check. Live flight
instruments remain unavailable until the X-Plane source is running.

## Centered preview, hand recognition, and panel ownership

The XR-3's left and right projections are asymmetric. A straight-ahead HUD
point appears near x=0.70 in the left-eye mirror and x=0.31 in the right-eye
mirror on the tested headset. A single-eye desktop screenshot therefore does
not measure binocular centering. The app now draws a centered desktop HUD
preview through its own mono camera and render texture, capped at 30 Hz. Its
framing fits the entire reference HUD with margin at different window aspects.
It does not replace either headset projection. Use `--xr3-eye-mirror` to inspect
the original eye mirror. Varjo Base continues to show its calibrated eye view
and the composited real-world video; headset comfort still needs a wearer test.

The passthrough presenter releases a canvas when the spatial workspace takes
ownership after startup. Radar/settings/utility panels keep their cockpit pose
and are no longer forced onto the forward flight HUD. Releasing also removes
the temporary camera parent while preserving the panel's world pose.

`XR3HandTracking` connects a native Varjo session to `LeapXRServiceProvider`,
reusing an existing provider when present. The XR-3 offsets are Y=-0.0112 m,
Z=0.0999 m, tilt=0 degrees. A visible status label reports detected left/right
hands. Frames older than 200 ms, missing sensor timestamps, disconnected
services, and invalid palm positions never count as recognition. Diagnostics
are written to `Application.persistentDataPath/XR3-hand-tracking.json`; they
contain connection state, frame age, and current/session hand counts, not images.

The compatible Ultraleap Tracking 7.3.0 package is embedded under
`Packages/com.ultraleap.tracking`, including original assets, GUIDs and licenses.
Its two removed Unity object-ID calls are replaced with object equality; see
`FAA_COMPATIBILITY.md` inside the package. Do not install a separate Gemini
runtime: the XR-3 service is supplied by Varjo Base.

On the 4090 PC the service reported one connected sensor and fresh frame
updates. Actual left/right recognition must be verified with hands in view;
service connection alone is not a successful hand-recognition test.

References:
- https://developer.varjo.com/docs/unity-xr-sdk/hand-tracking-with-varjo-xr-plugin
- https://developer.varjo.com/docs/v3.4.0/get-started/hand-tracking

## Live X-Plane 12.4.3 setup on 3080 (2026-10-24)

The Windows FAA PC connects directly to the simulator host using its existing
SSH alias `3080`. `Tools/XPlane3080/Connect-XPlane3080.ps1` binds only Windows
loopback ports 8086 (native REST/WebSocket API), 8767 and 12679 (read-only scenery).
The installed `FAA-XPlane3080-Tunnel` logon task runs this connection with the
existing user credentials and strict host-key checking. It retries disconnected
connections without changing firewall rules or copying credentials. Windows'
built-in OpenSSH is selected explicitly because another SSH executable on PATH
hung even during configuration inspection.

The simulator's read-only `xplane12-terrain.service` reads the installed Steam
scenery. Native Web API discovery selects the actual simulator automatically.
There is no synthetic flight feed and no dependency on the Mac remaining online.

The S-76C torque conversion now prefers `sim/flightmodel/engine/ENGN_TRQ[i]`
and `sim/aircraft/controls/acf_trq_max_eng`, both in the engine torque domain.
This avoids comparing per-engine transmission output against a prop reference,
which yielded an implausible ratio on the stock S-76. Invalid or missing preferred
metadata is not replaced with a different-domain reference. The displayed value
is a percentage of the simulator's configured engine torque limit.

The shared native schema includes those fields and accepts bounded signed pitot
IAS. X-Plane can report a small negative IAS at rest or in reverse flow; this no
longer discards otherwise healthy flight data. The raw sample is retained and
the existing display mapping still limits displayed indicated airspeed to zero.

`Tools/XPlane3080/start_s76.py` checks the installed stock aircraft and current
mouse-flight state. `--reset` explicitly replaces the current simulator flight.
The reset reloads the airframe to clear retained damage, loads KSEA runway 16L,
starts the engines while the simulation is running, releases the rotor brake,
sets flight throttle and checks that the aircraft remains parked. It pauses on
instability or manual mouse-flight input. It provides no takeoff or continuous
automatic flight control. Running it without `--reset` only checks availability.

Validation: 18 focused source tests passed in both source profiles, and the
updated Windows x64 Mono/Direct3D 11 player built with zero errors. The direct
SSH link returned native API version 12.4.3 and a real installed Seattle DSF tile.
Actual headset hand recognition remains a separate wearer test.

The effective failure configuration must be checked after flight initialization.
On this installed 12.4.3 build, supplying a zero MTBF through the flight JSON
left `enable_random_failures=1` and `mean_time_between_failure_hrs=0`, producing
an injected engine failure. The launcher now uses a positive interval, explicitly
disables random failures, repairs failed systems, and reads back the enable flag
before starting. It never treats the flight API's success response as proof that
the requested failure mode is disabled.

## Airborne demo and conformal rendering (2026-10-24)

The optional native HDG/IAS acquisition, checkpoint continuation and sustained
orbit workflow is documented in
[S-76 experimental controller](XPLANE_S76_EXPERIMENTAL_CONTROLLER.md).
It preserves the indexed legacy default described below. Deploy its complete
paired module set before selecting the new flags; the experimental path refuses
`--start` until fresh airborne initialization has separate native validation.

`Tools/XPlane3080/flight_demo.py --start` initializes an actual S-76C flight at
2,500 m MSL and 100 kt using X-Plane's native flight API. Subsequent motion comes
from simulator physics, native heading/altitude hold and a rate-limited collective
speed assist. It never writes a synthetic flight stream or overrides aircraft
physics. The user service runs without `--start`, attaching to the existing
S-76 flight rather than repeatedly repositioning it. Clearance, attitude,
engine, fuel and manual-input checks pause the demo on an unsuitable state.

The full integration profile's angular conformal mesh and native text were on
the default world layer, which the mixed-reality camera excluded. They now use
the HUD layer. The per-camera visibility callback includes the explicitly owned
centered preview, and that preview retains the headset far clip instead of
clipping the 4 km angular shell at 2 km. Actual calibrated headset projections
and opaque virtual-world culling are unchanged. The exact corrected full-profile
implementation is preserved under `SourceProfiles~/XR3Integration` because the
standard scene profile does not include its workspace dependencies.

The final standard-profile suite passed 22 tests with two optional full-profile
tests explicitly skipped. The updated Windows player built with zero errors.

The native attitude-window bounds are projected from the physical fixed-angle
HUD canvas. Full-eye-buffer viewport fractions are inappropriate once that HUD
occupies only part of the XR field of view: they allowed pitch labels to cross
the bank/FMA, heading and hand-status lanes. The projected window now bounds the
ladder, while its world angles remain unchanged. The hand-status label also
registers an optional chrome keep-out in the full integration profile. All 114
selected full-profile conformal/window tests passed after this correction.


## Desktop mouse buttons in native XR-3 mode (2026-10-24)

Left-click the visible controls inside the FAA desktop window. The centered
preview and the headset eye use different projections. A dedicated desktop
pointer camera now matches the preview in desktop window pixels, including
when the render texture is downsampled. Its canvas raycasters retain Unity's
normal button, slider, scroll, mask and CanvasGroup filtering. The original
raycasters are restored when the preview closes. Tracked XR input and the
workspace hand-pointer IDs keep their original headset camera.

The full integration chrome's menu-inside/outside check uses the same desktop
projection, so menu buttons remain usable after opening the flyout. The matching
full-profile chrome source is preserved in `SourceProfiles~/XR3Integration`.
Rendering/pointer regressions passed all 8 selected full-profile tests; the
standard profile passed 20 with two optional full-profile tests skipped.

The simulator air-start request allows up to 60 seconds for scenery loading;
ordinary data requests retain a five-second timeout.


## Remote XR-3 verification and recovery (2026-10-27)

The native cockpit camera now has a separate aircraft tracking origin. Validated
nose-up pitch, roll and true heading move that parent; the HMD pose driver keeps
ownership of its local pose and calibrated eye projections. While the headset
is idle, a provisional body frame supports the forward preview without claiming
tracking or seat calibration. The first valid tracked sample calibrates the
physical seat once. Temporary tracking loss preserves calibration; manual seat
recenter requires valid tracking. Cockpit panels refresh their seat reference
only on origin/calibration changes.

The desktop preview uses a normal enabled mono spectator camera and the same
camera for mouse hit-testing. It renders directly to display zero. The old
manual camera render and opaque IMGUI image could cover headset passthrough;
this separate pass leaves the native eye surfaces alone. MR routing preserves
the owned spectator and periodically reasserts video/alpha settings after a
headset/runtime reconnect.

The hand provider continues bounded reconnection attempts and waits for an
advancing frame timestamp after recovery. Focus loss keeps an existing pinch
release latch until a real unpinched sample, preventing a new press on refocus.
Read-only local evidence is written to `XR3-runtime-report.json`; unavailable
provider statistics are explicitly marked unavailable. `--xr3-audit` also writes
before-render phase breadcrumbs for diagnosing native rendering faults.

The S-76 helper waits for advancing native flight time, valid IAS and stable
engines before selecting HDG/ALT. Its service uses `--route --collective-assist`:
bounded Puget Sound waypoints, gentle heading commands (at most 0.5 degrees per
second and five degrees from current heading), and rate-limited collective
feedback with native autothrottle off. It never writes synthetic telemetry or
overrides physics. Only explicit `--start` sets initial conditions; service
restart attaches to existing flight and recovers validated waypoint progress.

On the stock S-76, collective initialization, assist writes and reported
`collective` feedback address only `sim/cockpit2/engine/actuators/prop_ratio`
index 0. The combined `prop_ratio_all` getter can change as the independent tail
control changes; writing that combined handle also addresses the tail control.
It remains a separate `aggregatePropRatio` diagnostic and never seeds or drives
the collective controller. Reports include `collectiveDataref`,
`collectiveIndex` and the sampled `values.mainCollective` to make the axis explicit.

The indexed transport uses X-Plane's documented REST `?index=0` parameter: an
indexed GET returns a one-element array, and PATCH sends one scalar. It never
reads and replaces the full handle array. See the
[Laminar Web API specification](https://developer.x-plane.com/article/x-plane-web-api/).
This axis correction alone does not validate the existing HDG/ALT startup,
control gains or sustained waypoint flight; those require separate native
flight testing. It is also independent of the unresolved Unity runtime crash.

Safety stops publish an atomic terminal report and specific reason. The verifier
rejects empty, malformed, frozen, future or nonfinite reports and requires
advancing simulation time and physical movement. Deploy `flight_demo.py`,
`start_s76.py` and `verify_flying.py` together because the helpers share code.

The full integration suite passes 1,145 tests. The public imported profile
passes 538 tests and skips two optional full-profile cases. Python reliability
coverage contains 41 passing cases. A headset wearer is still required to
validate optical alignment, comfort, real hand recognition and physical control
interaction. An idle sensor connection is not a successful wearer test.

Vendor references: [Varjo mixed-reality setup](https://developer.varjo.com/docs/unity-xr-sdk/mixed-reality-with-varjo-xr-plugin)
and [Laminar autopilot dataref semantics](https://developer.x-plane.com/article/accessing-the-x-plane-autopilot-from-datarefs/).


Controller recovery also handles a connected service with no device or a frozen
frame stream: after five continuous unhealthy seconds it recreates that SDK
controller on the main thread, at most once per five seconds. Native gestures
prefer the same fresh provider used by the hand-status label. This does not
restart vendor services. A controlled off/on test exposed a separate Varjo
backend reconstruction stall; only that supervised hand-tracking process was
restarted, preserving the HMD server and compositor.
