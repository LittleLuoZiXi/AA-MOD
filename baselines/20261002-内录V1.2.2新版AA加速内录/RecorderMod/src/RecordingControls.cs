using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Il2CppInterop.Runtime.Attributes;
using UnityEngine;

namespace AzureArchive.Recorder;

public sealed partial class RecorderBehaviour
{
    readonly Dictionary<IntPtr,(GameObject Object,bool Active)> hiddenControlRoots=new();
    readonly Dictionary<IntPtr,(UIWidget Widget,bool Enabled)> hiddenControlWidgets=new();
    readonly Dictionary<IntPtr,UIPanel> dirtyControlPanels=new();
    readonly bool controlsProbe=Environment.GetCommandLineArgs().Contains("--aa-recorder-controls-probe");
    string controlPass="";
    int controlCaptureChecks,controlVisibleFrames,controlRefreshes;

    [HideFromIl2Cpp]
    internal void HideControls(Test value)
    {
        if(!Capturing || player==null || player.Pointer!=value.Pointer)return;
        if(controlPass.Length==0)controlPass=preflighting?"preflight":"recording";
        SuppressControl(value.menuBtn?.gameObject);
        SuppressControl(value.autoToggle?.gameObject);
    }

    [HideFromIl2Cpp]
    void SuppressControl(GameObject? root)
    {
        if(root==null)return;
        bool knownRoot=hiddenControlRoots.ContainsKey(root.Pointer);
        // An already hidden tree cannot draw. First sightings and every active
        // reactivation still scan all widgets; never skip the pre-composite
        // HideControls call or the pending panel refresh in PrepareControlVisibility.
        if(knownRoot && !root.activeSelf)return;
        if(!knownRoot)hiddenControlRoots.Add(root.Pointer,(root,root.activeSelf));
        // Native dialogue coroutines can reactivate the parent after Test.Update.
        // A disabled widget stays disabled when its parent is activated again.
        // Scope this to MENU/AUTO; choices and their shared panels stay enabled.
        foreach(var widget in root.GetComponentsInChildren<UIWidget>(true))
        {
            if(widget==null)continue;
            if(!hiddenControlWidgets.ContainsKey(widget.Pointer))hiddenControlWidgets.Add(widget.Pointer,(widget,widget.enabled));
            var panel=widget.panel;
            if(panel==null && widget.drawCall!=null)panel=widget.drawCall.panel;
            bool changed=widget.enabled || widget.panel!=null;
            if(changed && panel!=null)dirtyControlPanels[panel.Pointer]=panel;
            if(widget.enabled)widget.enabled=false;
            if(widget.panel!=null)widget.RemoveFromPanel();
        }
        if(root.activeSelf)root.SetActive(false);
    }

    [HideFromIl2Cpp]
    void PrepareControlVisibility()
    {
        if(player!=null)HideControls(player);
        if(dirtyControlPanels.Count>0)
        {
            // NGUI may already have batched this frame. Rebuild affected panels
            // before rendering, and never reuse a composite containing old UI.
            cameraRenderedFrames.Clear();
            foreach(var panel in dirtyControlPanels.Values)if(panel!=null){panel.Refresh();controlRefreshes++;}
            dirtyControlPanels.Clear();
        }
        if(controlsProbe)
        {
            controlCaptureChecks++;
            if(hiddenControlWidgets.Values.Any(state=>state.Widget!=null && state.Widget.enabled && state.Widget.gameObject.activeInHierarchy))controlVisibleFrames++;
        }
    }

    [HideFromIl2Cpp]
    void RestoreControls()
    {
        if(hiddenControlRoots.Count==0 && hiddenControlWidgets.Count==0)return;
        int liveWidgets=0,restoredWidgets=0,liveRoots=0,restoredRoots=0;
        foreach(var state in hiddenControlRoots.Values)
        {
            if(state.Object==null)continue;
            liveRoots++;
            try{state.Object.SetActive(state.Active);if(state.Object.activeSelf==state.Active)restoredRoots++;}
            catch(Exception error){Config.Log.LogWarning("Could not restore playback control: "+error.Message);}
        }
        foreach(var state in hiddenControlWidgets.Values)
        {
            if(state.Widget==null)continue;
            liveWidgets++;
            try
            {
                state.Widget.enabled=state.Enabled;
                if(state.Widget.enabled==state.Enabled)restoredWidgets++;
                var panel=state.Widget.panel;
                if(panel!=null)dirtyControlPanels[panel.Pointer]=panel;
            }
            catch(Exception error){Config.Log.LogWarning("Could not restore playback widget: "+error.Message);}
        }
        foreach(var panel in dirtyControlPanels.Values)
            if(panel!=null)try{panel.Refresh();}catch(Exception error){Config.Log.LogWarning("Could not refresh playback controls: "+error.Message);}
        if(controlsProbe && directory.Length>0)
            try
            {
                File.WriteAllText(Path.Combine(directory,"controls-"+controlPass+".json"),JsonSerializer.Serialize(new{
                    pass=controlPass,roots=hiddenControlRoots.Count,widgets=hiddenControlWidgets.Count,
                    captureChecks=controlCaptureChecks,visibleWidgetFrames=controlVisibleFrames,panelRefreshes=controlRefreshes,
                    liveWidgets,restoredWidgets,liveRoots,restoredRoots,
                    restored=liveWidgets==restoredWidgets && liveRoots==restoredRoots}));
            }
            catch(Exception error){Config.Log.LogWarning("Control visibility diagnostic unavailable: "+error.Message);}
        hiddenControlRoots.Clear();hiddenControlWidgets.Clear();dirtyControlPanels.Clear();
        controlPass="";controlCaptureChecks=0;controlVisibleFrames=0;controlRefreshes=0;
    }
}
