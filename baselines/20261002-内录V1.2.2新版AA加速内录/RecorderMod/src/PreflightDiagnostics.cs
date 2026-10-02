using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
using TitleAnimation=ScenarioAnimation.TitleAnimation;

namespace AzureArchive.Recorder;

// No HarmonyPatch attributes: ordinary PatchAll must never discover these
// optional probes. Diagnostic failures cannot alter native playback.
internal static class PreflightDiagnostics
{
    static readonly Dictionary<string,(string State,double Next)> samples=new();
    static readonly HashSet<string> reportedErrors=new();
    static readonly Stopwatch wall=Stopwatch.StartNew();
    static Action<string>? warn;
    static Test? observedPlayer;
    static readonly Dictionary<string,double> nextSnapshots=new();

    internal static void Install(Harmony harmony,Action<string> warning)
    {
        if(!Environment.GetCommandLineArgs().Contains("--aa-recorder-choice-probe"))return;
        warn=warning;
        void Patch(Type? type,string name,Delegate callback,bool prefix=false)
        {
            try
            {
                var target=type==null?null:AccessTools.Method(type,name);
                if(target==null){warning($"Preflight diagnostic unavailable: {type?.FullName}.{name}");return;}
                var hook=new HarmonyMethod(callback.Method);
                harmony.Patch(target,prefix?hook:null,prefix?null:hook);
            }
            catch(Exception error){warning($"Preflight diagnostic skipped: {type?.FullName}.{name}: {error.Message}");}
        }
        try
        {
            Patch(typeof(Test),nameof(Test.Update),(Action<Test>)PlayerUpdate);
            Patch(typeof(Test),"OnBgChanged",(Action<Test,bool>)BackgroundChangedBefore,true);
            Patch(typeof(Test),"OnBgChanged",(Action<Test,bool>)BackgroundChangedAfter);
            Patch(typeof(BackgroundPresenter.Layer),"OnImageRendered",(Action<BackgroundPresenter.Layer,Material>)ImageRenderedBefore,true);
            Patch(typeof(Test),"OnReady",(Action<Test>)ReadyBefore,true);
            Patch(typeof(Test),"OnReady",(Action<Test>)ReadyAfter);
            Patch(typeof(TitleAnimation),"Complete",(Action<TitleAnimation,bool>)TitleCompleteBefore,true);
            Patch(typeof(TitleAnimation),"Complete",(Action<TitleAnimation,bool>)TitleCompleteAfter);
            var task=typeof(TitleAnimation).GetNestedTypes(BindingFlags.Public|BindingFlags.NonPublic)
                .FirstOrDefault(t=>t.Name.StartsWith("_TitleTask_d__",StringComparison.Ordinal));
            Patch(task,"MoveNext",(Action<object,bool>)TitleTaskAfter);
            Patch(typeof(TitleAnimation),"_TitleTask_b__8_0",(Action<TitleAnimation,bool,MethodBase>)TitlePredicateAfter);
            Patch(typeof(TitleAnimation),"_TitleTask_b__8_1",(Action<TitleAnimation,bool,MethodBase>)TitlePredicateAfter);
        }
        catch(Exception error){ReportError("install",error);}
    }

    static bool Owns(Test? player)
    {
        var recorder=RecorderBehaviour.Instance;
        return player!=null && recorder?.Capturing==true && recorder.OwnsPlayer(player);
    }

    static bool Due(string key,string state)
    {
        double now=wall.Elapsed.TotalSeconds;
        if(samples.TryGetValue(key,out var previous) && previous.State==state && now<previous.Next)return false;
        // Each key includes a native instance pointer. Bound retention across
        // many projects in one diagnostic process without affecting playback.
        if(samples.Count>256)samples.Clear();
        samples[key]=(state,now+1);
        return true;
    }

    static void ReportError(string scope,Exception error)
    {
        try
        {
            string key=scope+":"+error.GetType().Name;
            if(reportedErrors.Add(key))warn?.Invoke($"Preflight diagnostic sample skipped ({scope}): {error.Message}");
        }
        catch { }
    }

