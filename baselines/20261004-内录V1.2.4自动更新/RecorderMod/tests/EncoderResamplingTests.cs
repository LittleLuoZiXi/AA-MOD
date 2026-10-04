using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;
using AzureArchive.Recorder;
using UnityEngine;
using UnityEngine.Rendering;

// CPU-only dependencies: no AA process, GPU driver, or DLSS discovery.
namespace AzureArchive.Recorder
{
    public sealed class GpuInfo { public uint VendorId { get; set; } }
    public static class GpuProfiles
    {
        public static IEnumerable<GpuInfo> Detect()=>throw new InvalidOperationException("CPU test unexpectedly attempted GPU discovery.");
    }
}
namespace UnityEngine
{
    public enum TextureFormat { RGBA32 }
    public sealed class RenderTexture
    {
        internal byte[] Pixels;
        internal bool Error;
        internal RenderTexture(byte[] pixels,bool error=false){Pixels=pixels;Error=error;}
    }
}
namespace Il2CppSystem
{
    public sealed class Action<T>
    {
        readonly System.Action<T> action;
        Action(System.Action<T> action){this.action=action;}
        public static explicit operator Action<T>(System.Action<T> action)=>new Action<T>(action);
        public void Invoke(T value)=>action(value);
    }
}
namespace UnityEngine.Rendering
{
    public struct AsyncGPUReadbackRequest
    {
        internal AsyncGPUReadback.State? State;
        internal AsyncGPUReadback.State Value=>State??throw new InvalidOperationException("Audio-only queue entries must not touch a GPU request.");
        public bool done=>Value.Done;
        public bool hasError=>Value.Error;
        public int layerDataSize=>Value.Pixels.Length;
        public void WaitForCompletion(){AsyncGPUReadback.Waits++;AsyncGPUReadback.Complete(Value);}
        public IntPtr GetDataRaw(int layer)
        {
            if(!Value.Pin.IsAllocated)throw new InvalidOperationException("Readback native data already expired.");
            AsyncGPUReadback.Copies++;return Value.Pin.AddrOfPinnedObject();
        }
    }
    public static class AsyncGPUReadback
    {
        internal sealed class State
        {
            internal byte[] Pixels=Array.Empty<byte>();
            internal bool Done,Error;
            internal Il2CppSystem.Action<AsyncGPUReadbackRequest>? Callback;
            internal GCHandle Pin;
        }
        static readonly List<State> requests=new();
        public static int Copies,Waits,Outstanding,MaxOutstanding;
        public static int Requests=>requests.Count;
        public static void Reset(){if(Outstanding!=0)throw new Exception("Undrained stub requests");requests.Clear();Copies=Waits=Outstanding=MaxOutstanding=0;}
        public static AsyncGPUReadbackRequest Request(RenderTexture target,int mip,TextureFormat format,Il2CppSystem.Action<AsyncGPUReadbackRequest> callback)
        {
            var state=new State{Pixels=(byte[])target.Pixels.Clone(),Error=target.Error,Callback=callback};
            requests.Add(state);Outstanding++;MaxOutstanding=Math.Max(MaxOutstanding,Outstanding);
            return new AsyncGPUReadbackRequest{State=state};
        }
        public static void Complete(int index)=>Complete(requests[index]);
        internal static void Complete(State state)
        {
            if(state.Done)return;
            state.Done=true;state.Pin=GCHandle.Alloc(state.Pixels,GCHandleType.Pinned);
            try{state.Callback!.Invoke(new AsyncGPUReadbackRequest{State=state});}
            finally{state.Pin.Free();Outstanding--;}
        }
    }
}

