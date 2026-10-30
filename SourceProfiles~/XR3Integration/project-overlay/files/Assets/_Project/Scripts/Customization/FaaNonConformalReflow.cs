using System.Collections.Generic;
using UnityEngine;

namespace FAA.Customization
{
    /// <summary>
    /// Canvas-local lanes: head/camera pose and utility-panel visibility cannot feed back into flight layout.
    /// Basic-T (AC 25-11B 6.2.3, AC 29-2C AC 29.1321): IAS left and ALT right of the attitude field, symmetric about it, with their
    /// readouts level with the waterline; TQ under IAS and NR/N2 under ALT; fixed lane order ATT | G/S | ALT | VS on the right.
    /// The G/S lane is always reserved, so the altitude and VSI never move when glideslope guidance appears or drops out.
    /// Centre column: the FMA is pinned in its top lane and the LOC scale sits directly above the heading lane.
    /// Lane constants are reference px measured from the TOP of the 1920x1080 canvas and scale with canvas height.
    /// </summary>
    public sealed class FaaNonConformalReflow
    {
        // ---- Screen zone plan (reference px from the top of the canvas) ----
        public const float FmaLaneTop = 14f, FmaLaneBottom = 80f;
        public const float BankLaneBottom = 235f;
        public const float ProtectedFieldTop = 240f, ProtectedFieldBottom = 742f;
        /// <summary>Nominal LOC lane; the scale's bottom edge sits <see cref="LocLaneGap"/> above <see cref="HeadingLaneTop"/>.</summary>
        public const float LocLaneTop = 744f, LocLaneGap = 4f;
        public const float HeadingLaneTop = 790f, HeadingLaneBottom = 850f;
        /// <summary>No flight symbology below this line; persistent chrome starts at <see cref="PersistentChromeTop"/>.</summary>
        public const float SymbologyBottom = 990f, PersistentChromeTop = 1010f;
        /// <summary>Waterline / attitude reference height (the camera's optical axis).</summary>
        public const float WaterlineFromTop = 540f;
        /// <summary>Half-width of the attitude field (x 705-1215 on the reference canvas).</summary>
        public const float AttitudeFieldHalfWidth = 255f;
        /// <summary>Glideslope lane width reserved whether or not G/S guidance is shown.</summary>
        public const float ReservedGlideLane = 44f;
        public const float ColumnGap = 10f;

        private readonly IReadOnlyList<FaaNonConformalScaleTarget> modules;
        private readonly Dictionary<FaaNonConformalScaleTarget, RectTransform> anchors = new();
        private readonly Dictionary<FaaNonConformalScaleTarget, bool> anchorSearched = new();
        private float reservedGlideWidth, reservedGlideScale = -1f, reservedGlideK = -1f;
        private FaaNonConformalScaleTarget selfPinnedModule;
        private bool selfPinned;
        private ScalePair readoutPair = new ScalePair("airspeed", "altitude"), enginePair = new ScalePair("torque", "nr");

        public FaaNonConformalReflow(IReadOnlyList<FaaNonConformalScaleTarget> registered) { modules = registered; }

        private FaaNonConformalScaleTarget Find(string id)
        {
            if (modules == null) return null;
            for (int i = 0; i < modules.Count; i++) if (modules[i] != null && modules[i].Id == id) return modules[i];
            return null;
        }
        private Rect Bounds(RectTransform root, FaaNonConformalScaleTarget module) =>
            module != null && module.TryLayoutBounds(root, out Rect bounds) ? bounds : Rect.zero;

