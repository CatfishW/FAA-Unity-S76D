# Complete full-profile overlay

Use [the materializer guide](../../../docs/XR3_REPRODUCIBLE_PROFILE.md) and
`Tools/prepare_xr3_profile.py` from a committed profile revision. This directory
contains the complete reviewed delta, not a set of independent replacement
scripts. Do not copy individual files into the standard imported project.

`profile-manifest.json` pins the committed baseline trees, all 10,336 resulting
source inputs, 769 overlay payloads, 145 output-only removals, exclusions and the
official Cesium dependency. `files/` retains original destination paths and Unity
metadata. Preparation checks every byte, uses only committed Git/LFS objects,
and writes only a new dedicated output directory. The existing standard Assets,
Packages, ProjectSettings and flattened profile snapshots remain untouched.

The initial profile records the frozen 5f source. Actual Unity validation of the
new materialization route and separately approved lifecycle payload updates are
subsequent review gates. No successful materialized build or crash repair is
claimed by adding this bundle.
