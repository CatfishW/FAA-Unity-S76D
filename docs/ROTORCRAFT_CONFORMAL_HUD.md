# Rotorcraft conformal HUD / HMD

Implemented in `/Users/zlad/Development/FAA` for the FAA / S-76D research simulator.
This is not an FAA-approved instrument, an EFVS operational approval, or a landing-safety system.

## Presentation

`FaaConformalHudController` automatically installs `FaaRotorcraftConformalLayer` in Play mode
when the presentation is **Conformal**. No scene rewrite is required. `HeadFixed` restores
the existing presentation; the controller's `useRotorcraftSceneLayer` field also preserves
the earlier group-projection compatibility path. Existing instrument readouts, bank indication,
navigation command/deviation cues, heading tape, weather, traffic and Pilot Brief remain separate.

| Reference | Implementation |
| --- | --- |
| Horizon | Full-FOV earth horizon built only where it is on screen. Symmetric gap around the aircraft reference equal to the rung inner end (at least 2.8° and 1° beyond the waterline wing tips, about 3.5° on the desktop), plus gaps around every protected HUD element (`FaaHudKeepOut`, Symbology and Chrome) and around the drawn waterline and FPV. Pieces shorter than 1.5° are left out, so no slivers remain between gaps. Full intensity inside the attitude window, 70% outside. When the true horizon is off screen, a **dashed horizon** is clamped just inside the attitude-window edge nearest to it, parallel to it, with an open caret pointing toward it (`HorizonOffScale`). |
| Pitch ladder | 5° rungs confined to the **attitude window** (see below). Constant screen-angle rungs (no shrinking at high pitch), positive solid, negative dashed, hooks toward the horizon, numerals at both rung ends, rotated with the rung and never upside down. A rung is drawn **only as a complete pair**: both halves (inner end, outer end and hook) inside the window and both numerals placeable inside it, clear of other labels, the waterline and the FPV. Otherwise neither half is drawn, so there are no one-sided, unlabelled or edge-faded rungs. Rungs break around the waterline and FPV. No half-step ticks. Narrow windows (Classic dials) shorten the rungs so a centred pair still fits. |
| Aircraft waterline | Gull-wing "W" (V depth ≥ 6 mrad) built from validated pitch/roll/true heading (`FaaRotorcraftCueMath.BodyRotation`), **never** from the OwnAircraft transform, whose pitch sign is inverted. Positive pitch puts it above the horizon. Distinct from the circular FPV, and cut under the FPV ring, wings and fin when the two meet, so they never read as one glyph. **Never dropped**: when it does not fit inside the attitude window it is pinned just inside the nearest edge, dashed and at 70% intensity (`FaaHudStyle.MinQuietAlpha`, as the limited FPV), keeping its screen roll, and `DataStatus` adds `W LIMIT` (`WaterlineVisible`, `WaterlineLimited`). |
| FPV / FPM | Circle (34′), wings and upper stem on the measured ground-velocity direction; never heading as a substitute for track. Inside the window only; a FOV-limited FPV is drawn as a dashed ghost ring on the window edge (`FPV LIMIT`). Behind/out of view it is hidden and annunciated (`FPV OUT OF VIEW`). No velocity in forward flight: amber `FPV` flag. The FPV is drawn whole; lines and the waterline break around it. |
| Selected FPA | Pilot reference (initially −3°, −15…+10° in 0.5° steps), **approach context only**: HAGL ≤ 1500 ft or G/S active/armed with FD/AP coupled. Cyan long dashes (distinct from the green dashed negative rungs), clipped to the window and broken around the waterline/FPV; label `FPA −3.0°`, measured from the font, right of the reference or else mirrored left, inside the window and culled if it would overlap a numeral. Not a flight-director command. |
| Landing / helipad | Explicit selected scene point plus declared area outline. The outline dimensions must be authored; a pilot raycast creates a declared 20m box, not a surveyed helipad. |
| Waypoint / obstacle | Existing scene-transform anchors; diamond waypoint, amber obstacle triangle, optional second endpoint for an authored wire/linear obstacle. Symbols and line pieces inside protected HUD areas are left out. Only the 8 nearest cues are labelled; distances in FT (50 ft steps) below 0.3 NM, else NM. |
| Hover | Head-fixed plan-view velocity instrument, right/forward axes relative to aircraft heading, placed at the first candidate position clear of the attitude window and every protected area. Label `HOVER 10 KT` / `DRIFT >10 KT`. A separate selected geographic hover anchor is world-referenced. |

