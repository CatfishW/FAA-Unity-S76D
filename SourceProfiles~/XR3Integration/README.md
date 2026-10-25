# Full XR-3 integration source profile

This folder preserves corrected source from the archived full integration project used for the installed Windows release. The repository's standard scene profile uses earlier canvas-based conformal components and does not import the optional spatial-workspace dependencies of this implementation. Unity ignores the `SourceProfiles~` directory.

`FaaRotorcraftConformalLayer.cs` is the exact implementation built for the deployed full integration profile. Its native mesh and world-text objects use the FAA HUD layer, and its camera callback admits the explicitly owned centered preview camera. These fixes preserve the terrain-free mixed-reality culling policy and the headset's calibrated stereo projection.

The shared imported `XR3DesktopHudPreview` retains the headset's far clipping range so its distant angular cue shell is visible. Full integration rendering regression tests run against the archived integration project; the standard-profile test suite explicitly skips the two optional full-profile tests. The native attitude window is projected from the physical HUD canvas to keep pitch rungs clear of fixed instrument lanes.

`FaaPilotChrome.cs` preserves the full-profile menu hit-test correction: desktop inside/outside clicks use the centered preview pointer camera, while the world canvas retains its headset camera.
