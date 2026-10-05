using UnityEngine;

namespace FAA.Customization
{
    /// <summary>Composed flight mode annunciation for one frame. Empty strings mean "show nothing" in that field.</summary>
    public readonly struct FaaFma
    {
        public readonly bool Valid, Engaged;
        public readonly string Collective, CollectiveArmed, Lateral, LateralArmed, Vertical, VerticalArmed, Status;
        public FaaFma(bool valid,bool engaged,string collective,string collectiveArmed,string lateral,string lateralArmed,string vertical,string verticalArmed,string status)
        {
            Valid=valid;Engaged=engaged;Collective=collective??"";CollectiveArmed=collectiveArmed??"";Lateral=lateral??"";LateralArmed=lateralArmed??"";
            Vertical=vertical??"";VerticalArmed=verticalArmed??"";Status=status??"";
        }
        /// <summary>Column order C | R | P | STATUS, matching the AFCS mode-select layout.</summary>
        public string Active(int column)=>column==0?Collective:column==1?Lateral:column==2?Vertical:Status;
        public string Armed(int column)=>column==0?CollectiveArmed:column==1?LateralArmed:column==2?VerticalArmed:"";
    }

    /// <summary>
    /// One mode-annunciation composer for both symbology styles (AC 25.1329-1C mode annunciation; AC 25-11B FMA guidance):
    /// - active modes come from the autopilot state, never from UI buttons;
    /// - an armed mode appears only when it is distinct from the active mode (no "ALT / ALT ARM" duplication);
    /// - with the AP/FD disengaged, no modes are shown, so a disengaged autopilot is never displayed as guiding;
    /// - collective (third-axis) modes are never inferred: the C column stays empty until a real source exists;
    /// - stale data gives an invalid FMA, never a frozen last value.
    /// </summary>
    public static class FaaFlightModeAnnunciation
    {
        public const string StaleStatus="FMA --";

        public static string ArmedIfDistinct(string active,string armed)
        {
            if(string.IsNullOrWhiteSpace(armed))return "";
            string a=(active??"").Trim();string b=armed.Trim();
            string bare=b.EndsWith(" ARM")?b.Substring(0,b.Length-4).Trim():b;
            if(bare.Length==0||bare=="--"||string.Equals(bare,a,System.StringComparison.OrdinalIgnoreCase))return "";
            return b;
        }

        private static string Clean(string mode)=>string.IsNullOrWhiteSpace(mode)||mode.Trim()=="--"?"":mode.Trim();

        public static FaaFma Compose(FaaAnalogFlightSample s)
        {
            if(!s.Fresh)return new FaaFma(false,false,"","","","","","",StaleStatus);
            string coupling=(s.Coupling??"").Trim();
            bool engaged=coupling=="FD"||coupling=="CPL";
            if(!engaged)return new FaaFma(true,false,"","","","","","","");
            string lateral=Clean(s.RollMode),vertical=Clean(s.PitchMode);
            return new FaaFma(true,true,"","",lateral,ArmedIfDistinct(lateral,s.RollArmed),vertical,ArmedIfDistinct(vertical,s.PitchArmed),coupling);
        }
    }

    /// <summary>
    /// Tracks mode changes for the 10 s emphasis box and the AP-disengage annunciation (boxed "AP OFF", flashing then steady, then removed).
    /// Pure logic: pass the current time so it can be unit tested.
    /// </summary>
    public sealed class FaaFmaChangeTracker
    {
        private readonly string[] last=new string[4];
        private readonly float[] changedAt={-100,-100,-100,-100};
        private bool wasEngaged,initialized;
        private float disengagedAt=-100;

        public void Update(FaaFma fma,float now)
        {
            for(int i=0;i<4;i++)
            {
                string v=fma.Active(i);
                if(initialized&&v!=last[i]&&v.Length>0)changedAt[i]=now;
                last[i]=v;
            }
            if(initialized&&wasEngaged&&!fma.Engaged&&fma.Valid)disengagedAt=now;
            if(fma.Engaged)disengagedAt=-100;
            wasEngaged=fma.Engaged;initialized=true;
        }

        /// <summary>True while column <paramref name="column"/> should carry the mode-change box.</summary>
        public bool BoxVisible(int column,float now)=>column>=0&&column<4&&!string.IsNullOrEmpty(last[column])&&now-changedAt[column]<FaaHudStyle.ModeChangeBoxSeconds;
        /// <summary>True for 10 s after an AP/FD disengagement.</summary>
        public bool ApOffVisible(float now)=>now-disengagedAt<FaaHudStyle.ModeChangeBoxSeconds;
        /// <summary>Flash phase for the AP OFF cue: flashing for the first 5 s, then steady.</summary>
        public bool ApOffDrawn(float now)=>ApOffVisible(now)&&(now-disengagedAt>=FaaHudStyle.BlinkSeconds||Mathf.Repeat(now*FaaHudStyle.BlinkHz,1f)<.6f);
        public void Reset(){for(int i=0;i<4;i++){last[i]=null;changedAt[i]=-100;}wasEngaged=false;initialized=false;disengagedAt=-100;}
    }
}
