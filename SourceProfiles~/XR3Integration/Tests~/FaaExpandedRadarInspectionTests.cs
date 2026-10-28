using System;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.InputSystem.XR;
using UnityEngine.UI;

namespace FAA.Customization.Tests
{
    /// <summary>Project the real radar/drawer composite through the final desktop camera, independently of fit predictions.</summary>
    public sealed class FaaExpandedRadarInspectionTests
    {
        private const BindingFlags Any = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
        private static Type PanelType => Type.GetType("FAA.Customization.FaaSpatialRadarPanel, Assembly-CSharp", true);
        private static readonly Rect RequestedTarget = new Rect(100f, 100f, 800f, 600f);

        private sealed class Fixture : IDisposable
        {
            public readonly GameObject CanvasHost, CameraHost, CockpitHost;
            public readonly RectTransform CanvasRect, Radar, Header, Drawer;
            public readonly Camera View;
            public readonly RenderTexture Target;
            public readonly object Panel;
            public readonly Transform Cockpit;

            public Fixture(float yaw = 95f)
            {
                CameraHost = new GameObject("Desktop inspection eye", typeof(Camera));
                View = CameraHost.GetComponent<Camera>();
                View.fieldOfView = 60f;
                View.nearClipPlane = .03f;
                View.farClipPlane = 100f;
                View.transform.SetPositionAndRotation(new Vector3(10f, 3f, -4f), Quaternion.Euler(9f, 21f, 6f));
                Target = new RenderTexture(1920, 1080, 0);
                View.targetTexture = Target;
                View.aspect = 1920f / 1080f;
                CockpitHost = new GameObject("Cockpit inspection reference");
                Cockpit = CockpitHost.transform;
                Cockpit.SetPositionAndRotation(View.transform.position, Quaternion.Euler(2f, -11f, 3f));
                CanvasHost = new GameObject("Composite radar fixture", typeof(RectTransform), typeof(Canvas));
                CanvasRect = (RectTransform)CanvasHost.transform;
                CanvasRect.sizeDelta = new Vector2(1920f, 1080f);
                Canvas canvas = CanvasHost.GetComponent<Canvas>();
                canvas.renderMode = RenderMode.WorldSpace;
                canvas.worldCamera = View;
                Radar = ImageRect("Original radar scope", CanvasRect, Vector2.zero, new Vector2(320f, 320f));
                Header = ImageRect("Visible radar header", CanvasRect, new Vector2(0f, 230f), new Vector2(400f, 50f));
                Drawer = ImageRect("Expanded sibling drawer", CanvasRect, new Vector2(90f, 480f), new Vector2(480f, 288f));
                Panel = Activator.CreateInstance(PanelType, new object[] { null, "weather", canvas, Radar, yaw, false, .42f });
                Call("SetSpatial", true);
                ApplyNormal();
                Canvas.ForceUpdateCanvases();
            }

            private static RectTransform ImageRect(string name, Transform parent, Vector2 position, Vector2 size)
            {
                var go = new GameObject(name, typeof(RectTransform), typeof(Image));
                var rect = (RectTransform)go.transform;
                rect.SetParent(parent, false);
                rect.anchorMin = rect.anchorMax = rect.pivot = new Vector2(.5f, .5f);
                rect.anchoredPosition = position;
                rect.sizeDelta = size;
                go.GetComponent<Image>().color = Color.white;
                return rect;
            }

            public RectTransform AddGraphic(string name, Transform parent, Vector2 position, Vector2 size) => ImageRect(name, parent, position, size);
            public object Call(string method, params object[] args) => PanelType.GetMethod(method, Any).Invoke(Panel, args);
            public void ApplyNormal() => Call("Apply", Cockpit, View, false);
            public bool Inspect(bool nativeXr = false) => (bool)Call("ApplyDesktopInspection", View, RequestedTarget, nativeXr);
            public Rect VisibleRect()
            {
                object[] args = { default(Rect) };
                Assert.That((bool)PanelType.GetMethod("TryVisibleCanvasRect", Any).Invoke(Panel, args), Is.True);
                return (Rect)args[0];
            }

            public float[] LayoutSnapshot()
            {
                object layout = PanelType.GetField("Layout", Any).GetValue(Panel);
                return Array.ConvertAll(new[] { "yaw", "elevation", "distance", "scale" },
                    field => (float)layout.GetType().GetField(field, Any).GetValue(layout));
            }

            public void AssertRenderedCompositeFits() => AssertRenderedCompositeFits(RequestedTarget);

