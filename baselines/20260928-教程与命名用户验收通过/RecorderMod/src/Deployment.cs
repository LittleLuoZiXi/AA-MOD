using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using BepInEx;
namespace AzureArchive.Recorder;

internal static class Deployment
{
    internal static string Root=>AppContext.GetData("AzureArchive.Recorder.RuntimeRoot") as string ?? Path.Combine(Paths.GameRootPath,"RecorderMod");
    internal static string Enhancer=>Path.Combine(Root,"EnhanceHost.exe");
    internal static string BundledFfmpeg=>Path.Combine(Paths.GameRootPath,"mods","AzureArchiveRecorder","runtime","ffmpeg","ffmpeg.exe");
    internal static string ResolveFfmpeg(string configured)
    {
        // Re-resolve the private tool relative to the current game directory after a move.
        foreach(var candidate in new[]{configured,BundledFfmpeg,Path.Combine(Paths.GameRootPath,"..","DLSS5Tool-v2.3.3-win64","_internal","ffmpeg.EXE")})
        {
            try
            {
                var value=Environment.ExpandEnvironmentVariables(candidate.Trim().Trim('"'));
                if(value.Length==0)continue;
                var path=Path.GetFullPath(Path.IsPathRooted(value)?value:Path.Combine(Paths.GameRootPath,value));
                if(File.Exists(path))return path;
            }
            catch(ArgumentException){}catch(NotSupportedException){}
        }
        throw new FileNotFoundException("内置 FFmpeg 缺失，请运行 R4 或更新版内录安装包，选择覆盖重新安装。",BundledFfmpeg);
    }
    internal static Process StartEnhancement(string python,string job)
    {
        bool frozen=File.Exists(Enhancer);
        var start=new ProcessStartInfo(frozen?Enhancer:python){UseShellExecute=false,CreateNoWindow=true,RedirectStandardInput=true,RedirectStandardError=true,RedirectStandardOutput=true,
            StandardErrorEncoding=Encoding.UTF8,StandardOutputEncoding=Encoding.UTF8};
        if(!frozen){start.ArgumentList.Add("-u");start.ArgumentList.Add(Path.Combine(Root,"bridge","enhance.py"));}
        start.ArgumentList.Add(job);
        start.Environment["PYTHONIOENCODING"]="utf-8";
        start.Environment["AA_RECORDER_HOST_PID"]=Environment.ProcessId.ToString();
        start.Environment["AA_RECORDER_HOST_EXE"]=Path.Combine(Paths.GameRootPath,"AzureArchive.exe");
        return Process.Start(start)??throw new IOException("增强组件启动失败。");
    }
}
