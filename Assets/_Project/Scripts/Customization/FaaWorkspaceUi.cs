using TMPro;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.XR.Interaction.Toolkit.UI;
namespace FAA.Customization
{
    /// <summary>
    /// Shared builder and palette for the side 3D panels (settings, hand studio). One type scale and one control style everywhere:
    /// text never below <see cref="MinTextSize"/> (about 24 arcmin cap height at 1.5 m in XR), button captions auto-size within a row
    /// instead of depending on button width, selected state is shown by fill (<see cref="Selected"/>), and actions that remove primary
    /// flight data use a two-step <see cref="Guard"/> (arm, then confirm within <see cref="Guard.ConfirmSeconds"/>).
    /// </summary>
    public static class FaaWorkspaceUi
    {
        public static readonly Color Background=new(.025f,.044f,.065f,1), Card=new(.045f,.08f,.11f,1), Accent=new(.28f,.9f,.79f,1), Muted=new(.62f,.73f,.81f,1);
        /// <summary>Fill of the selected tab / segment / preset (state shown on the control itself).</summary>
        public static readonly Color Selected=new(.07f,.31f,.28f,1);
        /// <summary>Non-normal state text and armed guarded actions (matches the amber 'DATA:' status plate).</summary>
        public static readonly Color Caution=new(1f,.76f,.35f,1);
        public static readonly Color CautionFill=new(.36f,.24f,.04f,1);
        /// <summary>
        /// Smallest font size (panel units) on any side panel: FaaHudStyle.MinLabel plus one unit. The desktop inspection zoom fits a
        /// 640-unit panel into the clear column between the IAS and ALT readouts (about 600-620 reference px), i.e. about 0.95 reference
        /// px per panel unit, so a 16-unit floor still renders at or above MinLabel (15 reference px) on screen.
        /// </summary>
        public const float MinTextSize=FaaHudStyle.MinLabel+1f;
        /// <summary>Smallest auto-sized button caption.</summary>
        public const float MinCaptionSize=16f, MaxCaptionSize=22f;
        /// <summary>Smallest pointer target height on a world panel: 46 units is about 1.6 deg at 1.5 m, i.e. 52 px (26 reference px) on the 4K desktop view.</summary>
        public const float MinTargetHeight=46f;

