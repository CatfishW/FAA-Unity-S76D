# Full integration test fixtures

These are exact corrected fixtures from the archived XR-3 integration project. Unity ignores this directory because its parent is `SourceProfiles~`; these fixtures are preserved as source and are not imported by the standard profile.

- `FaaSourceDiscoveryTests.cs` covers the full integration source-discovery types, which are absent from the standard profile. Its invalid signed airspeed sample is below the accepted native range.
- `FaaScreenCueTests.cs` retains the full integration unusual-attitude cases. The shared projection cases derive expected positions from the camera viewport so they also run on a headless editor surface. The standard profile imports its own fixture with only its supported cases.
