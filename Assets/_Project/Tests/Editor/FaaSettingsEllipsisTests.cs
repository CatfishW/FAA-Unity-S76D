using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using NUnit.Framework;
using TMPro;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.TestTools;
using UnityEngine.UI;
using Object = UnityEngine.Object;

namespace FAA.Customization.Tests
{
    public sealed class FaaSettingsEllipsisTests
    {
        private const BindingFlags Any = BindingFlags.Instance | BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public;
        private static Type Owner => Type.GetType("FAA.Customization.FaaRadarControlsOverlay, Assembly-CSharp", true);
        private static readonly Regex EllipsisWarning = new Regex("The character used for Ellipsis is not available");
        private static object Call(object owner, string name, params object[] values) => owner.GetType().GetMethod(name, Any).Invoke(owner, values);
        private static TextOverflowModes Mode(TMP_Text label) => (TextOverflowModes)Owner.GetMethod("SettingsOverflowMode", Any).Invoke(null, new object[] { label });
        private static TMP_Text Text(Transform parent, string value, bool bold, float size = 17, float width = 110, float height = 34) =>
            (TMP_Text)Owner.GetMethod("SettingsText", Any).Invoke(null, new object[] { parent, "Measured setting", value, size, Vector2.zero, new Vector2(width, height), Color.white, bold });

        [Test] public void MissingLabelIsSafe() => Assert.That(Mode(null), Is.EqualTo(TextOverflowModes.Truncate));

        [TestCase(FontStyles.Normal, TextOverflowModes.Ellipsis)]
        [TestCase(FontStyles.Bold, TextOverflowModes.Truncate)]
        [TestCase(FontStyles.Italic, TextOverflowModes.Truncate)]
        public void ActualSyntheticStyleDeterminesAvailabilityWithoutChangingTypography(FontStyles style, TextOverflowModes expected)
        {
            var host = new GameObject("Overflow policy fixture", typeof(RectTransform), typeof(TextMeshProUGUI));
            try
            {
                var text = host.GetComponent<TMP_Text>(); text.font = TMP_Settings.defaultFontAsset;
                text.fontStyle = style; text.fontSize = 17; text.text = "320 NM";
                TMP_FontAsset font = text.font; Material material = text.fontSharedMaterial; Color color = text.color;
                Assert.That(Mode(text), Is.EqualTo(expected));
                Assert.That(text.font, Is.SameAs(font)); Assert.That(text.fontSharedMaterial, Is.SameAs(material));
                Assert.That(text.fontStyle, Is.EqualTo(style)); Assert.That(text.fontSize, Is.EqualTo(17)); Assert.That(text.color, Is.EqualTo(color));
            }
            finally { Object.DestroyImmediate(host); }
        }

        [TestCase(false)] [TestCase(true)]
        public void ValidExplicitTypefaceOrDeclaredFallbackRetainsEllipsis(bool throughFallback)
        {
            TMP_FontAsset source = TMP_Settings.defaultFontAsset;
            TMP_FontAsset styled = Object.Instantiate(source), primary = Object.Instantiate(source);
            var host = new GameObject("Valid styled fallback fixture", typeof(RectTransform), typeof(TextMeshProUGUI));
            try
            {
                var weights = styled.fontWeightTable; weights[7].regularTypeface = source;
                if (throughFallback) primary.fallbackFontAssetTable = new List<TMP_FontAsset> { styled };
                var text = host.GetComponent<TMP_Text>(); text.font = throughFallback ? primary : styled; text.fontStyle = FontStyles.Bold;
                Assert.That(Mode(text), Is.EqualTo(TextOverflowModes.Ellipsis), "A real styled glyph must keep ellipsis semantics.");
                Assert.That(source.fontWeightTable[7].regularTypeface, Is.Null, "The project font is never mutated.");
            }
            finally
            {
                Object.DestroyImmediate(host);
                // Instantiated font assets share the original atlas; the temporary asset must not own/destroy it.
                styled.atlasTextures = Array.Empty<Texture2D>(); primary.atlasTextures = Array.Empty<Texture2D>();
                styled.material = null; primary.material = null;
                Object.DestroyImmediate(styled); Object.DestroyImmediate(primary);
            }
        }

