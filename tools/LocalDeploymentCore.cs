using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;


namespace AzureArchive.RevisionCompare.LocalDeployment
{
    public sealed class OwnedFile { public string Path; public string Sha256; }
    public sealed class OwnedProfile { public string Profile; public string Version; }
    public sealed class InstallReceipt
    {
        public int Schema = 1;
        public string ProductId, ProductName, Version, Root;
        public List<OwnedFile> Files = new List<OwnedFile>();
        public List<OwnedProfile> Profiles = new List<OwnedProfile>();
    }
    public sealed class OperationResult
    {
        public string Message;
        public List<string> Preserved = new List<string>();
        public override string ToString() { return Message + (Preserved.Count == 0 ? "" : "\r\n\r\n已保留：\r\n• " + String.Join("\r\n• ", Preserved.ToArray())); }
    }
    internal sealed class FileChange { public string Path; public byte[] Before, After; }

    public static class DeploymentCore
    {
        public const string ProductId = "azurearchive.revisioncompare";
        public const string ProductName = "AzureArchiveRevisionCompare";
        public const string Version = "1.1.0";
        public const string PreviousVersion = "1.0.0";
        public const string LegacyVersion = "0.1.0";
        public const string ModDirectory = "mods/AzureArchiveRevisionCompare";
        public const string ReceiptRelative = ModDirectory + "/install-receipt.json";
        public const string DllRelative = ModDirectory + "/" + Version + "/AzureArchive.RevisionCompare.dll";
        public const string ManifestRelative = ModDirectory + "/" + Version + "/manifest.json";
        static readonly UTF8Encoding Utf8 = new UTF8Encoding(false, true);
        static readonly JavaScriptSerializer Json = new JavaScriptSerializer { MaxJsonLength = 16 * 1024 * 1024 };

