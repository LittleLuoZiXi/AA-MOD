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
using System.Windows.Automation;

// Drives only a real final installer in an explicitly isolated acceptance root.
// No GPU, URL, download, or installation service is substituted in this executable.
// All modes are local-only: the selected-DLSS mode requires a complete installation
// before launching the production EXE. There is no transfer/download test mode.
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
    [DllImport("user32.dll",SetLastError=true)] static extern IntPtr SendMessageTimeout(IntPtr window,uint message,IntPtr w,IntPtr l,uint flags,uint timeout,out IntPtr result);
    [DllImport("user32.dll")] static extern bool PrintWindow(IntPtr window,IntPtr hdc,uint flags);
    [StructLayout(LayoutKind.Sequential)] struct Rect { public int Left,Top,Right,Bottom; }
    [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr window,out Rect rect);
    static Process process;static string evidence;static IntPtr main;static bool reportedNativeMismatch;
    const string DlssLabel="安装 DLSS 组件（可选）";
    static readonly List<string> events=new List<string>();
    static void Check(bool condition,string message){if(!condition)throw new IOException(message);}
    static void Event(string message){events.Add(DateTime.UtcNow.ToString("O")+" "+message);Console.WriteLine(message);File.WriteAllLines(Path.Combine(evidence,"events.log"),events,new UTF8Encoding(false));}
    static string Text(IntPtr window){var text=new StringBuilder(32768);GetWindowText(window,text,text.Capacity);return text.ToString();}
    static IntPtr[] Windows(){var all=new List<IntPtr>();EnumWindows(delegate(IntPtr h,IntPtr unused){uint id;GetWindowThreadProcessId(h,out id);if(id==process.Id&&IsWindowVisible(h))all.Add(h);return true;},IntPtr.Zero);return all.ToArray();}
    static IntPtr[] Children(IntPtr window){var all=new List<IntPtr>();EnumChildWindows(window,delegate(IntPtr h,IntPtr unused){all.Add(h);return true;},IntPtr.Zero);return all.ToArray();}
    static IntPtr Button(IntPtr window,string label){return Children(window).FirstOrDefault(h=>Text(h)==label);}
    static bool Enabled(IntPtr window,string label){var button=Button(window,label);return button!=IntPtr.Zero&&IsWindowEnabled(button);}
    static void Click(IntPtr window,string label){var button=Button(window,label);Check(button!=IntPtr.Zero&&IsWindowEnabled(button),"Expected enabled button missing: "+label);Check(PostMessage(button,0x00F5,IntPtr.Zero,IntPtr.Zero),"Cannot send button click: "+label);}
    static bool DlssChecked()
    {
        var checkbox=Button(main,DlssLabel);Check(checkbox!=IntPtr.Zero,"Optional DLSS checkbox is missing");
        // WinForms paints its managed Checked state itself. BM_GETCHECK can
        // remain zero even when the visible checkbox is checked; use the
        // accessibility TogglePattern, which exposes the actual control state.
        var element=AutomationElement.FromHandle(checkbox);object value;
        Check(element!=null&&element.TryGetCurrentPattern(TogglePattern.Pattern,out value),"Optional DLSS checkbox has no accessibility toggle state");
        var state=((TogglePattern)element.GetCurrentPattern(TogglePattern.Pattern)).Current.ToggleState;
        Check(state!=ToggleState.Indeterminate,"Optional DLSS checkbox returned an indeterminate state");
        IntPtr native;
        if(!reportedNativeMismatch&&SendMessageTimeout(checkbox,0x00F0,IntPtr.Zero,IntPtr.Zero,2,5000,out native)!=IntPtr.Zero
            &&(native.ToInt64()==1)!=(state==ToggleState.On)){
            reportedNativeMismatch=true;Event("WinForms managed checkbox state read through UI Automation: "+state+"; native BM_GETCHECK="+native.ToInt64());
        }
        return state==ToggleState.On;
    }
    static void ToggleDlss()
    {
        var checkbox=Button(main,DlssLabel);Check(checkbox!=IntPtr.Zero&&IsWindowEnabled(checkbox),"Optional DLSS checkbox is not enabled");
        // Synchronous BM_CLICK returns after CheckedChanged has entered its first
        // await, so the following wait cannot mistake the old enabled state for
        // completion of HardwareChecking.
        IntPtr result;Check(SendMessageTimeout(checkbox,0x00F5,IntPtr.Zero,IntPtr.Zero,2,10000,out result)!=IntPtr.Zero,"Cannot click optional DLSS checkbox");
    }
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
            else if(title=="内录安装未完成"||title=="DLSS 安装未完成"||title=="安装错误"||title=="DLSS 安装"||title=="无法安装 DLSS"){
                Capture("error-dialog",window);var body=String.Join(" | ",Children(window).Select(Text));if(Enabled(window,"确定"))Click(window,"确定");throw new IOException("Installer dialog: "+title+" "+body);
            }
        }
    }
    public sealed class DlssState
    {
        public bool Exists;
        public Dictionary<string,string> Files=new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase);
        public List<string> Directories=new List<string>();
    }
    static DlssState DlssFiles(string root)
    {
        var directory=InstallCore.Within(root,OnlineDlssInstall.Mod.TrimEnd('/'));InstallCore.NoLinks(directory);
        Check(!File.Exists(directory),"Independent DLSS directory is occupied by a file");
        var state=new DlssState{Exists=Directory.Exists(directory)};if(!state.Exists)return state;
        var pending=new Stack<string>();pending.Push(directory);
        while(pending.Count>0){
            var current=pending.Pop();InstallCore.NoLinks(current);
            foreach(var file in Directory.GetFiles(current)){InstallCore.NoLinks(file);state.Files.Add(file.Substring(directory.Length+1),InstallCore.Hash(file));}
            foreach(var child in Directory.GetDirectories(current)){InstallCore.NoLinks(child);state.Directories.Add(child.Substring(directory.Length+1));pending.Push(child);}
        }
        state.Directories.Sort(StringComparer.OrdinalIgnoreCase);return state;
    }
    static void SameDlss(DlssState before,DlssState after)
    {
        Check(before.Exists==after.Exists&&before.Files.Count==after.Files.Count
            &&before.Files.All(p=>after.Files.ContainsKey(p.Key)&&after.Files[p.Key]==p.Value)
            &&before.Directories.SequenceEqual(after.Directories,StringComparer.OrdinalIgnoreCase),
            "Independent DLSS files, receipt, or directory presence changed during a local-only test");
    }
    static string[] TempSessions(){return Directory.GetDirectories(Path.GetTempPath(),"AARecorder-DLSS-*").OrderBy(x=>x,StringComparer.OrdinalIgnoreCase).ToArray();}
    static void GuardNoTransfer(bool coreOnly,string[] sessionsBefore)
    {
        var texts=Children(main).Select(Text).ToArray();
        bool transfer=texts.Any(t=>t.Contains("正在下载 共同组件")||t.Contains("正在下载 RTX ")||t.Contains("正在校验 共同组件")||t.Contains("连接中断，正在重试"));
        bool newSession=!sessionsBefore.SequenceEqual(TempSessions(),StringComparer.OrdinalIgnoreCase);
        if(transfer||newSession||(coreOnly&&(Enabled(main,"暂停下载")||Enabled(main,"恢复下载")||Enabled(main,"取消下载")))){
            Capture("forbidden-download-state",main);if(Enabled(main,"取消下载"))Click(main,"取消下载");
            throw new IOException("A local-only test observed a forbidden download stage or temporary download session; cancellation requested");
        }
    }
    [STAThread] public static int Main(string[] args)
    {
        bool success=false;string mode="",root=null;DlssState dlssBefore=null;bool afterSaved=false;
        try{
            Check(args.Length==1,"Usage: V1ExeDriver.exe settings.json");
            var options=InstallCore.Json.Deserialize<Dictionary<string,string>>(File.ReadAllText(args[0]));
            var installer=Path.GetFullPath(options["Installer"]);root=InstallCore.FullRoot(options["GameRoot"]);evidence=Path.GetFullPath(options["Evidence"]);
            var allowed=Path.GetFullPath(options["AllowedRoot"]).TrimEnd('\\')+"\\";
            Check(root.StartsWith(allowed+"V1实机验收\\",StringComparison.OrdinalIgnoreCase)||root.StartsWith(allowed+"V1安装测试\\",StringComparison.OrdinalIgnoreCase),"Real EXE tests require an isolated V1 acceptance root");
            Check(evidence.StartsWith(allowed+"V1安装测试\\",StringComparison.OrdinalIgnoreCase),"Evidence must stay under V1安装测试");
            InstallCore.ValidateGame(root,true);InstallCore.NoLinks(installer);InstallCore.NoLinks(evidence);Directory.CreateDirectory(evidence);
            Check(options.ContainsKey("LocalOnly")&&options["LocalOnly"].Equals("true",StringComparison.OrdinalIgnoreCase),"This driver accepts local-only tests");
            mode=options.ContainsKey("Mode")?options["Mode"]:"CoreOnly";
            Check(new[]{"CoreOnly","SelectedReuse","SelectThenUncheck"}.Contains(mode),"Unknown local-only mode: "+mode);
            bool reuse=mode=="SelectedReuse",select=mode!="CoreOnly",coreOnly=!reuse;
            V1GpuStatus gpu=null;
            dlssBefore=DlssFiles(root);File.WriteAllText(Path.Combine(evidence,"dlss-files-before.json"),InstallCore.Json.Serialize(dlssBefore),new UTF8Encoding(false));
            Event("Local-only mode="+mode+"; independent DLSS directory exists="+dlssBefore.Exists+"; files="+dlssBefore.Files.Count);
            Event("Installer SHA256: "+InstallCore.Hash(installer));
            if(select){gpu=V1Hardware.Detect();Check(gpu.Supported&&!gpu.RejectDlssStage,"Selection tests require a supported physical GPU: "+gpu.Name+" "+gpu.Reason);Event("Physical GPU: "+gpu.Name+"; series="+gpu.Series);}
            if(reuse){
                Check(OnlineDlssInstall.IsInstalled(root,gpu.Series),"Existing-component mode requires all fixed native hashes and the independent receipt before starting the EXE");
                Event("Existing-component reuse mode: all native files and receipt verified; new downloads are forbidden");
            }
            var before=TempSessions();
            process=Process.Start(new ProcessStartInfo(installer,"--root \""+root+"\""){UseShellExecute=false,CreateNoWindow=true,WindowStyle=ProcessWindowStyle.Hidden});
            Event("Real installer PID="+process.Id+"; root="+root);
            // InspectNow enables Start just before its finally restores input.
            // Wait for both controls so an inter-process read cannot observe
            // that valid but transient hand-off between the two operations.
            Wait(delegate{main=Windows().FirstOrDefault(w=>Text(w).StartsWith("AA 内录 V1.2",StringComparison.Ordinal));return main!=IntPtr.Zero&&Enabled(main,"开始安装内录")&&Enabled(main,DlssLabel);},90000,"preflight");
            Capture("01-preflight",main);
            Check(!Enabled(main,"继续"),"Continue enabled before installation");
            Check(Enabled(main,DlssLabel)&&!DlssChecked(),"Optional DLSS must initially be enabled and unchecked");
            Check(Children(main).Select(Text).Any(t=>t.Contains("显卡：勾选 DLSS 后检测")),"Initial screen suggests hardware detection occurred before selection");
            Event("Initial optional DLSS checkbox is enabled and unchecked; hardware detection is deferred");
            if(select){
                ToggleDlss();
                Wait(delegate{HandlePrompts();return Enabled(main,DlssLabel)&&Enabled(main,"开始安装内录")&&DlssChecked();},90000,"selected-hardware-check");
                Check(Children(main).Select(Text).Any(t=>t.Contains("检测到的显卡：")),"HardwareChecking completed without the detected GPU label");
                Capture("02-selected-local-components",main);Event("BM_CLICK selected DLSS; HardwareChecking completed and checkbox remains checked");
                if(mode=="SelectThenUncheck"){
                    ToggleDlss();Wait(delegate{return Enabled(main,DlssLabel)&&Enabled(main,"开始安装内录")&&!DlssChecked();},10000,"unselected-again");
                    Check(Children(main).Select(Text).Any(t=>t=="本次仅安装内录 MOD；点击开始安装内录。")
                        &&!Children(main).Select(Text).Any(t=>t.StartsWith("已选择 DLSS；",StringComparison.Ordinal)),
                        "Deselection left a stale selected-DLSS status on screen");
                    Capture("03-unselected-again",main);Event("Second BM_CLICK cleared DLSS; only recorder will be installed");
                }
            }
            Check(DlssChecked()==reuse,"DLSS selection does not match the requested local-only mode");
            if(reuse)Check(OnlineDlssInstall.IsInstalled(root,gpu.Series),"Local DLSS became incomplete before core installation; refusing to start");
            SameDlss(dlssBefore,DlssFiles(root));GuardNoTransfer(coreOnly,before);
            Click(main,"开始安装内录");Event("Started real V1.2 recorder installation; remote downloads forbidden");
            var timeout=Stopwatch.StartNew();
            while(timeout.Elapsed.TotalMinutes<10){
                if(process.HasExited)throw new IOException("Installer exited before completion: "+process.ExitCode);HandlePrompts();
                GuardNoTransfer(coreOnly,before);
                if(Enabled(main,"继续")){
                    Check(DlssChecked()==reuse,"Optional DLSS checkbox changed during installation");
                    if(reuse)Check(OnlineDlssInstall.IsInstalled(root,gpu.Series),"Continue became enabled before all native hashes and ownership were valid");
                    var receipt=InstallCore.Json.Deserialize<Receipt>(File.ReadAllText(InstallCore.Within(root,InstallCore.ReceiptName)));
                    Check(receipt.Version=="1.2.0","Recorder receipt version is not 1.2.0");
                    Check(File.Exists(InstallCore.Within(root,InstallCore.Mod+"1.2.0/AzureArchive.Recorder.dll")),"V1.2 MOD DLL is missing");
                    Capture("05-ready",main);Click(main,"继续");
                    Wait(delegate{return Enabled(main,"完成");},10000,"finish-page");Capture("06-finished",main);Click(main,"完成");
                    Check(process.WaitForExit(15000)&&process.ExitCode==0,"Installer did not exit successfully");
                    Check(before.SequenceEqual(TempSessions(),StringComparer.OrdinalIgnoreCase),"New DLSS session temp directory remains after completion");
                    var after=DlssFiles(root);File.WriteAllText(Path.Combine(evidence,"dlss-files-after.json"),InstallCore.Json.Serialize(after),new UTF8Encoding(false));afterSaved=true;SameDlss(dlssBefore,after);
                    Event("PASS: final real V1.2 EXE "+(reuse?"reused complete local DLSS":"installed recorder only")+"; mode="+mode+"; every independent component/receipt hash unchanged; exit=0");
                    success=true;return 0;
                }
                Thread.Sleep(100);
            }
            throw new TimeoutException("Local-only real V1.2 installer test exceeded 10 minutes");
        }catch(Exception error){Console.Error.WriteLine(error);if(!String.IsNullOrEmpty(evidence)&&Directory.Exists(evidence))File.WriteAllText(Path.Combine(evidence,"FAIL.txt"),error.ToString());return 1;}
        finally{
            if(!success&&process!=null&&!process.HasExited){if(main!=IntPtr.Zero)PostMessage(main,0x0010,IntPtr.Zero,IntPtr.Zero);process.WaitForExit(20000);}
            if(!afterSaved&&dlssBefore!=null&&(process==null||process.HasExited)){
                try{File.WriteAllText(Path.Combine(evidence,"dlss-files-after.json"),InstallCore.Json.Serialize(DlssFiles(root)),new UTF8Encoding(false));}
                catch(Exception error){File.WriteAllText(Path.Combine(evidence,"dlss-after-snapshot-error.txt"),error.ToString());}
            }
            if(process!=null)process.Dispose();if(success)File.WriteAllText(Path.Combine(evidence,"PASS.txt"),"Real production V1.2 EXE local-only flow passed. Mode="+mode+". No download test branch exists; independent DLSS state is unchanged.\n"+DateTime.UtcNow.ToString("O"));
        }
    }
}