        public void Apply(Camera view)
        {
            if (view == null) return;
            var altitudeModule = Find("altitude");
            if (altitudeModule == null || altitudeModule.Target == null) return;
            var parentCanvas = altitudeModule.Target.GetComponentInParent<Canvas>();
            var canvas = parentCanvas != null ? parentCanvas.rootCanvas : null;
            if (canvas == null) return;
            var root = (RectTransform)canvas.transform;
            Rect viewport = root.rect;
            if (viewport.width < 200 || viewport.height < 200) return;
            float k = viewport.height / 1080f, cx = viewport.center.x, gap = ColumnGap * k;
            PlaceCentreLanes(root, viewport, k);

            // Paired modules share one size so the Basic-T stays symmetric (IAS = ALT, TQ = NR).
            readoutPair.Apply(this); enginePair.Apply(this);
            var airspeedModule = Find("airspeed");
            FaaNonConformalScaleTarget glideModule = Find("glideslope"), vsiModule = Find("vertical-speed"),
                nrModule = Find("nr"), torqueModule = Find("torque");
            Rect altitude = Bounds(root, altitudeModule), airspeed = Bounds(root, airspeedModule), glide = Bounds(root, glideModule),
                vsi = Bounds(root, vsiModule), nr = Bounds(root, nrModule), torque = Bounds(root, torqueModule);
            if (altitude.width <= 0 || airspeed.width <= 0) return;

            // Offsets from each module's value centre to its bounds centre, so the DIGITS (not the caption block) sit on the
            // waterline and the IAS and ALT boxes (not whatever else their modules hold) mirror each other.
            Vector2 altAnchor = Anchor(root, altitudeModule, altitude), airAnchor = Anchor(root, airspeedModule, airspeed);
            float altOffset = altitude.center.y - altAnchor.y, airOffset = airspeed.center.y - airAnchor.y;
            float vsiOffset = vsi.height > 0 ? vsi.center.y - Anchor(root, vsiModule, vsi).y : 0f;
            float below = Mathf.Max(altitude.height * .5f - altOffset, airspeed.height * .5f - airOffset);
            float above = Mathf.Max(Mathf.Max(altitude.height * .5f + altOffset, airspeed.height * .5f + airOffset),
                Mathf.Max(vsi.height * .5f + vsiOffset, glide.height * .5f));
            float engineHeight = Mathf.Max(nr.height, torque.height);
            float floorY = viewport.yMax - SymbologyBottom * k, ceilingY = viewport.yMax - FmaLaneBottom * k - gap;
            float lowest = floorY + Mathf.Max(below + (engineHeight > 0 ? gap + engineHeight : 0f),
                Mathf.Max(vsi.height * .5f - vsiOffset, glide.height * .5f));
            float valueY = viewport.yMax - WaterlineFromTop * k;
            valueY = Mathf.Max(Mathf.Min(valueY, ceilingY - above), lowest);

            // Sticky reservation: once seen, the G/S width stays reserved even if the scale is deactivated, until its size changes.
            float glideScale = glideModule != null ? glideModule.EffectiveScale : 1f;
            if (!Mathf.Approximately(glideScale, reservedGlideScale) || !Mathf.Approximately(k, reservedGlideK))
            { reservedGlideScale = glideScale; reservedGlideK = k; reservedGlideWidth = 0f; }
            reservedGlideWidth = Mathf.Max(reservedGlideWidth, glide.width);
            float glideLane = Mathf.Max(ReservedGlideLane * k, reservedGlideWidth);
            float altLeft = cx + AttitudeFieldHalfWidth * k + gap + glideLane + gap;
            float altX = altLeft + (altAnchor.x - altitude.xMin);           // ALT value centre
            float altRight = altX + (altitude.xMax - altAnchor.x);
            float vsiX = altRight + gap + vsi.width * .5f;
            float rightEdge = Mathf.Max(vsiX + vsi.width * .5f, Mathf.Max(altRight, altX + nr.width * .5f));
            float overflow = Mathf.Max(0f, rightEdge - (viewport.xMax - gap));
            altLeft -= overflow; altX -= overflow; vsiX -= overflow;
            float glideX = altLeft - gap - glideLane * .5f;
            float airX = cx * 2f - altX;                                    // IAS value centre, mirrored
            altitudeModule.PlaceLayoutCenter(root, new Vector2(altX + (altitude.center.x - altAnchor.x), valueY + altOffset));
            airspeedModule?.PlaceLayoutCenter(root, new Vector2(airX + (airspeed.center.x - airAnchor.x), valueY + airOffset));
            glideModule?.PlaceLayoutCenter(root, new Vector2(glideX, valueY));
            vsiModule?.PlaceLayoutCenter(root, new Vector2(vsiX, valueY + vsiOffset));
            float engineTop = valueY - below - gap;
            nrModule?.PlaceLayoutCenter(root, new Vector2(altX, engineTop - nr.height * .5f));
            torqueModule?.PlaceLayoutCenter(root, new Vector2(airX, engineTop - torque.height * .5f));
        }

