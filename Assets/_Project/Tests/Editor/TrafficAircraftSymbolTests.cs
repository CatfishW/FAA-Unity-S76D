using System;
using System.Reflection;
using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.UI;

namespace FAA.Customization.Tests
{
    public class TrafficAircraftSymbolTests
    {
        private static Type Graphic => Type.GetType("IndicatorSystem.Display.TrafficAircraftSymbolGraphic, Assembly-CSharp", true);
        private static Type Data => Type.GetType("IndicatorSystem.Core.IndicatorData, Assembly-CSharp", true);
        private static Type Settings => Type.GetType("IndicatorSystem.Core.IndicatorSettings, Assembly-CSharp", true);
        private static Type Policy => Type.GetType("IndicatorSystem.Core.TrafficGlyphPolicy, Assembly-CSharp", true);
        private static Type Detail => Type.GetType("IndicatorSystem.Core.TrafficGlyphDetail, Assembly-CSharp", true);
        private const BindingFlags PrivateInstance = BindingFlags.NonPublic | BindingFlags.Instance;

        private static object Resolve(float distance, string previous = "MediumType", bool initialized = false, bool pose = true) =>
            Policy.GetMethod("ResolveDetail").Invoke(null, new object[] { distance, 5f, 40f, .25f, true, pose, Enum.Parse(Detail, previous), initialized });

        [TestCase(60f, "FarDoubleDiamond")]
        [TestCase(20f, "MediumType")]
        [TestCase(2f, "CloseAttitude")]
        public void DistanceSelectsTheExpectedInitialDetailTier(float distance, string expected) =>
            Assert.That(Resolve(distance).ToString(), Is.EqualTo(expected));

        [Test]
        public void HysteresisPreventsDistanceJitterButMissingAttitudeDowngradesImmediately()
        {
            Assert.That(Resolve(4.9f, "MediumType", true).ToString(), Is.EqualTo("MediumType"));
            Assert.That(Resolve(4.7f, "MediumType", true).ToString(), Is.EqualTo("CloseAttitude"));
            Assert.That(Resolve(5.1f, "CloseAttitude", true).ToString(), Is.EqualTo("CloseAttitude"));
            Assert.That(Resolve(5.3f, "CloseAttitude", true).ToString(), Is.EqualTo("MediumType"));
            Assert.That(Resolve(40.1f, "MediumType", true).ToString(), Is.EqualTo("MediumType"));
            Assert.That(Resolve(40.3f, "MediumType", true).ToString(), Is.EqualTo("FarDoubleDiamond"));
            Assert.That(Resolve(39.9f, "FarDoubleDiamond", true).ToString(), Is.EqualTo("FarDoubleDiamond"));
            Assert.That(Resolve(39.7f, "FarDoubleDiamond", true).ToString(), Is.EqualTo("MediumType"));
            Assert.That(Resolve(2f, "CloseAttitude", true, false).ToString(), Is.EqualTo("MediumType"));
        }

        [Test]
        public void FarSymbolUsesConcentricOuterAndInnerDiamondOutlines()
        {
            WithGraphic((component, settings) =>
            {
                Configure(component, Sample("Commercial",60,Quaternion.identity),settings);
                using var mesh = Mesh(component);
                Assert.That(mesh.currentVertCount, Is.EqualTo(64));
                Rect outer = Bounds(mesh,0,32), inner = Bounds(mesh,32,32);
                Assert.That(Vector2.Distance(outer.center,inner.center), Is.LessThan(.001f),
                    "The reference has nested diamonds with one center, not two offset diamonds.");
                Assert.That(outer.width, Is.GreaterThan(inner.width));
                Assert.That(outer.height, Is.GreaterThan(inner.height));
            });
        }

        [Test]
        public void HeadingTipIsAnOutwardNoseTriangleThatFollowsTheReportedHeading()
        {
            WithGraphic((component, settings) =>
            {
                Set(settings,"showTrafficHeadingTip",true);
                foreach (float heading in new[] { 0f, 90f })
                {
                    object data = Sample("Commercial",20,Quaternion.identity);
                    Data.GetField("RelativeHeadingDegrees").SetValue(data,heading);
                    Configure(component,data,settings);
                    using var mesh = Mesh(component);
                    Assert.That(ColorCount(mesh,Color.green), Is.EqualTo(3), "Only the nose triangle uses green in this style.");
                    var points = new Vector2[3]; int count = 0; UIVertex vertex = default;
                    for (int i = 0; i < mesh.currentVertCount; i++)
                    {
                        mesh.PopulateUIVertex(ref vertex,i);
                        if (vertex.color.r == 0 && vertex.color.g == 255 && vertex.color.b == 0) points[count++] = vertex.position;
                    }
                    Vector2 outward = points[0] - (points[1] + points[2]) * .5f;
                    Assert.That(Vector2.Angle(outward,heading == 0 ? Vector2.up : Vector2.right), Is.LessThan(.1f));
                    Assert.That(Mathf.Abs((points[1].x-points[0].x)*(points[2].y-points[0].y) -
                        (points[1].y-points[0].y)*(points[2].x-points[0].x)), Is.GreaterThan(1f));
                }
            });
        }

