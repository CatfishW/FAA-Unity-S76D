#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using IndicatorSystem.Core;
using IndicatorSystem.Display;
using TrafficRadar;
using UnityEditor;
using UnityEngine;
using UnityEngine.UI;

namespace IndicatorSystem.Editor
{
    public static class TrafficSymbologyReview
    {
        private const int Width = 1920, Height = 1280;
        private static readonly Color Cyan = new Color(.36f, .95f, .90f, 1);
        private static readonly List<UnityEngine.Object> Owned = new List<UnityEngine.Object>();
        private static Canvas canvas;
        private static Camera camera;
        private static IndicatorSettings Settings(bool outline = false)
        {
            var settings = ScriptableObject.CreateInstance<IndicatorSettings>(); Owned.Add(settings);
            settings.enableDistanceScaling = false; settings.trafficSymbolsOutline = outline;
            settings.globalScale = 1; return settings;
        }
        private static IndicatorData Example(TrafficRadarDataManager.AircraftType type, float distance, Quaternion pose, bool valid = true)
            => new IndicatorData { Id = "KEY-" + type + distance, Label = "KEY EXAMPLE", Type = IndicatorType.Traffic,
                AircraftType = type, Color = Cyan, DistanceNM = distance, IsActive = true,
                Visibility = IndicatorVisibility.OnScreen, HasHeading = true, Heading = 180,
                HasPitch = valid, HasRoll = valid, PitchDegrees = 0, RollDegrees = 0,
                ViewRelativeRotation = pose, RelativeHeadingDegrees = 0, ProjectionRect = new Rect(0, 0, Width, Height) };
        private static RectTransform Rect(string name, float x, float y, float width, float height)
        {
            var host = new GameObject(name, typeof(RectTransform)); host.layer = 30;
            host.transform.SetParent(canvas.transform, false);
            var rect = (RectTransform)host.transform; rect.sizeDelta = new Vector2(width, height);
            rect.anchoredPosition = new Vector2(x - Width * .5f, Height * .5f - y); return rect;
        }
        private static void Label(string text, float x, float y, float width, int size = 16)
        {
            var rect = Rect(text, x, y, width, size + 12); var label = rect.gameObject.AddComponent<Text>();
            label.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf"); label.fontSize = size;
            label.color = new Color(.87f, .94f, .98f); label.text = text; label.raycastTarget = false;
            label.alignment = TextAnchor.MiddleCenter; label.horizontalOverflow = HorizontalWrapMode.Wrap;
        }
        private static void Glyph(IndicatorData data, IndicatorSettings settings, float x, float y, float scale = 2f, bool offscreen = false)
        {
            var rect = Rect("Reference glyph " + data.Id, x, y, 42, 42); rect.localScale = Vector3.one * scale;
            var symbol = rect.gameObject.AddComponent<TrafficAircraftSymbolGraphic>(); symbol.Configure(data, settings);
            if (offscreen)
            {
                var arrowRect = Rect("Independent edge arrow", x, y, 108, 108); arrowRect.localScale = Vector3.one * scale;
                var arrow = arrowRect.gameObject.AddComponent<PilotCueSymbol>(); arrow.raycastTarget = false;
                arrow.ConfigureArrowOnly(true, 90, Cyan, 52);
            }
        }
        [MenuItem("FAA/Indicators/Render Reference Symbology Gallery")]
        public static void RenderGallery()
        {
            string output = Argument("-trafficGalleryOutput") ?? "_artifacts/traffic-symbols/gallery.png";
            try
            {
                Setup();
                Label("AIRCRAFT CUES · REFERENCE INTEGRATION", 960, 38, 1800, 30);
                Label("LABELLED SYMBOL KEY / TEST CASES · colors retain traffic-threat meaning · off-screen arrows remain independent", 960, 76, 1800, 18);
                string[] columns = { "FAR · DOUBLE DIAMOND", "MEDIUM · CATEGORY", "CLOSE · FRONT", "CLOSE · REAR", "CLOSE · SIDE", "CLOSE · BANK / PITCH", "OUTSIDE VIEW" };
                for (int c = 0; c < columns.Length; c++) Label(columns[c], 166 + c * 265, 132, 250, 16);
                var types = new[] { TrafficRadarDataManager.AircraftType.Commercial, TrafficRadarDataManager.AircraftType.Military,
                    TrafficRadarDataManager.AircraftType.General, TrafficRadarDataManager.AircraftType.Helicopter };
                string[] names = { "COMMERCIAL", "JET / MILITARY", "PROPELLER / GENERAL", "HELICOPTER" };
                var defaults = Settings(); defaults.enableDistanceScaling = true;
                for (int row = 0; row < 4; row++)
                {
                    float y = 235 + row * 185;
                    Quaternion[] poses = { Quaternion.identity, Quaternion.identity, Quaternion.Euler(0, 180, 0), Quaternion.identity,
                        Quaternion.Euler(0, 90, 0), Quaternion.Euler(-12, 145, -25), Quaternion.Euler(-8, 35, -12) };
                    for (int col = 0; col < columns.Length; col++)
                    {
                        float range = col == 0 ? 55 : col == 1 ? 15 : 1;
                        Glyph(Example(types[row], range, poses[col]), defaults, 166 + col * 265, y, 2, col == 6);
                    }
                    Label(names[row], 960, y + 78, 1100, 15);
                }
                Label("OPTIONAL VARIANTS · FILLED / OUTLINE · NOSE TIP · PORT / STARBOARD · TAIL · BODY STRIPE", 960, 935, 1800, 20);
                string[] variants = { "FILLED + MARKERS", "OUTLINE + MARKERS", "BODY CONTRAST", "NO WING MARKERS", "NO HEADING TIP", "ALERT TINT", "POSE MISSING → TYPE" };
                for (int i = 0; i < variants.Length; i++)
                {
                    var settings = Settings(i == 1); settings.showTrafficBodyStripe = i == 2;
                    settings.showNavigationLights = i != 3; settings.showTrafficHeadingTip = i != 4;
                    var data = Example(types[0], 1, Quaternion.Euler(-30, 20, -8), i != 6);
                    if (i == 5) { data.Color = new Color(1, .18f, .16f); data.Priority = 3; }
                    Glyph(data, settings, 166 + i * 265, 1040, 2);
                    Label(variants[i], 166 + i * 265, 1124, 250, 15);
                }
                Label("Far / medium / close thresholds reuse the cue settings. Missing or stale attitude never becomes a guessed pose.", 960, 1200, 1800, 20);
                Save(output); Debug.Log("[Traffic symbology] Rendered " + Path.GetFullPath(output));
            }
            finally { Cleanup(); }
        }
        public static void RenderKey()
        {
            string output = Argument("-trafficGalleryOutput") ?? "_artifacts/traffic-symbols/key.png";
            try
            {
                Setup(); var settings = Settings();
                var panel = TrafficSymbolOptionsPanel.Create(settings, canvas.transform);
                ((RectTransform)panel.transform).anchoredPosition = Vector2.zero;
                panel.transform.localScale = Vector3.one * 1.8f;
                Label("KEY / STYLE · ACTUAL RUNTIME PANEL", 960, 60, 1800, 28);
                Save(output);
            }
            finally { Cleanup(); }
        }
        private static void Setup()
        {
            var view = new GameObject("Symbology review camera", typeof(Camera)); Owned.Add(view); camera = view.GetComponent<Camera>();
            camera.stereoTargetEye = StereoTargetEyeMask.None; camera.orthographic = true; camera.orthographicSize = Height * .5f;
            camera.aspect = (float)Width / Height; camera.transform.position = new Vector3(0, 0, -1000);
            camera.nearClipPlane = .1f; camera.farClipPlane = 2000; camera.cullingMask = 1 << 30;
            camera.clearFlags = CameraClearFlags.SolidColor; camera.backgroundColor = new Color(.012f, .025f, .036f);
            var root = new GameObject("Symbology review canvas", typeof(RectTransform), typeof(Canvas)); Owned.Add(root); root.layer = 30;
            canvas = root.GetComponent<Canvas>(); canvas.renderMode = RenderMode.WorldSpace; canvas.worldCamera = camera;
            ((RectTransform)root.transform).sizeDelta = new Vector2(Width, Height);
        }
        private static void Save(string output)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output)));
            var texture = new RenderTexture(Width, Height, 24); var image = new Texture2D(Width, Height, TextureFormat.RGBA32, false);
            var previous = RenderTexture.active;
            try
            {
                camera.targetTexture = texture; texture.Create(); Canvas.ForceUpdateCanvases(); camera.Render();
                RenderTexture.active = texture; image.ReadPixels(new Rect(0, 0, Width, Height), 0, 0); image.Apply();
                File.WriteAllBytes(output, image.EncodeToPNG());
            }
            finally
            {
                RenderTexture.active = previous; camera.targetTexture = null; texture.Release();
                UnityEngine.Object.DestroyImmediate(texture); UnityEngine.Object.DestroyImmediate(image);
            }
        }
        private static void Cleanup()
        {
            for (int i = Owned.Count - 1; i >= 0; i--) if (Owned[i] != null) UnityEngine.Object.DestroyImmediate(Owned[i]);
            Owned.Clear(); canvas = null; camera = null;
        }
        private static string Argument(string name)
        {
            var args = Environment.GetCommandLineArgs(); for (int i = 0; i + 1 < args.Length; i++) if (args[i] == name) return args[i + 1]; return null;
        }
    }
}
#endif
