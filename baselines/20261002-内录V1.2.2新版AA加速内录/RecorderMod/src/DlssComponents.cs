using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;

namespace AzureArchive.Recorder
{
    // Shared by the .NET Framework installers and the .NET 6 recording plugin.
    // The supplemental product owns its files independently of the recorder.
    public static class DlssComponents
    {
        public const string MissingRuntimeMessage = "缺少DLSS组件，相关DLSS功能不可用。";
        public const string ToolRelative = "mods/AzureArchiveDLSS/runtime";
        public static readonly string[] RequiredFiles = {
            "ffmpeg.exe", "ffprobe.exe", "vsr_host.dll", "nvngx_vsr.dll",
            "dlssg_video_worker.exe", "nvngx_dlssg.dll", "dlssnr_host_v2.dll"
        };
        public static readonly Dictionary<int,string> Hashes = new Dictionary<int,string> {
            {30,"6eb209e764f39872625debd6abaf45e2bb6322f6f270f781f70c059ae30b3927"},
            {40,"ceb6432f6fbdf44d886014bcd47241932bf8b67439feef9bbdd0961436662650"},
            {50,"e16bcf15e16e13f527491cdf7845b2fe6521a738d8f7c9c721866a8496e1fc8e"}
        };
        static string Normalize(string root,string value)
        {
            if(String.IsNullOrWhiteSpace(value))return "";
            try {
                value=Environment.ExpandEnvironmentVariables(value.Trim().Trim('"'));
                return Path.GetFullPath(Path.IsPathRooted(value)?value:Path.Combine(root,value));
            } catch(ArgumentException) {return "";} catch(NotSupportedException) {return "";}
        }
        static bool NonEmptyFile(string path)
        {
            try{return File.Exists(path)&&new FileInfo(path).Length>0;}
            catch(IOException){return false;}catch(UnauthorizedAccessException){return false;}
        }
        public static string[] MissingCommon(string tool)
        {
            return RequiredFiles.Where(name=>String.IsNullOrEmpty(tool)||!NonEmptyFile(Path.Combine(tool,"_internal",name))).ToArray();
        }
        public static string ResolveTool(string gameRoot,string configured)
        {
            var supplement=Normalize(gameRoot,ToolRelative);
            var candidates=new[]{supplement,Normalize(gameRoot,configured),
                Normalize(gameRoot,"../DLSS5Tool-v2.3.3-win64"),Normalize(gameRoot,"DLSS5Tool-v2.3.3-win64")};
            // Installing the supplemental package before OR after the recorder
            // works without editing a saved profile or hard-coding a username.
            foreach(var path in candidates.Where(p=>p.Length>0).Distinct(StringComparer.OrdinalIgnoreCase))
                if(MissingCommon(path).Length==0)return path;
            return candidates.FirstOrDefault(p=>p.Length>0&&Directory.Exists(p))??supplement;
        }
        public static bool MatchesRuntime(string file,int series)
        {
            string expected;if(!Hashes.TryGetValue(series,out expected)||!File.Exists(file))return false;
            try {
                using(var input=File.OpenRead(file))using(var hash=SHA256.Create())
                    return BitConverter.ToString(hash.ComputeHash(input)).Replace("-","").Equals(expected,StringComparison.OrdinalIgnoreCase);
            } catch(IOException){return false;}catch(UnauthorizedAccessException){return false;}
        }
        public static bool HasRuntime(string tool,string profiles,int series)
        {
            var paths=new List<string>();
            if(!String.IsNullOrEmpty(profiles))paths.Add(Path.Combine(profiles,"rtx"+series,"nvngx_dlssnr.dll"));
            if(!String.IsNullOrEmpty(tool)) {
                paths.Add(Path.Combine(tool,"mods","dlss","rtx"+series,"nvngx_dlssnr.dll"));
                paths.Add(Path.Combine(tool,"mods","nvngx_dlssnr.dll"));
                paths.Add(Path.Combine(tool,"_internal","nvngx_dlssnr.dll"));
            }
            return paths.Any(path=>MatchesRuntime(path,series));
        }
        public sealed class RuntimeInspection
        {
            public bool Supported;
            public string Runtime = "";
            public string[] Missing = new string[0];
            public bool Ready { get { return Supported && Missing.Length==0 && Runtime.Length>0; } }
        }
        static IEnumerable<string> RuntimeCandidatesForInspection(string tool,string profiles,int series)
        {
            if(!String.IsNullOrEmpty(profiles))yield return Path.Combine(profiles,"rtx"+series,"nvngx_dlssnr.dll");
            if(!String.IsNullOrEmpty(tool)) {
                yield return Path.Combine(tool,"mods","dlss","rtx"+series,"nvngx_dlssnr.dll");
                yield return Path.Combine(tool,"mods","nvngx_dlssnr.dll");
                yield return Path.Combine(tool,"_internal","nvngx_dlssnr.dll");
            }
        }
        // Runtime admission is for the actual selected generation, not the
        // installer's all-generations completeness check. This never downloads.
        public static RuntimeInspection InspectForRuntime(string tool,string profiles,int series)
        {
            var result=new RuntimeInspection {Supported=Hashes.ContainsKey(series)};
            var missing=MissingCommon(tool).ToList();
            if(result.Supported) {
                result.Runtime=RuntimeCandidatesForInspection(tool,profiles,series).FirstOrDefault(path=>MatchesRuntime(path,series))??"";
                if(result.Runtime.Length==0)missing.Add("RTX "+series+" 系运行库（缺失或校验失败）");
            } else missing.Add("当前显卡系列暂不支持 DLSS 增强");
            result.Missing=missing.ToArray();return result;
        }
        // UI polling may reuse a successful hash result only while file metadata
        // is unchanged. Recording and enhancement preflights always rehash.
        public static string RuntimeFingerprint(string tool,string profiles,int series)
        {
            var paths=new List<string>();
            if(!String.IsNullOrEmpty(tool))paths.AddRange(RequiredFiles.Select(name=>Path.Combine(tool,"_internal",name)));
            paths.AddRange(RuntimeCandidatesForInspection(tool,profiles,series));
            return series+"|"+tool+"|"+profiles+"|"+String.Join("|",paths.Select(path=>{
                try {var file=new FileInfo(path);return path+":"+(file.Exists?file.Length+":"+file.LastWriteTimeUtc.Ticks+":"+file.CreationTimeUtc.Ticks:"missing");}
                catch(IOException){return path+":unreadable";}catch(UnauthorizedAccessException){return path+":unreadable";}
            }));
        }
        public static List<string> MissingForInstall(string gameRoot,string tool)
        {
            var missing=MissingCommon(tool).ToList();
            var supplement=Normalize(gameRoot,ToolRelative);
            if(String.Equals(Normalize(gameRoot,tool),supplement,StringComparison.OrdinalIgnoreCase)) {
                foreach(var series in new[]{30,40,50})
                    if(!MatchesRuntime(Path.Combine(tool,"mods","dlss","rtx"+series,"nvngx_dlssnr.dll"),series))
                        missing.Add("RTX "+series+" 系运行库（缺失或校验失败）");
            } else {
                // Legacy standalone tools may only have one generation installed.
                // The plugin still validates the exact detected GPU at export time.
                var installed=Normalize(gameRoot,"mods/AzureArchiveRecorder/runtime/gpu-runtimes");
                var development=Normalize(gameRoot,"RecorderMod/gpu-runtimes");
                if(!new[]{30,40,50}.Any(series=>HasRuntime(tool,installed,series)||HasRuntime(tool,development,series)))
                    missing.Add("受支持的 RTX 30 / 40 / 50 系运行库（缺失或校验失败）");
            }
            return missing;
        }
    }
}
