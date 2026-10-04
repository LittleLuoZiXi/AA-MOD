using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace AzureArchive.Recorder;

internal enum RecorderUpdateSessionState { Idle, Preparing, Applying, Completed, Failed, RestartRequested }
internal sealed record RecorderUpdateSessionSnapshot(RecorderUpdateSessionState State, string Message, string Error, bool InstallationUncertain = false)
{
    internal bool BlocksRecording => InstallationUncertain || State is RecorderUpdateSessionState.Preparing or RecorderUpdateSessionState.Applying or RecorderUpdateSessionState.Completed or RecorderUpdateSessionState.RestartRequested;
    internal bool CanRequestRestart => State == RecorderUpdateSessionState.Completed;
}

internal interface IRecorderUpdateHelper : IDisposable
{
    bool HasExited { get; }
}

// All disk and process work runs away from the Unity thread. The caller alone owns UI and Application.Quit.
internal sealed class RecorderUpdateSession : IDisposable
{
    internal const string SupportedHostSha256 = "260FC3ECC5F38DAB5230BA624A52E495708D861196DDA72486C1250760FE6EFF";
    internal const int MaximumEntries = 8192;
    internal const long MaximumExpandedBytes = 1024L * 1024 * 1024;
    internal const long MaximumEntryBytes = 256L * 1024 * 1024;
    private const string Mod = "mods/AzureArchiveRecorder/";
    private const string HelperName = "更新内录MOD.exe";
    private readonly Func<string> _gameRoot;
    private readonly string _executable, _hostHash, _temporaryBase;
    private readonly int _parentPid;
    private readonly long _parentStartUtcTicks;
    private readonly Func<ProcessStartInfo, IRecorderUpdateHelper> _startHelper;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private RecorderUpdateSessionSnapshot _snapshot = new(RecorderUpdateSessionState.Idle, "", "");
    private IRecorderUpdateHelper? _helper;
    private string? _taskRoot, _token;
    private bool _disposed, _installationCompleted;

    internal RecorderUpdateSession() : this(() => BepInEx.Paths.GameRootPath,
        Environment.ProcessPath ?? "", SupportedHostSha256,
        Path.Combine(Path.GetTempPath(), "AzureArchiveRecorderApply"),
        Environment.ProcessId, GetCurrentStartTicks(), StartProcess) { }

    private RecorderUpdateSession(Func<string> gameRoot, string executable, string hostHash, string temporaryBase,
        int parentPid, long parentStartUtcTicks, Func<ProcessStartInfo, IRecorderUpdateHelper> startHelper)
    {
        _gameRoot = gameRoot; _executable = executable; _hostHash = hostHash; _temporaryBase = temporaryBase;
        _parentPid = parentPid; _parentStartUtcTicks = parentStartUtcTicks; _startHelper = startHelper;
    }

    // Only deterministic local fixtures can replace process launching or the host identity. No production root/URL override.
    internal static RecorderUpdateSession CreateForTests(string root, string executable, string hostHash, string temporaryBase,
        Func<ProcessStartInfo, IRecorderUpdateHelper> startHelper) =>
        new(() => root, executable, hostHash, temporaryBase, Environment.ProcessId, GetCurrentStartTicks(), startHelper);

    internal RecorderUpdateSessionSnapshot Snapshot => Volatile.Read(ref _snapshot);