            public void AssertRenderedCompositeFits(Rect expectedBounds)
            {
                Assert.That(View.pixelWidth, Is.EqualTo(1920));
                Assert.That(View.pixelHeight, Is.EqualTo(1080));
                Assert.That(Quaternion.Angle(CanvasRect.rotation, View.transform.rotation), Is.LessThan(.01f),
                    "The entire original canvas, including its sibling drawer, is upright to the desktop eye.");
                var corners = new Vector3[4];
                foreach (RectTransform rect in new[] { Radar, Header, Drawer })
                {
                    if (!rect.gameObject.activeInHierarchy) continue;
                    rect.GetWorldCorners(corners);
                    foreach (Vector3 corner in corners)
                    {
                        Vector3 p = View.WorldToScreenPoint(corner);
                        Assert.That(p.z, Is.GreaterThan(View.nearClipPlane), rect.name + " must be in front of the eye.");
                        Assert.That(p.x, Is.InRange(expectedBounds.xMin - .5f, expectedBounds.xMax + .5f), rect.name + " horizontal fit");
                        Assert.That(p.y, Is.InRange(expectedBounds.yMin - .5f, expectedBounds.yMax + .5f), rect.name + " vertical fit");
                    }
                }
            }

            public void Dispose()
            {
                View.targetTexture = null;
                UnityEngine.Object.DestroyImmediate(CanvasHost);
                UnityEngine.Object.DestroyImmediate(CameraHost);
                UnityEngine.Object.DestroyImmediate(CockpitHost);
                UnityEngine.Object.DestroyImmediate(Target);
            }
        }

        [Test]
        public void VisibleCompositeBoundsShrinkOnCloseAndRefreshOnReopen()
        {
            using (var f = new Fixture())
            {
                Rect expanded = f.VisibleRect();
                f.Drawer.gameObject.SetActive(false);
                Rect closed = f.VisibleRect();
                Assert.That(expanded.yMax, Is.GreaterThan(closed.yMax + 100f), "The active sibling drawer must contribute its real extent.");
                Assert.That(expanded.width, Is.GreaterThan(closed.width), "Bounds must include the drawer overhang.");
                f.Drawer.gameObject.SetActive(true);
                Assert.That(f.VisibleRect(), Is.EqualTo(expanded), "Reopening must restore the current visible envelope, not a stale compact cache.");
            }
        }

        [Test]
        public void FullMapToolbarAndScrollViewportContributeButMaskedFarContentDoesNot()
        {
            using (var f = new Fixture())
            {
                f.Drawer.gameObject.SetActive(false);
                Rect compact = f.VisibleRect();
                RectTransform toolbar = f.AddGraphic("Active full-map toolbar", f.CanvasRect,
                    new Vector2(0f, 350f), new Vector2(796f, 110f));
                Rect withToolbar = f.VisibleRect();
                Assert.That(withToolbar.width, Is.GreaterThan(compact.width + 300f), "The active sibling toolbar is part of the composite.");
                Assert.That(withToolbar.yMax, Is.GreaterThan(compact.yMax + 100f));
                RectTransform viewport = f.AddGraphic("Visible scroll viewport", f.CanvasRect,
                    new Vector2(420f, 0f), new Vector2(240f, 180f));
                viewport.gameObject.AddComponent<RectMask2D>();
                ScrollRect scroll = viewport.gameObject.AddComponent<ScrollRect>();
                scroll.viewport = viewport;
                Rect viewportEnvelope = f.VisibleRect();
                Assert.That(viewportEnvelope.xMax, Is.GreaterThan(withToolbar.xMax + 100f), "The viewport's own visible plate must be included.");
                RectTransform farContent = f.AddGraphic("Clipped offscreen scroll content", viewport,
                    new Vector2(6000f, 5000f), new Vector2(640f, 480f));
                scroll.content = farContent;
                Assert.That(f.VisibleRect(), Is.EqualTo(viewportEnvelope), "Masked scroll content cannot enlarge the visible panel envelope.");
                Assert.That(toolbar.gameObject.activeInHierarchy, Is.True);
            }
        }

