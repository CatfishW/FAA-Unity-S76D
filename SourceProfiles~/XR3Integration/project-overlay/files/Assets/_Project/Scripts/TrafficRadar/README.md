# Traffic Radar System

A complete FAA TCAS-compliant aviation traffic radar system for Unity.

## Quick Setup

**Menu:** `Tools > Traffic Radar > Setup Wizard`

Or use one-click setup: `Tools > Traffic Radar > One-Click Complete Setup`

## Architecture

```
TrafficRadarController (main coordinator)
    ├── TrafficRadarDataManager (API fetching from Airplanes.live)
    ├── RadarDataProcessor (threat classification)
    └── TrafficRadarDisplay (circular radar rendering)

OwnAircraftRadarBridge (optional, for dynamic position)
    └── Updates TrafficRadarController.SetOwnPosition()
```

## Components

| Component | Purpose |
|-----------|---------|
| `TrafficRadarController` | Main entry point - coordinates all components |
| `TrafficRadarDataManager` | Fetches aircraft from Airplanes.live API |
| `RadarDataProcessor` | Calculates distances, bearings, threat levels |
| `TrafficRadarDisplay` | Renders circular radar with symbols |
| `GeoUtilities` | Static helper for geographic calculations |
| `OwnAircraftRadarBridge` | Links AircraftController position to radars |

## Threat Levels (AC 20-172B / DO-185B symbology)

| Level | Color | Symbol | Criteria |
|-------|-------|--------|----------|
| Resolution Advisory | Red | Square | Genuine TCAS RA source only (none connected) |
| Traffic Advisory | Amber | Circle | Genuine TCAS TA source only (none connected) |
| Proximate | Cyan | Filled Diamond | <=6 NM and <=1200 ft (unknown altitude counts as co-altitude) |
| Other Traffic | Cyan | Diamond | Beyond proximate |

The demonstrator has no TCAS logic, so `ThreatThresholds` never computes TA/RA from distance alone
(`allowComputedAdvisories` / `allowComputedResolutionAdvisory` are demo-only switches, off by default).
Own-ship is white and is drawn as the top layer by `RadarTrafficOverlay`.

## Altitude band and symbol limit

- `TrafficAltitudeBand`: Normal (+/-2700 ft, default), Above (-2700..+9900), Below (-9900..+2700), All.
  Out-of-band traffic is hidden unless it is an advisory; non-altitude-reporting traffic is kept (no tag).
- Data tags: relative altitude in hundreds of feet with a sign; arrow when climbing/descending >= 500 fpm.
  The legend lives in the settings help, not on the scope; the footer reports `n NO TAG` when tags are crowded out.
- `MaxTargets` truncates only after sorting by threat then range; proximate/advisory traffic is always kept.

## Scope chrome (FaaRadarPresentation)

- Header: `TRAFFIC` title, a `DISPLAY [ON|OFF]` selector (each option is its own button, the active one is filled; it hides
  the instrument locally and never commands TCAS), and the source line. `LIVE` is reserved for the local X-Plane simulator;
  the network fallback is `FALLBACK DATA` (amber), matching the status chip.
- Footer (two rows on an opaque plate): selected range with units (`10 NM`, never an intermediate zoom value) | orientation
  (`TRK UP` / `N UP`); then the altitude band, always annunciated (`NORM ±2700 FT`, `ABOVE +9900 FT`, `BELOW -9900 FT`,
  `ALL ALT`; TCAS short forms when space is tight) | the displayed-traffic count (`NO TFC` / `n TFC`, then `n NO TAG`).
- Every chrome text is at least 15 reference units at its rendered size (ReadabilityScale; while a panel is inspected the
  scale never shrinks, and a zoomed-in view magnifies text instead of shrinking it back to the floor).

## Features

- Live aircraft data from Airplanes.live API
- Auto range (5-40 NM): frames every proximate target and the 4 nearest displayed aircraft (+15%);
  expands at once, shrinks only after 5 s; waits for a live own-ship fix
- One range list for every control: 2, 5, 10, 20, 40, 80 NM
- Labelled half-range and outer rings, plus a dotted 2 NM ring at ranges up to 20 NM
- FAA sectional chart tile background, shown only out to 40 NM and only when the mosaic covers the
  whole scope (`TrafficRadarDisplay.ChartAvailable`); the legacy MapCanvas/Map Image layer is retired
- Traffic-first scope: a near-black `Scope Backdrop` disc (alpha 0.88-0.95 in the HUD presentation) sits BELOW the chart;
  the chart is drawn desaturated and dimmed (`ChartSaturation`, `ChartBrightness`, shader `_Saturation`/`_Brightness`) at
  no more than `MaxChartOpacity` (25%), so cyan traffic and the white own-ship dominate and amber/red stay reserved for advisories
- **Circular chart mask** - Chart and radar clip to circular shape
- **Adjustable chart transparency** - 10-25% via settings, voice or runtime (capped at `MaxChartOpacity`)
- **Smooth continuous zoom** - Animated zoom with configurable speed (like Online Maps)
- **Zoom range limits** - Min/max range constraints
- Dynamic position via OwnAircraftRadarBridge
- Preset locations (ATL, JFK, LAX, ORD, DFW, LHR)

## Inspector Settings

### TrafficRadarController
- `rangeNM` - Radar range in nautical miles
- `rangeOptionsNM` - The single range list (normalised to 2-80 NM at runtime)
- `autoRangeEnabled` / `autoRangeMinNM` / `autoRangeMaxNM` / `autoRangeNearestCount` / `autoRangeShrinkDelaySeconds`
- `altitudeBand` - TCAS relative-altitude band
- `verboseLogging` - Enable debug logs

### TrafficRadarDisplay
- `rangeNM` - Current range in NM
- `minRangeNM` / `maxRangeNM` - Zoom limits (default: 2-80 NM)
- `zoomSpeed` - Multiplier per zoom step (default: 1.5x)
- `enableSmoothZoom` - Enable animated zoom transitions
- `zoomAnimationDuration` - Animation time (default: 0.3s)
- `showRadarBackground` - Show/hide solid background circle
- `chartOpacity` - Chart opacity (0-0.25; capped at `MaxChartOpacity`, default 0.22)
- `chartEdgeSoftness` - Circular mask edge softness (0-0.1)

### TrafficRadarDataManager
- `referenceLatitude/Longitude` - API fetch center
- `radiusFilterKm` - Fetch radius
- `updateInterval` - Refresh rate in seconds

## Runtime API

### Zoom Control
```csharp
TrafficRadarDisplay display = FindAnyObjectByType<TrafficRadarDisplay>();

// Smooth zoom (animated if enableSmoothZoom is true)
display.ZoomIn();   // Zoom in by zoomSpeed multiplier
display.ZoomOut();  // Zoom out by zoomSpeed multiplier
display.ZoomBy(-5f); // Zoom in by 5 NM
display.SetRange(20f); // Set to 20 NM (animated)

// Immediate (no animation)
display.SetRangeImmediate(20f);

// Direct animation control
display.StartZoomAnimation(10f);  // Animate to 10 NM

// Subscribe to changes
display.OnZoomChanged.AddListener((range) => Debug.Log($"Range: {range}"));
```

### Chart Background Control
```csharp
display.ChartOpacity = 0.2f; // capped at TrafficRadarDisplay.MaxChartOpacity (0.25)
display.ToggleChartBackground();
```