    internal Task PrepareAndStartAsync(RecorderUpdateInfo info, string verifiedZip, CancellationToken cancellationToken = default) =>
        Task.Run(async () =>
        {
            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                ThrowIfDisposed();
                if (Snapshot.BlocksRecording) throw new InvalidOperationException("更新安装任务尚未结束。");
                Set(RecorderUpdateSessionState.Preparing, "正在核验更新包…");
                _helper?.Dispose(); _helper = null; _taskRoot = null; _token = null; _installationCompleted = false;
                RecorderUpdateNetwork.ValidateInfo(info);
                if (!EqualHash(info.HostSha256, _hostHash)) throw new InvalidDataException("更新包不适用于当前 AA 本体。");
                var root = FullRoot(_gameRoot());
                var exe = Within(root, "AzureArchive.exe");
                if (!string.Equals(Path.GetFullPath(_executable), exe, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("当前进程不是所选 AA 目录中的 AzureArchive.exe。");
                NoLinks(root); NoLinks(exe);
                if (!Directory.Exists(Within(root, "AzureArchive_Data")) || !EqualHash(await HashFileAsync(exe, cancellationToken).ConfigureAwait(false), _hostHash))
                    throw new InvalidDataException("当前 AA 本体校验失败，已停止更新。");
                var source = Path.GetFullPath(verifiedZip); NoLinks(source);
                var taskBase = FullRoot(_temporaryBase); NoLinks(taskBase); Directory.CreateDirectory(taskBase); NoLinks(taskBase);
                _taskRoot = Within(taskBase, Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(_taskRoot); NoLinks(_taskRoot);
                var payloadPath = Within(_taskRoot, "payload.zip");
                await CopyVerifiedAsync(source, payloadPath, info.Size, info.Sha256, cancellationToken).ConfigureAwait(false);
                // Deny writers while validating the archive, extracting the helper and dispatching it.
                using var payload = OpenRead(payloadPath);
                using var zip = new ZipArchive(payload, ZipArchiveMode.Read, true);
                var receipt = await ValidatePackageAsync(zip, root, info, cancellationToken).ConfigureAwait(false);
                var helperRelative = Mod + info.Version + "/" + HelperName;
                var helperEntry = zip.GetEntry(helperRelative)!;
                var helperPath = Within(_taskRoot, HelperName);
                using (var input = helperEntry.Open())
                using (var output = new FileStream(helperPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, true))
                    await input.CopyToAsync(output, 65536, cancellationToken).ConfigureAwait(false);
                if (!EqualHash(await HashFileAsync(helperPath, cancellationToken).ConfigureAwait(false), receipt.Files!.Single(f => f.Path == helperRelative).Sha256))
                    throw new InvalidDataException("更新助手提取后校验失败。");
                _token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
                var jobPath = Within(_taskRoot, "job.json");
                await WriteJsonAtomicAsync(jobPath, new
                {
                    Root = root, ParentPid = _parentPid, ParentStartUtcTicks = _parentStartUtcTicks,
                    Version = info.Version, PayloadPath = payloadPath, PayloadSha256 = info.Sha256,
                    PayloadSize = info.Size, Token = _token
                }, cancellationToken).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                NoLinks(helperPath); NoLinks(payloadPath); NoLinks(jobPath);
                var start = new ProcessStartInfo(helperPath)
                {
                    UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden,
                    WorkingDirectory = _taskRoot
                };
                start.ArgumentList.Add("--job"); start.ArgumentList.Add(jobPath);
                _helper = _startHelper(start) ?? throw new IOException("更新助手未启动。");
                Set(RecorderUpdateSessionState.Applying, "正在安装内录 MOD 更新…");
            }
            catch (OperationCanceledException) { Set(RecorderUpdateSessionState.Failed, "更新已取消。", "更新已取消。", _helper != null); }
            catch (Exception exception) { Set(RecorderUpdateSessionState.Failed, "更新未完成。", Explain(exception), _helper != null); }
            finally { _gate.Release(); }
        });

    internal Task RefreshAsync(CancellationToken cancellationToken = default) => Task.Run(async () =>
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            if (_helper == null || _taskRoot == null || Snapshot.State == RecorderUpdateSessionState.RestartRequested) return;
            var statusPath = Within(_taskRoot, "status.json");
            if (!File.Exists(statusPath))
            {
                if (_helper.HasExited) throw new InvalidDataException("更新助手已退出，但未报告安装结果。请重启 AA 前检查安装状态。");
                return;
            }
            NoLinks(statusPath);
            using var stream = new FileStream(statusPath, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete, 4096, true);
            if (stream.Length <= 0 || stream.Length > 16384) throw new InvalidDataException("更新助手状态文件大小无效。");
            using var json = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
            RejectDuplicateProperties(json.RootElement);
            var status = json.RootElement.Deserialize<HelperStatus>() ?? throw new InvalidDataException("更新助手状态为空。");
            var message = SafeMessage(status.Message); var error = SafeMessage(status.Error);
            switch (status.State)
            {
                case "Applying":
                    if (_helper.HasExited) throw new InvalidDataException("更新助手意外退出，安装结果尚未确认。");
                    if (Snapshot.State == RecorderUpdateSessionState.Completed) throw new InvalidDataException("更新助手状态顺序异常。");
                    Set(RecorderUpdateSessionState.Applying, string.IsNullOrEmpty(message) ? "正在安装内录 MOD 更新…" : message); break;
                case "Completed":
                    if (_helper.HasExited) throw new InvalidDataException("更新已安装，但重启助手已退出。请保存工程并手动重启 AA。");
                    _installationCompleted = true; Set(RecorderUpdateSessionState.Completed, "内录MOD更新完成，AA即将重启"); break;
                case "Failed":
                    Set(RecorderUpdateSessionState.Failed, "更新安装失败。", error.Length == 0 ? (message.Length == 0 ? "安装助手报告更新失败。" : message) : error, status.InstallationUncertain != false || _installationCompleted); break;
                default: throw new InvalidDataException("更新助手返回了未授权或无效的状态。");
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception exception) { Set(RecorderUpdateSessionState.Failed, "无法确认更新安装状态。", Explain(exception), true); }
        finally { _gate.Release(); }
    });

    // The UI must check unsaved work again immediately before calling this, then quit only after true.
    internal Task<bool> RequestRestartAsync(CancellationToken cancellationToken = default) => Task.Run(async () =>
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            if (Snapshot.State == RecorderUpdateSessionState.RestartRequested) return true;
            if (!Snapshot.CanRequestRestart || _helper == null || _taskRoot == null || _token == null) return false;
            if (_helper.HasExited) throw new InvalidDataException("重启助手已退出，请保存工程并手动重启 AA。");
            await WriteJsonAtomicAsync(Within(_taskRoot, "restart.json"), new { Token = _token }, cancellationToken).ConfigureAwait(false);
            Set(RecorderUpdateSessionState.RestartRequested, "内录MOD更新完成，AA即将重启");
            return true;
        }
        catch (Exception exception) { Set(RecorderUpdateSessionState.Failed, "未能发送重启确认。", Explain(exception), true); return false; }
        finally { _gate.Release(); }
    });

    private static async Task<PayloadReceipt> ValidatePackageAsync(ZipArchive zip, string root, RecorderUpdateInfo info, CancellationToken token)
    {
        if (zip.Entries.Count < 4 || zip.Entries.Count > MaximumEntries) throw new InvalidDataException("更新包文件数量超出允许范围。");
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase); long total = 0;
        foreach (var entry in zip.Entries)
        {
            ValidateRelative(entry.FullName); Within(root, entry.FullName);
            var kind = (entry.ExternalAttributes >> 16) & 0xF000;
            if ((kind != 0 && kind != 0x8000) || (entry.ExternalAttributes & ((int)FileAttributes.ReparsePoint | (int)FileAttributes.Directory)) != 0 ||
                !names.Add(entry.FullName) || entry.Length < 0 || entry.Length > MaximumEntryBytes)
                throw new InvalidDataException("更新包包含重复、过大或非普通文件。");
            total = checked(total + entry.Length);
            if (total > MaximumExpandedBytes || entry.Length > Math.Max(1024L * 1024, entry.CompressedLength * 200L))
                throw new InvalidDataException("更新包展开大小或压缩比例超出限制。");
        }
        var manifest = zip.GetEntry("payload-manifest.json");
        if (manifest == null || manifest.Length > 4 * 1024 * 1024) throw new InvalidDataException("更新包清单缺失或过大。");
        PayloadReceipt receipt;
        using (var stream = manifest.Open())
        using (var document = await JsonDocument.ParseAsync(stream, new JsonDocumentOptions { MaxDepth = 16 }, token).ConfigureAwait(false))
        {
            RejectDuplicateProperties(document.RootElement);
            receipt = document.RootElement.Deserialize<PayloadReceipt>() ?? throw new InvalidDataException("更新清单为空。");
        }
        if (receipt.Product != "AzureArchiveRecorder" || receipt.Version != info.Version || !string.IsNullOrEmpty(receipt.Root) ||
            receipt.Files == null || receipt.Files.Count + 1 != zip.Entries.Count)
            throw new InvalidDataException("更新清单的产品、版本、根目录或文件数量不匹配。");
        var owned = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in receipt.Files)
        {
            if (file == null || !AllowedPayload(file.Path, info.Version) || !RecorderUpdateNetwork.IsSha256(file.Sha256) || !owned.Add(file.Path))
                throw new InvalidDataException("更新清单包含越界、重复或无效的文件。");
            Within(root, file.Path);
            var entry = zip.GetEntry(file.Path) ?? throw new InvalidDataException("更新包缺少清单文件：" + file.Path);
            using var stream = entry.Open();
            var hash = await HashStreamAsync(stream, entry.Length, token).ConfigureAwait(false);
            if (!EqualHash(hash, file.Sha256)) throw new InvalidDataException("更新包文件摘要不匹配：" + file.Path);
        }
        foreach (var required in new[] { "AzureArchive.Recorder.dll", "manifest.json", HelperName })
            if (!owned.Contains(Mod + info.Version + "/" + required) || zip.GetEntry(Mod + info.Version + "/" + required)!.Length == 0)
                throw new InvalidDataException("更新包缺少必要文件或必要文件为空：" + required);
        var modManifest = zip.GetEntry(Mod + info.Version + "/manifest.json")!;
        if (modManifest.Length > 1024 * 1024) throw new InvalidDataException("MOD 版本清单过大。");
        using (var stream = modManifest.Open())
        using (var document = await JsonDocument.ParseAsync(stream, new JsonDocumentOptions { MaxDepth = 16 }, token).ConfigureAwait(false))
        {
            RejectDuplicateProperties(document.RootElement);
            if (document.RootElement.ValueKind != JsonValueKind.Object ||
                !document.RootElement.TryGetProperty("name", out var name) || name.ValueKind != JsonValueKind.String || name.GetString() != "AzureArchiveRecorder" ||
                !document.RootElement.TryGetProperty("version_number", out var number) || number.ValueKind != JsonValueKind.String || number.GetString() != info.Version)
                throw new InvalidDataException("MOD 版本清单与更新信息不匹配。");
        }
        // Legacy/generated entries are ownership metadata, never executable content or arbitrary removal authority.
        var metadata = new HashSet<string>(owned, StringComparer.OrdinalIgnoreCase);
        foreach (var file in receipt.LegacyFiles ?? new List<PayloadFile>())
        {
            if (file == null || !AllowedLegacy(file.Path, info.Version) || !RecorderUpdateNetwork.IsSha256(file.Sha256) || !metadata.Add(file.Path))
                throw new InvalidDataException("更新包旧版文件清单无效。");
            Within(root, file.Path);
        }
        foreach (var file in receipt.GeneratedFiles ?? new List<PayloadFile>())
        {
            if (file == null || !new[] { "30", "40", "50" }.Any(gpu => file.Path == Mod + "runtime/gpu-runtimes/rtx" + gpu + "/nvngx_dlssnr.dll") ||
                !RecorderUpdateNetwork.IsSha256(file.Sha256) || !metadata.Add(file.Path)) throw new InvalidDataException("更新包生成文件清单无效。");
            Within(root, file.Path);
        }
        return receipt;
    }

    private static bool AllowedPayload(string path, string version)
    {
        ValidateRelative(path);
        if (path == Mod + "卸载内录MOD.exe" || path == Mod + "使用说明.txt" || path == Mod + "第三方许可.txt") return true;
        if (path.StartsWith(Mod + "runtime/", StringComparison.Ordinal))
        {
            var extension = Path.GetExtension(path).ToLowerInvariant();
            return !new[] { ".cs", ".csproj", ".sln", ".pdb", ".ps1", ".bat", ".cmd", ".spec", ".bak", ".orig", ".rej" }.Contains(extension);
        }
        return new[] { "AzureArchive.Recorder.dll", "manifest.json", HelperName }.Any(name => path == Mod + version + "/" + name);
    }

    private static bool AllowedLegacy(string path, string version)
    {
        ValidateRelative(path);
        if (!path.StartsWith(Mod, StringComparison.Ordinal)) return false;
        var parts = path.Substring(Mod.Length).Split('/');
        if (parts.Length != 2 || !RecorderNumericVersion.TryParse(parts[0], out var previous) || !RecorderNumericVersion.TryParse(version, out var next) || previous.CompareTo(next) >= 0) return false;
        RecorderNumericVersion.TryParse("1.2.4", out var firstUpdater);
        return parts[1] == "AzureArchive.Recorder.dll" || parts[1] == "manifest.json" || (previous.CompareTo(firstUpdater) >= 0 && parts[1] == HelperName);
    }

    private static void ValidateRelative(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Length > 220 || path.IndexOfAny(new[] { '\\', ':', '\0', '<', '>', '"', '|', '?', '*' }) >= 0 || Path.IsPathRooted(path))
            throw new InvalidDataException("更新文件路径无效。");
        foreach (var part in path.Split('/'))
        {
            if (part.Length == 0 || part == "." || part == ".." || part.EndsWith(".", StringComparison.Ordinal) || part.EndsWith(" ", StringComparison.Ordinal) || part.Any(char.IsControl))
                throw new InvalidDataException("更新文件路径包含非法片段。");
            var stem = part.Split('.')[0].ToUpperInvariant();
            if (stem == "CON" || stem == "PRN" || stem == "AUX" || stem == "NUL" || stem == "CONIN$" || stem == "CONOUT$" ||
                (stem.Length == 4 && (stem.StartsWith("COM", StringComparison.Ordinal) || stem.StartsWith("LPT", StringComparison.Ordinal)) && ("123456789¹²³".Contains(stem[3]))))
                throw new InvalidDataException("更新文件使用了系统保留名称。");
        }
    }

    private static string FullRoot(string path) => Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    private static string Within(string root, string relative)
    {
        ValidateRelative(relative);
        var path = Path.GetFullPath(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));
        if (!path.StartsWith(FullRoot(root) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("更新文件超出允许目录。");
        NoLinks(path); return path;
    }
    private static void NoLinks(string path)
    {
        for (string? current = Path.GetFullPath(path); !string.IsNullOrEmpty(current); current = Path.GetDirectoryName(current))
        {
            try { if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("更新路径包含链接或目录联接，已停止。"); }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
        }
    }
    private static FileStream OpenRead(string path) { NoLinks(path); return new(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, true); }
    private static async Task<string> HashFileAsync(string path, CancellationToken token)
    { using var stream = OpenRead(path); return await HashStreamAsync(stream, stream.Length, token).ConfigureAwait(false); }
    private static async Task<string> HashStreamAsync(Stream stream, long expectedLength, CancellationToken token)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[65536]; long length = 0;
        while (true)
        {
            var count = await stream.ReadAsync(buffer.AsMemory(), token).ConfigureAwait(false); if (count == 0) break;
            length = checked(length + count); if (length > expectedLength) throw new InvalidDataException("更新文件实际大小超过清单限制。");
            hash.AppendData(buffer, 0, count);
        }
        if (length != expectedLength) throw new InvalidDataException("更新文件被截断。");
        return Convert.ToHexString(hash.GetHashAndReset());
    }
    private static async Task CopyVerifiedAsync(string source, string target, long size, string expectedHash, CancellationToken token)
    {
        using var input = OpenRead(source);
        if (input.Length != size) throw new InvalidDataException("更新包大小与下载信息不一致。");
        using var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, true);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256); var buffer = new byte[65536]; long total = 0;
        while (true)
        {
            var count = await input.ReadAsync(buffer.AsMemory(), token).ConfigureAwait(false); if (count == 0) break;
            total = checked(total + count); if (total > size) throw new InvalidDataException("更新包大小超出下载信息。");
            hash.AppendData(buffer, 0, count); await output.WriteAsync(buffer.AsMemory(0, count), token).ConfigureAwait(false);
        }
        if (total != size || !EqualHash(Convert.ToHexString(hash.GetHashAndReset()), expectedHash)) throw new InvalidDataException("更新包完整校验失败。");
        await output.FlushAsync(token).ConfigureAwait(false);
    }
    private static async Task WriteJsonAtomicAsync<T>(string path, T value, CancellationToken token)
    {
        NoLinks(path); var temporary = path + ".tmp"; NoLinks(temporary);
        using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, true))
        { await JsonSerializer.SerializeAsync(stream, value, cancellationToken: token).ConfigureAwait(false); await stream.FlushAsync(token).ConfigureAwait(false); }
        NoLinks(path); File.Move(temporary, path);
    }
    private static void RejectDuplicateProperties(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var item in element.EnumerateObject()) { if (!names.Add(item.Name)) throw new InvalidDataException("更新 JSON 包含重复字段。"); RejectDuplicateProperties(item.Value); }
        }
        else if (element.ValueKind == JsonValueKind.Array) foreach (var item in element.EnumerateArray()) RejectDuplicateProperties(item);
    }
    private static string SafeMessage(string? value) => (value ?? "").Length > 1024 ? value!.Substring(0, 1024) : value ?? "";
    private static bool EqualHash(string? left, string? right) => string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
    private static string Explain(Exception exception) => exception is InvalidDataException ? exception.Message : exception is JsonException ? "更新清单或助手状态不是有效的 JSON。" :
        exception is OperationCanceledException ? "操作已取消。" : exception is UnauthorizedAccessException ? "无法访问更新文件，请检查目录权限。" : exception is IOException ? "更新文件读写失败，请检查磁盘空间与文件占用。" : "更新助手启动或状态检查失败，请稍后重试。";
    private void Set(RecorderUpdateSessionState state, string message, string error = "", bool uncertain = false) => Volatile.Write(ref _snapshot, new(state, message, error, uncertain));
    private static long GetCurrentStartTicks() { using var process = Process.GetCurrentProcess(); return process.StartTime.ToUniversalTime().Ticks; }
    private static IRecorderUpdateHelper StartProcess(ProcessStartInfo start) => new ProcessHelper(Process.Start(start) ?? throw new IOException("更新助手未启动。"));
    private void ThrowIfDisposed() { if (_disposed) throw new ObjectDisposedException(nameof(RecorderUpdateSession)); }
    public void Dispose() { _disposed = true; /* A dispatched helper owns its lifetime; never kill it or remove its task directory. */ }
    private sealed class ProcessHelper : IRecorderUpdateHelper
    {
        private readonly Process _process;
        internal ProcessHelper(Process process) { _process = process; }
        public bool HasExited => _process.HasExited;
        public void Dispose() => _process.Dispose();
    }
    private sealed class PayloadFile { public string Path { get; set; } = ""; public string Sha256 { get; set; } = ""; }
    private sealed class PayloadReceipt
    {
        public string Product { get; set; } = ""; public string Version { get; set; } = ""; public string Root { get; set; } = "";
        public List<PayloadFile>? Files { get; set; } public List<PayloadFile>? LegacyFiles { get; set; } public List<PayloadFile>? GeneratedFiles { get; set; }
    }
    private sealed class HelperStatus { public string State { get; set; } = ""; public string Error { get; set; } = ""; public string Message { get; set; } = ""; public bool? InstallationUncertain { get; set; } }
}
