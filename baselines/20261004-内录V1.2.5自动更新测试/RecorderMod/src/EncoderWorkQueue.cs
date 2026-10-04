using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.ExceptionServices;
using System.Threading;

namespace AzureArchive.Recorder;

// Public property names are retained in protected builds and in completed.json.
public sealed class EncoderQueueStatistics
{
    public long EnqueueWaitCount { get; }
    public double EnqueueWaitSeconds { get; }
    public double MaxEnqueueWaitSeconds { get; }
    public long ImageWaitCount { get; }
    public double ImageWaitSeconds { get; }
    public double MaxImageWaitSeconds { get; }
    public long PcmWaitCount { get; }
    public double PcmWaitSeconds { get; }
    public double MaxPcmWaitSeconds { get; }
    public int PeakQueuedImages { get; }
    public long PeakBufferedPcmBytes { get; }
    public int QueuedImages { get; }
    public long BufferedPcmBytes { get; }
    public int QueuedItems { get; }
    public int ActiveItems { get; }
    public int ImageCapacity { get; }
    public long PcmByteCapacity { get; }

    internal EncoderQueueStatistics(long waitCount, double waitSeconds, double maxWaitSeconds,
        long imageWaitCount, double imageWaitSeconds, double maxImageWaitSeconds,
        long pcmWaitCount, double pcmWaitSeconds, double maxPcmWaitSeconds,
        int peakImages, long peakPcmBytes, int images, long pcmBytes, int items, int activeItems,
        int imageCapacity, long pcmByteCapacity)
    {
        EnqueueWaitCount=waitCount; EnqueueWaitSeconds=waitSeconds; MaxEnqueueWaitSeconds=maxWaitSeconds;
        ImageWaitCount=imageWaitCount; ImageWaitSeconds=imageWaitSeconds; MaxImageWaitSeconds=maxImageWaitSeconds;
        PcmWaitCount=pcmWaitCount; PcmWaitSeconds=pcmWaitSeconds; MaxPcmWaitSeconds=maxPcmWaitSeconds;
        PeakQueuedImages=peakImages; PeakBufferedPcmBytes=peakPcmBytes;
        QueuedImages=images; BufferedPcmBytes=pcmBytes; QueuedItems=items; ActiveItems=activeItems;
        ImageCapacity=imageCapacity; PcmByteCapacity=pcmByteCapacity;
    }
}

// A single FIFO preserves video/PCM order. Taking an item releases its waiting
// image slot; completing it releases PCM bytes, including the writer's active
// block. Closing/faulting wakes producers without silently dropping accepted work.
internal sealed class EncoderWorkQueue<T>
{
    internal sealed class WorkItem
    {
        internal readonly EncoderWorkQueue<T> Owner;
        internal readonly bool HasImage;
        internal readonly long PcmBytes;
        internal bool InFlight, Released;
        public T Value { get; }

        internal WorkItem(EncoderWorkQueue<T> owner, T value, bool hasImage, long pcmBytes)
        { Owner=owner; Value=value; HasImage=hasImage; PcmBytes=pcmBytes; }
    }

    readonly object gate=new();
    readonly Queue<WorkItem> items=new();
    readonly int imageCapacity;
    readonly long pcmByteCapacity;
    int queuedImages, activeItems, peakQueuedImages;
    long bufferedPcmBytes, peakBufferedPcmBytes;
    bool addingCompleted;
    ExceptionDispatchInfo? failure;
    long waitCount, imageWaitCount, pcmWaitCount;
    double waitSeconds, maxWaitSeconds, imageWaitSeconds, maxImageWaitSeconds, pcmWaitSeconds, maxPcmWaitSeconds;

    public EncoderWorkQueue(int imageCapacity, long pcmByteCapacity)
    {
        if(imageCapacity<1)throw new ArgumentOutOfRangeException(nameof(imageCapacity));
        if(pcmByteCapacity<1)throw new ArgumentOutOfRangeException(nameof(pcmByteCapacity));
        this.imageCapacity=imageCapacity; this.pcmByteCapacity=pcmByteCapacity;
    }

