using System;
using System.Collections.Generic;

namespace AzureArchive.Recorder;

// Pure timing arithmetic: Unity owns the native player loop and the caller owns
// images/audio. The first rendered state is t=0; record only intervals AFTER it.
internal sealed class NativeCaptureTimeline
{
    readonly List<float> deltas=new();
    readonly List<double> ends=new();
    float[] frozenDeltas=Array.Empty<float>();
    double[] frozenEnds=Array.Empty<double>();
    double seconds,correction;
    internal bool IsFrozen { get; private set; }
    internal int StepCount=>deltas.Count;
    internal double Seconds=>seconds;
    // Includes the final interval ending in Test.End. If End runs before EOF,
    // the caller must record that frame once before freezing the plan.
    internal float TerminalDeltaTime=>deltas.Count==0?0:deltas[deltas.Count-1];
    internal int OutputFps { get; private set; }
    internal long OutputFrames { get; private set; }
    internal double VideoSeconds=>IsFrozen?(double)OutputFrames/OutputFps:0;

    internal void RecordStep(float deltaTime)
    {
        if(IsFrozen)throw new InvalidOperationException("The native playback timeline is already frozen.");
        ValidateDelta(deltaTime);
        // Compensated accumulation preserves sub-millisecond steps in long runs.
        double adjusted=(double)deltaTime-correction;
        double next=seconds+adjusted;
        if(!double.IsFinite(next) || next<=seconds)throw new ArgumentOutOfRangeException(nameof(deltaTime));
        correction=(next-seconds)-adjusted;
        seconds=next;deltas.Add(deltaTime);ends.Add(seconds);
    }

    internal void Freeze(int outputFps)
    {
        if(IsFrozen)throw new InvalidOperationException("The native playback timeline is already frozen.");
        if(outputFps<=0)throw new ArgumentOutOfRangeException(nameof(outputFps));
        if(deltas.Count==0)throw new InvalidOperationException("Native playback has no completed simulation intervals.");
        long frames=FramesBefore(seconds,outputFps);
        if(frames<=0)throw new InvalidOperationException("Native playback has no video duration.");
        frozenDeltas=deltas.ToArray();frozenEnds=ends.ToArray();
        OutputFps=outputFps;OutputFrames=frames;IsFrozen=true;
    }

    internal NativeCaptureReplay CreateReplay(double maxTerminalDriftSeconds=0)
    {
        if(!IsFrozen)throw new InvalidOperationException("Complete the native preflight before replaying it.");
        if(!double.IsFinite(maxTerminalDriftSeconds) || maxTerminalDriftSeconds<0 || maxTerminalDriftSeconds>1)
            throw new ArgumentOutOfRangeException(nameof(maxTerminalDriftSeconds),"The natural-ending allowance must be between zero and one second.");
        // Arrays are immutable after publication; Reset never edits a live replay.
        return new NativeCaptureReplay(frozenDeltas,frozenEnds,OutputFps,OutputFrames,maxTerminalDriftSeconds);
    }

    internal void Reset()
    {
        deltas.Clear();ends.Clear();frozenDeltas=Array.Empty<float>();frozenEnds=Array.Empty<double>();
        seconds=0;correction=0;OutputFps=0;OutputFrames=0;IsFrozen=false;
    }

    internal static void ValidateDelta(float deltaTime)
    {
        if(!float.IsFinite(deltaTime) || deltaTime<=0)throw new ArgumentOutOfRangeException(nameof(deltaTime),"A native simulation interval must be finite and positive.");
    }

    // Output PTS values are n/fps, starting at zero, strictly before the ending
    // timestamp. Snap only negligible double arithmetic noise at exact boundaries.
    internal static long FramesBefore(double exclusiveEnd,int fps)
    {
        double value=exclusiveEnd*fps;
        if(!double.IsFinite(value) || value<0 || value>=long.MaxValue)throw new ArgumentOutOfRangeException(nameof(exclusiveEnd));
        double nearest=Math.Round(value);
        if(Math.Abs(value-nearest)<=1e-9)value=nearest;
        return checked((long)Math.Ceiling(value));
    }
}

internal readonly struct NativeCaptureBatch
{
    internal NativeCaptureBatch(long firstFrameIndex,long frameCount,double startSeconds,double endSeconds,bool isFinal)
    {FirstFrameIndex=firstFrameIndex;FrameCount=frameCount;StartSeconds=startSeconds;EndSeconds=endSeconds;IsFinal=isFinal;}
    internal long FirstFrameIndex { get; }
    internal long FrameCount { get; }
    internal double StartSeconds { get; }
    internal double EndSeconds { get; }
    internal bool IsFinal { get; }
    internal bool NeedsImage=>FrameCount>0;
}

