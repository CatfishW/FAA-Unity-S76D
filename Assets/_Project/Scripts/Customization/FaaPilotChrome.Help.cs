using System.Collections.Generic;
using System.Text;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace FAA.Customization
{
    /// <summary>
    /// On-demand key list (F1 or ?), off by default and dismissed by the same key, Esc, its CLOSE button or a click
    /// outside it. Shortcuts stay discoverable without persistent clutter (HF-STD-001B / TC-13/44: show accelerators;
    /// AC 25-11B 9.7: key hints only on demand). Entries are registered by the subsystem that owns the binding, so the list
    /// only shows keys that are live now. Developer bindings are listed only while developer mode is on (Ctrl+Shift+D),
    /// so the pilot list never offers a tool that swaps renderers or shows simulator internals.
    /// The width comes from the registered IAS/TQ keep-outs (<see cref="FlyoutWidth"/>) and long meanings wrap, so the
    /// list never touches the left flight column.
    /// </summary>
    public sealed partial class FaaPilotChrome
    {
        public const string HelpId="help", HelpName="Pilot key list";
        public const float HelpWidth=FlyoutMaxWidth, HelpRowHeight=24f, HelpKeyColumn=150f, HelpHeaderHeight=46f, HelpPad=14f, HelpRowGap=2f;

        private sealed class HelpEntry { public string keys, meaning; public int order; public bool developer; }
        private sealed class HelpRow { public TMP_Text keys, meaning; }

        private readonly List<HelpEntry> helpEntries=new();
        private readonly List<HelpEntry> helpScratch=new();
        private readonly List<HelpRow> helpRows=new();
        private RectTransform helpRoot;
        private bool helpDirty=true;

        public bool HelpVisible=>helpRoot!=null&&helpRoot.gameObject.activeSelf;
        public RectTransform HelpRect=>helpRoot;
        /// <summary>
        /// The key list as plain "KEYS  meaning" lines, exactly the rows the pilot sees: developer lines (prefixed [DEV])
        /// appear only while <see cref="DeveloperMode"/> is on.
        /// </summary>
        public string HelpText
        {
            get
            {
                var sb=new StringBuilder();
                foreach(var e in VisibleHelp())sb.Append(e.developer?"[DEV] ":"").Append(e.keys).Append("  ").Append(e.meaning).Append('\n');
                return sb.ToString();
            }
        }

        /// <summary>Adds or replaces the line for <paramref name="keys"/>. Lower order is listed first; developer lines are grouped last and shown only in developer mode.</summary>
        public void SetHelpEntry(string keys,string meaning,int order,bool developer=false)
        {
            if(string.IsNullOrEmpty(keys))return;
            foreach(var e in helpEntries)
                if(e.keys==keys)
                {
                    if(e.meaning!=meaning||e.order!=order||e.developer!=developer){e.meaning=meaning;e.order=order;e.developer=developer;helpDirty=true;}
                    RefreshHelpIfVisible();return;
                }
            helpEntries.Add(new HelpEntry{keys=keys,meaning=meaning,order=order,developer=developer});helpDirty=true;RefreshHelpIfVisible();
        }
        public void RemoveHelpEntry(string keys)
        {
            for(int i=helpEntries.Count-1;i>=0;i--)if(helpEntries[i].keys==keys){helpEntries.RemoveAt(i);helpDirty=true;}
            RefreshHelpIfVisible();
        }
        public void ToggleHelp()=>SetHelpVisible(!HelpVisible);
        public void SetHelpVisible(bool visible)
        {
            Build();
            if(visible)
            {
                // Activate before laying out: TMP returns no wrapped-text metrics for inactive objects,
                // which would size every wrapped row as one line and stack it onto the next row.
                EnsureHelp();RefreshBuiltInHelp();helpRoot.gameObject.SetActive(true);helpDirty=true;RenderHelp();
                NotifyFlyoutOpened(HelpId);
            }
            else if(helpRoot!=null){helpRoot.gameObject.SetActive(false);NotifyFlyoutClosed(HelpId);}
            SetButtonState(HelpId,true,visible);
        }

        private void RegisterBuiltInHelp()
        {
            AddButton(HelpId,FaaChromeCluster.Left,90,"?","F1",ToggleHelp);
            RegisterFlyout(HelpId,()=>SetHelpVisible(false));
            SetHelpEntry("F1  /  ?","Show or hide this key list",900);
            SetHelpEntry("ESC","Close the open menu, brief or key list",800);
            SetHelpEntry(DeveloperKeys,"Developer mode on / off",900,true);
        }
        /// <summary>Bindings owned by components outside this assembly's reach; listed only when the component is live.</summary>
        private void RefreshBuiltInHelp()
        {
            var cam=Camera.main;
            var look=cam!=null?cam.GetComponent<AircraftControl.Camera.AircraftCameraController>():null;
            if(look!=null&&look.isActiveAndEnabled)
            {
                SetHelpEntry("RIGHT-DRAG","Look around · drag a side panel",50);
                SetHelpEntry("V","Camera mode: cockpit / chase / free",60);
            }
            else{RemoveHelpEntry("RIGHT-DRAG");RemoveHelpEntry("V");}
            RemoveHelpEntry("F8");
            var switcher=FindAnyObjectByType<FAA.HUDToolkit.FaaHudModeSwitcher>();
            if(switcher!=null&&switcher.isActiveAndEnabled&&switcher.HotkeyEnabled)SetHelpEntry(FAA.HUDToolkit.FaaHudModeSwitcher.HotkeyLabel,"HUD renderer: UGUI / UI Toolkit",950,true);
            else RemoveHelpEntry(FAA.HUDToolkit.FaaHudModeSwitcher.HotkeyLabel);
        }

        /// <summary>Rows the pilot sees, in order: pilot bindings, then (developer mode only) developer bindings.</summary>
        private List<HelpEntry> VisibleHelp()
        {
            helpScratch.Clear();
            foreach(var e in helpEntries)if(!e.developer||developerMode)helpScratch.Add(e);
            helpScratch.Sort((a,b)=>a.developer!=b.developer?a.developer.CompareTo(b.developer):a.order!=b.order?a.order.CompareTo(b.order):string.CompareOrdinal(a.keys,b.keys));
            return helpScratch;
        }
        private void RefreshHelpIfVisible(){if(HelpVisible)RenderHelp();}

        private void EnsureHelp()
        {
            if(helpRoot!=null)return;
            helpRoot=NewRect(HelpName,flyouts);
            helpRoot.anchorMin=helpRoot.anchorMax=helpRoot.pivot=Vector2.zero;
            helpRoot.anchoredPosition=new Vector2(FlyoutLeft,FlyoutBottom);helpRoot.sizeDelta=new Vector2(HelpWidth,120);
            var plate=helpRoot.gameObject.AddComponent<Image>();plate.color=FaaHudStyle.ChromePlate;plate.raycastTarget=true;
            var o=helpRoot.gameObject.AddComponent<Outline>();o.effectColor=FaaHudStyle.ChromeOutline;o.effectDistance=new Vector2(1,1);
            var title=NewText("Title",helpRoot,FaaHudStyle.Chrome,FaaHudStyle.White);title.text="KEYS AND CONTROLS";title.fontStyle=FontStyles.Bold;
            Top(title.rectTransform,HelpPad,10,240,CloseButtonHeight);title.alignment=TextAlignmentOptions.MidlineLeft;
            CreateCloseButton(helpRoot,()=>SetHelpVisible(false));
            FaaHudKeepOutRegion.Ensure(helpRoot.gameObject,"chrome:help",FaaKeepOutKind.Chrome,4f);
            helpRoot.gameObject.SetActive(false);
        }
        private void RenderHelp()
        {
            if(helpRoot==null||!helpDirty)return;
            helpDirty=false;
            var list=VisibleHelp();
            // Width from the flight keep-outs at a generous band height first, then wrap meanings into that width.
            float width=FlyoutWidth(HelpWidth,HelpHeaderHeight+(list.Count+1)*(HelpRowHeight+HelpRowGap)+12);
            float meaningWidth=width-HelpKeyColumn-HelpPad;
            float y=HelpHeaderHeight;int row=0;bool devHeader=false;
            foreach(var e in list)
            {
                if(e.developer&&!devHeader)
                {
                    devHeader=true;
                    y+=PlaceHelpRow(row++,"DEVELOPER","",true,true,y,meaningWidth)+HelpRowGap;
                }
                y+=PlaceHelpRow(row++,e.keys,e.meaning,e.developer,false,y,meaningWidth)+HelpRowGap;
            }
            for(int i=row;i<helpRows.Count;i++){helpRows[i].keys.gameObject.SetActive(false);helpRows[i].meaning.gameObject.SetActive(false);}
            helpRoot.sizeDelta=new Vector2(width,Mathf.Ceil(y+10));
        }
        /// <summary>Lays out one key/meaning pair at <paramref name="top"/>; returns its height (the meaning wraps, never clips).</summary>
        private float PlaceHelpRow(int index,string keys,string meaning,bool quiet,bool header,float top,float meaningWidth)
        {
            while(helpRows.Count<=index)
            {
                var r=new HelpRow{keys=NewText("Keys",helpRoot,FaaHudStyle.Chrome,FaaHudStyle.White),meaning=NewText("Meaning",helpRoot,FaaHudStyle.Chrome,FaaHudStyle.White)};
                r.keys.fontStyle=FontStyles.Bold;r.keys.alignment=TextAlignmentOptions.TopLeft;
                r.meaning.alignment=TextAlignmentOptions.TopLeft;r.meaning.textWrappingMode=TextWrappingModes.Normal;
                helpRows.Add(r);
            }
            var row=helpRows[index];
            row.keys.gameObject.SetActive(true);row.meaning.gameObject.SetActive(!header);
            row.keys.text=keys;row.meaning.text=meaning??"";
            var color=quiet?FaaHudStyle.ChromeQuiet:FaaHudStyle.White;
            row.keys.color=color;row.meaning.color=color;
            float height=HelpRowHeight;
            if(!header&&!string.IsNullOrEmpty(meaning))
            {
                float preferred=row.meaning.font!=null?row.meaning.GetPreferredValues(meaning,meaningWidth,10000f).y
                    :Mathf.Ceil(meaning.Length*row.meaning.fontSize*.55f/Mathf.Max(1,meaningWidth))*HelpRowHeight;
                height=Mathf.Max(HelpRowHeight,Mathf.Ceil(preferred+4));
            }
            Top(row.keys.rectTransform,HelpPad,top,header?HelpKeyColumn+meaningWidth-HelpPad:HelpKeyColumn-HelpPad-6,HelpRowHeight);
            Top(row.meaning.rectTransform,HelpKeyColumn,top,meaningWidth,height);
            return height;
        }
        private static void Top(RectTransform rt,float left,float top,float width,float height)
        {
            rt.anchorMin=rt.anchorMax=rt.pivot=new Vector2(0,1);rt.anchoredPosition=new Vector2(left,-top);rt.sizeDelta=new Vector2(width,height);
        }
    }
}