        [Test]
        public void TailOnlyVariantKeepsTheTailDiscWhenWingNavigationMarksAreOff()
        {
            WithGraphic((component, settings) =>
            {
                Set(settings,"showTrafficTailLight",true);
                Set(settings,"showNavigationLights",false);
                Set(settings,"showTrafficHeadingTip",false);
                Configure(component,Sample("Commercial",1,Quaternion.identity),settings);
                using var mesh = Mesh(component);
                Assert.That(ColorCount(mesh,Color.white), Is.GreaterThan(0));
                Assert.That(ColorCount(mesh,Color.red), Is.Zero);
                Assert.That(ColorCount(mesh,Color.green), Is.Zero);
            });
        }

        [TestCase("Commercial")]
        [TestCase("General")]
        [TestCase("Military")]
        [TestCase("Helicopter")]
        public void FilledMediumSymbolsHaveSolidFuselageInteriorsWhileOutlineVariantsRemainHollow(string aircraftType)
        {
            WithGraphic((component, settings) =>
            {
                object data = Sample(aircraftType,20,Quaternion.identity);
                Data.GetField("RelativeHeadingDegrees").SetValue(data,0f);
                // Stay inside the helicopter cabin, clear of its intentional rotor crossbar.
                Vector3 interior = aircraftType == "Helicopter" ? new Vector3(1.8f,2f,0) : Vector3.zero;
                Set(settings,"trafficSymbolsOutline",false);
                Configure(component,data,settings);
                using (var filled = Mesh(component))
                    Assert.That(TintCovers(filled,Projected(component,interior,false)),Is.True,
                        "FILLED must fill the aircraft body, not only the source SVG stroke ribbon.");
                Set(settings,"trafficSymbolsOutline",true);
                Configure(component,data,settings);
                using (var outlined = Mesh(component))
                    Assert.That(TintCovers(outlined,Projected(component,interior,false)),Is.False,
                        "OUTLINE must retain the clean outer contour without filling the fuselage.");
            });
        }

        [TestCase("Commercial", .42f, -.14f, .04f, 180f)]
        [TestCase("General", .3f, -.32f, -.12f, 180f)]
        [TestCase("Helicopter", .25f, -.28f, .2f, 90f)]
        public void CloseCategoryHardwareAppearsAtItsProjectedBodyLocation(string aircraftType,float x,float y,float z,float yaw)
        {
            WithGraphic((component, settings) =>
            {
                Configure(component,Sample(aircraftType,1,Quaternion.Euler(0,yaw,0)),settings);
                using var mesh = Mesh(component);
                Assert.That(TintCovers(mesh,Projected(component,new Vector3(x,y,z),true)),Is.True,
                    "Airliner engine pods, general-aircraft landing gear, and helicopter skids must be real projected geometry.");
            });
        }

        [Test]
        public void FighterRearHasTwoDarkExhaustOpeningsWithThreatColoredRims()
        {
            WithGraphic((component, settings) =>
            {
                Configure(component,Sample("Military",1,Quaternion.identity),settings);
                using var mesh = Mesh(component);
                foreach (float engine in new[] { -.14f,.14f })
                {
                    Assert.That(TintCovers(mesh,Projected(component,new Vector3(engine,-.025f,-1.175f),true)),Is.False);
                    Assert.That(TintCovers(mesh,Projected(component,new Vector3(engine+.075f,-.025f,-1.175f),true)),Is.True);
                }
            });
        }