public static class EncoderResamplingTests
{
    const int Width=64,Height=48,Fps=30,Rate=48000,Channels=2;
    static int checks;
    static readonly List<object> cases=new();
    static void Check(bool success,string message)
    {
        if(!success)throw new Exception(message);
        checks++;Console.WriteLine("PASS "+checks+": "+message);
    }
    public static void Run(string root,string ffmpeg,string ffprobe,string encoderHash,string readbackHash)
    {
        if(!OperatingSystem.IsWindows())throw new PlatformNotSupportedException("PCM retention test uses Windows file-sharing semantics.");
        if(Directory.Exists(root))throw new IOException("Refusing to reuse test output: "+root);
        Directory.CreateDirectory(root);checks=0;cases.Clear();string? failure=null;
        try
        {
            RunDirect(root,ffmpeg,ffprobe,"legacy-default",Enumerable.Repeat(1,30).ToArray(),false);
            int[] repeats={0,3,0,6,2,0,8,1,10,0};
            RunDirect(root,ffmpeg,ffprobe,"atomic-zero-and-repeats",repeats,false);
            RunDirect(root,ffmpeg,ffprobe,"separate-pcm-and-frames",repeats,true);
            RunReadback(root,ffmpeg,ffprobe,"readback-zero-and-repeats",repeats);
            RunEmptyAudioBackpressure(root,ffmpeg,ffprobe);
            RunImageCapacityWithAudio(root,ffmpeg,ffprobe);
            RunOutOfOrder(root,ffmpeg,ffprobe);
            RunFailureAndDiscard(root,ffmpeg);
            RunTenSecondAudioBlock(root,ffmpeg,ffprobe);
            CheckOutputFrameArithmetic();
            foreach(int count in new[]{1,2,60})RunFpsConversion(root,ffmpeg,ffprobe,30,count);
            foreach(int outputFps in new[]{24,25,30,50,60})RunFpsConversion(root,ffmpeg,ffprobe,outputFps,61);
        }
        catch(Exception error){failure=error.ToString();throw;}
        finally
        {
            File.WriteAllText(Path.Combine(root,"results.json"),JsonSerializer.Serialize(new{
                passed=failure==null,checks,encoderSourceSha256=encoderHash,readbackSourceSha256=readbackHash,
                cpuOnly=true,aaStarted=false,dlssUsed=false,realFfmpeg=true,realGpuReadback=false,
                cases=cases.ToArray(),failure
            },new JsonSerializerOptions{WriteIndented=true}));
        }
    }
    static byte[] Pixels(int source,bool rgba)
    {
        int stride=rgba?4:3;var bytes=new byte[Width*Height*stride];
        for(int i=0;i<bytes.Length;i+=stride)
        {bytes[i]=(byte)(30+source*6);bytes[i+1]=(byte)(50+source*4);bytes[i+2]=(byte)(210-source*5);if(rgba)bytes[i+3]=255;}
        return bytes;
    }
    static float[] Sound(int step,int perChannel)
    {
        var samples=new float[perChannel*Channels];
        for(int i=0;i<perChannel;i++)
        {float value=(float)(.15*Math.Sin(2*Math.PI*(220+step*31)*i/Rate));samples[i*2]=value;samples[i*2+1]=-value;}
        return samples;
    }
    static void RunDirect(string root,string ffmpeg,string ffprobe,string name,int[] repeats,bool separate)
    {
        string dir=Path.Combine(root,name);
        using var encoder=new Encoder(ffmpeg,dir,Width,Height,Fps,Rate,Channels,18);
        var expectedAudio=new List<float>();var expectedFrames=new List<int>();
        using var audioLock=new FileStream(Path.Combine(dir,"audio.f32"),FileMode.Open,FileAccess.Read,FileShare.ReadWrite);
        for(int step=0;step<repeats.Length;step++)
        {
            var pcm=Sound(step,Rate/repeats.Length);var pixels=Pixels(step,false);
            expectedAudio.AddRange(pcm);expectedFrames.AddRange(Enumerable.Repeat(step,repeats[step]));
            if(separate)
            {
                encoder.WriteAudio(pcm);
                if(repeats[step]>0)encoder.WriteFrame(pixels,Array.Empty<float>(),repeats[step]);
            }
            else if(name=="legacy-default")encoder.WriteFrame(pixels,pcm);
            else encoder.WriteFrame(pixels,pcm,repeats[step]);
            // Public APIs must own their copies while the bounded worker runs.
            Array.Fill(pcm,9f);Array.Fill(pixels,(byte)0);
        }
        long frames=encoder.Frames,samples=encoder.AudioSamples;
        Expect<ArgumentOutOfRangeException>(()=>encoder.WriteFrame(Pixels(0,false),Array.Empty<float>(),-1),"negative repeat count rejected");
        Expect<ArgumentException>(()=>encoder.WriteAudio(new float[3]),"misaligned standalone PCM rejected");
        Check(encoder.Frames==frames && encoder.AudioSamples==samples,"Invalid writes do not change counters: "+name);
        string final=encoder.FinishAsync().GetAwaiter().GetResult();
        Validate(dir,final,ffmpeg,ffprobe,encoder,expectedAudio,expectedFrames);
        Expect<InvalidOperationException>(()=>encoder.WriteAudio(Array.Empty<float>()),"post-finish audio rejected");
        cases.Add(new{name,passed=true,frames=encoder.Frames,audioSamples=encoder.AudioSamples,output=final});
    }
    static void RunReadback(string root,string ffmpeg,string ffprobe,string name,int[] repeats)
    {
        AsyncGPUReadback.Reset();string dir=Path.Combine(root,name);
        using var encoder=new Encoder(ffmpeg,dir,Width,Height,Fps,Rate,Channels,18,"libx264","rgba");
        using var audioLock=new FileStream(Path.Combine(dir,"audio.f32"),FileMode.Open,FileAccess.Read,FileShare.ReadWrite);
        var readback=new FrameReadback(encoder,Width,Height);
        var expectedAudio=new List<float>();var expectedFrames=new List<int>();
        for(int step=0;step<repeats.Length;step++)
        {
            var pcm=Sound(step,Rate/repeats.Length);expectedAudio.AddRange(pcm);
            expectedFrames.AddRange(Enumerable.Repeat(step,repeats[step]));
            readback.Submit(new RenderTexture(Pixels(step,true)),pcm,repeats[step]);
        }
        readback.Flush(true);
        Check(AsyncGPUReadback.Requests==repeats.Count(n=>n>0) && AsyncGPUReadback.Copies==AsyncGPUReadback.Requests,
            "Zero-output steps skip GPU; each sampled snapshot is copied once despite repeated output");
        Check(AsyncGPUReadback.MaxOutstanding<=3 && AsyncGPUReadback.Outstanding==0 && AsyncGPUReadback.Waits>0,
            "Readback keeps the three-image bound and drains through blocking completion");
        string final=encoder.FinishAsync().GetAwaiter().GetResult();
        Validate(dir,final,ffmpeg,ffprobe,encoder,expectedAudio,expectedFrames);
        cases.Add(new{name,passed=true,frames=encoder.Frames,audioSamples=encoder.AudioSamples,gpuRequests=AsyncGPUReadback.Requests,
            gpuCopies=AsyncGPUReadback.Copies,maxOutstanding=AsyncGPUReadback.MaxOutstanding,output=final});
    }
    static void RunEmptyAudioBackpressure(string root,string ffmpeg,string ffprobe)
    {
        AsyncGPUReadback.Reset();string name="empty-pcm-does-not-block-readback",dir=Path.Combine(root,name);
        using var encoder=new Encoder(ffmpeg,dir,Width,Height,Fps,Rate,Channels,18,"libx264","rgba");
        using var audioLock=new FileStream(Path.Combine(dir,"audio.f32"),FileMode.Open,FileAccess.Read,FileShare.ReadWrite);
        var readback=new FrameReadback(encoder,Width,Height);var expectedAudio=new List<float>();
        var a=Sound(0,12000);var b=Sound(1,12000);var c=Sound(2,12000);var tail=Sound(3,12000);
        expectedAudio.AddRange(a);expectedAudio.AddRange(b);expectedAudio.AddRange(c);expectedAudio.AddRange(tail);
        var unsampled=new RenderTexture(Pixels(9,true));
        readback.Submit(new RenderTexture(Pixels(0,true)),a,9);

        // Native playback can produce many source updates with no DSP samples.
        // The first GPU request deliberately stays incomplete throughout them.
        for(int i=0;i<64;i++)
        {
            readback.SubmitAudio(Array.Empty<float>());
            readback.Submit(unsampled,Array.Empty<float>(),0);
        }
        Check(AsyncGPUReadback.Waits==0 && AsyncGPUReadback.Outstanding==1 && AsyncGPUReadback.Copies==0,
            "Empty PCM bursts cannot wait for or complete the pending GPU request");
        Check(AsyncGPUReadback.Requests==1 && encoder.Frames==0 && encoder.AudioSamples==0,
            "Empty standalone and zero-output submissions create no GPU work or encoded output");

        // Both remaining real slots must still be available. This demonstrates
        // capacity preservation without inspecting the private pending queue.
        readback.SubmitAudio(b);
        readback.Submit(new RenderTexture(Pixels(2,true)),c,21);
        Check(AsyncGPUReadback.Waits==0 && AsyncGPUReadback.Requests==2 && AsyncGPUReadback.Outstanding==2,
            "Empty PCM leaves room for a later image and intervening nonempty audio");
        for(int i=0;i<64;i++)readback.SubmitAudio(Array.Empty<float>());
        Check(AsyncGPUReadback.Waits==0 && AsyncGPUReadback.Outstanding==2 && encoder.Frames==0 && encoder.AudioSamples==0,
            "Empty PCM remains a no-op behind two pending images and intervening nonempty audio");

        AsyncGPUReadback.Complete(1);readback.Flush(false);
        for(int i=0;i<8;i++)readback.SubmitAudio(Array.Empty<float>());
        Check(AsyncGPUReadback.Copies==1 && AsyncGPUReadback.Waits==0 && encoder.Frames==0 && encoder.AudioSamples==0,
            "A later completed snapshot and nonempty PCM still cannot overtake the first GPU request");
        AsyncGPUReadback.Complete(0);readback.Flush(false);
        Check(encoder.Frames==30 && encoder.AudioSamples==36000,
            "The first snapshot, intervening nonempty PCM and later snapshot drain in original order");
        readback.SubmitAudio(tail);
        for(int i=0;i<8;i++)readback.SubmitAudio(Array.Empty<float>());
        readback.Flush(true);
        Check(AsyncGPUReadback.Waits==0 && AsyncGPUReadback.Outstanding==0 && AsyncGPUReadback.Copies==2,
            "Nonempty trailing PCM drains completely without any forced GPU wait");
        Check(encoder.AudioSamples==Rate && encoder.Frames==Fps,
            "Empty submissions neither lose nor duplicate the one-second PCM tail or video frames");
        string final=encoder.FinishAsync().GetAwaiter().GetResult();
        Validate(dir,final,ffmpeg,ffprobe,encoder,expectedAudio,Enumerable.Repeat(0,9).Concat(Enumerable.Repeat(2,21)).ToList());
        cases.Add(new{name,passed=true,emptyAudioSubmissions=208,frames=encoder.Frames,audioSamples=encoder.AudioSamples,
            gpuRequests=AsyncGPUReadback.Requests,gpuCopies=AsyncGPUReadback.Copies,gpuWaits=AsyncGPUReadback.Waits,
            maxOutstanding=AsyncGPUReadback.MaxOutstanding,rawPcmFifoAndTailVerified=true,output=final});
    }
    static void RunImageCapacityWithAudio(string root,string ffmpeg,string ffprobe)
    {
        AsyncGPUReadback.Reset();string name="pcm-does-not-spend-image-capacity",dir=Path.Combine(root,name);
        using var encoder=new Encoder(ffmpeg,dir,Width,Height,Fps,Rate,Channels,18,"libx264","rgba");
        using var audioLock=new FileStream(Path.Combine(dir,"audio.f32"),FileMode.Open,FileAccess.Read,FileShare.ReadWrite);
        var readback=new FrameReadback(encoder,Width,Height);
        var pcm=Enumerable.Range(0,12).Select(step=>Sound(step,4000)).ToArray();
        var expectedAudio=pcm.SelectMany(samples=>samples).ToList();
        var expectedFrames=Enumerable.Repeat(0,6).Concat(Enumerable.Repeat(1,7))
            .Concat(Enumerable.Repeat(2,8)).Concat(Enumerable.Repeat(3,9)).ToList();

        readback.Submit(new RenderTexture(Pixels(0,true)),pcm[0],6);
        for(int i=1;i<=3;i++)readback.SubmitAudio(pcm[i]);
        readback.Submit(new RenderTexture(Pixels(1,true)),pcm[4],7);
        for(int i=5;i<=8;i++)readback.SubmitAudio(pcm[i]);
        Check(AsyncGPUReadback.Requests==2 && AsyncGPUReadback.Outstanding==2 && AsyncGPUReadback.Waits==0
            && encoder.Frames==0 && encoder.AudioSamples==0,
            "Two pending images plus seven nonempty PCM blocks neither force a GPU wait nor overtake the FIFO head");

        readback.Submit(new RenderTexture(Pixels(2,true)),pcm[9],8);
        Check(AsyncGPUReadback.Requests==3 && AsyncGPUReadback.Outstanding==3 && AsyncGPUReadback.Waits==0,
            "Nonempty PCM leaves all three image slots available");
        // A later completed image still owns its pixel buffer until FIFO consumption.
        // Counting only unfinished GPU requests would incorrectly admit image four.
        AsyncGPUReadback.Complete(1);readback.Flush(false);
        Check(AsyncGPUReadback.Copies==1 && AsyncGPUReadback.Outstanding==2 && AsyncGPUReadback.Waits==0
            && encoder.Frames==0 && encoder.AudioSamples==0,
            "An out-of-order completed image retains its pixels and FIFO position behind the pending head");
        readback.Submit(new RenderTexture(Pixels(3,true)),pcm[10],9);
        Check(AsyncGPUReadback.Requests==4 && AsyncGPUReadback.Waits==1 && AsyncGPUReadback.Copies==2
            && AsyncGPUReadback.Outstanding==2 && AsyncGPUReadback.MaxOutstanding==3,
            "The fourth image waits for the oldest image even when a later GPU request has already completed");

        readback.SubmitAudio(pcm[11]);
        Check(AsyncGPUReadback.Waits==1 && encoder.Frames==13 && encoder.AudioSamples==36000,
            "Nonempty trailing PCM causes no additional wait while ready images and intervening PCM drain in order");
        AsyncGPUReadback.Complete(3);readback.Flush(false);
        Check(AsyncGPUReadback.Waits==1 && encoder.Frames==13 && encoder.AudioSamples==36000,
            "The final image and PCM tail cannot overtake the still-pending third image");
        AsyncGPUReadback.Complete(2);readback.Flush(true);
        Check(AsyncGPUReadback.Waits==1 && AsyncGPUReadback.Copies==4 && AsyncGPUReadback.Outstanding==0
            && encoder.Frames==Fps && encoder.AudioSamples==Rate,
            "Image capacity is bounded while all twelve PCM blocks and the audio-only tail drain intact");
        string final=encoder.FinishAsync().GetAwaiter().GetResult();
        Validate(dir,final,ffmpeg,ffprobe,encoder,expectedAudio,expectedFrames);
        cases.Add(new{name,passed=true,frames=encoder.Frames,audioSamples=encoder.AudioSamples,nonemptyAudioOnlyBlocks=8,
            gpuRequests=AsyncGPUReadback.Requests,gpuCopies=AsyncGPUReadback.Copies,gpuWaits=AsyncGPUReadback.Waits,
            maxOutstanding=AsyncGPUReadback.MaxOutstanding,completedImagesRetainCapacity=true,
            rawPcmFifoAndTailVerified=true,output=final});
    }
    static void RunOutOfOrder(string root,string ffmpeg,string ffprobe)
    {
        AsyncGPUReadback.Reset();string name="readback-out-of-order-with-pcm-tail",dir=Path.Combine(root,name);
        using var encoder=new Encoder(ffmpeg,dir,Width,Height,Fps,Rate,Channels,18,"libx264","rgba");
        using var audioLock=new FileStream(Path.Combine(dir,"audio.f32"),FileMode.Open,FileAccess.Read,FileShare.ReadWrite);
        var readback=new FrameReadback(encoder,Width,Height);var expectedAudio=new List<float>();
        var a=Sound(0,12000);var b=Sound(1,12000);var c=Sound(2,12000);var tail=Sound(3,12000);
        expectedAudio.AddRange(a);expectedAudio.AddRange(b);expectedAudio.AddRange(c);expectedAudio.AddRange(tail);
        readback.Submit(new RenderTexture(Pixels(0,true)),a,9);readback.SubmitAudio(b);
        readback.Submit(new RenderTexture(Pixels(2,true)),c,21);
        AsyncGPUReadback.Complete(1);readback.Flush(false);
        Check(encoder.Frames==0 && encoder.AudioSamples==0,"Later GPU completion and intervening audio cannot overtake the earlier source");
        AsyncGPUReadback.Complete(0);readback.Flush(false);
        Check(encoder.Frames==30 && encoder.AudioSamples==36000,"Completed source snapshots and intervening PCM drain in FIFO order");
        readback.SubmitAudio(tail);readback.Flush(true);
        Check(AsyncGPUReadback.Requests==2 && AsyncGPUReadback.Copies==2 && AsyncGPUReadback.Outstanding==0,
            "Out-of-order callback buffers survive native data expiry and are never recopied");
        string final=encoder.FinishAsync().GetAwaiter().GetResult();
        Validate(dir,final,ffmpeg,ffprobe,encoder,expectedAudio,Enumerable.Repeat(0,9).Concat(Enumerable.Repeat(2,21)).ToList());
        cases.Add(new{name,passed=true,frames=encoder.Frames,audioSamples=encoder.AudioSamples,gpuRequests=2,gpuCopies=2,output=final});
    }
    static void RunFailureAndDiscard(string root,string ffmpeg)
    {
        AsyncGPUReadback.Reset();string dir=Path.Combine(root,"readback-failure-and-discard");
        using var encoder=new Encoder(ffmpeg,dir,Width,Height,Fps,Rate,Channels,18,"libx264","rgba");
        var readback=new FrameReadback(encoder,Width,Height);
        Expect<ArgumentOutOfRangeException>(()=>readback.Submit(new RenderTexture(Pixels(0,true)),Array.Empty<float>(),-1),"Readback rejects negative repetitions before requesting GPU work");
        Check(AsyncGPUReadback.Requests==0,"Invalid readback submission has no GPU side effect");
        readback.Submit(new RenderTexture(Pixels(0,true),true),Sound(0,12000),3);
        AsyncGPUReadback.Complete(0);
        Expect<IOException>(()=>readback.Flush(true),"GPU readback error remains visible");
        readback.Submit(new RenderTexture(Pixels(1,true)),Sound(1,12000),3);
        readback.SubmitAudio(Sound(2,12000));
        readback.Submit(new RenderTexture(Pixels(3,true)),Sound(3,12000),3);
        AsyncGPUReadback.Complete(2); // One completed pooled buffer waits behind an incomplete request.
        readback.Discard();readback.Discard();
        Check(AsyncGPUReadback.Outstanding==0 && AsyncGPUReadback.Copies==1 && encoder.Frames==0 && encoder.AudioSamples==0,
            "Discard drains incomplete GPU work and completed pooled data without enqueueing discarded PCM");
        encoder.Abort();
        Expect<InvalidOperationException>(()=>encoder.WriteAudio(Sound(4,12000)),"post-abort audio rejected");
        cases.Add(new{name="readback-failure-and-discard",passed=true,realGpuReadback=false});
    }
    static void RunTenSecondAudioBlock(string root,string ffmpeg,string ffprobe)
    {
        string name="ten-second-audio-block",dir=Path.Combine(root,name);
        using var encoder=new Encoder(ffmpeg,dir,Width,Height,Fps,Rate,Channels,18);
        using var audioLock=new FileStream(Path.Combine(dir,"audio.f32"),FileMode.Open,FileAccess.Read,FileShare.ReadWrite);
        var pcm=Sound(0,Rate*10);var expectedAudio=pcm.ToList();var pixels=Pixels(0,false);
        encoder.WriteAudio(pcm);Array.Fill(pcm,9f);
        encoder.WriteFrame(pixels,Array.Empty<float>(),Fps*10);Array.Fill(pixels,(byte)0);
        Check(encoder.AudioSamples==Rate*10 && encoder.Frames==Fps*10,
            "A legitimate ten-second standalone PCM block fits the independent audio budget");
        string final=encoder.FinishAsync().GetAwaiter().GetResult();
        Validate(dir,final,ffmpeg,ffprobe,encoder,expectedAudio,Enumerable.Repeat(0,Fps*10).ToList());
        cases.Add(new{name,passed=true,frames=encoder.Frames,audioSamples=encoder.AudioSamples,
            singleAudioBlockSeconds=10,rawPcmFifoAndTailVerified=true,output=final});
    }
    static void CheckOutputFrameArithmetic()
    {
        foreach(int rate in new[]{24,25,30,50,60})
        {
            foreach(long count in new[]{0L,1L,2L,3L,59L,60L,61L,long.MaxValue})
            {
                // Independent arbitrary-precision oracle also catches intermediate
                // long overflow in otherwise representable final results.
                long expected=(long)(((System.Numerics.BigInteger)count*rate+59)/60);
                if(Encoder.OutputFrameCount(count,60,rate)!=expected)
                    throw new Exception("Incorrect cumulative frame conversion: "+count+" input frames / "+rate+" output fps");
            }
        }
        Check(true,"Output-frame arithmetic uses cumulative rational ceil across zero, odd/even, exact-second and long-limit boundaries");
        Expect<ArgumentOutOfRangeException>(()=>Encoder.OutputFrameCount(-1,60,30),"Negative input frame count rejected");
        Expect<ArgumentOutOfRangeException>(()=>Encoder.OutputFrameCount(1,0,30),"Zero input FPS rejected");
        Expect<ArgumentOutOfRangeException>(()=>Encoder.OutputFrameCount(1,60,0),"Zero output FPS rejected");
    }
    static byte[] ConversionPixels(int id)
    {
        // Eight wide binary stripes identify the source frame unambiguously even
        // after H.264/YUV conversion. A separate top/bottom color checks vflip.
        var pixels=new byte[Width*Height*3];
        for(int y=0;y<Height;y++)for(int x=0;x<Width;x++)
        {
            int at=(y*Width+x)*3,bit=x/8;
            pixels[at]=(byte)(((id>>bit)&1)!=0?220:30);
            pixels[at+1]=(byte)(y<Height/2?40:190);pixels[at+2]=60;
        }
        return pixels;
    }
    static void RunFpsConversion(string root,string ffmpeg,string ffprobe,int outputFps,int inputCount)
    {
        const int inputFps=60;
        string name="input60-output"+outputFps+"-frames"+inputCount,dir=Path.Combine(root,name);
        using var encoder=new Encoder(ffmpeg,dir,Width,Height,outputFps,Rate,Channels,18,"libx264","rgb24",inputFps);
        using var audioLock=new FileStream(Path.Combine(dir,"audio.f32"),FileMode.Open,FileAccess.Read,FileShare.ReadWrite);
        var expectedAudio=new List<float>();bool cumulativeCountsMatch=true;
        for(int input=0;input<inputCount;input++)
        {
            var pcm=Sound(input,Rate/inputFps);expectedAudio.AddRange(pcm);var pixels=ConversionPixels(input);
            if(input%2==0)encoder.WriteFrame(pixels,pcm);
            else{encoder.WriteAudio(pcm);encoder.WriteFrame(pixels,Array.Empty<float>());}
            // Submitted buffers belong to the encoder after both API forms.
            Array.Fill(pcm,9f);Array.Fill(pixels,(byte)0);
            int accepted=input+1,expectedCount=(accepted*outputFps+inputFps-1)/inputFps;
            cumulativeCountsMatch&=encoder.InputFrames==accepted && encoder.Frames==expectedCount;
        }
        int expectedFrames=(inputCount*outputFps+inputFps-1)/inputFps;
        double inputSeconds=(double)inputCount/inputFps,outputSeconds=(double)expectedFrames/outputFps;
        var expectedSourceIds=Enumerable.Range(0,expectedFrames).Select(frame=>frame*inputFps/outputFps).ToArray();
        Check(cumulativeCountsMatch && encoder.InputFps==inputFps && encoder.OutputFps==outputFps,
            "Input/output rates and frame counters stay separate; each batch uses cumulative ceil: "+name);
        Check(encoder.AudioSamples==(long)inputCount*Rate/inputFps && Math.Abs(encoder.VideoSeconds-outputSeconds)<1e-12,
            "Frame filtering does not duplicate or drop submitted PCM samples: "+name);
        Check(outputSeconds+1e-12>=inputSeconds && outputSeconds-inputSeconds<1d/outputFps,
            "Only tail rounding extends the sampled input duration by less than one output frame: "+name);
        string final=encoder.FinishAsync().GetAwaiter().GetResult();
        byte[] expectedPcm=MemoryMarshal.AsBytes(expectedAudio.ToArray().AsSpan()).ToArray();
        Check(File.ReadAllBytes(Path.Combine(dir,"audio.f32")).SequenceEqual(expectedPcm),
            "All PCM from kept and dropped image intervals remains byte-exact and ordered: "+name);
        var stats=encoder.SnapshotWaitStatistics();
        Check(stats.QueuedItems==0 && stats.ActiveItems==0 && stats.QueuedImages==0 && stats.BufferedPcmBytes==0,
            "Converted export drains every owned queue reservation: "+name);
        File.WriteAllText(Path.Combine(dir,"queue-statistics.json"),JsonSerializer.Serialize(stats,new JsonSerializerOptions{WriteIndented=true}));
        string json=RunTool(ffprobe,"-v","error","-count_frames","-show_streams","-show_format","-of","json",final);
        File.WriteAllText(Path.Combine(dir,"ffprobe.json"),json);
        using var document=JsonDocument.Parse(json);var streams=document.RootElement.GetProperty("streams").EnumerateArray().ToArray();
        var video=streams.Single(s=>s.GetProperty("codec_type").GetString()=="video");var audio=streams.Single(s=>s.GetProperty("codec_type").GetString()=="audio");
        double duration=double.Parse(video.GetProperty("duration").GetString()!,CultureInfo.InvariantCulture);
        Check(video.GetProperty("codec_name").GetString()=="h264" && video.GetProperty("nb_read_frames").GetString()==expectedFrames.ToString(CultureInfo.InvariantCulture)
            && video.GetProperty("avg_frame_rate").GetString()==outputFps+"/1" && Math.Abs(duration-outputSeconds)<.000002
            && double.Parse(video.GetProperty("start_time").GetString()!,CultureInfo.InvariantCulture)==0,
            "Real fps filter/EOF yields the exact expected output frames, zero origin, selected rate and duration: "+name);
        Check(audio.GetProperty("codec_name").GetString()=="aac" && audio.GetProperty("sample_rate").GetString()==Rate.ToString(CultureInfo.InvariantCulture)
            && audio.GetProperty("channels").GetInt32()==Channels,
            "Output conversion retains the original audio sample rate and channel layout: "+name);
        string decodedPath=Path.Combine(dir,"decoded-conversion.rgb");
        RunTool(ffmpeg,"-v","error","-nostdin","-n","-i",final,"-map","0:v:0","-fps_mode","passthrough","-f","rawvideo","-pix_fmt","rgb24",decodedPath);
        byte[] decoded=File.ReadAllBytes(decodedPath);int frameBytes=Width*Height*3;
        Check(decoded.Length==expectedFrames*frameBytes,"Decoded conversion has exactly the advertised complete frames: "+name);
        for(int frame=0;frame<expectedFrames;frame++)
        {
            int expectedId=expectedSourceIds[frame];
            if(expectedId>=inputCount || (long)expectedId*outputFps>(long)frame*inputFps)
                throw new Exception("Invalid causal test oracle");
            foreach(int y in new[]{8,Height-9})for(int bit=0;bit<8;bit++)
            {
                int at=frame*frameBytes+(y*Width+bit*8+4)*3;
                int red=((expectedId>>bit)&1)!=0?220:30,green=y<Height/2?190:40;
                if(Math.Abs(decoded[at]-red)>12 || Math.Abs(decoded[at+1]-green)>12 || Math.Abs(decoded[at+2]-60)>12)
                    throw new Exception("Wrong source frame/vflip at output "+frame+", expected source "+expectedId+", bit "+bit+" in "+name);
            }
        }
        Check(true,"Every decoded frame, including first/last, selects floor(outputIndex*60/outputFps) without a future snapshot: "+name);
        RunTool(ffmpeg,"-v","error","-xerror","-nostdin","-i",final,"-f","null","-");
        Check(true,"Complete converted audio/video decodes without errors: "+name);
        cases.Add(new{name,passed=true,inputFrames=encoder.InputFrames,inputFps=encoder.InputFps,frames=encoder.Frames,outputFps=encoder.OutputFps,
            inputSeconds,videoSeconds=encoder.VideoSeconds,tailRoundingSeconds=outputSeconds-inputSeconds,audioSamples=encoder.AudioSamples,
            firstSourceId=expectedSourceIds[0],lastSourceId=expectedSourceIds[expectedSourceIds.Length-1],expectedSourceIds,
            causalSelectionVerified=true,rawPcmByteExact=true,output=final});
    }
    static void Validate(string dir,string final,string ffmpeg,string ffprobe,Encoder encoder,List<float> expectedAudio,List<int> expectedFrames)
    {
        string name=Path.GetFileName(dir);
        var queueStats=encoder.SnapshotWaitStatistics();
        Check(queueStats.ImageCapacity==3 && queueStats.PcmByteCapacity==12L*Rate*Channels*sizeof(float)
            && queueStats.PeakQueuedImages<=3 && queueStats.PeakBufferedPcmBytes<=queueStats.PcmByteCapacity,
            "Real encoder uses independent three-pending-image and twelve-second PCM limits: "+name);
        Check(queueStats.QueuedItems==0 && queueStats.ActiveItems==0 && queueStats.QueuedImages==0 && queueStats.BufferedPcmBytes==0,
            "Successful real encoder finish releases all queue and active-writer reservations: "+name);
        File.WriteAllText(Path.Combine(dir,"queue-statistics.json"),JsonSerializer.Serialize(queueStats,new JsonSerializerOptions{WriteIndented=true}));
        Check(encoder.Frames==expectedFrames.Count && encoder.AudioSamples==expectedAudio.Count/Channels,"Frame and independent PCM counts are exact: "+name);
        byte[] expected=MemoryMarshal.AsBytes(expectedAudio.ToArray().AsSpan()).ToArray();
        Check(File.ReadAllBytes(Path.Combine(dir,"audio.f32")).SequenceEqual(expected),"Raw PCM is byte-exact, ordered, and includes the audio-only tail: "+name);
        string json=RunTool(ffprobe,"-v","error","-count_frames","-show_streams","-show_format","-of","json",final);
        File.WriteAllText(Path.Combine(dir,"ffprobe.json"),json);
        using var document=JsonDocument.Parse(json);var streams=document.RootElement.GetProperty("streams").EnumerateArray().ToArray();
        var video=streams.Single(s=>s.GetProperty("codec_type").GetString()=="video");var audio=streams.Single(s=>s.GetProperty("codec_type").GetString()=="audio");
        double duration=double.Parse(document.RootElement.GetProperty("format").GetProperty("duration").GetString()!,CultureInfo.InvariantCulture);
        Check(video.GetProperty("codec_name").GetString()=="h264" && video.GetProperty("nb_read_frames").GetString()==expectedFrames.Count.ToString(CultureInfo.InvariantCulture)
            && video.GetProperty("avg_frame_rate").GetString()=="30/1" && audio.GetProperty("codec_name").GetString()=="aac" && Math.Abs(duration-(double)expectedFrames.Count/Fps)<.05,
            "Real ffprobe confirms H.264/AAC, 30 fps, exact frame count, and expected duration: "+name);
        string raw=Path.Combine(dir,"decoded.rgb");
        RunTool(ffmpeg,"-v","error","-nostdin","-n","-i",final,"-map","0:v:0","-f","rawvideo","-pix_fmt","rgb24",raw);
        byte[] decoded=File.ReadAllBytes(raw);int frameBytes=Width*Height*3;
        Check(decoded.Length==expectedFrames.Count*frameBytes,"Decoded video has the expected number of complete frames: "+name);
        for(int frame=0;frame<expectedFrames.Count;frame++)
        {
            byte[] source=Pixels(expectedFrames[frame],false);
            for(int channel=0;channel<3;channel++)
                if(Math.Abs(decoded[frame*frameBytes+channel]-source[channel])>6)
                    throw new Exception("Unexpected source snapshot/order at output frame "+frame+" in "+name);
        }
        Check(true,"Decoded colors preserve every expected source snapshot and its repetition count: "+name);
        RunTool(ffmpeg,"-v","error","-nostdin","-i",final,"-f","null","-");
        Check(true,"Full FFmpeg audio/video decode succeeds: "+name);
    }
    static void Expect<T>(System.Action action,string message) where T:Exception
    {
        try{action();}catch(T){Check(true,message);return;}
        throw new Exception("Expected "+typeof(T).Name+": "+message);
    }
    static string RunTool(string executable,params string[] arguments)
    {
        var start=new ProcessStartInfo(executable){UseShellExecute=false,CreateNoWindow=true,WindowStyle=ProcessWindowStyle.Hidden,RedirectStandardOutput=true,RedirectStandardError=true};
        foreach(string argument in arguments)start.ArgumentList.Add(argument);
        using var process=Process.Start(start)??throw new IOException("Could not start "+executable);
        var stdout=process.StandardOutput.ReadToEndAsync();var stderr=process.StandardError.ReadToEndAsync();
        if(!process.WaitForExit(30000)){process.Kill(true);process.WaitForExit();throw new TimeoutException("Media verification exceeded 30 seconds.");}
        string output=stdout.GetAwaiter().GetResult(),error=stderr.GetAwaiter().GetResult();
        if(process.ExitCode!=0)throw new IOException(Path.GetFileName(executable)+" failed: "+error);
        return output;
    }
}