using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using System.Buffers;
using System.Collections.Generic;
using System.Linq;

namespace AzureArchive.Recorder;

// No Unity dependency: also exercised by the CPU-only integration test.
public sealed class Encoder : IDisposable
{
    readonly Process process;
    readonly Task<string> stderr;
    readonly FileStream audio;
    readonly string ffmpeg, directory;
    readonly int fps, sampleRate, channels;
    readonly int frameBytes;
    readonly EncoderWorkQueue<(byte[]? Pixels,float[] Samples,bool Pooled,int RepeatCount)> queue;
    readonly Task writer;
    volatile bool finished;
    public long InputFrames { get; private set; }
    public long Frames=>OutputFrameCount(InputFrames,InputFps,OutputFps);
    public long AudioSamples { get; private set; }
    public int InputFps { get; }
    public int OutputFps=>fps;
    public double VideoSeconds=>(double)Frames/OutputFps;
    public int Width { get; }
    public int Height { get; }
    public string VideoEncoder { get; }
    public EncoderQueueStatistics SnapshotWaitStatistics()=>queue.Snapshot();
    internal object GetQueueDiagnostics()=>SnapshotWaitStatistics();

    public static Process Start(string executable, params string[] arguments)
    {
        var info = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardError = true };
        foreach (var arg in arguments) info.ArgumentList.Add(arg);
        return Process.Start(info) ?? throw new IOException("无法启动编码器。");
    }

    public Encoder(string exe,string dir,int width,int height,int frameRate,int rate,int channelCount,int crf)
        :this(exe,dir,width,height,frameRate,rate,channelCount,crf,"libx264","rgb24"){}
    public Encoder(string exe, string dir, int width, int height, int frameRate, int rate, int channelCount, int crf,string videoEncoder,string pixelFormat)
        :this(exe,dir,width,height,frameRate,rate,channelCount,crf,videoEncoder,pixelFormat,frameRate){}
    public Encoder(string exe, string dir, int width, int height, int frameRate, int rate, int channelCount, int crf,string videoEncoder,string pixelFormat,int inputFrameRate)
    {
        if (!File.Exists(exe)) throw new FileNotFoundException("未找到 FFmpeg。", exe);
        if (width < 2 || height < 2 || width % 2 != 0 || height % 2 != 0) throw new ArgumentException("录制宽高必须为偶数。");
        if(frameRate<1 || rate<1 || channelCount<1)throw new ArgumentOutOfRangeException("录制帧率和音频格式必须为正数。");
        if(inputFrameRate<1)throw new ArgumentOutOfRangeException(nameof(inputFrameRate));
        ffmpeg = exe; directory = dir; Width = width; Height = height; fps = frameRate; sampleRate = rate; channels = channelCount;
        InputFps=inputFrameRate;
        if(pixelFormat!="rgb24" && pixelFormat!="rgba")throw new ArgumentException("Unsupported capture pixel format.");
        frameBytes=width*height*(pixelFormat=="rgba"?4:3);VideoEncoder=videoEncoder;
        // Three waiting pictures plus the writer's current picture, matching
        // the old image-only queue depth. PCM also counts the active write;
        // twelve seconds permits a valid ten-second native interval plus margin.
        queue=new(3,checked(12L*sampleRate*channels*sizeof(float)));
        Directory.CreateDirectory(dir);
        audio = new FileStream(Path.Combine(dir, "audio.f32"), FileMode.CreateNew, FileAccess.Write, FileShare.Read);
        try
        {
            var args=new List<string>{"-hide_banner","-loglevel","warning","-nostdin","-n","-f","rawvideo",
                "-pixel_format",pixelFormat,"-video_size",width+"x"+height,"-framerate",InputFps.ToString(),"-i","pipe:0","-an","-vf",
                InputFps==OutputFps?"vflip":"vflip,fps="+OutputFps.ToString(CultureInfo.InvariantCulture)+":start_time=0:round=up:eof_action=pass"};
            args.AddRange(CodecArguments(videoEncoder,crf));
            args.AddRange(new[]{"-color_primaries","bt709","-color_trc","bt709","-colorspace","bt709",Path.Combine(dir,"video.partial.mp4")});
            process=Start(exe,args.ToArray());
            stderr = process.StandardError.ReadToEndAsync();
            writer=Task.Run(Pump);
        }
        catch { audio.Dispose(); throw; }
    }

    // The input spans [0,inputFrames/inputFps). With an origin of zero and
    // upward EOF rounding, output PTS k/outputFps must be strictly before its
    // end. Divide first to keep even very long recordings free of overflow.
    public static long OutputFrameCount(long inputFrames,int inputFps,int outputFps)
    {
        if(inputFrames<0)throw new ArgumentOutOfRangeException(nameof(inputFrames));
        if(inputFps<1)throw new ArgumentOutOfRangeException(nameof(inputFps));
        if(outputFps<1)throw new ArgumentOutOfRangeException(nameof(outputFps));
        long whole=inputFrames/inputFps,remaining=inputFrames%inputFps;
        long partial=(remaining*outputFps+inputFps-1)/inputFps;
        return checked(whole*outputFps+partial);
    }

    public void WriteFrame(byte[] rgb, float[] samples, int repeatCount=1)
    {
        if (rgb.Length != frameBytes) throw new ArgumentException("画面尺寸在录制中改变。");
        Enqueue((byte[])rgb.Clone(),(float[])samples.Clone(),false,repeatCount);
    }
    public void WriteAudio(float[] samples)
    {
        if(finished)throw new InvalidOperationException("录制已结束。");
        if(writer.IsFaulted)writer.GetAwaiter().GetResult();
        if(samples.Length==0)return;
        Enqueue(null,(float[])samples.Clone(),false,0);
    }
    internal void WritePooledFrame(byte[] rgb,float[] samples,int repeatCount=1)=>Enqueue(rgb,samples,true,repeatCount);
    void Enqueue(byte[]? rgb,float[] samples,bool pooled,int repeatCount)
    {
        bool accepted=false;
        try
        {
        if (finished) throw new InvalidOperationException("录制已结束。");
        if(writer.IsFaulted)writer.GetAwaiter().GetResult();
        if(repeatCount<0)throw new ArgumentOutOfRangeException(nameof(repeatCount));
        if(rgb==null && repeatCount!=0)throw new ArgumentException("缺少画面缓冲区。");
        if(rgb!=null && rgb.Length<frameBytes)throw new ArgumentException("画面缓冲区太短。");
        if (samples.Length % channels != 0) throw new ArgumentException("音频样本未对齐。");
        queue.Add((rgb,samples,pooled,repeatCount),rgb!=null,checked((long)samples.Length*sizeof(float)));
        accepted=true;
        AudioSamples += samples.Length / channels;
        InputFrames+=repeatCount;
        }
        finally{if(pooled&&!accepted&&rgb!=null)ArrayPool<byte>.Shared.Return(rgb);}
    }
    void Pump()
    {
        try
        {
            while(queue.TryTake(out var item))
            {
                var frame=item.Value;
                Task? pendingWrite=null;
                try
                {
                    // One owned source snapshot may span several CFR output
                    // frames. Its PCM block belongs to the simulation step and
                    // must be written once, including audio-only queue entries.
                    for(int repeat=0;repeat<frame.RepeatCount;repeat++)
                    {
                        pendingWrite=process.StandardInput.BaseStream.WriteAsync(frame.Pixels!,0,frameBytes);
                        if(!pendingWrite.Wait(TimeSpan.FromSeconds(30)))throw new TimeoutException("编码器 30 秒无响应。");
                        pendingWrite=null;
                    }
                    audio.Write(System.Runtime.InteropServices.MemoryMarshal.AsBytes(frame.Samples.AsSpan()));
                }
                finally
                {
                    queue.Complete(item);
                    if(frame.Pooled&&frame.Pixels!=null)
                    {
                        if(pendingWrite!=null && !pendingWrite.IsCompleted)
                        {
                            // A timed-out pipe write may still reference this
                            // array. Return it only after the write has settled.
                            byte[] pixels=frame.Pixels;
                            _=pendingWrite.ContinueWith(completed=>
                            {
                                if(completed.IsFaulted)_=completed.Exception;
                                ArrayPool<byte>.Shared.Return(pixels);
                            },System.Threading.CancellationToken.None,TaskContinuationOptions.ExecuteSynchronously,TaskScheduler.Default);
                        }
                        else ArrayPool<byte>.Shared.Return(frame.Pixels);
                    }
                }
            }
        }
        catch(Exception error){queue.Fail(error);throw;}
        finally{DiscardQueuedFrames();}
    }

    void DiscardQueuedFrames()
    {
        // TryDiscard transfers each waiting buffer to exactly one caller. The
        // active item is owned exclusively by Pump's finally block.
        while(queue.TryDiscard(out var left))
            if(left.Pooled&&left.Pixels!=null)ArrayPool<byte>.Shared.Return(left.Pixels);
    }

    public async Task<string> FinishAsync()
    {
        if (finished) throw new InvalidOperationException("编码器已经关闭。");
        finished = true;
        queue.CompleteAdding();
        try{await writer;}catch{Abort();throw;}
        audio.Dispose();
        process.StandardInput.Close();
        using var deadline = new System.Threading.CancellationTokenSource(TimeSpan.FromMinutes(5));
        try { await process.WaitForExitAsync(deadline.Token); }
        catch { if (!process.HasExited) process.Kill(true); throw; }
        var log = await stderr;
        File.WriteAllText(Path.Combine(directory, "encode.log"), log);
        if (process.ExitCode != 0 || Frames == 0 || AudioSamples == 0)
            throw new IOException("录制未生成完整音视频，请检查 encode.log；临时文件已保留。" + log);
        double seconds = VideoSeconds;
        if (Math.Abs((double)AudioSamples / sampleRate - seconds) > 0.15)
            throw new IOException("内部音视频时钟偏差超过 150ms，保留分离素材，未发布有误的 MP4。");
        var final = Path.Combine(directory, "recording.mp4");
        using var mux = Start(ffmpeg, "-hide_banner", "-loglevel", "warning", "-nostdin", "-n",
            "-i", Path.Combine(directory, "video.partial.mp4"), "-f", "f32le", "-ar", sampleRate.ToString(),
            "-ac", channels.ToString(), "-i", Path.Combine(directory, "audio.f32"),
            "-map", "0:v:0", "-map", "1:a:0", "-c:v", "copy", "-c:a", "aac", "-b:a", "256k",
            "-af", "apad", "-t", seconds.ToString("0.########", CultureInfo.InvariantCulture), "-movflags", "+faststart",
            Path.Combine(directory, "mux.partial.mp4"));
        mux.StandardInput.Close();
        var muxLog = mux.StandardError.ReadToEndAsync();
        try { await mux.WaitForExitAsync(deadline.Token); }
        catch { if (!mux.HasExited) mux.Kill(true); throw; }
        File.WriteAllText(Path.Combine(directory, "mux.log"), await muxLog);
        if (mux.ExitCode != 0) throw new IOException("MP4 音视频封装失败，请检查 mux.log。");
        File.Move(Path.Combine(directory, "mux.partial.mp4"), final, false);
        // Publication succeeded. A viewer/scanner may still hold a temporary
        // input open: retain it instead of reporting a valid MP4 as failed.
        CleanupTemporaryFile("video.partial.mp4");
        CleanupTemporaryFile("audio.f32");
        return final;
    }

    void CleanupTemporaryFile(string name)
    {
        try { File.Delete(Path.Combine(directory,name)); }
        catch(Exception error) when(error is IOException || error is UnauthorizedAccessException)
        {
            // Only these two files in this unique job are cleanup candidates.
            // Diagnostic logging must not turn successful publication into failure.
            try { File.AppendAllText(Path.Combine(directory,"cleanup.log"),
                DateTime.UtcNow.ToString("O",CultureInfo.InvariantCulture)+" Retained "+name+": "+error.GetType().Name+": "+error.Message+Environment.NewLine); }
            catch { }
        }
    }

    public void Abort()
    {
        finished = true;
        queue.Abort();
        try { if (!process.HasExited) process.Kill(true); } catch { }
        try{writer.Wait(TimeSpan.FromSeconds(5));}catch{}
        DiscardQueuedFrames();
        audio.Dispose();
    }
    public void Dispose() { if (!finished) Abort(); process.Dispose(); }

    static string[] CodecArguments(string codec,int quality)=>codec switch
    {
        "h264_qsv"=>new[]{"-c:v",codec,"-preset","veryfast","-global_quality",Math.Max(1,quality).ToString(),"-look_ahead","0","-bf","0","-pix_fmt","nv12"},
        "h264_nvenc"=>new[]{"-c:v",codec,"-preset","p1","-rc","vbr","-cq",quality.ToString(),"-b:v","0","-bf","0","-pix_fmt","yuv420p"},
        "h264_amf"=>new[]{"-c:v",codec,"-quality","speed","-rc","cqp","-qp_i",quality.ToString(),"-qp_p",quality.ToString(),"-pix_fmt","nv12"},
        "libx264"=>new[]{"-c:v",codec,"-preset","ultrafast","-crf",quality.ToString(),"-pix_fmt","yuv420p"},
        _=>throw new ArgumentException("Unknown video encoder.")
    };
    public static string SelectHardwareEncoder(string exe,int width,int height,int fps,int quality,string directory)
    {
        if(quality==0)return "libx264";
        GpuInfo[] devices;
        try{devices=GpuProfiles.Detect().ToArray();}catch(Exception ex){File.AppendAllText(Path.Combine(directory,"encoder-selection.log"),ex.Message);return "libx264";}
        var codecs=devices.Select(d=>d.VendorId switch{0x8086=>"h264_qsv",0x10DE=>"h264_nvenc",0x1002=>"h264_amf",_=>""}).Where(x=>x.Length>0).Distinct();
        foreach(var codec in codecs)
        {
            try
            {
                var args=new List<string>{"-hide_banner","-loglevel","error","-nostdin","-f","lavfi","-i",$"color=black:s={width}x{height}:r={fps}","-frames:v","3","-an"};
                args.AddRange(CodecArguments(codec,quality));args.AddRange(new[]{"-f","null","-"});
                using var probe=Start(exe,args.ToArray());probe.StandardInput.Close();var errors=probe.StandardError.ReadToEndAsync();
                if(!probe.WaitForExit(10000)){probe.Kill(true);probe.WaitForExit();}
                File.AppendAllText(Path.Combine(directory,"encoder-selection.log"),$"{codec}: exit={probe.ExitCode}\n{errors.GetAwaiter().GetResult()}\n");
                if(probe.ExitCode==0)return codec;
            }
            catch(Exception ex){File.AppendAllText(Path.Combine(directory,"encoder-selection.log"),$"{codec}: {ex.Message}\n");}
        }
        return "libx264";
    }
}
