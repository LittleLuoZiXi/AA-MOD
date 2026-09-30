using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;

// Runs only against disposable copies below RecorderMod/packaging/ffmpeg-test-*.
// The optional third argument is the saved R3 ZIP, not a live installation.
public static class FfmpegTests
{
    static string game, payload, workspace, root;
    static int passed;
    static void Check(bool ok,string message){if(!ok)throw new Exception(message);}
    static void Pass(string message){Console.WriteLine("PASS "+(++passed)+": "+message);}
    static string P(string name){return Path.Combine(root,name.Replace('/',Path.DirectorySeparatorChar));}
    static string Config {get{return P("profiles/Test/configs/azurearchive.recorder.cfg");}}
    static string PrivateEncoder {get{return P(InstallCore.Mod+"runtime/ffmpeg/ffmpeg.exe");}}
    static string PrivateProbe {get{return P(InstallCore.Mod+"runtime/ffmpeg/ffprobe.exe");}}
    static void Put(string name,string value){var path=P(name);Directory.CreateDirectory(Path.GetDirectoryName(path));File.WriteAllText(path,value,new UTF8Encoding(false));}
    static void Fixture(string name)
    {
        root=Path.Combine(workspace,name);Directory.CreateDirectory(root);
        File.Copy(Path.Combine(game,"AzureArchive.exe"),P("AzureArchive.exe"));Directory.CreateDirectory(P("AzureArchive_Data"));
        Put("ActiveProfile.txt","Test");Put("profiles/Test/modconfig.json","{\"EnabledMods\":[{\"name\":\"OtherMod\",\"version\":\"1.0\"}],\"custom\":42}");
        Put("RecorderMod/src/Plugin.cs","PRESERVE ORIGINAL SOURCE");Put("mods/OtherMod/keep.dll","PRESERVE OTHER MOD");Put("Recordings/user.mp4","PRESERVE USER VIDEO");
    }
    static Dictionary<string,string> Snapshot(){return Directory.GetFiles(root,"*",SearchOption.AllDirectories).ToDictionary(f=>f,f=>InstallCore.Hash(f));}
    static void Unchanged(Dictionary<string,string> before)
    {
        var after=Snapshot();Check(before.Count==after.Count&&before.All(x=>after.ContainsKey(x.Key)&&after[x.Key]==x.Value),"Cancellation modified fixture files");
    }
    static bool Install(IEnumerable<string> candidates,bool defaults,Func<List<string>,bool> repeat=null,Func<List<DependencyIssue>,bool> dependencies=null,Func<bool> bundled=null)
    {
        using(var stream=File.OpenRead(payload))return InstallCore.Install(root,stream,delegate{},dependencies??delegate{return true;},repeat,candidates,defaults,bundled??delegate{return true;});
    }
    static string Quote(string value){Check(value.IndexOf('"')<0,"Unexpected quote in test path");return "\""+value+"\"";}
    static string Run(string exe,string arguments)
    {
        var stdout=new StringBuilder();var stderr=new StringBuilder();
        using(var process=new Process {StartInfo=new ProcessStartInfo(exe,arguments){UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true}}){
            process.OutputDataReceived+=delegate(object sender,DataReceivedEventArgs e){if(e.Data!=null)lock(stdout)stdout.AppendLine(e.Data);};
            process.ErrorDataReceived+=delegate(object sender,DataReceivedEventArgs e){if(e.Data!=null)lock(stderr)stderr.AppendLine(e.Data);};
            Check(process.Start(),"Could not launch test encoder");process.BeginOutputReadLine();process.BeginErrorReadLine();
            if(!process.WaitForExit(30000)){try{process.Kill();}catch{}throw new Exception("Test encoder/probe timed out");}
            process.WaitForExit();Check(process.ExitCode==0,"Tool exited "+process.ExitCode+": "+stderr);return stdout.ToString();
        }
    }
    static void AssertConfigured(string expected)
    {
        Check(File.Exists(Config),"Recorder config missing");
        var lines=File.ReadAllLines(Config);var section="";string found=null;
        foreach(var raw in lines){var line=raw.Trim();if(line.StartsWith("[")&&line.EndsWith("]"))section=line;if(section=="[Recording]"&&line.StartsWith("FFmpegPath = "))found=line.Substring("FFmpegPath = ".Length);}
        Check(String.Equals(found,expected,StringComparison.OrdinalIgnoreCase),"Unexpected encoder configuration: "+found+"; wanted "+expected);
    }
    static void SeedR3(string zip)
    {
        using(var stream=File.OpenRead(zip))using(var archive=new ZipArchive(stream,ZipArchiveMode.Read)){
            Receipt receipt;using(var reader=new StreamReader(archive.GetEntry("payload-manifest.json").Open()))receipt=InstallCore.Json.Deserialize<Receipt>(reader.ReadToEnd());
            Check(!receipt.Files.Any(x=>x.Path.StartsWith(InstallCore.Mod+"runtime/ffmpeg/")),"Expected original R3 payload without bundled FFmpeg");
            foreach(var file in receipt.Files){Check(InstallCore.AllowedOwned(file.Path),"Unsafe legacy fixture path");var target=InstallCore.Within(root,file.Path);Directory.CreateDirectory(Path.GetDirectoryName(target));using(var input=archive.GetEntry(file.Path).Open())using(var output=File.Create(target))input.CopyTo(output);Check(InstallCore.Hash(target)==file.Sha256,"R3 fixture payload hash mismatch");}
            receipt.Root=root;Put(InstallCore.ReceiptName,InstallCore.Json.Serialize(receipt));
        }
        Put("profiles/Test/modconfig.json","{\"EnabledMods\":[{\"name\":\"OtherMod\",\"version\":\"1.0\"},{\"name\":\"AzureArchiveRecorder\",\"version\":\"0.2.1\"}],\"custom\":42}");
    }
    static string BrokenEncoderPayload()
    {
        var damaged=Path.Combine(workspace,"payload-with-invalid-encoder.zip");
        var invalid=Path.Combine(workspace,"invalid-encoder.bin");
        File.WriteAllText(invalid,"INVALID EXECUTABLE: installer must reach the capability check and roll back");
        File.Copy(payload,damaged);
        using(var stream=File.Open(damaged,FileMode.Open,FileAccess.ReadWrite))using(var archive=new ZipArchive(stream,ZipArchiveMode.Update)){
            var manifest=archive.GetEntry("payload-manifest.json");Receipt receipt;
            using(var reader=new StreamReader(manifest.Open()))receipt=InstallCore.Json.Deserialize<Receipt>(reader.ReadToEnd());
            var encoder=receipt.Files.Single(x=>x.Path==InstallCore.Mod+"runtime/ffmpeg/ffmpeg.exe");encoder.Sha256=InstallCore.Hash(invalid);
            archive.GetEntry(encoder.Path).Delete();using(var output=archive.CreateEntry(encoder.Path).Open())using(var input=File.OpenRead(invalid))input.CopyTo(output);
            manifest.Delete();using(var writer=new StreamWriter(archive.CreateEntry("payload-manifest.json").Open(),new UTF8Encoding(false)))writer.Write(InstallCore.Json.Serialize(receipt));
        }
        return damaged;
    }
    sealed class ForbiddenCandidates:IEnumerable<string>
    {
        public IEnumerator<string> GetEnumerator(){throw new Exception("FFmpeg search ran before user approval");}
        IEnumerator IEnumerable.GetEnumerator(){return GetEnumerator();}
    }
    [STAThread] public static int Main(string[] args)
    {
        try{
            var self=System.Reflection.Assembly.GetExecutingAssembly().Location;
            if(Path.GetFileName(self)=="version-only-ffmpeg.exe"){
                if(args.Contains("-version")){Console.WriteLine("ffmpeg version test-without-encoders");return 0;}
                return 9;
            }
            if(Path.GetFileName(self)=="slow-ffmpeg.exe"){System.Threading.Thread.Sleep(30000);return 0;}
            Check(args.Length>=2,"Usage: FfmpegTests.exe game-root new-payload.zip [saved-r3-payload.zip]");
            game=Path.GetFullPath(args[0]);payload=Path.GetFullPath(args[1]);
            workspace=Path.Combine(game,"RecorderMod","packaging","ffmpeg-test-"+DateTime.Now.ToString("yyyyMMdd-HHmmss"));Directory.CreateDirectory(workspace);
            Fixture("无外部工具 中文");
            Put("假工具/ffmpeg.exe","THIS IS NOT AN EXECUTABLE");
            Check(!InstallCore.ProbeFfmpeg(P("假工具/ffmpeg.exe")),"Plain text executable accepted");
            Check(!InstallCore.ProbeFfmpeg(P("不存在/ffmpeg.exe")),"Missing executable accepted");
            Check(InstallCore.FindFfmpeg(root,new[]{P("假工具/ffmpeg.exe"),P("不存在/ffmpeg.exe")},false)==null,"Invalid search candidates accepted");
            File.Copy(self,P("假工具/version-only-ffmpeg.exe"));
            Check(!InstallCore.ProbeFfmpeg(P("假工具/version-only-ffmpeg.exe")),"An executable reporting FFmpeg version but unable to encode was accepted");
            File.Copy(self,P("假工具/slow-ffmpeg.exe"));var timer=Stopwatch.StartNew();
            Check(!InstallCore.ProbeFfmpeg(P("假工具/slow-ffmpeg.exe"),150)&&timer.Elapsed.TotalSeconds<5,"Unresponsive candidate was not bounded by the probe timeout");
            Pass("Missing, fake, encoder-less and unresponsive executables are rejected by real capability checks and bounded timeouts");

            var oldPath="Z:\\不存在的工具\\ffmpeg.exe";
            var ini="# 中文用户注释\r\n[Recording]\r\nFrameRate = 30\r\n; 保留此行\r\nFFmpegPath = "+oldPath+"\r\nOutputDirectory = C:\\Users\\Public\\Videos\\用户输出\r\n[DLSS]\r\nEnabled = false\r\n[Other]\r\nFFmpegPath = untouched\r\n";
            var edited=InstallCore.UpdateFfmpegConfig(ini,PrivateEncoder);
            Check(edited==ini.Replace("FFmpegPath = "+oldPath,"FFmpegPath = "+PrivateEncoder),"Config editor changed comments, line endings, or unrelated keys");
            Pass("Configuration repair preserves comments, CRLF, and all unrelated sections and keys");

            Put("profiles/Test/configs/azurearchive.recorder.cfg",ini);
            var before=Snapshot();Check(!Install(new ForbiddenCandidates(),false,null,delegate{return false;}),"Dependency cancellation was ignored");Unchanged(before);
            Pass("Cancelling before installation performs no FFmpeg search and writes no files");

            var bundledPrompts=0;before=Snapshot();
            Check(!Install(new string[0],false,null,null,delegate{bundledPrompts++;return false;}),"Bundled FFmpeg cancellation was ignored");
            Check(bundledPrompts==1,"Missing FFmpeg did not require explicit consent");Unchanged(before);
            using(var stream=File.OpenRead(payload))Check(!InstallCore.Install(root,stream,delegate{},delegate{return true;},null,new string[0],false),"Missing FFmpeg installed without a confirmation callback");Unchanged(before);
            Pass("Missing FFmpeg prompts before mutation; declining or omitting consent cancels installation without changing any file");

            bundledPrompts=0;Check(Install(new string[0],false,null,null,delegate{bundledPrompts++;return true;}),"Bundled installation cancelled");AssertConfigured(PrivateEncoder);Check(bundledPrompts==1,"Bundled FFmpeg installed without exactly one affirmative callback");
            Check(File.ReadAllText(Config)==ini.Replace("FFmpegPath = "+oldPath,"FFmpegPath = "+PrivateEncoder),"Install changed unrelated settings");
            Check(File.Exists(PrivateEncoder)&&File.Exists(PrivateProbe),"Bundled tools not deployed");
            var receipt=InstallCore.Json.Deserialize<Receipt>(File.ReadAllText(P(InstallCore.ReceiptName)));
            Check(receipt.Files.Any(x=>x.Path==InstallCore.Mod+"runtime/ffmpeg/ffmpeg.exe"&&x.Sha256==InstallCore.Hash(PrivateEncoder))&&receipt.Files.Any(x=>x.Path==InstallCore.Mod+"runtime/ffmpeg/ffprobe.exe"&&x.Sha256==InstallCore.Hash(PrivateProbe)),"Private tools not included in ownership receipt");
            Pass("Fresh install with no external candidates deploys, verifies, configures and owns the bundled tools");

            var movie=P("Recordings/内置编码验证.mp4");
            Run(PrivateEncoder,"-nostdin -hide_banner -loglevel error -y -f lavfi -i testsrc=size=64x64:rate=25 -f lavfi -i sine=frequency=880:sample_rate=48000 -t 0.4 -c:v libx264 -threads 1 -pix_fmt yuv420p -c:a aac -movflags +faststart "+Quote(movie));
            var metadata=InstallCore.Json.Deserialize<Dictionary<string,object>>(Run(PrivateProbe,"-v error -show_entries stream=codec_name,codec_type,width,height,avg_frame_rate:format=duration -of json "+Quote(movie)));
            var streams=((IEnumerable)metadata["streams"]).Cast<Dictionary<string,object>>().ToList();
            Check(streams.Any(x=>(string)x["codec_name"]=="h264"&&Convert.ToInt32(x["width"])==64&&Convert.ToInt32(x["height"])==64&&(string)x["avg_frame_rate"]=="25/1")&&streams.Any(x=>(string)x["codec_name"]=="aac"),"Encoded MP4 does not contain expected H264/AAC streams");
            var duration=Double.Parse((string)((Dictionary<string,object>)metadata["format"])["duration"],System.Globalization.CultureInfo.InvariantCulture);
            Check(duration>=0.39&&duration<=0.5,"Unexpected MP4 duration");
            Run(PrivateEncoder,"-nostdin -v error -i "+Quote(movie)+" -f null -");
            Pass("Bundled binaries really encode and decode an H264/AAC MP4 with verified size, frame rate, and duration");

            var external=Path.Combine(workspace,"用户已有 FFmpeg 工具","ffmpeg.exe");Directory.CreateDirectory(Path.GetDirectoryName(external));File.Copy(PrivateEncoder,external);var externalHash=InstallCore.Hash(external);
            File.WriteAllText(Config,ini.Replace("FFmpegPath = "+oldPath,"FFmpegPath = "+external));var configHash=InstallCore.Hash(Config);
            Check(Install(null,true,delegate{return true;},null,delegate{throw new Exception("Valid configured FFmpeg prompted for installation");}),"Existing-encoder reinstall cancelled");
            AssertConfigured(external);Check(InstallCore.Hash(Config)==configHash&&InstallCore.Hash(external)==externalHash,"Valid existing encoder/config was modified");
            Pass("A valid configured encoder in a Chinese path with spaces is retained without modifying it or other settings");

            var brokenPayload=BrokenEncoderPayload();before=Snapshot();var failedAtProbe=false;
            try{
                using(var stream=File.OpenRead(brokenPayload))InstallCore.Install(root,stream,delegate{},delegate{return true;},delegate{return true;},new[]{external},false,delegate{throw new Exception("Valid explicit FFmpeg prompted for installation");});
            }catch(IOException ex){failedAtProbe=ex.Message.Contains("内置 FFmpeg");}
            Check(failedAtProbe,"Damaged bundled executable did not reach and fail the capability check after payload writes");
            Unchanged(before);Check(InstallCore.Hash(external)==externalHash,"Failed update changed external FFmpeg");
            Pass("A manifest-valid but unusable bundled encoder fails after file writes and restores every original file/config hash without touching external tools or source");

            before=Snapshot();Check(!Install(new ForbiddenCandidates(),false,delegate{return false;}),"Repeat-install cancellation was ignored");Unchanged(before);Check(InstallCore.Hash(external)==externalHash,"Cancel modified external tool");
            Pass("Cancelling reinstall leaves the full installation and existing external tool unchanged");

            var originalPath=Environment.GetEnvironmentVariable("PATH");
            try{
                Fixture("PATH 自动发现 中文");
                Environment.SetEnvironmentVariable("PATH",Path.GetDirectoryName(external));
                Check(Install(null,true,null,null,delegate{throw new Exception("Valid PATH FFmpeg prompted for installation");}),"PATH-based install cancelled");AssertConfigured(external);
                Check(InstallCore.Hash(external)==externalHash,"PATH discovery modified user's tool");
            }finally{Environment.SetEnvironmentVariable("PATH",originalPath);}
            Pass("Fresh installation discovers and configures a real FFmpeg on PATH, including Chinese characters and spaces");

            var saved=new[]{"RecorderMod/src/Plugin.cs","mods/OtherMod/keep.dll","Recordings/user.mp4","AzureArchive.exe"}.ToDictionary(x=>x,x=>InstallCore.Hash(P(x)));
            InstallCore.Uninstall(root,delegate{});
            Check(!File.Exists(PrivateEncoder)&&!File.Exists(PrivateProbe)&&!File.Exists(P(InstallCore.ReceiptName)),"Uninstall left owned FFmpeg or receipt");
            foreach(var file in saved)Check(File.Exists(P(file.Key))&&InstallCore.Hash(P(file.Key))==file.Value,"Uninstall touched unrelated file: "+file.Key);
            Check(File.Exists(external)&&InstallCore.Hash(external)==externalHash,"Uninstall touched external FFmpeg");
            Pass("Uninstall removes only private FFmpeg and preserves the external tool, development source, other MOD, video and AA executable");

            if(args.Length>=3){
                Fixture("R3 覆盖升级 中文");SeedR3(Path.GetFullPath(args[2]));Put("profiles/Test/configs/azurearchive.recorder.cfg",ini);
                var repeats=0;Check(Install(new string[0],false,delegate(List<string> versions){repeats++;Check(versions.Contains("0.2.1"),"Old version not identified");return true;}),"R3 upgrade cancelled");
                Check(repeats==1,"R3 upgrade bypassed reinstall consent");AssertConfigured(PrivateEncoder);
                Check(File.ReadAllText(Config)==ini.Replace("FFmpegPath = "+oldPath,"FFmpegPath = "+PrivateEncoder),"R3 migration changed other settings");
                Check(File.ReadAllText(P("profiles/Test/modconfig.json")).Contains("OtherMod")&&File.ReadAllText(P("profiles/Test/modconfig.json")).Contains("42"),"R3 migration changed other MOD/profile data");
                receipt=InstallCore.Json.Deserialize<Receipt>(File.ReadAllText(P(InstallCore.ReceiptName)));
                foreach(var file in receipt.Files)Check(File.Exists(P(file.Path))&&InstallCore.Hash(P(file.Path))==file.Sha256,"R3 upgraded payload differs from its receipt");
                Pass("A complete R3 installation upgrades through the confirmation path, repairs FFmpeg, and preserves source, settings and other MOD data");
            }
            File.WriteAllText(Path.Combine(workspace,"PASS.txt"),"ALL "+passed+" CHECKS PASSED\r\n"+DateTime.Now);
            Console.WriteLine("ALL "+passed+" CHECKS PASSED: "+workspace);return 0;
        }catch(Exception ex){Console.Error.WriteLine(ex);return 1;}
    }
}
