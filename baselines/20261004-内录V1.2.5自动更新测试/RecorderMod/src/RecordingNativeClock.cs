using System;
using System.IO;
using Il2CppInterop.Runtime.Attributes;
using UnityEngine;

namespace AzureArchive.Recorder;

public sealed partial class RecorderBehaviour
{
    readonly NativeCaptureTimeline nativeTimeline=new();
    NativeCaptureReplay? nativeReplay;
    NativeLiveCaptureTimeline? liveTimeline;
    NativeCompositeSchedule? liveCompositeSchedule;
    long liveComposites,liveSkippedComposites;
    double liveCompositeAt,liveMaximumSampleAge,liveMaximumSourceStep;
    long liveSourceStepsOver100ms,liveSourceStepsOver1Second,liveClampedSourceSteps;
    double liveScaledSeconds,liveClockClampSeconds;
    RenderTexture? livePreviousTarget;
    bool nativeSnapshotReady,listenerHeld;
    int nativeSnapshotFrame;
    float oldListenerVolume;
    double nativeAudioSeconds;
    internal double NativeElapsedSeconds { [HideFromIl2Cpp] get=>preflighting?nativeTimeline.Seconds:measureTotalRequested?nativeReplay?.ElapsedSeconds??0:liveTimeline?.ElapsedSeconds??0; }

    [HideFromIl2Cpp]
    void PrepareNativePass()
    {
        nativeSnapshotReady=false;nativeSnapshotFrame=-1;nativeAudioSeconds=0;
        if(preflighting)
        {
            nativeReplay=null;
            oldListenerVolume=AudioListener.volume;listenerHeld=true;AudioListener.volume=0;
        }
        else
        {
            nativeReplay=measureTotalRequested?nativeTimeline.CreateReplay(1.0):null;
            liveTimeline=measureTotalRequested?null:new NativeLiveCaptureTimeline(CaptureSamplingFps);
            liveCompositeSchedule=measureTotalRequested?null:new NativeCompositeSchedule();
            liveComposites=0;liveSkippedComposites=0;liveCompositeAt=0;liveMaximumSampleAge=0;
            liveMaximumSourceStep=0;liveSourceStepsOver100ms=0;liveSourceStepsOver1Second=0;liveClampedSourceSteps=0;liveScaledSeconds=0;liveClockClampSeconds=0;
            if(!AudioRenderer.Start())throw new InvalidOperationException("Unity AudioRenderer 启动失败，已取消录制。");
            audioStarted=true;audioStartDsp=AudioSettings.dspTime;
        }
    }

    [HideFromIl2Cpp]
    void RestoreNativePass()
    {
        if(listenerHeld){AudioListener.volume=oldListenerVolume;listenerHeld=false;}
        nativeSnapshotReady=false;
    }

    [HideFromIl2Cpp]
    unsafe void CompleteNativeStep()
    {
        if(!nativeSnapshotReady || nativeSnapshotFrame==Time.frameCount)return;
        if(!measureTotalRequested){CompleteLiveStep();return;}
        float delta=Time.deltaTime;
        if(preflighting)
        {
            nativeTimeline.RecordStep(delta);
            capturedFrames=NativeCaptureTimeline.FramesBefore(nativeTimeline.Seconds,RecordingFps);
        }
        else
        {
            if(nativeReplay==null || !nativeReplay.HasNextStep)
                throw new InvalidOperationException("剧情回放超出预演时间和允许的结束误差，已停止导出。请重试内录。");
            // AudioRenderer must run once per source interval, including zero DSP
            // blocks. Never attach this PCM to repeated output video frames.
            int count=AudioRenderer.GetSampleCountForCaptureFrame(),rate=AudioSettings.outputSampleRate;
            if(count<0 || count>rate)throw new IOException("离屏混音采样数量异常，已停止内录。");
            var samples=new float[count*channels];float empty=0;
            fixed(float* pointer=samples)
                if(!AudioRenderer.Internal_AudioRenderer_Render(samples.Length==0?&empty:pointer,samples.Length))throw new IOException("Unity 混音采集失败。");
            nativeAudioSeconds+=(double)count/rate;
            nativeReplay.CompleteStep(delta);
            if(readback!=null)readback.SubmitAudio(samples);else encoder!.WriteAudio(samples);
            if(nativeReplay.ElapsedSeconds>=1 && Math.Abs(nativeAudioSeconds-nativeReplay.ElapsedSeconds)>.15)
                throw new IOException($"离屏音频时钟偏差：音频 {nativeAudioSeconds:0.000} 秒 / 剧情 {nativeReplay.ElapsedSeconds:0.000} 秒，已停止以避免不同步。");
        }
        nativeSnapshotFrame=Time.frameCount;
    }