        public static RectTransform Rect(string name,Transform parent,float x,float y,float width,float height)
        {
            var rect=(RectTransform)new GameObject(name,typeof(RectTransform)).transform;rect.SetParent(parent,false);
            rect.anchorMin=rect.anchorMax=new Vector2(0,1);rect.pivot=new Vector2(.5f,.5f);
            rect.anchoredPosition=new Vector2(x,-y);rect.sizeDelta=new Vector2(width,height);return rect;
        }
        public static Image Box(string name,Transform parent,float x,float y,float width,float height,Color tint,bool hit=false)
        {var rect=Rect(name,parent,x,y,width,height);var image=rect.gameObject.AddComponent<Image>();image.color=tint;image.raycastTarget=hit;return image;}
        public static TMP_Text Text(string name,Transform parent,string value,float x,float y,float width,float height,float size=18,bool left=false)
        {
            var rect=Rect(name,parent,x,y,width,height);var text=rect.gameObject.AddComponent<TextMeshProUGUI>();
            text.font=TMP_Settings.defaultFontAsset;text.fontSize=Mathf.Max(size,MinTextSize);text.text=value;text.richText=false;text.raycastTarget=false;
            text.color=Color.white;text.alignment=left?TextAlignmentOptions.MidlineLeft:TextAlignmentOptions.Center;
            text.textWrappingMode=TextWrappingModes.Normal;return text;
        }
        public static Button Button(string name,Transform parent,string caption,float x,float y,float width,float height,UnityEngine.Events.UnityAction action)
        {
            var image=Box(name,parent,x,y,width,height,Card,true);var button=image.gameObject.AddComponent<Button>();button.targetGraphic=image;
            var colors=button.colors;colors.highlightedColor=new Color(.72f,1,.95f);colors.pressedColor=new Color(.4f,.8f,.7f);colors.disabledColor=new Color(.3f,.3f,.3f);button.colors=colors;
            button.navigation=new Navigation{mode=Navigation.Mode.None};if(action!=null)button.onClick.AddListener(action);
            var label=Text(name+" Caption",image.transform,caption,width*.5f,height*.5f,width-12,height-4,CaptionSize(height));
            // One size per row regardless of button width: long captions shrink (never below the floor) instead of overflowing.
            label.textWrappingMode=TextWrappingModes.NoWrap;label.enableAutoSizing=true;
            label.fontSizeMin=MinCaptionSize;label.fontSizeMax=CaptionSize(height);return button;
        }
        /// <summary>Caption size for a button height (16..22 panel units).</summary>
        public static float CaptionSize(float height)=>Mathf.Clamp(height*.46f,MinCaptionSize,MaxCaptionSize);
        /// <summary>Single-glyph button (&lt; &gt; - +): a larger fixed glyph.</summary>
        public static Button Glyph(Button button,float size=28f)
        {
            var label=CaptionOf(button);if(label==null)return button;
            label.enableAutoSizing=false;label.fontSize=Mathf.Max(size,MinTextSize);return button;
        }
        public static TMP_Text CaptionOf(Button button)=>button!=null?button.GetComponentInChildren<TMP_Text>(true):null;
        /// <summary>Sets a caption only when it changed (no canvas rebuild or allocation at rest).</summary>
        public static void SetCaption(Button button,string caption){var label=CaptionOf(button);if(label!=null&&label.text!=caption)label.text=caption;}
        /// <summary>Shows a control's selected state by its fill.</summary>
        public static void SetSelected(Button button,bool selected)
        {
            if(button==null||button.targetGraphic==null)return;
            Color c=selected?Selected:Card;if(button.targetGraphic.color!=c)button.targetGraphic.color=c;
        }
        public static bool IsSelected(Button button)=>button!=null&&button.targetGraphic!=null&&button.targetGraphic.color==Selected;
        public static void SetActive(Component component,bool active){if(component!=null&&component.gameObject.activeSelf!=active)component.gameObject.SetActive(active);}
        public static void SetText(TMP_Text text,string value){if(text!=null&&text.text!=value)text.text=value;}
        public static void SetColor(Graphic graphic,Color color){if(graphic!=null&&graphic.color!=color)graphic.color=color;}
        public static Canvas Canvas(string name,Transform parent,int order)
        {
            var go=new GameObject(name,typeof(RectTransform),typeof(Canvas),typeof(FaaCanvasPixelRaycaster),typeof(TrackedDeviceGraphicRaycaster));
            go.transform.SetParent(parent,false);var canvas=go.GetComponent<Canvas>();canvas.renderMode=RenderMode.WorldSpace;canvas.sortingOrder=order;
            return canvas;
        }

        /// <summary>
        /// Two-step protection for an action that removes primary flight data (AC 20-175 inadvertent operation): the first press arms
        /// the control (amber fill, confirm caption), a second press within <see cref="ConfirmSeconds"/> executes, otherwise it disarms.
        /// Plain object, ticked by its owner at UI cadence; no per-frame work.
        /// </summary>
        public sealed class Guard
        {
            public const float ConfirmSeconds=3f;
            public readonly Button Button;
            private readonly string caption,confirmCaption;
            private readonly UnityEngine.Events.UnityAction action;
            private float deadline;
            public bool Armed{get;private set;}
            public Guard(Button button,string caption,string confirmCaption,UnityEngine.Events.UnityAction action)
            {Button=button;this.caption=caption;this.confirmCaption=confirmCaption;this.action=action;}
            /// <summary>Press at time <paramref name="now"/> (unscaled seconds). Returns true when the action executed.</summary>
            public bool Press(float now)
            {
                if(Armed&&now<=deadline){Disarm();action?.Invoke();return true;}
                Armed=true;deadline=now+ConfirmSeconds;
                SetCaption(Button,confirmCaption);if(Button!=null&&Button.targetGraphic!=null)Button.targetGraphic.color=CautionFill;
                var label=CaptionOf(Button);if(label!=null)label.color=Caution;
                return false;
            }
            public void Tick(float now){if(Armed&&now>deadline)Disarm();}
            public void Disarm()
            {
                Armed=false;SetCaption(Button,caption);
                if(Button!=null&&Button.targetGraphic!=null)Button.targetGraphic.color=Card;
                var label=CaptionOf(Button);if(label!=null)label.color=Color.white;
            }
        }
        public static Guard GuardedButton(string name,Transform parent,string caption,string confirmCaption,float x,float y,float width,float height,UnityEngine.Events.UnityAction action)
        {
            var button=Button(name,parent,caption,x,y,width,height,null);
            var guard=new Guard(button,caption,confirmCaption,action);
            button.onClick.AddListener(()=>guard.Press(Time.unscaledTime));
            return guard;
        }
    }
}
