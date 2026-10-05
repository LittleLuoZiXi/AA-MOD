using System;
using System.Linq;
using AzureArchive.Automation;
using HarmonyLib;
using Studio.Scripts;
using UnityEngine;
using Object=UnityEngine.Object;

namespace AzureArchive.RevisionCompare;

// AA's player and audio manager are global singletons. Borrow the existing
// editor player for this short preview instead of creating a second engine.
// The selected side shows the native live target while the other side retains
// an owned frozen frame. Project data is never swapped, and borrowed textures
// are never released by this class.
internal sealed class ComparisonPreview : IDisposable
{
    static ComparisonPreview? active;
    static Harmony? playbackHarmony;
    static bool hooksActive;
    static int environmentSyncDepth;
    readonly Action<string> report;
    ScriptNodeInspector? inspector;
    Test? player;
    UITexture? editorDisplay;
    Texture? originalDisplayTexture;
    RenderTexture? borrowedOutput,frozenCurrent,frozenPrevious;
    FlatData.IScenarioScriptExcel? previousLine;
    bool displaySwapped,playingPrevious,allowAdvance,hasPlayed,stopping,pendingCurrent;
    bool originalAuto,originalPreviewMode;
    long comparisonBgmId;
    string frozenPreviousText="";
    int previousReadyFrame=-1;

    internal ComparisonPreview(Action<string> report)=>this.report=report;
    internal bool IsPlayingPrevious=>displaySwapped && playingPrevious;
    internal RenderTexture? Texture=>playingPrevious?borrowedOutput:frozenPrevious;
    internal string PreviewText=>playingPrevious?ReadDisplayedText():frozenPreviousText;
    internal bool PreviewReady=>displaySwapped && hasPlayed && player!=null && !player.willDestroy &&
        (playingPrevious?player.ready && borrowedOutput!=null && borrowedOutput.IsCreated():frozenPrevious!=null && frozenPrevious.IsCreated());

    string ReadDisplayedText()=>player?.dialogPanel!=null?string.Join("",player.dialogPanel.GetComponentsInChildren<UILabel>().Where(x=>x.gameObject.activeInHierarchy).Select(x=>x.text)):"";

    // Preparing the feature does not detour AA's ordinary preview at startup.
    internal static void InstallHooks(Harmony harmony)
    {playbackHarmony ??= new Harmony(harmony.Id+".preview");}

    static void ActivateHooks()
    {
        if(hooksActive)return;
        var harmony=playbackHarmony ?? throw new InvalidOperationException("对比预览尚未初始化。");
        hooksActive=true;
        harmony.Patch(AccessTools.Method(typeof(ScriptNodeInspector),nameof(ScriptNodeInspector.PlayPreview)),prefix:new HarmonyMethod(typeof(ComparisonPreview),nameof(KeepFrozenComparison)));
        harmony.Patch(AccessTools.Method(typeof(ScriptNodeInspector),nameof(ScriptNodeInspector.SetBGM)),prefix:new HarmonyMethod(typeof(ComparisonPreview),nameof(KeepFrozenComparison)));
        harmony.Patch(AccessTools.Method(typeof(ScriptNodeInspector),nameof(ScriptNodeInspector.SyncEnvironmentProperties)),prefix:new HarmonyMethod(typeof(ComparisonPreview),nameof(BeginEnvironmentSync)),finalizer:new HarmonyMethod(typeof(ComparisonPreview),nameof(EndEnvironmentSync)));
        harmony.Patch(AccessTools.Method(typeof(AudioManager),nameof(AudioManager.SetBGM),new[]{typeof(long)}),prefix:new HarmonyMethod(typeof(ComparisonPreview),nameof(KeepComparisonBgmDuringEditorSync)));
        harmony.Patch(AccessTools.Method(typeof(Test),nameof(Test.AdvanceScenario)),prefix:new HarmonyMethod(typeof(ComparisonPreview),nameof(AllowOneLineOnly)));
    }

