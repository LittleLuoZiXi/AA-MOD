using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows.Forms;
using AzureArchive.Recorder;

public sealed class InstallRollbackException : IOException { public InstallRollbackException(string message):base(message){} }
public sealed class OwnedFile { public string Path; public string Sha256; }
public sealed class Receipt { public string Product; public string Version; public string Root; public List<OwnedFile> Files; public List<OwnedFile> LegacyFiles; public List<OwnedFile> GeneratedFiles; }
public sealed class DependencyIssue
{
    public string Name, Impact, Detail;
    public bool OptionalDlss;
    public override string ToString(){return "• "+Name+"\r\n  "+Impact+"\r\n  "+Detail;}
}
public static partial class InstallCore
{
    public const string Product="AzureArchiveRecorder", Version="1.2.4";
    public const string Mod="mods/AzureArchiveRecorder/";
    public const string ReceiptName=Mod+"installed-files.json";
    public static readonly JavaScriptSerializer Json=new JavaScriptSerializer { MaxJsonLength=64*1024*1024 };
    public static string HostHash="@@HOSTHASH@@";
    public static string Hash(string file) { using(var s=File.OpenRead(file))using(var h=SHA256.Create())return BitConverter.ToString(h.ComputeHash(s)).Replace("-",""); }
    public static string FullRoot(string root) { return Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar); }
    public static void NoLinks(string path)
    {
        var current=Path.GetFullPath(path);
        while(!String.IsNullOrEmpty(current)) {
            if((Directory.Exists(current)||File.Exists(current)) && (File.GetAttributes(current)&FileAttributes.ReparsePoint)!=0)
                throw new IOException("为避免误操作，拒绝符号链接或目录联接："+current);
            current=Path.GetDirectoryName(current);
        }
    }
    public static string Within(string root,string relative)
    {
        if(String.IsNullOrWhiteSpace(relative)||Path.IsPathRooted(relative)||relative.Contains(":")||relative.Split('/','\\').Any(x=>x==".."||x=="."||x.Length==0))
            throw new IOException("安装清单路径无效："+relative);
        var path=Path.GetFullPath(Path.Combine(root,relative.Replace('/',Path.DirectorySeparatorChar)));
        if(!path.StartsWith(FullRoot(root)+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase))throw new IOException("路径超出安装目录。");
        NoLinks(path);return path;
    }
    public static bool AllowedOwned(string relative)
    {
        return AllowedOwnedForVersion(relative,Version);
    }
    static bool AllowedOwnedForVersion(string relative,string version)
    {
        if(String.IsNullOrEmpty(relative)||!SupportedReceiptVersion(version))return false;
        return relative.StartsWith(Mod+version+"/",StringComparison.Ordinal) || relative.StartsWith(Mod+"runtime/",StringComparison.Ordinal)
            || relative==Mod+"卸载内录MOD.exe" || relative==Mod+"使用说明.txt" || relative==Mod+"第三方许可.txt";
    }
    static bool SupportedReceiptVersion(string version) {return version=="0.2.1"||version=="1.0.0"||version=="1.1.0"||version=="1.2.0"||version=="1.2.1"||version=="1.2.2"||version=="1.2.3"||version=="1.2.4"||version==Version;}
    static bool AllowedLegacy(string path,string receiptVersion)
    {
        if(!SupportedReceiptVersion(receiptVersion))return false;
        // Explicit known releases only, always strictly older than the receipt.
        // Updater executables first shipped in 1.2.4 and are never wildcard-owned.
        var receiptNumber=new Version(receiptVersion);
        return new[]{"0.1.0","0.1.1","0.2.0","0.2.1","1.0.0","1.1.0","1.2.0","1.2.1","1.2.2","1.2.3","1.2.4"}.Any(v=>
            new Version(v)<receiptNumber && (path==Mod+v+"/AzureArchive.Recorder.dll"||path==Mod+v+"/manifest.json"||(new Version(v)>=new Version("1.2.4")&&path==Mod+v+"/更新内录MOD.exe")));
    }
    public static void ValidateGame(string root,bool checkRunning)
    {
        NoLinks(root);
        var exe=Within(root,"AzureArchive.exe");
        if(!File.Exists(exe)||Hash(exe)!=HostHash || !Directory.Exists(Within(root,"AzureArchive_Data")))
            throw new IOException("所选目录不是受支持的 AzureArchive 1.0 fix4 根目录。请将安装包放在 AzureArchive.exe 旁，或选择正确目录。");
        if(checkRunning)foreach(var p in Process.GetProcessesByName("AzureArchive"))using(p) {
            string path;try{path=p.MainModule.FileName;}catch{throw new IOException("无法确认 AA 进程状态。请关闭 AA 后再操作。");}
            if(String.Equals(Path.GetFullPath(path),exe,StringComparison.OrdinalIgnoreCase))throw new IOException("请先关闭此目录中的 AzureArchive，再安装或卸载内录 MOD。");
        }
    }
    static string ActiveProfile(string root)
    {
        var file=Within(root,"ActiveProfile.txt");var name=File.Exists(file)?File.ReadAllText(file).Trim():"Recorder";
        if(String.IsNullOrWhiteSpace(name)||name.IndexOfAny(Path.GetInvalidFileNameChars())>=0||name=="."||name=="..")throw new IOException("当前 profile 名称无效。");
        return name;
    }
    static Dictionary<string,string> ReadIni(string path)
    {
        var values=new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase);var section="";
        if(!File.Exists(path))return values;
        foreach(var raw in File.ReadAllLines(path)){
            var line=raw.Trim();if(line.Length==0||line.StartsWith("#")||line.StartsWith(";"))continue;
            if(line.StartsWith("[")&&line.EndsWith("]")){section=line.Substring(1,line.Length-2).Trim();continue;}
            var split=line.IndexOf('=');if(split<1)continue;
            values[section+"/"+line.Substring(0,split).Trim()]=line.Substring(split+1).Trim();
        }
        return values;
    }
    static string Setting(Dictionary<string,string> values,string key,string fallback){string value;return values.TryGetValue(key,out value)?value:fallback;}
    static string DependencyPath(string root,string value)
    {
        if(String.IsNullOrWhiteSpace(value))return "";
        try{return Path.GetFullPath(Path.IsPathRooted(value)?value:Path.Combine(root,value));}catch(ArgumentException){return "";}catch(NotSupportedException){return "";}
    }
    static void MissingFiles(List<DependencyIssue> issues,string name,string impact,string directory,params string[] files)
    {
        var missing=files.Where(f=>String.IsNullOrEmpty(directory)||!File.Exists(Path.Combine(directory,f))||new FileInfo(Path.Combine(directory,f)).Length==0).ToArray();
        if(missing.Length>0)issues.Add(new DependencyIssue{Name=name,Impact=impact,Detail="缺少："+String.Join("、",missing)});
    }
    static bool RunFfmpegCheck(string executable,string arguments,string expectedPrefix,int timeoutMilliseconds)
    {
        if(timeoutMilliseconds<1)throw new ArgumentOutOfRangeException("timeoutMilliseconds");
        if(String.IsNullOrWhiteSpace(executable)||!File.Exists(executable))return false;
        try {
            using(var process=new Process()) {
                process.StartInfo=new ProcessStartInfo(executable,arguments){UseShellExecute=false,CreateNoWindow=true,WindowStyle=ProcessWindowStyle.Hidden,RedirectStandardOutput=true,RedirectStandardError=true};
                var output=new StringBuilder();var gate=new object();var stdout=new TaskCompletionSource<bool>();var stderr=new TaskCompletionSource<bool>();
                Action<string> collect=delegate(string line){if(line!=null)lock(gate){if(output.Length<16384)output.AppendLine(line);}};
                process.OutputDataReceived+=delegate(object sender,DataReceivedEventArgs e){if(e.Data==null)stdout.TrySetResult(true);else collect(e.Data);};
                process.ErrorDataReceived+=delegate(object sender,DataReceivedEventArgs e){if(e.Data==null)stderr.TrySetResult(true);else collect(e.Data);};
                var watch=Stopwatch.StartNew();
                if(!process.Start())return false;process.BeginOutputReadLine();process.BeginErrorReadLine();
                if(!process.WaitForExit(timeoutMilliseconds)){try{process.Kill();process.WaitForExit(2000);}catch{}return false;}
                // A child inheriting the output handles must not make the version check wait forever.
                if(!Task.WaitAll(new Task[]{stdout.Task,stderr.Task},Math.Max(1,timeoutMilliseconds-(int)Math.Min(watch.ElapsedMilliseconds,timeoutMilliseconds))))return false;
                if(process.ExitCode!=0)return false;
                lock(gate)return expectedPrefix==null||output.ToString().TrimStart().StartsWith(expectedPrefix,StringComparison.OrdinalIgnoreCase);
            }
        }catch(System.ComponentModel.Win32Exception){return false;}catch(IOException){return false;}catch(InvalidOperationException){return false;}catch(UnauthorizedAccessException){return false;}
    }
    public static bool ProbeFfmpeg(string executable,int timeoutMilliseconds=10000)
    {
        if(!RunFfmpegCheck(executable,"-nostdin -version","ffmpeg version",timeoutMilliseconds))return false;
        // Exercise the CPU fallback and the actual MP4 muxer without creating a recording or requiring a GPU.
        return RunFfmpegCheck(executable,"-nostdin -hide_banner -loglevel error -f lavfi -i color=c=black:s=16x16:r=10 -f lavfi -i anullsrc=r=48000:cl=stereo -t 0.1 -c:v libx264 -pix_fmt yuv420p -threads 1 -c:a aac -f mp4 -movflags frag_keyframe+empty_moov -y NUL",null,timeoutMilliseconds);
    }
    static IEnumerable<string> MatchingToolDirectories(string parent,string pattern)
    {
        if(String.IsNullOrEmpty(parent)||!Directory.Exists(parent))return new string[0];
        try{return Directory.EnumerateDirectories(parent,pattern,SearchOption.TopDirectoryOnly).Take(12).ToArray();}
        catch(IOException){return new string[0];}catch(UnauthorizedAccessException){return new string[0];}
    }
    static IEnumerable<string> DefaultFfmpegCandidates(string root)
    {
        var config=ReadIni(Within(root,"profiles/"+ActiveProfile(root)+"/configs/azurearchive.recorder.cfg"));
        var configured=Setting(config,"Recording/FFmpegPath","");if(!String.IsNullOrWhiteSpace(configured))yield return configured;
        foreach(var directory in (Environment.GetEnvironmentVariable("PATH")??"").Split(Path.PathSeparator).Take(64))
            if(!String.IsNullOrWhiteSpace(directory))yield return directory.Trim().Trim('"')+Path.DirectorySeparatorChar+"ffmpeg.exe";
        foreach(var relative in new[]{"ffmpeg.exe","ffmpeg/ffmpeg.exe","ffmpeg/bin/ffmpeg.exe","tools/ffmpeg.exe","tools/ffmpeg/bin/ffmpeg.exe",Mod+"runtime/ffmpeg/ffmpeg.exe"})yield return Path.Combine(root,relative);
        var tool=Setting(config,"DLSS/ToolDirectory","");if(!String.IsNullOrWhiteSpace(tool))yield return tool.TrimEnd('/','\\')+"/_internal/ffmpeg.exe";
        var parent=Path.GetDirectoryName(root);
        foreach(var directory in MatchingToolDirectories(parent,"DLSS5Tool*"))yield return Path.Combine(directory,"_internal","ffmpeg.exe");
        foreach(var directory in MatchingToolDirectories(root,"DLSS5Tool*"))yield return Path.Combine(directory,"_internal","ffmpeg.exe");
        foreach(var directory in MatchingToolDirectories(parent,"ffmpeg*")){yield return Path.Combine(directory,"bin","ffmpeg.exe");yield return Path.Combine(directory,"ffmpeg.exe");}
        foreach(var programFiles in new[]{Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86)})
            foreach(var directory in MatchingToolDirectories(programFiles,"ffmpeg*")){yield return Path.Combine(directory,"bin","ffmpeg.exe");yield return Path.Combine(directory,"ffmpeg.exe");}
        var systemDrive=Path.GetPathRoot(Environment.GetFolderPath(Environment.SpecialFolder.Windows));
        yield return Path.Combine(systemDrive,"ffmpeg","bin","ffmpeg.exe");
        yield return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),"chocolatey","bin","ffmpeg.exe");
        yield return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),"scoop","apps","ffmpeg","current","bin","ffmpeg.exe");
        yield return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"Microsoft","WinGet","Links","ffmpeg.exe");
    }
    public static string FindFfmpeg(string root,IEnumerable<string> candidates=null,bool includeDefaultLocations=true)
    {
        root=FullRoot(root);
        var defaults=includeDefaultLocations?DefaultFfmpegCandidates(root):Enumerable.Empty<string>();
        var paths=defaults.Concat(candidates??Enumerable.Empty<string>());
        var visited=new HashSet<string>(StringComparer.OrdinalIgnoreCase);int attempts=0;
        foreach(var candidate in paths.Take(192)) {
            var path=DependencyPath(root,candidate);if(path.Length==0||!visited.Add(path)||!File.Exists(path))continue;
            if(++attempts>16)break;
            if(ProbeFfmpeg(path))return path;
        }
        return null;
    }
    public static string UpdateFfmpegConfig(string original,string executable)
    {
        if(String.IsNullOrWhiteSpace(executable)||executable.IndexOfAny(new[]{'\r','\n'})>=0)throw new ArgumentException("FFmpeg 路径无效。","executable");
        original=original??"";
        var newline=original.Contains("\r\n")?"\r\n":original.Contains("\n")?"\n":"\r\n";
        var result=new StringBuilder();bool recording=false,sectionFound=false,keyFound=false;
        Action appendKey=delegate{if(result.Length>0&&result[result.Length-1]!='\n'&&result[result.Length-1]!='\r')result.Append(newline);result.Append("FFmpegPath = ").Append(executable).Append(newline);keyFound=true;};
        foreach(Match item in Regex.Matches(original,@"[^\r\n]*(?:\r\n|\r|\n|$)")) {
            if(item.Length==0)continue;var raw=item.Value;var line=raw.TrimEnd('\r','\n');var trimmed=line.Trim();
            if(trimmed.StartsWith("[")&&trimmed.EndsWith("]")) {
                if(recording&&!keyFound)appendKey();
                recording=trimmed.Substring(1,trimmed.Length-2).Trim().Equals("Recording",StringComparison.OrdinalIgnoreCase);sectionFound|=recording;
            } else if(recording&&!trimmed.StartsWith("#")&&!trimmed.StartsWith(";")) {
                var split=line.IndexOf('=');
                if(split>=0&&line.Substring(0,split).Trim().Equals("FFmpegPath",StringComparison.OrdinalIgnoreCase)) {
                    keyFound=true;
                    if(!line.Substring(split+1).Trim().Equals(executable,StringComparison.OrdinalIgnoreCase)) {
                        var start=split+1;while(start<line.Length&&(line[start]==' '||line[start]=='\t'))start++;
                        raw=line.Substring(0,start)+executable+raw.Substring(line.Length);
                    }
                }
            }
            result.Append(raw);
        }
        if(!sectionFound){if(result.Length>0&&result[result.Length-1]!='\n'&&result[result.Length-1]!='\r')result.Append(newline);result.Append("[Recording]").Append(newline);}
        if(!keyFound)appendKey();
        return result.ToString();
    }
    public static List<DependencyIssue> DetectDependencies(string root)
    {
        root=FullRoot(root);ValidateGame(root,false);
        var issues=new List<DependencyIssue>();
        var config=ReadIni(Within(root,"profiles/"+ActiveProfile(root)+"/configs/azurearchive.recorder.cfg"));
        var doorstopPath=Within(root,"doorstop_config.ini");var doorstop=ReadIni(doorstopPath);
        MissingFiles(issues,"BepInEx IL2CPP 框架（必需）","缺失时 AA 无法加载此 MOD。",Within(root,"BepInEx/core"),"BepInEx.Core.dll","BepInEx.Unity.IL2CPP.dll","Il2CppInterop.Runtime.dll");
        MissingFiles(issues,"AA MOD 管理器（必需）","缺失时无法按 profile 启用内录。",Within(root,"BepInEx/patchers"),"ModTheAzureArchive.dll");
        var enabled=Setting(doorstop,"General/enabled","");
        if(!File.Exists(Within(root,"winhttp.dll"))||!File.Exists(doorstopPath)||!enabled.Equals("true",StringComparison.OrdinalIgnoreCase))
            issues.Add(new DependencyIssue{Name="MOD 启动入口（必需）",Impact="缺失或禁用时 MOD 不会启动。",Detail="需要 winhttp.dll、doorstop_config.ini，并在 [General] 中设置 enabled = true。"});
        var target=DependencyPath(root,Setting(doorstop,"General/target_assembly","BepInEx/core/BepInEx.Unity.IL2CPP.dll"));
        if(File.Exists(doorstopPath)&&(!File.Exists(target)||!Path.GetFileName(target).Equals("BepInEx.Unity.IL2CPP.dll",StringComparison.OrdinalIgnoreCase)))
            issues.Add(new DependencyIssue{Name="BepInEx 启动目标（必需）",Impact="当前 Doorstop 入口未正确指向 IL2CPP 框架。",Detail="检查 [General] 的 target_assembly 配置。"});
        var coreclr=DependencyPath(root,Setting(doorstop,"Il2Cpp/coreclr_path","dotnet/coreclr.dll"));
        var corlib=DependencyPath(root,Setting(doorstop,"Il2Cpp/corlib_dir","dotnet"));
        if(!File.Exists(coreclr)||String.IsNullOrEmpty(corlib)||!File.Exists(Path.Combine(corlib,"System.Private.CoreLib.dll"))||!File.Exists(Path.Combine(corlib,"hostpolicy.dll")))
            issues.Add(new DependencyIssue{Name="AA 内置 .NET 运行时（必需）",Impact="缺失时 BepInEx / 内录 MOD 无法运行。",Detail="需要 coreclr.dll、hostpolicy.dll、System.Private.CoreLib.dll（按 Doorstop 配置检测）。"});
        var tool=DlssComponents.ResolveTool(root,Setting(config,"DLSS/ToolDirectory",""));
        var internals=tool.Length>0?Path.Combine(tool,"_internal"):"";
        var firstDlss=issues.Count;
        MissingFiles(issues,"DLSS5Tool 视频工具（增强可选）","缺失仅影响增强处理；普通 MP4 内录已内置 FFmpeg。",internals,"ffmpeg.EXE","ffprobe.exe");
        MissingFiles(issues,"RTX 超分组件（增强可选）","缺失时无法进行超分。",internals,"vsr_host.dll","nvngx_vsr.dll");
        MissingFiles(issues,"DLSS 补帧组件（增强可选）","缺失时无法进行补帧。",internals,"dlssg_video_worker.exe","nvngx_dlssg.dll");
        MissingFiles(issues,"DLSS 神经渲染组件（增强可选）","缺失时无法开启神经渲染。",internals,"dlssnr_host_v2.dll");
        var runtimeMissing=DlssComponents.MissingForInstall(root,tool).Where(item=>!DlssComponents.RequiredFiles.Contains(item,StringComparer.OrdinalIgnoreCase)).ToArray();
        if(runtimeMissing.Length>0)issues.Add(new DependencyIssue{Name="DLSS 显卡运行库（增强可选）",Impact="安装超分 MOD 补充包可离线提供 RTX 30 / 40 / 50 系组件。",Detail=String.Join("、",runtimeMissing)});
        foreach(var issue in issues.Skip(firstDlss))issue.OptionalDlss=true;
        return issues;
    }
    static Dictionary<string,object> ReadProfile(string path)
    {
        var data=File.Exists(path)?Json.Deserialize<Dictionary<string,object>>(File.ReadAllText(path)):new Dictionary<string,object>();
        if(data==null)throw new IOException("MOD 配置为空或格式错误："+path);
        object raw;
        if(data.TryGetValue("EnabledMods",out raw) && !(raw is System.Collections.IEnumerable) || raw is string)
            throw new IOException("EnabledMods 格式错误："+path);
        return data;
    }
    static string ChangeProfile(string path,bool enable,bool onlyIfPresent=false)
    {
        var data=ReadProfile(path);var result=new List<object>();object raw;bool found=false;Dictionary<string,object> own=null;
        if(data.TryGetValue("EnabledMods",out raw))foreach(var entry in (System.Collections.IEnumerable)raw) {
            var item=entry as Dictionary<string,object>;object name;
            if(item==null || !item.TryGetValue("name",out name) || !(name is string))throw new IOException("MOD 配置项目格式错误："+path);
            if(!String.Equals((string)name,Product,StringComparison.Ordinal))result.Add(item);
            else {found=true;if(own==null)own=new Dictionary<string,object>(item);}
        }
        if(!found&&(!enable||onlyIfPresent))return null;
        if(enable){if(own==null)own=new Dictionary<string,object>();own["name"]=Product;own["version"]=Version;result.Add(own);}
        data["EnabledMods"]=result;return Json.Serialize(data);
    }
    static Dictionary<string,string> ProfilesForInstall(string root,string activeProfile)
    {
        var changes=new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase);
        var active=Within(root,"profiles/"+activeProfile+"/modconfig.json");
        changes.Add(active,ChangeProfile(active,true));
        var profiles=Within(root,"profiles");
        if(Directory.Exists(profiles))foreach(var directory in Directory.EnumerateDirectories(profiles)) {
            NoLinks(directory);var path=Path.Combine(directory,"modconfig.json");NoLinks(path);
            if(!File.Exists(path)||String.Equals(path,active,StringComparison.OrdinalIgnoreCase))continue;
            // Never turn the MOD on in a profile where the user left it disabled.
            // Previously enabled profiles migrate together, so no old DLL stays selected.
            var changed=ChangeProfile(path,true,true);
            if(changed!=null)changes.Add(path,changed);
        }
        return changes;
    }
    public sealed class Transaction : IDisposable
    {
        readonly Dictionary<string,byte[]> before=new Dictionary<string,byte[]>(StringComparer.OrdinalIgnoreCase);
        readonly List<string> dirs=new List<string>();bool complete;
        public void MakeDirectory(string dir) { if(Directory.Exists(dir))return;MakeDirectory(Path.GetDirectoryName(dir));Directory.CreateDirectory(dir);dirs.Add(dir); }
        public void Track(string file) { if(!before.ContainsKey(file))before.Add(file,File.Exists(file)?File.ReadAllBytes(file):null); }
        public void Write(string file,byte[] bytes) { Track(file);MakeDirectory(Path.GetDirectoryName(file));Atomic(file,bytes); }
        public void Delete(string file) { if(!File.Exists(file))return;Track(file);File.Delete(file); }
        public void Commit(){complete=true;}
        public void Dispose() {
            if(complete)return;var errors=new List<string>();
            foreach(var entry in before.Reverse())try{
                if(entry.Value==null){if(File.Exists(entry.Key))File.Delete(entry.Key);}
                else if(!File.Exists(entry.Key)||!File.ReadAllBytes(entry.Key).SequenceEqual(entry.Value))Atomic(entry.Key,entry.Value);
            }catch(Exception ex){errors.Add(ex.Message);}
            foreach(var dir in dirs.AsEnumerable().Reverse())try{if(Directory.Exists(dir)&&!Directory.EnumerateFileSystemEntries(dir).Any())Directory.Delete(dir,false);}catch(Exception ex){errors.Add(ex.Message);}
            if(errors.Count>0)throw new InstallRollbackException("安装失败且回滚未完全完成，请保留现场："+String.Join("；",errors));
        }
    }
    static void Atomic(string file,byte[] bytes)
    {
        var temp=file+".aa-install-"+Guid.NewGuid().ToString("N");
        try { File.WriteAllBytes(temp,bytes);if(File.Exists(file))File.Replace(temp,file,null);else File.Move(temp,file); }
        finally { if(File.Exists(temp))File.Delete(temp); }
    }
    static void ValidateReceipt(string root,Receipt receipt,bool allowPreviousVersion=false)
    {
        if(receipt==null||receipt.Product!=Product||(receipt.Version!=Version&&!(allowPreviousVersion&&SupportedReceiptVersion(receipt.Version)))||!String.Equals(FullRoot(receipt.Root),root,StringComparison.OrdinalIgnoreCase)||receipt.Files==null||receipt.Files.Count==0)
            throw new IOException("卸载清单身份不匹配，已停止，未删除任何文件。");
        var paths=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach(var file in receipt.Files){if(file==null||!AllowedOwnedForVersion(file.Path,receipt.Version)||!paths.Add(file.Path)||!Regex.IsMatch(file.Sha256??"","^[0-9A-Fa-f]{64}$"))throw new IOException("卸载清单包含越界、重复或无效项目。");Within(root,file.Path);}
        foreach(var file in receipt.LegacyFiles??new List<OwnedFile>()){if(file==null||!AllowedLegacy(file.Path,receipt.Version)||!paths.Add(file.Path)||!Regex.IsMatch(file.Sha256??"","^[0-9A-Fa-f]{64}$"))throw new IOException("旧版清单路径无效。");Within(root,file.Path);}
        foreach(var file in receipt.GeneratedFiles??new List<OwnedFile>()){if(file==null||!new[]{30,40,50}.Any(s=>file.Path==Mod+"runtime/gpu-runtimes/rtx"+s+"/nvngx_dlssnr.dll")||!paths.Add(file.Path)||!Regex.IsMatch(file.Sha256??"","^[0-9A-Fa-f]{64}$"))throw new IOException("运行库缓存清单无效。");Within(root,file.Path);}
    }
    public static List<string> FindExistingInstallation(string root,Receipt receipt)
    {
        var versions=new List<string>();if(receipt!=null)versions.Add(receipt.Version);
        foreach(var version in new[]{"0.1.0","0.1.1","0.2.0","0.2.1","1.0.0","1.1.0","1.2.0","1.2.1","1.2.2","1.2.3",Version}) {
            var manifest=Within(root,Mod+version+"/manifest.json");if(!File.Exists(manifest))continue;
            try {var data=Json.Deserialize<Dictionary<string,object>>(File.ReadAllText(manifest));object name;if(data!=null&&data.TryGetValue("name",out name)&&String.Equals(name as string,Product,StringComparison.Ordinal)&&!versions.Contains(version))versions.Add(version);}catch(ArgumentException){}
        }
        if(versions.Count==0&&File.Exists(Within(root,Mod+Version+"/AzureArchive.Recorder.dll")))versions.Add(Version+"（检测到运行文件）");
        return versions;
    }
    public static bool Install(string root,Stream payload,Action<string> progress,Func<List<DependencyIssue>,bool> confirmDependencies=null,Func<List<string>,bool> confirmReinstall=null,IEnumerable<string> ffmpegCandidates=null,bool searchDefaultFfmpegLocations=true,Func<bool> confirmBundledFfmpeg=null,Func<List<DependencyIssue>,bool> confirmDlssMissing=null)
    {
        root=FullRoot(root);ValidateGame(root,true);
        string active=Within(root,"ActiveProfile.txt");
        string profile=ActiveProfile(root);
        var profilePath=Within(root,"profiles/"+profile+"/modconfig.json");
        var profileChanges=ProfilesForInstall(root,profile); // Validate all profiles before any mutation.
        var receiptPath=Within(root,ReceiptName);Receipt old=null;
        if(File.Exists(receiptPath)){old=Json.Deserialize<Receipt>(File.ReadAllText(receiptPath));ValidateReceipt(root,old,true);}
        var existing=FindExistingInstallation(root,old);
        if(existing.Count>0&&(confirmReinstall==null||!confirmReinstall(existing)))return false;
        var dependencies=DetectDependencies(root);
        if(confirmDlssMissing!=null) {
            var dlss=dependencies.Where(issue=>issue.OptionalDlss).ToList();
            if(dlss.Count>0&&!confirmDlssMissing(dlss))return false;
            dependencies=dependencies.Where(issue=>!issue.OptionalDlss).ToList();
        }
        if(dependencies.Count>0 && (confirmDependencies==null||!confirmDependencies(dependencies)))return false;
        ValidateGame(root,true); // The user may have started AA while the prompt was open.
        using(var archive=new ZipArchive(payload,ZipArchiveMode.Read,true)) {
            var manifest=archive.GetEntry("payload-manifest.json");if(manifest==null)throw new IOException("安装包清单缺失。");
            Receipt next;using(var reader=new StreamReader(manifest.Open(),Encoding.UTF8))next=Json.Deserialize<Receipt>(reader.ReadToEnd());
            next.Root=root;ValidateReceipt(root,next);
            var names=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach(var entry in archive.Entries)if(!names.Add(entry.FullName))throw new IOException("安装包包含重复文件。");
            if(archive.Entries.Count!=next.Files.Count+1)throw new IOException("安装包文件数量不符。");
            foreach(var file in next.Files) {
                var target=Within(root,file.Path);var entry=archive.GetEntry(file.Path);if(entry==null)throw new IOException("安装包文件缺失："+file.Path);
                using(var data=entry.Open())using(var hash=SHA256.Create())if(!String.Equals(BitConverter.ToString(hash.ComputeHash(data)).Replace("-",""),file.Sha256,StringComparison.OrdinalIgnoreCase))throw new IOException("安装包文件校验失败："+file.Path);
                if(File.Exists(target) && !String.Equals(Hash(target),file.Sha256,StringComparison.OrdinalIgnoreCase) && (old==null||!old.Files.Any(x=>String.Equals(x.Path,file.Path,StringComparison.OrdinalIgnoreCase)&&String.Equals(x.Sha256,Hash(target),StringComparison.OrdinalIgnoreCase))))
                    throw new IOException("目标位置存在不属于此安装器的文件，已停止以避免覆盖："+target);
            }
            progress("正在查找并验证本机 FFmpeg…");
            var ffmpeg=FindFfmpeg(root,ffmpegCandidates,searchDefaultFfmpegLocations);
            if(ffmpeg==null&&(confirmBundledFfmpeg==null||!confirmBundledFfmpeg()))return false;
            ValidateGame(root,true); // Recheck after the FFmpeg confirmation, before the first write.
            profileChanges=ProfilesForInstall(root,profile); // Recheck profiles after all user prompts.
            var retainedPaths=new HashSet<string>(next.Files.Select(x=>x.Path).Concat((next.GeneratedFiles??new List<OwnedFile>()).Select(x=>x.Path)),StringComparer.OrdinalIgnoreCase);
            var obsolete=old==null?new List<OwnedFile>():old.Files.Concat(old.LegacyFiles??new List<OwnedFile>()).Concat(old.GeneratedFiles??new List<OwnedFile>()).Where(x=>!retainedPaths.Contains(x.Path)).ToList();
            using(var tx=new Transaction()) {
                int index=0;
                foreach(var file in next.Files) {
                    progress("正在安装 "+(++index)+" / "+next.Files.Count);
                    using(var s=archive.GetEntry(file.Path).Open())using(var memory=new MemoryStream()){s.CopyTo(memory);tx.Write(Within(root,file.Path),memory.ToArray());}
                }
                progress("正在验证内置 FFmpeg 和 MP4 编码…");
                var bundledFfmpeg=Within(root,Mod+"runtime/ffmpeg/ffmpeg.exe");
                var bundledFfprobe=Within(root,Mod+"runtime/ffmpeg/ffprobe.exe");
                if(!ProbeFfmpeg(bundledFfmpeg)||!RunFfmpegCheck(bundledFfprobe,"-version","ffprobe version",10000))
                    throw new IOException("安装包内置 FFmpeg 无法运行或不支持 H.264/AAC MP4 编码。安装已终止并回滚，请检查安全软件是否拦截后重试。");
                if(ffmpeg==null)ffmpeg=bundledFfmpeg;
                var recorderConfig=Within(root,"profiles/"+profile+"/configs/azurearchive.recorder.cfg");
                var originalConfig=File.Exists(recorderConfig)?File.ReadAllText(recorderConfig):"";
                var updatedConfig=UpdateFfmpegConfig(originalConfig,ffmpeg);
                if(!String.Equals(originalConfig,updatedConfig,StringComparison.Ordinal))tx.Write(recorderConfig,Encoding.UTF8.GetBytes(updatedConfig));
                foreach(var change in profileChanges)tx.Write(change.Key,Encoding.UTF8.GetBytes(change.Value));
                if(!File.Exists(active))tx.Write(active,Encoding.UTF8.GetBytes(profile));
                foreach(var file in obsolete) {
                    var path=Within(root,file.Path);
                    if(!File.Exists(path))continue;
                    if(String.Equals(Hash(path),file.Sha256,StringComparison.OrdinalIgnoreCase))tx.Delete(path);
                    else progress("保留已被修改的旧版文件（不再由 profile 启用）："+file.Path);
                }
                // Never claim a modified or unreceipted legacy DLL just because its
                // current bytes happen to match a build-machine legacy hash.
                next.LegacyFiles=new List<OwnedFile>();
                tx.Write(receiptPath,Encoding.UTF8.GetBytes(Json.Serialize(next)));tx.Commit();
            }
        }
        return true;
    }
    public static List<string> Uninstall(string root,Action<string> progress)
    {
        root=FullRoot(root);ValidateGame(root,true);
        var receiptPath=Within(root,ReceiptName);
        var receipt=Json.Deserialize<Receipt>(File.ReadAllText(receiptPath));ValidateReceipt(root,receipt);
        var changes=new Dictionary<string,string>();
        var profiles=Within(root,"profiles");
        if(Directory.Exists(profiles))foreach(var dir in Directory.EnumerateDirectories(profiles)) {
            NoLinks(dir);var profile=Path.Combine(dir,"modconfig.json");NoLinks(profile);
            if(File.Exists(profile)){var changed=ChangeProfile(profile,false);if(changed!=null)changes[profile]=changed;}
            // User settings, Tutorial.SeenVersion and recorder-state/export-sequence
            // intentionally survive uninstall and future reinstall.
        }
        var retained=new List<string>();var deletes=new List<string>();
        foreach(var file in receipt.Files.Concat(receipt.LegacyFiles??new List<OwnedFile>()).Concat(receipt.GeneratedFiles??new List<OwnedFile>())) {
            var path=Within(root,file.Path);
            if(!File.Exists(path))continue;
            if(!String.Equals(Hash(path),file.Sha256,StringComparison.OrdinalIgnoreCase)){retained.Add(path);continue;} // Preserve changed/unrecognized files.
            deletes.Add(path);
        }
        // Revalidate every owned path and every profile before the first deletion.
        foreach(var path in deletes)NoLinks(path);
        using(var tx=new Transaction()) {
            foreach(var change in changes)tx.Write(change.Key,Encoding.UTF8.GetBytes(change.Value));
            foreach(var path in deletes){progress("正在清理内录 MOD…");tx.Delete(path);}
            tx.Delete(receiptPath);tx.Commit();
        }
        var directories=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach(var path in deletes) {
            for(var dir=Path.GetDirectoryName(path);dir.StartsWith(Within(root,Mod.TrimEnd('/'))+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase);dir=Path.GetDirectoryName(dir))directories.Add(dir);
        }
        directories.Add(Within(root,Mod.TrimEnd('/')));
        foreach(var dir in directories.OrderByDescending(x=>x.Length))if(Directory.Exists(dir)&&!Directory.EnumerateFileSystemEntries(dir).Any())Directory.Delete(dir,false);
        return retained;
    }
}

