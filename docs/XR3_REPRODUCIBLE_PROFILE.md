# Reproduce the complete XR-3 integration project

The standard imported project and the full XR-3 integration project are distinct
profiles. The standard project can compile its own dependency graph, but copying
the flattened `SourceProfiles~/XR3Integration/*.cs` snapshots into `Assets` is not
a complete integration. The full profile also needs the workspace partials,
Classic graphics, source-discovery contracts, shaders, settings and scene.

The complete profile now has a SHA-pinned materializer:

```sh
python3 Tools/prepare_xr3_profile.py \
  --output ../FAA-XR3-Full-Profile --download-cesium
```

Run it from a clone of **CatfishW/FAA-Unity-S76D** with Git LFS installed and this
revision's LFS objects already available. A normal LFS-enabled clone supplies
those objects; if necessary, fetch them from the S76D remote first. The tool does
not fetch Git/LFS data or select a remote. In developer worktrees, do not assume
an existing `origin` points to S76D.

For an already downloaded official Cesium archive, use:

```sh
python3 Tools/prepare_xr3_profile.py \
  --output ../FAA-XR3-Full-Profile \
  --cesium-archive /path/to/com.cesium.unity-1.25.1.tgz
```

The output must not exist and must be outside the source checkout. There is no
force or in-place override. The preparation creates about 4.01 GB of project
inputs; downloading Cesium also temporarily needs its 594 MB archive. Unity's
later import/build needs additional space.

## Committed inputs and the complete inventory

The tool reads only committed Git blobs at `--revision` (default `HEAD`). It
warns that modified and untracked working files are excluded. Neither a dirty
working asset nor an uncommitted overlay can become an input. The three committed
root trees must match the reviewed baseline:

| Root | Git tree |
|---|---|
| Assets | `e6580cd58e9228847fa35199e014c2011b7d3a29` |
| Packages | `af3496808ead10ed241f7f70abc9d44b4d714a3a` |
| ProjectSettings | `f0aab97c515749e586ec9b2711a2bd4d79b0e84b` |

Commit `1d92bbb0` is provenance, not a required ancestor object. A depth-one clone
works when those root trees are unchanged and the committed profile matches the
tool's manifest SHA. Git LFS pointers resolve only through the `LocalMediaDir`
reported by `git lfs env`; both size and SHA-256 must match. There is no fallback
to working-tree files or an unverified object.

`SourceProfiles~/XR3Integration/project-overlay/profile-manifest.json` contains
the complete expected 10,348-file inventory, original relative paths, SHA-256,
sizes, executable bits, directory set, replacements and removals. It derives from
the frozen full-profile source that produced baseline Assembly-CSharp MVID
`5f5d7d11-cd7e-4f94-a6a8-c7f4106324f8`, plus 24 approved repair paths and two Editor-only Settings raster fixture paths.
`approvedRepairs` pins their exact hashes. Existing Unity metadata/GUIDs retain
their frozen bytes; new component/test metadata matches the approved candidate.
Ten affected flattened runtime sources and their metadata
are synchronized, with all 46 source snapshots mapped by `flattenedSnapshots`.

The scoped delta is 453 new files, 321 replacements, 145 removals and ten Cesium
import-metadata replacements: 784 overlay payloads, about 102.58 MB uncompressed.
The initial audit counted 760 non-Cesium changes; excluding orphan cache metadata
reduced the baseline to 759; the reviewed repairs and the raster fixture add 15 overlay paths and update 11
existing payloads. Seventeen `__pycache__.meta` markers are excluded from the
whole project, including sixteen inherited baseline markers. The removals affect
only the new output: the old XR Interaction Simulator copy is replaced by the
full profile's authored XRI 3.6.0 sample, and its release-specific script validator
replaces the standard validator/test pair.

No Library, Temp, UserSettings, Git database, diagnostics, captured room images,
recordings, `.DS_Store`, Python bytecode or cache metadata is copied. Authored XRI
icons/gesture recordings are package sample assets. The webcam model retains its
third-party notices. A known `apiKey: empty` field is a non-credential placeholder;
the proposed changed texts/configuration were scanned for actual credential
values and private absolute paths. Runtime credentials remain user configuration.

## Pinned external dependency

The full profile uses embedded Cesium 1.25.1. Instead of duplicating its 2.79 GB
vendor tree, preparation verifies and extracts the official archive:

