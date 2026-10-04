using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AzureArchive.Recorder;

// Production FIFO only: no encoder process, Unity, GPU, hardware discovery or DLSS.
public static class EncoderWorkQueueTests
{
    const long BytesPerSecond=48000L*2*sizeof(float),Budget=12*BytesPerSecond;
    static int checks;
    static readonly List<object> cases=new();
    static void Check(bool value,string description)
    {if(!value)throw new Exception(description);checks++;Console.WriteLine("PASS "+checks+": "+description);}
    sealed class Fixture:IDisposable
    {
        internal readonly EncoderWorkQueue<string> Queue=new(3,Budget);
        readonly List<EncoderWorkQueue<string>.WorkItem> taken=new();
        readonly List<Task<Exception?>> producers=new();
        internal EncoderWorkQueue<string>.WorkItem Take()
        {
            if(Queue.Snapshot().QueuedItems==0)throw new Exception("Test attempted to take an absent item");
            if(!Queue.TryTake(out var item))throw new Exception("FIFO ended before an accepted item");
            taken.Add(item);return item;
        }
        internal Task<Exception?> AddBlocked(string value,bool image,long bytes)
        {
            using var entered=new ManualResetEventSlim();
            var task=Task.Run<Exception?>(()=>{entered.Set();try{Queue.Add(value,image,bytes,TimeSpan.FromSeconds(10));return null;}catch(Exception error){return error;}});
            producers.Add(task);
            if(!entered.Wait(5000))throw new TimeoutException("Producer was not scheduled");
            return task;
        }
        public void Dispose()
        {
            Queue.Abort();
            foreach(var item in taken)Queue.Complete(item);
            while(Queue.TryDiscard(out _)){}
            foreach(var producer in producers)if(!producer.Wait(5000))throw new TimeoutException("Producer survived test cleanup");
        }
    }
    static Exception? Finish(Task<Exception?> task)
    {if(!task.Wait(5000))throw new TimeoutException("Blocked producer did not wake");return task.Result;}
    static void Blocked(Task task,string message)=>Check(!task.Wait(100),message);
    static void Empty(EncoderQueueStatistics s,string message)=>Check(s.QueuedImages==0 && s.BufferedPcmBytes==0 && s.QueuedItems==0 && s.ActiveItems==0,message);
    static void Stats(EncoderQueueStatistics s,bool image,bool pcm)
    {
        Check(s.EnqueueWaitCount>0 && s.EnqueueWaitSeconds>0 && s.MaxEnqueueWaitSeconds>0 && s.MaxEnqueueWaitSeconds<=s.EnqueueWaitSeconds,
            "Contention records enqueue call count, total time and maximum time");
        Check((image?s.ImageWaitCount>0:s.ImageWaitCount==0) && (pcm?s.PcmWaitCount>0:s.PcmWaitCount==0),
            "Wait reasons distinguish image quota from PCM quota");
        Check((!image || s.ImageWaitSeconds>0 && s.MaxImageWaitSeconds>0 && s.MaxImageWaitSeconds<=s.ImageWaitSeconds) &&
            (!pcm || s.PcmWaitSeconds>0 && s.MaxPcmWaitSeconds>0 && s.MaxPcmWaitSeconds<=s.PcmWaitSeconds),
            "Each contended quota records total and longest wait");
        Check(s.ImageCapacity==3 && s.PcmByteCapacity==Budget && s.PeakQueuedImages<=3 && s.PeakBufferedPcmBytes<=Budget,
            "Reported peaks respect three waiting images and twelve seconds of stereo PCM");
    }
    public static void Run(string root,string sourceHash)
    {
        if(Directory.Exists(root))throw new IOException("Refusing to reuse output: "+root);
        Directory.CreateDirectory(root);checks=0;cases.Clear();string? failure=null;
        try{ImageCapacityAndFifo();PcmBudgetIncludesActive();CompleteWakesAndDrains();FaultReleases();AbortReleases();TimeoutAndOversize();ConcurrentDiscard();BlockedReaderEnds();}
        catch(Exception error){failure=error.ToString();throw;}
        finally{File.WriteAllText(Path.Combine(root,"results.json"),JsonSerializer.Serialize(new{
            passed=failure==null,checks,queueSourceSha256=sourceHash,cpuOnly=true,ffmpegStarted=false,aaStarted=false,dlssUsed=false,
            imageQuota="Three waiting images; the single writer may own one additional active image",pcmQuota="Twelve seconds; queued plus writer-active PCM",
            cases=cases.ToArray(),failure},new JsonSerializerOptions{WriteIndented=true}));}
    }
    static void ImageCapacityAndFifo()
    {
        using var f=new Fixture();var q=f.Queue;var order=new List<string>();
        for(int i=0;i<3;i++)q.Add("pcm"+i,false,BytesPerSecond/10,TimeSpan.FromSeconds(1));
        for(int i=0;i<3;i++)q.Add("image"+i,true,0,TimeSpan.FromSeconds(1));
        var initial=q.Snapshot();
        Check(initial.QueuedItems==6 && initial.QueuedImages==3 && initial.EnqueueWaitCount==0,
            "Three nonempty PCM items occupy no image slots even with a stopped consumer");
        var fourth=f.AddBlocked("image3",true,0);Blocked(fourth,"A fourth waiting image blocks at the three-image limit");
        for(int i=0;i<3;i++){var item=f.Take();order.Add(item.Value);q.Complete(item);}
        Blocked(fourth,"Consuming audio-only items does not incorrectly release an image slot");
        var active=f.Take();order.Add(active.Value);
        Check(Finish(fourth)==null,"Taking the oldest image releases exactly one waiting-image slot");
        var holding=q.Snapshot();
        Check(holding.ActiveItems==1 && holding.QueuedImages==3 && holding.QueuedItems==3,
            "Capacity permits one writer-active image plus three waiting images, explicitly excluding active from image quota");
        var fifth=f.AddBlocked("image4",true,0);Blocked(fifth,"Another image still blocks with one active and three waiting");
        q.Complete(active);Blocked(fifth,"Completing the active image alone does not enlarge pending-image capacity");
        var next=f.Take();order.Add(next.Value);Check(Finish(fifth)==null,"Taking the next queued image wakes its producer");q.Complete(next);
        q.CompleteAdding();
        while(q.Snapshot().QueuedItems>0){var item=f.Take();order.Add(item.Value);q.Complete(item);}
        Check(!q.TryTake(out _),"A completed and drained queue ends the consumer");
        Check(order.SequenceEqual(new[]{"pcm0","pcm1","pcm2","image0","image1","image2","image3","image4"}),
            "PCM and images retain one FIFO through slow consumption and quota waits");
        var stats=q.Snapshot();Empty(stats,"Successful drain releases every image and PCM reservation");Stats(stats,true,false);
        Check(stats.EnqueueWaitCount==2 && stats.ImageWaitCount==2,"Wait counts represent the two blocked Add calls, not polling iterations");
        cases.Add(new{name="image-capacity-and-fifo",passed=true,stats});
    }
    static void PcmBudgetIncludesActive()
    {
        using var f=new Fixture();var q=f.Queue;
        q.Add("ten-seconds",false,10*BytesPerSecond);q.Add("two-seconds",false,2*BytesPerSecond);
        var active=f.Take();
        Check(active.Value=="ten-seconds" && q.Snapshot().BufferedPcmBytes==Budget,"A valid ten-second PCM block fits and remains budgeted while writer-active");
        var extra=f.AddBlocked("tail",false,BytesPerSecond);Blocked(extra,"PCM at twelve seconds blocks additional audio until active data is completed");
        // Image-only output remains admissible even with the full audio budget.
        q.Add("image",true,0,TimeSpan.FromSeconds(1));
        Check(q.Snapshot().QueuedImages==1,"A full PCM budget does not block an image carrying no PCM");
        q.Complete(active);Check(Finish(extra)==null,"Completing active PCM wakes a producer waiting for byte capacity");
        long before=q.Snapshot().BufferedPcmBytes;q.Complete(active);
        Check(q.Snapshot().BufferedPcmBytes==before && before==3*BytesPerSecond,"Repeated completion is idempotent and cannot over-release PCM quota");
        var order=new List<string>();q.CompleteAdding();
        while(q.Snapshot().QueuedItems>0){var item=f.Take();order.Add(item.Value);q.Complete(item);}
        Check(order.SequenceEqual(new[]{"two-seconds","image","tail"}),"Accepted PCM tail stays behind the image admitted while its producer was blocked");
        var stats=q.Snapshot();Empty(stats,"PCM drain clears active and queued bytes");Stats(stats,false,true);
        Check(stats.PeakBufferedPcmBytes==Budget && stats.PcmWaitCount==1,"PCM statistics capture the actual twelve-second high-water mark and one blocked call");
        cases.Add(new{name="active-pcm-budget",passed=true,stats});
    }
    static void CompleteWakesAndDrains()
    {
        using var f=new Fixture();var q=f.Queue;q.Add("accepted",false,Budget);
        var producer=f.AddBlocked("rejected",false,4);Blocked(producer,"Full audio budget blocks before normal close");
        q.CompleteAdding();
        Check(Finish(producer) is InvalidOperationException,"CompleteAdding wakes a blocked PCM producer with a closed-queue error");
        var item=f.Take();Check(item.Value=="accepted","Normal close preserves already accepted work");q.Complete(item);
        Check(!q.TryTake(out _),"Normal close ends only after accepted PCM drains");
        var stats=q.Snapshot();Empty(stats,"Normal close/drain releases quota");Stats(stats,false,true);
        cases.Add(new{name="complete-wakes-and-drains",passed=true,stats});
    }
    static void FaultReleases()
    {
        using var f=new Fixture();var q=f.Queue;q.Add("active",false,Budget/2);var active=f.Take();q.Add("queued",true,Budget/2);
        var producer=f.AddBlocked("not-accepted",false,4);Blocked(producer,"Audio producer is waiting when writer fails");
        var error=new IOException("intentional writer failure");q.Fail(error);q.Abort();
        Check(ReferenceEquals(Finish(producer),error),"Writer failure reaches blocked producer as the original exception");
        Exception? takeError=null;try{q.TryTake(out _);}catch(Exception e){takeError=e;}
        Check(ReferenceEquals(takeError,error),"Writer failure also wakes/fails consumer without hiding the original exception");
        Exception? addError=null;try{q.Add("late",false,0);}catch(Exception e){addError=e;}
        Check(ReferenceEquals(addError,error),"Later Abort preserves the original writer failure for all subsequent Add calls");
        q.Complete(active);var discarded=new List<string>();while(q.TryDiscard(out var value))discarded.Add(value);
        Check(discarded.SequenceEqual(new[]{"queued"}),"Failure cleanup returns every pending payload exactly once for caller disposal");
        var stats=q.Snapshot();Empty(stats,"Writer failure cleanup releases active PCM and pending image/PCM quota");Stats(stats,false,true);
        cases.Add(new{name="writer-fault-release",passed=true,stats});
    }
    static void AbortReleases()
    {
        using var f=new Fixture();var q=f.Queue;q.Add("active",true,Budget/4);var active=f.Take();
        for(int i=0;i<3;i++)q.Add("queued"+i,true,Budget/4);
        var producer=f.AddBlocked("not-accepted",true,4);Blocked(producer,"Both image and PCM limits can block one producer before Abort");
        q.Abort();Check(Finish(producer) is OperationCanceledException,"Abort promptly wakes a producer blocked by both quotas");
        q.Complete(active);q.Complete(active);var discarded=new List<string>();while(q.TryDiscard(out var value))discarded.Add(value);
        Check(discarded.SequenceEqual(new[]{"queued0","queued1","queued2"}),"Abort returns accepted waiting buffers once, in FIFO order");
        var stats=q.Snapshot();Empty(stats,"Abort cleanup releases every active and waiting reservation");Stats(stats,true,true);
        Check(stats.EnqueueWaitCount==1 && stats.ImageWaitCount==1 && stats.PcmWaitCount==1,"Simultaneous quota waits count one Add with both reasons");
        cases.Add(new{name="abort-release",passed=true,stats});
    }
    static void TimeoutAndOversize()
    {
        using var f=new Fixture();var q=f.Queue;Exception? oversized=null;
        try{q.Add("too-large",false,Budget+4,TimeSpan.FromSeconds(1));}catch(Exception error){oversized=error;}
        Check(oversized is ArgumentOutOfRangeException && q.Snapshot().QueuedItems==0,"A block larger than the entire PCM budget fails without admission or a wait");
        for(int i=0;i<3;i++)q.Add("image"+i,true,0);
        Exception? timeout=null;try{q.Add("timeout",true,0,TimeSpan.FromMilliseconds(100));}catch(Exception error){timeout=error;}
        Check(timeout is TimeoutException && q.Snapshot().QueuedItems==3,"Timed-out image submission is not accepted and does not consume capacity");
        q.CompleteAdding();while(q.Snapshot().QueuedItems>0){var item=f.Take();q.Complete(item);}
        var stats=q.Snapshot();Empty(stats,"Timeout leaves original FIFO drainable without leaked reservations");Stats(stats,true,false);
        cases.Add(new{name="timeout-and-oversize",passed=true,stats});
    }
    static void ConcurrentDiscard()
    {
        using var f=new Fixture();var q=f.Queue;q.Add("active",true,1024);var active=f.Take();
        for(int i=0;i<3;i++)q.Add("image"+i,true,16);
        for(int i=0;i<1000;i++)q.Add("pcm"+i,false,4);
        q.Abort();var released=new System.Collections.Concurrent.ConcurrentBag<string>();
        using var ready=new CountdownEvent(2);using var resume=new ManualResetEventSlim();
        Task<int> Drain()=>Task.Run(()=>{
            int count=0;
            if(q.TryDiscard(out var first)){released.Add(first);count++;}
            ready.Signal();if(!resume.Wait(5000))throw new TimeoutException("Concurrent cleanup gate timed out");
            while(q.TryDiscard(out var value)){released.Add(value);count++;Thread.Yield();}
            return count;
        });
        var one=Drain();var two=Drain();
        try
        {
            Check(ready.Wait(5000),"Both Abort/Pump cleanup consumers have acquired a distinct waiting item");
            q.Complete(active);resume.Set();
            Check(Task.WaitAll(new Task[]{one,two},5000),"Concurrent cleanup consumers terminate after queue exhaustion");
            Check(one.Result>0 && two.Result>0 && released.Count==1003 && released.Distinct().Count()==1003,
                "Two concurrent TryDiscard consumers return all 1003 accepted payloads exactly once");
            var expected=Enumerable.Range(0,3).Select(i=>"image"+i).Concat(Enumerable.Range(0,1000).Select(i=>"pcm"+i));
            Check(expected.OrderBy(x=>x).SequenceEqual(released.OrderBy(x=>x)),"Concurrent cleanup loses no waiting image or PCM payload");
            Check(!q.TryDiscard(out _),"A third discard cannot retrieve an already released payload");
            var stats=q.Snapshot();Empty(stats,"Concurrent discard plus active completion releases both quotas exactly once");
            cases.Add(new{name="concurrent-failure-cleanup",passed=true,releasedItems=released.Count,stats});
        }
        finally{resume.Set();if(!Task.WaitAll(new Task[]{one,two},5000))throw new TimeoutException("Concurrent cleanup worker survived cleanup");}
    }
    static void BlockedReaderEnds()
    {
        foreach(var mode in new[]{"complete","fault","abort"})
        {
            using var f=new Fixture();var q=f.Queue;using var entered=new ManualResetEventSlim();
            var reader=Task.Run(()=>{entered.Set();try{return (taken:q.TryTake(out _),error:(Exception?)null);}catch(Exception error){return (taken:false,error:(Exception?)error);}});
            if(!entered.Wait(5000))throw new TimeoutException("Reader was not scheduled");
            try
            {
                Blocked(reader,"Empty consumer waits before "+mode);
                var fault=new IOException("blocked reader failure");
                if(mode=="complete")q.CompleteAdding();else if(mode=="fault")q.Fail(fault);else q.Abort();
                Check(reader.Wait(5000),"Blocked consumer wakes on "+mode);
                var result=reader.Result;
                Check(!result.taken && (mode=="complete"?result.error==null:mode=="fault"?ReferenceEquals(result.error,fault):result.error is OperationCanceledException),
                    "Consumer reports the correct terminal state on "+mode);
                Empty(q.Snapshot(),"Empty terminal queue has no leaked capacity: "+mode);
            }
            finally{q.Abort();if(!reader.Wait(5000))throw new TimeoutException("Reader survived cleanup");}
        }
        cases.Add(new{name="blocked-readers-terminal-states",passed=true});
    }
}