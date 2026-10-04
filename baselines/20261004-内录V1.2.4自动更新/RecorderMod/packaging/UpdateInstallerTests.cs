using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Threading;

public static class UpdateInstallerTests
{
    static string workspace,fake,helper;static int passed;
    static void Check(bool value,string message){if(!value)throw new Exception(message);}
    static void Pass(string message){Console.WriteLine("PASS "+(++passed)+": "+message);}
    static void Fails(Action action,string message){bool failed=false;try{action();}catch{failed=true;}Check(failed,message);}
    sealed class Fixture:IDisposable
    {
        public string Root,Payload;public Process Parent;public Receipt Old;public Dictionary<string,string> Sentinels=new Dictionary<string,string>();
        public string PathOf(string relative){return Path.Combine(Root,relative.Replace('/',Path.DirectorySeparatorChar));}
        public void Put(string path,string value){var full=PathOf(path);Directory.CreateDirectory(Path.GetDirectoryName(full));File.WriteAllText(full,value);}
        public string Add(string path,string text){Put(path,text);return path;}
        public Fixture()
        {
            Root=Path.Combine(workspace,Guid.NewGuid().ToString("N"));Directory.CreateDirectory(Root);File.Copy(fake,PathOf("AzureArchive.exe"));Directory.CreateDirectory(PathOf("AzureArchive_Data"));
            Put("ActiveProfile.txt","Active");
            Put("profiles/Active/modconfig.json","{\"EnabledMods\":[{\"name\":\"OtherMod\",\"version\":\"8\",\"custom\":42},{\"name\":\"AzureArchiveRecorder\",\"version\":\"1.2.3\",\"extra\":{\"keep\":true}}],\"Unknown\":\"retain\"}");
            Put("profiles/AlsoEnabled/modconfig.json","{\"EnabledMods\":[{\"name\":\"AzureArchiveRecorder\",\"version\":\"1.2.3\"}],\"Other\":99}");
            Put("profiles/Disabled/modconfig.json","{ \"EnabledMods\": [{\"name\":\"OtherMod\",\"version\":\"8\"}], \"layout\":\"keep bytes\" }");
            foreach(var path in new[]{"profiles/Active/configs/azurearchive.recorder.cfg","mods/OtherMod/file.txt","mods/AzureArchiveRecorder/user.txt","RecorderMod/src/Plugin.cs","RecorderMod/packaging/Installer.cs","project/剧情.aap2","Recordings/old.mp4","mods/AzureArchiveRecorder/runtime/user-data/custom.json","DLSS/tool.exe"})Put(path,"SENTINEL "+path);
            var paths=new[]{Add(InstallCore.Mod+"1.2.3/AzureArchive.Recorder.dll","old locked dll"),Add(InstallCore.Mod+"1.2.3/manifest.json","old manifest"),Add(InstallCore.Mod+"使用说明.txt","old guide"),Add(InstallCore.Mod+"卸载内录MOD.exe","old uninstaller"),Add(InstallCore.Mod+"runtime/ffmpeg/ffmpeg.exe","same ffmpeg DO NOT EXECUTE"),Add(InstallCore.Mod+"runtime/obsolete.dat","keep obsolete runtime")};
            Old=new Receipt{Product=InstallCore.Product,Version="1.2.3",Root=Root,Files=paths.Select(p=>new OwnedFile{Path=p,Sha256=InstallCore.Hash(PathOf(p))}).ToList(),LegacyFiles=new List<OwnedFile>{new OwnedFile{Path=Add(InstallCore.Mod+"1.2.2/AzureArchive.Recorder.dll","old legacy"),Sha256=InstallCore.Hash(PathOf(InstallCore.Mod+"1.2.2/AzureArchive.Recorder.dll"))}},GeneratedFiles=new List<OwnedFile>{new OwnedFile{Path=Add(InstallCore.Mod+"runtime/gpu-runtimes/rtx40/nvngx_dlssnr.dll","local DLSS cached"),Sha256=InstallCore.Hash(PathOf(InstallCore.Mod+"runtime/gpu-runtimes/rtx40/nvngx_dlssnr.dll"))}}};
            SaveReceipt();
            foreach(var file in Directory.GetFiles(Root,"*",SearchOption.AllDirectories)){var relative=file.Substring(Root.Length+1).Replace('\\','/');if(!relative.EndsWith("installed-files.json")&&!relative.EndsWith("使用说明.txt")&&!relative.EndsWith("卸载内录MOD.exe")&&!relative.StartsWith("profiles/Active/modconfig")&&!relative.StartsWith("profiles/AlsoEnabled/modconfig"))Sentinels[relative]=InstallCore.Hash(file);}
            Parent=Process.Start(new ProcessStartInfo(PathOf("AzureArchive.exe"),"--wait \""+PathOf("stop.flag")+"\""){UseShellExecute=false,CreateNoWindow=true,WindowStyle=ProcessWindowStyle.Hidden});Thread.Sleep(100);
            Payload=Path.Combine(Root,"payload-source.zip");MakePayload();
        }
        public void SaveReceipt(){Put(InstallCore.ReceiptName,InstallCore.Json.Serialize(Old));}
        public void MakePayload(Action<Receipt> mutate=null,string extra=null,bool wrongHash=false)
        {
            var files=new Dictionary<string,byte[]>();var version=InstallCore.Mod+InstallCore.Version+"/";
            files[version+"AzureArchive.Recorder.dll"]=Encoding.UTF8.GetBytes("new DLL");files[version+"manifest.json"]=Encoding.UTF8.GetBytes("{\"name\":\"AzureArchiveRecorder\",\"version_number\":\""+InstallCore.Version+"\"}");files[version+InstallCore.UpdateHelperName]=File.ReadAllBytes(helper);files[InstallCore.Mod+"runtime/empty.dist-info/REQUESTED"]=new byte[0];files[InstallCore.Mod+"使用说明.txt"]=Encoding.UTF8.GetBytes("new guide");files[InstallCore.Mod+"卸载内录MOD.exe"]=Encoding.UTF8.GetBytes("new uninstaller");files[InstallCore.Mod+"runtime/ffmpeg/ffmpeg.exe"]=Encoding.UTF8.GetBytes("same ffmpeg DO NOT EXECUTE");
            var receipt=new Receipt{Product=InstallCore.Product,Version=InstallCore.Version,Root="",Files=files.Select(p=>new OwnedFile{Path=p.Key,Sha256=BytesHash(p.Value)}).ToList(),LegacyFiles=new List<OwnedFile>(),GeneratedFiles=new List<OwnedFile>{new OwnedFile{Path=InstallCore.Mod+"runtime/gpu-runtimes/rtx50/nvngx_dlssnr.dll",Sha256=new string('F',64)}}};
            if(mutate!=null)mutate(receipt);if(wrongHash)receipt.Files[0].Sha256=new string('A',64);
            using(var output=new FileStream(Payload,FileMode.Create))using(var zip=new ZipArchive(output,ZipArchiveMode.Create)){
                foreach(var file in files)using(var stream=zip.CreateEntry(file.Key).Open())stream.Write(file.Value,0,file.Value.Length);
                using(var writer=new StreamWriter(zip.CreateEntry("payload-manifest.json").Open()))writer.Write(InstallCore.Json.Serialize(receipt));
                if(extra!=null)using(var writer=new StreamWriter(zip.CreateEntry(extra).Open()))writer.Write("unexpected");
            }
        }
        public void Apply(){using(var stream=File.OpenRead(Payload))InstallCore.ApplyUpdate(Root,stream,Parent.Id,Parent.StartTime.ToUniversalTime().Ticks,delegate{});}
        public void CheckSentinels(){foreach(var item in Sentinels)Check(File.Exists(PathOf(item.Key))&&InstallCore.Hash(PathOf(item.Key))==item.Value,"sentinel changed "+item.Key);}
        public void Stop(){if(!Parent.HasExited){Put("stop.flag","normal exit");Check(Parent.WaitForExit(5000),"fake AA did not exit gracefully");}}
        public void Dispose(){Stop();Parent.Dispose();}
    }
    static string BytesHash(byte[] data){using(var h=System.Security.Cryptography.SHA256.Create())return BitConverter.ToString(h.ComputeHash(data)).Replace("-","");}
    static void NoWrites(Fixture f,Action action,string reason){var before=Directory.GetFiles(f.Root,"*",SearchOption.AllDirectories).ToDictionary(p=>p,InstallCore.Hash);Fails(action,reason);foreach(var item in before)Check(File.Exists(item.Key)&&InstallCore.Hash(item.Key)==item.Value,"failure changed "+item.Key);Check(Directory.GetFiles(f.Root,"*",SearchOption.AllDirectories).Length==before.Count,"failure left added files");}
    static UpdateApplyStatus Status(string directory){var path=Path.Combine(directory,"status.json");if(!File.Exists(path))return null;try{return InstallCore.Json.Deserialize<UpdateApplyStatus>(File.ReadAllText(path));}catch{return null;}}
    static bool Until(Func<bool> action,int milliseconds){var timer=Stopwatch.StartNew();while(timer.ElapsedMilliseconds<milliseconds){if(action())return true;Thread.Sleep(50);}return false;}
    static string Job(Fixture f,Action<UpdateApplyJob> mutate=null)
    {
        var directory=Path.Combine(Path.GetTempPath(),"AzureArchiveRecorderApply",Guid.NewGuid().ToString("N"));Directory.CreateDirectory(directory);File.Copy(helper,Path.Combine(directory,InstallCore.UpdateHelperName));File.Copy(f.Payload,Path.Combine(directory,"payload.zip"));
        var job=new UpdateApplyJob{Root=f.Root,ParentPid=f.Parent.Id,ParentStartUtcTicks=f.Parent.StartTime.ToUniversalTime().Ticks,Version=InstallCore.Version,PayloadPath=Path.Combine(directory,"payload.zip"),PayloadSize=new FileInfo(f.Payload).Length,PayloadSha256=InstallCore.Hash(f.Payload),Token=new string('B',64)};
        if(mutate!=null)mutate(job);File.WriteAllText(Path.Combine(directory,"job.json"),InstallCore.Json.Serialize(job));return directory;
    }
    static Process StartHelper(string directory){return Process.Start(new ProcessStartInfo(Path.Combine(directory,InstallCore.UpdateHelperName),"--job \""+Path.Combine(directory,"job.json")+"\""){UseShellExecute=false,CreateNoWindow=true,WindowStyle=ProcessWindowStyle.Hidden});}
    static ProcessStartInfo HelperStartInfo(string directory)
    {
        return new ProcessStartInfo(Path.Combine(directory,InstallCore.UpdateHelperName),"--job \""+Path.Combine(directory,"job.json")+"\""){UseShellExecute=false,CreateNoWindow=true,WindowStyle=ProcessWindowStyle.Hidden};
    }
    static void RestartEnvironmentTests(string selected=null)
    {
        foreach(var variant in new[]{"true","empty"}) {
            if(selected!=null&&selected!=variant)continue;
            using(var f=new Fixture()) {
                var job=Job(f);var start=HelperStartInfo(job);
                var marker="keep-"+Guid.NewGuid().ToString("N");
                start.EnvironmentVariables["DOORSTOP_DISABLE"]=variant=="empty"?"":"TRUE";
                start.EnvironmentVariables["DOORSTOP_INITIALIZED"]="preserve-initialized";
                start.EnvironmentVariables["AARECORDER_RESTART_TEST_MARKER"]=marker;
                var currentDisable=Environment.GetEnvironmentVariables()["DOORSTOP_DISABLE"];
                using(var process=Process.Start(start)) {
                    Check(Until(()=>Status(job)!=null&&Status(job).State=="Completed",10000),"environment helper never completed");
                    File.WriteAllText(Path.Combine(job,"restart.json"),InstallCore.Json.Serialize(new UpdateRestartRequest{Token=new string('B',64)}));
                    Thread.Sleep(350);f.Stop();
                    Check(process.WaitForExit(5000)&&process.ExitCode==0,"environment helper failed");
                    Check(Until(()=>File.Exists(f.PathOf("restarted.flag")),3000),"environment restart missing");
                    var probe=InstallCore.Json.Deserialize<Dictionary<string,string>>(File.ReadAllText(f.PathOf("restart-environment.json")));
                    Check(probe["DisablePresent"]=="False","DOORSTOP_DISABLE survived restart ("+variant+"); Doorstop would skip BepInEx");
                    Check(probe["DisableValue"]=="<absent>","restart retained disable value");
                    Check(probe["PreserveMarker"]==marker&&probe["Initialized"]=="preserve-initialized","restart changed unrelated environment");
                    Check(String.Equals(probe["WorkingDirectory"],f.Root,StringComparison.OrdinalIgnoreCase),"restart working directory changed");
                    Check(Status(job).State=="Restarting"&&!File.Exists(Path.Combine(job,"payload.zip")),"environment restart did not finish normal cleanup");
                }
                Check(Object.Equals(Environment.GetEnvironmentVariables()["DOORSTOP_DISABLE"],currentDisable),"launching helper changed test parent environment");
                Pass("protected helper removes inherited DOORSTOP_DISABLE="+(variant=="empty"?"<empty>":"TRUE")+" only; preserves other environment and game working directory");
            }
        }
    }
    [System.Runtime.InteropServices.DllImport("kernel32.dll",CharSet=System.Runtime.InteropServices.CharSet.Unicode,SetLastError=true)]
    static extern bool SetEnvironmentVariableW(string name,string value);
    static void RestartStartInfoTests()
    {
        var names=new[]{"DOORSTOP_DISABLE","DOORSTOP_INITIALIZED","AARECORDER_RESTART_TEST_MARKER"};
        var original=Environment.GetEnvironmentVariables();
        try {
            foreach(var disable in new[]{"TRUE",""}) {
                Check(SetEnvironmentVariableW("DOORSTOP_DISABLE",disable),"cannot set native disable fixture");
                Check(SetEnvironmentVariableW("DOORSTOP_INITIALIZED","preserve-initialized"),"cannot set initialized fixture");
                Check(SetEnvironmentVariableW("AARECORDER_RESTART_TEST_MARKER","preserve-other"),"cannot set marker fixture");
                var before=Environment.GetEnvironmentVariables();
                Check(before.Contains("DOORSTOP_DISABLE")&&(string)before["DOORSTOP_DISABLE"]==disable,"native fixture must include disable key even when empty");
                var method=typeof(UpdateHelperProgram).GetMethod("CreateRestartStartInfo",new[]{typeof(string)});
                Check(method!=null,"production restart factory missing");
                var root=Path.GetDirectoryName(fake);
                var start=(ProcessStartInfo)method.Invoke(null,new object[]{root});
                Check(!start.EnvironmentVariables.ContainsKey("DOORSTOP_DISABLE"),"restart factory retained inherited disable key");
                Check(start.EnvironmentVariables["DOORSTOP_INITIALIZED"]=="preserve-initialized"&&start.EnvironmentVariables["AARECORDER_RESTART_TEST_MARKER"]=="preserve-other","restart factory changed unrelated keys");
                Check(!start.UseShellExecute&&start.CreateNoWindow&&start.WindowStyle==ProcessWindowStyle.Normal&&start.WorkingDirectory==root&&start.FileName==Path.Combine(root,"AzureArchive.exe"),"restart factory changed launch configuration");
                var after=Environment.GetEnvironmentVariables();
                Check(before.Count==after.Count&&before.Keys.Cast<string>().All(key=>Object.Equals(before[key],after[key])),"restart factory mutated its own process environment");
                Pass("production restart factory leaves own environment untouched with "+(disable.Length==0?"empty":"TRUE")+" disable key and preserves launch settings");
            }
        }finally{foreach(var name in names)Check(SetEnvironmentVariableW(name,original.Contains(name)?(string)original[name]:null),"failed to restore fixture environment");}
    }
    static void FutureLegacyTests()
    {
        foreach(var unknown in new[]{false,true})using(var f=new Fixture()) {
            Check(InstallCore.Version=="1.2.5","future fixture must compile with simulated 1.2.5 InstallCore");
            foreach(var file in f.Old.Files.Where(p=>p.Path.StartsWith(InstallCore.Mod+"1.2.3/",StringComparison.Ordinal))) {
                var renamed=file.Path.Replace("1.2.3/","1.2.4/");Directory.CreateDirectory(Path.GetDirectoryName(f.PathOf(renamed)));File.Move(f.PathOf(file.Path),f.PathOf(renamed));file.Path=renamed;
            }
            f.Old.Version="1.2.4";
            var previousHelper=InstallCore.Mod+"1.2.4/"+(unknown?"unknown.exe":InstallCore.UpdateHelperName);f.Put(previousHelper,"old helper bytes");f.Old.Files.Add(new OwnedFile{Path=previousHelper,Sha256=InstallCore.Hash(f.PathOf(previousHelper))});f.SaveReceipt();
            f.Put("profiles/Active/modconfig.json","{\"EnabledMods\":[{\"name\":\"AzureArchiveRecorder\",\"version\":\"1.2.4\"},{\"name\":\"OtherMod\",\"version\":\"8\"}]}");
            if(unknown){NoWrites(f,f.Apply,"unknown old EXE accepted");Pass("simulated 1.2.5 refuses unknown EXE in prior version receipt");}
            else {
                f.Apply();var next=InstallCore.Json.Deserialize<Receipt>(File.ReadAllText(f.PathOf(InstallCore.ReceiptName)));Check(next.LegacyFiles.Any(p=>p.Path==previousHelper)&&File.Exists(f.PathOf(previousHelper)),"previous updater ownership lost");
                Check(!next.LegacyFiles.Any(p=>p.Path==InstallCore.Mod+"1.2.5/"+InstallCore.UpdateHelperName),"current updater incorrectly legacy-owned");
                f.Stop();InstallCore.Uninstall(f.Root,delegate{});Check(!File.Exists(f.PathOf(previousHelper))&&!File.Exists(f.PathOf(InstallCore.Mod+"1.2.5/"+InstallCore.UpdateHelperName)),"old/current updater not uninstalled");Check(File.Exists(f.PathOf("mods/OtherMod/file.txt"))&&File.Exists(f.PathOf("RecorderMod/src/Plugin.cs")),"future uninstall touched foreign/source files");Pass("simulated 1.2.4 to 1.2.5 preserves old helper in LegacyFiles and safely uninstalls both helper versions");
            }
        }
    }
    public static int Main(string[] args)
    {
        try {
            workspace=Path.GetFullPath(args[0]);fake=Path.GetFullPath(args[1]);helper=Path.GetFullPath(args[2]);Directory.CreateDirectory(workspace);
            if(args.Length>3&&args[3]=="--restart-environment"){RestartEnvironmentTests(args.Length>4?args[4]:null);RestartStartInfoTests();Console.WriteLine("ALL "+passed+" RESTART ENVIRONMENT CHECKS PASSED");return 0;}
            if(args.Length>3&&args[3]=="--future"){FutureLegacyTests();Console.WriteLine("ALL "+passed+" FUTURE HELPER OWNERSHIP CHECKS PASSED");return 0;}
            using(var f=new Fixture())using(var locked=new FileStream(f.PathOf(InstallCore.Mod+"1.2.3/AzureArchive.Recorder.dll"),FileMode.Open,FileAccess.Read,FileShare.Read))using(var ffmpeg=new FileStream(f.PathOf(InstallCore.Mod+"runtime/ffmpeg/ffmpeg.exe"),FileMode.Open,FileAccess.Read,FileShare.Read)){
                f.Apply();f.CheckSentinels();var next=InstallCore.Json.Deserialize<Receipt>(File.ReadAllText(f.PathOf(InstallCore.ReceiptName)));
                Check(next.Version=="1.2.4"&&next.LegacyFiles.Any(p=>p.Path==InstallCore.Mod+"1.2.3/AzureArchive.Recorder.dll")&&next.LegacyFiles.Any(p=>p.Path==InstallCore.Mod+"1.2.2/AzureArchive.Recorder.dll"),"legacy ownership lost");
                Check(next.GeneratedFiles.Count==1&&next.GeneratedFiles[0].Path.Contains("rtx40"),"package generated hashes replaced local ownership");Check(next.Files.Any(p=>p.Path.EndsWith("obsolete.dat")),"obsolete runtime ownership lost");
                var active=File.ReadAllText(f.PathOf("profiles/Active/modconfig.json"));Check(active.Contains("1.2.4")&&active.Contains("OtherMod")&&active.Contains("42")&&active.Contains("Unknown")&&active.Contains("extra"),"profile fields lost");Check(File.ReadAllText(f.PathOf("profiles/AlsoEnabled/modconfig.json")).Contains("1.2.4"),"inactive enabled profile not migrated");
                Pass("1.2.3 update succeeds with old DLL and unchanged FFmpeg locked; other mods, sources, projects, config and disabled profile unchanged; old/local ownership retained");
            }
            using(var f=new Fixture())using(var locked=new FileStream(f.PathOf(InstallCore.Mod+"使用说明.txt"),FileMode.Open,FileAccess.Read,FileShare.Read)){NoWrites(f,f.Apply,"locked shared file accepted");Pass("sharing violation rolls back new version files and preserves receipt/profile");}
            using(var f=new Fixture()){f.Put(InstallCore.Mod+"1.2.4/AzureArchive.Recorder.dll","new DLL");NoWrites(f,f.Apply,"unknown identical target accepted");Pass("unreceipted target rejected even if bytes match new package");}
            using(var f=new Fixture()){f.Old.Files[0].Path="mods/OtherMod/file.txt";f.SaveReceipt();NoWrites(f,f.Apply,"foreign receipt path accepted");Pass("foreign MOD receipt path rejected before writes");}
            using(var f=new Fixture()){f.MakePayload(null,"hidden.txt");NoWrites(f,f.Apply,"extra ZIP entry accepted");Pass("hidden extra ZIP entry rejected");}
            using(var f=new Fixture()){f.MakePayload(null,null,true);NoWrites(f,f.Apply,"bad file hash accepted");Pass("per-file hash corruption rejected before writes");}
            using(var f=new Fixture()){f.MakePayload(r=>r.Files[0].Path=InstallCore.Mod+"1.2.4/Plugin.cs");NoWrites(f,f.Apply,"source path accepted");Pass("source path in payload rejected");}
            using(var f=new Fixture()){f.Put("profiles/AlsoEnabled/modconfig.json","{broken json");NoWrites(f,f.Apply,"broken profile accepted");Pass("invalid profile rejected before writes");}
            using(var f=new Fixture()){using(var other=Process.Start(new ProcessStartInfo(f.PathOf("AzureArchive.exe"),"--wait \""+f.PathOf("stop-second.flag")+"\""){UseShellExecute=false,CreateNoWindow=true,WindowStyle=ProcessWindowStyle.Hidden})){try{Thread.Sleep(100);NoWrites(f,f.Apply,"duplicate AA accepted");}finally{f.Put("stop-second.flag","normal exit");Check(other.WaitForExit(5000),"second fake AA did not exit");}}Pass("second AA process at same path rejected");}
            foreach(var relative in new[]{"../outside.txt","mods/AzureArchiveRecorder/runtime/a:stream","mods/AzureArchiveRecorder/runtime/CON.txt","mods/AzureArchiveRecorder/runtime/trailing.","mods/AzureArchiveRecorder/runtime/trailing ","mods\\AzureArchiveRecorder\\runtime\\x"})Fails(()=>InstallCore.StrictUpdatePath(workspace,relative),"unsafe path accepted");Pass("traversal, ADS, Windows aliases and noncanonical separators rejected");
            using(var f=new Fixture()){using(var zip=new ZipArchive(new FileStream(f.Payload,FileMode.Open,FileAccess.ReadWrite),ZipArchiveMode.Update))zip.GetEntry(InstallCore.Mod+InstallCore.Version+"/AzureArchive.Recorder.dll").ExternalAttributes=unchecked((int)0xA0000000);NoWrites(f,f.Apply,"ZIP symlink accepted");Pass("ZIP symlink entry rejected");}
            using(var f=new Fixture()){var link=f.PathOf(InstallCore.Mod+"runtime/junction");var target=f.PathOf("mods/OtherMod");using(var create=Process.Start(new ProcessStartInfo("powershell.exe","-NoProfile -NonInteractive -Command \"New-Item -ItemType Junction -Path '"+link.Replace("'","''")+"' -Target '"+target.Replace("'","''")+"' | Out-Null\""){UseShellExecute=false,CreateNoWindow=true,WindowStyle=ProcessWindowStyle.Hidden})){Check(create.WaitForExit(5000)&&create.ExitCode==0,"junction creation failed");}try{f.Old.Files.Add(new OwnedFile{Path=InstallCore.Mod+"runtime/junction/file.txt",Sha256=InstallCore.Hash(f.PathOf("mods/OtherMod/file.txt"))});f.SaveReceipt();Fails(f.Apply,"junction receipt accepted");f.CheckSentinels();}finally{Check((File.GetAttributes(link)&FileAttributes.ReparsePoint)!=0,"junction changed");Directory.Delete(link,false);}Pass("actual junction rejected without touching its foreign target");}
            var rollbackFile=Path.Combine(workspace,"rollback-failure.txt");FileStream rollbackHold=null;bool typed=false;
            try{using(var transaction=new InstallCore.Transaction()){transaction.Write(rollbackFile,Encoding.UTF8.GetBytes("locked new file"));rollbackHold=new FileStream(rollbackFile,FileMode.Open,FileAccess.Read,FileShare.Read);}}catch(InstallRollbackException){typed=true;}finally{if(rollbackHold!=null)rollbackHold.Dispose();}Check(typed,"rollback error not identifiable");Pass("actual rollback failure raises InstallationUncertain classifier exception");
            using(var f=new Fixture()){var job=Job(f,j=>j.ParentStartUtcTicks++);using(var process=StartHelper(job)){Check(process.WaitForExit(10000)&&process.ExitCode!=0,"wrong start-time identity accepted");}Check(Status(job).State=="Failed"&&!Status(job).InstallationUncertain,"prewrite rejection uncertainty incorrect");Pass("protected helper rejects reused PID/start-time identity without installation uncertainty");}
            using(var f=new Fixture()){var job=Job(f,j=>j.PayloadPath=f.Payload);using(var process=StartHelper(job)){Check(process.WaitForExit(10000)&&process.ExitCode!=0,"external payload location accepted");}Check(Status(job).State=="Failed","outside payload did not fail");Pass("protected helper refuses payload outside its dedicated task directory");}
            using(var f=new Fixture()){var job=Job(f,j=>j.PayloadSha256=new string('0',64));using(var process=StartHelper(job)){Check(process.WaitForExit(10000)&&process.ExitCode!=0,"bad ZIP checksum helper did not fail");}Check(Status(job).State=="Failed"&&!Status(job).InstallationUncertain&&File.ReadAllText(f.PathOf(InstallCore.ReceiptName)).Contains("1.2.3"),"bad ZIP helper mutated install");Pass("protected helper rejects whole ZIP hash mismatch and writes Failed");}
            using(var f=new Fixture()){var job=Job(f);using(var process=StartHelper(job)){Check(Until(()=>Status(job)!=null&&Status(job).State=="Completed",10000),"protected helper never completed: "+(Status(job)==null?"missing":Status(job).Error));Check(!process.HasExited&&!f.Parent.HasExited&&!File.Exists(f.PathOf("restarted.flag")),"restarted without OK");using(var duplicate=StartHelper(job)){Check(duplicate.WaitForExit(5000)&&duplicate.ExitCode!=0&&Status(job).State=="Completed","duplicate helper overwrote active status");}File.WriteAllText(Path.Combine(job,"restart.json"),"{\"Token\":\"wrong\"}");Thread.Sleep(350);Check(!process.HasExited&&!File.Exists(f.PathOf("restarted.flag")),"wrong token restarted");f.Stop();Check(process.WaitForExit(5000)&&process.ExitCode==0,"no-OK helper did not finish");Check(!File.Exists(f.PathOf("restarted.flag")),"parent exit without OK restarted");}Pass("Completed waits for OK; wrong token and parent exit without OK never restart");}
            using(var f=new Fixture()){var job=Job(f);using(var process=StartHelper(job)){Check(Until(()=>Status(job)!=null&&Status(job).State=="Completed",10000),"helper never completed");Check(Status(job).Message=="内录MOD更新完成，AA即将重启","completion wording changed");File.WriteAllText(Path.Combine(job,"restart.json"),InstallCore.Json.Serialize(new UpdateRestartRequest{Token=new string('B',64)}));Thread.Sleep(350);Check(!File.Exists(f.PathOf("restarted.flag"))&&!process.HasExited,"restarted before parent normal exit");f.Stop();Check(process.WaitForExit(5000)&&process.ExitCode==0,"confirmed helper failed");Check(Until(()=>File.Exists(f.PathOf("restarted.flag")),3000)&&Status(job).State=="Restarting","confirmed restart missing");Check(!File.Exists(Path.Combine(job,"payload.zip")),"successful update retained large payload");}Pass("protected helper accepts matching OK token, waits for normal parent exit and restarts original AA");}
            using(var f=new Fixture()){var job=Job(f);using(var process=StartHelper(job)){Check(Until(()=>Status(job)!=null&&Status(job).State=="Completed",10000),"helper never completed before abnormal exit test");File.WriteAllText(Path.Combine(job,"restart.json"),InstallCore.Json.Serialize(new UpdateRestartRequest{Token=new string('B',64)}));Thread.Sleep(350);f.Put("stop.flag","abnormal");Check(f.Parent.WaitForExit(5000)&&f.Parent.ExitCode==7,"abnormal fixture did not return nonzero");Check(process.WaitForExit(5000)&&process.ExitCode!=0,"helper restarted abnormal exit");Check(!File.Exists(f.PathOf("restarted.flag"))&&Status(job).State=="Failed"&&Status(job).InstallationUncertain,"abnormal exit status must retain recording lock");Check(File.Exists(Path.Combine(job,"payload.zip")),"uncertain restart failure removed repair payload");}Pass("matching OK followed by abnormal AA exit never restarts and keeps InstallationUncertain true");}
            using(var f=new Fixture())using(var locked=new FileStream(f.PathOf(InstallCore.Mod+"使用说明.txt"),FileMode.Open,FileAccess.Read,FileShare.Read)){var before=File.ReadAllText(f.PathOf(InstallCore.ReceiptName));var job=Job(f);using(var process=StartHelper(job)){Check(process.WaitForExit(10000)&&process.ExitCode!=0,"protected helper did not report rollback");}Check(Status(job).State=="Failed"&&!Status(job).InstallationUncertain&&!File.Exists(Path.Combine(job,"payload.zip")),"known rollback did not safely remove verified payload");Check(File.ReadAllText(f.PathOf(InstallCore.ReceiptName))==before&&!Directory.Exists(f.PathOf(InstallCore.Mod+InstallCore.Version)),"helper rollback left changed installation");Pass("fully rolled-back protected helper removes only its validated payload ZIP");}
            using(var f=new Fixture()){var job=Job(f);var data=UpdateHelperProgram.ReadJob(job);data.PayloadPath=f.Payload;Check(!UpdateHelperProgram.TryDeleteVerifiedPayload(job,data)&&File.Exists(f.Payload)&&File.Exists(Path.Combine(job,"payload.zip")),"cleanup accepted external payload");data.PayloadPath=Path.Combine(job,"payload.zip");data.PayloadSha256=new string('0',64);Check(!UpdateHelperProgram.TryDeleteVerifiedPayload(job,data)&&File.Exists(data.PayloadPath),"cleanup removed identity-mismatched payload");Pass("payload cleanup refuses outside-task and hash-mismatched files");}
            RestartEnvironmentTests();RestartStartInfoTests();
            Console.WriteLine("ALL "+passed+" UPDATE INSTALLER CHECKS PASSED");return 0;
        }catch(Exception ex){Console.Error.WriteLine(ex);return 1;}
    }
}