        [TestCase("Commercial")]
        [TestCase("General")]
        [TestCase("Military")]
        [TestCase("Helicopter")]
        public void CloseVolumesStayVisibleInFrontRearSideAndBankedViews(string aircraftType)
        {
            WithGraphic((component, settings) =>
            {
                foreach (Quaternion rotation in new[] { Quaternion.identity, Quaternion.Euler(0,180,0), Quaternion.Euler(0,90,0), Quaternion.Euler(0,270,90), Quaternion.Euler(-25,35,60) })
                {
                    object data = Sample(aircraftType, 1, rotation);
                    Configure(component, data, settings);
                    Assert.That(Graphic.GetProperty("CurrentDetail").GetValue(component).ToString(), Is.EqualTo("CloseAttitude"));
                    using var mesh = Mesh(component);
                    Assert.That(mesh.currentVertCount, Is.GreaterThan(0));
                    Rect bounds = Bounds(mesh);
                    Assert.That(bounds.width, Is.GreaterThan(2f));
                    Assert.That(bounds.height, Is.GreaterThan(2f), "A real volume must not disappear at an edge-on viewing angle.");
                    AssertFootprint(mesh, 26f);
                }
            });
        }

        [TestCase("HasHeading")]
        [TestCase("HasPitch")]
        [TestCase("HasRoll")]
        public void MissingPoseMetadataFallsBackToTypeWithoutInventingAttitudeFromVerticalSpeed(string missing)
        {
            WithGraphic((component, settings) =>
            {
                object data = Sample("Commercial", 1, Quaternion.Euler(-15,30,20));
                Data.GetField(missing).SetValue(data, false);
                Data.GetField("HasVerticalSpeed").SetValue(data, true);
                Data.GetField("VerticalSpeedFpm").SetValue(data, 2000f);
                Configure(component, data, settings);
                Assert.That(Graphic.GetProperty("CurrentDetail").GetValue(component).ToString(), Is.EqualTo("MediumType"));
                using var mesh = Mesh(component);
                Assert.That(mesh.currentVertCount, Is.GreaterThan(0));
            });
        }

        [Test]
        public void UnknownClassStaysADiamondQuestionWithoutNavigationOrAttitudeClaims()
        {
            WithGraphic((component, settings) =>
            {
                Set(settings, "showNavigationLights", true); Set(settings, "showTrafficHeadingTip", true); Set(settings, "showTrafficTailLight", true);
                Configure(component, Sample("Unknown", 1, Quaternion.Euler(-15,50,70)), settings);
                Assert.That(Graphic.GetProperty("CurrentDetail").GetValue(component).ToString(), Is.EqualTo("FarDoubleDiamond"));
                using var mesh = Mesh(component);
                Assert.That(mesh.currentVertCount, Is.GreaterThan(16));
                Assert.That(ColorCount(mesh, Color.red), Is.Zero);
                Assert.That(ColorCount(mesh, Color.green), Is.Zero);
                Assert.That(ColorCount(mesh, Color.white), Is.Zero);
            });
        }

        [Test]
        public void InvalidQuaternionCannotEnableCloseAttitudeOrEmitNonFiniteVertices()
        {
            WithGraphic((component, settings) =>
            {
                foreach (Quaternion invalid in new[] { new Quaternion(0,0,0,0), new Quaternion(float.NaN,0,0,1), new Quaternion(float.MaxValue,0,0,1) })
                {
                    Configure(component, Sample("Commercial",1,invalid),settings);
                    Assert.That(Graphic.GetProperty("CurrentDetail").GetValue(component).ToString(), Is.EqualTo("MediumType"));
                    using var mesh = Mesh(component);
                    Assert.That(mesh.currentVertCount, Is.GreaterThan(0));
                    AssertFootprint(mesh,26f);
                }
            });
        }

        [Test]
        public void HeadingAndWingTailMarksUseTheTargetsBodyAxesRatherThanAnInvertedApproachTest()
        {
            WithGraphic((component, settings) =>
            {
                Set(settings, "showNavigationLights", true); Set(settings, "showTrafficHeadingTip", false); Set(settings, "showTrafficTailLight", true);
                Configure(component, Sample("Commercial", 1, Quaternion.identity), settings);
                using (var rear = Mesh(component))
                {
                    Assert.That(ColorCount(rear, Color.red), Is.Zero);
                    Assert.That(ColorCount(rear, Color.green), Is.Zero);
                    Assert.That(ColorCount(rear, Color.white), Is.GreaterThan(0), "A receding aircraft exposes the tail mark.");
                }
                Configure(component, Sample("Commercial", 1, Quaternion.Euler(0,180,0)), settings);
                using (var front = Mesh(component))
                {
                    Assert.That(ColorCount(front, Color.red), Is.GreaterThan(0));
                    Assert.That(ColorCount(front, Color.green), Is.GreaterThan(0));
                    Assert.That(ColorCount(front, Color.white), Is.Zero);
                    Assert.That(ColorMeanX(front, Color.red), Is.GreaterThan(ColorMeanX(front, Color.green)),
                        "When viewed head-on, target port/red appears on the observer's right.");
                }
                Configure(component, Sample("Commercial", 1, Quaternion.Euler(0,90,0)), settings);
                using (var side = Mesh(component))
                {
                    Assert.That(ColorCount(side, Color.red), Is.Zero);
                    Assert.That(ColorCount(side, Color.green), Is.GreaterThan(0));
                }
            });
        }