        [Test]
        public void OffscreenOrOversizedTargetClampsWholeCompositeInsideViewportAboveChromeAndBelowFma()
        {
            using (var f = new Fixture())
            {
                Type chrome = Type.GetType("FAA.Customization.FaaPilotChrome, Assembly-CSharp", true);
                float chromeTop = (float)chrome.GetField("BarBottom", Any).GetRawConstantValue() +
                    (float)chrome.GetField("BarHeight", Any).GetRawConstantValue();
                // Validate product-visible clear regions, independently of the implementation's extra padding.
                Rect visibleArea = Rect.MinMaxRect(0f, chromeTop, f.View.pixelWidth, f.View.pixelHeight - 80f);
                foreach (Rect target in new[] { new Rect(-4000f, -3000f, 6000f, 5000f), new Rect(3000f, 2000f, 200f, 150f) })
                {
                    Assert.That((bool)f.Call("ApplyDesktopInspection", f.View, target, false), Is.True);
                    f.AssertRenderedCompositeFits(visibleArea);
                    object[] args = { f.View, default(Rect) };
                    Assert.That((bool)PanelType.GetMethod("TryScreenRect", Any).Invoke(f.Panel, args), Is.True);
                    Rect screen = (Rect)args[1];
                    Assert.That(screen.xMin, Is.GreaterThanOrEqualTo(visibleArea.xMin - .5f));
                    Assert.That(screen.xMax, Is.LessThanOrEqualTo(visibleArea.xMax + .5f));
                    Assert.That(screen.yMin, Is.GreaterThanOrEqualTo(visibleArea.yMin - .5f), "Keep the whole composite above the pilot chrome.");
                    Assert.That(screen.yMax, Is.LessThanOrEqualTo(visibleArea.yMax + .5f), "Keep the whole composite below the FMA.");
                }
            }
        }

        [TestCase(95f)]
        [TestCase(-95f)]
        public void ExpandedCompositeFitsDesktopTargetUprightWithoutChangingCockpitLayout(float yaw)
        {
            using (var f = new Fixture(yaw))
            {
                float[] layout = f.LayoutSnapshot();
                Assert.That(f.Inspect(), Is.True);
                f.AssertRenderedCompositeFits();
                CollectionAssert.AreEqual(layout, f.LayoutSnapshot(), "Inspection presentation must not rewrite the saved cockpit layout.");
            }
        }

        [Test]
        public void NativeRequestRejectsDesktopPresentationWithoutChangingPoseOrLayout()
        {
            using (var f = new Fixture())
            {
                Vector3 position = f.CanvasRect.position, scale = f.CanvasRect.localScale;
                Quaternion rotation = f.CanvasRect.rotation;
                float[] layout = f.LayoutSnapshot();
                Assert.That(f.Inspect(true), Is.False);
                Assert.That(f.CanvasRect.position, Is.EqualTo(position));
                Assert.That(f.CanvasRect.rotation, Is.EqualTo(rotation));
                Assert.That(f.CanvasRect.localScale, Is.EqualTo(scale));
                CollectionAssert.AreEqual(layout, f.LayoutSnapshot());
            }
        }

        [Test]
        public void TrackedCameraRejectsDesktopPresentationWithoutChangingCanvasPose()
        {
            using (var f = new Fixture())
            {
                f.CameraHost.AddComponent<TrackedPoseDriver>();
                Vector3 position = f.CanvasRect.position, scale = f.CanvasRect.localScale;
                Quaternion rotation = f.CanvasRect.rotation;
                Assert.That(f.Inspect(), Is.False);
                Assert.That(f.CanvasRect.position, Is.EqualTo(position));
                Assert.That(f.CanvasRect.rotation, Is.EqualTo(rotation));
                Assert.That(f.CanvasRect.localScale, Is.EqualTo(scale));
            }
        }

        [Test]
        public void NormalApplyRestoresPhysicalCockpitPoseAfterDesktopPresentation()
        {
            using (var f = new Fixture())
            {
                Vector3 position = f.CanvasRect.position, scale = f.CanvasRect.localScale;
                Quaternion rotation = f.CanvasRect.rotation;
                float[] layout = f.LayoutSnapshot();
                Assert.That(f.Inspect(), Is.True);
                f.ApplyNormal();
                Assert.That(Vector3.Distance(f.CanvasRect.position, position), Is.LessThan(.0001f));
                Assert.That(Quaternion.Angle(f.CanvasRect.rotation, rotation), Is.LessThan(.01f));
                Assert.That(Vector3.Distance(f.CanvasRect.localScale, scale), Is.LessThan(.00001f));
                CollectionAssert.AreEqual(layout, f.LayoutSnapshot());
            }
        }

        [Test]
        public void ClosingThenReopeningDrawerRefitsCurrentCompositeWithoutAccumulatedTransformDrift()
        {
            using (var f = new Fixture())
            {
                Assert.That(f.Inspect(), Is.True);
                f.AssertRenderedCompositeFits();
                Vector3 expandedPosition = f.CanvasRect.position, expandedScale = f.CanvasRect.localScale;
                f.Drawer.gameObject.SetActive(false);
                Assert.That(f.Inspect(), Is.True);
                f.AssertRenderedCompositeFits();
                f.Drawer.gameObject.SetActive(true);
                Assert.That(f.Inspect(), Is.True);
                f.AssertRenderedCompositeFits();
                Assert.That(Vector3.Distance(f.CanvasRect.position, expandedPosition), Is.LessThan(.0001f));
                Assert.That(Vector3.Distance(f.CanvasRect.localScale, expandedScale), Is.LessThan(.00001f));
            }
        }
    }
}
