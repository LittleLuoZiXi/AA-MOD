using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

// Drives only a real final installer in an explicitly isolated acceptance root.
// No GPU, URL, download, or installation service is substituted in this executable.
public static class V1ExeDriver
{
    delegate bool EnumCallback(IntPtr handle,IntPtr ignored);
    [DllImport("user32.dll")] static extern bool EnumWindows(EnumCallback callback,IntPtr parameter);
    [DllImport("user32.dll")] static extern bool EnumChildWindows(IntPtr window,EnumCallback callback,IntPtr parameter);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr window,out uint process);
    [DllImport("user32.dll",CharSet=CharSet.Unicode)] static extern int GetWindowText(IntPtr window,StringBuilder text,int count);
    [DllImport("user32.dll")] static extern bool IsWindowEnabled(IntPtr window);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr window);
    [DllImport("user32.dll")] static extern bool PostMessage(IntPtr window,uint message,IntPtr w,IntPtr l);
    [DllImport("user32.dll")] static extern bool PrintWindow(IntPtr window,IntPtr hdc,uint flags);
    [StructLayout(LayoutKind.Sequential)] struct Rect { public int Left,Top,Right,Bottom; }
    [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr window,out Rect rect);
    static Process process;static string evidence;static IntPtr main;
    static readonly List<string> events=new List<string>();
    static void Check(bool condition,string message){if(!condition)throw new IOException(message);}
    static void Event(string message){events.Add(DateTime.UtcNow.ToString("O")+" "+message);Console.WriteLine(message);File.WriteAllLines(Path.Combine(evidence,"events.log"),events,new UTF8Encoding(false));}
    static string Text(IntPtr window){var text=new StringBuilder(32768);GetWindowText(window,text,text.Capacity);return text.ToString();}
    static IntPtr[] Windows(){var all=new List<IntPtr>();EnumWindows(delegate(IntPtr h,IntPtr unused){uint id;GetWindowThreadProcessId(h,out id);if(id==process.Id&&IsWindowVisible(h))all.Add(h);return true;},IntPtr.Zero);return all.ToArray();}
    static IntPtr[] Children(IntPtr window){var all=new List<IntPtr>();EnumChildWindows(window,delegate(IntPtr h,IntPtr unused){all.Add(h);return true;},IntPtr.Zero);return all.ToArray();}
    static IntPtr Button(IntPtr window,string label){return Children(window).FirstOrDefault(h=>Text(h)==label);}
    static bool Enabled(IntPtr window,string label){var button=Button(window,label);return button!=IntPtr.Zero&&IsWindowEnabled(button);}
    static void Click(IntPtr window,string label){var button=Button(window,label);Check(button!=IntPtr.Zero&&IsWindowEnabled(button),"Expected enabled button missing: "+label);Check(PostMessage(button,0x00F5,IntPtr.Zero,IntPtr.Zero),"Cannot send button click: "+label);}
    static void Capture(string name,IntPtr window)
    {
        Rect rect;if(window==IntPtr.Zero||!GetWindowRect(window,out rect))return;
        int width=rect.Right-rect.Left,height=rect.Bottom-rect.Top;if(width<=0||height<=0||width>4096||height>4096)return;
        using(var bitmap=new Bitmap(width,height))using(var graphics=Graphics.FromImage(bitmap)){
            IntPtr hdc=graphics.GetHdc();bool done;try{done=PrintWindow(window,hdc,2);}finally{graphics.ReleaseHdc(hdc);}
            if(done)bitmap.Save(Path.Combine(evidence,name+".png"),ImageFormat.Png);
        }
        File.WriteAllLines(Path.Combine(evidence,name+"-text.txt"),Children(window).Select(Text),new UTF8Encoding(false));
    }
    static void Wait(Func<bool> ready,int milliseconds,string label)
    {
        var timer=Stopwatch.StartNew();while(timer.ElapsedMilliseconds<milliseconds){if(process.HasExited)throw new IOException("Installer exited during "+label+": "+process.ExitCode);if(ready())return;Thread.Sleep(75);}
        Capture("timeout-"+label,main);throw new TimeoutException("Timed out: "+label);
    }
    static void HandlePrompts()
    {
        foreach(var window in Windows().Where(w=>w!=main)){
            var title=Text(window);
            if(title=="重复安装确认"&&Enabled(window,"覆盖重新安装")){Event("Explicit test overwrite confirmation accepted");Click(window,"覆盖重新安装");Thread.Sleep(100);}
            else if(title=="依赖检测：缺少组件"&&Enabled(window,"继续安装")){Capture("dependency-confirmation",window);Event("Explicit isolated-fixture dependency confirmation accepted");Click(window,"继续安装");Thread.Sleep(100);}
            else if(title=="安装内置 FFmpeg"&&Enabled(window,"安装 FFmpeg 并继续")){Capture("ffmpeg-confirmation",window);Event("Explicit bundled FFmpeg confirmation accepted");Click(window,"安装 FFmpeg 并继续");Thread.Sleep(100);}
            else if(title=="内录安装未完成"||title=="DLSS 安装未完成"||title=="安装错误"||title=="DLSS 安装"){
                Capture("error-dialog",window);var body=String.Join(" | ",Children(window).Select(Text));if(Enabled(window,"确定"))Click(window,"确定");throw new IOException("Installer dialog: "+title+" "+body);
            }
        }
    }
    static Dictionary<string,string> DlssFiles(string root)
    {
        var directory=InstallCore.Within(root,OnlineDlssInstall.Mod.TrimEnd('/'));InstallCore.NoLinks(directory);
        return Directory.GetFiles(directory,"*",SearchOption.AllDirectories).ToDictionary(f=>f.Substring(directory.Length+1),f=>{InstallCore.NoLinks(f);return InstallCore.Hash(f);},StringComparer.OrdinalIgnoreCase);
    }
    [STAThread] public static int Main(string[] args)
    {
        bool success=false;
        try{
            Check(args.Length==1,"Usage: V1ExeDriver.exe settings.json");
            var options=InstallCore.Json.Deserialize<Dictionary<string,string>>(File.ReadAllText(args[0]));
            var installer=Path.GetFullPath(options["Installer"]);var root=InstallCore.FullRoot(options["GameRoot"]);evidence=Path.GetFullPath(options["Evidence"]);
            var allowed=Path.GetFullPath(options["AllowedRoot"]).TrimEnd('\\')+"\\";
            Check(root.StartsWith(allowed+"V1实机验收\\",StringComparison.OrdinalIgnoreCase)||root.StartsWith(allowed+"V1安装测试\\",StringComparison.OrdinalIgnoreCase),"Real EXE tests require an isolated V1 acceptance root");
            Check(evidence.StartsWith(allowed+"V1安装测试\\",StringComparison.OrdinalIgnoreCase),"Evidence must stay under V1安装测试");
            InstallCore.ValidateGame(root,true);InstallCore.NoLinks(installer);InstallCore.NoLinks(evidence);Directory.CreateDirectory(evidence);
            var gpu=V1Hardware.Detect();Check(gpu.Supported&&!gpu.RejectDlssStage,"Physical GPU is not supported: "+gpu.Name+" "+gpu.Reason);
            Event("Physical GPU: "+gpu.Name+"; series="+gpu.Series);Event("Installer SHA256: "+InstallCore.Hash(installer));
            bool reuse=options.ContainsKey("ReuseInstalledComponents")&&options["ReuseInstalledComponents"].Equals("true",StringComparison.OrdinalIgnoreCase);
            Dictionary<string,string> dlssBefore=null;
            if(reuse){
                Check(OnlineDlssInstall.IsInstalled(root,gpu.Series),"Existing-component mode requires all fixed native hashes and the independent receipt before starting the EXE");
                dlssBefore=DlssFiles(root);File.WriteAllText(Path.Combine(evidence,"dlss-files-before.json"),InstallCore.Json.Serialize(dlssBefore),new UTF8Encoding(false));
                Event("Existing-component reuse mode: all native files and receipt verified; new downloads are forbidden");
            }
            var before=Directory.GetDirectories(Path.GetTempPath(),"AARecorder-DLSS-*").OrderBy(x=>x).ToArray();
            process=Process.Start(new ProcessStartInfo(installer,"--root \""+root+"\""){UseShellExecute=false,CreateNoWindow=true,WindowStyle=ProcessWindowStyle.Hidden});
            Event("Real installer PID="+process.Id+"; root="+root);
            Wait(delegate{main=Windows().FirstOrDefault(w=>Text(w).StartsWith("AA 内录 V1.0",StringComparison.Ordinal));return main!=IntPtr.Zero&&Enabled(main,"开始安装内录");},90000,"preflight");
            Check(!Enabled(main,"继续"),"Continue enabled before installation");Capture("01-preflight",main);Click(main,"开始安装内录");Event("Started real recorder installation");
            bool paused=false,resumed=false,observedHint=false;DateTime pauseAt=DateTime.MinValue;var timeout=Stopwatch.StartNew();
            while(timeout.Elapsed.TotalMinutes<25){
                if(process.HasExited)throw new IOException("Installer exited before completion: "+process.ExitCode);HandlePrompts();
                var texts=Children(main).Select(Text).ToArray();
                if(reuse&&texts.Any(t=>t.Contains("正在下载 共同组件")||t.Contains("正在下载 RTX ")||t.Contains("正在校验 共同组件")||t.Contains("连接中断，正在重试"))){
                    if(Enabled(main,"取消下载"))Click(main,"取消下载");throw new IOException("Existing-component mode observed a request to download; cancelled immediately");
                }
                if(!reuse&&!paused&&Enabled(main,"暂停下载")){
                    Check(!Enabled(main,"继续"),"Continue enabled during download");Capture("02-download",main);Click(main,"暂停下载");
                    Wait(delegate{return Enabled(main,"恢复下载");},10000,"pause");Capture("03-paused",main);pauseAt=DateTime.UtcNow;paused=true;Event("Real GitHub transfer paused; Continue remains disabled");
                }
                if(paused&&!resumed){
                    Check(!Enabled(main,"继续"),"Continue enabled while paused");
                    if(texts.Any(t=>t.Contains("暂停后排查网络"))){observedHint=true;Capture("04-rotating-network-hint",main);}
                    if(observedHint&&(DateTime.UtcNow-pauseAt).TotalSeconds>=4.5){Click(main,"恢复下载");resumed=true;Event("GitHub network hint rotation observed; transfer resumed");}
                    if((DateTime.UtcNow-pauseAt).TotalSeconds>12)throw new IOException("Network hint did not rotate while paused");
                }
                if(Enabled(main,"继续")){
                    Check(reuse||(paused&&resumed&&observedHint),"Completion arrived before the required real pause/resume/hint checks");
                    Check(OnlineDlssInstall.IsInstalled(root,gpu.Series),"Continue became enabled before all native hashes and ownership were valid");
                    var receipt=InstallCore.Json.Deserialize<Receipt>(File.ReadAllText(InstallCore.Within(root,InstallCore.ReceiptName)));
                    Check(receipt.Version=="1.0.0","Recorder version is not V1");Capture("05-ready",main);Click(main,"继续");
                    Wait(delegate{return Enabled(main,"完成");},10000,"finish-page");Capture("06-finished",main);Click(main,"完成");
                    Check(process.WaitForExit(15000)&&process.ExitCode==0,"Installer did not exit successfully");
                    Check(before.SequenceEqual(Directory.GetDirectories(Path.GetTempPath(),"AARecorder-DLSS-*").OrderBy(x=>x)),"New DLSS session temp directory remains after completion");
                    if(reuse){
                        var after=DlssFiles(root);Check(after.Count==dlssBefore.Count&&dlssBefore.All(p=>after.ContainsKey(p.Key)&&after[p.Key]==p.Value),"Existing native files or independent receipt changed during reuse");
                        File.WriteAllText(Path.Combine(evidence,"dlss-files-after.json"),InstallCore.Json.Serialize(after),new UTF8Encoding(false));
                        Event("PASS: final real EXE reused already-verified local DLSS; every component/receipt hash unchanged; exit=0");
                    }else Event("PASS: final real EXE installed recorder and downloaded/validated physical GPU components, including pause/resume; exit=0");success=true;return 0;
                }
                Thread.Sleep(100);
            }
            throw new TimeoutException("Real GitHub installer test exceeded 25 minutes");
        }catch(Exception error){Console.Error.WriteLine(error);if(!String.IsNullOrEmpty(evidence)&&Directory.Exists(evidence))File.WriteAllText(Path.Combine(evidence,"FAIL.txt"),error.ToString());return 1;}
        finally{
            if(!success&&process!=null&&!process.HasExited){if(main!=IntPtr.Zero)PostMessage(main,0x0010,IntPtr.Zero,IntPtr.Zero);process.WaitForExit(20000);}
            if(process!=null)process.Dispose();if(success)File.WriteAllText(Path.Combine(evidence,"PASS.txt"),"Real production EXE / physical GPU flow passed. Transfer or verified local-reuse mode is recorded in settings.json and events.log.\n"+DateTime.UtcNow.ToString("O"));
        }
    }
}
