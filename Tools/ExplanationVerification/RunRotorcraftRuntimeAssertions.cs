// Play-mode assertions using owned fixtures. Never sends X-Plane commands or edits the live bridge.
if (!Application.isPlaying) throw new Exception("Run in Play mode.");
var results = new System.Collections.Generic.List<object>();
System.Action<string, bool> check = (name, value) => results.Add(new { name, passed = value });
var flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public;
// The complete conformal ladder is intentionally suppressed by Classic instrument-attitude
// mode. Establish the fixture's full-conformal presentation instead of depending on user preferences.
var workspace = FAA.Customization.FaaSpatialWorkspace.Current;
var savedStyle = workspace != null ? workspace.CurrentSymbology : FAA.Customization.FaaSymbologyVersion.Digital;
bool savedPersistence = workspace != null && workspace.PersistChanges;
var dirtyField = typeof(FAA.Customization.FaaSpatialWorkspace).GetField("dirty", flags);
bool savedDirty = workspace != null && (bool)dirtyField.GetValue(workspace);
if(workspace != null) { workspace.PersistChanges = false; workspace.SetSymbologyVersion(FAA.Customization.FaaSymbologyVersion.Digital); }
// Live conformal mode holds the cockpit camera on the airframe boresight (review C3) and restores the scene value on exit.
var liveController = UnityEngine.Object.FindFirstObjectByType<FAA.Customization.FaaConformalHudController>();
var liveRig = Camera.main != null ? Camera.main.GetComponent<AircraftControl.Camera.AircraftCameraController>() : null;
if (liveController != null && liveController.UsesRotorcraftSceneLayer && liveRig != null)
    check("Conformal mode holds the cockpit camera on the airframe boresight (FlightPathBlend 0)", liveRig.FlightPathBlend == 0f);
