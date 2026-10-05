using System.Collections.Generic;
using System.Text;
using TMPro;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.UI;

namespace FAA.Customization
{
    /// <summary>
    /// Merged status chip (left end of the bar). Each subsystem reports one entry; the chip shows the most severe entry
    /// in its alert colour plus, when it fits, the next one in quiet text, so one fault is never annunciated twice and
    /// unrelated faults are not hidden. All-nominal condenses to a short quiet "DATA LIVE" with a green dot.
    /// It is drawn as an annunciator, not a button: no button plate, no chevron (the chevrons are reserved for the view
    /// buttons' turn direction), an alert-coloured box and left bar only for cautions and warnings. A click still opens
    /// the data-source settings page as a shortcut.
    /// Severity rises immediately; it falls only after <see cref="StatusClearDelay"/> so a flapping source does not flicker.
    /// Text is rebuilt only when a report changes (never per frame).
    /// </summary>
    public sealed partial class FaaPilotChrome
    {
        public const string StatusId="status";
        public const float StatusClearDelay=1f, StatusMaxWidth=440f, StatusDotSize=10f;

        private sealed class StatusEntry
        {
            public string source, text, detail, pendingText, pendingDetail;
            public FaaChromeSeverity severity, pendingSeverity;
            public int rank; public float pendingSince=-1;
        }

        private readonly List<StatusEntry> statusEntries=new();
        private readonly StringBuilder statusBuilder=new();
        private Entry chip;
        private Image chipDot, chipBar;
        private Outline chipEdge;
        private bool statusDirty=true;
        private float statusWidth=MinButtonWidth;
        private UnityAction statusClick;
        private static string quietHex, amberHex, redHex;

        /// <summary>Severity currently drawn by the chip (Nominal when hidden or condensed).</summary>
        public FaaChromeSeverity StatusSeverity { get; private set; }
        /// <summary>Plain text currently drawn by the chip, without colour tags (empty when hidden).</summary>
        public string StatusText { get; private set; }="";
        /// <summary>Detail line of the most severe entry (shown on the data-source settings page, kept here for tools and tests).</summary>
        public string StatusDetail { get; private set; }="";
        public bool StatusVisible=>chip!=null&&chip.visible;

        /// <summary>
        /// Reports one subsystem's state. <paramref name="shortText"/> is the chip wording (keep it to two or three words);
        /// a Nominal report with empty text is silent. Lower <paramref name="rank"/> wins ties (data before terrain).
        /// </summary>
        public void ReportStatus(string source,FaaChromeSeverity severity,string shortText,string detail=null,int rank=0,bool immediate=false)
        {
            if(string.IsNullOrEmpty(source))return;
            Build();
            shortText??="";detail??="";
            StatusEntry e=null;
            foreach(var s in statusEntries)if(s.source==source){e=s;break;}
            if(e==null){e=new StatusEntry{source=source,severity=severity,text=shortText,detail=detail,rank=rank};statusEntries.Add(e);statusDirty=true;}
            else if(severity>=e.severity||immediate)
            {
                e.rank=rank;e.pendingSince=-1;
                if(e.severity!=severity||e.text!=shortText||e.detail!=detail){e.severity=severity;e.text=shortText;e.detail=detail;statusDirty=true;}
            }
            else
            {
                e.rank=rank;
                if(e.pendingSince<0||e.pendingSeverity!=severity){e.pendingSince=Time.unscaledTime;e.pendingSeverity=severity;}
                e.pendingText=shortText;e.pendingDetail=detail;
            }
            FlushStatus();
        }
        public void ClearStatus(string source)
        {
            for(int i=statusEntries.Count-1;i>=0;i--)if(statusEntries[i].source==source){statusEntries.RemoveAt(i);statusDirty=true;}
            FlushStatus();
        }
        private void FlushStatus(){if(statusDirty){RenderStatus();Relayout();}}
        /// <summary>Overrides the chip click (default: open the data-source page of the settings panel).</summary>
        public void SetStatusClick(UnityAction action){statusClick=action;}

        private void TickStatus()
        {
            float now=Time.unscaledTime;
            foreach(var e in statusEntries)
                if(e.pendingSince>=0&&now-e.pendingSince>=StatusClearDelay)
                {e.severity=e.pendingSeverity;e.text=e.pendingText;e.detail=e.pendingDetail;e.pendingSince=-1;statusDirty=true;}
            FlushStatus();
        }

