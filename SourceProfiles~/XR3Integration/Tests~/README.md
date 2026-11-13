# Full integration test fixtures

These are exact corrected fixtures from the archived XR-3 integration project. Unity ignores this directory because its parent is `SourceProfiles~`; these fixtures are preserved as source and are not imported by the standard profile.

- `FaaSourceDiscoveryTests.cs` covers the full integration source-discovery types, which are absent from the standard profile. Its invalid signed airspeed sample is below the accepted native range.
- `FaaScreenCueTests.cs` retains the full integration unusual-attitude cases. The shared projection cases derive expected positions from the camera viewport so they also run on a headless editor surface. The standard profile imports its own fixture with only its supported cases.

The near-space fixtures (`FaaNearSpaceFastPathTests`, `FaaNearSpaceFastPathGpuTests`, `FaaBatchedProjectionTests`) and `NearSpaceBaseline` are exact full-integration sources. Imported standard variants explicitly distinguish absent optional canvas/module/region assertions from shared registry/projection checks. The full profile runs the real consumers and actual-module benchmark; the standard profile skips the missing region and actual-module cases. GPU/cost fixtures are explicit and require exact-method selection with a graphics device. See [the validation plan](../../../docs/XR3_NEAR_SPACE_GEOMETRY.md).
