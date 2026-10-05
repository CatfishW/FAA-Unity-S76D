using System.Collections.Generic;
using UnityEngine;

namespace FAA.Customization
{
    public sealed partial class FaaSpatialWorkspace
    {
        // The bank sprite is a hollow arc whose rect covers the attitude field; attitude lives inside the field.
        // 'fma' registers its own Z1 region (FaaFlightModeAnnunciator), so the module copy would only duplicate it.
        private static readonly HashSet<string> KeepOutExcludedModules=new(){"bank","attitude","legacy-compass","fma"};
        private readonly List<ModuleKeepOut> moduleKeepOuts=new();

        /// <summary>Publishes each visible head-fixed instrument as a protected area so conformal cues never cross it.</summary>
        private sealed class ModuleKeepOut : FaaHudKeepOut.IRegion
        {
            private readonly FaaSpatialWorkspace owner;private readonly FaaNonConformalScaleTarget module;
            private readonly string id;
            // Deviation scales reserve their lane while hidden; they protect screen area only while real guidance is drawn.
            private readonly FaaNavigationScaleGraphic navigation;private readonly FaaClassicDeviationGraphic classicDeviation;
            public ModuleKeepOut(FaaSpatialWorkspace owner,FaaNonConformalScaleTarget module)
            {
                this.owner=owner;this.module=module;id="module:"+module.Id;
                if(module.Id=="glideslope"||module.Id=="localizer")
                {
                    navigation=module.Target.GetComponentInChildren<FaaNavigationScaleGraphic>(true);
                    classicDeviation=module.Target.GetComponentInChildren<FaaClassicDeviationGraphic>(true);
                }
            }
            public string Id=>id;
            public FaaKeepOutKind Kind=>FaaKeepOutKind.Symbology;
            public bool TryGetScreenRect(out Rect rect)
            {
                rect=default;
                if(navigation!=null&&!navigation.HasGuidance)return false;
                if(classicDeviation!=null&&!classicDeviation.Shown)return false;
                return owner!=null&&owner.isActiveAndEnabled&&owner.View!=null&&module!=null&&module.TryScreenBounds(owner.View,out rect);
            }
        }

        private void RegisterModuleKeepOuts()
        {
            ReleaseModuleKeepOuts();
            foreach(var m in digitalModules)AddModuleKeepOut(m);
            foreach(var m in classicModules)AddModuleKeepOut(m);
        }
        private void AddModuleKeepOut(FaaNonConformalScaleTarget m)
        {
            if(m==null||m.Target==null||KeepOutExcludedModules.Contains(m.Id))return;
            var k=new ModuleKeepOut(this,m);moduleKeepOuts.Add(k);FaaHudKeepOut.Register(k);
        }
        private void ReleaseModuleKeepOuts(){foreach(var k in moduleKeepOuts)FaaHudKeepOut.Unregister(k);moduleKeepOuts.Clear();}
    }
}