    internal void Play(Test source,FlatData.IScenarioScriptExcel oldLine,float ratio)
    {
        Stop();
        var currentInspector=ScriptNodeInspector.instance;
        if(currentInspector==null || currentInspector.preview==null || source==null || currentInspector.preview.Pointer!=source.Pointer)
            throw new InvalidOperationException("请先在编辑器中选中一条对话。");
        var display=currentInspector.transform.Find("Preview")?.GetComponent<UITexture>();
        var currentImage=display?.mainTexture;
        if(display==null || currentImage==null)
            throw new InvalidOperationException("原生预览画面尚未准备好，请先播放一次。");
        var nativeOutput=currentImage.TryCast<RenderTexture>();
        // A skin may put a plain Texture on the display while its native camera
        // still has a live target. Use only a camera belonging to this player.
        if(nativeOutput==null && source.text!=null)
        {
            var visualRoot=source.text.GetComponentInParent<UIRoot>();
            nativeOutput=visualRoot?.GetComponentsInChildren<Camera>(true).Where(c=>c!=null && c.targetTexture!=null).Select(c=>c.targetTexture).FirstOrDefault();
        }
        if(nativeOutput==null || !nativeOutput.IsCreated())
            throw new InvalidOperationException("当前原生预览未提供实时画面，请播放后重试。");

        inspector=currentInspector;player=source;editorDisplay=display;
        originalDisplayTexture=currentImage;borrowedOutput=nativeOutput;previousLine=oldLine;
        comparisonBgmId=ResolveFrozenBgm(oldLine);
        originalAuto=source.auto;originalPreviewMode=source.previewMode;active=this;
        try
        {
            FreezeFrame(ref frozenCurrent,currentImage,"AARevisionCompare_CurrentFrame");
            ActivateHooks();
            editorDisplay.mainTexture=frozenCurrent;displaySwapped=true;playingPrevious=true;
            Replay();
            RevisionComparePlugin.Instance.Log.LogInfo("Comparison preview borrowed native player; right display frozen, native audio and rendering active.");
        }
        catch{Stop();throw;}
    }

    internal void Replay()
    {
        if(!displaySwapped || player==null || previousLine==null || editorDisplay==null || borrowedOutput==null)
            throw new InvalidOperationException("原生预览或旧版对话已失效，请重新打开对比。");
        pendingCurrent=false;previousReadyFrame=-1;
        if(!playingPrevious)
        {
            RefreshBorrowedOutput();
            RenderBorrowedOutput();
            FreezeFrame(ref frozenCurrent,editorDisplay.mainTexture ?? borrowedOutput,"AARevisionCompare_CurrentFrame");
            playingPrevious=true;
            editorDisplay.mainTexture=frozenCurrent;
        }
        player.StopAllCoroutines();
        player.Clear(new Il2CppSystem.Nullable<uint>());
        player.previewMode=true;player.auto=false;
        // Native AdvanceScenario(preview:true) deliberately skips BGM. AA's
        // editor normally sets it separately while syncing environment fields.
        // Here it must come from the frozen version, never the current editor.
        if(AudioManager.Instance!=null)AudioManager.Instance.SetBGM(comparisonBgmId);
        hasPlayed=false;
        var current=previousLine.TryCast<Script>();
        // Match AA's PlayPreview: prepare the previous stage with its voice and
        // transition disabled, then play this frozen line as continuous. These
        // are detached snapshots, never the live project Script instances.
        if(current?.prev!=null)
        {
            var stage=new Script(current.prev,false);
            stage.voice="";stage.transition=0;
            AdvanceOnce(stage.Cast<FlatData.IScenarioScriptExcel>());
            RevisionComparePlugin.Instance.Log.LogInfo("Comparison preview: primed frozen previous stage.");
        }
        bool continuous=current?.continuous ?? false;
        try
        {
            if(current!=null)current.continuous=true;
            AdvanceOnce(previousLine);hasPlayed=true;
        }
        finally{if(current!=null)current.continuous=continuous;}
        RevisionComparePlugin.Instance.Log.LogInfo("Comparison preview: playing one frozen line through the native player.");
    }