        [TestCase("WEATHER SETTINGS", 16f, 260f, 26f)] [TestCase("TRAFFIC SETTINGS", 16f, 260f, 26f)]
        [TestCase("320 NM", 17f, 110f, 34f)] [TestCase("1000 NM", 17f, 110f, 34f)]
        [TestCase("-15.0°", 17f, 110f, 34f)] [TestCase("+15.0°", 17f, 110f, 34f)]
        [TestCase("-8 dB", 17f, 110f, 34f)] [TestCase("+8 dB", 17f, 110f, 34f)]
        [TestCase("200 aircraft", 17f, 110f, 34f)] [TestCase("100%", 17f, 110f, 34f)]
        [TestCase("560 px", 17f, 110f, 34f)] [TestCase("12 rings", 17f, 110f, 34f)]
        public void WidestSettingsTitlesAndNumericStatusesRemainCompletelyLegible(string value, float size, float width, float height)
        {
            var host = new GameObject("Full settings value fixture", typeof(RectTransform), typeof(Canvas));
            host.GetComponent<Canvas>().renderMode = RenderMode.ScreenSpaceOverlay;
            try
            {
                TMP_Text text = Text(host.transform, value, true, size, width, height);
                text.font = TMP_Settings.defaultFontAsset; text.overflowMode = Mode(text); text.ForceMeshUpdate(true, true);
                Assert.That(text.preferredWidth, Is.LessThanOrEqualTo(width), value + " must fit without losing status characters.");
                Assert.That(text.preferredHeight, Is.LessThanOrEqualTo(height));
                Assert.That(text.textInfo.characterCount, Is.EqualTo(value.Length)); Assert.That(text.isTextOverflowing, Is.False);
            }
            finally { Object.DestroyImmediate(host); }
        }

