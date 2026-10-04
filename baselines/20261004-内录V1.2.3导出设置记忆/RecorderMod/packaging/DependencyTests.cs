using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;

public static class DependencyTests
{
    static string root,installer,payload;static int count;
    static string P(string value){return Path.Combine(root,value.Replace('/',Path.DirectorySeparatorChar));}
    static void Put(string value,string text){var file=P(value);Directory.CreateDirectory(Path.GetDirectoryName(file));File.WriteAllText(file,text);}
    static void Check(bool condition,string message){if(!condition)throw new Exception(message);}
    static void Pass(string message){count++;Console.WriteLine("PASS "+count+": "+message);}
    static Dictionary<string,string> Snapshot(){var values=Directory.GetFiles(root,"*",SearchOption.AllDirectories).ToDictionary(f=>"F:"+f,f=>InstallCore.Hash(f));foreach(var dir in Directory.GetDirectories(root,"*",SearchOption.AllDirectories))values.Add("D:"+dir,"");return values;}
    static void Unchanged(Dictionary<string,string> before){var after=Snapshot();Check(before.Count==after.Count && before.All(x=>after.ContainsKey(x.Key)&&after[x.Key]==x.Value),"Cancelled installer modified files");}
    static IntPtr FindWindow(Process process,string title)
    {
        var timer=Stopwatch.StartNew();IntPtr result=IntPtr.Zero;
        while(result==IntPtr.Zero&&!process.HasExited&&timer.Elapsed.TotalSeconds<30){
            EnumWindows(delegate(IntPtr h,IntPtr p){uint pid;GetWindowThreadProcessId(h,out pid);if(pid==process.Id&&Text(h)==title)result=h;return true;},IntPtr.Zero);
            if(result==IntPtr.Zero)Thread.Sleep(80);
        }
        Check(result!=IntPtr.Zero,"Expected popup missing: "+title);return result;
    }
    static string Text(IntPtr handle){var value=new StringBuilder(16000);GetWindowText(handle,value,value.Capacity);return value.ToString();}
    static List<IntPtr> Children(IntPtr window){var values=new List<IntPtr>();EnumChildWindows(window,delegate(IntPtr h,IntPtr p){values.Add(h);return true;},IntPtr.Zero);return values;}
    static void Click(IntPtr dialog,string label){var timer=Stopwatch.StartNew();IntPtr button=IntPtr.Zero;while(button==IntPtr.Zero&&timer.Elapsed.TotalSeconds<10){button=Children(dialog).FirstOrDefault(h=>Text(h)==label);if(button==IntPtr.Zero)Thread.Sleep(80);}Check(button!=IntPtr.Zero,"Missing button: "+label);PostMessage(button,0x00F5,IntPtr.Zero,IntPtr.Zero);}
    static Process Start(){var process=Process.Start(new ProcessStartInfo(installer,"--quiet --root \""+root+"\""){UseShellExecute=false,CreateNoWindow=true});Console.WriteLine("Test installer PID: "+process.Id);return process;}
    static void Exit(Process process,int code){Check(process.WaitForExit(120000),"Installer did not exit");Check(process.ExitCode==code,"Unexpected exit: "+process.ExitCode);}
    static void ExitWithoutFfmpegPrompt(Process process,int code)
    {
        var timer=Stopwatch.StartNew();
        while(!process.WaitForExit(100)&&timer.Elapsed.TotalSeconds<120){
            IntPtr prompt=IntPtr.Zero;EnumWindows(delegate(IntPtr h,IntPtr p){uint pid;GetWindowThreadProcessId(h,out pid);if(pid==process.Id&&Text(h)=="安装内置 FFmpeg")prompt=h;return true;},IntPtr.Zero);
            if(prompt!=IntPtr.Zero){Click(prompt,"取消安装");Exit(process,2);throw new Exception("Existing valid FFmpeg unexpectedly triggered the bundled-install confirmation");}
        }
        Check(process.HasExited&&process.ExitCode==code,"Installer did not finish without a redundant FFmpeg prompt");
    }
    [STAThread] public static int Main(string[] args)
    {
        try {
            var game=Path.GetFullPath(args[0]);installer=Path.GetFullPath(args[1]);payload=Path.GetFullPath(args[2]);
            root=Path.Combine(Directory.GetParent(game).FullName,"R6安装测试","deps-"+DateTime.Now.ToString("HHmmss"),"AA 依赖测试");Directory.CreateDirectory(root);
            File.Copy(Path.Combine(game,"AzureArchive.exe"),P("AzureArchive.exe"));Directory.CreateDirectory(P("AzureArchive_Data"));
            Put("ActiveProfile.txt","Test");Put("profiles/Test/modconfig.json","{\"EnabledMods\":[{\"name\":\"OtherMod\",\"version\":\"1.0\"}]}");
            Put("RecorderMod/src/source.cs","PRESERVE SOURCE");Put("mods/OtherMod/keep.dll","PRESERVE OTHER MOD");Put("Recordings/user.mp4","PRESERVE VIDEO");
            var issues=InstallCore.DetectDependencies(root);var names=String.Join("\n",issues.Select(x=>x.Name));
            foreach(var name in new[]{"BepInEx","AA MOD 管理器",".NET","RTX 超分","DLSS 补帧"})Check(names.Contains(name),"Missing dependency not reported: "+name);
            Check(!issues.Any(x=>x.Name.StartsWith("FFmpeg")),"Bundled ordinary-recording FFmpeg incorrectly reported missing");
            Check(!names.Contains("Python"),"Bundled Python incorrectly required");Pass("Framework and optional enhancement dependencies are listed; bundled FFmpeg is not a missing prerequisite");
            using(var dialog=new DependencyDialog(issues))Check(((Button)dialog.AcceptButton).Text=="取消安装","Missing dependencies should default to cancel");
            using(var dialog=new ReinstallDialog(new List<string>{"0.2.1"}))Check(((Button)dialog.AcceptButton).Text=="取消安装","Reinstall should default to cancel");
            using(var dialog=new FfmpegInstallDialog())Check(((Button)dialog.AcceptButton).Text=="取消安装","Bundled FFmpeg install should default to cancel");
            using(var dialog=new DlssInstallDialog(issues.Where(x=>x.OptionalDlss).ToList()))Check(((Button)dialog.AcceptButton).Text=="取消安装","DLSS should default to cancel");Pass("Dependency, DLSS, reinstall, and bundled-FFmpeg confirmation dialogs all default to cancel");
            var before=Snapshot();using(var proc=Start()){
                Click(FindWindow(proc,"DLSS组件缺失"),"安装");
                var dialog=FindWindow(proc,"依赖检测：缺少组件");var body=String.Join("\n",Children(dialog).Select(Text));
                Check(body.Contains("BepInEx")&&!body.Contains("增强可选")&&!body.Contains("缺失时无法编码和导出 MP4"),"Actual dialog has stale, duplicate, or omitted dependency details");
                Click(dialog,"取消安装");Exit(proc,2);
            }Unchanged(before);Pass("Real dependency popup explains impact; cancel exits without writing files");
            before=Snapshot();using(var proc=Start()){
                Click(FindWindow(proc,"DLSS组件缺失"),"安装");
                Click(FindWindow(proc,"依赖检测：缺少组件"),"继续安装");var dialog=FindWindow(proc,"安装内置 FFmpeg");
                Click(dialog,"取消安装");Exit(proc,2);
            }Unchanged(before);Pass("Real quiet-mode installer asks before installing missing FFmpeg; declining changes no files");
            using(var proc=Start()){Click(FindWindow(proc,"DLSS组件缺失"),"安装");Click(FindWindow(proc,"依赖检测：缺少组件"),"继续安装");Click(FindWindow(proc,"安装内置 FFmpeg"),"安装 FFmpeg 并继续");Exit(proc,0);}
            Check(File.Exists(P(InstallCore.ReceiptName))&&File.Exists(P(InstallCore.Mod+"runtime/ffmpeg/ffmpeg.exe")),"User-approved install did not deploy bundled FFmpeg");Pass("Affirmative FFmpeg consent installs MOD and bundled tools even before framework dependencies are present");
            Put("profiles/Test/configs/azurearchive.recorder.cfg","[Recording]\nFrameRate = 60\nFFmpegPath = "+P("custom encoder/ffmpeg.exe")+"\n[DLSS]\nToolDirectory = "+P("custom DLSS")+"\n");
            var config=P("profiles/Test/configs/azurearchive.recorder.cfg");var configHash=InstallCore.Hash(config);before=Snapshot();
            using(var proc=Start()){var dialog=FindWindow(proc,"重复安装确认");Check(String.Join("\n",Children(dialog).Select(Text)).Contains("0.2.1"),"Reinstall popup omitted installed version");Click(dialog,"取消安装");Exit(proc,2);}
            Unchanged(before);Pass("Actual repeat-install cancellation preserves complete installed tree and config");
            using(var proc=Start()){
                Click(FindWindow(proc,"重复安装确认"),"覆盖重新安装");Click(FindWindow(proc,"DLSS组件缺失"),"安装");Click(FindWindow(proc,"依赖检测：缺少组件"),"继续安装");ExitWithoutFfmpegPrompt(proc,0);
            }
            var repaired=File.ReadAllText(config);
            Check(repaired.Contains("FrameRate = 60")&&repaired.Contains("ToolDirectory = "+P("custom DLSS"))&&!repaired.Contains("FFmpegPath = "+P("custom encoder/ffmpeg.exe"))&&File.ReadAllText(P("profiles/Test/modconfig.json")).Contains("OtherMod"),"Reinstall did not repair missing FFmpeg or changed unrelated settings");Pass("Confirmed reinstall repairs an invalid FFmpeg path and preserves other settings and MOD entries");
            Pass("Existing valid bundled FFmpeg is reused without another installation prompt");
            Directory.CreateDirectory(P("custom encoder"));File.Copy(P(InstallCore.Mod+"runtime/ffmpeg/ffmpeg.exe"),P("custom encoder/ffmpeg.exe"));
            File.WriteAllText(config,InstallCore.UpdateFfmpegConfig(repaired,P("custom encoder/ffmpeg.exe")));configHash=InstallCore.Hash(config);
            Check(InstallCore.ProbeFfmpeg(P("custom encoder/ffmpeg.exe")),"Real configured encoder did not pass executable and H264/AAC checks");
            Check(InstallCore.FindFfmpeg(root,new[]{P("custom encoder/ffmpeg.exe")},false)==P("custom encoder/ffmpeg.exe"),"Configured FFmpeg candidate ignored");
            issues=InstallCore.DetectDependencies(root);Check(!issues.Any(x=>x.Name.StartsWith("FFmpeg")),"Bundled FFmpeg wrongly warned");Check(issues.Any(x=>x.Name.Contains("增强可选")),"Optional enhancement absence ignored");Pass("Real custom FFmpeg is usable; optional enhancement dependencies remain distinct");
            foreach(var file in new[]{"BepInEx/core/BepInEx.Core.dll","BepInEx/core/BepInEx.Unity.IL2CPP.dll","BepInEx/core/Il2CppInterop.Runtime.dll","BepInEx/patchers/ModTheAzureArchive.dll","winhttp.dll","dotnet/coreclr.dll","dotnet/hostpolicy.dll","dotnet/System.Private.CoreLib.dll"})Put(file,"dependency detection fixture");
            Put("doorstop_config.ini","[General]\nenabled = true\ntarget_assembly = BepInEx/core/BepInEx.Unity.IL2CPP.dll\n[Il2Cpp]\ncoreclr_path = dotnet/coreclr.dll\ncorlib_dir = dotnet\n");
            foreach(var file in new[]{"ffmpeg.EXE","ffprobe.exe","vsr_host.dll","nvngx_vsr.dll","dlssg_video_worker.exe","nvngx_dlssg.dll","dlssnr_host_v2.dll"})Put("custom DLSS/_internal/"+file,"dependency detection fixture");
            var remaining=InstallCore.DetectDependencies(root);Check(remaining.Count>0&&remaining.All(x=>x.OptionalDlss)&&remaining.Any(x=>x.Name.Contains("显卡运行库")),"Framework fixture must be complete but text files must not satisfy GPU runtime hashes");Pass("Complete framework and common-file fixtures still warn for unverified GPU runtime");
            using(var proc=Start()){Click(FindWindow(proc,"重复安装确认"),"覆盖重新安装");Click(FindWindow(proc,"DLSS组件缺失"),"安装");ExitWithoutFfmpegPrompt(proc,0);}Check(InstallCore.Hash(config)==configHash,"Complete reinstall changed config");Pass("Complete framework/external encoder skips redundant prompts while missing GPU runtime retains its own prompt");
            Put("doorstop_config.ini","[General]\nenabled = false\n");Check(InstallCore.DetectDependencies(root).Any(x=>x.Name.Contains("MOD 启动入口")),"Disabled bootstrap not reported");Pass("Installed but disabled MOD bootstrap is detected");
            var keep=new[]{"RecorderMod/src/source.cs","mods/OtherMod/keep.dll","Recordings/user.mp4","custom encoder/ffmpeg.exe"}.ToDictionary(x=>x,x=>InstallCore.Hash(P(x)));
            InstallCore.Uninstall(root,delegate{});foreach(var file in keep)Check(InstallCore.Hash(P(file.Key))==file.Value,"Uninstall touched unrelated file");Check(!File.Exists(P(InstallCore.ReceiptName))&&!File.Exists(P(InstallCore.Mod+"runtime/ffmpeg/ffmpeg.exe")),"Uninstall did not remove owned receipt/FFmpeg");Pass("Uninstall removes private FFmpeg and preserves source/other MOD/video/external tools");
            File.WriteAllText(Path.Combine(Directory.GetParent(root).FullName,"PASS.txt"),"ALL "+count+" CHECKS PASSED");Console.WriteLine("ALL "+count+" CHECKS PASSED: "+root);return 0;
        }catch(Exception ex){Console.Error.WriteLine(ex);return 1;}
    }
    delegate bool Callback(IntPtr h,IntPtr p);
    [DllImport("user32.dll")] static extern bool EnumWindows(Callback callback,IntPtr p);
    [DllImport("user32.dll")] static extern bool EnumChildWindows(IntPtr h,Callback callback,IntPtr p);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h,out uint pid);
    [DllImport("user32.dll",CharSet=CharSet.Unicode)] static extern int GetWindowText(IntPtr h,StringBuilder value,int count);
    [DllImport("user32.dll")] static extern bool PostMessage(IntPtr h,uint message,IntPtr w,IntPtr l);
}
