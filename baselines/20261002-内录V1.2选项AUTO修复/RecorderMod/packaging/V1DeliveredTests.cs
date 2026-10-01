using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using Mono.Cecil;

public static class V1DeliveredTests
{
    static string workspace,game,root,stage;static int passed;
    static readonly List<object> cases=new List<object>();
    static void Check(bool value,string message){if(!value)throw new Exception(message);}
    static void Test(string name,Action test){Console.WriteLine("START "+name);var timer=Stopwatch.StartNew();test();passed++;cases.Add(new{name=name,passed=true,milliseconds=timer.ElapsedMilliseconds});Console.WriteLine("PASS "+passed+": "+name);}
    static string P(string relative){return InstallCore.Within(root,relative);}
    static void Put(string relative,string value){var path=P(relative);Directory.CreateDirectory(Path.GetDirectoryName(path));File.WriteAllText(path,value,new UTF8Encoding(false));}
    static string Digest(byte[] bytes){using(var hash=SHA256.Create())return BitConverter.ToString(hash.ComputeHash(bytes)).Replace("-","");}
    static string S(Dictionary<string,object> proof,string key){return (string)proof[key];}
    static void Fixture(string name)
    {
        root=Path.Combine(workspace,name);Directory.CreateDirectory(root);File.Copy(Path.Combine(game,"AzureArchive.exe"),P("AzureArchive.exe"));Directory.CreateDirectory(P("AzureArchive_Data"));
        Put("ActiveProfile.txt","Test");Put("profiles/Test/modconfig.json","{\"EnabledMods\":[{\"name\":\"OtherMod\",\"version\":\"5\"}],\"custom\":42}");
        Put("profiles/Test/configs/azurearchive.recorder.cfg","# User settings\r\n[Recording]\r\nFrameRate = 60\r\n[DLSS]\r\nEnabled = false\r\n[Tutorial]\r\nSeenVersion = 1\r\n");
        Put("profiles/Test/configs/azurearchive.recorder-state/export-sequence.json","{\"date\":\"20260929\",\"sequence\":52}");
        Put("mods/OtherMod/keep.dll","OTHER MOD");Put("用户剧情/keep.aap2","USER STORY");Put("用户视频/keep.mp4","USER MOVIE");Put("RecorderMod/src/keep.cs","DEVELOPMENT SOURCE");
        Put("mods/AzureArchiveDLSS/runtime/user-note.txt","INDEPENDENT UNKNOWN FILE");
    }
    static Dictionary<string,string> KeepSet()
    {
        return new[]{"AzureArchive.exe","ActiveProfile.txt","profiles/Test/configs/azurearchive.recorder.cfg","profiles/Test/configs/azurearchive.recorder-state/export-sequence.json","mods/OtherMod/keep.dll","用户剧情/keep.aap2","用户视频/keep.mp4","RecorderMod/src/keep.cs","mods/AzureArchiveDLSS/runtime/user-note.txt"}.ToDictionary(x=>x,x=>InstallCore.Hash(P(x)));
    }
    static void Preserve(Dictionary<string,string> keep){foreach(var pair in keep)Check(File.Exists(P(pair.Key))&&InstallCore.Hash(P(pair.Key))==pair.Value,"Preserved file changed: "+pair.Key);}
    static Receipt Manifest(string payload)
    {
        using(var zip=ZipFile.OpenRead(payload))using(var reader=new StreamReader(zip.GetEntry("payload-manifest.json").Open()))return InstallCore.Json.Deserialize<Receipt>(reader.ReadToEnd());
    }
    static void Install(string payload,bool existing)
    {
        int repeats=0;using(var stream=File.OpenRead(payload))Check(InstallCore.Install(root,stream,delegate{},delegate{return true;},delegate{repeats++;return true;},new string[0],false,delegate{return true;},delegate{return true;}),"Real payload install cancelled");
        Check(repeats==(existing?1:0),"Incorrect replacement confirmation count");
        var receipt=InstallCore.Json.Deserialize<Receipt>(File.ReadAllText(P(InstallCore.ReceiptName)));var manifest=Manifest(payload);Check(receipt.Version==InstallCore.Version&&receipt.Files.Count==manifest.Files.Count,"Installed receipt is not the current recorder version");
        foreach(var file in manifest.Files)Check(File.Exists(P(file.Path))&&InstallCore.Hash(P(file.Path)).Equals(file.Sha256,StringComparison.OrdinalIgnoreCase),"Installed final payload hash differs: "+file.Path);
    }
    static void Shell(string file)
    {
        var domain=AppDomain.CreateDomain("V1Shell-"+Guid.NewGuid().ToString("N"));
        try{var inspector=(ProtectionInspection)domain.CreateInstanceFromAndUnwrap(Assembly.GetExecutingAssembly().Location,typeof(ProtectionInspection).FullName);foreach(var line in inspector.Inspect(file))Console.WriteLine(line);}
        finally{AppDomain.Unload(domain);}
    }
    static void Inspect(Dictionary<string,object> proof,bool protect)
    {
        var installer=S(proof,"Installer");Check(InstallCore.Hash(installer)==S(proof,"InstallerSha256"),"Final installer differs from build proof");
        Check(FileVersionInfo.GetVersionInfo(installer).FileVersion==InstallCore.Version+".0","Final EXE file version is not the current recorder version");
        var payload=S(proof,"Payload");var manifest=Manifest(payload);Check(manifest.Version==InstallCore.Version&&manifest.Product==InstallCore.Product,"Payload identity incorrect");
        using(var zip=ZipFile.OpenRead(payload)){
            Check(zip.Entries.Count==manifest.Files.Count+1,"Payload files differ from receipt");
            foreach(var entry in zip.Entries)Check(!entry.FullName.ToLowerInvariant().Contains("nvngx_")&&!entry.FullName.ToLowerInvariant().Contains("vsr_host")&&!entry.FullName.ToLowerInvariant().Contains("dlssnr_host")&&!entry.FullName.ToLowerInvariant().Contains("dlssg_video_worker"),"DLSS native binary embedded in recorder payload: "+entry.FullName);
            Check(zip.GetEntry(InstallCore.Mod+"卸载内录MOD.exe")!=null,"Missing recorder uninstaller");
        }
        var inner=protect?Path.Combine(S(proof,"BuildDirectory"),"AARecorder.Install.Core.exe"):installer;
        using(var assembly=AssemblyDefinition.ReadAssembly(inner)){
            Check(assembly.MainModule.EntryPoint.DeclaringType.Name=="V1Program","Production installer does not use the V1 wizard entry point");
            var payloadResource=assembly.MainModule.Resources.OfType<EmbeddedResource>().Single(r=>r.Name=="installer.payload");
            Check(Digest(payloadResource.GetResourceData())==InstallCore.Hash(payload),"Embedded payload differs from verified ZIP");
            var helper=assembly.MainModule.Resources.OfType<EmbeddedResource>().Single(r=>r.Name=="installer.dlss-uninstaller");
            Check(Digest(helper.GetResourceData())==S(proof,"DlssUninstallerSha256"),"Embedded standalone DLSS uninstaller differs from verified helper");
            Check(helper.GetResourceData().Length<16*1024*1024,"DLSS uninstaller resource unexpectedly contains large component payload");
        }
        foreach(var key in new[]{"Uninstaller","DlssUninstaller"}){
            var exe=S(proof,key);Check(InstallCore.Hash(exe)==S(proof,key+"Sha256"),"Final helper hash differs: "+key);Check(FileVersionInfo.GetVersionInfo(exe).FileVersion==InstallCore.Version+".0","Helper version differs: "+key);
            if(protect)Shell(exe);else using(var assembly=AssemblyDefinition.ReadAssembly(exe))Check(assembly.MainModule.Types.Any(t=>t.Name=="InstallCore")&&!assembly.MainModule.Resources.Any(r=>r.Name=="aa.encrypted-core"),"Unprotected helper is not readable");
        }
        if(protect)Shell(installer);
        using(var plugin=AssemblyDefinition.ReadAssembly(S(proof,"Plugin"))){
            if(protect)Check(plugin.MainModule.Types.Any(t=>t.Name=="RecorderLoader")&&plugin.MainModule.Resources.Any(r=>r.Name=="recorder.payload"),"Protected MOD loader missing");
            else Check(plugin.MainModule.Types.Any(t=>t.FullName=="AzureArchive.Recorder.RecorderPlugin")&&plugin.MainModule.Resources.Count(r=>r.Name.StartsWith("recorder.tutorial.arona.page-"))==6,"Readable MOD or six tutorial pages missing");
        }
    }
    static void SeedNative(string helper)
    {
        var prepared=Directory.GetFiles(Path.Combine(stage,"mods","AzureArchiveDLSS"),"*",SearchOption.AllDirectories)
            .Where(f=>!f.Contains("\\rtx30\\")&&!f.Contains("\\rtx40\\"))
            .ToDictionary(f=>f.Substring(stage.TrimEnd('\\').Length+1).Replace('\\','/'),f=>f,StringComparer.OrdinalIgnoreCase);
        prepared[OnlineDlssInstall.Uninstaller]=helper;
        using(var control=new OnlineDlssControl())Check(OnlineDlssInstall.CommitPrepared(root,50,prepared,control,delegate{}).Completed,"Real component fixture commit failed");
    }
    static void UninstallExe(string relative,string receipt)
    {
        var exe=P(relative);var before=InstallCore.Hash(exe);
        using(var process=Process.Start(new ProcessStartInfo(exe,"--quiet --root \""+root+"\""){UseShellExecute=false,CreateNoWindow=true,WindowStyle=ProcessWindowStyle.Hidden})){
            Check(process.WaitForExit(15000)&&process.ExitCode==0,"Actual uninstall launcher failed: "+relative);
        }
        var timer=Stopwatch.StartNew();while((File.Exists(P(receipt))||File.Exists(exe))&&timer.Elapsed.TotalSeconds<60)Thread.Sleep(100);
        Check(!File.Exists(P(receipt))&&!File.Exists(exe),"Actual uninstaller/helper did not remove its own receipt and EXE: "+relative);
        Console.WriteLine("Actual quiet uninstaller passed; source SHA256="+before);
    }
    [STAThread]public static int Main(string[] args)
    {
        bool success=false;
        try{
            var options=InstallCore.Json.Deserialize<Dictionary<string,string>>(File.ReadAllText(args[0]));game=Path.GetFullPath(options["GameRoot"]);workspace=Path.GetFullPath(options["Workspace"]);stage=Path.GetFullPath(options["DlssFixtureStage"]);
            Check(workspace.StartsWith(Path.Combine(Directory.GetParent(game).FullName,"V1安装测试")+"\\",StringComparison.OrdinalIgnoreCase)&&!Directory.Exists(workspace),"Delivered tests require a new isolated V1 root");Directory.CreateDirectory(workspace);
            var development=InstallCore.Json.Deserialize<Dictionary<string,object>>(File.ReadAllText(options["DevelopmentProof"]));var protect=InstallCore.Json.Deserialize<Dictionary<string,object>>(File.ReadAllText(options["ProtectedProof"]));
            Test("Final readable EXE embeds V1 payload and its readable standalone DLSS uninstaller, with no DLSS native payload",delegate{Inspect(development,false);});
            Test("Final protected EXE and both uninstallers retain AES/GZip in-memory shells; V1 payload identity is correct",delegate{Inspect(protect,true);});
            Test("Final variants have identical owned path sets",delegate{Check(Manifest(S(protect,"Payload")).Files.Select(x=>x.Path).OrderBy(x=>x).SequenceEqual(Manifest(S(development,"Payload")).Files.Select(x=>x.Path).OrderBy(x=>x)),"Variant files would become orphaned");});
            if(options.ContainsKey("Suite")&&options["Suite"]=="Metadata"){success=true;Console.WriteLine("ALL "+passed+" FINAL PACKAGE METADATA CHECKS PASSED: "+workspace);return 0;}
            Dictionary<string,string> keep=null;
            Test("Final protected payload installs and every owned byte matches its receipt",delegate{Fixture("p");Install(S(protect,"Payload"),false);keep=KeepSet();});
            Test("Final protected payload switches to final readable payload without losing user state",delegate{Install(S(development,"Payload"),true);Preserve(keep);});
            Test("Final readable payload switches back to final protected payload without losing user state",delegate{Install(S(protect,"Payload"),true);Preserve(keep);});
            foreach(var pair in new[]{new{Proof=protect,Name="protected"},new{Proof=development,Name="readable"}}){var variant=pair;
                Test("Actual "+variant.Name+" DLSS uninstall EXE preserves recorder and modified/unknown files",delegate{
                    if(variant.Name=="readable"){Fixture("d");Install(S(variant.Proof,"Payload"),false);keep=KeepSet();}
                    SeedNative(S(variant.Proof,"DlssUninstaller"));Put(OnlineDlssInstall.Mod+"使用说明.txt","USER MODIFIED COMPONENT GUIDE");
                    UninstallExe(OnlineDlssInstall.Uninstaller,OnlineDlssInstall.ReceiptName);Preserve(keep);
                    Check(File.ReadAllText(P(OnlineDlssInstall.Mod+"使用说明.txt"))=="USER MODIFIED COMPONENT GUIDE"&&File.Exists(P(InstallCore.ReceiptName)),"DLSS uninstall damaged independent recorder or user files");
                });
                Test("Actual "+variant.Name+" recorder uninstall EXE retains config, tutorial state, sequence and all foreign files",delegate{
                    Put(InstallCore.Mod+"使用说明.txt","USER MODIFIED RECORDER GUIDE");UninstallExe(InstallCore.Mod+"卸载内录MOD.exe",InstallCore.ReceiptName);Preserve(keep);
                    Check(!File.Exists(P(InstallCore.Mod+InstallCore.Version+"/AzureArchive.Recorder.dll"))&&!File.Exists(P(InstallCore.Mod+"runtime/ffmpeg/ffmpeg.exe")),"Owned recorder files remain");
                    Check(File.ReadAllText(P(InstallCore.Mod+"使用说明.txt"))=="USER MODIFIED RECORDER GUIDE","Modified recorder file removed");
                    var profile=File.ReadAllText(P("profiles/Test/modconfig.json"));Check(profile.Contains("OtherMod")&&profile.Contains("42")&&!profile.Contains(InstallCore.Product),"Profile cleanup damaged other MODs or left recorder enabled");
                });
            }
            success=true;Console.WriteLine("ALL "+passed+" FINAL PACKAGE CHECKS PASSED: "+workspace);return 0;
        }catch(Exception error){Console.Error.WriteLine(error);return 1;}
        finally{if(!String.IsNullOrEmpty(workspace)&&Directory.Exists(workspace)){File.WriteAllText(Path.Combine(workspace,"results.json"),InstallCore.Json.Serialize(new{passed=success,count=passed,cases=cases,notes="Final package metadata, payload bytes, variant migration and actual uninstaller execution. Native fixtures are genuine verified R6 bytes; this suite does not download or run GPU inference."}));if(success)File.WriteAllText(Path.Combine(workspace,"PASS.txt"),"ALL "+passed+" FINAL PACKAGE CHECKS PASSED\n"+DateTime.UtcNow.ToString("O"));}}
    }
}
