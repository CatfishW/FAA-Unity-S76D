using System;
using System.Collections.Generic;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.UI;

namespace FAA.Customization.Tests
{
    /// <summary>
    /// Procedural Digital roll scale (C2): arc, sky pointer and slip brick share one centre and radius, the pointer reads bank 1:1 about
    /// the arc centre with the Classic convention, ticks are at the standard angles and the scale end equals the arc end.
    /// </summary>
    public class FaaDigitalBankScaleTests
    {
        private const BindingFlags Any = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
        private static Type T(string name) => Type.GetType(name + ", Assembly-CSharp", true);
        private static Type Bank => T("HUDControl.Elements.BankScaleElement");
        private static Type Scale => T("FAA.Customization.FaaDigitalBankGraphic");
        private static float Const(string name) => Convert.ToSingle(Scale.GetField(name, Any).GetValue(null));

        private static object Call(object target, string method, params object[] args)
        {
            Type type = target as Type ?? target.GetType();
            MethodInfo info = type.GetMethod(method, Any);
            Assert.That(info, Is.Not.Null, method);
            return info.Invoke(target is Type ? null : target, args);
        }

        private static object Get(object target, string property) => target.GetType().GetProperty(property, Any).GetValue(target);

        private static void SetField(object target, string field, object value)
        {
            FieldInfo info = target.GetType().GetField(field, Any);
            Assert.That(info, Is.Not.Null, field);
            info.SetValue(target, value);
        }

        /// <summary>A bank element shaped like the scene's: bitmap arc, 'Bank Scale IP' pivot with the bitmap pointer and brick.</summary>
        private static Component NewProceduralBank(out GameObject root, out Image arc, out Image pointer, out Image slip, out Graphic scale)
        {
            root = new GameObject("Bank Scale", typeof(RectTransform));
            root.SetActive(false);
            var arcObject = new GameObject("Bank Scale", typeof(RectTransform), typeof(CanvasRenderer), typeof(Image));
            arcObject.transform.SetParent(root.transform, false);
            var ip = new GameObject("Bank Scale IP", typeof(RectTransform));
            ip.transform.SetParent(root.transform, false);
            var pointerObject = new GameObject("Roll Pointer", typeof(RectTransform), typeof(CanvasRenderer), typeof(Image));
            pointerObject.transform.SetParent(ip.transform, false);
            var slipObject = new GameObject("Slip Slider", typeof(RectTransform), typeof(CanvasRenderer), typeof(Image));
            slipObject.transform.SetParent(ip.transform, false);
            arc = arcObject.GetComponent<Image>();
            pointer = pointerObject.GetComponent<Image>();
            slip = slipObject.GetComponent<Image>();
            Component bank = root.AddComponent(Bank);
            SetField(bank, "bankScale", (RectTransform)arcObject.transform);
            SetField(bank, "bankScaleIP", ip.transform);
            SetField(bank, "rollPointer", (RectTransform)pointerObject.transform);
            SetField(bank, "slipSlider", (RectTransform)slipObject.transform);
            Call(bank, "Initialize");
            Call(bank, "ConfigureMeasuredSlip");
            scale = (Graphic)Call(bank, "EnsureProceduralScale");
            Assert.That(scale, Is.Not.Null);
            return bank;
        }

        private static void Roll(Component bank, float degrees)
        {
            object state = Activator.CreateInstance(Type.GetType("AircraftControl.Core.AircraftState, AircraftControl", true));
            state.GetType().GetField("Roll").SetValue(state, degrees);
            Call(bank, "UpdateElement", state);
        }

        /// <summary>Vertices of the scale's mesh, in local units about the arc centre (the RectTransform pivot).</summary>
        private static List<Vector2> Mesh(Graphic graphic)
        {
            var vh = new VertexHelper();
            MethodInfo populate = typeof(Graphic).GetMethod("OnPopulateMesh", BindingFlags.Instance | BindingFlags.NonPublic, null,
                new[] { typeof(VertexHelper) }, null);
            populate.Invoke(graphic, new object[] { vh });
            var list = new List<Vector2>(vh.currentVertCount);
            UIVertex v = default;
            for (int i = 0; i < vh.currentVertCount; i++) { vh.PopulateUIVertex(ref v, i); list.Add(v.position); }
            vh.Dispose();
            return list;
        }

        private static float BearingOf(Vector2 p) => Mathf.Atan2(p.x, p.y) * Mathf.Rad2Deg;

        private static bool HasVertexNear(List<Vector2> mesh, Vector2 target, float tolerance)
        {
            foreach (Vector2 v in mesh) if ((v - target).sqrMagnitude <= tolerance * tolerance) return true;
            return false;
        }