    static string F(float value)=>value.ToString("0.000",CultureInfo.InvariantCulture);
    static string PlayerState(Test player)=>
        $"cur={player.cur} ready={player.ready} locked={player.locked} auto={player.auto}/{player.IsAutoEnabled} stopped={player.stoppedAnimCount} anims={player.currentAnims?.Count}";

    static string AnimationStates(UnityEngine.Animation animation)
    {
        var values=new List<string>();
        var entries=animation.GetEnumerator();
        for(int i=0;i<8 && entries.MoveNext();i++)
        {
            var state=entries.Current?.TryCast<AnimationState>();
            if(state!=null)values.Add($"{state.name}:time={F(state.time)},normalized={F(state.normalizedTime)},speed={F(state.speed)},enabled={state.enabled}");
        }
        return string.Join("|",values);
    }

    static bool SnapshotDue(string key)
    {
        double now=wall.Elapsed.TotalSeconds;
        if(nextSnapshots.TryGetValue(key,out var next) && now<next)return false;
        if(nextSnapshots.Count>256)nextSnapshots.Clear();
        nextSnapshots[key]=now+1;
        return true;
    }

    static string Inspect(string scope,Func<string> read)
    {
        try{return read();}
        catch(Exception error){ReportError(scope,error);return "<unavailable:"+error.GetType().Name+">";}
    }

    static string TextureState(Texture? texture)=>texture==null?"<null>":$"{texture.name}@{texture.Pointer}({texture.width}x{texture.height})";

    static string ContentState(BackgroundPresenter.Content? content)
    {
        if(content==null)return "<null>";
        var resource=content.Resource;
        string identity=resource==null?"<null>":$"id={resource.Id},name={resource.Name},type={resource.Type},source={resource.Source},path={resource.FullPath}";
        return $"ptr={content.Pointer},disposed={content.disposed},texture={TextureState(content.Image)},spine={content.Spine?.Pointer},resource=[{identity}]";
    }

    static string ImageState(UITexture? image)
    {
        if(image==null)return "<null>";
        var panel=image.panel;
        var call=image.drawCall;
        var mesh=call?.mRenderer;
        var geometry=image.geometry;
        return $"ptr={image.Pointer},name={image.name},active={image.gameObject.activeSelf}/{image.gameObject.activeInHierarchy},enabled={image.enabled},layer={image.gameObject.layer},alpha={F(image.alpha)},lastAlpha={F(image.mLastAlpha)},visible={image.isVisible},visibleAlpha={image.mIsVisibleByAlpha},visiblePanel={image.mIsVisibleByPanel},inFront={image.mIsInFront},hideOffscreen={image.hideIfOffScreen},size={image.width}x{image.height},texture={TextureState(image.mainTexture)},onRender={image.mOnRender?.Pointer},"+
            $"panel=[ptr={panel?.Pointer},enabled={panel?.enabled},active={panel?.gameObject.activeInHierarchy},alpha={panel?.alpha},depth={panel?.depth},clip={panel?.clipping},alwaysOnScreen={panel?.alwaysOnScreen},widgets={panel?.widgets?.Count},drawCalls={panel?.drawCalls?.Count}],"+
            $"drawCall=[ptr={call?.Pointer},enabled={call?.enabled},active={call?.gameObject.activeInHierarchy},layer={call?.gameObject.layer},widgets={call?.widgetCount},triangles={call?.triangles},dirty={call?.isDirty},onRender={call?.onRender?.Pointer},texture={TextureState(call?.mTexture)},dynamicMaterial={call?.mDynamicMat?.Pointer},meshEnabled={mesh?.enabled},meshVisible={mesh?.isVisible},meshActive={mesh?.gameObject.activeInHierarchy},meshBounds={mesh?.bounds},world={image.transform.position},scale={image.transform.lossyScale}],"+
            $"geometry=[ptr={geometry?.Pointer},hasVertices={geometry?.hasVertices},transformed={geometry?.hasTransformed},verts={geometry?.verts?.Count},uvs={geometry?.uvs?.Count},colors={geometry?.cols?.Count}]";
    }

