# Near-space HUD geometry and complete bounds

Explicitly owned native-MR canvases can use Unity's relative view/model matrices while their render coordinates are near the origin. Registration ties each root to an active camera owner, its parent and lifecycle. Close coordinates alone do not enable this path. Unregistered, far, unrelated, disabled, reparented and different-owner roots retain the original manual geometry path.

The shared helper retains the actual view and calibrated projection, pixel rectangle and inverse click calculations. It does not change stereo matrices, tracked local head pose, aircraft/seat data or geographic target values. The local-canvas path preserves the exact depth < 128 contract and accepts small/negative scales; singular geometry falls back. Registration is released on owner/view teardown and must be acquired again on reconnect. The implementation has no cross-frame matrix cache.

`TryProjectLocalBounds` captures projection state once for all four rectangle corners. The full integration's module and keep-out consumers use this API. The former diagonal-only module bounds could collapse at 45 degrees or underestimate a sheared rectangle, allowing protected instruments to overlap. Existing visibility, behind-plane, padding and desktop fixed-camera policies are retained.

## Profiles and tests

The standard imported profile contains `AircraftViewRenderSpace`, `XR3MixedRealityPresentation` and `IndicatorCanvasProjection`. Their pre-change sources matched the integration baseline byte for byte. This patch changes only these shared files in Assets; it does not import the optional spatial workspace or replace its earlier controller/chrome dependencies.

All six final runtime implementations are preserved exactly in `SourceProfiles~/XR3Integration`, including the previously missing `FaaCanvasLocalGeometry`. Full fixtures and archived namespace-isolated manual test helpers are preserved under its ignored `Tests~` directory. The imported standard test variants cover the actual shared registry, projection, inverse-click and batched-bounds APIs. They omit assertions against absent optional consumers, explicitly skip the region fixture and actual-module benchmark, and use the common near-local API in their GPU/geometry benchmark. Full integration tests retain the complete consumer/visibility/zero-scale fallback assertions.

The final private integration candidate passed 71 focused checks, including the selected normal enabled-camera GPU and 24-root cost tests under Unity 6000.3.27f1. The standard-profile port then compiled and passed 840 functional checks with zero failures in an isolated copy. Its 15 suite skips comprised six absent optional-profile cases and nine explicit graphics/benchmark methods. A separate normal-graphics run passed four supported checks (large-origin presentation, new projection/inverse/AABB rendering, representative 24-root geometry cost and settings text raster); four full-profile-only consumer/layer/shader checks explicitly skipped. No runtime C# change was needed for the standard port. The tests exercise explicit ownership, tiny/negative/zero scales, nested/different owners, release/reconnect/reparent, custom/asymmetric views, depth limits, four-corner rotation/shear, visibility, behind-plane handling and normal-frame raster bounds. GPU tests use ordinary enabled-camera rendering, never `Camera.Render`.

## Measured cost and limits

The paired Mac Editor benchmark used 24 registered roots, 24 moving aircraft poses, 24 dynamic graphics and alternating measurement arms. Actual WorldSpace module bounds took median 103.700 microseconds for the archived two-corner loop, 197.925 for a correct four-corner manual baseline and 51.425 for the batched four-corner implementation: 50.4 percent lower cost than the original cheaper loop. At zero rotation all three bounds agree; during rotation only the complete four-corner results are required to agree. The separate generic four-call geometry bundle measured 7.945 versus 7.882 microseconds, which is parity within noise. The earlier single-root speedup is not a representative 24-root claim.

The enabled-camera 80-frame check measured maximum 0.567 pixel center error and 1.072 pixel four-corner bounds/raster error, while preserving the calibrated projection and inverse-click geometry. The JSON receipts are in [the validation folder](validation/xr3-near-space/README.md).

These are measured geometry/rendering results. They do not establish native crash repair, a Windows frame-rate target, sustained-flight endurance, or physical headset/hand/optical acceptance. Native matrix returns remain in use; the original manual fallback remains available.

## Native runtime status on 2026-10-30

An ordinary uninstrumented native launch (PID 30908) ended with `0xc0000005` after 493.3 seconds, before its planned close and without diagnostic watcher/input/recording activity. This confirms that the unresolved native failure can occur outside a diagnostic watch session; its default crash capture does not establish the original writer or cause.

The optimized candidate subsequently completed separate 15-minute and 12-minute watched native trials with normal exit code zero and no captured access violation. Remote checks exercised Help and Commands open/close, cue and Brief panels, real traffic cue OFF/ON, and flight-HUD OFF/ON with the intended toolbar/traffic separation. These bounded successes do not establish crash resolution or endurance. Overall Windows FPS improvement, physical headset alignment/hand recognition and full video acceptance are not claimed by this source change.
