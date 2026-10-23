# XR Interaction Simulator runtime dependencies

This directory vendors the runtime dependency graph required by
`Assets/Resources/FAA/XR3/XR Interaction Simulator.prefab` and the FAA scenes.
The source is Unity XR Interaction Toolkit **3.6.0**, sample
`Samples~/XR Interaction Simulator`, from the resolved package cache.

The three runtime C# scripts, UI prefab, input actions, hand expressions,
textures, cursor assets, and their original `.meta` files are preserved
without changes. The runtime assembly definition retains its original name
and GUID, with one project-specific adjustment: its optional
`Unity.XR.Hands` assembly reference (`ce522b6ed64c8be4c989a1d26d0e3275`)
is removed because this project does not install `com.unity.xr.hands`.
The sample guards all direct XR Hands API usage with package version defines.
If that optional package is installed later, restore the original assembly
reference when enabling its hand-visualization integration. Their original asset GUIDs are required
by the existing simulator prefab references. The assembly name is also kept
because the toolkit exposes internal APIs to this sample assembly.

These dependencies live in a tracked directory because `/Assets/Samples/` is
ignored by this repository. Do not additionally import the XR Interaction
Simulator sample into this project: that would duplicate the asset GUIDs.
The sample editor validation scripts and unrelated sample assets are omitted.
Four referenced shared sprites are also preserved from the same package: the
three hand-expression icons from `XR Device Simulator/UI/Hands` and
`Starter Assets/DemoAssets/Sprites/Round Radius 4 Outline.png`.

Copyright Unity Technologies. See `LICENSE.md` for the Unity Companion License
notice supplied with XR Interaction Toolkit 3.6.0.
