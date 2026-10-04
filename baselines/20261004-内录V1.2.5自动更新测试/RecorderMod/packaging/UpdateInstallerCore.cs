using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

public static partial class InstallCore
{
    public const string UpdateHelperName="更新内录MOD.exe";
    public const long MaxUpdateFileBytes=1536L*1024*1024, MaxUpdateExpandedBytes=8L*1024*1024*1024;
    public static string StrictUpdatePath(string root,string relative)
    {
        if(String.IsNullOrEmpty(relative)||relative.Contains("\\")||relative.Length>240)throw new IOException("更新清单路径无效。");
        foreach(var part in relative.Split('/'))if(part.Length==0||part.EndsWith(".")||part.EndsWith(" ")||part.IndexOfAny(Path.GetInvalidFileNameChars())>=0||Regex.IsMatch(part,@"^(CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])(\.|$)",RegexOptions.IgnoreCase))throw new IOException("更新清单包含不安全路径。");
        return Within(root,relative);
    }
    static bool UpdateOwned(string path,string version)
    {
        if(!AllowedOwnedForVersion(path,version))return false;
        if(path.StartsWith(Mod+version+"/",StringComparison.Ordinal))return path==Mod+version+"/AzureArchive.Recorder.dll"||path==Mod+version+"/manifest.json"||(new Version(version)>=new Version("1.2.4")&&path==Mod+version+"/"+UpdateHelperName);
        return !Regex.IsMatch(path,@"\.(cs|csproj|sln|pdb|ps1|bat|cmd|spec)$",RegexOptions.IgnoreCase);
    }
    public static Process ValidateUpdateParent(string root,int parentPid,long startUtcTicks)
    {
        root=FullRoot(root);ValidateGame(root,false);var exe=Within(root,"AzureArchive.exe");Process parent=null;
        try {
            parent=Process.GetProcessById(parentPid);
            var retainedHandle=parent.Handle; // Cache the OS handle before identity checks; prevent PID reuse during the handshake.
            if(parent.HasExited||parent.StartTime.ToUniversalTime().Ticks!=startUtcTicks||!String.Equals(Path.GetFullPath(parent.MainModule.FileName),exe,StringComparison.OrdinalIgnoreCase))throw new IOException("更新请求的 AA 进程身份不匹配。");
            foreach(var candidate in Process.GetProcessesByName("AzureArchive"))using(candidate) {
                string path;try{path=Path.GetFullPath(candidate.MainModule.FileName);}catch{throw new IOException("无法确认其他 AA 进程身份，已停止更新。");}
                if(String.Equals(path,exe,StringComparison.OrdinalIgnoreCase)&&candidate.Id!=parentPid)throw new IOException("同一目录存在多个 AA 进程，请只保留当前窗口。");
            }
            return parent;
        }catch{if(parent!=null)parent.Dispose();throw;}
    }
    public static Receipt ValidateUpdateArchive(string root,ZipArchive archive)
    {
        var manifest=archive.GetEntry("payload-manifest.json");
        if(manifest==null||manifest.Length>8*1024*1024)throw new IOException("更新清单缺失或过大。");
        Receipt next;using(var reader=new StreamReader(manifest.Open(),Encoding.UTF8))next=Json.Deserialize<Receipt>(reader.ReadToEnd());
        if(next==null||!String.IsNullOrEmpty(next.Root))throw new IOException("更新清单根路径必须为空。");
        next.Root=FullRoot(root);ValidateReceipt(next.Root,next);
        if(next.Files.Count>20000||archive.Entries.Count!=next.Files.Count+1)throw new IOException("更新包文件数量不符。");
        var names=new HashSet<string>(StringComparer.OrdinalIgnoreCase);long expanded=0;
        foreach(var entry in archive.Entries) {
            StrictUpdatePath(root,entry.FullName);
            var unixType=(entry.ExternalAttributes>>16)&0xF000;
            if(!names.Add(entry.FullName)||entry.FullName.EndsWith("/")||(entry.ExternalAttributes&0x400)!=0||(entry.ExternalAttributes&0x10)!=0||(unixType!=0&&unixType!=0x8000)||entry.Length<0||entry.Length>MaxUpdateFileBytes)throw new IOException("更新包包含重复、链接、目录或超大文件。");
            expanded=checked(expanded+entry.Length);if(expanded>MaxUpdateExpandedBytes)throw new IOException("更新包解压大小超限。");
        }
        foreach(var file in next.Files) {
            if(!UpdateOwned(file.Path,next.Version))throw new IOException("更新包声明了非运行文件："+file.Path);
            StrictUpdatePath(root,file.Path);var entry=archive.GetEntry(file.Path);if(entry==null)throw new IOException("更新包缺少文件："+file.Path);
            using(var stream=entry.Open())using(var hash=SHA256.Create())if(!String.Equals(BitConverter.ToString(hash.ComputeHash(stream)).Replace("-",""),file.Sha256,StringComparison.OrdinalIgnoreCase))throw new IOException("更新文件校验失败："+file.Path);
        }
        foreach(var name in new[]{"AzureArchive.Recorder.dll","manifest.json",UpdateHelperName})if(!next.Files.Any(f=>f.Path==Mod+Version+"/"+name)||archive.GetEntry(Mod+Version+"/"+name).Length==0)throw new IOException("更新包缺少必要组件："+name);
        var modManifest=archive.GetEntry(Mod+Version+"/manifest.json");
        if(modManifest.Length>1024*1024)throw new IOException("MOD 清单过大。");
        Dictionary<string,object> data;using(var reader=new StreamReader(modManifest.Open(),Encoding.UTF8))data=Json.Deserialize<Dictionary<string,object>>(reader.ReadToEnd());
        object nameValue,versionValue;
        if(data==null||!data.TryGetValue("name",out nameValue)||!data.TryGetValue("version_number",out versionValue)||!String.Equals(nameValue as string,Product,StringComparison.Ordinal)||!String.Equals(versionValue as string,Version,StringComparison.Ordinal))throw new IOException("MOD 版本身份不匹配。");
        return next;
    }
    // Only this updater entry point permits an identified, idle AA process to stay open.
    // It never probes dependencies, changes recorder.cfg, deletes old DLLs or runs tools.
    public static void ApplyUpdate(string root,Stream payload,int parentPid,long parentStartUtcTicks,Action<string> progress)
    {
        root=FullRoot(root);using(var parent=ValidateUpdateParent(root,parentPid,parentStartUtcTicks)) {
            var receiptPath=StrictUpdatePath(root,ReceiptName);
            if(!File.Exists(receiptPath)||new FileInfo(receiptPath).Length>8*1024*1024)throw new IOException("已安装版本缺少有效归属清单，请使用完整安装器。");
            var receiptBytes=File.ReadAllBytes(receiptPath);var old=Json.Deserialize<Receipt>(Encoding.UTF8.GetString(receiptBytes).TrimStart('\uFEFF'));ValidateReceipt(root,old,true);
            if(new Version(old.Version)>=new Version(Version))throw new IOException("更新仅支持从较早版本升级。");
            var oldFiles=old.Files.Concat(old.LegacyFiles??new List<OwnedFile>()).Concat(old.GeneratedFiles??new List<OwnedFile>()).ToDictionary(f=>f.Path,StringComparer.OrdinalIgnoreCase);
            foreach(var file in oldFiles.Values) {
                StrictUpdatePath(root,file.Path);
                if(old.Files.Contains(file)&&!UpdateOwned(file.Path,old.Version))throw new IOException("原安装清单包含无法识别的文件："+file.Path);
                var path=Within(root,file.Path);
                if(File.Exists(path)&&!String.Equals(Hash(path),file.Sha256,StringComparison.OrdinalIgnoreCase))throw new IOException("已安装文件已被修改，已停止以保留原文件："+file.Path);
            }
            using(var archive=new ZipArchive(payload,ZipArchiveMode.Read,true)) {
                var next=ValidateUpdateArchive(root,archive);var shipped=next.Files.ToArray();var changed=new List<OwnedFile>();
                foreach(var file in shipped) {
                    var target=StrictUpdatePath(root,file.Path);OwnedFile owned;
                    if(Directory.Exists(target))throw new IOException("更新目标被目录占用："+file.Path);
                    if(File.Exists(target)) {
                        if(!oldFiles.TryGetValue(file.Path,out owned))throw new IOException("更新目标存在无归属文件："+file.Path);
                        var actual=Hash(target);if(!String.Equals(actual,owned.Sha256,StringComparison.OrdinalIgnoreCase))throw new IOException("更新目标内容已改变："+file.Path);
                        if(String.Equals(actual,file.Sha256,StringComparison.OrdinalIgnoreCase))continue;
                    }
                    changed.Add(file);
                }
                // Package-machine legacy/generated hashes cannot establish ownership on this PC.
                next.LegacyFiles=new List<OwnedFile>();next.GeneratedFiles=(old.GeneratedFiles??new List<OwnedFile>()).Select(f=>new OwnedFile{Path=f.Path,Sha256=f.Sha256}).ToList();
                var newPaths=new HashSet<string>(next.Files.Select(f=>f.Path).Concat(next.GeneratedFiles.Select(f=>f.Path)),StringComparer.OrdinalIgnoreCase);
                foreach(var file in old.Files.Concat(old.LegacyFiles??new List<OwnedFile>()))if(!newPaths.Contains(file.Path)) {
                    if(AllowedLegacy(file.Path,next.Version))next.LegacyFiles.Add(new OwnedFile{Path=file.Path,Sha256=file.Sha256});
                    else if(UpdateOwned(file.Path,next.Version))next.Files.Add(new OwnedFile{Path=file.Path,Sha256=file.Sha256});
                    else throw new IOException("无法安全保留旧文件归属："+file.Path);
                }
                ValidateReceipt(root,next);
                var profileName=ActiveProfile(root);var profileChanges=ProfilesForInstall(root,profileName);
                var profileBefore=profileChanges.Keys.ToDictionary(p=>p,p=>File.Exists(p)?File.ReadAllBytes(p):null,StringComparer.OrdinalIgnoreCase);
                using(var recheck=ValidateUpdateParent(root,parentPid,parentStartUtcTicks)){}
                using(var tx=new Transaction()) {
                    int index=0;
                    foreach(var file in changed) {
                        progress("正在更新 "+(++index)+" / "+changed.Count);var target=StrictUpdatePath(root,file.Path);OwnedFile owned;
                        if(File.Exists(target)&&(!oldFiles.TryGetValue(file.Path,out owned)||!String.Equals(Hash(target),owned.Sha256,StringComparison.OrdinalIgnoreCase)))throw new IOException("更新时目标文件发生变化："+file.Path);
                        using(var data=archive.GetEntry(file.Path).Open())using(var memory=new MemoryStream()){data.CopyTo(memory);tx.Write(target,memory.ToArray());}
                    }
                    using(var recheck=ValidateUpdateParent(root,parentPid,parentStartUtcTicks)){}
                    if(ActiveProfile(root)!=profileName||!File.ReadAllBytes(receiptPath).SequenceEqual(receiptBytes))throw new IOException("更新期间安装清单或当前配置发生变化。");
                    foreach(var change in profileChanges) {
                        NoLinks(change.Key);var before=profileBefore[change.Key];
                        if((before==null&&File.Exists(change.Key))||(before!=null&&(!File.Exists(change.Key)||!File.ReadAllBytes(change.Key).SequenceEqual(before))))throw new IOException("更新期间 MOD 配置发生变化。");
                        var bytes=Encoding.UTF8.GetBytes(change.Value);if(before==null||!before.SequenceEqual(bytes))tx.Write(change.Key,bytes);
                    }
                    tx.Write(Within(root,ReceiptName),Encoding.UTF8.GetBytes(Json.Serialize(next)));tx.Commit();
                }
            }
        }
    }
}