        public static string NormalizeRoot(string path)
        {
            string full = Path.GetFullPath(path);
            return full.Length == Path.GetPathRoot(full).Length ? full : full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }
        public static string Hash(byte[] bytes) { using (var sha = SHA256.Create()) return BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant(); }
        static byte[] Serialize(object value) { return Utf8.GetBytes(Json.Serialize(value)); }
        static byte[] SerializeReceipt(InstallReceipt receipt)
        {
            // Reflection field order can change after PowerShell inspects a DTO. Keep receipt bytes stable.
            var files = new List<Dictionary<string, object>>();
            foreach (OwnedFile file in receipt.Files) files.Add(new Dictionary<string, object> { { "Path", file.Path }, { "Sha256", file.Sha256 } });
            var profiles = new List<Dictionary<string, object>>();
            foreach (OwnedProfile profile in receipt.Profiles) profiles.Add(new Dictionary<string, object> { { "Profile", profile.Profile }, { "Version", profile.Version } });
            return Serialize(new Dictionary<string, object> {
                { "Schema", receipt.Schema }, { "ProductId", receipt.ProductId }, { "ProductName", receipt.ProductName },
                { "Version", receipt.Version }, { "Root", receipt.Root }, { "Files", files }, { "Profiles", profiles }
            });
        }
        static bool Same(byte[] a, byte[] b) { return a == null ? b == null : b != null && Hash(a) == Hash(b); }
        public static void RejectLinks(string path)
        {
            for (string current = Path.GetFullPath(path); !String.IsNullOrEmpty(current); current = Path.GetDirectoryName(current))
            {
                FileAttributes attributes;
                try { attributes = File.GetAttributes(current); }
                catch (FileNotFoundException) { continue; }
                catch (DirectoryNotFoundException) { continue; }
                // Inspect attributes directly: Exists() can return false for a dangling link.
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                    throw new IOException("为避免误写，不能使用目录联接或符号链接：" + current);
            }
        }
        public static string Within(string root, string relative)
        {
            if (String.IsNullOrWhiteSpace(relative) || Path.IsPathRooted(relative) || relative.IndexOf(':') >= 0)
                throw new IOException("无效的安装路径。");
            foreach (string part in relative.Split('/', '\\'))
                if (part.Length == 0 || part == "." || part == "..") throw new IOException("安装路径超出允许范围。");
            string path = Path.GetFullPath(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));
            if (!path.StartsWith(NormalizeRoot(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new IOException("安装路径超出 AA 目录。");
            RejectLinks(path);
            return path;
        }
        static bool SupportedVersion(string version) { return version == Version || version == PreviousVersion || version == LegacyVersion; }
        static bool AllowedPayload(string relative, string version)
        {
            return SupportedVersion(version) && (relative == ModDirectory + "/" + version + "/AzureArchive.RevisionCompare.dll" || relative == ModDirectory + "/" + version + "/manifest.json");
        }
        static bool IsHash(string value)
        {
            if (value == null || value.Length != 64) return false;
            foreach (char c in value) if (!((c >= '0' && c <= '9') || (c >= 'a' && c <= 'f'))) return false;
            return true;
        }
        static void ValidateProfileName(string profile)
        {
            if (String.IsNullOrWhiteSpace(profile) || profile == "." || profile == ".." || profile.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || profile.TrimEnd(' ', '.') != profile)
                throw new IOException("ActiveProfile.txt 中的配置名称无效。");
        }
        static string ProfileRelative(string profile) { ValidateProfileName(profile); return "profiles/" + profile + "/modconfig.json"; }
        public static void ValidateGame(string root)
        {
            RejectLinks(root);
            if (!File.Exists(Within(root, "AzureArchive.exe")) || !Directory.Exists(Within(root, "AzureArchive_Data")))
                throw new IOException("请选择含 AzureArchive.exe 和 AzureArchive_Data 的 AA 根目录。");
            foreach (Process process in Process.GetProcessesByName("AzureArchive"))
                using (process) { throw new IOException("请先关闭所有 AzureArchive 窗口，再安装或卸载。"); }
        }
        static InstallReceipt NewReceipt(string root)
        {
            return new InstallReceipt { ProductId = ProductId, ProductName = ProductName, Version = Version, Root = root };
        }
        public static InstallReceipt ReadReceipt(string root)
        {
            root = NormalizeRoot(root);
            string path = Within(root, ReceiptRelative);
            if (!File.Exists(path)) return null;
            InstallReceipt receipt;
            try { receipt = Json.Deserialize<InstallReceipt>(File.ReadAllText(path, Utf8)); }
            catch (Exception ex) { throw new IOException("安装收据损坏，已停止，未删除文件。", ex); }
            if (receipt == null || receipt.Schema != 1 || receipt.ProductId != ProductId || receipt.ProductName != ProductName || !SupportedVersion(receipt.Version) || !String.Equals(receipt.Root, root, StringComparison.OrdinalIgnoreCase) || receipt.Files == null || receipt.Profiles == null)
                throw new IOException("安装收据不属于本 MOD 或此 AA 目录，已停止。");
            var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (OwnedFile file in receipt.Files)
                if (file == null || !AllowedPayload(file.Path, receipt.Version) || !IsHash(file.Sha256) || !paths.Add(file.Path)) throw new IOException("安装收据包含不允许的文件记录。");
            var profiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (OwnedProfile profile in receipt.Profiles)
            {
                if (profile == null || profile.Version != receipt.Version || !profiles.Add(profile.Profile)) throw new IOException("安装收据包含不允许的配置记录。");
                ValidateProfileName(profile.Profile);
            }
            return receipt;
        }
        static Dictionary<string, object> ReadProfile(string path, out IList enabled)
        { return ReadProfileBytes(path, Current(path), out enabled); }
        static Dictionary<string, object> ReadProfileBytes(string path, byte[] bytes, out IList enabled)
        {
            Dictionary<string, object> config;
            try { config = bytes != null ? Json.DeserializeObject(Utf8.GetString(bytes).TrimStart('\uFEFF')) as Dictionary<string, object> : new Dictionary<string, object>(); }
            catch (Exception ex) { throw new IOException("当前配置 JSON 无法读取，已停止，未覆盖配置：" + path, ex); }
            if (config == null) throw new IOException("modconfig.json 必须为 JSON 对象。");
            object existing;
            if (!config.TryGetValue("EnabledMods", out existing)) enabled = new ArrayList();
            else
            {
                IList source = existing as IList;
                if (source == null) throw new IOException("EnabledMods 必须为 JSON 数组。");
                enabled = new ArrayList(source);
            }
            config["EnabledMods"] = enabled;
            return config;
        }
        static bool IsOurItem(object item)
        {
            var map = item as Dictionary<string, object>; object name;
            return map != null && map.TryGetValue("name", out name) && name is string && String.Equals((string)name, ProductName, StringComparison.OrdinalIgnoreCase);
        }
        static bool ExactOwnedItem(object item, string version)
        {
            var map = item as Dictionary<string, object>; object name, itemVersion;
            return map != null && map.Count == 2 && map.TryGetValue("name", out name) && (name as string) == ProductName && map.TryGetValue("version", out itemVersion) && (itemVersion as string) == version;
        }
        static byte[] Current(string path) { return File.Exists(path) ? File.ReadAllBytes(path) : null; }
        static void AddChange(List<FileChange> changes, string path, byte[] after)
        { AddExpectedChange(changes, path, Current(path), after); }
        static void AddExpectedChange(List<FileChange> changes, string path, byte[] before, byte[] after)
        {
            if (!Same(before, after)) changes.Add(new FileChange { Path = path, Before = before, After = after });
        }
        static void AtomicWrite(string path, byte[] bytes)
        {
            RejectLinks(path);
            if (bytes == null) { if (File.Exists(path)) File.Delete(path); return; }
            string temporary = path + ".revisioncompare-" + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { stream.Write(bytes, 0, bytes.Length); stream.Flush(true); }
                if (File.Exists(path)) File.Replace(temporary, path, null, true); else File.Move(temporary, path);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
        static void EnsureDirectory(string path, List<string> created)
        {
            RejectLinks(path);
            if (Directory.Exists(path)) return;
            string parent = Path.GetDirectoryName(path);
            if (!Directory.Exists(parent)) EnsureDirectory(parent, created);
            Directory.CreateDirectory(path); created.Add(path);
        }
        static void Commit(List<FileChange> changes)
        {
            var done = new List<FileChange>(); var created = new List<string>();
            try
            {
                foreach (FileChange change in changes)
                {
                    RejectLinks(change.Path);
                    if (!Same(Current(change.Path), change.Before)) throw new IOException("操作期间文件发生变化，请重试：" + change.Path);
                    if (change.After != null) EnsureDirectory(Path.GetDirectoryName(change.Path), created);
                    AtomicWrite(change.Path, change.After); done.Add(change);
                }
            }
            catch (Exception original)
            {
                var failures = new List<string>();
                for (int i = done.Count - 1; i >= 0; --i)
                {
                    FileChange change = done[i];
                    try
                    {
                        if (!Same(Current(change.Path), change.After)) throw new IOException("文件已被其他程序修改。");
                        AtomicWrite(change.Path, change.Before);
                    }
                    catch { failures.Add(change.Path); }
                }
                for (int i = created.Count - 1; i >= 0; --i)
                    try { if (Directory.Exists(created[i]) && Directory.GetFileSystemEntries(created[i]).Length == 0) Directory.Delete(created[i]); } catch { }
                if (failures.Count > 0) throw new IOException("操作未完成，以下文件无法自动恢复，请保留安装器并检查：" + String.Join("、", failures.ToArray()), original);
                throw new IOException("操作未完成，已恢复本次文件更改：" + original.Message, original);
            }
        }
        public static OperationResult Install(string selectedRoot, byte[] plugin, byte[] manifest)
        { return WithUpdateLock(selectedRoot, delegate { return InstallUnlocked(selectedRoot, plugin, manifest); }); }
        static OperationResult WithUpdateLock(string selectedRoot, Func<OperationResult> action)
        {
            // Share the helper/installer lock so an authorized automatic update cannot race a local operation.
            string mutexName = "Local\\AARevisionCompareUpdate-" + Hash(Utf8.GetBytes(NormalizeRoot(selectedRoot).ToUpperInvariant()));
            using (var mutex = new Mutex(false, mutexName))
            {
                bool owns = false;
                try
                {
                    try { owns = mutex.WaitOne(0); } catch (AbandonedMutexException) { owns = true; }
                    if (!owns) throw new IOException("此 AA 已有剧情 MOD 更新或安装任务，请完成后重试。");
                    return action();
                }
                finally { if (owns) mutex.ReleaseMutex(); }
            }
        }
        static OperationResult InstallUnlocked(string selectedRoot, byte[] plugin, byte[] manifest)
        {
            string root = NormalizeRoot(selectedRoot); ValidateGame(root);
            if (plugin == null || plugin.Length < 2 || plugin[0] != 'M' || plugin[1] != 'Z') throw new IOException("内嵌插件无效。");
            Dictionary<string, object> metadata;
            try { metadata = Json.DeserializeObject(Utf8.GetString(manifest)) as Dictionary<string, object>; }
            catch (Exception ex) { throw new IOException("内嵌 manifest.json 无效。", ex); }
            object name, version;
            if (metadata == null || !metadata.TryGetValue("name", out name) || (name as string) != ProductName || !metadata.TryGetValue("version_number", out version) || (version as string) != Version)
                throw new IOException("内嵌 manifest.json 的名称或版本不匹配。");
            string activePath = Within(root, "ActiveProfile.txt");
            if (!File.Exists(activePath)) throw new IOException("未找到 ActiveProfile.txt，请先启动 AA 创建配置，关闭 AA 后重试。");
            string active = File.ReadAllText(activePath, Utf8).Trim(); ValidateProfileName(active);
            string profilePath = Within(root, ProfileRelative(active));
            if (!Directory.Exists(Path.GetDirectoryName(profilePath))) throw new IOException("当前 profile 目录不存在，请先在 AA 中建立有效配置。");
            string receiptPath = Within(root, ReceiptRelative); byte[] receiptBefore = Current(receiptPath);
            InstallReceipt receipt = ReadReceipt(root) ?? NewReceipt(root);
            var changes = new List<FileChange>();
            string previousVersion = receipt.Version;
            bool migrating = previousVersion != Version;
            var previousChanges = new List<FileChange>();
            if (migrating)
            {
                // This local migration owns only the active profile; never silently migrate other profiles.
                if (receipt.Files.Count != 2 || receipt.Profiles.Count != 1 || !String.Equals(receipt.Profiles[0].Profile, active, StringComparison.OrdinalIgnoreCase))
                    throw new IOException("旧收据不属于当前配置的完整安装，无法自动迁移。");
                foreach (OwnedFile old in receipt.Files)
                {
                    byte[] bytes = Current(Within(root, old.Path));
                    if (bytes == null || Hash(bytes) != old.Sha256) throw new IOException("旧安装文件缺失或已修改，迁移已停止并保留原内容：" + old.Path);
                    previousChanges.Add(new FileChange { Path = Within(root, old.Path), Before = bytes, After = null });
                }
                receipt.Files.Clear();
            }
            var payload = new Dictionary<string, byte[]> { { DllRelative, plugin }, { ManifestRelative, manifest } };
            foreach (var entry in payload)
            {
                string path = Within(root, entry.Key); byte[] before = Current(path);
                OwnedFile owned = receipt.Files.Find(delegate(OwnedFile f) { return f.Path == entry.Key; });
                if (before != null && (owned == null || Hash(before) != owned.Sha256))
                    throw new IOException("该文件不是本安装器拥有的原始文件，已保留并停止：" + path);
                AddExpectedChange(changes, path, before, entry.Value);
                if (owned == null) receipt.Files.Add(new OwnedFile { Path = entry.Key, Sha256 = Hash(entry.Value) }); else owned.Sha256 = Hash(entry.Value);
            }
            byte[] profileBefore = Current(profilePath);
            IList enabled; var config = ReadProfileBytes(profilePath, profileBefore, out enabled);
            bool found = false; int ourCount = 0;
            foreach (object item in enabled) if (IsOurItem(item)) ++ourCount;
            if (ourCount > 1) throw new IOException("当前配置有重复的本 MOD 条目，已停止并保留配置。");
            if (migrating && ourCount != 1) throw new IOException("旧收据对应的本 MOD 配置项缺失，迁移已停止。");
            for (int i = 0; i < enabled.Count; ++i)
            {
                object item = enabled[i];
                if (IsOurItem(item))
                {
                    found = true;
                    if (!ExactOwnedItem(item, migrating ? previousVersion : Version)) throw new IOException("当前 profile 已有本 MOD 的不同或修改过的条目，请先在 AA 中检查该条目。");
                    if (migrating)
                    {
                        enabled[i] = new Dictionary<string, object> { { "name", ProductName }, { "version", Version } };
                        receipt.Profiles[0].Version = Version;
                        AddExpectedChange(changes, profilePath, profileBefore, Serialize(config));
                    }
                }
            }
            if (!found)
            {
                enabled.Add(new Dictionary<string, object> { { "name", ProductName }, { "version", Version } });
                AddExpectedChange(changes, profilePath, profileBefore, Serialize(config));
                if (!receipt.Profiles.Exists(delegate(OwnedProfile p) { return String.Equals(p.Profile, active, StringComparison.OrdinalIgnoreCase); })) receipt.Profiles.Add(new OwnedProfile { Profile = active, Version = Version });
            }
            changes.AddRange(previousChanges);
            receipt.Version = Version;
            AddExpectedChange(changes, receiptPath, receiptBefore, SerializeReceipt(receipt));
            ValidateGame(root);
            Commit(changes);
            return new OperationResult { Message = (migrating ? "本地升级完成。" : "本地部署完成。") + "已在当前配置「" + active + "」启用剧情改稿同步 MOD " + Version + "。" };
        }
        public static OperationResult Uninstall(string selectedRoot)
        { return WithUpdateLock(selectedRoot, delegate { return UninstallUnlocked(selectedRoot); }); }
        static OperationResult UninstallUnlocked(string selectedRoot)
        {
            string root = NormalizeRoot(selectedRoot); ValidateGame(root);
            string receiptPath = Within(root, ReceiptRelative); byte[] receiptBefore = Current(receiptPath);
            InstallReceipt receipt = ReadReceipt(root);
            if (receipt == null) return new OperationResult { Message = "未找到本安装器的收据，未删除任何文件或配置。" };
            var result = new OperationResult { Message = "卸载完成，其他 MOD 和用户配置已保留。" };
            var changes = new List<FileChange>(); var remainingFiles = new List<OwnedFile>(); var remainingProfiles = new List<OwnedProfile>();
            foreach (OwnedFile owned in receipt.Files)
            {
                string path = Within(root, owned.Path); byte[] bytes = Current(path);
                if (bytes == null) continue;
                if (Hash(bytes) != owned.Sha256) { result.Preserved.Add(owned.Path + "（安装后被修改）"); remainingFiles.Add(owned); }
                else AddExpectedChange(changes, path, bytes, null);
            }
            foreach (OwnedProfile profile in receipt.Profiles)
            {
                string path = Within(root, ProfileRelative(profile.Profile)); if (!File.Exists(path)) continue;
                try
                {
                    byte[] profileBefore = Current(path);
                    IList enabled; var config = ReadProfileBytes(path, profileBefore, out enabled); int count = 0, index = -1;
                    for (int i = 0; i < enabled.Count; ++i) if (IsOurItem(enabled[i])) { ++count; index = i; }
                    if (count == 0) continue;
                    if (count != 1 || !ExactOwnedItem(enabled[index], profile.Version)) { remainingProfiles.Add(profile); result.Preserved.Add("配置「" + profile.Profile + "」中的 MOD 条目（已被修改）"); continue; }
                    enabled.RemoveAt(index); AddExpectedChange(changes, path, profileBefore, Serialize(config));
                }
                catch (IOException) { remainingProfiles.Add(profile); result.Preserved.Add("配置「" + profile.Profile + "」（无法安全读取）"); }
            }
            receipt.Files = remainingFiles; receipt.Profiles = remainingProfiles;
            AddExpectedChange(changes, receiptPath, receiptBefore, remainingFiles.Count + remainingProfiles.Count == 0 ? null : SerializeReceipt(receipt));
            Commit(changes);
            if (result.Preserved.Count > 0) result.Message = "已移除可安全确认的安装内容；修改过的内容和对应收据已保留。";
            foreach (string directory in new[] { ModDirectory + "/" + receipt.Version, ModDirectory })
            {
                string path = Within(root, directory);
                if (Directory.Exists(path) && Directory.GetFileSystemEntries(path).Length == 0) Directory.Delete(path);
            }
            return result;
        }
    }

}
