using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using AzureArchive.Recorder;

// Hardware discovery is outside this CPU-only cleanup regression. Fail if the
// production encoder unexpectedly reaches it; FFmpeg encoding/muxing is real.
namespace AzureArchive.Recorder
{
    public sealed class GpuInfo { public uint VendorId { get; set; } }
    public static class GpuProfiles
    {
        public static IEnumerable<GpuInfo> Detect()=>throw new InvalidOperationException("Cleanup tests must not probe GPU or DLSS.");
    }
}

public static class EncoderCleanupTests
{
    static int checks;
    static readonly List<object> cases=new();
    static void Check(bool success,string message)
    {
        if(!success)throw new Exception(message);
        checks++;Console.WriteLine("PASS "+checks+": "+message);
    }

    public static void Run(string root,string ffmpeg,string ffprobe,string sourceSha256)
    {
        if(!OperatingSystem.IsWindows())throw new PlatformNotSupportedException("This regression requires Windows delete-sharing semantics.");
        if(Directory.Exists(root))throw new IOException("Refusing to reuse test output: "+root);
        Directory.CreateDirectory(root);
        checks=0;cases.Clear();
        string? failure=null;
        try
        {
            RunCase(root,ffmpeg,ffprobe,"normal",false,false,false,false);
            RunCase(root,ffmpeg,ffprobe,"locked-video",true,false,false,false);
            RunCase(root,ffmpeg,ffprobe,"readonly-audio",false,true,false,false);
            RunCase(root,ffmpeg,ffprobe,"locked-video-log-unwritable",true,false,true,false);
            RunCase(root,ffmpeg,ffprobe,"publication-collision",false,false,false,true);
        }
        catch(Exception error){failure=error.ToString();throw;}
        finally
        {
            File.WriteAllText(Path.Combine(root,"results.json"),JsonSerializer.Serialize(new{
                passed=failure==null,checks,encoderSourceSha256=sourceSha256,ffmpeg,ffprobe,
                cpuOnly=true,aaStarted=false,dlssUsed=false,cases=cases.ToArray(),failure
            },new JsonSerializerOptions{WriteIndented=true}));
        }
    }

    static void RunCase(string root,string ffmpeg,string ffprobe,string name,bool lockVideo,bool readonlyAudio,bool blockLog,bool collision)
    {
        string dir=Path.Combine(root,name),video=Path.Combine(dir,"video.partial.mp4"),audio=Path.Combine(dir,"audio.f32");
        string final=Path.Combine(dir,"recording.mp4"),cleanupLog=Path.Combine(dir,"cleanup.log");
        const int frames=30,width=64,height=48,fps=30,rate=48000;
        using var encoder=new Encoder(ffmpeg,dir,width,height,fps,rate,2,18,"libx264","rgb24");
        var pixels=new byte[width*height*3];
        for(int y=0;y<height;y++)for(int x=0;x<width;x++)
        {int at=(y*width+x)*3;pixels[at]=(byte)(x*3);pixels[at+1]=(byte)(y*4);pixels[at+2]=180;}
        for(int frame=0;frame<frames;frame++)
        {
            var sound=new float[1600*2];
            for(int sample=0;sample<1600;sample++)
                sound[2*sample]=sound[2*sample+1]=(float)(.1*Math.Sin(2*Math.PI*440*(frame*1600+sample)/rate));
            encoder.WriteFrame(pixels,sound);
        }
        FileStream? held=null;
        try
        {
            if(lockVideo)
            {
                var deadline=Stopwatch.StartNew();
                while(!File.Exists(video) && deadline.Elapsed.TotalSeconds<10)Thread.Sleep(10);
                // Encoding can continue and mux can read. Omit FileShare.Delete.
                held=new FileStream(video,FileMode.Open,FileAccess.Read,FileShare.ReadWrite);
            }
            if(readonlyAudio)File.SetAttributes(audio,File.GetAttributes(audio)|FileAttributes.ReadOnly);
            if(blockLog)Directory.CreateDirectory(cleanupLog);
            if(collision)File.WriteAllText(final,"existing user video sentinel");

            if(collision)
            {
                Exception? rejection=null;
                try{encoder.FinishAsync().GetAwaiter().GetResult();}catch(IOException error){rejection=error;}
                Check(rejection!=null,"Publication collision still fails; cleanup handling does not hide File.Move failure");
                Check(File.ReadAllText(final)=="existing user video sentinel" && File.Exists(video) && File.Exists(audio) && File.Exists(Path.Combine(dir,"mux.partial.mp4")),
                    "Failed publication preserves the existing destination and all temporary inputs");
                Check(!File.Exists(cleanupLog),"Cleanup is not attempted before successful publication");
                cases.Add(new{name,passed=true,publicationRejected=true,temporaryFilesRetained=true});
                return;
            }

            string result=encoder.FinishAsync().GetAwaiter().GetResult();
            Check(result==final && File.Exists(final),"FinishAsync succeeds after mux/publication: "+name);
            Check(encoder.Frames==frames && encoder.AudioSamples==rate,"Source frame and sample counts are intact: "+name);
            VerifyVideo(ffmpeg,ffprobe,final,dir,frames);
            Check(File.Exists(video)==lockVideo,"Video temporary cleanup/retention matches file ownership lock: "+name);
            Check(File.Exists(audio)==readonlyAudio,"Audio temporary cleanup/retention matches access restriction: "+name);
            if(lockVideo)
            {
                IOException? locked=null;
                try{File.Delete(video);}catch(IOException error){locked=error;}
                Check(held!=null && locked!=null && (locked.HResult&0xffff)==32 && File.Exists(video),
                    "Held FileShare.ReadWrite lock really denies deletion after successful mux: "+name);
            }
            if(readonlyAudio)
            {
                UnauthorizedAccessException? denied=null;
                try{File.Delete(audio);}catch(UnauthorizedAccessException error){denied=error;}
                Check(denied!=null && File.Exists(audio),"Read-only audio really raises UnauthorizedAccessException after successful mux");
            }
            if(blockLog)Check(Directory.Exists(cleanupLog),"Unwritable cleanup log cannot fail a completed video");
            else if(lockVideo || readonlyAudio)
            {
                string log=File.ReadAllText(cleanupLog);
                Check(log.Contains(lockVideo?"video.partial.mp4":"audio.f32") && log.Contains(lockVideo?"IOException":"UnauthorizedAccessException"),
                    "Retained temporary input is recorded in cleanup.log: "+name);
            }
            else Check(!File.Exists(cleanupLog),"Ordinary successful cleanup produces no warning log");
            cases.Add(new{name,passed=true,output=final,frames,seconds=1.0,lockVideo,readonlyAudio,blockLog,videoRetained=File.Exists(video),audioRetained=File.Exists(audio)});
        }
        finally
        {
            held?.Dispose();
            // Leave evidence files, but remove the test-only read-only attribute.
            if(readonlyAudio && File.Exists(audio))File.SetAttributes(audio,File.GetAttributes(audio)&~FileAttributes.ReadOnly);
        }
    }