        [TestCase(30f)]
        [TestCase(-20f)]
        [TestCase(10f)]
        public void PointerBearingAboutTheArcCentreEqualsRoll(float roll)
        {
            Component bank = NewProceduralBank(out GameObject root, out _, out _, out _, out Graphic scale);
            try
            {
                Roll(bank, roll);
                Vector2 tip = (Vector2)Call(Scale, "PointerTip", roll);
                List<Vector2> mesh = Mesh(scale);
                Assert.That(HasVertexNear(mesh, tip, .01f), Is.True, "The drawn pointer tip is the computed tip.");
                // Sky-pointer convention (same as Classic): the pointer moves with the horizon, counter-clockwise for a right bank.
                Assert.That(BearingOf(tip), Is.EqualTo(-roll).Within(.5f), "Pointer bearing about the arc centre equals the bank angle.");
                Assert.That((float)Get(scale, "PointerBearingDegrees"), Is.EqualTo(-roll).Within(.5f));
                Assert.That(BearingOf(tip), Is.EqualTo((float)Call(T("FAA.Customization.FaaClassicBankGraphic"), "PointerBearing", roll)).Within(.01f),
                    "Digital and Classic use the same pointer convention.");
                Assert.That(tip.magnitude, Is.EqualTo(Const("Radius") - Const("PointerGap")).Within(.01f),
                    "The pointer rides on the arc's own radius, about the arc's own centre.");

                // The arc is centred on the same origin and ends at the scale end (60 deg).
                float radius = Const("Radius"), end = Const("ScaleEndDegrees"), halfStroke = Const("ArcStroke") * .5f;
                bool arcEnd = false;
                foreach (Vector2 v in mesh)
                    if (Mathf.Abs(BearingOf(v) + end) < .6f && Mathf.Abs(v.magnitude - (radius + halfStroke)) < .1f) { arcEnd = true; break; }
                Assert.That(arcEnd, Is.True, "Arc stroke at the 60 deg end on the pointer's centre.");
                Assert.That((float)Get(bank, "MaxBankAngle"), Is.EqualTo(end), "The scale end equals the arc end.");
            }
            finally { UnityEngine.Object.DestroyImmediate(root); }
        }

        [Test]
        public void TicksAreAtTheStandardAnglesWithAFixedIndex()
        {
            Component bank = NewProceduralBank(out GameObject root, out _, out _, out _, out Graphic scale);
            try
            {
                var ticks = (float[])Scale.GetField("TickAngles", Any).GetValue(null);
                foreach (float required in new[] { 10f, 20f, 30f, 60f })
                    Assert.That(Array.IndexOf(ticks, required), Is.GreaterThanOrEqualTo(0), required + " deg tick");
                var standard = new[] { 10f, 20f, 30f, 45f, 60f };
                foreach (float t in ticks) Assert.That(Array.IndexOf(standard, t), Is.GreaterThanOrEqualTo(0), t + " deg is not a standard tick angle.");
                Assert.That(Const("ShortTickLength"), Is.LessThan(Const("MinorTickLength")), "45 deg is the small mark.");

                List<Vector2> mesh = Mesh(scale);
                float radius = Const("Radius");
                foreach (float a in new[] { 10f, 20f, 30f, 60f })
                    for (int side = -1; side <= 1; side += 2)
                    {
                        float length = a == 30f || a == 60f ? Const("MajorTickLength") : Const("MinorTickLength");
                        Vector2 outer = new Vector2(Mathf.Sin(side * a * Mathf.Deg2Rad), Mathf.Cos(side * a * Mathf.Deg2Rad)) * (radius + length);
                        Assert.That(HasVertexNear(mesh, outer, 1.6f), Is.True, "Tick at " + side * a + " deg on the arc's centre.");
                    }
                // Fixed hollow index above the arc, apex on the arc at the top.
                Assert.That(HasVertexNear(mesh, new Vector2(0f, radius + Const("IndexGap")), 1.6f), Is.True);
            }
            finally { UnityEngine.Object.DestroyImmediate(root); }
        }

        [Test]
        public void BankBeyondTheArcEndIsPeggedAndFlaggedOffScale()
        {
            Component bank = NewProceduralBank(out GameObject root, out _, out _, out _, out Graphic scale);
            try
            {
                Roll(bank, 75f);
                Assert.That((bool)Get(bank, "OverBank"), Is.True);
                Assert.That((bool)Get(scale, "OffScale"), Is.True, "Off-scale indication beyond the arc end.");
                Assert.That((float)Get(scale, "PointerBearingDegrees"), Is.EqualTo(-60f).Within(.01f), "Pegged at the arc end.");
                Roll(bank, -45f);
                Assert.That((bool)Get(bank, "OverBank"), Is.False);
                Assert.That((bool)Get(scale, "OffScale"), Is.False);
                Assert.That((float)Get(scale, "PointerBearingDegrees"), Is.EqualTo(45f).Within(.01f));
            }
            finally { UnityEngine.Object.DestroyImmediate(root); }
        }

