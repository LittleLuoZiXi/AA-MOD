using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.Diagnostics;
using System.Runtime.InteropServices;
using UnityEngine;
using UnityEngine.Rendering;
namespace AzureArchive.Recorder;

// Keep at most three in-flight images and consume all blocks in submission order.
// Audio-only steps share the FIFO with snapshots so even the final PCM tail is
// drained before FinishAsync. A snapshot is copied once, then encoded N times.
internal sealed class FrameReadback
{
    sealed class Frame
    {
        internal AsyncGPUReadbackRequest Request;
        internal float[] Audio=Array.Empty<float>();
        internal byte[]? Pixels;
        internal Exception? Error;
        internal volatile bool Ready;
        internal bool Discarded;
        internal int RepeatCount;
    }
    readonly Queue<Frame> pending=new();
    readonly Encoder encoder;
    readonly int bytes;
    int pendingImages;
    long gpuWaitCount,gpuWaitTicks,maximumGpuWaitTicks;
    internal object GetDiagnostics()=>new{gpuWaitCount,gpuWaitSeconds=(double)gpuWaitTicks/Stopwatch.Frequency,maximumGpuWaitSeconds=(double)maximumGpuWaitTicks/Stopwatch.Frequency};
    internal FrameReadback(Encoder encoder,int width,int height){this.encoder=encoder;bytes=width*height*4;}
    internal void Submit(RenderTexture target,float[] audio,int repeatCount=1)
    {
        if(repeatCount<0)throw new ArgumentOutOfRangeException(nameof(repeatCount));
        if(repeatCount==0){SubmitAudio(audio);return;}
        MakeImageRoom();
        var frame=new Frame{Audio=audio,RepeatCount=repeatCount};
        frame.Request=AsyncGPUReadback.Request(target,0,TextureFormat.RGBA32,(Il2CppSystem.Action<AsyncGPUReadbackRequest>)(Action<AsyncGPUReadbackRequest>)(request=>Complete(frame,request)));
        pending.Enqueue(frame);pendingImages++;
    }
    internal void SubmitAudio(float[] audio)
    {
        // A native source step can produce no DSP samples. Such a step must
        // not occupy the bounded GPU queue or force an early readback wait.
        if(audio.Length==0)return;
        // PCM does not allocate a GPU snapshot and must not spend an image
        // slot. Keep its FIFO position so audio order/tails stay unchanged.
        Flush(false);
        pending.Enqueue(new Frame{Audio=audio,Ready=true});
    }
    void MakeImageRoom()
    {
        Flush(false);
        while(pendingImages>=3)Consume(true);
    }
    internal void Flush(bool wait)
    {while(pending.Count>0 && (wait || pending.Peek().Ready || pending.Peek().Request.done))Consume(wait);}
    void Complete(Frame frame,AsyncGPUReadbackRequest request)
    {
        // Unity frees a readback's native data after one frame. Copy in its
        // completion callback, even if a later scene render delays consumption.
        lock(frame)
        {
            if(frame.Ready || frame.Discarded)return;
            byte[]? pixels=null;
            try
            {
                if(request.hasError || request.layerDataSize!=bytes)throw new IOException($"GPU 离屏回读失败（error={request.hasError}, bytes={request.layerDataSize}/{bytes}）；可在 MOD 配置中关闭 AsyncReadback 后重试。");
                pixels=ArrayPool<byte>.Shared.Rent(bytes);
                Marshal.Copy(request.GetDataRaw(0),pixels,0,bytes);
                frame.Pixels=pixels;
            }
            catch(Exception ex){if(pixels!=null)ArrayPool<byte>.Shared.Return(pixels);frame.Error=ex;}
            finally{frame.Ready=true;}
        }
    }
    void Consume(bool wait)
    {
        var frame=pending.Dequeue();
        if(frame.RepeatCount==0){encoder.WriteAudio(frame.Audio);return;}
        pendingImages--;
        var request=frame.Request;
        if(wait && !request.done)
        {
            long started=Stopwatch.GetTimestamp();gpuWaitCount++;
            try{request.WaitForCompletion();}
            finally{long elapsed=Stopwatch.GetTimestamp()-started;gpuWaitTicks+=elapsed;maximumGpuWaitTicks=Math.Max(maximumGpuWaitTicks,elapsed);}
        }
        Complete(frame,request);
        if(frame.Error!=null)throw frame.Error;
        var pixels=frame.Pixels??throw new IOException("GPU frame did not complete.");frame.Pixels=null;
        encoder.WritePooledFrame(pixels,frame.Audio,frame.RepeatCount);
    }
    internal void Discard()
    {
        while(pending.Count>0)
        {
            var frame=pending.Dequeue();
            lock(frame){frame.Discarded=true;if(frame.Pixels!=null){ArrayPool<byte>.Shared.Return(frame.Pixels);frame.Pixels=null;}}
            if(frame.RepeatCount==0)continue;
            pendingImages--;
            var request=frame.Request;if(!request.done)request.WaitForCompletion();
        }
    }
}