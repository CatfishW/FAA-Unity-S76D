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

The initial frozen 5f profile was actually materialized and independently verified
byte-for-byte. This profile adds 24 approved lifecycle, Settings/input and
traffic repair paths and two Editor-only raster fixture paths, which passed 49 focused normal-graphics checks. The candidate
full suite passed 1,632 tests with zero failures and ten explicit GPU/benchmark
skips before adding that explicit raster fixture. Seven existing GPU methods also
passed; the new Settings raster capture is inconclusive because its independent
known screen-camera marker was absent. Fresh materialized validation, player
build and final Windows validation
are subsequent gates.
`approvedRepairs` pins each approved path and `flattenedSnapshots` identifies the
matching 46 source snapshots and their metadata. No crash repair is claimed by
source preparation alone.