        [Test]
        public void BitmapScaleIsHiddenAndValidityRemovesPointerAndBrick()
        {
            Component bank = NewProceduralBank(out GameObject root, out Image arc, out Image pointer, out Image slip, out Graphic scale);
            try
            {
                Assert.That(arc.enabled || arc.gameObject.activeSelf, Is.False, "The bitmap arc is hidden at runtime.");
                Assert.That(pointer.enabled || pointer.gameObject.activeSelf, Is.False);
                Assert.That(slip.enabled || slip.gameObject.activeSelf, Is.False);
                Assert.That(scale.transform.parent, Is.SameAs(root.transform), "Under the module root: style gate and module scale apply.");
                Assert.That(scale.raycastTarget, Is.False);

                Roll(bank, 5f);
                Assert.That((bool)Get(scale, "PointerDrawn"), Is.True);
                Assert.That((bool)Get(scale, "BrickDrawn"), Is.False, "No measured slip yet: no brick (never a frozen centre).");
                Call(bank, "SetSlipData", .3f, true);
                Assert.That((bool)Get(scale, "BrickDrawn"), Is.True);
                Call(bank, "SetAttitudeValid", false);
                Assert.That((bool)Get(scale, "PointerDrawn"), Is.False);
                Assert.That((bool)Get(scale, "BrickDrawn"), Is.False);
                Call(bank, "SetAttitudeValid", true);
                Call(bank, "SetSlipData", float.NaN, false);
                Assert.That((bool)Get(scale, "PointerDrawn"), Is.True);
                Assert.That((bool)Get(scale, "BrickDrawn"), Is.False);
                Assert.That(pointer.enabled, Is.False, "The bitmap pointer is never re-enabled by validity changes.");
            }
            finally { UnityEngine.Object.DestroyImmediate(root); }
        }

        [Test]
        public void BrickRidesUnderThePointerWithTheClassicSign()
        {
            float radius = Const("Radius"), inset = Const("BrickCenterInset"), half = Const("BrickHalfWidth");
            Vector2 level = (Vector2)Call(Scale, "BrickCenter", 0f, .5f);
            Assert.That(level.x, Is.EqualTo(half).Within(.01f), "Half-scale slip moves the brick half a brick width, to the right (+x).");
            Assert.That(level.y, Is.EqualTo(radius - inset).Within(.01f));
            Vector2 banked = (Vector2)Call(Scale, "BrickCenter", 30f, 0f);
            Assert.That(BearingOf(banked), Is.EqualTo(-30f).Within(.01f), "Centred brick sits on the pointer's bearing.");
            Assert.That(banked.magnitude, Is.EqualTo(radius - inset).Within(.01f), "Same centre as the arc and pointer.");
            Vector2 full = (Vector2)Call(Scale, "BrickCenter", 0f, 3f);
            Assert.That(full.x, Is.EqualTo(2f * half).Within(.01f), "Travel is limited to one brick width.");
        }

        [Test]
        public void EndMarksAreProtectedAndTheLaneClearsTheFmaAndTheAttitudeField()
        {
            Component bank = NewProceduralBank(out GameObject root, out _, out _, out _, out Graphic scale);
            try
            {
                float radius = Const("Radius"), end = Const("ScaleEndDegrees"), tick = Const("MajorTickLength");
                for (int side = 0; side < 2; side++)
                {
                    object region = Call(scale, "GetEndMarkRegion", side);
                    var mark = (RectTransform)Call(scale, "EndMark", side);
                    Assert.That(region, Is.Not.Null);
                    Assert.That(mark, Is.Not.Null);
                    Assert.That(region.GetType().GetProperty("Id").GetValue(region), Is.EqualTo(side == 0 ? "bank-end-left" : "bank-end-right"));
                    Assert.That(region.GetType().GetProperty("Kind").GetValue(region).ToString(), Is.EqualTo("Symbology"),
                        "The conformal ladder and horizon break around the end marks.");
                    float sign = side == 0 ? -1f : 1f;
                    Vector2 expected = new Vector2(Mathf.Sin(sign * end * Mathf.Deg2Rad), Mathf.Cos(sign * end * Mathf.Deg2Rad)) * (radius + tick * .5f);
                    Assert.That(Vector2.Distance(mark.localPosition, expected), Is.LessThan(.05f), "End mark " + side + " sits on the 60 deg tick.");
                }

                float top = Const("ArcTopFromScreenTop");
                float indexTop = top - Const("IndexGap") - Const("IndexLength");
                Assert.That(indexTop, Is.GreaterThan((float)T("FAA.Customization.FaaNonConformalReflow").GetField("FmaLaneBottom").GetRawConstantValue()),
                    "The index stays below the FMA lane.");
                float arcEnd = top + radius - radius * Mathf.Cos(end * Mathf.Deg2Rad);
                Assert.That(arcEnd, Is.LessThanOrEqualTo((float)T("FAA.Customization.FaaNonConformalReflow").GetField("BankLaneBottom").GetRawConstantValue()),
                    "The arc ends inside the bank lane, above the protected attitude field.");
            }
            finally { UnityEngine.Object.DestroyImmediate(root); }
        }
    }
}