        [TestCase(false)]
        [TestCase(true)]
        public void ExtremeScaleAndOptionalStylesStayInsideTheAdvertisedFootprint(bool outline)
        {
            WithGraphic((component, settings) =>
            {
                Set(settings, "enableDistanceScaling", true); Set(settings, "closeDistanceScale", 100f); Set(settings, "farDistanceScale", 100f);
                Set(settings, "trafficSymbolsOutline", outline); Set(settings, "showTrafficBodyStripe", true);
                Set(settings, "showTrafficHeadingTip", true); Set(settings, "showNavigationLights", true); Set(settings, "showTrafficTailLight", true);
                Set(settings, "navLightSize", 20f);
                foreach (string type in new[] { "Commercial", "General", "Military", "Helicopter", "Unknown" })
                foreach (float distance in new[] { 1f, 20f, 60f })
                {
                    Graphic.GetMethod("ResetDetailHistory").Invoke(component, null);
                    Configure(component, Sample(type, distance, Quaternion.Euler(-25,70,40)), settings);
                    using var mesh = Mesh(component);
                    Assert.That(mesh.currentVertCount, Is.GreaterThan(0));
                    AssertFootprint(mesh, 40f);
                    Assert.That((float)Graphic.GetProperty("FootprintHalfExtent").GetValue(component), Is.LessThanOrEqualTo(40f));
                }
            });
        }

        [Test]
        public void ProximityNeverChangesThreatTintAndGraphicsRemainNonBlockingOnTheMrLayer()
        {
            WithGraphic((component, settings) =>
            {
                object data = Sample("Commercial", 1, Quaternion.identity);
                Data.GetField("Color").SetValue(data, Color.cyan);
                Configure(component, data, settings);
                using var mesh = Mesh(component);
                Assert.That(ColorCount(mesh, Color.cyan), Is.GreaterThan(0));
                Assert.That(ColorCount(mesh, Color.red), Is.Zero, "Closeness is not a threat classification.");
                Assert.That(((UnityEngine.UI.Graphic)component).raycastTarget, Is.False);
                Assert.That(component.gameObject.layer, Is.EqualTo(30));
            });
        }

        private static void WithGraphic(Action<Component, ScriptableObject> run)
        {
            var canvas = new GameObject("MR indicator canvas", typeof(RectTransform), typeof(Canvas));
            canvas.layer = 30;
            var host = new GameObject("Traffic glyph", typeof(RectTransform));
            host.transform.SetParent(canvas.transform, false);
            ((RectTransform)host.transform).sizeDelta = new Vector2(42,42);
            ScriptableObject settings = ScriptableObject.CreateInstance(Settings);
            try
            {
                Set(settings, "useAdaptiveTrafficSymbols", true); Set(settings, "enableDistanceScaling", false);
                Set(settings, "closeDistanceNM", 5f); Set(settings, "farDistanceNM", 40f); Set(settings, "trafficLodHysteresisNM", .25f);
                Set(settings, "showNavigationLights", false); Set(settings, "showTrafficHeadingTip", false); Set(settings, "showTrafficTailLight", false);
                Set(settings, "portLightColor", Color.red); Set(settings, "starboardLightColor", Color.green); Set(settings, "tailLightColor", Color.white);
                run(host.AddComponent(Graphic), settings);
            }
            finally { UnityEngine.Object.DestroyImmediate(canvas); UnityEngine.Object.DestroyImmediate(settings); }
        }