- URL: [Cesium 1.25.1 release asset](https://github.com/CesiumGS/cesium-unity/releases/download/v1.25.1/com.cesium.unity-1.25.1.tgz)
- Size: `594262589` bytes
- SHA-256: `bde7aacd4a8b251a44984aaefa4ce44878727cab30d81f588ebf9ed146a5da3b`

The official release metadata confirms that digest. All 2,007 archived files were
compared with the frozen embedded package: source/native content matches, with
only ten SVG importer metadata files requiring the included overlay. Existing
licensed/vendor assets come from committed baseline/LFS inputs, not duplicated
private trees. Archive links, traversal, unexpected entries and collisions are
refused. An optional download uses only the pinned URL and is removed from the
prepared output after extraction.

## Preparation and build are separate gates

The complete repaired source at commit
`54fc2c0351c092e5ab5501992985b07a07646312` was prepared through this materializer,
then independently verified against all **10,348 files, 812 directories,
4,007,105,954 bytes and executable flags**. All 26 approved source/test paths
matched the reviewed candidate; private baseline probes were absent. Unity's
Library was separately copied only as an import cache, outside the committed
project-input inventory.

That freshly materialized project passed **1,632 tests with zero failures and
11 explicit GPU/benchmark skips**, exit code 0. The full input inventory remained
pinned to that commit; the skipped methods are selected separately when their
normal-render or benchmark requirements are available. The repaired candidate
also passed all 49 focused lifecycle, desktop Settings/input and traffic-boundary
checks, plus seven existing dedicated normal-render GPU methods. The new
Settings raster fixture is separately **inconclusive**: ordinary display-camera
readback did not contain its independent known screen-camera marker, so it makes
no product pixel claim.

The same materialized source produced a non-Development Windows x64 / Mono /
Direct3D11 player: **Succeeded, zero errors and 257 warnings**. Its normal-user,
zero-argument run on `4090` completed 30 minutes and closed with exit code
`00000000`, without a debugger or forced termination. An audited 115.3-second desktop
recording completes the planned core UI workflow, including the corrected Settings
route, Data Sources closing and final baseline restoration;
separate signed stills prove actual traffic glyph ON/OFF/ON. The live S-76 source
also passed independent native-physics journal audits for a 1,800-second trial
and separate 360- and 600-second continuations, all ending paused. Final log review
found no reproduced access violation, managed exception or camera teardown
warning. Residual font fallback and request-timeout warnings remain documented.

See [the final remote validation note](XR3_CLASSIC_REMOTE_VALIDATION.md) for exact
counts, timing, recording omissions and source/build identity. Successful bounded
runs do not establish resolution of the original native corruption writer.
Physical headset alignment, hand recognition and wearer comfort were outside the
remote-only test scope.

Preparation writes into its own temporary staging directory, verifies every
resulting project file and executable bit, then publishes with an exclusive
atomic directory rename. Even a destination created concurrently is not
replaced. Failure cleans only the owned staging directory. The result includes
`XR3-PROFILE-RECEIPT.json`; preparation never starts Unity or a player.

After reviewing the prepared source, open that output with Unity **6000.3.27f1**
and Windows Build Support (Mono). The full release entry is
`FAA.Headset.Editor.XR3ReleaseBuild.BuildWindows64`. It builds
**`Assets/_Project/Scenes/ExperimentScene.unity`**, rather than the Main scene
listed in ordinary Editor build settings. It verifies native Varjo startup,
selects Windows x64 / Mono / Direct3D11, and processes only the build's scene
copy, including removal of XR simulators and unvalidated legacy post-processing.
It restores editor build preferences afterward.

Example after the separate Unity validation gate:

```sh
UNITY_EDITOR -batchmode -projectPath ../FAA-XR3-Full-Profile \
  -buildTarget Win64 -executeMethod FAA.Headset.Editor.XR3ReleaseBuild.BuildWindows64 \
  -xr3Output /path/to/new-build/FAA-XR3.exe -quit -logFile /path/to/new-build.log
```

`UNITY_EDITOR` stands for the chosen editor executable. Native binaries need not
be bit-identical across fresh imports/builds; the input inventory and recipe are
the reproducibility contract. The fresh materialized import/test, Windows build
and bounded remote runtime gates above passed. This preserves a demonstrated
source-to-player route without claiming that the earlier native corruption is
resolved or replacing physical wearer checks.