### Attitude window

Start: `FaaHudKeepOut.AttitudeWindow(width, height)` (a registered AttitudeWindow region, otherwise the default viewport
(.37, .33, .26, .43), whose top at 0.76 of the screen height stays below the roll-scale end marks).

The window is **built around the projected waterline**: it is extended toward the waterline symbol, ±10° of pitch around it and
room for a centred rung pair (`FaaRotorcraftCueMath.GrowToward`), never above its own top and never wider than 0.30–0.70 of the
screen width. Every protected rectangle (padded by 8 reference units) that overlaps the result and lies left, right, above or below
the **attitude-field centre** (the start window's centre) then pushes the nearest edge past it, choosing the side that loses the
least area. Deciding the side from the field centre means a window grown toward a low waterline stops above the heading scale
instead of jumping below it. This clears the IAS/TQ column, ALT/VSI/NR column, the G/S scale when it is shown, the FMA row, the
heading scale and the bottom chrome bar, in Digital and Classic alike. A rectangle that covers the field centre is ignored (attitude
keeps priority). Below 10% × 16% of the screen the default viewport is used. Growth follows flight data and is never eased; other
inward edge changes snap (nothing is ever crossed), outward changes ease with an 80 ms time constant, and sub-pixel changes are
ignored, so the window never jitters. `AttitudeWindowPixels` exposes the result. If the waterline is still outside (behind a protected
element, above the window top, or the pilot is looking well off the boresight), it is pinned and ghosted as described above.

The ladder and waterline are centred on the validated **heading**, which is the aircraft boresight azimuth. The desktop camera rig
takes its yaw from the aircraft transform, which the bridge drives from the same heading, so with no look offset the ladder is on the
screen centre line. A look offset (free look, an inspection turn, an eased FORWARD return) moves the view, not the ladder: this is the
correct conformal behaviour, and the window then grows toward the waterline. `ViewHeadingOffsetDegrees` (camera forward minus heading,
including the look) and `BoresightHeadingOffsetDegrees` (the rig's aircraft reference minus heading) expose the relation; a boresight
difference above 3° that persists for 2 s is logged at most every 30 s, because it means the view and the data disagree.

While the rotorcraft layer is active, `FaaConformalHudController` holds `AircraftCameraController.FlightPathBlend` at 0, so the
cockpit view stays on the airframe axis instead of being pitched 60% toward the flight path, and restores the previous value when the
layer is not active, the camera changes or the controller is disabled. Note: the bridge still sets the OwnAircraft rotation with an
inverted pitch sign (`Quaternion.Euler(data.pitch, ...)`), and the camera rig derives its pitch from that transform. Until that is
fixed, the view axis sits at −pitch, so the waterline is drawn about 2 × pitch from the screen centre; the window follows it.

### Declutter and failure states

| State | Behaviour |
| --- | --- |
| Unusual attitude | Engage at \|bank\| > 60° or pitch > +30° or pitch < −20°; release only when \|bank\| < 55° and −15° < pitch < +25°. Keeps what recognition and recovery need (standards 2.7): the horizon (clamped and dashed when off screen), the ladder, the waterline (pinned if needed) and **red recovery chevrons** on the 10° rungs beyond the thresholds, pointing to the horizon and drawn even where their rung pair does not fit. Removes the FPV, FPA, hover instrument and every scene reference (waypoint, LZ, obstacle symbols and labels). `FaaRotorcraftConformalLayer.UnusualAttitudeActive` lets other layers declutter together. |
| Classic local attitude | While `FaaSpatialWorkspace.UsesClassicAttitude`, no conformal horizon, ladder, waterline, FPV or FPA is drawn over the non-conformal inset. Georeferenced scene cues remain. |
| Panel inspection | Every vertex and label alpha is multiplied by `FaaSpatialWorkspace.ForwardIntensityFor` (the shared `FaaHudInspection` fade, 16% while a side panel is inspected). |
| Attitude invalid / stale | No horizon, ladder or FPV. A **red boxed `ATT`** flag appears at the window centre. |

Text and strokes are sized by visual angle at the eye: 28′ cap height for HUD alphanumerics, about 1 mrad lines (1.2 mrad for the
horizon, waterline and FPV), with a dark halo for contrast. In native XR camera degrees are eye degrees; on the desktop the angle
is converted through the camera FOV and the 700 mm / 27-in 4K design eye used by `FaaHudStyle`. The mesh is rebuilt only when an
input changes and the label pool reuses stable slots, so nothing allocates per frame. References: AC 25-11B 5.10.1.2, 5.10.3.2,
A.2.2, A.7.3, F.3.3.3, F.4.4–F.4.5, F.5.4.6.2–3; AC 23.1311-1C 17.4; HF-STD-001B 5.3.3.2.6/.9.

Angular geometry is placed on a distant, camera-centered **world-oriented** shell. This avoids
translating the pitch/FPV with a screen-space instrument group. Geographic references retain
their actual scene-relative position and depth. Heading look offsets therefore change the
view of these references without dragging the fixed data around the screen. World-space labels
use native TMP text meshes and a resource-backed overlay material. They are sized by measured
font metrics and do not depend on per-camera Canvas batch rebuilding.

## Controls and authoring

The duplicate green reference/status dock has been removed at the pilot's request. No FPA/SCENE/HOVER/MARK LZ button strip is instantiated. The public `SetSelectedFpa`, `SetFpaVisible`, `SetSceneCuesVisible`, `SetHoverVisible`, `MarkLandingReference` and `ClearLandingReference` APIs remain for integration and inspector-driven configuration.
`MarkLandingReference` uses the camera's forward ray against loaded colliders, ignores own-aircraft children,
and declines placement when no suitable geometric intersection exists. It does not load missing terrain,
check rotor clearance, measure slope suitability, detect wires, or authorize a landing.

For persistent authored references, select a real scene object and use
**FAA > HUD > Scene Reference**. Choose Landing Area, Waypoint, Obstacle or Hover Reference.
Set the identifier, validity, declared area, optional line endpoint, range and selection in the
`FaaRotorcraftCueAnchor` Inspector. Landing/hover references require selection. These commands
use Undo and do not silently save your scene. Keep anchors under the same terrain/georeference
hierarchy as their physical objects, so floating-origin shifts move them together.

The existing radar selection supplies latitude/longitude but no target altitude. It is therefore
**not automatically converted into a 3D landing point at an invented elevation**. Likewise no
unverified obstacle database or synthetic wire detections are introduced by this change.

## Data and failure handling

The bridge now distinguishes true ground track (`sim/flightmodel/position/hpath`) from magnetic
heading (`mag_psi`). It uses ground speed and vertical speed to construct the ground-velocity
vector. The remote XPlaneConnect relay source also reads actual `hpath` and `y_agl`, instead
of substituting heading, an estimated AGL, or a linear approximation to flight-path angle.
The relay source was edited locally only; its aircraft-control loop is not launched by HUD setup.

New `AviationFlightData` validity flags distinguish missing attitude, velocity and AGL channels
from real zero-valued samples. Track interpolation recovers after an unavailable/NaN sample.
Other data producers must populate these flags before the new conformal layer treats them as valid.
Legacy and UI Toolkit FPV paths are guarded against invalid track values as well.

The conformal layer requires the bridge's healthy-feed flag and finite packet age no greater than
one second. This is a packet-level gate, not an independently certified per-sensor integrity system.
Stale/invalid attitude clears the conformal attitude geometry and shows a red boxed `ATT` flag. Missing ground
velocity suppresses FPV without inventing forward motion and shows an amber `FPV` flag. An FPV behind the view is
hidden and annunciated; an FPV outside the attitude window is drawn as a dashed ghost on its edge, never as a
normal FPV at a misleading position. Unknown engine values display `--`.
The new readout says **HAGL**, because `y_agl` does not establish a radar-altimeter sensor reading.

Hover enters below 5kt and exits at 8kt to avoid threshold flicker. Its vector supports lateral and
rearward motion and displays an off-scale indication rather than a clamped valid marker. These
thresholds, full-scale range and FPA bounds are simulator design choices, not FAA-mandated limits.

## Coordinate / XR assumptions

The default local tangent axes are Unity X east, Y up, Z true north. Assign `earthFrame` when the
scene uses a rotated tangent frame. Scene units must represent metres for anchor ranges and areas.
The render callback updates from the final camera pose; duplicate refreshes with identical frame
and pose are avoided. The dynamic line mesh and label pool are reused rather than recreated each frame.

The geometry uses native Unity world rendering with stereo shader support; it is not a mono
screen projection pasted over both eyes. The finite angular shell approximates, rather than proves,
optical collimation. Native XR controls use a world-space canvas and TrackedDeviceGraphicRaycaster;
the existing XR-3 compatibility component remains responsible for the fixed flight canvases.
The rig's XR input module/interactors, eye calibration, head-tracking latency, binocular registration,
near-field comfort and performance still require actual headset validation. No XR-3 player build or
hardware qualification is implied by a desktop Editor test.

## Validation

`Tools/ExplanationVerification/RunRotorcraftAssertions.cs` runs focused NUnit methods directly
inside the existing Editor. The final measured result is **142 passing Edit-mode assertions**,
including 37 new rotorcraft cases and 105 existing HUD/engine/view cases.
`RunRotorcraftRuntimeAssertions.cs` adds **27 passing isolated Play-mode component and UI
assertions**, including calibrated native text size and real-collider landing mark/clear interaction.
The total is **169 passing focused checks**, with a successful final Unity script compilation. These
are direct Editor assertions, not a complete Unity Test Runner/XML result or a whole-project test run.
Machine-readable final results and desktop captures are stored under `artifacts/rotorcraft-hud-20260924/`.
The attitude-window overhaul adds `Assets/_Project/Tests/Editor/FaaConformalWindowTests.cs` (waterline sign, window
shrinking/smoothing, horizon gap, screen-angle rungs, upright numerals, unusual-attitude hysteresis, approach-only FPA,
visual-angle sizing, ft/NM distances, projection round trip, an isolated layer fixture and the heading-root restore guard)
and updates the Play-mode fixtures for the new label size, the boxed `ATT` flag and the Classic suppression. The review fix
wave adds tests for the window grown toward the waterline and kept on the field side of protected elements, the pinned and
ghosted waterline (`W LIMIT`), rungs only as labelled pairs (level and banked), the waterline/FPV occlusion bands and rung
inner end, the clamped off-screen horizon, interval subtraction, and the FlightPathBlend hold and restore. The Play-mode
fixture checks the same behaviour plus the live FlightPathBlend and the unusual-attitude removal of scene references.

Desktop Play-mode checks use the existing scene and live X-Plane feed for forward-flight rendering.
Synthetic hover/stale/scene-reference cases are isolated fixtures, not claims about live flight states.
No unknown real landing areas or obstacles are inserted into the user's scene. The current scene can
report missing terrain coverage independently of this HUD; the new controls do not conceal that state.

The pre-existing dirty scene and worktree are preserved. No automatic scene save, commit, push,
remote relay deployment, simulator repositioning or release is part of this change.

## Implementation references

- X-Plane developer documentation, *Moving the Plane*: world velocity, derived speed/path values,
  and local/geographic coordinate relationships: https://developer.x-plane.com/article/movingtheplane/
- Unity XR Interaction Toolkit, *Tracked Device Graphic Raycaster*: canvas pointer interaction:
  https://docs.unity.cn/Packages/com.unity.xr.interaction.toolkit@3.1/manual/tracked-device-graphic-raycaster.html

These are implementation references, not a statement of regulatory compliance.
