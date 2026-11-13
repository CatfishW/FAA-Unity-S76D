using System.Collections.Generic;
using System.Globalization;
using FAA.XPlaneIntegration.Runtime;
using HUDControl.Elements;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace FAA.Customization
{
    /// <summary>
    /// Presentation only: flight values and unavailable-data states remain owned by the existing HUD elements.
    /// IAS and ALT boxes are mirror images (same size), carry one caption pattern ("IAS KT", "ALT FT"), and sit on one dark plate that
    /// covers the digits and the caption, with a full-strength halo, so they stay legible over haze, terrain and the horizon.
    /// The airspeed element bound here is the one that writes the visible digits (not merely the nearest one in the hierarchy), and its
    /// format never carries a leading zero. While a side panel is inspected the digits, plate, brackets and alert box keep
    /// max(forward, <see cref="FaaHudInspection.AwarenessHudIntensity"/>); the caption follows the forward intensity.
    /// The altitude box adds an "AGL nnn" line on its own plate only below 1,000 ft above ground (automatic declutter).
    /// IAS above the DEMONSTRATOR Vne (<see cref="FaaRotorcraftLimits.VneKnots"/>, not RFM data) turns the digits and brackets red and
    /// draws a full red box that flashes for <see cref="FaaHudStyle.BlinkSeconds"/> and then stays steady.
    /// Authored in edit mode as well as in play; objects added in edit mode are not saved into the scene.
    /// </summary>
    [ExecuteAlways, DisallowMultipleComponent]
    public sealed class FaaPrimaryFlightReadout : MonoBehaviour
    {
        public const float ValueWidth = 154f, ValueHeight = 56f, UnitOffset = 46f, UnitHeight = 26f, AglOffset = 74f;
        /// <summary>AGL appears below this height and is removed above <see cref="AglHideAboveFeet"/> (hysteresis).</summary>
        public const float AglShowBelowFeet = 1000f, AglHideAboveFeet = 1100f;
        /// <summary>No leading zero on airspeed (leading zeros are reserved for directions); fixed digit cells stop the value shifting.</summary>
        public const string AirspeedFormat = "<mspace=0.62em>{0:0}</mspace>";
        /// <summary>Thickness of the Vne exceedance box (heavier than the 1.5-unit brackets).</summary>
        public const float AlertBoxWidth = 2.5f;
        private const float FrameAlpha = 1f, BracketWidth = 1.5f;
        private static readonly string[] AlertEdges = { "Alert Top", "Alert Bottom", "Alert Left", "Alert Right" };

        [SerializeField] private TMP_Text valueText;
        [SerializeField] private TMP_Text unitText;
        [SerializeField] private bool altitude;
        private TMP_Text aglText;
        private Image plate, aglPlate;
        private readonly List<Image> frameLines = new();
        private readonly List<Image> alertLines = new();
        private AirspeedIndicatorElement airspeedElement;
        private XPlane12ApiHudBridge bridge;
        private float nextBridgeSearch, nextElementSearch, warningOnset = -100f;
        private bool aglShown, warning;
        private int aglKey = int.MaxValue;

        public RectTransform ValueRect => valueText != null ? valueText.rectTransform : null;
        public bool IsAltitude => altitude;
        public string AglText => aglText != null ? aglText.text : "";
        /// <summary>The airspeed element whose readout is this box's digits (null for altitude or when none drives them).</summary>
        public AirspeedIndicatorElement BoundAirspeedElement => airspeedElement;
        /// <summary>True while the IAS readout shows the Vne exceedance coding.</summary>
        public bool VneWarning => warning;

        public void Configure(TMP_Text value, TMP_Text units, bool isAltitude)
        {
            valueText = value;
            unitText = units;
            altitude = isAltitude;
            ApplyPresentation();
        }

        private void OnEnable() => ApplyPresentation();

        /// <summary>The airspeed text both styles show: integer knots, no leading zero; "---" when invalid (m1: one IAS formatter).</summary>
        public static string FormatAirspeed(float knots, bool valid) => valid && !float.IsNaN(knots) && !float.IsInfinity(knots)
            ? Mathf.RoundToInt(Mathf.Max(0f, knots)).ToString(CultureInfo.InvariantCulture) : FaaDigitalColumnStyle.Invalid;

        /// <summary>
        /// The airspeed element that writes <paramref name="value"/>: an ancestor whose readout is that text, else any scene element whose
        /// readout is that text, else the nearest enabled ancestor. A disabled duplicate with no readout is never chosen over the live one.
        /// </summary>
        public static AirspeedIndicatorElement ResolveAirspeedElement(Transform from, TMP_Text value)
        {
            if (from == null) return null;
            AirspeedIndicatorElement enabledAncestor = null, anyAncestor = null;
            foreach (var element in from.GetComponentsInParent<AirspeedIndicatorElement>(true))
            {
                if (element == null) continue;
                if (value != null && element.Readout == value) return element;
                if (enabledAncestor == null && element.enabled) enabledAncestor = element;
                if (anyAncestor == null) anyAncestor = element;
            }
            if (value != null)
                foreach (var element in FindObjectsByType<AirspeedIndicatorElement>(FindObjectsInactive.Include, FindObjectsSortMode.None))
                    if (element != null && element.Readout == value && element.gameObject.scene == from.gameObject.scene) return element;
            return enabledAncestor != null ? enabledAncestor : anyAncestor;
        }

        public void ApplyPresentation()
        {
            if (valueText == null || unitText == null) return;
            RectTransform value = valueText.rectTransform;
            // Mirror-image boxes: IAS and ALT share one width so the Basic-T reads symmetrically.
            value.sizeDelta = new Vector2(ValueWidth, ValueHeight);
            valueText.fontSize = FaaDigitalColumnStyle.ReadoutSize;
            valueText.fontStyle = FontStyles.Normal;
            valueText.alignment = TextAlignmentOptions.Center;
            valueText.enableAutoSizing = false;
            valueText.textWrappingMode = TextWrappingModes.NoWrap;
            valueText.overflowMode = TextOverflowModes.Overflow;
            valueText.richText = true;
            valueText.raycastTarget = false;
            valueText.characterSpacing = 0f;
            // Do not change its color: loss-of-data and exceedance colouring are owned by the HUD element.
            FaaHudStyle.ApplyHalo(valueText, FaaDigitalColumnStyle.HaloStrength);
            RectTransform units = unitText.rectTransform;
            units.anchorMin = value.anchorMin;
            units.anchorMax = value.anchorMax;
            units.pivot = value.pivot;
            units.localScale = value.localScale;
            units.anchoredPosition = value.anchoredPosition + new Vector2(0f, -UnitOffset * value.localScale.y);
            units.sizeDelta = new Vector2(ValueWidth, UnitHeight);
            unitText.text = altitude ? FaaDigitalColumnStyle.AltitudeCaption : FaaDigitalColumnStyle.AirspeedCaption;
            unitText.fontSize = FaaDigitalColumnStyle.LabelSize;
            unitText.fontStyle = FontStyles.Normal;
            unitText.characterSpacing = 1f;
            unitText.alignment = TextAlignmentOptions.Center;
            unitText.enableAutoSizing = false;
            unitText.textWrappingMode = TextWrappingModes.NoWrap;
            unitText.raycastTarget = false;
            unitText.color = FaaHudStyle.WithAlpha(FaaDigitalColumnStyle.Normal, FaaDigitalColumnStyle.TitleAlpha);
            FaaHudStyle.ApplyHalo(unitText, FaaDigitalColumnStyle.HaloStrength);

            RectTransform frame = Child(transform, "FAA Readout Frame");
            frame.anchorMin = value.anchorMin;
            frame.anchorMax = value.anchorMax;
            frame.pivot = value.pivot;
            frame.anchoredPosition = value.anchoredPosition;
            frame.sizeDelta = value.sizeDelta + new Vector2(12f, 4f);
            frame.localScale = value.localScale;
            frame.SetAsFirstSibling();
            // One dark plate behind the digits AND the caption (frame-local units; the caption sits UnitOffset below the digits).
            // The name keeps SymbologyColorManager from tinting it ("background").
            float top = frame.sizeDelta.y * .5f, bottom = -(UnitOffset + UnitHeight * .5f + 2f);
            RectTransform backing = Child(frame, "Contrast Background");
            backing.anchorMin = backing.anchorMax = backing.pivot = new Vector2(.5f, .5f);
            backing.anchoredPosition = new Vector2(0f, (top + bottom) * .5f);
            backing.sizeDelta = new Vector2(frame.sizeDelta.x, top - bottom);
            backing.localScale = Vector3.one;
            backing.SetAsFirstSibling();
            plate = backing.GetComponent<Image>();
            if (plate == null) plate = backing.gameObject.AddComponent<Image>();
            plate.color = FaaDigitalColumnStyle.Plate;
            plate.raycastTarget = false;
            float halfWidth = frame.sizeDelta.x * .5f, halfHeight = top - 4f;
            Color line = FaaHudStyle.WithAlpha(FaaDigitalColumnStyle.Normal, FrameAlpha);
            frameLines.Clear();
            frameLines.Add(Line(frame, "Left", new Vector2(-halfWidth, 0), new Vector2(BracketWidth, halfHeight * 2f), line));
            frameLines.Add(Line(frame, "Right", new Vector2(halfWidth, 0), new Vector2(BracketWidth, halfHeight * 2f), line));
            frameLines.Add(Line(frame, "Top Left", new Vector2(-halfWidth + 4f, halfHeight), new Vector2(8f, BracketWidth), line));
            frameLines.Add(Line(frame, "Top Right", new Vector2(halfWidth - 4f, halfHeight), new Vector2(8f, BracketWidth), line));
            frameLines.Add(Line(frame, "Bottom Left", new Vector2(-halfWidth + 4f, -halfHeight), new Vector2(8f, BracketWidth), line));
            frameLines.Add(Line(frame, "Bottom Right", new Vector2(halfWidth - 4f, -halfHeight), new Vector2(8f, BracketWidth), line));
            // Full Vne box inside the frame rect (so the layout bounds never change); transparent until IAS exceeds Vne.
            alertLines.Clear();
            float edge = AlertBoxWidth * .5f;
            alertLines.Add(Line(frame, AlertEdges[0], new Vector2(0f, top - edge), new Vector2(halfWidth * 2f, AlertBoxWidth), Color.clear));
            alertLines.Add(Line(frame, AlertEdges[1], new Vector2(0f, -top + edge), new Vector2(halfWidth * 2f, AlertBoxWidth), Color.clear));
            alertLines.Add(Line(frame, AlertEdges[2], new Vector2(-halfWidth + edge, 0f), new Vector2(AlertBoxWidth, top * 2f), Color.clear));
            alertLines.Add(Line(frame, AlertEdges[3], new Vector2(halfWidth - edge, 0f), new Vector2(AlertBoxWidth, top * 2f), Color.clear));

            if (altitude)
            {
                RectTransform agl = Child(transform, "AGL Readout");
                agl.anchorMin = value.anchorMin;
                agl.anchorMax = value.anchorMax;
                agl.pivot = value.pivot;
                agl.localScale = value.localScale;
                agl.anchoredPosition = value.anchoredPosition + new Vector2(0f, -AglOffset * value.localScale.y);
                agl.sizeDelta = new Vector2(ValueWidth, UnitHeight);
                // Its own plate, inside the reserved AGL row, drawn just before the text; transparent while AGL is hidden.
                RectTransform aglBacking = Child(transform, "AGL Contrast Background");
                aglBacking.anchorMin = value.anchorMin;
                aglBacking.anchorMax = value.anchorMax;
                aglBacking.pivot = value.pivot;
                aglBacking.localScale = value.localScale;
                aglBacking.anchoredPosition = agl.anchoredPosition;
                aglBacking.sizeDelta = new Vector2(ValueWidth - 26f, UnitHeight);
                if (aglBacking.GetSiblingIndex() > agl.GetSiblingIndex()) aglBacking.SetSiblingIndex(agl.GetSiblingIndex());
                aglPlate = aglBacking.GetComponent<Image>();
                if (aglPlate == null) aglPlate = aglBacking.gameObject.AddComponent<Image>();
                aglPlate.raycastTarget = false;
                aglPlate.color = Color.clear;
                aglText = agl.GetComponent<TMP_Text>();
                if (aglText == null) aglText = agl.gameObject.AddComponent<TextMeshProUGUI>();
                if (aglText.font == null) aglText.font = unitText.font != null ? unitText.font : TMP_Settings.defaultFontAsset;
                aglText.fontSize = FaaDigitalColumnStyle.LabelSize;
                aglText.fontStyle = FontStyles.Normal;
                aglText.characterSpacing = 1f;
                aglText.alignment = TextAlignmentOptions.Center;
                aglText.enableAutoSizing = false;
                aglText.richText = false;
                aglText.textWrappingMode = TextWrappingModes.NoWrap;
                aglText.overflowMode = TextOverflowModes.Overflow;
                aglText.raycastTarget = false;
                aglText.color = FaaHudStyle.WithAlpha(FaaDigitalColumnStyle.Normal, FaaDigitalColumnStyle.TitleAlpha);
                // The row stays reserved (empty text) so the column never jumps when AGL appears.
                aglText.text = ""; aglKey = int.MaxValue; aglShown = false;
                FaaHudStyle.ApplyHalo(aglText, FaaDigitalColumnStyle.HaloStrength);
            }
            else BindAirspeedElement();
        }

        /// <summary>Binds the element that drives the visible digits and removes zero padding from its format (M1).</summary>
        private void BindAirspeedElement()
        {
            airspeedElement = ResolveAirspeedElement(transform, valueText);
            if (airspeedElement != null) airspeedElement.SetDisplayFormat(AirspeedFormat);
        }

        private void LateUpdate()
        {
            if (valueText == null || unitText == null) return;
            // Re-bind if a setup step re-pointed the readout (throttled; reference compare otherwise).
            if (!altitude && (airspeedElement == null || airspeedElement.Readout != valueText) && Time.realtimeSinceStartup >= nextElementSearch)
            {
                nextElementSearch = Time.realtimeSinceStartup + 2f;
                BindAirspeedElement();
            }
            Color normal = FaaDigitalColumnStyle.Normal;
            float forward = FaaDigitalColumnStyle.DetailIntensity(transform), alpha = FaaDigitalColumnStyle.AwarenessIntensity(forward);
            // Re-assert the halo: SymbologyColorManager may swap in a per-text material when the pilot changes colour.
            FaaHudStyle.ApplyHalo(valueText, FaaDigitalColumnStyle.HaloStrength);
            FaaHudStyle.ApplyHalo(unitText, FaaDigitalColumnStyle.HaloStrength);
            Color caption = FaaHudStyle.WithAlpha(normal, FaaDigitalColumnStyle.TitleAlpha);
            if (unitText.color != caption) unitText.color = caption;
            bool exceed = !altitude && airspeedElement != null && airspeedElement.VneExceeded;
            if (exceed && !warning) warningOnset = Time.unscaledTime;
            warning = exceed;
            // Brackets stay red while exceeding; the full box flashes and then stays steady. Bounds never change.
            Color frame = exceed ? FaaHudStyle.Red : FaaHudStyle.WithAlpha(normal, FrameAlpha);
            Color box = exceed && FaaHudStyle.BlinkVisible(warningOnset) ? FaaHudStyle.Red : Color.clear;
            Tint(frameLines, frame, alpha);
            Tint(alertLines, box, alpha);
            if (plate != null)
            {
                if (plate.color != FaaDigitalColumnStyle.Plate) plate.color = FaaDigitalColumnStyle.Plate;
                FaaDigitalColumnStyle.SetRendererAlpha(plate.canvasRenderer, alpha);
            }
            FaaDigitalColumnStyle.SetRendererAlpha(valueText.canvasRenderer, alpha);
            FaaDigitalColumnStyle.SetRendererAlpha(unitText.canvasRenderer, forward);
            if (altitude && aglText != null) UpdateAgl(caption, alpha);
        }

        private static void Tint(List<Image> lines, Color color, float alpha)
        {
            for (int i = 0; i < lines.Count; i++)
            {
                var line = lines[i];
                if (line == null) continue;
                if (line.color != color) line.color = color;
                FaaDigitalColumnStyle.SetRendererAlpha(line.canvasRenderer, alpha);
            }
        }

        /// <summary>Part-time AGL (AC 25-11B: shown automatically when relevant). Hysteresis keeps it from blinking near the threshold.</summary>
        public static bool ShouldShowAgl(float feet, bool valid, bool wasShown) =>
            valid && !float.IsNaN(feet) && !float.IsInfinity(feet) && feet >= 0f && feet < (wasShown ? AglHideAboveFeet : AglShowBelowFeet);

        /// <summary>5 ft steps below 200 ft, 10 ft above, so the last digit does not flicker.</summary>
        public static int RoundAgl(float feet) => feet < 200f ? Mathf.RoundToInt(feet / 5f) * 5 : Mathf.RoundToInt(feet / 10f) * 10;

        private void UpdateAgl(Color caption, float alpha)
        {
            if (bridge == null && Application.isPlaying && Time.unscaledTime >= nextBridgeSearch)
            {
                nextBridgeSearch = Time.unscaledTime + 1f;
                bridge = FindAnyObjectByType<XPlane12ApiHudBridge>();
            }
            var data = bridge != null ? bridge.LatestFlightData : null;
            bool valid = Application.isPlaying && bridge != null && bridge.IsFeedHealthy && data != null && data.altitudeAGLValid;
            float feet = valid ? data.altitudeAGL : 0f;
            aglShown = ShouldShowAgl(feet, valid, aglShown);
            int key = aglShown ? RoundAgl(feet) : int.MinValue;
            if (key != aglKey)
            {
                aglKey = key;
                aglText.text = aglShown ? "AGL " + key.ToString(CultureInfo.InvariantCulture) : "";
            }
            if (aglText.color != caption) aglText.color = caption;
            FaaHudStyle.ApplyHalo(aglText, FaaDigitalColumnStyle.HaloStrength);
            FaaDigitalColumnStyle.SetRendererAlpha(aglText.canvasRenderer, alpha);
            if (aglPlate != null)
            {
                Color backing = aglShown ? FaaDigitalColumnStyle.Plate : Color.clear;
                if (aglPlate.color != backing) aglPlate.color = backing;
                FaaDigitalColumnStyle.SetRendererAlpha(aglPlate.canvasRenderer, alpha);
            }
        }

        private static RectTransform Child(Transform parent, string name)
        {
            Transform found = parent.Find(name);
            RectTransform rect = found != null ? found.GetComponent<RectTransform>() : null;
            if (rect == null)
            {
                var go = new GameObject(name, typeof(RectTransform));
                // Edit-mode preview objects are rebuilt on enable and never written into the scene file.
                if (!Application.isPlaying) go.hideFlags = HideFlags.DontSave;
                rect = go.GetComponent<RectTransform>();
            }
            rect.SetParent(parent, false);
            return rect;
        }

        private static Image Line(RectTransform parent, string name, Vector2 position, Vector2 size, Color color)
        {
            RectTransform rect = Child(parent, name);
            rect.anchorMin = rect.anchorMax = rect.pivot = new Vector2(.5f, .5f);
            rect.anchoredPosition = position;
            rect.sizeDelta = size;
            rect.localScale = Vector3.one;
            Image image = rect.GetComponent<Image>();
            if (image == null) image = rect.gameObject.AddComponent<Image>();
            image.color = color;
            image.raycastTarget = false;
            return image;
        }
    }
}
