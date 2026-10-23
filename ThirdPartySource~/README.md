# Preserved third-party source

`cesium-unity-1.20.0` preserves the original embedded Cesium source without
changing its implementation or license. It is retained for reference and is
not imported by Unity from this directory.

The project resolves the official Cesium **1.25.1** package through the existing
Cesium scoped registry. This package includes the generated bindings and native
libraries needed by Unity **6000.5.10f1**. The former 1.20 source checkout lacked
its generated Reinterop assembly, and the 1.20 release bindings also reference
Unity APIs that are errors in this editor version.

The archived vendor development instructions describe an actively embedded
package at `Packages/com.cesium.unity`. To develop that historical source, restore
it as an embedded package in a separate checkout and follow those instructions
with its matching toolchain. It must not override the resolved 1.25.1 package in
the current project.
