using System;

namespace AzureArchive.Recorder;

// Direct recording observes every native interval; it never supplies a simulation
// delta to Unity. Native logic and PCM continue each frame. The caller retains
// the latest completed GPU composite, with an independent fixed 60 Hz optional
// schedule and mandatory first/background/title renders, then reads that held
// image only for due output timestamps. It need not create a new image each step.
internal sealed class NativeLiveCaptureTimeline
{
    readonly int outputFps;
    double seconds,correction;
    internal NativeLiveCaptureTimeline(int outputFps)
    {
        if(outputFps<=0)throw new ArgumentOutOfRangeException(nameof(outputFps));
        this.outputFps=outputFps;
    }

    internal bool IsStarted { get; private set; }
    internal bool IsFinished { get; private set; }
    internal int OutputFps=>outputFps;
    internal long SourceIntervals { get; private set; }
    internal double ElapsedSeconds=>seconds;
    internal long OutputFrames { get; private set; }
    internal double VideoSeconds=>(double)OutputFrames/outputFps;
    internal double NextOutputTimestamp=>(double)OutputFrames/outputFps;

    // Called once, AFTER the first complete native image has been retained.
    // The frame containing PlayerStarting is not an elapsed story interval.
    internal void Begin()
    {
        if(IsStarted)throw new InvalidOperationException("The initial native snapshot was already established.");
        IsStarted=true;
    }

    // The delta describes the interval that has JUST elapsed. Returned CFR
    // frames use the latest completed image whose time is at or before
    // StartSeconds, never a future image rendered at EndSeconds. Multiple output
    // timestamps may reuse it, and long native stalls increase its sample age.
    // Zero copies means no GPU readback; the separate composite schedule decides
    // whether the caller renders and retains a new image after this interval.
    internal NativeCaptureBatch Advance(float unscaledDeltaTime)=>AdvanceCore(unscaledDeltaTime,false);

    // If the terminal native frame was already observed by Advance, pass zero.
    // Otherwise pass its delta exactly once. Never append an extra terminal PTS
    // or pad the story to an estimate: the interval is half-open [start,end).
    internal NativeCaptureBatch Finish(float terminalDeltaTime=0)=>AdvanceCore(terminalDeltaTime,true);

    NativeCaptureBatch AdvanceCore(float deltaTime,bool final)
    {
        if(!IsStarted)throw new InvalidOperationException("Retain the initial native image before advancing live recording.");
        if(IsFinished)throw new InvalidOperationException("Live recording already reached its natural endpoint.");
        if(!float.IsFinite(deltaTime) || deltaTime<0 || deltaTime>10)
            throw new ArgumentOutOfRangeException(nameof(deltaTime),"A native elapsed interval must be finite and between zero and ten seconds; longer stalls cannot be silently discarded.");
        double start=seconds;
        double adjusted=(double)deltaTime-correction;
        double end=deltaTime==0?seconds:seconds+adjusted;
        if(!double.IsFinite(end) || (deltaTime>0 && end<=seconds))throw new ArgumentOutOfRangeException(nameof(deltaTime));
        long frames=NativeCaptureTimeline.FramesBefore(end,outputFps);
        var batch=new NativeCaptureBatch(OutputFrames,frames-OutputFrames,start,end,final);
        // Validate the entire step before publishing state to the recorder.
        if(deltaTime>0)
        {
            correction=(end-seconds)-adjusted;
            seconds=end;SourceIntervals++;
        }
        OutputFrames=frames;IsFinished=final;
        return batch;
    }
}