var owner = new GameObject("Rotorcraft HUD assertion fixture");
owner.hideFlags = HideFlags.DontSave;
var priorSelections = FAA.Customization.FaaRotorcraftCueAnchor.Active.Where(a=>a!=null).ToDictionary(a=>a,a=>a.selected);
try
{
    var cameraObject = new GameObject("Fixture camera", typeof(Camera));
    cameraObject.transform.SetParent(owner.transform, false);
    var camera = cameraObject.GetComponent<Camera>(); camera.enabled = false;
    camera.farClipPlane = 9000; camera.aspect = 16f/9f;
    camera.transform.position = new Vector3(100000,100000,100000);
    var air = new GameObject("Fixture aircraft"); air.transform.SetParent(owner.transform, false);
    air.transform.position = camera.transform.position;
    var canvasObject = new GameObject("Fixture canvas", typeof(RectTransform), typeof(Canvas));
    canvasObject.transform.SetParent(owner.transform, false);
    var canvas = canvasObject.GetComponent<Canvas>();
    var instruments = new GameObject("Second Interation GUI", typeof(RectTransform), typeof(CanvasGroup));
    instruments.transform.SetParent(canvasObject.transform, false);
    var legacy = new GameObject("Scale", typeof(RectTransform), typeof(UnityEngine.UI.Image));
    legacy.transform.SetParent(instruments.transform, false);
    var bridgeObject = new GameObject("Inactive fixture bridge"); bridgeObject.SetActive(false);
    bridgeObject.transform.SetParent(owner.transform, false);
    var bridge = bridgeObject.AddComponent<FAA.XPlaneIntegration.Runtime.XPlane12ApiHudBridge>();
    var data = new AviationUI.AviationFlightData { attitudeValid=true, groundVelocityValid=true,
        altitudeAGLValid=true, pitch=0, roll=0, heading=0, track=8, groundSpeed=75, verticalSpeed=-400, altitudeAGL=100 };
    bridge.GetType().GetField("_latestFlightData", flags).SetValue(bridge,data);
    bridge.GetType().GetProperty("IsFeedHealthy", flags).SetValue(bridge,true);
    bridge.GetType().GetProperty("LastPacketAgeSeconds", flags).SetValue(bridge,0.05f);
    var layer = owner.AddComponent<FAA.Customization.FaaRotorcraftConformalLayer>();
    layer.GetType().GetField("flightData", flags).SetValue(layer,bridge);
    layer.Bind(camera, air.transform, canvas); layer.RefreshPresentation();
    // Only rungs that can reach the attitude window and the on-screen part of the horizon are built (CONF-02/03).
    check("Fresh forward-flight geometry",layer.HasLiveAttitude && layer.HasLiveVelocity && !layer.HoverMode && layer.LastVertexCount>200 && layer.LastRungCount>=3);
    // C3: the aircraft reference is drawn whenever attitude is live; at the boresight it sits inside the window, not pinned.
    check("Aircraft reference drawn whenever attitude is live",layer.WaterlineVisible && !layer.WaterlineLimited);
    // M7: every drawn rung carries both halves and both numerals; nothing one-sided or unlabelled.
    var rungNumerals=owner.GetComponentsInChildren<TMPro.TMP_Text>().Where(t=>t.name.StartsWith("FAA Cue Label")&&int.TryParse(t.text,out _)).Select(t=>t.text).ToList();
    check("Rungs are drawn only as labelled pairs",rungNumerals.Count==2*layer.LastRungCount&&rungNumerals.GroupBy(n=>n).All(g=>g.Count()==2));
    check("Legacy ladder graphic suppressed",!legacy.GetComponent<UnityEngine.UI.Image>().enabled);
    var buttons = owner.GetComponentsInChildren<UnityEngine.UI.Button>();
    check("Removed reference strip creates no buttons",buttons.Length==0);
    layer.SetSelectedFpa(-2.5f); check("FPA reference API preserved",layer.SelectedFpaDegrees==-2.5f);
    layer.SetSelectedFpa(-3f); check("FPA reference reset preserved",layer.SelectedFpaDegrees==-3f);
    check("Reference strip canvas is not instantiated",!owner.GetComponentsInChildren<Canvas>().Any(c=>c.name=="FAA Rotorcraft Reference Controls"));
    var label=owner.GetComponentsInChildren<TMPro.TMP_Text>().First(t=>t.name.StartsWith("FAA Cue Label"));
    check("World labels use non-occluded included overlay shader",label.fontSharedMaterial.shader.name.EndsWith("Overlay"));
    float labelAngle=Mathf.Atan(label.GetPreferredValues("0").y*label.transform.lossyScale.x /
        Vector3.Distance(label.transform.position,camera.transform.position))*Mathf.Rad2Deg;
    // Line height of 28-arcmin cap text (HF-STD-001B HUD alphanumerics), converted through the camera FOV on desktop, 1:1 in XR.
    check("Native world text has readable calibrated angular height",layer.LabelCapDegrees>=28f/60f*.99f && labelAngle>layer.LabelCapDegrees*1.15f && labelAngle<layer.LabelCapDegrees*2.3f);
    var window=layer.AttitudeWindowPixels;
    check("Pitch numerals stay inside the attitude window",owner.GetComponentsInChildren<TMPro.TMP_Text>().Where(t=>t.name.StartsWith("FAA Cue Label")&&int.TryParse(t.text,out _))
        .All(t=>{var p=camera.WorldToScreenPoint(t.transform.position);return window.Contains(new Vector2(p.x,p.y));}));
    var keep=new System.Collections.Generic.List<Rect>();FAA.Customization.FaaHudKeepOut.Collect(keep,true);
    check("Attitude window overlaps no protected HUD element",keep.All(r=>r.Contains(window.center)||!r.Overlaps(window)));
    check("Selected-FPA reference shown in approach context (HAGL 100 ft)",layer.FpaReferenceVisible);
    data.altitudeAGL=8000; layer.RefreshPresentation();
    check("Selected-FPA reference hidden in cruise",!layer.FpaReferenceVisible&&!owner.GetComponentsInChildren<TMPro.TMP_Text>().Any(t=>t.text.StartsWith("FPA")));
    data.altitudeAGL=100; data.pitch=10; layer.RefreshPresentation();
    var wl=layer.LastWaterlineDirection; var hz=FAA.Customization.FaaRotorcraftCueMath.EarthRay(0,data.heading);
    check("Nose-up waterline sits above the horizon (CONF-01)",camera.WorldToViewportPoint(camera.transform.position+wl*1000f).y>camera.WorldToViewportPoint(camera.transform.position+hz*1000f).y+.02f);
    data.pitch=35; layer.RefreshPresentation();
    check("Unusual attitude declutters FPV/FPA and keeps the ladder",layer.UnusualAttitude&&FAA.Customization.FaaRotorcraftConformalLayer.UnusualAttitudeActive&&!layer.FpaReferenceVisible&&layer.LastRungCount>0);
    data.pitch=27; layer.RefreshPresentation(); check("Unusual-attitude hysteresis holds above +25",layer.UnusualAttitude);
    data.pitch=0; layer.RefreshPresentation(); check("Unusual attitude releases",!layer.UnusualAttitude&&!FAA.Customization.FaaRotorcraftConformalLayer.UnusualAttitudeActive);
    data.groundSpeed=4; data.track=90; layer.RefreshPresentation(); check("Hover activates below 5kt",layer.HoverMode);
    data.groundSpeed=6; layer.RefreshPresentation(); check("Hover hysteresis retains at 6kt",layer.HoverMode);
    data.groundSpeed=8; layer.RefreshPresentation(); check("Forward mode resumes at 8kt",!layer.HoverMode);
    data.track=180; data.groundSpeed=30; layer.RefreshPresentation(); check("Behind-view FPV annunciated, not clamped",layer.DataStatus.StartsWith("FPV OUT OF VIEW"));
    data.track=0; data.groundVelocityValid=false; layer.RefreshPresentation();
    check("Unknown velocity suppresses FPV but preserves attitude",!layer.HasLiveVelocity && layer.HasLiveAttitude && layer.DataStatus.StartsWith("FPV UNAVAILABLE"));
    data.groundVelocityValid=true;
    bridge.GetType().GetProperty("LastPacketAgeSeconds",flags).SetValue(bridge,1.1f); layer.RefreshPresentation();
    // Invalid attitude: no ladder, horizon or FPV; only the red boxed ATT flag (4 box strokes + halo) where the attitude normally appears.
    check("Stale sample clears conformal attitude geometry and shows boxed ATT flag",!layer.HasLiveAttitude && !layer.HasLiveVelocity && layer.LastRungCount==0 &&
        layer.AttitudeFlagVisible && layer.LastVertexCount<=32 && owner.GetComponentsInChildren<TMPro.TMP_Text>().Any(t=>t.text=="ATT"));
    bridge.GetType().GetProperty("LastPacketAgeSeconds",flags).SetValue(bridge,0.02f); layer.RefreshPresentation();
    check("Fresh sample recovers",layer.HasLiveVelocity && layer.LastVertexCount>0);
    var mark = new GameObject("Fixture geographic point"); mark.transform.SetParent(owner.transform,false);
    mark.transform.position=camera.transform.position+new Vector3(0,0,500);
    var anchor=mark.AddComponent<FAA.Customization.FaaRotorcraftCueAnchor>(); anchor.identifier="FIXTURE";
    layer.RefreshPresentation(); check("Known scene waypoint rendered",layer.VisibleSceneCueCount==1);
    layer.SetSceneCuesVisible(false); layer.RefreshPresentation(); check("Scene declutter hides references",layer.VisibleSceneCueCount==0);
    layer.SetSceneCuesVisible(true); layer.RefreshPresentation(); check("Scene references restore",layer.VisibleSceneCueCount==1);
    anchor.kind=FAA.Customization.FaaRotorcraftCueAnchor.CueType.LandingArea;
    layer.RefreshPresentation(); check("Unselected landing area suppressed",layer.VisibleSceneCueCount==0);
    anchor.Select(); layer.RefreshPresentation(); check("Selected landing area rendered",layer.VisibleSceneCueCount==1);
    // Standards 2.7: unusual attitude keeps only what recognition and recovery need (no waypoint/LZ/obstacle references).
    data.pitch=35; layer.RefreshPresentation(); check("Unusual attitude removes scene references and keeps the aircraft reference",layer.UnusualAttitude&&layer.VisibleSceneCueCount==0&&layer.WaterlineVisible);
    data.pitch=0; layer.RefreshPresentation(); check("Scene references return after unusual attitude",!layer.UnusualAttitude&&layer.VisibleSceneCueCount==1);
    var group=instruments.GetComponent<CanvasGroup>(); group.alpha=0; layer.RefreshPresentation();
    check("Primary HUD declutter hides new layer",!owner.GetComponentInChildren<MeshRenderer>().enabled);
    group.alpha=1; layer.RefreshPresentation(); check("HUD declutter restores",owner.GetComponentInChildren<MeshRenderer>().enabled);
    layer.enabled=false; check("Disable restores original graphics",legacy.GetComponent<UnityEngine.UI.Image>().enabled);
    layer.enabled=true; layer.RefreshPresentation(); check("Reenable suppresses only legacy again",!legacy.GetComponent<UnityEngine.UI.Image>().enabled);
    var rootRect=(RectTransform)instruments.transform; var position=rootRect.anchoredPosition; var rotation=rootRect.localRotation;
    camera.transform.rotation=Quaternion.Euler(5,30,10); layer.RefreshPresentation();
    check("Head look never shifts fixed instrument root",rootRect.anchoredPosition==position && rootRect.localRotation==rotation);
    // C3: looking 30 deg off the boresight never drops the aircraft reference; it is pinned, dashed and ghosted, and annunciated.
    check("Off-boresight look pins the aircraft reference at the window edge (W LIMIT)",layer.WaterlineVisible&&layer.WaterlineLimited&&layer.DataStatus.Contains("W LIMIT"));
    check("Camera yaw vs validated heading is reported",Mathf.Abs(Mathf.DeltaAngle(layer.ViewHeadingOffsetDegrees,30f))<1f);
    camera.transform.rotation=Quaternion.Euler(45,0,0);
    var ground=GameObject.CreatePrimitive(PrimitiveType.Cube); ground.name="Fixture landing surface";
    ground.transform.SetParent(owner.transform,false); ground.layer=31;
    ground.transform.position=camera.transform.position+new Vector3(0,-70,70);
    ground.transform.localScale=new Vector3(200,1,200);
    layer.GetType().GetField("landingSurfaceLayers",flags).SetValue(layer,(LayerMask)(1<<31));
    UnityEngine.Physics.SyncTransforms(); layer.RefreshPresentation();
    // Standards 3.1: looking 45 deg down puts the true horizon off screen; a dashed horizon with a caret is clamped to the window edge.
    check("Off-screen horizon is clamped to the attitude window edge",layer.HorizonOffScale);
    layer.MarkLandingReference();
    var marked=(FAA.Customization.FaaRotorcraftCueAnchor)layer.GetType().GetField("userLandingReference",flags).GetValue(layer);
    check("Mark LZ API still attaches selected reference to actual collider",marked!=null && marked.selected && marked.transform.parent==ground.transform);
    layer.ClearLandingReference();
    check("Clear LZ API still removes pilot reference",layer.GetType().GetField("userLandingReference",flags).GetValue(layer)==null);
}
finally
{
    UnityEngine.Object.DestroyImmediate(owner);
    foreach (var item in priorSelections) if(item.Key!=null) item.Key.selected=item.Value;
    if(workspace != null) { workspace.SetSymbologyVersion(savedStyle); dirtyField.SetValue(workspace,savedDirty); workspace.PersistChanges=savedPersistence; }
}
return results;