    static string LayerState(BackgroundPresenter.Layer? layer)
    {
        if(layer==null)return "<null>";
        return $"ptr={layer.Pointer},alpha={F(layer.Alpha)},queue={layer.Queue},renderedCallback={layer.Rendered?.Pointer},"+
            $"content=[{Inspect("layer-content",()=>ContentState(layer.Content))}],image=[{Inspect("layer-image",()=>ImageState(layer.Image))}],layerPanel={layer.Panel?.Pointer}";
    }

    static string BackgroundState(Test player)
    {
        // Backgrounds is a lazy getter; inspect its existing backing field so
        // sampling cannot initialize the presenter or change native readiness.
        var presenter=player.backgroundPresenter;
        if(presenter==null)return $"latest={player.isBackgroundLatest},presenter=<uninitialized>";
        return $"latest={player.isBackgroundLatest},presenter={presenter.Pointer},hasOverlap={presenter.HasOverlap},"+
            $"current=[{Inspect("current-content",()=>ContentState(presenter.current))}],target=[{Inspect("target-content",()=>ContentState(presenter.target))}],"+
            $"main=[{Inspect("main-layer",()=>LayerState(presenter.main))}],overlap=[{Inspect("overlap-layer",()=>LayerState(presenter.overlap))}]";
    }

    static string CameraStates()
    {
        var values=new List<string>();
        foreach(var camera in Camera.allCameras.Take(16))
            values.Add(Inspect("camera",()=>camera==null?"<null>":$"name={camera.name},ptr={camera.Pointer},active={camera.gameObject.activeInHierarchy},enabled={camera.enabled},mask=0x{unchecked((uint)camera.cullingMask):X8},target={TextureState(camera.targetTexture)},depth={F(camera.depth)},clear={camera.clearFlags},position={camera.transform.position},rotation={camera.transform.rotation},ortho={camera.orthographic}/{camera.orthographicSize},clip={camera.nearClipPlane}/{camera.farClipPlane},aspect={camera.aspect},rect={camera.rect},pixelRect={camera.pixelRect}"));
        return string.Join(" | ",values);
    }

    static void SampleBackground(string phase,Test player,string detail="")
    {
        if(!Owns(player))return;
        observedPlayer=player;
        if(!SnapshotDue("background:"+phase+":"+player.Pointer))return;
        RecorderPlugin.Instance.Log.LogInfo($"PREFLIGHT BACKGROUND phase={phase} wall={wall.Elapsed.TotalSeconds:0.000} videoTime={RecorderBehaviour.Instance?.PlaybackTime} {PlayerState(player)} {detail} {Inspect("background",()=>BackgroundState(player))}");
        if(SnapshotDue("cameras:"+player.Pointer))
            RecorderPlugin.Instance.Log.LogInfo($"PREFLIGHT CAMERAS wall={wall.Elapsed.TotalSeconds:0.000} [{CameraStates()}]");
    }

    static void SampleTitle(string phase,TitleAnimation title,string detail="")
    {
        if(!Owns(title.owner))return;
        SampleBackground("title",title.owner);
        var settings=title.settings;
        var animation=settings?.titleAnimation;
        string state=PlayerState(title.owner)+$" started={title.isStarted} completed={title.hasCompleted} cancelled={title.isCancelled} rootActive={settings?.titleRoot?.activeInHierarchy} animationEnabled={animation?.enabled} playing={animation?.isPlaying} culling={animation?.cullingType} tweenEnabled={settings?.tweenAlpha?.enabled} "+detail;
        string key=phase+":"+title.Pointer;
        if(!Due(key,state))return;
        string timing=animation==null?"<missing>":AnimationStates(animation);
        RecorderPlugin.Instance.Log.LogInfo($"PREFLIGHT TITLE phase={phase} wall={wall.Elapsed.TotalSeconds:0.000} videoTime={RecorderBehaviour.Instance?.PlaybackTime} {state} states=[{timing}] tweenFactor={settings?.tweenAlpha?.tweenFactor}");
    }