    internal void PlayCurrent()
    {
        if(!displaySwapped || player==null || inspector==null || editorDisplay==null || borrowedOutput==null)
            throw new InvalidOperationException("原生预览或旧版对话已失效，请重新打开对比。");
        // Once the right side is live its original serialized button listener
        // already performs playback. Never freeze a current-version frame into
        // the previous-version image on a repeated toolbar click.
        if(!playingPrevious)return;
        // AdvanceScenario can finish its setup before the camera has produced
        // an old-version frame. Keep the request pending until that frame can
        // be captured, rather than preserving the opening current-version RT.
        if(!hasPlayed || !player.ready || previousReadyFrame<0 || Time.frameCount<=previousReadyFrame)
        {pendingCurrent=true;return;}
        try
        {
            pendingCurrent=false;
            RefreshBorrowedOutput();
            RenderBorrowedOutput();
            FreezeFrame(ref frozenPrevious,borrowedOutput,"AARevisionCompare_PreviousFrame");
            frozenPreviousText=ReadDisplayedText();
            if(string.IsNullOrEmpty(frozenPreviousText))frozenPreviousText=previousLine?.TryCast<Script>()?.text ?? "";
            playingPrevious=false;allowAdvance=false;
            editorDisplay.mainTexture=borrowedOutput;
            player.auto=originalAuto;player.previewMode=originalPreviewMode;
            inspector.SetBGM();inspector.PlayPreview();
            RevisionComparePlugin.Instance.Log.LogInfo("Comparison preview: current editor playback active; previous frame remains pinned in the window.");
        }
        catch{Stop();throw;}
    }

    void RenderBorrowedOutput()
    {
        if(borrowedOutput==null)return;
        var previousTarget=RenderTexture.active;
        try
        {
            foreach(var camera in Camera.allCameras)
                if(camera.enabled && camera.targetTexture!=null && camera.targetTexture.Pointer==borrowedOutput.Pointer)camera.Render();
        }
        finally{RenderTexture.active=previousTarget;}
    }

    static void FreezeFrame(ref RenderTexture? target,Texture source,string name)
    {
        int width=Math.Max(1,source.width),height=Math.Max(1,source.height);
        if(target==null || target.width!=width || target.height!=height || !target.IsCreated())
        {
            var replacement=new RenderTexture(width,height,0,RenderTextureFormat.ARGB32)
            {name=name,hideFlags=HideFlags.HideAndDontSave};
            if(!replacement.Create())
            {Object.Destroy(replacement);throw new InvalidOperationException("无法保留对比预览画面。");}
            if(target!=null){target.Release();Object.Destroy(target);}
            target=replacement;
        }
        var previousTarget=RenderTexture.active;
        try{Graphics.Blit(source,target);}finally{RenderTexture.active=previousTarget;}
    }

    void AdvanceOnce(FlatData.IScenarioScriptExcel line)
    {
        allowAdvance=true;
        try{player!.AdvanceScenario(true,line);}finally{allowAdvance=false;}
    }

    static long ResolveFrozenBgm(FlatData.IScenarioScriptExcel line)
    {
        if(line.BGMId!=0)return line.BGMId;
        var seen=new System.Collections.Generic.HashSet<IntPtr>();
        for(var script=line.TryCast<Script>();script!=null && seen.Add(script.Pointer);script=script.prev)
            if(script.bgmId!=0)return script.bgmId;
        return 999; // AA's ResolveBgmIdForPreview fallback after inherited 0s.
    }

    internal void Update()
    {
        if(!displaySwapped || stopping)return;
        RefreshBorrowedOutput();
        if(player==null || inspector==null || editorDisplay==null || borrowedOutput==null || !borrowedOutput.IsCreated())
        {Stop();report("原生预览已变化，请重新打开对比。");return;}
        if(playingPrevious && hasPlayed && player.ready)
        {
            if(previousReadyFrame<0)previousReadyFrame=Time.frameCount;
            if(pendingCurrent && Time.frameCount>previousReadyFrame)PlayCurrent();
        }
    }

