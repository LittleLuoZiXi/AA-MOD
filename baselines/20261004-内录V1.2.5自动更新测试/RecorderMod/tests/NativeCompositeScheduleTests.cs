using System;
using System.Collections.Generic;
using System.Linq;
using AzureArchive.Recorder;

public static class NativeCompositeScheduleTests
{
    public static void Run()
    {
        int checks=0;
        void Check(bool ok,string message){if(!ok)throw new Exception(message);checks++;}
        void Reject(Action action,string message)
        {try{action();}catch(ArgumentOutOfRangeException){checks++;return;}throw new Exception(message);}
        bool Close(double left,double right,double tolerance=1e-10)=>Math.Abs(left-right)<=tolerance;

        var initial=new NativeCompositeSchedule();
        Check(initial.ShouldRender(0,false),"First image is mandatory even without an external readiness flag");
        Check(initial.ShouldRender(0,true) && !initial.HasRendered,"Repeated first-image queries do not change state");
        initial.Rendered(0);
        Check(initial.HasRendered && initial.LastRenderedSeconds==0,"Only successful rendering establishes a snapshot");
        Check(Close(initial.NextDeadlineSeconds,1d/60),"Initial snapshot schedules the first absolute deadline");
        Check(!initial.ShouldRender(.001,false),"High-rate native source interval can skip compositing");
        Check(initial.ShouldRender(.001,true),"Native readiness bypasses an unexpired deadline");
        Check(Close(initial.NextDeadlineSeconds,1d/60),"Mandatory query alone does not acknowledge a successful render");
        initial.Rendered(.001);
        Check(Close(initial.NextDeadlineSeconds,1d/60),"Early mandatory rendering preserves the absolute grid");
        Check(initial.ShouldRender(.02,false),"Late frame observes an expired deadline");
        Check(initial.ShouldRender(.02,false) && Close(initial.NextDeadlineSeconds,1d/60),"Repeated due query leaves failed or deferred rendering due");
        initial.Rendered(.02);
        Check(Close(initial.NextDeadlineSeconds,2d/60),"Late rendering does not shift the deadline to render time plus an interval");
        Check(!initial.ShouldRender(.02,false),"A successful source snapshot is not rendered twice to catch up");

        // At 100 source Hz the absolute grid must retain 60 composites/second,
        // rather than degrading to 50 Hz through relative interval accumulation.
        foreach(int sourceHz in new[]{40,100,600,1000})
        {
            var schedule=new NativeCompositeSchedule();int renders=0,skips=0;
            for(int frame=0;frame<=sourceHz*3;frame++)
            {
                double now=(double)frame/sourceHz;
                bool due=schedule.ShouldRender(now,false);
                Check(due==schedule.ShouldRender(now,false),"Repeated schedule queries are stable");
                if(due){schedule.Rendered(now);renders++;}
                else skips++;
                Check(!schedule.ShouldRender(now,false),"No extra same-timestamp render is owed after a completed frame");
                Check(schedule.NextDeadlineSeconds>now,"Next absolute deadline is in the future");
            }
            Check(renders==Math.Min(sourceHz,60)*3+1,"Fixed 60 Hz grid neither changes native cadence nor catches up slow source frames");
            Check(sourceHz<=60?skips==0:skips>0,"Only high-rate source frames omit optional composites");
        }

        var stalled=new NativeCompositeSchedule();stalled.Rendered(0);
        Check(stalled.ShouldRender(2.503,false),"Long native frame makes one real render due");
        stalled.Rendered(2.503);
        Check(Close(stalled.NextDeadlineSeconds,151d/60),"Long stall jumps directly to its following grid deadline");
        for(int query=0;query<20;query++)
            Check(!stalled.ShouldRender(2.503,false),"Missed deadlines never accumulate a batch of catch-up renders");
        Check(stalled.ShouldRender(2.504,true),"Title/background render remains mandatory immediately after a long frame");
        stalled.Rendered(2.504);
        Check(Close(stalled.NextDeadlineSeconds,151d/60),"Forced title rendering does not restart the optional cadence");

        foreach(int boundary in new[]{1,2,7,19,59,60,61,1001,216000})
        {
            double edge=(double)boundary/60;
            var schedule=new NativeCompositeSchedule();schedule.Rendered((double)(boundary-1)/60);
            Check(!schedule.ShouldRender(edge-1e-7,false),"A real positive gap before a deadline cannot render early");
            Check(schedule.ShouldRender(Math.BitDecrement(edge),false),"Adjacent-double arithmetic noise does not miss a grid edge");
            Check(schedule.ShouldRender(edge,false) && schedule.ShouldRender(Math.BitIncrement(edge),false),"Exact and just-after deadline are due");
            schedule.Rendered(Math.BitDecrement(edge));
            Check(Close(schedule.NextDeadlineSeconds,(double)(boundary+1)/60),"Boundary acknowledgement advances exactly one absolute grid slot");
            Check(!schedule.ShouldRender(edge,false),"Floating boundary noise cannot cause duplicate composites");
        }

        // Use the real live timeline to check that gating never gates source
        // intervals, PCM opportunities or CFR timestamps. Each CFR sample still
        // sees a retained image from the past, including long native intervals.
        List<int>? referenceRenders=null;double referenceSeconds=0;
        foreach(int fps in new[]{24,25,30,50,60})
        {
            var live=new NativeLiveCaptureTimeline(fps);live.Begin();
            var schedule=new NativeCompositeSchedule();schedule.Rendered(0);
            var renderSteps=new List<int>();double retainedAt=0;int pcmSteps=0;
            float[] deltas={.0008f,.0012f,.004f,.002f,.0033f,.006f};
            for(int step=0;step<1800;step++)
            {
                float dt=step%211==210?.21f:deltas[step%deltas.Length];
                var batch=live.Advance(dt);pcmSteps++;
                for(long frame=batch.FirstFrameIndex;frame<batch.FirstFrameIndex+batch.FrameCount;frame++)
                {
                    double pts=(double)frame/fps;
                    Check(pts+1e-9>=retainedAt,"CFR samples never use a future scene image");
                    Check(pts-retainedAt<=NativeCompositeSchedule.IntervalSeconds+dt+1e-9,"Held image age is bounded by one composite interval plus the current native interval");
                }
                double now=live.ElapsedSeconds;
                bool titleActive=step>=200 && step<240;
                bool backgroundWaiting=step>=600 && step<608;
                bool mandatory=titleActive || backgroundWaiting;
                if(schedule.ShouldRender(now,mandatory))
                {schedule.Rendered(now);retainedAt=now;renderSteps.Add(step);}
                if(mandatory)Check(retainedAt==now,"Every title/background source frame receives a real composite");
                Check(live.SourceIntervals==pcmSteps,"Compositing skips never skip native time or PCM opportunities");
            }
            long beforeEnd=live.OutputFrames;double seconds=live.ElapsedSeconds;
            var tail=live.Finish();
            Check(tail.FrameCount==0 && live.OutputFrames==beforeEnd && live.ElapsedSeconds==seconds,"Natural end adds neither an invented interval nor a padding frame");
            if(referenceRenders==null){referenceRenders=renderSteps;referenceSeconds=seconds;}
            else Check(renderSteps.SequenceEqual(referenceRenders) && seconds==referenceSeconds,"Output FPS does not change render decisions or native duration");
        }

        var invalid=new NativeCompositeSchedule();invalid.Rendered(1);
        foreach(double value in new[]{-1d,double.NaN,double.PositiveInfinity,double.NegativeInfinity,double.MaxValue,.99d})
        {
            Reject(()=>invalid.ShouldRender(value,false),"Invalid or reversed native clock is rejected during queries");
            Reject(()=>invalid.ShouldRender(value,true),"Mandatory rendering cannot accept an invalid native clock");
            Reject(()=>invalid.Rendered(value),"Invalid render acknowledgement is rejected");
            Check(invalid.LastRenderedSeconds==1 && Close(invalid.NextDeadlineSeconds,61d/60),"Rejected inputs leave the successful snapshot unchanged");
        }
        Console.WriteLine($"Native composite schedule: {checks} checks passed (fixed 60 Hz, 24/25/30/50/60 FPS independent, mandatory native renders, long frames, query purity and floating boundaries).");
    }
}
