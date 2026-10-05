using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Web.Script.Serialization;
using System.Text.RegularExpressions;
using System.Threading;


namespace AzureArchive.RevisionCompare.Installation
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
        public string BackupDirectory;
        public List<string> Preserved = new List<string>();
        public override string ToString() { return Message + (Preserved.Count == 0 ? "" : "\r\n\r\n已保留：\r\n• " + String.Join("\r\n• ", Preserved.ToArray())) + (String.IsNullOrEmpty(BackupDirectory) ? "" : "\r\n\r\n原文件备份：" + BackupDirectory); }
    }
    public sealed class InstallerRollbackException : IOException { public InstallerRollbackException(string message, Exception inner) : base(message, inner) {} }
    public sealed class BackupFile { public string Path, Sha256; public bool Existed; }
    public sealed class BackupRecord { public string ProductId, Root, Operation, CreatedUtc; public List<BackupFile> Files = new List<BackupFile>(); }
    internal sealed class FileChange { public string Path; public byte[] Before, After; }

    public static class InstallerCore
    {
        public const string ProductId = "azurearchive.revisioncompare";
        public const string ProductName = "AzureArchiveRevisionCompare";
        public const string Version = "1.1.0";
        public const string PreviousVersion = "0.1.0";
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
                if (part.Length == 0 || part == "." || part == ".." || part.TrimEnd(' ', '.') != part || part.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || Regex.IsMatch(part, @"^(CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])(\.|$)", RegexOptions.IgnoreCase)) throw new IOException("安装路径超出允许范围。");
            string path = Path.GetFullPath(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));
            if (!path.StartsWith(NormalizeRoot(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new IOException("安装路径超出 AA 目录。");
            RejectLinks(path);
            return path;
        }
        static bool SupportedVersion(string version) { return ReceiptVersion.IsValid(version); }
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
            if (String.IsNullOrWhiteSpace(profile) || profile == "." || profile == ".." || profile.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || profile.TrimEnd(' ', '.') != profile || Regex.IsMatch(profile, @"^(CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])(\.|$)", RegexOptions.IgnoreCase))
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
        static InstallReceipt ReadReceipt(string root)
        {
            string path = Within(root, ReceiptRelative);
            if (!File.Exists(path)) return null;
            InstallReceipt receipt;
            try
            {
                if (new FileInfo(path).Length > 1024 * 1024) throw new IOException("安装收据过大。");
                string content = File.ReadAllText(path, Utf8);
                var shape = Json.DeserializeObject(content) as Dictionary<string, object>;
                if (shape == null) throw new IOException("安装收据必须为 JSON 对象。");
                foreach (string required in new[] { "Schema", "ProductId", "ProductName", "Version", "Root", "Files", "Profiles" })
                    if (!shape.ContainsKey(required)) throw new IOException("安装收据缺少字段：" + required);
                receipt = Json.Deserialize<InstallReceipt>(content);
            }
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
        static string BackupChanges(string root, List<FileChange> changes, string operation, string backupRoot)
        {
            if (changes.Count == 0) return null;
            if (String.IsNullOrEmpty(backupRoot)) backupRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AzureArchiveRevisionCompare", "InstallerBackups");
            backupRoot = NormalizeRoot(backupRoot); RejectLinks(backupRoot);
            string rootPrefix = NormalizeRoot(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (backupRoot.Equals(NormalizeRoot(root), StringComparison.OrdinalIgnoreCase) || backupRoot.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase)) throw new IOException("安装备份必须位于 AA 目录之外。");
            Directory.CreateDirectory(backupRoot); RejectLinks(backupRoot);
            string directory = Within(backupRoot, DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(directory);
            var record = new BackupRecord { ProductId = ProductId, Root = root, Operation = operation, CreatedUtc = DateTime.UtcNow.ToString("o") };
            foreach (FileChange change in changes)
            {
                if (!change.Path.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase)) throw new IOException("备份文件超出本次 AA 目录。");
                string relative = change.Path.Substring(rootPrefix.Length).Replace(Path.DirectorySeparatorChar, '/');
                record.Files.Add(new BackupFile { Path = relative, Existed = change.Before != null, Sha256 = change.Before == null ? null : Hash(change.Before) });
                if (change.Before == null) continue;
                string path = Within(directory, "files/" + relative); Directory.CreateDirectory(Path.GetDirectoryName(path));
                using (var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { output.Write(change.Before, 0, change.Before.Length); output.Flush(true); }
            }
            File.WriteAllBytes(Within(directory, "backup.json"), Serialize(record));
            return directory;
        }
        static string Commit(string root, List<FileChange> changes, string operation, string backupRoot)
        {
            string backup = BackupChanges(root, changes, operation, backupRoot);
            var done = new List<FileChange>(); var created = new List<string>();
            try
            {
                ValidateGame(root);
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
                if (failures.Count > 0) throw new InstallerRollbackException("操作未完成，以下文件无法自动恢复：" + String.Join("、", failures.ToArray()) + "。原始备份：" + backup, original);
                throw new IOException("操作未完成，已恢复本次文件更改：" + original.Message + "。原始备份：" + backup, original);
            }
            return backup;
        }
        public static OperationResult Install(string selectedRoot, byte[] plugin, byte[] manifest)
        { return Install(selectedRoot, plugin, manifest, null); }
        public static OperationResult Install(string selectedRoot, byte[] plugin, byte[] manifest, string backupRoot)
        {
            return WithExclusiveRoot(selectedRoot, delegate { return InstallCore(selectedRoot, plugin, manifest, backupRoot); });
        }
        static OperationResult InstallCore(string selectedRoot, byte[] plugin, byte[] manifest, string backupRoot)
        {
            string root = NormalizeRoot(selectedRoot); ValidateGame(root);
            if (plugin == null || plugin.Length < 2 || plugin[0] != 'M' || plugin[1] != 'Z') throw new IOException("内嵌插件无效。");
            Dictionary<string, object> metadata;
            try { metadata = Json.DeserializeObject(Utf8.GetString(manifest).TrimStart('\uFEFF')) as Dictionary<string, object>; }
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
            if (ReceiptVersion.Compare(receipt.Version, Version) > 0) throw new IOException("已安装更新版本 " + receipt.Version + "，此安装器不会降级覆盖。");
            var changes = new List<FileChange>();
            string previousVersion = receipt.Version;
            bool migrating = receipt.Version != Version;
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
            AddExpectedChange(changes, receiptPath, receiptBefore, Serialize(receipt));
            ValidateGame(root);
            string backup = Commit(root, changes, "install", backupRoot);
            return new OperationResult { Message = (migrating ? "升级完成。" : "安装完成。") + "已在当前配置「" + active + "」启用剧本改稿同步 MOD " + Version + "。", BackupDirectory = backup };
        }
        public static OperationResult Uninstall(string selectedRoot)
        { return Uninstall(selectedRoot, null); }
        public static OperationResult Uninstall(string selectedRoot, string backupRoot)
        {
            return WithExclusiveRoot(selectedRoot, delegate { return UninstallCore(selectedRoot, backupRoot); });
        }
        static OperationResult WithExclusiveRoot(string root, Func<OperationResult> operation)
        {
            string name = "Local\\AARevisionCompareUpdate-" + Hash(Utf8.GetBytes(NormalizeRoot(root).ToUpperInvariant()));
            using (var mutex = new Mutex(false, name))
            {
                bool owned = false;
                try
                {
                    try { owned = mutex.WaitOne(0); } catch (AbandonedMutexException) { owned = true; }
                    if (!owned) throw new IOException("此 AA 已有本 MOD 的安装或更新任务，请稍后重试。");
                    return operation();
                }
                finally { if (owned) mutex.ReleaseMutex(); }
            }
        }
        static OperationResult UninstallCore(string selectedRoot, string backupRoot)
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
            AddExpectedChange(changes, receiptPath, receiptBefore, remainingFiles.Count + remainingProfiles.Count == 0 ? null : Serialize(receipt));
            result.BackupDirectory = Commit(root, changes, "uninstall", backupRoot);
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

namespace AzureArchive.RevisionCompare.Installation {
    public static class ReceiptVersion
    {
        public static bool IsValid(string value)
        {
            if (String.IsNullOrEmpty(value) || value.Length > 128 || !Regex.IsMatch(value, @"^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)(-[0-9A-Za-z-]+(\.[0-9A-Za-z-]+)*)?(\+[0-9A-Za-z-]+(\.[0-9A-Za-z-]+)*)?$")) return false;
            string precedence = value.Split('+')[0]; int dash = precedence.IndexOf('-');
            if (dash >= 0) foreach (string part in precedence.Substring(dash + 1).Split('.')) if (Digits(part) && part.Length > 1 && part[0] == '0') return false;
            return true;
        }
        static bool Digits(string value) { return Regex.IsMatch(value, "^[0-9]+$"); }
        static int Number(string a, string b) { int length = a.Length.CompareTo(b.Length); return length == 0 ? String.CompareOrdinal(a, b) : length; }
        public static int Compare(string a, string b)
        {
            if (!IsValid(a) || !IsValid(b)) throw new IOException("更新版本号无效。");
            string left = a.Split('+')[0], right = b.Split('+')[0]; int la = left.IndexOf('-'), rb = right.IndexOf('-');
            string[] lc = (la < 0 ? left : left.Substring(0, la)).Split('.'), rc = (rb < 0 ? right : right.Substring(0, rb)).Split('.');
            for (int i = 0; i < 3; i++) { int result = Number(lc[i], rc[i]); if (result != 0) return result; }
            if (la < 0 || rb < 0) return (la < 0 ? 1 : 0).CompareTo(rb < 0 ? 1 : 0);
            string[] lp = left.Substring(la + 1).Split('.'), rp = right.Substring(rb + 1).Split('.');
            for (int i = 0; i < Math.Min(lp.Length, rp.Length); i++)
            {
                bool ln = Digits(lp[i]), rn = Digits(rp[i]);
                int result = ln && rn ? Number(lp[i], rp[i]) : ln != rn ? (ln ? -1 : 1) : String.CompareOrdinal(lp[i], rp[i]);
                if (result != 0) return result;
            }
            return lp.Length.CompareTo(rp.Length);
        }
    }
}
