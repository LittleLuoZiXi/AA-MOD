using System;
using AzureArchive.Recorder;

public static class NativeLiveCaptureTimelineTests
{
    public static void Run()
    {
        int checks=0;
        void Check(bool ok,string message){if(!ok)throw new Exception(message);checks++;}
        void Reject<T>(Action action,string message) where T:Exception
        {try{action();}catch(T){checks++;return;}throw new Exception(message);}
        bool Close(double left,double right,double tolerance=1e-9)=>Math.Abs(left-right)<=tolerance;
        Reject<ArgumentOutOfRangeException>(()=>new NativeLiveCaptureTimeline(0),"Output FPS must be positive");
        var start=new NativeLiveCaptureTimeline(30);
        Reject<InvalidOperationException>(()=>start.Advance(.01f),"No delta is consumed before initial snapshot");
        Reject<InvalidOperationException>(()=>start.Finish(),"Cannot finish before initial snapshot");
        start.Begin();
        Check(start.OutputFrames==0 && start.ElapsedSeconds==0,"Initial snapshot does not count its preceding frame delta");
        Reject<InvalidOperationException>(()=>start.Begin(),"Initial origin cannot be silently reset");
        foreach(float invalid in new[]{-.01f,10.01f,float.NaN,float.PositiveInfinity})
            Reject<ArgumentOutOfRangeException>(()=>start.Advance(invalid),"Invalid real-time deltas are rejected");
        Check(start.SourceIntervals==0 && start.OutputFrames==0,"Invalid samples do not change progress");
        var initial=start.Advance(.001f);
        Check(initial.FirstFrameIndex==0 && initial.FrameCount==1 && initial.StartSeconds==0,"The first positive interval emits initial image at PTS zero");
        Check(start.Advance(.001f).FrameCount==0,"High-rate source frame without an output PTS needs no readback");
        Check(start.Advance(0).FrameCount==0 && start.SourceIntervals==2,"Zero elapsed time adds no frame and no source interval");
        long beforeFinish=start.OutputFrames;
        var finished=start.Finish();
        Check(finished.IsFinal && finished.FrameCount==0 && start.OutputFrames==beforeFinish,"Already-completed terminal interval is not counted twice");
        Reject<InvalidOperationException>(()=>start.Advance(.01f),"Cannot append frames after a natural end");
        Reject<InvalidOperationException>(()=>start.Finish(),"Cannot finish twice");

        var exact=new NativeLiveCaptureTimeline(4);exact.Begin();
        for(int i=0;i<3;i++)
        {
            var batch=exact.Advance(.25f);
            Check(batch.FirstFrameIndex==i && batch.FrameCount==1,"Exact PTS boundary belongs to next native interval");
        }
        var exactEnd=exact.Finish(.25f);
        Check(exactEnd.FirstFrameIndex==3 && exactEnd.FrameCount==1 && exactEnd.IsFinal,"End-before-EOF completes precisely its pending interval");
        Check(exact.OutputFrames==4 && exact.ElapsedSeconds==1 && exact.VideoSeconds==1,"No extra terminal video frame appears at an exact boundary");

        double sameSeconds=0;
        float[] pattern={.001f,.0016f,.0021f,.004f,.012f,.33333334f,.0012f};
        foreach(int fps in new[]{24,25,30,50,60})
        {
            var live=new NativeLiveCaptureTimeline(fps);live.Begin();
            long indices=0,zeroCopies=0,repeated=0;
            for(int step=0;step<2400;step++)
            {
                float dt=pattern[step%pattern.Length];
                var batch=step==2399?live.Finish(dt):live.Advance(dt);
                Check(batch.FirstFrameIndex==indices,"Direct output indices stay contiguous across uneven intervals");
                if(batch.FrameCount==0)zeroCopies++;
                if(batch.FrameCount>1)repeated++;
                for(long frame=batch.FirstFrameIndex;frame<batch.FirstFrameIndex+batch.FrameCount;frame++)
                {
                    double pts=(double)frame/fps;
                    Check(pts+1e-9>=batch.StartSeconds && pts<batch.EndSeconds+1e-9,"Every output PTS uses the preceding native snapshot");
                }
                indices+=batch.FrameCount;
            }
            if(sameSeconds==0)sameSeconds=live.ElapsedSeconds;
            Check(live.ElapsedSeconds==sameSeconds,"Output FPS cannot influence native elapsed time");
            Check(live.OutputFrames==indices && live.IsFinished && live.SourceIntervals==2400,"Natural end preserves all intervals and emitted frames");
            Check(zeroCopies>0 && repeated>0,"Fast and stalled native frames use selective readback and held images");
            Check(live.VideoSeconds>=live.ElapsedSeconds-1e-9 && live.VideoSeconds-live.ElapsedSeconds<1d/fps,"Only final output-frame rounding changes direct video duration");
        }

        // Unscaled native time can exceed the scaled Unity maximumDeltaTime.
        // Preserve that real interval instead of changing the story clock or
        // silently deleting its audio/video duration.
        var stalled=new NativeLiveCaptureTimeline(60);stalled.Begin();
        var longFrame=stalled.Finish(2.5f);
        Check(longFrame.FrameCount==150 && stalled.SourceIntervals==1,"A long native stall repeats the preceding image without dropping elapsed time");
        Check(Close(stalled.ElapsedSeconds,2.5) && stalled.VideoSeconds==2.5,"Long elapsed intervals do not depend on capture FPS");
        var maximum=new NativeLiveCaptureTimeline(24);maximum.Begin();
        Check(maximum.Advance(10).FrameCount==240 && maximum.ElapsedSeconds==10,"Ten-second stall is preserved at the explicit safety boundary");
        Reject<ArgumentOutOfRangeException>(()=>maximum.Advance(11),"An excessive native stall fails instead of being clipped");
        Check(maximum.OutputFrames==240 && maximum.ElapsedSeconds==10 && maximum.SourceIntervals==1,"Excessive stall rejection preserves prior output state");
        Console.WriteLine($"Native live capture timeline: {checks} checks passed (24/25/30/50/60 FPS; first/last frame, exact boundaries, native jitter and long stalls).");
    }
}
