using System;
using System.IO;
using UnityEngine;

namespace IndicatorSystem.Core
{
    /// <summary>Opt-in bounded projection diagnostics. No camera, SDK or rendering changes.</summary>
    internal static class IndicatorLayoutAudit
    {
        private static readonly bool enabled=Array.IndexOf(Environment.GetCommandLineArgs(),"--indicator-layout-audit")>=0;
        private static StreamWriter writer;private static float started;private static int count;private static bool finished;
        [Serializable] private sealed class Row
        {
            public int frame;public float time,pixelScale;public string phase,id,visibility,result;
            public Rect region;public Vector2 raw,placed;public float arrow;public int occupied;
        }
        public static void Write(IndicatorData raw,IndicatorData placed,float scale,string phase,string result,int occupied)
        {
            if(!enabled||finished)return;
            try{WriteCore(raw,placed,scale,phase,result,occupied);}
            catch(IOException exception){Finish();Debug.LogWarning("Indicator layout audit stopped: "+exception.Message);}
            catch(UnauthorizedAccessException exception){Finish();Debug.LogWarning("Indicator layout audit stopped: "+exception.Message);}
        }
        private static void WriteCore(IndicatorData raw,IndicatorData placed,float scale,string phase,string result,int occupied)
        {
            if(writer==null)
            {
                started=Time.unscaledTime;
                string path=Path.Combine(Application.persistentDataPath,"Indicator-layout-audit-"+DateTime.UtcNow.ToString("yyyyMMdd-HHmmss")+".jsonl");
                writer=new StreamWriter(new FileStream(path,FileMode.CreateNew,FileAccess.Write,FileShare.ReadWrite),System.Text.Encoding.UTF8,32768);
            }
            if(count>=16000||Time.unscaledTime-started>=60f){Finish();return;}
            var row=new Row{frame=Time.frameCount,time=Time.unscaledTime,pixelScale=scale,phase=phase,id=raw.Id,
                visibility=raw.Visibility.ToString(),result=result,region=raw.ProjectionRect,raw=raw.ScreenPosition,
                placed=placed.ScreenPosition,arrow=placed.ArrowRotation,occupied=occupied};
            writer.WriteLine(JsonUtility.ToJson(row));if(++count%180==0)writer.Flush();
        }
        public static void Finish()
        {
            var pending=writer;writer=null;if(enabled)finished=true;
            try{pending?.Dispose();}catch(IOException){}
        }
    }
}