internal sealed class NativeCaptureReplay
{
    readonly float[] deltas;
    readonly double[] ends;
    readonly int fps;
    readonly float terminalStep;
    readonly double maxTerminalDriftSeconds;
    double terminalElapsedSeconds;
    // Do not request a final sub-microsecond Unity frame merely to consume a
    // float conversion remainder. The allowance is a maximum, never a target.
    const double EndpointTolerance=1e-6;
    internal NativeCaptureReplay(float[] deltas,double[] ends,int fps,long outputFrames,double maxTerminalDriftSeconds)
    {
        this.deltas=deltas;this.ends=ends;this.fps=fps;OutputFrames=outputFrames;
        this.maxTerminalDriftSeconds=maxTerminalDriftSeconds;
        terminalStep=TypicalTerminalStep(deltas);
    }
    internal int CompletedSteps { get; private set; }
    // TotalSteps and OutputFrames describe the immutable measured plan. Completed
    // steps/frames may exceed them only within the explicit ending allowance.
    internal int TotalSteps=>deltas.Length;
    internal int TerminalSteps=>Math.Max(0,CompletedSteps-deltas.Length);
    internal long EmittedFrames { get; private set; }
    internal long OutputFrames { get; }
    internal double ElapsedSeconds=>IsMeasuredPlanComplete?TotalSeconds+terminalElapsedSeconds:CompletedSteps==0?0:ends[CompletedSteps-1];
    internal double TotalSeconds=>ends[ends.Length-1];
    internal bool IsMeasuredPlanComplete=>CompletedSteps>=deltas.Length;
    internal double RemainingMeasuredSeconds=>Math.Max(0,TotalSeconds-ElapsedSeconds);
    internal double DurationDifferenceSeconds=>ElapsedSeconds-TotalSeconds;
    internal double MaximumReplaySeconds=>TotalSeconds+maxTerminalDriftSeconds;
    internal bool HasNextStep=>!IsMeasuredPlanComplete || maxTerminalDriftSeconds-terminalElapsedSeconds>EndpointTolerance;
    internal long NextFrameCount=>NextBatch.FrameCount;
    internal float ExpectedDeltaTime
    {
        get
        {
            RequireNext();
            if(!IsMeasuredPlanComplete)return deltas[CompletedSteps];
            double remaining=maxTerminalDriftSeconds-terminalElapsedSeconds;
            float next=(float)Math.Min(terminalStep,remaining);
            // Never let conversion back to Unity's float delta overshoot the
            // allowance. No unbounded repetition of a terminal/loading frame.
            if(next>remaining)next=MathF.BitDecrement(next);
            return next;
        }
    }

    // At t_i, inspect this BEFORE requesting a GPU readback of the t_i image.
    // Only intervals containing an output PTS need pixels. Native render events
    // may still be required on every engine frame to release AA's title/BG waits.
    internal NativeCaptureBatch NextBatch
    {
        get
        {
            RequireNext();
            double end=IsMeasuredPlanComplete?TotalSeconds+terminalElapsedSeconds+ExpectedDeltaTime:ends[CompletedSteps];
            long count=NativeCaptureTimeline.FramesBefore(end,fps)-EmittedFrames;
            bool final=IsMeasuredPlanComplete?MaximumReplaySeconds-end<=EndpointTolerance:
                CompletedSteps==deltas.Length-1 && maxTerminalDriftSeconds<=EndpointTolerance;
            return new NativeCaptureBatch(EmittedFrames,count,ElapsedSeconds,end,final);
        }
    }

    // Call after Unity completes the interval, then apply ExpectedDeltaTime to
    // captureDeltaTime for the NEXT engine frame. Do not consume the plan before
    // rendering that interval's audio. Its video uses the cached t_i image.
    internal NativeCaptureBatch CompleteStep(float actualDeltaTime)
    {
        RequireNext();NativeCaptureTimeline.ValidateDelta(actualDeltaTime);
        float expected=ExpectedDeltaTime;
        double tolerance=Math.Max(1e-6,(double)expected*1e-4);
        if(Math.Abs((double)actualDeltaTime-expected)>tolerance)
            throw new InvalidOperationException($"Native replay step {CompletedSteps} used {actualDeltaTime:R}s; expected {expected:R}s. Apply the saved delta before the next frame starts.");
        var batch=NextBatch;
        if(IsMeasuredPlanComplete)terminalElapsedSeconds+=expected;
        CompletedSteps++;EmittedFrames+=batch.FrameCount;
        return batch;
    }

    static float TypicalTerminalStep(float[] deltas)
    {
        // A single long asset-load frame at the end must not become the tail
        // cadence. A lower median of the last 256 observations rejects it and
        // is independent of the chosen video FPS. If every observed frame in a
        // very short scenario is a stall, use a small, FPS-independent fallback.
        int count=Math.Min(256,deltas.Length);
        var recent=new float[count];Array.Copy(deltas,deltas.Length-count,recent,0,count);Array.Sort(recent);
        float typical=recent[(count-1)/2];
        return typical<=.1f?Math.Max(.0001f,typical):.01f;
    }

    void RequireNext()
    {
        if(!HasNextStep)throw new InvalidOperationException("Native playback exhausted its measured timeline and permitted natural-ending interval; do not extend or repeat simulation steps.");
    }
}
