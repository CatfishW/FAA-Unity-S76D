# XR-3 rendering-origin stability

The current project targets Unity6000.3.27f1 LTS. Native Varjo passthrough remains the ordinary Windows launch mode; `--xr3-sim` explicitly selects the desktop simulator.

## Corrected behavior

A headlocked world-space HUD previously inherited the virtual aircraft's position tens of kilometres from the Unity origin. Although its local layout was unchanged, float precision in the rendered transforms moved static gauge rims and thin controls several pixels between frames. Normal enabled-camera GPU rendering reproduced the issue in both6.5 and6.3.

The active mixed-reality view now renders near the origin. `AircraftViewRenderSpace` owns a translation for that camera and its explicitly bound desktop preview. The camera controller commits that translation with each native rig pose. Geographic aircraft state, traffic records and physics objects retain their original data-world coordinates.

- Target projection, legacy glyph orientation and navigation lights convert data-world points into the view's render space.
- Weather targets convert a cached camera reference back into data space before constructing their positions.
- Scene references retain geographic positions; scene geometry uses relative vectors, and landing-reference raycasts start in data space. The before-render cache also detects an offset change.
- Spatial panels and hand/UI rays stay in render space. The optional legacy conformal canvas uses an owned stable render frame with mode, disable and scene restoration.
- Headset local tracking pose, seat calibration, body attitude and calibrated stereo projections remain provider-owned. Other cameras and ordinary desktop/VR coordinate behavior retain their existing path.

The projection helper is the original `bb754107` implementation. NoOptimization/NoInlining and the local-accumulator diagnostic experiments are not presented as repairs.

## Source and package profiles

Imported standard-profile files retain their existing dependencies. Exact optional full-integration implementations and tests are kept under the Unity-ignored `SourceProfiles~/XR3Integration` directory. The LTS manifest uses UGUI2.0.0, InputSystem1.20.1, Burst1.8.31 and the VR module required by XRSettings/XRStats; equivalent object-search API spellings preserve behavior. The standard package lock preserves its existing Cesium source.

## Verified checkpoint

As of2026-10-30UTC:

| Check | Result |
| --- | --- |
| Full integration functional suite |1561passed,0failed;6explicit GPU cases run separately|
| Dedicated integration GPU checks |6passed, including normal-render original/near/restored comparison|
| Standard-profile suite in isolated clone |819passed,0failed;10 documented optional-profile/explicit GPU skips|
| Standard normal-render origin regression |Passed separately|
| Windows Mono/D3D11 build |Succeeded with0errors|

Across120normal-render samples per arm, the original/restored gauge center ranged561–565px with3.5px maximum adjacent movement. The repaired presentation stayed exactly563px with zero measured movement. Local headset pose and asymmetric projection were asserted unchanged.

Native Windows validation is ongoing. The frozen LTS control completed a20-minute diagnostic interval with no captured access violation, but its final minutes lost live flight data when the simulator's guard paused. That is not the final native crash/endurance or input-acceptance result. Physical wearer optics, hands and comfort have not been tested.

## Diagnostics

`--xr3-ui-audit` observes final UI-module/raycast/click routing; it never invokes a UI action. `--xr3-transform-jit-audit` records the already-used helper's metadata and bounded entry bytes once. Both are opt-in. Runtime reports distinguish the near render eye, its data-world offset and the logical data-world eye. Source-health logging records loss and recovery without extending the one-second freshness limit.

The settings text factory also checks whether its actual style/fallback chain can supply an ellipsis. Synthetic bold in the shipped font previously failed that lookup and repeatedly fell back to truncation. Keeping that effective mode when unavailable removes the repeated warnings while retaining the same font, weight, pixels and complete wrapped guidance. Eight normal-render frames were byte-identical before and after, with six full-profile/seven standard-profile warnings reduced to zero.

The standard test assembly explicitly references InputSystem and XR Interaction Toolkit. The existing committed mouse/XRI regression failed compilation without these two references; the minimal additions are included with the source repair.

The HUD hide/show command also covers the independently rendered conformal layer. Its visibility check reads current active ancestor CanvasGroups, including groups created after binding, and the final render pass admits a visibility change even when the pose has not moved. The actual command/render regression measured142 ATT-warning pixels when visible, zero while hidden and142 after restoration. The warning remains legible at the existing35% brightness floor. Traffic/weather cues and chrome retain independent ownership. Six dedicated integration GPU checks pass; the standard profile explicitly skips the two optional modern-layer tests.