public static class Program
{
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)] static extern bool MoveFileEx(string a,string b,int flags);
    static string Self {get{return AppDomain.CurrentDomain.GetData("AARecorder.InstallerHost") as string ?? Assembly.GetExecutingAssembly().Location;}}
    static string Arg(string[] args,string key){var i=Array.IndexOf(args,key);return i>=0&&i+1<args.Length?args[i+1]:null;}
    public static int ExitCode;
    [STAThread] public static int Main(string[] args)
    {
        Application.EnableVisualStyles();Application.SetCompatibleTextRenderingDefault(false);
        try {
#if UNINSTALL
            if(args.Contains("--cleanup"))return Cleanup(args);
            var root=Directory.GetParent(Directory.GetParent(Path.GetDirectoryName(Self)).FullName).FullName;
            InstallCore.ValidateGame(root,true);
            if(!args.Contains("--quiet") && MessageBox.Show("卸载内录 MOD 的安装文件？\n设置、教程状态、导出序号、视频、剧情工程和其他 MOD 将保留。","卸载内录 MOD",MessageBoxButtons.OKCancel,MessageBoxIcon.Question)!=DialogResult.OK)return 0;
            var helper=Path.Combine(Path.GetTempPath(),"AARecorder-Uninstall-"+Guid.NewGuid().ToString("N")+".exe");File.Copy(Self,helper,false);
            var start=new ProcessStartInfo(helper,"--cleanup --root \""+root+"\" --parent "+Process.GetCurrentProcess().Id+(args.Contains("--quiet")?" --quiet":"")){UseShellExecute=false,CreateNoWindow=true};Process.Start(start);return 0;
#else
            var root=Arg(args,"--root")??Path.GetDirectoryName(Self);
            if(args.Contains("--quiet")){using(var stream=Assembly.GetExecutingAssembly().GetManifestResourceStream("installer.payload"))return InstallCore.Install(root,stream,delegate{},delegate(List<DependencyIssue> issues){return DependencyDialog.Confirm(null,issues);},delegate(List<string> versions){return ReinstallDialog.Confirm(null,versions);},null,true,delegate{return FfmpegInstallDialog.Confirm(null);},delegate(List<DependencyIssue> issues){return DlssInstallDialog.Confirm(null,issues);})?0:2;}
            Application.Run(new SetupWindow(root));return ExitCode;
#endif
        }catch(Exception ex){Fail(ex);return 1;}
    }
    static int Cleanup(string[] args)
    {
        var root=InstallCore.FullRoot(Arg(args,"--root"));
        var original=InstallCore.Within(root,InstallCore.Mod+"卸载内录MOD.exe");
        if(!Path.GetFullPath(Self).StartsWith(Path.GetFullPath(Path.GetTempPath()),StringComparison.OrdinalIgnoreCase)||!Path.GetFileName(Self).StartsWith("AARecorder-Uninstall-",StringComparison.Ordinal)||!File.Exists(original)||InstallCore.Hash(Self)!=InstallCore.Hash(original))throw new IOException("卸载器身份校验失败。");
        int pid;if(!Int32.TryParse(Arg(args,"--parent"),out pid))throw new IOException("卸载调用无效。");
        try{using(var parent=Process.GetProcessById(pid))if(!parent.WaitForExit(10000))throw new IOException("卸载程序尚未退出，请重试。");}catch(ArgumentException){}
        var kept=InstallCore.Uninstall(root,delegate{});
        if(!args.Contains("--quiet"))MessageBox.Show(kept.Count==0?"内录 MOD 已卸载。":"内录 MOD 已卸载。以下文件曾被修改，已保留：\n"+String.Join("\n",kept),"卸载完成",MessageBoxButtons.OK,MessageBoxIcon.Information);
        MoveFileEx(Self,null,4);return 0;
    }
    public static void Fail(Exception ex)
    {
        ExitCode=1;
        MessageBox.Show("操作失败，程序即将终止。\n\n"+ex.Message,"内录 MOD 安装错误",MessageBoxButtons.OK,MessageBoxIcon.Error);
        Environment.Exit(1);
    }
}
public sealed class SetupWindow : Form
{
    readonly TextBox path=new TextBox();readonly Button install=new Button();readonly Button browse=new Button();readonly Label status=new Label();bool working;
    public SetupWindow(string root)
    {
        Text="AzureArchive 内录 MOD "+InstallCore.Version;Font=new Font("Microsoft YaHei UI",10);ClientSize=new Size(620,275);FormBorderStyle=FormBorderStyle.FixedDialog;MaximizeBox=false;StartPosition=FormStartPosition.CenterScreen;
        Controls.Add(new Label{Text="安装内录 MOD",Font=new Font(Font.FontFamily,17,FontStyle.Bold),Location=new Point(25,20),Size=new Size(550,38)});
        Controls.Add(new Label{Text="选择包含 AzureArchive.exe 的软件根目录",Location=new Point(25,72),Size=new Size(560,26)});
        path.Text=root;path.SetBounds(25,105,475,30);Controls.Add(path);browse.Text="浏览…";browse.SetBounds(510,103,85,33);Controls.Add(browse);
        browse.Click+=delegate{using(var dialog=new FolderBrowserDialog()){dialog.Description="选择 AzureArchive 根目录";dialog.SelectedPath=path.Text;if(dialog.ShowDialog(this)==DialogResult.OK)path.Text=dialog.SelectedPath;}};
        status.Text="自动查找 FFmpeg；未找到时可选择安装内置版本，无需下载。";status.SetBounds(25,153,565,50);Controls.Add(status);
        install.Text="安装";install.SetBounds(450,218,145,36);Controls.Add(install);install.Click+=Install;
        FormClosing+=delegate(object sender,FormClosingEventArgs e){if(working)e.Cancel=true;};
    }
    async void Install(object sender,EventArgs e)
    {
        working=true;install.Enabled=browse.Enabled=path.Enabled=false;var root=path.Text;
        try {
            bool missing=false;
            var installed=await Task.Run(delegate{using(var stream=Assembly.GetExecutingAssembly().GetManifestResourceStream("installer.payload"))return InstallCore.Install(root,stream,delegate(string message){BeginInvoke((Action)delegate{status.Text=message;});},delegate(List<DependencyIssue> issues){missing=true;return (bool)Invoke((Func<bool>)delegate{return DependencyDialog.Confirm(this,issues);});},delegate(List<string> versions){return (bool)Invoke((Func<bool>)delegate{return ReinstallDialog.Confirm(this,versions);});},null,true,delegate{return (bool)Invoke((Func<bool>)delegate{return FfmpegInstallDialog.Confirm(this);});},delegate(List<DependencyIssue> issues){missing=true;return (bool)Invoke((Func<bool>)delegate{return DlssInstallDialog.Confirm(this,issues);});});});
            working=false;if(!installed){Program.ExitCode=2;Close();return;}
            MessageBox.Show(this,(missing?"MOD 已安装，FFmpeg 已自动配置。刚才提示的其他依赖会影响相应功能。":"安装完成，FFmpeg 已自动配置。启动 AA 后，在“入场”旁点击“内录”。")+"\n卸载程序位于 mods\\AzureArchiveRecorder。","安装完成",MessageBoxButtons.OK,MessageBoxIcon.Information);Close();
        }catch(Exception ex){working=false;Program.Fail(ex);Application.Exit();}
    }
}
public sealed class DlssInstallDialog : Form
{
    public DlssInstallDialog(List<DependencyIssue> issues)
    {
        Text="DLSS组件缺失";Font=new Font("Microsoft YaHei UI",10);ClientSize=new Size(710,420);FormBorderStyle=FormBorderStyle.FixedDialog;MaximizeBox=false;MinimizeBox=false;StartPosition=FormStartPosition.CenterScreen;
        Controls.Add(new Label{Text="DLSS组件缺失，请安装超分MOD包后或继续执行内录安装。",Location=new Point(22,20),Size=new Size(666,55)});
        var detail=new TextBox{Multiline=true,ReadOnly=true,ScrollBars=ScrollBars.Vertical,WordWrap=true,Text=String.Join("\r\n\r\n",issues.Select(i=>i.ToString())),TabStop=false,BackColor=SystemColors.Window};detail.SetBounds(22,80,666,228);Controls.Add(detail);
        Controls.Add(new Label{Text="点击“安装”继续安装内录 MOD；普通内录可使用。\n点击“取消安装”退出本次安装，不写入任何安装文件。",Location=new Point(22,321),Size=new Size(666,52)});
        var yes=new Button{Text="安装",DialogResult=DialogResult.Yes};yes.SetBounds(402,373,135,33);Controls.Add(yes);
        var no=new Button{Text="取消安装",DialogResult=DialogResult.No};no.SetBounds(550,373,138,33);Controls.Add(no);AcceptButton=no;CancelButton=no;ActiveControl=no;
    }
    public static bool Confirm(IWin32Window owner,List<DependencyIssue> issues){using(var dialog=new DlssInstallDialog(issues))return dialog.ShowDialog(owner)==DialogResult.Yes;}
}
public sealed class DependencyDialog : Form
{
    public DependencyDialog(List<DependencyIssue> issues)
    {
        Text="依赖检测：缺少组件";Font=new Font("Microsoft YaHei UI",10);ClientSize=new Size(710,515);FormBorderStyle=FormBorderStyle.FixedDialog;MaximizeBox=false;MinimizeBox=false;StartPosition=FormStartPosition.CenterScreen;
        Controls.Add(new Label{Text="检测到以下依赖缺失或未启用，是否继续安装？",Location=new Point(22,18),Size=new Size(670,30)});
        var text=new TextBox{Multiline=true,ReadOnly=true,ScrollBars=ScrollBars.Vertical,WordWrap=true,Text=String.Join("\r\n\r\n",issues.Select(i=>i.ToString())),TabStop=false,BackColor=SystemColors.Window};text.SetBounds(22,57,666,333);Controls.Add(text);
        Controls.Add(new Label{Text="普通 MP4 所需的 FFmpeg 已内置，将自动配置；以上缺项影响相应功能。\n取消安装不会写入任何文件。Python 环境已包含，无需另装。",Location=new Point(22,405),Size=new Size(666,52)});
        var yes=new Button{Text="继续安装",DialogResult=DialogResult.Yes};yes.SetBounds(402,469,135,33);Controls.Add(yes);
        var no=new Button{Text="取消安装",DialogResult=DialogResult.No};no.SetBounds(550,469,138,33);Controls.Add(no);AcceptButton=no;CancelButton=no;ActiveControl=no;
    }
    public static bool Confirm(IWin32Window owner,List<DependencyIssue> issues){using(var dialog=new DependencyDialog(issues))return dialog.ShowDialog(owner)==DialogResult.Yes;}
}
public sealed class FfmpegInstallDialog : Form
{
    public FfmpegInstallDialog()
    {
        Text="安装内置 FFmpeg";Font=new Font("Microsoft YaHei UI",10);ClientSize=new Size(640,325);FormBorderStyle=FormBorderStyle.FixedDialog;MaximizeBox=false;MinimizeBox=false;StartPosition=FormStartPosition.CenterScreen;
        Controls.Add(new Label{Text="未找到经过验证可用的 FFmpeg。\n\n安装 EXE 已包含离线版本，无需联网下载。\n将安装到 mods\\AzureArchiveRecorder\\runtime\\ffmpeg。\n约占用 283 MiB（296 MB）磁盘空间，仅供此 MOD 使用。\n不修改系统 PATH，可随内录 MOD 一起卸载。\n\n是否安装 FFmpeg 并继续安装 MOD？",Location=new Point(22,20),Size=new Size(596,232)});
        var yes=new Button{Text="安装 FFmpeg 并继续",DialogResult=DialogResult.Yes};yes.SetBounds(278,269,195,35);Controls.Add(yes);
        var no=new Button{Text="取消安装",DialogResult=DialogResult.No};no.SetBounds(490,269,128,35);Controls.Add(no);AcceptButton=no;CancelButton=no;ActiveControl=no;
    }
    public static bool Confirm(IWin32Window owner){using(var dialog=new FfmpegInstallDialog())return dialog.ShowDialog(owner)==DialogResult.Yes;}
}
public sealed class ReinstallDialog : Form
{
    public ReinstallDialog(List<string> versions)
    {
        Text="重复安装确认";Font=new Font("Microsoft YaHei UI",10);ClientSize=new Size(600,255);FormBorderStyle=FormBorderStyle.FixedDialog;MaximizeBox=false;MinimizeBox=false;StartPosition=FormStartPosition.CenterScreen;
        Controls.Add(new Label{Text="检测到已安装的内录 MOD："+String.Join("、",versions)+"\n\n是否覆盖重新安装 "+InstallCore.Version+"？\n\n将更新内录运行文件；配置、源码、剧情和导出视频保留。\n遇到未知或被修改的文件冲突时仍会报错停止。",Location=new Point(22,20),Size=new Size(555,174)});
        var yes=new Button{Text="覆盖重新安装",DialogResult=DialogResult.Yes};yes.SetBounds(280,207,150,33);Controls.Add(yes);
        var no=new Button{Text="取消安装",DialogResult=DialogResult.No};no.SetBounds(445,207,132,33);Controls.Add(no);AcceptButton=no;CancelButton=no;ActiveControl=no;
    }
    public static bool Confirm(IWin32Window owner,List<string> versions){using(var dialog=new ReinstallDialog(versions))return dialog.ShowDialog(owner)==DialogResult.Yes;}
}
