using System;
using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace FAA.Customization
{
    /// <summary>
    /// One row of the COMMANDS flyout. <see cref="Segments"/> are the choices drawn as square segment buttons at the right of
    /// the row ("ON" / "OFF", or "40" "60" "80" "100"). <see cref="State"/> returns the index of the segment that is
    /// currently true (-1 when the state is unknown: then no segment is highlighted, the state is never guessed).
    /// <see cref="Actions"/> rows are plain one-shot buttons with no state.
    /// </summary>
    public sealed class FaaChromeCommandRow
    {
        public static readonly string[] OnOff={"ON","OFF"};
        public readonly string Section, Label;
        public readonly string[] Segments;
        public readonly Func<int> State;
        public readonly Action<int> Select;
        public readonly bool Actions;
        public FaaChromeCommandRow(string section,string label,string[] segments,Func<int> state,Action<int> select,bool actions=false)
        {
            Section=section??"";Label=label??"";Segments=segments??OnOff;State=state;Select=select;Actions=actions;
        }
        /// <summary>Current segment index, or -1 when unknown or the row has no state. Never throws.</summary>
        public int CurrentState()
        {
            if(Actions||State==null)return -1;
            try{int s=State();return s>=0&&s<Segments.Length?s:-1;}catch(Exception){return -1;}
        }
        /// <summary>Two-segment ON/OFF row backed by a boolean (null = unknown).</summary>
        public static FaaChromeCommandRow Toggle(string section,string label,Func<bool?> isOn,Action<bool> set)=>
            new FaaChromeCommandRow(section,label,OnOff,()=>{var on=isOn?.Invoke();return on==null?-1:on.Value?0:1;},i=>set?.Invoke(i==0));
    }

    /// <summary>
    /// COMMANDS flyout (Tab, or the bar's COMMANDS button). It opens in the fixed left flyout slot above the bar, outside the
    /// attitude field and the IAS/TQ column, and never hides or dims flight symbology (no full-screen backdrop: only its own
    /// plate). Each function is one row with its live state as highlighted segments, so there are no separate Show / Hide
    /// items to choose between. It closes with Tab, Esc, CLOSE, or a click outside it. Same plate, type and button style as
    /// every other chrome flyout (AC 25-11B consistency; HF-STD-001B labelled controls with visible state).
    /// The rows come from the command owner (the radial command menu); this class only renders and polls them.
    /// </summary>
    public sealed partial class FaaPilotChrome
    {
        public const string CommandsId="commands", CommandsName="Pilot commands";
        public const float CommandHeaderHeight=46f, CommandSectionHeight=26f, CommandRowHeight=36f, CommandSegmentHeight=30f,
            CommandSegmentMinWidth=52f, CommandSegmentGap=4f, CommandPad=12f, CommandPollSeconds=.2f;

        private sealed class CommandRowView
        {
            public FaaChromeCommandRow row;
            public TMP_Text label;
            public Image[] plates, marks;
            public int shown=-2;
        }

        private readonly List<CommandRowView> commandViews=new();
        private RectTransform commandsRoot, commandsBody;
        private TMP_Text commandsEmpty;
        private float nextCommandPoll;

        public bool CommandsVisible=>commandsRoot!=null&&commandsRoot.gameObject.activeSelf;
        public RectTransform CommandsRect=>commandsRoot;
        public int CommandRowCount=>commandViews.Count;
        public string CommandRowLabel(int row)=>row>=0&&row<commandViews.Count?commandViews[row].row.Label:null;
        /// <summary>Segment drawn as active for a row (-1 none). Updated at 5 Hz while the flyout is open and after every press.</summary>
        public int CommandRowState(int row)=>row>=0&&row<commandViews.Count?commandViews[row].shown:-1;

        private void RegisterCommandsFlyout()=>RegisterFlyout(CommandsId,()=>SetCommandsVisible(false));

        /// <summary>Replaces the rows (called by the command owner each time the flyout opens; allocation happens only then).</summary>
        public void SetCommandRows(IList<FaaChromeCommandRow> rows)
        {
            Build();EnsureCommands();
            for(int i=commandsBody.childCount-1;i>=0;i--)
            {
                var child=commandsBody.GetChild(i).gameObject;
                if(child==commandsEmpty.gameObject)continue;
                child.SetActive(false);if(Application.isPlaying)Destroy(child);else DestroyImmediate(child);
            }
            commandViews.Clear();
            int count=rows?.Count??0;
            float height=0;string section=null;
            for(int i=0;i<count;i++)
            {
                var r=rows[i];if(r==null)continue;
                if(r.Section!=section){section=r.Section;height+=CommandSectionHeight;}
                height+=CommandRowHeight;
            }
            float total=CommandHeaderHeight+Mathf.Max(height,CommandRowHeight)+CommandPad;
            float width=FlyoutWidth(FlyoutMaxWidth,total);
            commandsRoot.sizeDelta=new Vector2(width,total);
            commandsEmpty.gameObject.SetActive(height<=0);
            float y=0;section=null;
            for(int i=0;i<count;i++)
            {
                var r=rows[i];if(r==null)continue;
                if(r.Section!=section)
                {
                    section=r.Section;
                    var caption=NewText("Section "+section,commandsBody,FaaHudStyle.Chrome,FaaHudStyle.ChromeQuiet);caption.text=section;caption.fontStyle=FontStyles.Bold;
                    caption.alignment=TextAlignmentOptions.BottomLeft;Top(caption.rectTransform,CommandPad,y,width-2*CommandPad,CommandSectionHeight-4);
                    y+=CommandSectionHeight;
                }
                commandViews.Add(BuildCommandRow(r,y,width));
                y+=CommandRowHeight;
            }
            RefreshCommandStates();
        }

        public void SetCommandsVisible(bool visible)
        {
            Build();EnsureCommands();
            if(CommandsVisible==visible){SetButtonState(CommandsId,IsButtonVisible(CommandsId),visible);return;}
            commandsRoot.gameObject.SetActive(visible);
            if(visible){NotifyFlyoutOpened(CommandsId);RefreshCommandStates();nextCommandPoll=Time.unscaledTime+CommandPollSeconds;}
            else NotifyFlyoutClosed(CommandsId);
            SetButtonState(CommandsId,IsButtonVisible(CommandsId),visible);
        }
        public void ToggleCommands()=>SetCommandsVisible(!CommandsVisible);

        /// <summary>Presses one segment as a click would (keyboard routes, tests).</summary>
        public void PressCommand(int row,int segment)
        {
            if(row<0||row>=commandViews.Count)return;
            var v=commandViews[row];
            if(segment<0||segment>=v.row.Segments.Length)return;
            try{v.row.Select?.Invoke(segment);}
            catch(Exception ex){Debug.LogWarning("[FaaPilotChrome] Command '"+v.row.Label+"' failed: "+ex.Message);}
            RefreshCommandStates();
        }

        private void TickCommands()
        {
            if(!CommandsVisible||Time.unscaledTime<nextCommandPoll)return;
            nextCommandPoll=Time.unscaledTime+CommandPollSeconds;
            RefreshCommandStates();
        }
        /// <summary>Re-reads every row's state; plates change only when a state changed (no allocation).</summary>
        private void RefreshCommandStates()
        {
            for(int i=0;i<commandViews.Count;i++)
            {
                var v=commandViews[i];int s=v.row.CurrentState();
                if(s==v.shown)continue;
                v.shown=s;
                for(int k=0;k<v.plates.Length;k++)
                {
                    bool on=k==s;
                    v.plates[k].color=on?FaaHudStyle.ChromeButtonActive:FaaHudStyle.ChromeButton;
                    v.marks[k].enabled=on;
                }
            }
        }

        private void EnsureCommands()
        {
            if(commandsRoot!=null)return;
            commandsRoot=NewRect(CommandsName,flyouts);
            commandsRoot.anchorMin=commandsRoot.anchorMax=commandsRoot.pivot=Vector2.zero;
            commandsRoot.anchoredPosition=new Vector2(FlyoutLeft,FlyoutBottom);commandsRoot.sizeDelta=new Vector2(FlyoutMaxWidth,CommandHeaderHeight+CommandRowHeight+CommandPad);
            var plate=commandsRoot.gameObject.AddComponent<Image>();plate.color=FaaHudStyle.ChromePlate;plate.raycastTarget=true;
            var o=commandsRoot.gameObject.AddComponent<Outline>();o.effectColor=FaaHudStyle.ChromeOutline;o.effectDistance=new Vector2(1,1);
            var title=NewText("Title",commandsRoot,FaaHudStyle.Chrome,FaaHudStyle.White);title.text="COMMANDS";title.fontStyle=FontStyles.Bold;
            title.alignment=TextAlignmentOptions.MidlineLeft;Top(title.rectTransform,HelpPad,10,200,CloseButtonHeight);
            CreateCloseButton(commandsRoot,()=>SetCommandsVisible(false));
            commandsBody=NewRect("Rows",commandsRoot);commandsBody.anchorMin=new Vector2(0,1);commandsBody.anchorMax=Vector2.one;commandsBody.pivot=new Vector2(.5f,1);
            commandsBody.offsetMin=new Vector2(0,-4000);commandsBody.offsetMax=new Vector2(0,-CommandHeaderHeight+2);
            commandsEmpty=NewText("No commands",commandsBody,FaaHudStyle.Chrome,FaaHudStyle.ChromeQuiet);commandsEmpty.text="NO COMMANDS AVAILABLE";
            commandsEmpty.alignment=TextAlignmentOptions.MidlineLeft;Top(commandsEmpty.rectTransform,CommandPad,0,300,CommandRowHeight);
            FaaHudKeepOutRegion.Ensure(commandsRoot.gameObject,"chrome:commands",FaaKeepOutKind.Chrome,4f);
            commandsRoot.gameObject.SetActive(false);
        }

        private CommandRowView BuildCommandRow(FaaChromeCommandRow r,float top,float width)
        {
            var v=new CommandRowView{row=r,plates=new Image[r.Segments.Length],marks=new Image[r.Segments.Length]};
            var rowRect=NewRect("Row "+r.Label,commandsBody);Top(rowRect,0,top,width,CommandRowHeight);
            // Segments right-aligned; each wide enough for its caption and at least an 8 mm target.
            float x=width-CommandPad;
            for(int k=r.Segments.Length-1;k>=0;k--)
            {
                string text=r.Segments[k];
                var seg=NewRect("Segment "+text,rowRect);
                var caption=NewText("Caption",seg,FaaHudStyle.Chrome,FaaHudStyle.White);caption.text=text;caption.alignment=TextAlignmentOptions.Center;caption.fontStyle=FontStyles.Bold;
                float w=Mathf.Max(CommandSegmentMinWidth,Mathf.Ceil(TextWidth(caption,text)+2*CaptionPadding));
                x-=w;
                seg.anchorMin=seg.anchorMax=seg.pivot=new Vector2(0,.5f);seg.anchoredPosition=new Vector2(x,0);seg.sizeDelta=new Vector2(w,CommandSegmentHeight);
                var plate=seg.gameObject.AddComponent<Image>();plate.color=FaaHudStyle.ChromeButton;plate.raycastTarget=true;
                var ol=seg.gameObject.AddComponent<Outline>();ol.effectColor=FaaHudStyle.ChromeOutline;ol.effectDistance=new Vector2(1,1);
                var ct=caption.rectTransform;ct.anchorMin=Vector2.zero;ct.anchorMax=Vector2.one;ct.offsetMin=ct.offsetMax=Vector2.zero;
                var mark=NewRect("Active marker",seg).gameObject.AddComponent<Image>();
                var mr=mark.rectTransform;mr.anchorMin=new Vector2(0,0);mr.anchorMax=new Vector2(1,0);mr.pivot=new Vector2(.5f,0);
                mr.offsetMin=new Vector2(4,2);mr.offsetMax=new Vector2(-4,5);mark.color=FaaHudStyle.Cyan;mark.raycastTarget=false;mark.enabled=false;
                var button=seg.gameObject.AddComponent<Button>();button.targetGraphic=plate;StyleButton(button);
                int index=k,rowIndex=commandViews.Count;
                button.onClick.AddListener(()=>PressCommand(rowIndex,index));
                v.plates[k]=plate;v.marks[k]=mark;
                x-=CommandSegmentGap;
            }
            v.label=NewText("Label",rowRect,FaaHudStyle.Chrome,FaaHudStyle.White);v.label.text=r.Label;
            v.label.alignment=TextAlignmentOptions.MidlineLeft;v.label.overflowMode=TextOverflowModes.Ellipsis;
            var lt=v.label.rectTransform;lt.anchorMin=lt.anchorMax=lt.pivot=new Vector2(0,.5f);lt.anchoredPosition=new Vector2(CommandPad+4,0);
            lt.sizeDelta=new Vector2(Mathf.Max(40,x-CommandPad-12),CommandRowHeight);
            return v;
        }
    }
}
