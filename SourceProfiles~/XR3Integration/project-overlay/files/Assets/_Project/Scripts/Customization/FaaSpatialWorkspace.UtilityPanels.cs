using UnityEngine;
namespace FAA.Customization
{
    public sealed partial class FaaSpatialWorkspace
    {
        public Canvas SettingsCanvas { get; private set; }
        public FaaSpatialRadarPanel RegisterUtility(string id,Canvas canvas,RectTransform root,float yaw,float width=.74f)
        {
            var previous=GetPanel(id);if(previous!=null){previous.Dispose();utilityPanels.Remove(previous);}
            var utility=new FaaSpatialRadarPanel(this,id,canvas,root,yaw,true,width);
            utilityPanels.Add(utility);utility.SetSpatial(true);return utility;
        }
        private void LoadUtilityProfile()
        {
            if(!PersistChanges||!PlayerPrefs.HasKey(profileKey)||!FaaSpatialLayoutMath.TryReadProfile(PlayerPrefs.GetString(profileKey),out var profile))return;
            foreach(var saved in profile.entries)foreach(var panel in utilityPanels)
                if(saved.id==panel.Id)
                {panel.Layout.yaw=saved.yaw;panel.Layout.elevation=saved.elevation;panel.Layout.distance=saved.distance;panel.Layout.scale=saved.scale;}
        }
        public void BringUtilityHere(string id)
        {
            // Compatibility entry point: recovery now means the safe side slot, never the pilot's gaze.
            StowUtility(id);
        }
        public void StowUtility(string id)
        {
            var panel=GetPanel(id);if(panel==null||!panel.IsUtility)return;
            CancelManipulation();panel.Layout.yaw=panel.DefaultLayout.yaw;panel.Layout.elevation=panel.DefaultLayout.elevation;
            panel.Layout.distance=panel.DefaultLayout.distance;
            MarkChanged();RefreshTransforms();
            // Screen-chrome unit: stowing the inspected panel returns the view forward instead of staring at empty space.
            if(InspectedPanelId==id)ReturnToForwardView();
        }

        // ---- Screen-chrome unit (wave 1): inspection state and FaaHudInspection hooks. ----
        // InspectedPanelId names the panel the desktop camera is turned to ("settings", "camera-controls", "weather",
        // "traffic"), or null when looking forward. FaaHudInspection dims every forward HUD layer while it is set;
        // the pilot chrome bar stays fully visible and highlights the inspected destination.

        /// <summary>Panel the desktop camera is inspecting, or null when looking forward (always null in native XR).</summary>
        public string InspectedPanelId { get; private set; }
        public bool IsInspectingPanel=>InspectedPanelId!=null;

        /// <summary>
        /// Opens a utility panel and, on desktop, turns the camera to it. In native XR the panel opens where it lives and
        /// the pilot turns their head (the camera never drives a tracked HMD), so the button is never a dead control.
        /// </summary>
        public void InspectUtility(string id)
        {
            var panel=GetPanel(id);if(panel==null||!panel.IsUtility||View==null)return;
            // Open-only: a destination button never closes a panel or stops the camera.
            if(id=="settings")OpenMenu();
            else if(id=="camera-controls")LaptopCamera?.OpenPanel();
            RefreshTransforms();
            if(NativeXr){RefreshControls();return;}
            // Framed clear of the awareness readouts and chrome, as near the centre as that allows, and zoomed (desktop only)
            // until the panel's smallest text is legible; FORWARD/R restores the exact FOV.
            BeginInspection(id,TurnToPanel(panel));
        }
        public void InspectPanel(string id)
        {
            var panel=GetPanel(id);if(panel==null||View==null||NativeXr)return;
            if(panel.IsUtility){InspectUtility(id);return;}
            RefreshTransforms();
            // Scope, header and any open drawer framed together; the turn passes through forward (never behind the seat).
            BeginInspection(id,TurnToPanel(panel));
        }
        public void ReturnToForwardView()
        {
            if(!NativeXr&&View!=null){var camera=View.GetComponent<AircraftControl.Camera.AircraftCameraController>();if(camera!=null)camera.ResetView();}
            EndInspection();
        }
        private void BeginInspection(string id,bool started)
        {
            if(!started)return;
            InspectedPanelId=id;ApplyHudInspection();RefreshControls();
        }
        private void EndInspection()
        {
            // The HUD stays dimmed while the eased return is still far off-axis, then fades back in (ApplyHudInspection).
            bool had=InspectedPanelId!=null;InspectedPanelId=null;
            // The inspection zoom always ends with the inspection (eased back to the exact base FOV).
            if(had&&desktopView!=null)desktopView.ReleaseFieldOfView();
            ApplyHudInspection();
            if(had)RefreshControls();
        }
        /// <summary>
        /// Keeps inspection state honest when the view returns forward without a chrome action: R (camera reset),
        /// a deliberate right-drag look, a camera-mode change, or the inspected panel being closed.
        /// Runs every frame from <see cref="UpdateControlCanvasPose"/>; no allocations.
        /// </summary>
        private void SyncInspectionState()
        {
            if(InspectedPanelId==null)return;
            bool cameraInspecting=!NativeXr&&desktopView!=null&&desktopView.isActiveAndEnabled&&desktopView.IsPanelInspectionActive;
            if(!cameraInspecting){EndInspection();return;}
            bool closed=GetPanel(InspectedPanelId)==null||
                InspectedPanelId=="settings"&&!MenuOpen||
                InspectedPanelId=="camera-controls"&&LaptopCamera!=null&&!LaptopCamera.PanelOpen;
            if(closed)ReturnToForwardView();
        }
        public void ResizeUtility(string id,float delta)
        {var panel=GetPanel(id);if(panel!=null)SetScale(id,panel.Layout.scale+delta);}
        public void SetPanelDistance(string id,float delta)
        {
            var panel=GetPanel(id);if(panel==null)return;
            panel.Layout.distance=Mathf.Clamp(panel.Layout.distance+delta,.55f,3f);MarkChanged();RefreshTransforms();
        }
    }
}