    internal static void PlayerUpdate(Test __instance)
    {
        try
        {
            if(!Owns(__instance))return;
            observedPlayer=__instance;
            // MoveNext is not called while a WaitUntil keeps waiting. This
            // heartbeat still shows whether the underlying animation advances.
            var animations=__instance.currentAnims;
            if(animations==null)return;
            for(int i=0;i<animations.Count && i<16;i++)
            {
                var title=animations[i]?.TryCast<TitleAnimation>();
                if(title!=null)SampleTitle("heartbeat",title);
            }
        }
        catch(Exception error){ReportError("heartbeat",error);}
    }

    internal static void BackgroundChangedBefore(Test __instance,bool __0)
    {try{SampleBackground("bg-changed-before",__instance,"argument="+__0);}catch(Exception error){ReportError("bg-changed-before",error);}}
    internal static void BackgroundChangedAfter(Test __instance,bool __0)
    {try{SampleBackground("bg-changed-after",__instance,"argument="+__0);}catch(Exception error){ReportError("bg-changed-after",error);}}

    internal static void ImageRenderedBefore(BackgroundPresenter.Layer __instance,Material __0)
    {
        try
        {
            var player=observedPlayer;
            if(!Owns(player))return;
            var presenter=player!.backgroundPresenter;
            if(presenter==null || (presenter.main?.Pointer!=__instance.Pointer && presenter.overlap?.Pointer!=__instance.Pointer))return;
            if(!SnapshotDue("image-rendered:"+__instance.Pointer))return;
            RecorderPlugin.Instance.Log.LogInfo($"PREFLIGHT IMAGE-RENDERED wall={wall.Elapsed.TotalSeconds:0.000} player={player.Pointer} latest={player.isBackgroundLatest} material={__0?.name}@{__0?.Pointer} layer=[{Inspect("image-rendered-layer",()=>LayerState(__instance))}]");
        }
        catch(Exception error){ReportError("image-rendered",error);}
    }

    static void Ready(string phase,Test player)
    {
        if(!Owns(player))return;
        string state=PlayerState(player);
        if(Due(phase+":"+player.Pointer,state))
            RecorderPlugin.Instance.Log.LogInfo($"PREFLIGHT READY phase={phase} wall={wall.Elapsed.TotalSeconds:0.000} videoTime={RecorderBehaviour.Instance?.PlaybackTime} {state}");
    }
    internal static void ReadyBefore(Test __instance)
    {try{Ready("before",__instance);}catch(Exception error){ReportError("ready-before",error);}}
    internal static void ReadyAfter(Test __instance)
    {try{Ready("after",__instance);}catch(Exception error){ReportError("ready-after",error);}}
    internal static void TitleCompleteBefore(TitleAnimation __instance,bool __0)
    {try{SampleTitle("complete-before",__instance,"argument="+__0);}catch(Exception error){ReportError("complete-before",error);}}
    internal static void TitleCompleteAfter(TitleAnimation __instance,bool __0)
    {try{SampleTitle("complete-after",__instance,"argument="+__0);}catch(Exception error){ReportError("complete-after",error);}}

    internal static void TitleTaskAfter(object __instance,bool __result)
    {
        try
        {
            var type=__instance.GetType();
            object? Read(string name)=>type.GetProperty(name)?.GetValue(__instance);
            var title=Read("__4__this") as TitleAnimation;
            if(title==null || !Owns(title.owner))return;
            var current=Read("__2__current") as Il2CppSystem.Object;
            SampleTitle("task",title,$"state={Read("__1__state")} result={__result} yield={current?.GetIl2CppType().FullName??"<null>"}");
        }
        catch(Exception error){ReportError("title-task",error);}
    }

    internal static void TitlePredicateAfter(TitleAnimation __instance,bool __result,MethodBase __originalMethod)
    {
        try{SampleTitle(__originalMethod.Name,__instance,"predicate="+__result);}
        catch(Exception error){ReportError("title-predicate",error);}
    }
}