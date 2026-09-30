using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

public static class InstallerSafetyTests
{
    static string workspace,game,payload,root,installer;static int passed;
    static void Check(bool ok,string message){if(!ok)throw new Exception(message);}
    static void Pass(string message){passed++;Console.WriteLine("PASS "+passed+": "+message);}
    static string P(string name){return Path.Combine(root,name.Replace('/',Path.DirectorySeparatorChar));}
    static void Put(string name,string value){var p=P(name);Directory.CreateDirectory(Path.GetDirectoryName(p));File.WriteAllText(p,value);}
    static void Install(){using(var s=File.OpenRead(payload))Check(InstallCore.Install(root,s,delegate{},delegate{return true;},null,new string[0],false,delegate{return true;}),"Install cancelled unexpectedly");}
    static void MustFail(Action action,string message){bool failed=false;try{action();}catch{failed=true;}Check(failed,message);}
    [STAThread] public static int Main(string[] args)
    {
        try {
            game=Path.GetFullPath(args[0]);payload=Path.GetFullPath(args[1]);installer=Path.GetFullPath(args[2]);
            workspace=Path.Combine(game,"RecorderMod","packaging","safety-"+DateTime.Now.ToString("yyyyMMdd-HHmmss"));
            root=Path.Combine(workspace,"AA with spaces 中文");Directory.CreateDirectory(root);
            File.Copy(Path.Combine(game,"AzureArchive.exe"),P("AzureArchive.exe"));
            foreach(var dir in new[]{"AzureArchive_Data","BepInEx/core","dotnet"})Directory.CreateDirectory(P(dir));
            Put("ActiveProfile.txt","TestProfile");
            Put("profiles/TestProfile/modconfig.json","{\"EnabledMods\":[{\"name\":\"OtherMod\",\"version\":\"7.0.0\",\"custom\":42}],\"Unrelated\":\"keep\"}");
            Put("profiles/Untouched/modconfig.json","{ \"EnabledMods\": [{\"name\":\"Unrelated\",\"version\":\"1.2.3\"}] }");
            Put("mods/OtherMod/0.2.1/AzureArchive.Recorder.dll","OTHER MOD SAME DLL NAME");
            Put("mods/OtherMod/settings.cfg","KEEP OTHER MOD");Put("user-data/剧情.aap2","KEEP ORIGINAL PROJECT");Put("Recordings/user.mp4","KEEP EXPORTED VIDEO");Put("mods/AzureArchiveRecorder/user-file.txt","KEEP USER FILE INSIDE MOD");
            Put("RecorderMod/src/Plugin.cs","DEVELOPMENT SOURCE MUST SURVIVE");Put("RecorderMod/build.ps1","BUILD SCRIPT MUST SURVIVE");Put("RecorderMod/packaging/Installer.cs","INSTALLER SOURCE MUST SURVIVE");Put("RecorderMod/history.zip","SOURCE BACKUP MUST SURVIVE");
            var sentinels=new[]{"AzureArchive.exe","ActiveProfile.txt","profiles/Untouched/modconfig.json","mods/OtherMod/0.2.1/AzureArchive.Recorder.dll","mods/OtherMod/settings.cfg","user-data/剧情.aap2","Recordings/user.mp4","mods/AzureArchiveRecorder/user-file.txt","RecorderMod/src/Plugin.cs","RecorderMod/build.ps1","RecorderMod/packaging/Installer.cs","RecorderMod/history.zip"}.ToDictionary(x=>x,x=>InstallCore.Hash(P(x)));
            // Invalid profile must fail before writing the first install file.
            var profile=P("profiles/TestProfile/modconfig.json");var original=File.ReadAllText(profile);
            File.WriteAllText(profile,"{bad json");MustFail(Install,"Malformed profile accepted");Check(!File.Exists(P(InstallCore.ReceiptName)),"Partial install on invalid profile");File.WriteAllText(profile,original);Pass("Malformed profile stops before mutation");
            Install();Check(File.Exists(P(InstallCore.ReceiptName)),"Receipt missing");
            Check(File.Exists(P(InstallCore.Mod+"runtime/ffmpeg/ffmpeg.exe"))&&File.Exists(P(InstallCore.Mod+"runtime/ffmpeg/ffprobe.exe")),"Bundled FFmpeg pair missing");
            var active=InstallCore.Json.Deserialize<Dictionary<string,object>>(File.ReadAllText(profile));
            Check(File.ReadAllText(profile).Contains("OtherMod")&&File.ReadAllText(profile).Contains("42")&&File.ReadAllText(profile).Contains("0.2.1"),"Existing profile lost");Pass("Full EXE payload installs and preserves unrelated profile fields");
            // Tamper with a receipt path. No file or profile can change.
            var receiptPath=P(InstallCore.ReceiptName);var receiptText=File.ReadAllText(receiptPath);var beforeProfile=InstallCore.Hash(profile);
            var receipt=InstallCore.Json.Deserialize<Receipt>(receiptText);receipt.Files[0].Path="mods/OtherMod/settings.cfg";
            File.WriteAllText(receiptPath,InstallCore.Json.Serialize(receipt));MustFail(delegate{InstallCore.Uninstall(root,delegate{});},"Other MOD path accepted");Check(InstallCore.Hash(profile)==beforeProfile,"Profile changed before validating receipt");File.WriteAllText(receiptPath,receiptText);Pass("Foreign MOD path rejected before deletion or profile edit");
            foreach(var invalid in new[]{"../outside.txt","mods/AzureArchiveRecorder/../../OtherMod/settings.cfg","C:/Windows/win.ini","mods/AzureArchiveRecorder/runtime/file:stream"})MustFail(delegate{InstallCore.Within(root,invalid);},"Unsafe relative path accepted");Pass("Traversal, absolute path, alternate stream rejected");
            // Actual junction inside runtime must block uninstall, including all profile edits.
            var link=P("mods/AzureArchiveRecorder/runtime/junction-test");var target=P("mods/OtherMod");
            var psi=new ProcessStartInfo("powershell.exe","-NoProfile -NonInteractive -Command \"New-Item -ItemType Junction -Path '"+link.Replace("'","''")+"' -Target '"+target.Replace("'","''")+"' | Out-Null\""){UseShellExecute=false,CreateNoWindow=true};
            using(var proc=Process.Start(psi)){proc.WaitForExit();Check(proc.ExitCode==0,"Cannot create test junction");}
            receipt=InstallCore.Json.Deserialize<Receipt>(receiptText);receipt.Files.Add(new OwnedFile{Path=InstallCore.Mod+"runtime/junction-test/settings.cfg",Sha256=InstallCore.Hash(P("mods/OtherMod/settings.cfg"))});
            File.WriteAllText(receiptPath,InstallCore.Json.Serialize(receipt));MustFail(delegate{InstallCore.Uninstall(root,delegate{});},"Junction accepted");Check(InstallCore.Hash(profile)==beforeProfile,"Profile changed for junction rejection");File.WriteAllText(receiptPath,receiptText);
            // Remove only the junction itself; never recurse or enumerate its target.
            Check((File.GetAttributes(link)&FileAttributes.ReparsePoint)!=0,"Not a junction");Directory.Delete(link,false);Pass("Real directory junction rejected without touching target");
            // Exercise rollback after actual file writes then a sharing violation.
            var rollback=P("rollback-test");Directory.CreateDirectory(rollback);var keep=Path.Combine(rollback,"existing.txt");var locked=Path.Combine(rollback,"locked.txt");var created=Path.Combine(rollback,"created.txt");File.WriteAllText(keep,"original");File.WriteAllText(locked,"locked");
            using(var hold=new FileStream(locked,FileMode.Open,FileAccess.ReadWrite,FileShare.Read))MustFail(delegate{using(var tx=new InstallCore.Transaction()){tx.Write(keep,Encoding.UTF8.GetBytes("changed"));tx.Write(created,new byte[]{1});tx.Write(locked,new byte[]{2});tx.Commit();}},"Write failure did not stop transaction");
            Check(File.ReadAllText(keep)=="original"&&!File.Exists(created)&&File.ReadAllText(locked)=="locked","Rollback failed");Pass("Write error rolls back replaced and newly created files");
            // Modified owned file and unknown user file must survive; genuine owned files must go.
            var changed=P(InstallCore.Mod+"使用说明.txt");File.AppendAllText(changed,"USER NOTE");
            Put("profiles/TestProfile/configs/azurearchive.recorder.cfg","owned recorder setting");Put("profiles/TestProfile/configs/other.mod.cfg","OTHER CONFIG");sentinels.Add("profiles/TestProfile/configs/other.mod.cfg",InstallCore.Hash(P("profiles/TestProfile/configs/other.mod.cfg")));
            var start=new ProcessStartInfo(P(InstallCore.Mod+"卸载内录MOD.exe"),"--quiet"){UseShellExecute=false,CreateNoWindow=true};
            using(var proc=Process.Start(start)){Check(proc.WaitForExit(15000)&&proc.ExitCode==0,"Uninstall launcher failed");}
            var timer=Stopwatch.StartNew();while(File.Exists(receiptPath)&&timer.Elapsed.TotalSeconds<45)Thread.Sleep(100);
            Check(!File.Exists(receiptPath),"Uninstall helper did not complete");Check(!File.Exists(P(InstallCore.Mod+"0.2.1/AzureArchive.Recorder.dll")),"Plugin was not removed");Check(!File.Exists(P(InstallCore.Mod+"runtime/ffmpeg/ffmpeg.exe"))&&!File.Exists(P(InstallCore.Mod+"runtime/ffmpeg/ffprobe.exe")),"Owned FFmpeg pair was not removed");Check(!File.Exists(P(InstallCore.Mod+"卸载内录MOD.exe")),"Uninstaller did not remove itself");Check(File.ReadAllText(changed).Contains("USER NOTE"),"Modified owned file removed");Check(!File.Exists(P("profiles/TestProfile/configs/azurearchive.recorder.cfg")),"Recorder config remained");Check(!File.ReadAllText(profile).Contains("AzureArchiveRecorder")&&File.ReadAllText(profile).Contains("OtherMod"),"Profile cleanup incorrect");
            foreach(var sentinel in sentinels)Check(File.Exists(P(sentinel.Key))&&InstallCore.Hash(P(sentinel.Key))==sentinel.Value,"Sentinel changed: "+sentinel.Key);Pass("Real uninstall EXE and helper preserve every foreign MOD/file, remove owned files and own config");
            // Verify an error popup is real and the installer exits nonzero after dismissal.
            var bad=Path.Combine(workspace,"NotAA");Directory.CreateDirectory(bad);
            using(var proc=Process.Start(new ProcessStartInfo(installer,"--quiet --root \""+bad+"\""){UseShellExecute=false,CreateNoWindow=true})){
                timer.Restart();IntPtr dialog=IntPtr.Zero;
                while(dialog==IntPtr.Zero&&!proc.HasExited&&timer.Elapsed.TotalSeconds<15){Thread.Sleep(100);EnumWindows(delegate(IntPtr handle,IntPtr unused){uint pid;GetWindowThreadProcessId(handle,out pid);var text=new StringBuilder(256);GetWindowText(handle,text,text.Capacity);if(pid==proc.Id&&text.ToString().Contains("安装错误"))dialog=handle;return true;},IntPtr.Zero);}
                Check(dialog!=IntPtr.Zero,"Error MessageBox not shown");PostMessage(dialog,0x0010,IntPtr.Zero,IntPtr.Zero);var exited=proc.WaitForExit(10000);Check(exited&&proc.ExitCode!=0,"Installer did not terminate on error; exited="+exited+", code="+(exited?proc.ExitCode.ToString():"still running"));
                Check(!Directory.EnumerateFileSystemEntries(bad).Any(),"Invalid target changed");
            }Pass("Actual error MessageBox displayed; process terminates with error code and target unchanged");
            Console.WriteLine("ALL "+passed+" CHECKS PASSED. Evidence: "+workspace);File.WriteAllText(Path.Combine(workspace,"PASS.txt"),"ALL "+passed+" CHECKS PASSED\n"+DateTime.Now);return 0;
        }catch(Exception ex){Console.Error.WriteLine(ex);return 1;}
    }
    delegate bool WindowCallback(IntPtr handle,IntPtr param);
    [DllImport("user32.dll")] static extern bool EnumWindows(WindowCallback callback,IntPtr value);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h,out uint p);
    [DllImport("user32.dll",CharSet=CharSet.Unicode)] static extern int GetWindowText(IntPtr h,StringBuilder text,int count);
    [DllImport("user32.dll")] static extern bool PostMessage(IntPtr h,uint msg,IntPtr w,IntPtr l);
}