        private void PlaceCentreLanes(RectTransform root, Rect viewport, float k)
        {
            float cx = viewport.center.x;
            var fma = Find("fma");
            // FaaFlightModeAnnunciator pins its own top edge in Z1; moving it here as well would make the two fight every frame.
            if (fma != null && !SelfPinned(fma) && fma.TryLayoutBounds(root, out Rect fb))
                fma.PlaceLayoutCenter(root, new Vector2(cx, viewport.yMax - FmaLaneTop * k - fb.height * .5f));
            var loc = Find("localizer");
            if (loc != null && loc.TryLayoutBounds(root, out Rect lb))
                loc.PlaceLayoutCenter(root, new Vector2(cx, viewport.yMax - (HeadingLaneTop - LocLaneGap) * k + lb.height * .5f));
        }

        private bool SelfPinned(FaaNonConformalScaleTarget module)
        {
            if (selfPinnedModule != module)
            {
                selfPinnedModule = module;
                // By name, so the layout does not depend on the annunciator's implementation.
                selfPinned = module.Target != null && module.Target.GetComponent("FaaFlightModeAnnunciator") != null;
            }
            return selfPinned;
        }

        /// <summary>Canvas-local centre of a module's value: the IAS/ALT digits box, the VSI value box, else the bounds centre.</summary>
        private Vector2 Anchor(RectTransform root, FaaNonConformalScaleTarget module, Rect bounds)
        {
            if (module == null || module.Target == null) return bounds.center;
            if (!anchorSearched.TryGetValue(module, out bool searched) || !searched)
            {
                anchorSearched[module] = true;
                var readout = module.Target.GetComponentInChildren<FaaPrimaryFlightReadout>(true);
                RectTransform anchor = readout != null ? readout.ValueRect : null;
                if (anchor == null)
                {
                    var vsi = module.Target.GetComponentInChildren<FaaEngineInstrumentGraphic>(true);
                    if (vsi != null && vsi.Instrument == FaaEngineInstrument.VerticalSpeed) anchor = vsi.rectTransform;
                }
                anchors[module] = anchor;
            }
            if (!anchors.TryGetValue(module, out var rect) || rect == null || !rect.gameObject.activeInHierarchy ||
                !FaaCanvasLocalGeometry.TryMatrix(rect, root, out var matrix))
                return bounds.center;
            return FaaCanvasLocalGeometry.TransformRect(rect.rect, matrix).center;
        }

        /// <summary>Keeps two modules at one size: the one the pilot changed last wins; a mismatched saved profile takes the larger.</summary>
        private struct ScalePair
        {
            private readonly string a, b;
            private float lastA, lastB;
            private bool seeded;
            public ScalePair(string first, string second) { a = first; b = second; lastA = lastB = 0f; seeded = false; }
            public void Apply(FaaNonConformalReflow owner)
            {
                var ma = owner.Find(a); var mb = owner.Find(b);
                if (ma == null || mb == null) return;
                float sa = ma.Layout.scale, sb = mb.Layout.scale;
                if (!Mathf.Approximately(sa, sb))
                {
                    float v = !seeded ? Mathf.Max(sa, sb) : !Mathf.Approximately(sa, lastA) ? sa : !Mathf.Approximately(sb, lastB) ? sb : Mathf.Max(sa, sb);
                    ma.Layout.scale = mb.Layout.scale = v; ma.Apply(); mb.Apply();
                    sa = sb = v;
                }
                lastA = sa; lastB = sb; seeded = true;
            }
        }
    }
}