    public void Add(T value, bool hasImage, long pcmBytes, TimeSpan? timeout=null)
    {
        if(pcmBytes<0 || pcmBytes>pcmByteCapacity)throw new ArgumentOutOfRangeException(nameof(pcmBytes));
        double limit=(timeout ?? TimeSpan.FromSeconds(30)).TotalSeconds;
        if(limit<0)throw new ArgumentOutOfRangeException(nameof(timeout));
        lock(gate)
        {
            long started=0;
            bool waited=false, waitedForImage=false, waitedForPcm=false;
            double imageSeconds=0, pcmSeconds=0;
            try
            {
                while(true)
                {
                    ThrowIfClosed();
                    bool imageBlocked=hasImage && queuedImages>=imageCapacity;
                    bool pcmBlocked=pcmBytes>pcmByteCapacity-bufferedPcmBytes;
                    if(!imageBlocked && !pcmBlocked)
                    {
                        items.Enqueue(new WorkItem(this,value,hasImage,pcmBytes));
                        if(hasImage)queuedImages++;
                        bufferedPcmBytes+=pcmBytes;
                        peakQueuedImages=Math.Max(peakQueuedImages,queuedImages);
                        peakBufferedPcmBytes=Math.Max(peakBufferedPcmBytes,bufferedPcmBytes);
                        Monitor.PulseAll(gate);
                        return;
                    }
                    if(!waited){ started=Stopwatch.GetTimestamp(); waited=true; }
                    waitedForImage|=imageBlocked; waitedForPcm|=pcmBlocked;
                    double remaining=limit-SecondsSince(started);
                    if(remaining<=0)throw new TimeoutException("编码器 30 秒无响应（队列容量等待）。");
                    long before=Stopwatch.GetTimestamp();
                    // A close or released quota normally wakes us immediately;
                    // the bounded wait also keeps the timeout independent of it.
                    Monitor.Wait(gate,TimeSpan.FromSeconds(Math.Min(remaining,.05)));
                    double elapsed=SecondsSince(before);
                    if(imageBlocked)imageSeconds+=elapsed;
                    if(pcmBlocked)pcmSeconds+=elapsed;
                }
            }
            finally
            {
                if(waited)
                {
                    double elapsed=SecondsSince(started);
                    waitCount++; waitSeconds+=elapsed; maxWaitSeconds=Math.Max(maxWaitSeconds,elapsed);
                    if(waitedForImage)
                    { imageWaitCount++; imageWaitSeconds+=imageSeconds; maxImageWaitSeconds=Math.Max(maxImageWaitSeconds,imageSeconds); }
                    if(waitedForPcm)
                    { pcmWaitCount++; pcmWaitSeconds+=pcmSeconds; maxPcmWaitSeconds=Math.Max(maxPcmWaitSeconds,pcmSeconds); }
                }
            }
        }
    }

    public bool TryTake(out WorkItem item)
    {
        lock(gate)
        {
            while(true)
            {
                failure?.Throw();
                if(items.Count!=0)
                {
                    item=items.Dequeue();
                    if(item.HasImage)queuedImages--;
                    item.InFlight=true; activeItems++;
                    Monitor.PulseAll(gate);
                    return true;
                }
                if(addingCompleted){ item=null!; return false; }
                Monitor.Wait(gate);
            }
        }
    }

    public void Complete(WorkItem item)
    {
        if(item==null || !ReferenceEquals(item.Owner,this))throw new ArgumentException("队列项不属于此编码器。",nameof(item));
        lock(gate)
        {
            if(item.Released)return;
            if(!item.InFlight)throw new InvalidOperationException("队列项尚未交给写入器。");
            item.Released=true; activeItems--; bufferedPcmBytes-=item.PcmBytes;
            Monitor.PulseAll(gate);
        }
    }

    public void CompleteAdding()
    {
        lock(gate){ addingCompleted=true; Monitor.PulseAll(gate); }
    }

    public void Fail(Exception error)
    {
        if(error==null)throw new ArgumentNullException(nameof(error));
        lock(gate)
        {
            failure ??= ExceptionDispatchInfo.Capture(error);
            addingCompleted=true; Monitor.PulseAll(gate);
        }
    }

    public void Abort()=>Fail(new OperationCanceledException("编码器已中止。"));

    // Failure/abort cleanup only; the caller still owns disposal of value's buffers.
    public bool TryDiscard(out T value)
    {
        lock(gate)
        {
            if(items.Count==0){ value=default!; return false; }
            var item=items.Dequeue();
            if(item.HasImage)queuedImages--;
            bufferedPcmBytes-=item.PcmBytes; item.Released=true;
            value=item.Value; Monitor.PulseAll(gate);
            return true;
        }
    }

    public EncoderQueueStatistics Snapshot()
    {
        lock(gate)return new EncoderQueueStatistics(waitCount,waitSeconds,maxWaitSeconds,
            imageWaitCount,imageWaitSeconds,maxImageWaitSeconds,pcmWaitCount,pcmWaitSeconds,maxPcmWaitSeconds,
            peakQueuedImages,peakBufferedPcmBytes,queuedImages,bufferedPcmBytes,items.Count,activeItems,
            imageCapacity,pcmByteCapacity);
    }

    void ThrowIfClosed()
    {
        failure?.Throw();
        if(addingCompleted)throw new InvalidOperationException("录制已结束，编码队列不再接收数据。");
    }

    static double SecondsSince(long timestamp)=>(Stopwatch.GetTimestamp()-timestamp)/(double)Stopwatch.Frequency;
}