    [HideFromIl2Cpp]
    void CaptureNativeFrame()
    {
        if(Screen.width/2*2!=width || Screen.height/2*2!=height)throw new InvalidOperationException("录制时显示模式发生变化，已中止并保留临时数据。请保持录制显示模式固定。");
        CompleteNativeStep();
        PrepareControlVisibility();
        CheckPreflightChoice();
        // Native logic and PCM run every source frame. Repeated offscreen
        // compositing need not run faster than the maximum supported video FPS.
        // Never defer a real render needed by AA background/title readiness.
        if(!measureTotalRequested && nativeSnapshotReady &&
            !liveCompositeSchedule!.ShouldRender(NativeElapsedSeconds,LiveRenderRequired()))
        {liveSkippedComposites++;return;}
        int generation=passGeneration;
        if(!RenderCaptureCameras() || !Capturing || generation!=passGeneration)return;
        if(!nativeSnapshotReady)
        {
            nativeSnapshotReady=true;nativeSnapshotFrame=Time.frameCount;
            // PlayerStarting occurs in an already-running native frame. This
            // first complete image defines t=0; the first saved interval follows.
            playbackClockOrigin=RealTime.time;unityClockOrigin=Time.time;
            if(!preflighting)audioStartDsp=AudioSettings.dspTime;
            if(!measureTotalRequested)liveTimeline!.Begin();
        }
        CheckPreflightChoice();
        if(preflighting)return;
        if(!measureTotalRequested)
        {
            SwapLiveTarget();liveCompositeAt=NativeElapsedSeconds;
            liveCompositeSchedule!.Rendered(liveCompositeAt);liveComposites++;if(FastSimulationHz>0)Time.captureDeltaTime=1f/FastSimulationHz;return;
        }
        if(nativeReplay==null || !nativeReplay.HasNextStep)
            throw new InvalidOperationException("剧情超过预演时长一秒仍未自然结束，已停止导出。请重试内录。");
        int copies=checked((int)nativeReplay.NextFrameCount);
        if(copies>0)
        {
            if(readback!=null)readback.Submit(renderTarget!,Array.Empty<float>(),copies);
            else SubmitNativePixels(copies);
            capturedFrames+=copies;
            if(capturedFrames==copies)Config.Log.LogInfo($"First timestamped image submitted; {copies} output frames; {nativeTimeline.StepCount} native steps; async={readback!=null}.");
            if(UiDiagnostics.CaptureTest && capturedFrames>=60 && capturedFrames-copies<60)UiDiagnostics.SaveFrame(Path.Combine(UiDiagnostics.Root,"progress-screen.ppm"));
            if(UiDiagnostics.StopTest && capturedFrames>=120 && capturedFrames-copies<120)OnMainThread(()=>UiDiagnostics.Click("AARecorder_Stop"));
        }
        // Apply only after this frame's mixer/render work; this value belongs to
        // the NEXT Unity interval, independently of the output frame rate.
        Time.captureDeltaTime=nativeReplay.ExpectedDeltaTime;
    }

    [HideFromIl2Cpp]
    void SubmitNativePixels(int copies,RenderTexture? target=null)
    {
        var previous=RenderTexture.active;
        try{RenderTexture.active=target??renderTarget;texture!.ReadPixels(new Rect(0,0,width,height),0,0,false);}
        finally{RenderTexture.active=previous;}
        if(texture!.GetRawImageDataSize()!=(ulong)(width*height*3))throw new IOException("离屏纹理格式不符。");
        var raw=texture.GetRawTextureData();
        var rgb=System.Buffers.ArrayPool<byte>.Shared.Rent(raw.Length);
        System.Runtime.InteropServices.Marshal.Copy(raw.Pointer+4*IntPtr.Size,rgb,0,raw.Length);
        encoder!.WritePooledFrame(rgb,Array.Empty<float>(),copies);
    }
}