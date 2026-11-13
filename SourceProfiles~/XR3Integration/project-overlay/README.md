# Complete full-profile overlay

Use [the materializer guide](../../../docs/XR3_REPRODUCIBLE_PROFILE.md) and
`Tools/prepare_xr3_profile.py` from a committed profile revision. This directory
contains the complete reviewed delta, not a set of independent replacement
scripts. Do not copy individual files into the standard imported project.

`profile-manifest.json` pins the committed baseline trees, all 10,348 resulting
source inputs, 784 overlay payloads, 145 output-only removals, exclusions and the
official Cesium dependency. `files/` retains original destination paths and Unity
metadata. Preparation checks every byte, uses only committed Git/LFS objects,
and writes only a new dedicated output directory. The existing standard Assets,
Packages and ProjectSettings remain untouched.

The complete repaired source at commit `54fc2c03` was materialized and independently
verified against every expected file, size, SHA-256 and executable flag. All
1,632 ordinary full-profile tests passed, with 11 explicit GPU/benchmark skips;
the Windows x64 build succeeded with zero errors. Its normal remote 30-minute
XR-3 runtime check exited cleanly. Completed log review found no reproduced
exception or camera teardown warning; residual font/network warnings remain.
Separate 30-, six- and ten-minute S-76 journal audits passed and ended paused.
See [the validation note](../../../docs/XR3_CLASSIC_REMOTE_VALIDATION.md)
for the measured desktop UI scope, complete planned core workflow recording and
separate supplemental cue-visibility stills. The original native corruption writer and physical wearer checks remain
unresolved or unperformed respectively.

`approvedRepairs` pins the 24 repair paths and two Editor-only raster fixture
paths. `flattenedSnapshots` identifies the matching 46 source snapshots and
metadata. The generated manifest records source-preparation state at its commit;
subsequent test/build/runtime receipts are summarized in the validation note.
