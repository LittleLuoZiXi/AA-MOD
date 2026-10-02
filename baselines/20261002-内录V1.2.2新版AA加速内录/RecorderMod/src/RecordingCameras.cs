using System;
using System.Collections.Generic;
using System.Linq;
using Il2CppInterop.Runtime.Attributes;
using UnityEngine;

namespace AzureArchive.Recorder;

public sealed partial class RecorderBehaviour
{
    readonly Dictionary<IntPtr,(int Frame,IntPtr Target,int Generation,long Order)> cameraRenderedFrames=new();
    Camera.CameraCallback? captureRenderCallback;
    long automaticCameraFrames,explicitCameraFrames;
    long builtInNaturalCameraRenders,builtInExplicitCameraCallbacks,cameraRenderOrder;
    bool explicitCameraRenderActive;
    bool renderCallbackWarning;

    [HideFromIl2Cpp]
    void BeginCameraTracking()
    {
        EndCameraTracking();
        automaticCameraFrames=0;explicitCameraFrames=0;renderCallbackWarning=false;
        builtInNaturalCameraRenders=0;builtInExplicitCameraCallbacks=0;cameraRenderOrder=0;
        explicitCameraRenderActive=false;
        captureRenderCallback=(Camera.CameraCallback)(Action<Camera>)OnCaptureCameraRendered;
        // Reuse real Built-in completion notifications when available. This
        // host lacks callable SRP events, so missing notifications retain the
        // explicit composite fallback without probing unsupported APIs.
        Camera.onPostRender+=captureRenderCallback;
        Config.Log.LogInfo("Recording render pipeline: "+(UnityEngine.Rendering.GraphicsSettings.currentRenderPipeline?.GetIl2CppType().FullName??"Built-in"));
    }

    [HideFromIl2Cpp]
    void OnCaptureCameraRendered(Camera camera)
    {
        try
        {
            var target=renderTarget;
            if(!Capturing || target==null || camera==null || camera==nativeUi?.ScreenCamera || camera.targetTexture!=target || !recordingCameras.Exists(owned=>owned!=null && owned.Pointer==camera.Pointer))return;
            // Explicit Camera.Render can raise the same native callbacks. They
            // must never be counted as natural rendering or enable frame reuse.
            if(explicitCameraRenderActive)
            {
                builtInExplicitCameraCallbacks++;
                return;
            }
            builtInNaturalCameraRenders++;
            cameraRenderedFrames[camera.Pointer]=(Time.frameCount,target.Pointer,passGeneration,++cameraRenderOrder);
        }
        catch(Exception error)
        {
            if(!renderCallbackWarning){renderCallbackWarning=true;Config.Log.LogWarning("Render observation unavailable: "+error.Message);}
        }
    }

    [HideFromIl2Cpp]
    bool RenderCaptureCameras()
    {
        // NGUI's real render event releases AA's background/title wait. Merely
        // assigning targetTexture is insufficient in the affected AA host.
        // Never fake readiness or advance past the waiting story node.
        var target=renderTarget;int generation=passGeneration,frame=Time.frameCount;
        var cameras=recordingCameras.Where(camera=>camera!=null && (camera.enabled || fastHeldCameras.ContainsKey(camera.Pointer)) && camera.gameObject.activeInHierarchy && camera.targetTexture==target).OrderBy(camera=>camera.depth).ToArray();
        if(cameras.Length==0)throw new InvalidOperationException("剧情场景没有可录制的相机。");
        // A background/title waiting on NGUI rendering still receives the real
        // explicit path. A camera notification must not substitute readiness.
        long previousOrder=0;
        bool complete=dirtyControlPanels.Count==0 && (player==null || player.isBackgroundLatest);
        foreach(var camera in cameras)
        {
            if(!cameraRenderedFrames.TryGetValue(camera.Pointer,out var rendered) || rendered.Frame!=frame || rendered.Target!=target!.Pointer || rendered.Generation!=generation || rendered.Order<=previousOrder)
            {complete=false;break;}
            previousOrder=rendered.Order;
        }
        if(complete)
        {
            automaticCameraFrames++;
            return Capturing && generation==passGeneration && renderTarget==target;
        }
        var previous=RenderTexture.active;
        bool previousExplicitRender=explicitCameraRenderActive;
        try
        {
            explicitCameraRenderActive=true;
            // If part of a multi-camera stack was missed, rebuild the whole
            // composite in depth order, so a late low-depth render cannot
            // overwrite a high-depth layer or blend it twice into the target.
            RenderTexture.active=target;GL.Clear(true,true,Color.black);
            foreach(var camera in cameras)
            {
                if(!Capturing || generation!=passGeneration || renderTarget!=target || target==null)return false;
                if(camera==null || (!camera.enabled && !fastHeldCameras.ContainsKey(camera.Pointer)) || camera.targetTexture!=target)continue;
                camera.Render();
                if(!Capturing || generation!=passGeneration || renderTarget!=target || target==null)return false;
            }
            explicitCameraFrames++;
            return true;
        }
        finally{explicitCameraRenderActive=previousExplicitRender;RenderTexture.active=previous!=null?previous:null;}
    }

    [HideFromIl2Cpp]
    void EndCameraTracking()
    {
        if(captureRenderCallback!=null)
        {
            Camera.onPostRender-=captureRenderCallback;
            captureRenderCallback=null;
            Config.Log.LogInfo($"Offscreen composite frames: automatic={automaticCameraFrames}, explicit={explicitCameraFrames}; Built-in camera callbacks: natural={builtInNaturalCameraRenders}, explicit={builtInExplicitCameraCallbacks}.");
        }
        cameraRenderedFrames.Clear();
    }
}
