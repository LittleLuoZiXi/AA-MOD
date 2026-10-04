using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using Il2CppInterop.Runtime.Attributes;
using BepInEx.Unity.IL2CPP.Utils.Collections;
using UnityEngine;
namespace AzureArchive.Recorder;

public sealed partial class RecorderBehaviour
{
    bool preflighting,replaying;
    int offlineFps,passGeneration;
    string playbackSource="",preflightSourceHash="";
    readonly HashSet<int> preflightVisited=new();
    int RecordingFps { [HideFromIl2Cpp] get=>offlineFps>0?offlineFps:Config.Fps.Value; }
    // Accelerated capture samples the source clock at the chosen output FPS.
    // Real-time fallback retains a common 60 Hz sampling workload; measured
    // playback continues to use its recorded native cadence independently.
    int CaptureSamplingFps { [HideFromIl2Cpp] get=>measureTotalRequested || FastSimulationHz>0?RecordingFps:60; }
    [HideFromIl2Cpp]
    void ResetTimingPlan()
    {
        FreezeCaptureTiming();
        durationPlan.Reset();nativeTimeline.Reset();nativeReplay=null;offlineFps=Config.Fps.Value;preflighting=measureTotalRequested;replaying=false;
        capturedFrames=0;succeeded=false;failed=false;preflightVisited.Clear();playbackSource="";preflightSourceHash="";
    }
    [HideFromIl2Cpp]
    void CheckPreflightRoute()
    {
        if(!preflighting || player==null || player.cur==lastScriptIndex || player.cur<0)return;
        if(!preflightVisited.Add(player.cur))
            Fail(new InvalidOperationException("剧情 AUTO 路线重复进入同一节点，无法确定有限总时长。请检查循环后重试；尚未开始导出。"));
    }
    [HideFromIl2Cpp]
    void CheckPreflightChoice()
    {
        if(player==null || !player.hasSelection)return;
        var manager=player.selectionManager;
        if(manager==null || !manager.isSelectionActive)return;
        if(manager.elements==null || manager.defaultSelectionIndex<0 || manager.defaultSelectionIndex>=manager.elements.Count)
            throw new InvalidOperationException("剧情中存在可选项，并且未标记 auto，故无法进行内录。");
    }
    [HideFromIl2Cpp]
    void FinishPreflight(Test finishedPlayer)
    {
        try
        {
            if(encoder!=null || readback!=null)throw new InvalidOperationException("时长计算阶段意外创建了编码器。");
            nativeTimeline.Freeze(RecordingFps);
            durationPlan.Freeze(nativeTimeline.OutputFrames,RecordingFps);
            if(Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(playbackSource)))!=preflightSourceHash)
                throw new IOException("计算时长期间剧情文件发生变化，请重新开始内录。");
            File.WriteAllText(Path.Combine(directory,"preflight.json"),JsonSerializer.Serialize(new{
                completed=true,computedUtc=DateTime.UtcNow,source=playbackSource,sourceSha256=preflightSourceHash,
                frames=durationPlan.Frames,fps=RecordingFps,videoSeconds=durationPlan.Seconds,
                nativeSeconds=nativeTimeline.Seconds,nativeSteps=nativeTimeline.StepCount,
                measureSeconds=captureClock.Elapsed.TotalSeconds,encoderStarted=false,gpuReadback=false,
                visitedRows=preflightVisited.Count,label=durationPlan.Label}));
            Config.Log.LogInfo($"Duration preflight completed: {durationPlan.Frames} frames, {durationPlan.Seconds:0.000} seconds. Total locked before encoder startup.");
            Restore();player=null;capturedFrames=0;scriptProgress=0;preflighting=false;replaying=true;
            Armed=true;waitClock.Restart();passGeneration++;
            status=durationPlan.Label+"，正在准备导出…";
            StartCoroutine(ReplayAfterPreflight(finishedPlayer,passGeneration).WrapToIl2Cpp());
        }
        catch(Exception error){Fail(error);}
    }
    [HideFromIl2Cpp]
    IEnumerator ReplayAfterPreflight(Test oldPlayer,int generation)
    {
        // Test.End is still on the native stack when the prefix above runs.
        // Do not open the second scene until its old player has been destroyed.
        var wait=Stopwatch.StartNew();
        while(Armed && replaying && generation==passGeneration && oldPlayer!=null && wait.Elapsed.TotalSeconds<20)yield return null;
        if(!Armed || !replaying || generation!=passGeneration)yield break;
        if(oldPlayer!=null){Fail(new TimeoutException("剧情预演已结束，但旧播放器未退出，已取消导出。"));yield break;}
        for(int i=0;i<5 && Armed && replaying && generation==passGeneration;i++)yield return null;
        if(!Armed || !replaying || generation!=passGeneration)yield break;
        try
        {
            if(Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(playbackSource)))!=preflightSourceHash)
                throw new IOException("计算时长后剧情文件发生变化，请重新开始内录。");
            replaying=false;waitClock.Restart();
            StartCoroutine(OpenAtMonitorResolution(playbackSource).WrapToIl2Cpp());
        }
        catch(Exception error){Fail(error);}
    }
    [HideFromIl2Cpp]
    void CancelPreflight(string reason)
    {
        var previous=player;preflighting=false;replaying=false;passGeneration++;
        Restore();player=null;busy=false;capturedFrames=0;
        status=reason+"；尚未生成视频。";
        if(previous!=null)try{previous.End();}catch(Exception error){Config.Log.LogWarning(error.Message);}
        if(directory.Length>0)File.WriteAllText(Path.Combine(directory,"preflight-cancelled.json"),JsonSerializer.Serialize(new{reason,videoCreated=false}));
        if(smokeSource.Length>0)Application.Quit(2);
    }
    [HideFromIl2Cpp]
    internal void PlayerDestroyed(Test value)
    {
        if(Capturing && OwnsPlayer(value))Stop("剧情播放器已退出");
    }
}
