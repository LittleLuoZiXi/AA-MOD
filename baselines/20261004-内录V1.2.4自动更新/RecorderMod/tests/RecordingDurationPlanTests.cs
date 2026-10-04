using System;
using AzureArchive.Recorder;

public static class RecordingDurationPlanTests
{
    public static void Run()
    {
        int checks=0;
        void Check(bool condition,string message){if(!condition)throw new Exception(message);checks++;Console.WriteLine("PASS: "+message);}
        void Reject<T>(Action action,string message) where T:Exception
        {try{action();}catch(T){Check(true,message);return;}throw new Exception(message);}
        var plan=new RecordingDurationPlan();
        Check(!plan.IsReady && plan.Frames==0 && plan.Seconds==0 && plan.Label=="预计总时长：计算中","Before preflight completes the plan explicitly remains calculating");
        Reject<ArgumentOutOfRangeException>(()=>plan.Freeze(0,24),"An empty preflight cannot become a completed plan");
        Reject<ArgumentOutOfRangeException>(()=>plan.Freeze(-1,24),"Negative frame counts are rejected");
        Reject<ArgumentOutOfRangeException>(()=>plan.Freeze(451,0),"Zero FPS is rejected");
        Reject<ArgumentOutOfRangeException>(()=>plan.Freeze(451,-24),"Negative FPS is rejected");
        Check(!plan.IsReady && plan.Frames==0 && plan.Seconds==0,"Invalid attempts leave the plan unchanged");
        plan.Freeze(451,24);
        Check(plan.IsReady && plan.Frames==451 && Math.Abs(plan.Seconds-451d/24)<.0000001,"Recorded preflight frames and FPS determine the frozen video duration");
        Check(plan.Label=="预计总时长 18.8 秒","The ready label reports the fixed preflight result without dynamic wording");
        var fixedLabel=plan.Label;var fixedSeconds=plan.Seconds;
        // Actual export may advance beyond the preflight count by a few frames.
        // No observation API exists: querying progress cannot replace the plan.
        foreach(long renderedFrames in new long[]{0,1,120,450,451,454,1000})
        {
            _=(double)renderedFrames/24;
            Check(plan.Frames==451 && plan.Seconds==fixedSeconds && plan.Label==fixedLabel,"Fixed duration survives export progress at frame "+renderedFrames);
        }
        Reject<InvalidOperationException>(()=>plan.Freeze(454,24),"A replay difference cannot silently revise the prediction");
        Reject<InvalidOperationException>(()=>plan.Freeze(451,24),"Even an identical second freeze is rejected");
        Check(plan.Frames==451 && plan.Label==fixedLabel,"Rejected refreezes preserve the original result");
        plan.Reset();
        Check(!plan.IsReady && plan.Frames==0 && plan.Seconds==0 && plan.Label=="预计总时长：计算中","Reset removes all prior-session timing state");
        plan.Freeze(1153,60);
        Check(plan.IsReady && plan.Frames==1153 && plan.Label=="预计总时长 19.2 秒","A new preflight can freeze a new frame rate and duration after reset");
        Console.WriteLine($"Recording duration plan: {checks} checks passed.");
    }
}