    void RefreshBorrowedOutput()
    {
        if(editorDisplay==null)return;
        // AA can refresh its display texture after a resize. Preserve our frozen
        // current frame only while the old line owns the player. While the right
        // side is live, leave native texture changes and playback unrestricted.
        var displayed=editorDisplay.mainTexture;
        if(displayed!=null && (frozenCurrent==null || displayed.Pointer!=frozenCurrent.Pointer) &&
            (frozenPrevious==null || displayed.Pointer!=frozenPrevious.Pointer) &&
            (borrowedOutput==null || displayed.Pointer!=borrowedOutput.Pointer))
        {
            originalDisplayTexture=displayed;
            var live=originalDisplayTexture.TryCast<RenderTexture>();
            if(live!=null && live.IsCreated())borrowedOutput=live;
        }
        if(playingPrevious && frozenCurrent!=null)editorDisplay.mainTexture=frozenCurrent;
    }

    static bool KeepFrozenComparison(ScriptNodeInspector __instance)
    {return active==null || !active.IsPlayingPrevious || active.inspector==null || __instance.Pointer!=active.inspector.Pointer;}
    static void BeginEnvironmentSync(ScriptNodeInspector __instance,out bool __state)
    {__state=!KeepFrozenComparison(__instance);if(__state)environmentSyncDepth++;}
    static Exception? EndEnvironmentSync(Exception? __exception,bool __state)
    {if(__state)environmentSyncDepth=Math.Max(0,environmentSyncDepth-1);return __exception;}
    static bool KeepComparisonBgmDuringEditorSync()
    {return environmentSyncDepth==0 || active==null || !active.IsPlayingPrevious;}
    static bool AllowOneLineOnly(Test __instance)
    {
        var owner=active;
        if(owner==null || !owner.IsPlayingPrevious || owner.player==null || __instance.Pointer!=owner.player.Pointer)return true;
        if(!owner.allowAdvance)return false;
        owner.allowAdvance=false;
        return true;
    }

    internal void Stop()
    {
        if(stopping)return;
        stopping=true;
        var restorePlayback=displaySwapped;
        displaySwapped=false;playingPrevious=false;allowAdvance=false;hasPlayed=false;pendingCurrent=false;previousReadyFrame=-1;
        if(active==this)active=null;
        try
        {
            if(hooksActive){playbackHarmony?.UnpatchSelf();hooksActive=false;}
            environmentSyncDepth=0;
            if(editorDisplay!=null)
                editorDisplay.mainTexture=originalDisplayTexture!=null?originalDisplayTexture:borrowedOutput;
            if(player!=null){player.auto=originalAuto;player.previewMode=originalPreviewMode;}
            // Rebuild from whatever line is now selected, including edits made
            // while the floating window was open or a newly selected line.
            if(restorePlayback && inspector!=null && inspector.gameObject.activeInHierarchy &&
                inspector.selectedScriptItem!=null && inspector.preview!=null && AuthoringEditorSession.Current!=null)
            {inspector.SetBGM();inspector.PlayPreview();}
        }
        catch(Exception ex){report("已关闭对比；恢复当前预览时遇到问题："+ex.Message);}
        finally
        {
            if(frozenCurrent!=null){frozenCurrent.Release();Object.Destroy(frozenCurrent);}
            if(frozenPrevious!=null){frozenPrevious.Release();Object.Destroy(frozenPrevious);}
            // borrowedOutput and originalDisplayTexture belong to AA.
            frozenCurrent=null;frozenPrevious=null;borrowedOutput=null;originalDisplayTexture=null;frozenPreviousText="";
            inspector=null;player=null;editorDisplay=null;previousLine=null;stopping=false;
        }
    }
    public void Dispose()=>Stop();
}
