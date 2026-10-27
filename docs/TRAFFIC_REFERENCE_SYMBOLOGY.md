# Aircraft symbols for on-screen and off-screen cues

Traffic cues use the supplied distance/detail design in both the centered desktop view and the Varjo HUD. Open **CUES → KEY / STYLE** to see labelled examples and change the style.

- Beyond the configured far threshold (default 40 NM): concentric double-outline diamond.
- Between the thresholds: commercial airliner, military jet, propeller/general aircraft or helicopter silhouette, using the reported category.
- Inside the close threshold (default 5 NM): the matching aircraft's body geometry is projected using its reported heading, pitch and roll. It shows front, rear, side, climb/descent and bank aspects. Missing attitude retains the category silhouette; unknown categories retain an unknown diamond.

A small hysteresis band prevents detail flicker near a threshold. Distance affects symbol size within a bounded footprint. **ADAPTIVE OFF** keeps type symbols at all distances.

**FILLED / OUTLINE**, **NOSE TIP**, **NAV MARKS**, **TAIL**, and **STRIPE** are independent saved display preferences. The green nose triangle follows reported aircraft orientation. Red port, green starboard and white tail dots are display marks; they do not assert actual aircraft-light state. Traffic tint keeps the existing threat classification. Distance alone never turns a target red.

The separate edge chevron points toward an aircraft outside the visible HUD area. Its direction is independent of the aircraft's heading. In-view symbols retain the actual projected target position. Edge arrows and readout plates avoid occupied menus and other cues. The projected world-HUD boundary, rather than an unrelated desktop resolution, determines their bounds.

Native X-Plane multiplayer traffic carries per-target pose and vertical-speed validity through the radar and indicator adapters. An unchanged native pose baseline is retained only during a healthy session with an unchanged trusted slot identity and no TCAS override. Invalid values, identity changes or disconnects clear that pose. Packet-based sources retain packet-local missing-field semantics. Ground track is not substituted for body heading, and vertical speed is not converted to pitch.

The KEY contains explicitly labelled examples; they never enter the live traffic pool. Its near-range illustrations remain visible when adaptive rendering is disabled so the design is still explained.

The editor command `FAA/Indicators/Render Reference Symbology Gallery` produces an actual Unity-rendered comparison sheet for review. Focused tests cover range transitions, source validity, solid/outline geometry, attitude projection, marker visibility, screen bounds, layers and style persistence.

## Validation, 2026-10-27

The full integration EditMode suite passes 1,245/1,245 cases. The standard imported profile passes 638 cases with 2 expected optional-profile skips and no failures. The Unity-rendered gallery covers four aircraft categories, far/medium/close detail, front/rear/side/banked aspects, independent edge arrows and optional styles. The runtime KEY panel was also rendered and inspected for clipping.

Desktop hit testing synchronizes the owned spectator camera to the current head pose before raycasts and menu containment queries. The XR camera retains its stereo projection and tracked-pointer path. Two regressions fail against the prior source and pass with synchronization; all 12 actual uGUI style-button mappings are covered, and 13 real Windows mouse clicks were verified against their saved preferences. Optional `--xr3-ui-audit` logs mouse-press hit details for diagnosis; a normal EXE launch does not emit this trace.

Visible instrument and chrome keepouts are included in traffic layout. Off-screen cues use the nearest clear bounded vertical slot; a fully blocked cue is decluttered. The central attitude window remains available for actual in-view target projections. New regressions cover VSI collision, a tall open symbol key, blocked HUDs and visible region registration.