        private sealed class Observer
        {
            public int Warnings;
            public void Log(string message, string stack, LogType type) { if (type == LogType.Warning && EllipsisWarning.IsMatch(message)) Warnings++; }
        }
        [Serializable] private sealed class Report { public string engine, graphics, baselineSha256; public int baselineWarnings, repairedWarnings, identicalNormalFrames, labels; public bool wrappedHelpPreserved; public string[] baselineHelpOverflow, baselineHelpWrap; }
        private static RectTransform Rect(string name, Transform parent, float x)
        {
            var host = new GameObject(name, typeof(RectTransform)); host.transform.SetParent(parent, false);
            var rect = (RectTransform)host.transform; rect.sizeDelta = new Vector2(476, 288); rect.anchoredPosition = new Vector2(x, 0); return rect;
        }
        private static void Configure(Component owner, RectTransform weather, RectTransform traffic)
        { Call(owner, "EnsureReadableWeatherControls", weather); Call(owner, "EnsureReadableTrafficControls", traffic); }
        private static byte[] Pixels(RenderTexture texture, Texture2D readback)
        {
            var old = RenderTexture.active; RenderTexture.active = texture; readback.ReadPixels(new Rect(0, 0, 1600, 900), 0, 0); readback.Apply(); RenderTexture.active = old;
            return readback.GetRawTextureData<byte>().ToArray();
        }
        [UnityTest, Explicit("Dedicated ordinary enabled-camera font/layout regression; run this exact method.")]
        public IEnumerator RepeatedSettingsRefreshHasNoWarningsAndMatchesAlreadyTruncatedNormalFramePixels()
        {
            if (SystemInfo.graphicsDeviceType == UnityEngine.Rendering.GraphicsDeviceType.Null) Assert.Ignore("Requires actual graphics device");
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single); yield return new EnterPlayMode();
            var cameraHost = new GameObject("Settings typography normal camera", typeof(Camera)); var view = cameraHost.GetComponent<Camera>();
            var canvasHost = new GameObject("Settings typography canvas", typeof(RectTransform), typeof(Canvas));
            var ownerHost = new GameObject("Inactive settings data owner"); ownerHost.SetActive(false); Component owner = ownerHost.AddComponent(Owner);
            var texture = new RenderTexture(1600, 900, 24); var readback = new Texture2D(1600, 900, TextureFormat.RGB24, false);
            var observer = new Observer(); var report = new Report { engine = Application.unityVersion, graphics = SystemInfo.graphicsDeviceName };
            string output = Path.GetFullPath(Path.Combine(Application.dataPath, "../../settings-font-gpu")); Directory.CreateDirectory(output);
            try
            {
                foreach (var script in Resources.FindObjectsOfTypeAll<MonoBehaviour>())
                    if (script != null && script.GetType().FullName == "FAA.Headset.XR3HeadsetCompatibility") script.enabled = false;
                view.orthographic = true; view.orthographicSize = 4.5f; view.aspect = 16f / 9f; view.cullingMask = 1 << 31;
                view.clearFlags = CameraClearFlags.SolidColor; view.backgroundColor = Color.black; view.allowHDR = false; view.allowMSAA = false;
                texture.Create(); view.targetTexture = texture;
                var canvas = canvasHost.GetComponent<Canvas>(); canvas.renderMode = RenderMode.WorldSpace; canvas.worldCamera = view;
                ((RectTransform)canvasHost.transform).sizeDelta = new Vector2(1600, 900); canvasHost.transform.localScale = Vector3.one * .01f; canvasHost.transform.position = new Vector3(0, 0, 10);
                RectTransform weather = Rect("WEATHER SETTINGS", canvasHost.transform, -300), traffic = Rect("TRAFFIC SETTINGS", canvasHost.transform, 300);
                Application.logMessageReceived += observer.Log; Configure(owner, weather, traffic);
                TMP_Text[] labels = canvasHost.GetComponentsInChildren<TMP_Text>(true); report.labels = labels.Length;
                string[] helpFields = { "_weatherHelp", "_trafficHelp" };
                var helpOverflow = new TextOverflowModes[2]; var helpWrap = new TextWrappingModes[2];
                report.baselineHelpOverflow = new string[2]; report.baselineHelpWrap = new string[2];
                for (int i = 0; i < helpFields.Length; i++)
                {
                    var help = (TMP_Text)Owner.GetField(helpFields[i], Any).GetValue(owner);
                    helpOverflow[i] = help.overflowMode; helpWrap[i] = help.textWrappingMode;
                    report.baselineHelpOverflow[i] = helpOverflow[i].ToString(); report.baselineHelpWrap[i] = helpWrap[i].ToString();
                }
                // Reproduce the old request and its actual TMP downgrade, never emulate a successful ellipsis glyph.
                int expectedBaselineWarnings = 0;
                foreach (TMP_Text label in labels)
                {
                    if ((label.fontStyle & FontStyles.Bold) != 0)
                    {
                        expectedBaselineWarnings++;
                        label.overflowMode = TextOverflowModes.Ellipsis; LogAssert.Expect(LogType.Warning, EllipsisWarning);
                    }
                    label.ForceMeshUpdate(true, true);
                }
                report.baselineWarnings = observer.Warnings;
                Assert.That(expectedBaselineWarnings, Is.GreaterThan(0));
                Assert.That(report.baselineWarnings, Is.EqualTo(expectedBaselineWarnings), "The standard grid retains its extra Max Targets value; count actual styled fields.");
                foreach (Transform child in canvasHost.GetComponentsInChildren<Transform>(true)) child.gameObject.layer = 31;
                yield return null; yield return null;
                byte[] baseline = Pixels(texture, readback);
                using (var hash = SHA256.Create()) report.baselineSha256 = BitConverter.ToString(hash.ComputeHash(baseline)).Replace("-", "").ToLowerInvariant();
                File.WriteAllBytes(Path.Combine(output, "already-truncated-baseline.png"), readback.EncodeToPNG());
                observer.Warnings = 0;
                for (int round = 0; round < 8; round++)
                {
                    Configure(owner, weather, traffic);
                    foreach (TMP_Text label in labels) label.ForceMeshUpdate(true, true);
                    yield return null; yield return null;
                    CollectionAssert.AreEqual(baseline, Pixels(texture, readback), "Refresh " + round + " must preserve actual text/font/layout pixels.");
                    report.identicalNormalFrames++;
                    for (int i = 0; i < helpFields.Length; i++)
                    {
                        var help = (TMP_Text)Owner.GetField(helpFields[i], Any).GetValue(owner);
                        Assert.That(help.textWrappingMode, Is.EqualTo(helpWrap[i])); Assert.That(help.overflowMode, Is.EqualTo(helpOverflow[i]), "Preserve this profile's existing normal-font guidance policy.");
                        Assert.That(help.textInfo.characterCount, Is.EqualTo(help.text.Length)); Assert.That(help.isTextOverflowing, Is.False, "Complete settings guidance must remain visible.");
                    }
                }
                report.wrappedHelpPreserved = true; report.repairedWarnings = observer.Warnings; Assert.That(report.repairedWarnings, Is.Zero);
                File.WriteAllBytes(Path.Combine(output, "repaired-identical-settings.png"), readback.EncodeToPNG());
                File.WriteAllText(Path.Combine(output, "settings-font-normal-render.json"), JsonUtility.ToJson(report, true));
                TestContext.WriteLine("Eight ordinary enabled-camera frames are byte-identical to TMP's already-truncated baseline; original styled-field warnings, 0 repaired warnings; wrapped help preserved.");
            }
            finally
            {
                Application.logMessageReceived -= observer.Log; view.targetTexture = null;
                Object.DestroyImmediate(canvasHost); Object.DestroyImmediate(ownerHost); Object.DestroyImmediate(cameraHost); Object.DestroyImmediate(texture); Object.DestroyImmediate(readback);
            }
            yield return new ExitPlayMode();
        }
    }
}