        private void BuildStatusChip()
        {
            chip=MakeEntry(StatusId,FaaChromeCluster.Left);chip.order=-1000;chip.seq=sequence++;entries.Add(chip);SetAnchors(chip);
            chip.button.onClick.AddListener(OnStatusClicked);
            // Annunciator, not a control: transparent plate (still a hit area) and no hover brightening.
            chip.plate.color=new Color(0,0,0,0);chip.button.transition=Selectable.Transition.None;
            chip.caption.richText=true;chip.caption.overflowMode=TextOverflowModes.Ellipsis;
            chipEdge=chip.rect.GetComponent<Outline>();
            chipDot=NewRect("Status dot",chip.rect).gameObject.AddComponent<Image>();
            var d=chipDot.rectTransform;d.anchorMin=d.anchorMax=new Vector2(0,.5f);d.pivot=new Vector2(0,.5f);
            d.anchoredPosition=new Vector2(CaptionPadding,0);d.sizeDelta=new Vector2(StatusDotSize,StatusDotSize);chipDot.raycastTarget=false;
            chipBar=NewRect("Alert bar",chip.rect).gameObject.AddComponent<Image>();
            var b=chipBar.rectTransform;b.anchorMin=new Vector2(0,0);b.anchorMax=new Vector2(0,1);b.pivot=new Vector2(0,.5f);
            b.offsetMin=new Vector2(0,0);b.offsetMax=new Vector2(4,0);chipBar.raycastTarget=false;
            quietHex??=ColorUtility.ToHtmlStringRGB(FaaHudStyle.ChromeQuiet);
            amberHex??=ColorUtility.ToHtmlStringRGB(FaaHudStyle.Amber);
            redHex??=ColorUtility.ToHtmlStringRGB(FaaHudStyle.Red);
            chip.visible=false;chip.rect.gameObject.SetActive(false);
            RenderStatus();
        }
        private void OnStatusClicked()
        {
            if(statusClick!=null){statusClick.Invoke();return;}
            var workspace=FaaSpatialWorkspace.Current;
            if(workspace!=null)workspace.OpenDataSourceSettings();
        }

        public static Color ColorFor(FaaChromeSeverity severity)=>
            severity==FaaChromeSeverity.Warning?FaaHudStyle.Red:severity==FaaChromeSeverity.Caution?FaaHudStyle.Amber:FaaHudStyle.ChromeQuiet;

        private static bool Better(StatusEntry a,StatusEntry b)=>b==null||a.severity>b.severity||(a.severity==b.severity&&a.rank<b.rank);

        private void RenderStatus()
        {
            statusDirty=false;
            if(chip==null)return;
            StatusEntry top=null,second=null,nominal=null;
            foreach(var e in statusEntries)
            {
                if(e.severity==FaaChromeSeverity.Nominal)
                {if(!string.IsNullOrEmpty(e.text)&&(nominal==null||e.rank<nominal.rank))nominal=e;continue;}
                if(Better(e,top)){second=top;top=e;}
                else if(Better(e,second))second=e;
            }
            bool visible=top!=null||nominal!=null;
            statusBuilder.Clear();
            string plain;
            if(top==null)
            {
                StatusSeverity=FaaChromeSeverity.Nominal;
                if(nominal!=null)
                {
                    statusBuilder.Append("<color=#").Append(quietHex).Append('>').Append(nominal.text).Append("</color>");
                    plain=nominal.text;StatusDetail=nominal.detail;
                }
                else{plain="";StatusDetail="";}
                chipDot.enabled=true;chipDot.color=FaaHudStyle.Green;chipBar.enabled=false;
                chipEdge.effectColor=new Color(0,0,0,0);
            }
            else
            {
                StatusSeverity=top.severity;StatusDetail=top.detail;
                string hex=top.severity==FaaChromeSeverity.Warning?redHex:top.severity==FaaChromeSeverity.Caution?amberHex:quietHex;
                statusBuilder.Append("<color=#").Append(hex).Append('>').Append(top.text).Append("</color>");
                plain=top.text;
                if(second!=null&&!string.IsNullOrEmpty(second.text))
                {
                    int mark=statusBuilder.Length;
                    statusBuilder.Append("<color=#").Append(quietHex).Append(">  ·  ").Append(second.text).Append("</color>");
                    // Keep the secondary only while the chip stays compact; never truncate the primary alert for it.
                    if(TextWidth(chip.caption,top.text+"  ·  "+second.text)+CaptionPadding*2>StatusMaxWidth)statusBuilder.Length=mark;
                    else plain+="  ·  "+second.text;
                }
                bool alert=top.severity>=FaaChromeSeverity.Caution;
                chipDot.enabled=false;chipBar.enabled=alert;chipBar.color=ColorFor(top.severity);
                chipEdge.effectColor=alert?FaaHudStyle.WithAlpha(ColorFor(top.severity),.9f):new Color(0,0,0,0);
            }
            StatusText=visible?plain:"";
            string rich=statusBuilder.ToString();
            if(chip.shown!=rich){chip.shown=rich;chip.caption.text=rich;}
            float left=CaptionPadding+(top==null?StatusDotSize+Gap:0);
            float textWidth=TextWidth(chip.caption,plain);
            statusWidth=Mathf.Clamp(Mathf.Ceil(left+textWidth+CaptionPadding+2),MinButtonWidth,StatusMaxWidth);
            var ct=chip.caption.rectTransform;ct.anchoredPosition=new Vector2(left,0);ct.sizeDelta=new Vector2(statusWidth-left-CaptionPadding+2,0);
            if(chip.visible!=visible){chip.visible=visible;chip.rect.gameObject.SetActive(visible);}
            layoutDirty=true;
        }
    }
}
