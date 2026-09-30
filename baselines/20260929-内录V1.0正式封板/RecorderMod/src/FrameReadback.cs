using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using UnityEngine;
using UnityEngine.Rendering;
namespace AzureArchive.Recorder;

// Keep at most three in-flight GPU copies and consume them in submission order.
// The mixer samples travel with their video frame, including empty DSP blocks.
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
    }
    readonly Queue<Frame> pending=new();
    readonly Encoder encoder;
    readonly int bytes;
    internal FrameReadback(Encoder encoder,int width,int height){this.encoder=encoder;bytes=width*height*4;}
    internal void Submit(RenderTexture target,float[] audio)
    {
        Flush(false);
        if(pending.Count>=3)Consume(true);
        var frame=new Frame{Audio=audio};
        frame.Request=AsyncGPUReadback.Request(target,0,TextureFormat.RGBA32,(Il2CppSystem.Action<AsyncGPUReadbackRequest>)(Action<AsyncGPUReadbackRequest>)(request=>Complete(frame,request)));
        pending.Enqueue(frame);
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
        var frame=pending.Dequeue();var request=frame.Request;
        if(wait && !request.done)request.WaitForCompletion();
        Complete(frame,request);
        if(frame.Error!=null)throw frame.Error;
        var pixels=frame.Pixels??throw new IOException("GPU frame did not complete.");frame.Pixels=null;
        encoder.WritePooledFrame(pixels,frame.Audio);
    }
    internal void Discard()
    {
        while(pending.Count>0)
        {
            var frame=pending.Dequeue();
            lock(frame){frame.Discarded=true;if(frame.Pixels!=null){ArrayPool<byte>.Shared.Return(frame.Pixels);frame.Pixels=null;}}
            var request=frame.Request;if(!request.done)request.WaitForCompletion();
        }
    }
}
