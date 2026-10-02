using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using AzureArchive.Recorder;
using Mono.Cecil;

// Runs against uniquely named disposable fixtures. Never launches AA itself.
public static class R6InstallerTests
{
    const string Prompt="DLSS组件缺失，请安装超分MOD包后或继续执行内录安装。";
    const string Supplement="mods/AzureArchiveDLSS/";
    static Dictionary<string,string> options;
    static string game,workspace,root;
    static int passed;
    static readonly List<Process> launched=new List<Process>();
    static string Opt(string name){string value;return options.TryGetValue(name,out value)?value:"";}
    static void Check(bool condition,string message){if(!condition)throw new Exception(message);}
    static void Pass(string message){Console.WriteLine("PASS "+(++passed)+": "+message);}
    static string P(string relative){return InstallCore.Within(root,relative);}
    static void Put(string name,string value){var path=P(name);Directory.CreateDirectory(Path.GetDirectoryName(path));File.WriteAllText(path,value,new UTF8Encoding(false));}
    static void Fixture(string name,bool framework)
    {
        root=Path.Combine(workspace,name);Check(!Directory.Exists(root),"Fixture already exists: "+root);Directory.CreateDirectory(root);
        File.Copy(Path.Combine(game,"AzureArchive.exe"),P("AzureArchive.exe"));Directory.CreateDirectory(P("AzureArchive_Data"));
        Put("ActiveProfile.txt","R6");Put("profiles/R6/modconfig.json","{\"EnabledMods\":[{\"name\":\"OtherMod\",\"version\":\"7\"}],\"custom\":42}");
        Put("profiles/R6/configs/azurearchive.recorder.cfg","[Recording]\nFrameRate = 60\nFFmpegPath = "+Path.Combine(game,"RecorderMod","packaging","ffmpeg-vendor","ffmpeg.exe")+"\n[DLSS]\nEnabled = false\n");
        Put("mods/OtherMod/keep.dll","OTHER MOD");Put("用户剧情/保留.aap2","USER PROJECT");Put("用户视频/保留.mp4","USER VIDEO");Put("RecorderMod/src/keep.cs","DEVELOPMENT SOURCE");
        if(framework) {
            foreach(var file in new[]{"BepInEx/core/BepInEx.Core.dll","BepInEx/core/BepInEx.Unity.IL2CPP.dll","BepInEx/core/Il2CppInterop.Runtime.dll","BepInEx/patchers/ModTheAzureArchive.dll","winhttp.dll","dotnet/coreclr.dll","dotnet/hostpolicy.dll","dotnet/System.Private.CoreLib.dll"})Put(file,"dependency existence fixture, not executable framework");
            Put("doorstop_config.ini","[General]\nenabled = true\ntarget_assembly = BepInEx/core/BepInEx.Unity.IL2CPP.dll\n[Il2Cpp]\ncoreclr_path = dotnet/coreclr.dll\ncorlib_dir = dotnet\n");
        }
    }
    static Dictionary<string,string> Snapshot(string directory)
    {
        var values=new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase);
        if(!Directory.Exists(directory))return values;
        values.Add("D:","");
        foreach(var path in Directory.GetDirectories(directory,"*",SearchOption.AllDirectories))values.Add("D:"+path.Substring(directory.Length),"");
        foreach(var path in Directory.GetFiles(directory,"*",SearchOption.AllDirectories))values.Add("F:"+path.Substring(directory.Length),InstallCore.Hash(path));
        return values;
    }
    static void Same(Dictionary<string,string> before,string directory,string context)
    {
        var after=Snapshot(directory);var difference=before.Keys.Union(after.Keys).Where(k=>!before.ContainsKey(k)||!after.ContainsKey(k)||before[k]!=after[k]).ToArray();
        Check(difference.Length==0,context+": changed files/directories: "+String.Join(", ",difference.Take(20)));
    }
    static void MustFail(Action action,string context){bool failed=false;try{action();}catch(Exception ex){failed=true;Console.WriteLine("Expected rejection: "+ex.GetBaseException().Message);}Check(failed,context+" did not reject");}
    sealed class ForbiddenCandidates : IEnumerable<string>
    {
        public IEnumerator<string> GetEnumerator(){throw new Exception("FFmpeg search reached before cancellation");}
        IEnumerator IEnumerable.GetEnumerator(){return GetEnumerator();}
    }
    sealed class ForbiddenStream : Stream
    {
        public override bool CanRead{get{return true;}}public override bool CanSeek{get{return false;}}public override bool CanWrite{get{return false;}}
        public override long Length{get{throw new Exception("Payload read before consent");}}public override long Position{get{throw new Exception("Payload read before consent");}set{throw new NotSupportedException();}}
        public override int Read(byte[] buffer,int offset,int count){throw new Exception("Payload read before consent");}
        public override void Flush(){throw new NotSupportedException();}public override long Seek(long offset,SeekOrigin origin){throw new NotSupportedException();}
        public override void SetLength(long value){throw new NotSupportedException();}public override void Write(byte[] buffer,int offset,int count){throw new NotSupportedException();}
    }
    static void CoreTests()
    {
        Fixture("01 DLSS取消 中文 空格",true);var issues=InstallCore.DetectDependencies(root);Check(issues.Count>0&&issues.All(x=>x.OptionalDlss),"Framework fixture must leave only optional DLSS issues");
        using(var dialog=new DlssInstallDialog(issues)) {
            var controls=dialog.Controls.Cast<Control>().ToArray();
            Check(controls.Any(c=>c.Text==Prompt),"DLSS message must match requested text exactly");
            Check(controls.OfType<Button>().Select(b=>b.Text).OrderBy(x=>x).SequenceEqual(new[]{"安装","取消安装"}.OrderBy(x=>x)),"Unexpected DLSS button labels");
            Check(((Button)dialog.AcceptButton).Text=="取消安装"&&((Button)dialog.CancelButton).Text=="取消安装"&&dialog.ActiveControl==dialog.CancelButton,"DLSS must default to cancel for Enter and Escape");
        }Pass("Exact DLSS wording, two requested buttons, Enter/Escape/default focus all select cancellation");
        int dlss=0,required=0;var before=Snapshot(root);
        using(var stream=new ForbiddenStream())Check(!InstallCore.Install(root,stream,delegate{},delegate{required++;throw new Exception("Unexpected required prompt");},null,new ForbiddenCandidates(),false,delegate{throw new Exception("Unexpected FFmpeg prompt");},delegate(List<DependencyIssue> values){dlss++;Check(values.All(x=>x.OptionalDlss),"Required issue leaked into DLSS prompt");return false;}),"DLSS cancellation did not return false");
        Check(dlss==1&&required==0,"DLSS cancellation callback count");Same(before,root,"DLSS cancellation");Pass("DLSS cancel returns before reading payload/searching FFmpeg and leaves identical files AND directories");
        Fixture("02 两类依赖互斥",false);before=Snapshot(root);dlss=0;required=0;
        using(var stream=new ForbiddenStream())Check(!InstallCore.Install(root,stream,delegate{},delegate(List<DependencyIssue> values){required++;Check(values.Count>0&&values.All(x=>!x.OptionalDlss),"DLSS duplicated in mandatory prompt");return false;},null,new ForbiddenCandidates(),false,null,delegate(List<DependencyIssue> values){dlss++;Check(values.Count>0&&values.All(x=>x.OptionalDlss),"Required issue leaked into optional prompt");return true;}),"Required cancellation ignored");
        Check(dlss==1&&required==1,"Split prompt count");Same(before,root,"Required cancellation after DLSS approval");Pass("Optional/required confirmations are disjoint; later cancellation still leaves a zero-change target");
        int legacy=0;using(var stream=new ForbiddenStream())Check(!InstallCore.Install(root,stream,delegate{},delegate(List<DependencyIssue> values){legacy++;Check(values.Any(x=>x.OptionalDlss)&&values.Any(x=>!x.OptionalDlss),"Legacy callback lost dependencies");return false;},null,new ForbiddenCandidates(),false),"Legacy cancellation ignored");
        Check(legacy==1,"Legacy callback count");Same(before,root,"Legacy cancellation");Pass("Legacy API callers retain combined dependency consent and zero-write cancellation");
        Fixture("03 坏内录负载",true);before=Snapshot(root);
        using(var bad=new MemoryStream(new byte[]{1,2,3,4}))MustFail(delegate{InstallCore.Install(root,bad,delegate{},delegate{return true;},null,new string[0],false,delegate{return true;},delegate{return true;});},"Corrupt ZIP");
        Same(before,root,"Corrupt recorder ZIP");
        foreach(var relative in new[]{"../outside.txt","mods/AzureArchiveRecorder/runtime/file:stream","mods/OtherMod/keep.dll"}) {
            using(var malformed=BadPayload(InstallCore.Product,relative))MustFail(delegate{InstallCore.Install(root,malformed,delegate{},delegate{return true;},null,new string[0],false,delegate{return true;},delegate{return true;});},"Unsafe recorder manifest");
            Same(before,root,"Unsafe recorder manifest");
        }Pass("Corrupt ZIP, traversal, ADS and foreign MOD payload paths reject before any target mutation");
    }
    static MemoryStream BadPayload(string product,string relative)
    {
        var stream=new MemoryStream();var bytes=Encoding.UTF8.GetBytes("BAD PAYLOAD");
        using(var archive=new ZipArchive(stream,ZipArchiveMode.Create,true)) {
            var receipt=new Receipt{Product=product,Version=product==InstallCore.Product?InstallCore.Version:"1.0.0",Files=new List<OwnedFile>{new OwnedFile{Path=relative,Sha256=new string('0',64)}}};
            using(var writer=new StreamWriter(archive.CreateEntry("payload-manifest.json").Open(),new UTF8Encoding(false)))writer.Write(InstallCore.Json.Serialize(receipt));
            using(var output=archive.CreateEntry(relative).Open())output.Write(bytes,0,bytes.Length);
        }stream.Position=0;return stream;
    }
    static string WindowText(IntPtr handle){var value=new StringBuilder(32000);GetWindowText(handle,value,value.Capacity);return value.ToString();}
    static List<IntPtr> Children(IntPtr handle){var values=new List<IntPtr>();EnumChildWindows(handle,delegate(IntPtr child,IntPtr p){values.Add(child);return true;},IntPtr.Zero);return values;}
    static List<IntPtr> Windows(Process process){var values=new List<IntPtr>();EnumWindows(delegate(IntPtr window,IntPtr p){uint id;GetWindowThreadProcessId(window,out id);if(id==process.Id)values.Add(window);return true;},IntPtr.Zero);return values;}
    static string Body(IntPtr window){return WindowText(window)+"\n"+String.Join("\n",Children(window).Select(WindowText));}
    static IntPtr WaitWindow(Process process,string title)
    {
        var clock=Stopwatch.StartNew();while(!process.HasExited&&clock.Elapsed.TotalSeconds<90){var window=Windows(process).FirstOrDefault(h=>WindowText(h)==title&&IsWindowVisible(h));if(window!=IntPtr.Zero){Console.WriteLine("Actual dialog PID "+process.Id+": "+Body(window));return window;}Thread.Sleep(80);}
        throw new Exception("Missing actual dialog '"+title+"', process "+process.Id+(process.HasExited?" exited "+process.ExitCode: " remains running"));
    }
    static void Click(IntPtr window,string caption){var clock=Stopwatch.StartNew();IntPtr button=IntPtr.Zero;while(button==IntPtr.Zero&&clock.Elapsed.TotalSeconds<10){button=Children(window).FirstOrDefault(h=>WindowText(h)==caption);if(button==IntPtr.Zero)Thread.Sleep(80);}Check(button!=IntPtr.Zero,"Missing button: "+caption);Check(PostMessage(button,0x00F5,IntPtr.Zero,IntPtr.Zero),"Cannot post real button click");}
    static Process Start(string exe,bool quiet)
    {
        var process=Process.Start(new ProcessStartInfo(exe,(quiet?"--quiet ":"")+"--root \""+root+"\""){UseShellExecute=false,CreateNoWindow=true});launched.Add(process);Console.WriteLine("Started installer PID "+process.Id+" at fixture "+root);return process;
    }
    static void Exit(Process process,int code)
    {
        var clock=Stopwatch.StartNew();while(!process.WaitForExit(100)&&clock.Elapsed.TotalSeconds<240) {
            var windows=Windows(process);var unexpected=windows.FirstOrDefault(h=>WindowText(h).Contains("错误"));
            if(unexpected!=IntPtr.Zero)throw new Exception("Unexpected error popup: "+Body(unexpected));
        }Check(process.HasExited&&process.ExitCode==code,"Wrong installer result: "+(process.HasExited?process.ExitCode.ToString():"timeout"));
    }
    static void CancelExe(string exe,string name,bool normal,bool close)
    {
        Fixture(name,true);var before=Snapshot(root);var process=Start(exe,!normal);if(normal)Click(WaitWindow(process,"AzureArchive 内录 MOD 0.2.1"),"安装");
        var dialog=WaitWindow(process,"DLSS组件缺失");Check(Children(dialog).Any(h=>WindowText(h)==Prompt),"Actual message not exact");
        Check(Children(dialog).Any(h=>WindowText(h)=="安装")&&Children(dialog).Any(h=>WindowText(h)=="取消安装"),"Actual buttons missing");
        if(close)PostMessage(dialog,0x0010,IntPtr.Zero,IntPtr.Zero);else Click(dialog,"取消安装");
        Exit(process,2);Same(before,root,"Actual EXE cancellation");Pass(name+": actual "+(close?"close X":"取消安装")+" exits 2; no files or directories changed");
    }
    static void RunRecorder(string exe,bool repeated,bool dlssMissing)
    {
        var process=Start(exe,true);if(repeated)Click(WaitWindow(process,"重复安装确认"),"覆盖重新安装");
        if(dlssMissing){var dialog=WaitWindow(process,"DLSS组件缺失");Check(Body(dialog).Contains(Prompt),"DLSS positive prompt wording");Click(dialog,"安装");}
        Exit(process,0);Check(File.Exists(P(InstallCore.ReceiptName)),"Recorder receipt absent");
    }
    static void RunUninstaller(string relative,string receipt)
    {
        Check(File.Exists(P(relative)),"Uninstaller missing: "+relative);var process=Start(P(relative),true);Exit(process,0);
        var clock=Stopwatch.StartNew();while(File.Exists(P(receipt))&&clock.Elapsed.TotalSeconds<90)Thread.Sleep(100);
        Check(!File.Exists(P(receipt)),"Uninstaller helper did not remove receipt: "+receipt);Check(!File.Exists(P(relative)),"Uninstaller did not remove own EXE");
    }
    static Dictionary<string,string> Sentinels(){return new[]{"AzureArchive.exe","mods/OtherMod/keep.dll","用户剧情/保留.aap2","用户视频/保留.mp4","RecorderMod/src/keep.cs"}.ToDictionary(x=>x,x=>InstallCore.Hash(P(x)));}
    static void CheckSentinels(Dictionary<string,string> before){foreach(var item in before)Check(File.Exists(P(item.Key))&&InstallCore.Hash(P(item.Key))==item.Value,"User sentinel changed: "+item.Key);}
    static void VerifyReceipt(string relative,string product)
    {
        var receipt=InstallCore.Json.Deserialize<Receipt>(File.ReadAllText(P(relative)));Check(receipt.Product==product,"Wrong receipt product");
        foreach(var file in receipt.Files)Check(File.Exists(P(file.Path))&&InstallCore.Hash(P(file.Path)).Equals(file.Sha256,StringComparison.OrdinalIgnoreCase),"Installed hash mismatch: "+file.Path);
    }
    static void FinalTests()
    {
        foreach(var item in new[]{new[]{Opt("DevelopmentInstaller"),"未加壳"},new[]{Opt("ProtectedInstaller"),"加壳"}}) {
            CancelExe(item[0],"10 "+item[1]+" quiet取消",false,false);
            CancelExe(item[0],"11 "+item[1]+" 主界面取消",true,false);
            CancelExe(item[0],"12 "+item[1]+" 关闭提示",false,true);
        }
        Fixture("20 双内录真实覆盖",true);var keep=Sentinels();
        RunRecorder(Opt("DevelopmentInstaller"),false,true);VerifyReceipt(InstallCore.ReceiptName,InstallCore.Product);
        Check(!Directory.Exists(P(Supplement.TrimEnd('/'))),"Continue recorder unexpectedly installed DLSS supplement");Pass("Actual 安装 button completes plain recorder install without creating supplemental DLSS product");
        var before=Snapshot(root);var proc=Start(Opt("ProtectedInstaller"),true);Click(WaitWindow(proc,"重复安装确认"),"取消安装");Exit(proc,2);Same(before,root,"Protected overwrite decline");Pass("Actual protected overwrite cancellation preserves full plain installation");
        RunRecorder(Opt("ProtectedInstaller"),true,true);VerifyReceipt(InstallCore.ReceiptName,InstallCore.Product);CheckSentinels(keep);Pass("Actual plain-to-protected overwrite succeeds with explicit confirmations and preserves user files");
        RunRecorder(Opt("DevelopmentInstaller"),true,true);VerifyReceipt(InstallCore.ReceiptName,InstallCore.Product);CheckSentinels(keep);Pass("Actual protected-to-plain overwrite succeeds and preserves user files");
        RunUninstaller(InstallCore.Mod+"卸载内录MOD.exe",InstallCore.ReceiptName);CheckSentinels(keep);Pass("Plain recorder uninstall EXE removes its owned files and itself, preserving user files");
        VerifyResources(Opt("DevelopmentPayload"),false);VerifyResources(Opt("ProtectedPayload"),true);if(Opt("Phase")=="Recorder")return;
        Fixture("30 双MOD互相保留",true);keep=Sentinels();proc=Start(Opt("DlssInstaller"),true);Exit(proc,0);VerifyReceipt(Supplement+"installed-files.json","AzureArchiveDLSSSupplement");
        var tool=P(DlssComponents.ToolRelative);Check(DlssComponents.MissingForInstall(root,tool).Count==0,"Fresh supplemental package is incomplete");
        foreach(var series in new[]{30,40,50})Check(DlssComponents.MatchesRuntime(Path.Combine(tool,"mods","dlss","rtx"+series,"nvngx_dlssnr.dll"),series),"Supplement runtime hash mismatch: "+series);
        Check(InstallCore.DetectDependencies(root).Count==0,"Complete official supplement still triggers missing dependencies");Pass("Real supplemental EXE installs independent receipt/uninstaller and all three pinned runtime hashes");
        var common=Path.Combine(tool,"_internal","vsr_host.dll");var commonBytes=File.ReadAllBytes(common);File.WriteAllBytes(common,new byte[0]);Check(InstallCore.DetectDependencies(root).Any(x=>x.OptionalDlss),"Empty common DLSS DLL incorrectly accepted");File.WriteAllBytes(common,commonBytes);Pass("An empty common DLSS DLL is reported as missing");
        before=Snapshot(root);proc=Start(Opt("DlssInstaller"),true);var repeat=WaitWindow(proc,"DLSS 补充 MOD · 重复安装确认");var cancel=GetDlgItem(repeat,2);Check(cancel!=IntPtr.Zero,"Supplement MessageBox cancel button absent");PostMessage(cancel,0x00F5,IntPtr.Zero,IntPtr.Zero);Exit(proc,2);Same(before,root,"Supplement reinstall cancellation");Pass("Actual supplemental repeat-install cancellation leaves identical files and directories");
        var receiptPath=P(Supplement+"installed-files.json");var receiptText=File.ReadAllText(receiptPath);var forged=InstallCore.Json.Deserialize<Receipt>(receiptText);forged.Files[0].Path=InstallCore.Mod+"runtime/foreign.dll";File.WriteAllText(receiptPath,InstallCore.Json.Serialize(forged));before=Snapshot(root);
        MustFail(delegate{Assembly.GetExecutingAssembly().GetType("DlssSupplementCore",true).GetMethod("Uninstall").Invoke(null,new object[]{root,(Action<string>)delegate{}});},"Foreign recorder path in supplemental receipt");Same(before,root,"Tampered supplemental uninstall");File.WriteAllText(receiptPath,receiptText);Pass("A tampered supplemental uninstall receipt cannot delete recorder files or edit profiles");
        var addonBefore=Snapshot(P(Supplement.TrimEnd('/')));RunRecorder(Opt("DevelopmentInstaller"),false,false);RunUninstaller(InstallCore.Mod+"卸载内录MOD.exe",InstallCore.ReceiptName);Same(addonBefore,P(Supplement.TrimEnd('/')),"Recorder uninstall touched supplemental product");CheckSentinels(keep);Pass("Recorder uninstall preserves every supplemental file and directory");
        // Recorder uninstall removes only its own config; give the next fixture an existing real encoder again.
        Put("profiles/R6/configs/azurearchive.recorder.cfg","[Recording]\nFFmpegPath = "+Path.Combine(game,"RecorderMod","packaging","ffmpeg-vendor","ffmpeg.exe")+"\n");
        RunRecorder(Opt("ProtectedInstaller"),false,false);var recorderBefore=Snapshot(P(InstallCore.Mod.TrimEnd('/')));var configBefore=InstallCore.Hash(P("profiles/R6/configs/azurearchive.recorder.cfg"));
        Put(Supplement+"user-note.txt","UNKNOWN USER NOTE");File.AppendAllText(P(Supplement+"使用说明.txt"),"\nUSER EDIT\n");var modifiedGuide=InstallCore.Hash(P(Supplement+"使用说明.txt"));
        RunUninstaller(Supplement+"卸载DLSS补充MOD.exe",Supplement+"installed-files.json");Same(recorderBefore,P(InstallCore.Mod.TrimEnd('/')),"Supplement uninstall touched recorder");Check(InstallCore.Hash(P("profiles/R6/configs/azurearchive.recorder.cfg"))==configBefore,"Supplement uninstall changed recorder config");CheckSentinels(keep);Pass("Supplement uninstall EXE preserves protected recorder, its settings, and user files");
        Check(InstallCore.Hash(P(Supplement+"使用说明.txt"))==modifiedGuide&&File.ReadAllText(P(Supplement+"user-note.txt"))=="UNKNOWN USER NOTE","Supplement uninstall removed modified/unknown files");Pass("Supplement uninstall preserves modified owned guide and unknown user file");
        RunUninstaller(InstallCore.Mod+"卸载内录MOD.exe",InstallCore.ReceiptName);CheckSentinels(keep);Pass("Protected recorder uninstall EXE removes owned files and itself after supplement removal");
        SupplementBadPayloadTests();
    }
    static void SupplementBadPayloadTests()
    {
        Fixture("40 坏DLSS负载",true);var before=Snapshot(root);var type=Assembly.GetExecutingAssembly().GetType("DlssSupplementCore",true);var method=type.GetMethod("Install",BindingFlags.Public|BindingFlags.Static);Check(method!=null,"Supplement core method missing");
        Action<Stream> install=delegate(Stream stream){method.Invoke(null,new object[]{root,stream,(Action<string>)delegate{},(Func<bool>)delegate{return true;}});};
        using(var bad=new MemoryStream(new byte[]{0,1,2}))MustFail(delegate{install(bad);},"Corrupt supplemental ZIP");Same(before,root,"Corrupt supplemental ZIP");
        foreach(var path in new[]{"../outside.txt",InstallCore.Mod+"runtime/foreign.dll",Supplement+"runtime/test.dll"}) {
            using(var bad=BadPayload("AzureArchiveDLSSSupplement",path))MustFail(delegate{install(bad);},"Malformed supplemental manifest");Same(before,root,"Malformed supplemental manifest");
        }
        using(var original=ZipFile.OpenRead(Opt("DlssPayload")))using(var reader=new StreamReader(original.GetEntry("payload-manifest.json").Open())) {
            var manifest=InstallCore.Json.Deserialize<Receipt>(reader.ReadToEnd());
            using(var bad=new MemoryStream()) {
                using(var zip=new ZipArchive(bad,ZipArchiveMode.Create,true)) {
                    using(var writer=new StreamWriter(zip.CreateEntry("payload-manifest.json").Open()))writer.Write(InstallCore.Json.Serialize(manifest));
                    foreach(var file in manifest.Files)using(var output=zip.CreateEntry(file.Path).Open())output.WriteByte(1);
                }bad.Position=0;MustFail(delegate{install(bad);},"Correct manifest with wrong content hashes");
            }
        }Same(before,root,"Hash-corrupted supplemental payload");Pass("Supplement core rejects corrupt ZIP, traversal, foreign recorder paths and actual content hash mismatch without target changes");
    }
    static byte[] Read(Stream input){using(var memory=new MemoryStream()){input.CopyTo(memory);return memory.ToArray();}}
    static byte[] Resource(ModuleDefinition module,string name){var resource=module.Resources.OfType<EmbeddedResource>().Single(x=>x.Name==name);using(var input=resource.GetResourceStream())return Read(input);}
    static string HashBytes(byte[] bytes){using(var hash=SHA256.Create())return BitConverter.ToString(hash.ComputeHash(bytes)).Replace("-","");}
    static byte[] UnwrapPlugin(byte[] bytes)
    {
        using(var assembly=AssemblyDefinition.ReadAssembly(new MemoryStream(bytes))) {
            var method=assembly.MainModule.Types.Single(t=>t.Name=="RecorderLoader").Methods.Single(m=>m.Name=="Load");
            var strings=method.Body.Instructions.Where(i=>i.OpCode.Code==Mono.Cecil.Cil.Code.Ldstr).Select(i=>(string)i.Operand).ToArray();
            var values=new List<byte[]>();foreach(var value in strings){try{values.Add(Convert.FromBase64String(value));}catch(FormatException){}}
            var key=values.Single(v=>v.Length==32);var iv=values.Single(v=>v.Length==16);var encrypted=Resource(assembly.MainModule,"recorder.payload");
            using(var aes=Aes.Create()){aes.Key=key;aes.IV=iv;using(var decrypt=aes.CreateDecryptor()) {
                var compressed=decrypt.TransformFinalBlock(encrypted,0,encrypted.Length);using(var gzip=new GZipStream(new MemoryStream(compressed),CompressionMode.Decompress)) {
                    var core=Read(gzip);Check(strings.Contains(HashBytes(core)),"Protected core does not match embedded integrity hash");return core;
                }
            }}
        }
    }
    static void VerifyResources(string payload,bool protectedVariant)
    {
        byte[] bytes;using(var zip=ZipFile.OpenRead(payload))using(var stream=zip.GetEntry(InstallCore.Mod+InstallCore.Version+"/AzureArchive.Recorder.dll").Open())bytes=Read(stream);
        if(protectedVariant)bytes=UnwrapPlugin(bytes);
        using(var assembly=AssemblyDefinition.ReadAssembly(new MemoryStream(bytes))) {
            var images=assembly.MainModule.Resources.OfType<EmbeddedResource>().Where(r=>r.Name.StartsWith("recorder.tutorial.arona.")).ToArray();Check(images.Length==6,"Exactly six tutorial illustration resources required");var hashes=new HashSet<string>();
            for(int page=1;page<=6;page++) {
                var name="recorder.tutorial.arona.page-"+page.ToString("00")+".png";var data=Resource(assembly.MainModule,name);
                Check(data.Length>24&&data.Take(8).SequenceEqual(new byte[]{137,80,78,71,13,10,26,10}),"Invalid PNG resource: "+name);
                var asset=Path.Combine(game,"RecorderMod","assets","tutorial",page==1?"arona-guide.png":"arona-page-"+page.ToString("00")+".png");
                Check(File.Exists(asset)&&HashBytes(data)==InstallCore.Hash(asset),"Packaged tutorial image differs from approved source: "+name);
                hashes.Add(HashBytes(data));Console.WriteLine((protectedVariant?"Protected":"Plain")+" resource "+name+" SHA256="+HashBytes(data));
            }Check(hashes.Count==6,"Tutorial pages reuse identical illustration bytes");
        }Pass((protectedVariant?"Protected core decoded only in memory":"Plain plugin")+" contains six distinct intact tutorial PNGs; no assembly execution");
    }
    [STAThread] public static int Main(string[] args)
    {
        try {
            options=InstallCore.Json.Deserialize<Dictionary<string,string>>(File.ReadAllText(args[0]));game=Path.GetFullPath(Opt("GameRoot"));workspace=Path.GetFullPath(Opt("Workspace"));
            Check(workspace.StartsWith(Path.GetFullPath(Path.Combine(Directory.GetParent(game).FullName,"R6安装测试"))+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase),"Fixture root must remain inside the isolated R6 test area");
            Directory.CreateDirectory(workspace);Application.EnableVisualStyles();
            CoreTests();if(Opt("Phase")=="Final"||Opt("Phase")=="Recorder")FinalTests();
            File.WriteAllText(Path.Combine(workspace,"PASS.txt"),"ALL "+passed+" R6 CHECKS PASSED\n"+DateTime.Now.ToString("O"));Console.WriteLine("ALL "+passed+" R6 CHECKS PASSED: "+workspace);return 0;
        }catch(Exception ex){Console.Error.WriteLine(ex);return 1;}
        finally{foreach(var process in launched){try{if(!process.HasExited){Console.Error.WriteLine("Stopping only test-launched installer PID "+process.Id);process.Kill();process.WaitForExit(10000);}}catch{}process.Dispose();}}
    }
    delegate bool WindowCallback(IntPtr window,IntPtr parameter);
    [DllImport("user32.dll")] static extern bool EnumWindows(WindowCallback callback,IntPtr parameter);
    [DllImport("user32.dll")] static extern bool EnumChildWindows(IntPtr parent,WindowCallback callback,IntPtr parameter);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr window,out uint process);
    [DllImport("user32.dll")] static extern IntPtr GetDlgItem(IntPtr window,int id);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr window);
    [DllImport("user32.dll",CharSet=CharSet.Unicode)] static extern int GetWindowText(IntPtr window,StringBuilder text,int count);
    [DllImport("user32.dll")] static extern bool PostMessage(IntPtr window,uint message,IntPtr w,IntPtr l);
}
