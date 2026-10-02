using System;
using System.IO;
using Il2CppInterop.Runtime.Attributes;
using UnityEngine;
using Object=UnityEngine.Object;

namespace AzureArchive.Recorder;

public sealed partial class RecorderBehaviour
{
    [HideFromIl2Cpp]
    unsafe void CompleteLiveStep()
    {
        if(liveTimeline==null || livePreviousTarget==null)throw new InvalidOperationException("录制快照尚未准备完成。");
        float delta=Time.unscaledDeltaTime;
        var batch=liveTimeline.Advance(delta);
        float scaledDelta=Time.deltaTime;liveScaledSeconds+=scaledDelta;
        if(delta-scaledDelta>1e-6f){liveClampedSourceSteps++;liveClockClampSeconds+=delta-scaledDelta;}
        liveMaximumSourceStep=Math.Max(liveMaximumSourceStep,delta);
        if(delta>.1f)liveSourceStepsOver100ms++;
        if(delta>1f)liveSourceStepsOver1Second++;
        // Accelerated capture maps unscaled delta to the fixed scenario delta;
        // real-time fallback observes the native elapsed interval. Ask the
        // synchronous mixer for that same interval, then restore the source
        // clock setting before Unity starts its next frame.
        float priorCaptureDelta=Time.captureDeltaTime;
        float[] samples;
        try
        {
            Time.captureDeltaTime=delta;
            int rate=AudioSettings.outputSampleRate,count=AudioRenderer.GetSampleCountForCaptureFrame();
            if(count<0 || count>rate*10+8192)throw new IOException("混音采样数量异常，已停止内录。");
            samples=new float[count*channels];float empty=0;
            fixed(float* pointer=samples)
                if(!AudioRenderer.Internal_AudioRenderer_Render(samples.Length==0?&empty:pointer,samples.Length))throw new IOException("Unity 混音采集失败。");
            nativeAudioSeconds+=(double)count/rate;
        }
        finally{Time.captureDeltaTime=priorCaptureDelta;}
        if(readback!=null)readback.SubmitAudio(samples);else encoder!.WriteAudio(samples);
        if(liveTimeline.ElapsedSeconds>=1 && Math.Abs(nativeAudioSeconds-liveTimeline.ElapsedSeconds)>.15)
            throw new IOException($"音频时钟偏差：音频 {nativeAudioSeconds:0.000} 秒 / 剧情 {liveTimeline.ElapsedSeconds:0.000} 秒，已停止以避免不同步。");
        int copies=checked((int)batch.FrameCount);
        if(copies>0)
        {
            double lastOutputTimestamp=(double)(batch.FirstFrameIndex+copies-1)/CaptureSamplingFps;
            liveMaximumSampleAge=Math.Max(liveMaximumSampleAge,lastOutputTimestamp-liveCompositeAt);
            if(readback!=null)readback.Submit(livePreviousTarget,Array.Empty<float>(),copies);
            else SubmitNativePixels(copies,livePreviousTarget);
            long previousOutputFrames=capturedFrames;
            capturedFrames=Encoder.OutputFrameCount(liveTimeline.OutputFrames,CaptureSamplingFps,RecordingFps);
            if(previousOutputFrames==0)Config.Log.LogInfo(FastSimulationHz>0
                ?$"First timestamped image submitted; accelerated {FastSimulationHz} Hz scenario clock; independent PCM; no duration preflight."
                :"First live timestamped image submitted; native Unity clock retained; no duration preflight.");
            if(UiDiagnostics.CaptureTest && capturedFrames>=60 && previousOutputFrames<60)UiDiagnostics.SaveFrame(Path.Combine(UiDiagnostics.Root,"progress-screen.ppm"));
            if(UiDiagnostics.StopTest && capturedFrames>=120 && previousOutputFrames<120)OnMainThread(()=>UiDiagnostics.Click("AARecorder_Stop"));
        }
        nativeSnapshotFrame=Time.frameCount;
    }

    [HideFromIl2Cpp]
    bool LiveRenderRequired()
    {
        if(player==null || !player.isBackgroundLatest)return true;
        var animations=player.currentAnims;
        if(animations!=null)for(int i=0;i<animations.Count;i++)
        {
            var title=animations[i]?.TryCast<ScenarioAnimation.TitleAnimation>();
            if(title!=null && !title.hasCompleted && !title.isCancelled)return true;
        }
        return false;
    }

    [HideFromIl2Cpp]
    void SwapLiveTarget()
    {
        if(livePreviousTarget==null)
        {
            livePreviousTarget=new RenderTexture(width,height,24,RenderTextureFormat.ARGB32);
            livePreviousTarget.hideFlags=HideFlags.HideAndDontSave;
            if(!livePreviousTarget.Create())throw new IOException("无法创建实时录制的上一帧快照。");
        }
        // Retain the latest completed composite on the GPU. Native source
        // logic/PCM can run between composites; only due CFR samples read back.
        var completed=renderTarget!;
        renderTarget=livePreviousTarget;livePreviousTarget=completed;
        foreach(var camera in recordingCameras)
            if(camera!=null && camera.targetTexture==completed)camera.targetTexture=renderTarget;
        cameraRenderedFrames.Clear();
    }

    [HideFromIl2Cpp]
    void ReleaseLiveTarget()
    {
        // Caller has already flushed/discarded readbacks and detached cameras.
        if(livePreviousTarget!=null && livePreviousTarget!=renderTarget)
        {livePreviousTarget.Release();Object.Destroy(livePreviousTarget);}
        livePreviousTarget=null;
    }
}
