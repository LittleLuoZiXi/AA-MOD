using System;
using AzureArchive.Recorder;

public static class NativeCaptureTimelineTests
{
    public static void Run()
    {
        int checks=0;
        void Check(bool ok,string message)
        {if(!ok)throw new Exception(message);checks++;}
        void Reject<T>(Action action,string message) where T:Exception
        {try{action();}catch(T){checks++;return;}throw new Exception(message);}
        bool Close(double left,double right,double tolerance=1e-9)=>Math.Abs(left-right)<=tolerance;

        var empty=new NativeCaptureTimeline();
        Reject<InvalidOperationException>(()=>empty.CreateReplay(),"Cannot replay unfinished preflight");
        Reject<InvalidOperationException>(()=>empty.Freeze(30),"Cannot freeze empty preflight");
        foreach(float invalid in new[]{0f,-.01f,float.NaN,float.PositiveInfinity,float.NegativeInfinity})
            Reject<ArgumentOutOfRangeException>(()=>empty.RecordStep(invalid),"Invalid delta is rejected");
        Check(empty.StepCount==0 && empty.Seconds==0,"Rejected deltas never mutate the measured timeline");
        empty.RecordStep(.25f);
        Reject<ArgumentOutOfRangeException>(()=>empty.Freeze(0),"Invalid output FPS is rejected");
        Check(!empty.IsFrozen && empty.StepCount==1,"Rejected freeze preserves observations");

        // PTS at the exclusive endpoint belongs to the next interval, avoiding
        // a duplicate boundary frame and preserving the current picture at t=0.
        var boundary=new NativeCaptureTimeline();
        for(int i=0;i<4;i++)boundary.RecordStep(.25f);
        boundary.Freeze(4);
        var edge=boundary.CreateReplay();
        for(int i=0;i<4;i++)
        {
            var before=edge.NextBatch;
            Check(before.FirstFrameIndex==i && before.FrameCount==1,"Exact endpoint emits one preceding image");
            Check(Close(before.StartSeconds,i*.25) && Close(before.EndSeconds,(i+1)*.25),"Interval timestamps are correct");
            Check(edge.NextFrameCount==1 && edge.CompletedSteps==i,"Peeking never consumes the simulation step");
            edge.CompleteStep(.25f);
        }
        Check(boundary.OutputFrames==4 && edge.EmittedFrames==4 && !edge.HasNextStep,"No frame is emitted at terminal t=1");
        Reject<InvalidOperationException>(()=>edge.CompleteStep(.25f),"Exhausted replay cannot extend its planned duration");
        Reject<InvalidOperationException>(()=>{_=edge.ExpectedDeltaTime;},"Cannot silently repeat the last simulation delta");

        // High native rate: most native frames need no GPU readback; a long
        // native stall repeats the preceding image without jumping ahead.
        var irregular=new NativeCaptureTimeline();
        foreach(float dt in new[]{.025f,.025f,.15f})irregular.RecordStep(dt);
        irregular.Freeze(24);
        var cursor=irregular.CreateReplay();
        long[] batches={1,1,3};
        for(int i=0;i<batches.Length;i++)
        {
            Check(cursor.NextFrameCount==batches[i],"Variable native steps yield the expected held-image count");
            cursor.CompleteStep(cursor.ExpectedDeltaTime);
        }
        Check(cursor.EmittedFrames==5 && Close(irregular.TerminalDeltaTime,.15f),"Final partial output frame is retained without changing the final native delta");

        // A one-frame phase error must fail before modifying progress, otherwise
        // measured deltas would be shifted silently for the entire recording.
        var phase=irregular.CreateReplay();
        Reject<InvalidOperationException>(()=>phase.CompleteStep(.15f),"A next-frame delta applied to the current frame is rejected");
        Check(phase.CompletedSteps==0 && phase.ElapsedSeconds==0 && phase.EmittedFrames==0,"Delta mismatch leaves replay unchanged");

        float[] pattern={.0013f,.0018f,.0022f,.0047f,.0063f,.0091f,.0161f,.028f};
        double referenceSeconds=0;
        long firstNativeSteps=0;
        foreach(int fps in new[]{24,25,30,50,60})
        {
            var plan=new NativeCaptureTimeline();
            for(int i=0;i<12000;i++)plan.RecordStep(pattern[i%pattern.Length]);
            plan.Freeze(fps);
            if(referenceSeconds==0){referenceSeconds=plan.Seconds;firstNativeSteps=plan.StepCount;}
            Check(plan.Seconds==referenceSeconds && plan.StepCount==firstNativeSteps,"Output FPS cannot change the measured native timeline");
            Check(plan.VideoSeconds>=plan.Seconds-1e-9 && plan.VideoSeconds-plan.Seconds<1d/fps,"Only final output-frame rounding affects video duration");
            var replay=plan.CreateReplay();
            long oracleFrames=0,zeroImageSteps=0;
            while(replay.HasNextStep)
            {
                var batch=replay.NextBatch;
                if(!batch.NeedsImage)zeroImageSteps++;
                for(long frame=batch.FirstFrameIndex;frame<batch.FirstFrameIndex+batch.FrameCount;frame++)
                {
                    double timestamp=(double)frame/fps;
                    Check(timestamp+1e-9>=batch.StartSeconds && timestamp<batch.EndSeconds+1e-9,"Every output PTS selects the most recent native image");
                    oracleFrames++;
                }
                replay.CompleteStep(replay.ExpectedDeltaTime);
            }
            Check(zeroImageSteps>0,"Intermediate native steps do not require a GPU readback");
            Check(oracleFrames==plan.OutputFrames && replay.EmittedFrames==plan.OutputFrames,"All output frames are allocated once, with no gaps");
            Check(replay.ElapsedSeconds==plan.Seconds,"Replay stops at the exact measured endpoint at every output FPS");
            Reject<InvalidOperationException>(()=>plan.RecordStep(.01f),"Frozen observations cannot change during recording");
            Reject<InvalidOperationException>(()=>plan.Freeze(fps),"Preflight cannot silently refreeze");
        }

        // Independent cursors and resets must not change a frozen replay in use.
        var preserved=irregular.CreateReplay();
        var independent=irregular.CreateReplay();
        independent.CompleteStep(independent.ExpectedDeltaTime);
        irregular.Reset();
        Check(!irregular.IsFrozen && irregular.StepCount==0 && irregular.Seconds==0,"Reset clears only the next measurement");
        Check(preserved.CompletedSteps==0 && independent.CompletedSteps==1,"Replay cursors own independent progress");
        while(preserved.HasNextStep)preserved.CompleteStep(preserved.ExpectedDeltaTime);
        Check(preserved.EmittedFrames==5,"Reset cannot mutate previously frozen replay arrays");

        foreach(double invalid in new[]{-.001,1.001,double.NaN,double.PositiveInfinity})
            Reject<ArgumentOutOfRangeException>(()=>boundary.CreateReplay(invalid),"Natural-ending allowance is explicitly bounded to one second");
        var early=boundary.CreateReplay(1);
        early.CompleteStep(.25f);
        Check(!early.IsMeasuredPlanComplete && Close(early.RemainingMeasuredSeconds,.75) && Close(early.DurationDifferenceSeconds,-.75),"Caller can accept an early natural ending without rewriting the plan");
        Check(boundary.OutputFrames==4 && early.OutputFrames==4,"Early ending never changes the expected frame count");

        float? commonTerminalStep=null;
        foreach(int fps in new[]{24,25,30,50,60})
        {
            var tailPlan=new NativeCaptureTimeline();
            for(int i=0;i<100;i++)tailPlan.RecordStep(.002f);
            tailPlan.RecordStep(.33333334f); // A terminal load frame is an outlier.
            tailPlan.Freeze(fps);
            long expectedPlanFrames=tailPlan.OutputFrames;
            double expectedPlanSeconds=tailPlan.Seconds;
            var tail=tailPlan.CreateReplay(1);
            for(int i=0;i<tail.TotalSteps;i++)tail.CompleteStep(tail.ExpectedDeltaTime);
            Check(tail.IsMeasuredPlanComplete && tail.HasNextStep && tail.TerminalSteps==0,"Measured completion leaves an optional bounded tail available");
            Check(tail.RemainingMeasuredSeconds==0 && tail.DurationDifferenceSeconds==0,"Measured endpoint remains separately observable");
            Check(tail.ExpectedDeltaTime==.002f,"Long loading frame does not determine the terminal cadence");
            if(commonTerminalStep==null)commonTerminalStep=tail.ExpectedDeltaTime;
            Check(commonTerminalStep==tail.ExpectedDeltaTime,"Terminal cadence is independent of output FPS");
            long nextFrame=tail.EmittedFrames;
            bool sawFinal=false;
            while(tail.HasNextStep)
            {
                var batch=tail.NextBatch;
                Check(batch.FirstFrameIndex==nextFrame,"Tail batches join the measured video without repeated indices");
                for(long frame=batch.FirstFrameIndex;frame<batch.FirstFrameIndex+batch.FrameCount;frame++)
                {
                    double pts=(double)frame/fps;
                    Check(pts+1e-9>=batch.StartSeconds && pts<batch.EndSeconds+1e-9,"Tail video keeps the same timestamp sampling rule");
                }
                nextFrame+=batch.FrameCount;sawFinal|=batch.IsFinal;
                tail.CompleteStep(tail.ExpectedDeltaTime);
                Check(tail.ElapsedSeconds<=tail.MaximumReplaySeconds+1e-12,"No tail step exceeds its maximum allowance");
                Check(tail.TerminalSteps<=10001,"Tail never needs unbounded repeated steps");
            }
            Check(sawFinal && tail.IsMeasuredPlanComplete && !tail.HasNextStep,"Tail exhaustion is explicit");
            Check(tail.DurationDifferenceSeconds<=1 && tail.DurationDifferenceSeconds>=1-1e-6,"Tail ends within the one-second upper bound");
            Check(tail.EmittedFrames==NativeCaptureTimeline.FramesBefore(tail.ElapsedSeconds,fps),"Actual tail frame count matches its actual endpoint");
            Check(tailPlan.OutputFrames==expectedPlanFrames && tail.OutputFrames==expectedPlanFrames && tailPlan.Seconds==expectedPlanSeconds,"Tail recording never edits the frozen estimate");
            Reject<InvalidOperationException>(()=>tail.CompleteStep(.002f),"Cannot extend beyond the permitted natural-ending interval");
        }
        var allStalls=new NativeCaptureTimeline();allStalls.RecordStep(.33333334f);allStalls.Freeze(30);
        var stallTail=allStalls.CreateReplay(.025);
        stallTail.CompleteStep(stallTail.ExpectedDeltaTime);
        Check(stallTail.ExpectedDeltaTime==.01f,"All-stall observations use a short FPS-independent fallback");
        while(stallTail.HasNextStep)stallTail.CompleteStep(stallTail.ExpectedDeltaTime);
        Check(stallTail.TerminalSteps==3 && stallTail.DurationDifferenceSeconds<=.025,"Final tail step is shortened to the configured remaining allowance");
        var tiny=new NativeCaptureTimeline();tiny.RecordStep(1e-20f);tiny.RecordStep(.25f);tiny.Freeze(30);
        var tinyTail=tiny.CreateReplay(.001);
        while(!tinyTail.IsMeasuredPlanComplete)tinyTail.CompleteStep(tinyTail.ExpectedDeltaTime);
        Check(tinyTail.ExpectedDeltaTime==.0001f,"Sub-resolution observed steps cannot cause unbounded terminal iterations");
        Console.WriteLine($"Native capture timeline: {checks} checks passed (24/25/30/50/60 FPS; variable steps, terminal rounding, phase errors, held-image sampling, bounded natural-ending allowance).");
    }
}