        private static object Sample(string aircraftType, float distance, Quaternion rotation)
        {
            object data = Activator.CreateInstance(Data);
            Data.GetField("Type").SetValue(data, Enum.Parse(Data.GetField("Type").FieldType, "Traffic"));
            Data.GetField("AircraftType").SetValue(data, Enum.Parse(Data.GetField("AircraftType").FieldType, aircraftType));
            Data.GetField("Id").SetValue(data, "TEST");
            Data.GetField("DistanceNM").SetValue(data, distance);
            Data.GetField("Color").SetValue(data, new Color(1,.65f,0,1));
            Data.GetField("HasHeading").SetValue(data, true); Data.GetField("HasPitch").SetValue(data, true); Data.GetField("HasRoll").SetValue(data, true);
            Data.GetField("Heading").SetValue(data, 30f); Data.GetField("PitchDegrees").SetValue(data, 5f); Data.GetField("RollDegrees").SetValue(data, 10f);
            Data.GetField("RelativeHeadingDegrees").SetValue(data, 30f); Data.GetField("ViewRelativeRotation").SetValue(data, rotation);
            return data;
        }
        private static void Set(object settings, string name, object value) => Settings.GetField(name).SetValue(settings, value);
        private static void Configure(Component graphic, object data, object settings) => Graphic.GetMethod("Configure").Invoke(graphic, new[] { data, settings });
        private static VertexHelper Mesh(Component graphic)
        {
            var mesh = new VertexHelper();
            Graphic.GetMethod("OnPopulateMesh", PrivateInstance | BindingFlags.DeclaredOnly,
                null, new[] { typeof(VertexHelper) }, null).Invoke(graphic, new object[] { mesh });
            return mesh;
        }
        private static Vector2 Projected(Component graphic,Vector3 point,bool volume) => (Vector2)Graphic.GetMethod("ProjectPoint",
            PrivateInstance|BindingFlags.DeclaredOnly,null,new[] { typeof(Vector3),typeof(bool) },null).Invoke(graphic,new object[] { point,volume });
        private static bool TintCovers(VertexHelper mesh,Vector2 point)
        {
            var stream = new List<UIVertex>(); mesh.GetUIVertexStream(stream);
            Color32 tint = new Color(1,.65f,0,1);
            for (int i = stream.Count-3; i >= 0; i-=3)
            {
                Color32 aColor = stream[i].color,bColor = stream[i+1].color,cColor = stream[i+2].color;
                Vector2 a = stream[i].position,b = stream[i+1].position,c = stream[i+2].position;
                if (Mathf.Abs(Cross(a,b,c)) < .000001f) continue;
                float ab = Cross(a,b,point),bc = Cross(b,c,point),ca = Cross(c,a,point);
                if (!((ab >= -.00001f && bc >= -.00001f && ca >= -.00001f) ||
                    (ab <= .00001f && bc <= .00001f && ca <= .00001f))) continue;
                return aColor.r == tint.r && aColor.g == tint.g && aColor.b == tint.b &&
                    bColor.r == tint.r && bColor.g == tint.g && bColor.b == tint.b &&
                    cColor.r == tint.r && cColor.g == tint.g && cColor.b == tint.b;
            }
            return false;
        }
        private static float Cross(Vector2 a,Vector2 b,Vector2 p) => (b.x-a.x)*(p.y-a.y)-(b.y-a.y)*(p.x-a.x);
        private static Rect Bounds(VertexHelper mesh, int start = 0, int count = -1)
        {
            Vector2 min = new Vector2(float.PositiveInfinity,float.PositiveInfinity), max = new Vector2(float.NegativeInfinity,float.NegativeInfinity);
            UIVertex vertex = default;
            int end = count < 0 ? mesh.currentVertCount : Mathf.Min(mesh.currentVertCount,start+count);
            for (int i = start; i < end; i++) { mesh.PopulateUIVertex(ref vertex, i); min = Vector2.Min(min,vertex.position); max = Vector2.Max(max,vertex.position); }
            return Rect.MinMaxRect(min.x,min.y,max.x,max.y);
        }
        private static void AssertFootprint(VertexHelper mesh, float extent)
        {
            Rect bounds = Bounds(mesh);
            Assert.That(bounds.xMin, Is.GreaterThanOrEqualTo(-extent - .01f)); Assert.That(bounds.xMax, Is.LessThanOrEqualTo(extent + .01f));
            Assert.That(bounds.yMin, Is.GreaterThanOrEqualTo(-extent - .01f)); Assert.That(bounds.yMax, Is.LessThanOrEqualTo(extent + .01f));
        }
        private static int ColorCount(VertexHelper mesh, Color color)
        {
            Color32 target = color; int count = 0; UIVertex vertex = default;
            for (int i = 0; i < mesh.currentVertCount; i++) { mesh.PopulateUIVertex(ref vertex,i); if (vertex.color.r == target.r && vertex.color.g == target.g && vertex.color.b == target.b) count++; }
            return count;
        }
        private static float ColorMeanX(VertexHelper mesh, Color color)
        {
            Color32 target = color; float total = 0; int count = 0; UIVertex vertex = default;
            for (int i = 0; i < mesh.currentVertCount; i++) { mesh.PopulateUIVertex(ref vertex,i); if (vertex.color.r == target.r && vertex.color.g == target.g && vertex.color.b == target.b) { total += vertex.position.x; count++; } }
            return total / Mathf.Max(1,count);
        }
    }
}