    static void VerifyVideo(string ffmpeg,string ffprobe,string final,string dir,int expectedFrames)
    {
        string json=RunTool(ffprobe,"-v","error","-count_frames","-show_streams","-show_format","-of","json",final);
        File.WriteAllText(Path.Combine(dir,"ffprobe.json"),json);
        using var doc=JsonDocument.Parse(json);
        var streams=doc.RootElement.GetProperty("streams").EnumerateArray().ToArray();
        var video=streams.Single(stream=>stream.GetProperty("codec_type").GetString()=="video");
        var audio=streams.Single(stream=>stream.GetProperty("codec_type").GetString()=="audio");
        double duration=double.Parse(doc.RootElement.GetProperty("format").GetProperty("duration").GetString()!,CultureInfo.InvariantCulture);
        Check(video.GetProperty("codec_name").GetString()=="h264" && video.GetProperty("nb_read_frames").GetString()==expectedFrames.ToString(CultureInfo.InvariantCulture)
            && video.GetProperty("avg_frame_rate").GetString()=="30/1" && audio.GetProperty("codec_name").GetString()=="aac" && Math.Abs(duration-1)<.05,
            "Real ffprobe validates H.264/AAC, 30 frames and 1 second: "+Path.GetFileName(dir));
        RunTool(ffmpeg,"-v","error","-nostdin","-i",final,"-f","null","-");
        Check(true,"Full real FFmpeg video/audio decode succeeds: "+Path.GetFileName(dir));
    }

    static string RunTool(string executable,params string[] arguments)
    {
        var start=new ProcessStartInfo(executable){UseShellExecute=false,CreateNoWindow=true,WindowStyle=ProcessWindowStyle.Hidden,
            RedirectStandardOutput=true,RedirectStandardError=true};
        foreach(string argument in arguments)start.ArgumentList.Add(argument);
        using var process=Process.Start(start)??throw new IOException("Could not start "+executable);
        var stdout=process.StandardOutput.ReadToEndAsync();var stderr=process.StandardError.ReadToEndAsync();
        if(!process.WaitForExit(30000)){process.Kill(true);process.WaitForExit();throw new TimeoutException("Media verification exceeded 30 seconds.");}
        string output=stdout.GetAwaiter().GetResult(),error=stderr.GetAwaiter().GetResult();
        if(process.ExitCode!=0)throw new IOException(Path.GetFileName(executable)+" failed: "+error);
        return output;
    }
}
