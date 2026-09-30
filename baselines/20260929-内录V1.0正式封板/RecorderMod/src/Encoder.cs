using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using System.Buffers;
using System.Collections.Concurrent;
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
    readonly BlockingCollection<(byte[] Pixels,float[] Samples,bool Pooled)> queue=new(3);
    readonly Task writer;
    bool finished;
    public long Frames { get; private set; }
    public long AudioSamples { get; private set; }
    public int Width { get; }
    public int Height { get; }
    public string VideoEncoder { get; }

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
    {
        if (!File.Exists(exe)) throw new FileNotFoundException("未找到 FFmpeg。", exe);
        if (width < 2 || height < 2 || width % 2 != 0 || height % 2 != 0) throw new ArgumentException("录制宽高必须为偶数。");
        ffmpeg = exe; directory = dir; Width = width; Height = height; fps = frameRate; sampleRate = rate; channels = channelCount;
        if(pixelFormat!="rgb24" && pixelFormat!="rgba")throw new ArgumentException("Unsupported capture pixel format.");
        frameBytes=width*height*(pixelFormat=="rgba"?4:3);VideoEncoder=videoEncoder;
        Directory.CreateDirectory(dir);
        audio = new FileStream(Path.Combine(dir, "audio.f32"), FileMode.CreateNew, FileAccess.Write, FileShare.Read);
        try
        {
            var args=new List<string>{"-hide_banner","-loglevel","warning","-nostdin","-n","-f","rawvideo",
                "-pixel_format",pixelFormat,"-video_size",width+"x"+height,"-framerate",fps.ToString(),"-i","pipe:0","-an","-vf","vflip"};
            args.AddRange(CodecArguments(videoEncoder,crf));
            args.AddRange(new[]{"-color_primaries","bt709","-color_trc","bt709","-colorspace","bt709",Path.Combine(dir,"video.partial.mp4")});
            process=Start(exe,args.ToArray());
            stderr = process.StandardError.ReadToEndAsync();
            writer=Task.Run(Pump);
        }
        catch { audio.Dispose(); throw; }
    }

    public void WriteFrame(byte[] rgb, float[] samples)
    {
        if (rgb.Length != frameBytes) throw new ArgumentException("画面尺寸在录制中改变。");
        Enqueue((byte[])rgb.Clone(),(float[])samples.Clone(),false);
    }
    internal void WritePooledFrame(byte[] rgb,float[] samples)=>Enqueue(rgb,samples,true);
    void Enqueue(byte[] rgb,float[] samples,bool pooled)
    {
        bool accepted=false;
        try
        {
        if (finished) throw new InvalidOperationException("录制已结束。");
        if(writer.IsFaulted)writer.GetAwaiter().GetResult();
        if(rgb.Length<frameBytes)throw new ArgumentException("画面缓冲区太短。");
        if (samples.Length % channels != 0) throw new ArgumentException("音频样本未对齐。");
        var clock=Stopwatch.StartNew();
        while(!queue.TryAdd((rgb,samples,pooled),50))
        {
            if(writer.IsFaulted)writer.GetAwaiter().GetResult();
            if(clock.Elapsed.TotalSeconds>30)throw new TimeoutException("编码器 30 秒无响应。");
        }
        accepted=true;
        AudioSamples += samples.Length / channels;
        Frames++;
        }
        finally{if(pooled&&!accepted)ArrayPool<byte>.Shared.Return(rgb);}
    }
    void Pump()
    {
        try
        {
            foreach(var frame in queue.GetConsumingEnumerable())
            {
                try
                {
                    var write=process.StandardInput.BaseStream.WriteAsync(frame.Pixels,0,frameBytes);
                    if(!write.Wait(TimeSpan.FromSeconds(30)))throw new TimeoutException("编码器 30 秒无响应。");
                    audio.Write(System.Runtime.InteropServices.MemoryMarshal.AsBytes(frame.Samples.AsSpan()));
                }
                finally{if(frame.Pooled)ArrayPool<byte>.Shared.Return(frame.Pixels);}
            }
        }
        finally{while(queue.TryTake(out var left))if(left.Pooled)ArrayPool<byte>.Shared.Return(left.Pixels);}
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
        double seconds = (double)Frames / fps;
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
        // Only files created inside this unique job are removed, after publication.
        File.Delete(Path.Combine(directory, "video.partial.mp4"));
        File.Delete(Path.Combine(directory, "audio.f32"));
        return final;
    }

    public void Abort()
    {
        finished = true;
        queue.CompleteAdding();
        try { if (!process.HasExited) process.Kill(true); } catch { }
        try{writer.Wait(TimeSpan.FromSeconds(5));}catch{}
        while(queue.TryTake(out var left))if(left.Pooled)ArrayPool<byte>.Shared.Return(left.Pixels);
        audio.Dispose();
    }
    public void Dispose() { if (!finished) Abort(); process.Dispose();if(writer.IsCompleted)queue.Dispose(); }

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
