using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace AzureArchive.RevisionCompare;

internal sealed record RevisionUpdateApplyPayload(string Version, string AssemblyPath, string ManifestPath,
    string AssemblySha256, long AssemblySize, string ManifestSha256, long ManifestSize);
internal enum RevisionUpdateApplyState { Preparing, AwaitingApproval, WaitingForExit, Applying, Completed, Failed, Cancelled }
internal sealed record RevisionUpdateApplySnapshot(RevisionUpdateApplyState State, string Message, string Error, bool InstallationUncertain = false);

// The downloaded files are data. Only this installed plugin's embedded helper code is executed.
internal sealed class RevisionUpdateApplySession : IDisposable
{
    private readonly string root, currentVersion;
    private readonly SemaphoreSlim gate = new(1, 1);
    private RevisionUpdateApplySnapshot snapshot = new(RevisionUpdateApplyState.Preparing, "尚未准备更新。", "");
    private string? taskDirectory, token;
    private Process? helper;
    private bool started, confirmed, disposed;
    private volatile bool cancelRequested;
    internal RevisionUpdateApplySnapshot Snapshot => Volatile.Read(ref snapshot);
    internal string? TaskDirectory => taskDirectory;

    internal RevisionUpdateApplySession(string gameRoot, string installedVersion)
    {
        root = Path.GetFullPath(gameRoot).TrimEnd(Path.DirectorySeparatorChar);
        currentVersion = installedVersion;
    }

    internal async Task PrepareAndStartAsync(RevisionUpdateApplyPayload payload, CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (disposed) throw new ObjectDisposedException(nameof(RevisionUpdateApplySession));
            if (started) throw new InvalidOperationException("更新准备已经开始。");
            started = true;
            if (!RevisionSemanticVersion.TryParse(currentVersion, out var current) || !RevisionSemanticVersion.TryParse(payload.Version, out var next) || next.ToString() != payload.Version || next.CompareTo(current) <= 0)
                throw new InvalidDataException("更新目标必须为更新的规范版本号。");
            Set(RevisionUpdateApplyState.Preparing, "正在准备安全更新助手…");
            await Task.Run(() => PrepareFilesAndStart(payload, cancellationToken), cancellationToken).ConfigureAwait(false);
            await WaitForAsync(RevisionUpdateApplyState.AwaitingApproval, TimeSpan.FromSeconds(60), cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { Cancel(); throw; }
        catch (Exception error) { Cancel(); Set(RevisionUpdateApplyState.Failed, "更新准备失败，原安装未改动。", error.Message); throw; }
        finally { gate.Release(); }
    }

    private void PrepareFilesAndStart(RevisionUpdateApplyPayload payload, CancellationToken cancellationToken)
    {
        NoLinks(root);
        string executable = Within(root, "AzureArchive.exe");
        if (!Directory.Exists(Within(root, "AzureArchive_Data")) || !string.Equals(Path.GetFullPath(Environment.ProcessPath ?? ""), executable, StringComparison.OrdinalIgnoreCase))
            throw new IOException("当前进程与 AA 安装目录不匹配。");
        string temporary = Path.Combine(Path.GetTempPath(), "AzureArchiveRevisionCompareApply");
        NoLinks(temporary); Directory.CreateDirectory(temporary); NoLinks(temporary);
        taskDirectory = Within(temporary, Guid.NewGuid().ToString("N")); Directory.CreateDirectory(taskDirectory); NoLinks(taskDirectory);
        CopyVerified(payload.AssemblyPath, Within(taskDirectory, "AzureArchive.RevisionCompare.dll"), payload.AssemblySize, payload.AssemblySha256, 64L * 1024 * 1024, cancellationToken);
        CopyVerified(payload.ManifestPath, Within(taskDirectory, "manifest.json"), payload.ManifestSize, payload.ManifestSha256, 65536, cancellationToken);
        WriteResource("RevisionCompare.UpdateHelper.ps1", Within(taskDirectory, "UpdateHelper.ps1"));
        WriteResource("RevisionCompare.UpdateHelper.cs", Within(taskDirectory, "UpdateHelper.cs"));
        string cecil = Within(root, "BepInEx/core/Mono.Cecil.dll");
        if (!File.Exists(cecil)) throw new IOException("AA 的本地元数据校验组件缺失。");
        File.Copy(cecil, Within(taskDirectory, "Mono.Cecil.dll"), false);
        token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        using var current = Process.GetCurrentProcess();
        byte[] job = JsonSerializer.SerializeToUtf8Bytes(new
        {
            Root = root, CurrentVersion = currentVersion, TargetVersion = payload.Version, Token = token,
            ParentPid = Environment.ProcessId, ParentStartUtcTicks = current.StartTime.ToUniversalTime().Ticks,
            payload.AssemblySha256, payload.AssemblySize, payload.ManifestSha256, payload.ManifestSize,
            HostSha256 = Hash(File.ReadAllBytes(executable))
        });
        string jobPath = Within(taskDirectory, "job.json"); File.WriteAllBytes(jobPath, job);
        cancellationToken.ThrowIfCancellationRequested();
        if (cancelRequested) throw new OperationCanceledException("更新已取消。");
        string powershell = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32", "WindowsPowerShell", "v1.0", "powershell.exe");
        if (!File.Exists(powershell)) throw new IOException("Windows PowerShell 不可用，无法安全准备更新。");
        var start = new ProcessStartInfo(powershell) { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden, WorkingDirectory = taskDirectory };
        start.Environment.Remove("DOORSTOP_DISABLE");
        foreach (string argument in new[] { "-NoLogo", "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", Within(taskDirectory, "UpdateHelper.ps1"), "-JobPath", jobPath, "-JobSha256", Hash(job) }) start.ArgumentList.Add(argument);
        helper = Process.Start(start) ?? throw new IOException("更新助手未启动。");
    }

    internal async Task<bool> AuthorizeExitAsync(CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (disposed) throw new ObjectDisposedException(nameof(RevisionUpdateApplySession));
            if (cancelRequested) return false;
            Refresh();
            if (Snapshot.State != RevisionUpdateApplyState.AwaitingApproval || taskDirectory == null || token == null || helper == null || helper.HasExited) return false;
            Signal("authorize");
            await WaitForAsync(RevisionUpdateApplyState.WaitingForExit, TimeSpan.FromSeconds(10), cancellationToken).ConfigureAwait(false);
            confirmed = true;
            return true;
        }
        catch { Cancel(); throw; }
        finally { gate.Release(); }
    }

    internal void Refresh()
    {
        if (taskDirectory == null) return;
        string path = Within(taskDirectory, "status.json");
        if (File.Exists(path))
        {
            if (new FileInfo(path).Length > 16384) throw new IOException("更新助手状态文件过大。");
            using var json = JsonDocument.Parse(File.ReadAllBytes(path)); var value = json.RootElement;
            if (!Enum.TryParse<RevisionUpdateApplyState>(value.GetProperty("State").GetString(), out var state)) throw new IOException("更新助手状态无效。");
            string message = value.GetProperty("Message").GetString() ?? "";
            string error = value.GetProperty("Error").GetString() ?? "";
            bool uncertain = value.TryGetProperty("InstallationUncertain", out var field) && field.GetBoolean();
            Volatile.Write(ref snapshot, new(state, message, error, uncertain));
        }
        if (helper != null && helper.HasExited && Snapshot.State is RevisionUpdateApplyState.Preparing or RevisionUpdateApplyState.AwaitingApproval or RevisionUpdateApplyState.WaitingForExit)
            Set(RevisionUpdateApplyState.Failed, "更新助手已退出，未确认完成。", Snapshot.Error);
    }

    private async Task WaitForAsync(RevisionUpdateApplyState desired, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var timer = Stopwatch.StartNew();
        while (timer.Elapsed < timeout)
        {
            cancellationToken.ThrowIfCancellationRequested(); Refresh();
            if (cancelRequested) throw new OperationCanceledException("更新已取消。", cancellationToken);
            if (Snapshot.State == desired) return;
            if (Snapshot.State == RevisionUpdateApplyState.Failed) throw new IOException(string.IsNullOrEmpty(Snapshot.Error) ? Snapshot.Message : Snapshot.Error);
            if (Snapshot.State == RevisionUpdateApplyState.Cancelled) throw new OperationCanceledException(Snapshot.Message, cancellationToken);
            await Task.Delay(100, cancellationToken).ConfigureAwait(false);
        }
        throw new TimeoutException("更新助手响应超时，原安装未改动。");
    }

    internal void Cancel()
    {
        // Explicit cancellation can revoke an acknowledged exit before the AA process exits.
        cancelRequested = true;
        confirmed = false;
        try { if (taskDirectory != null && token != null) Signal("cancel"); }
        catch (Exception error)
        {
            // The owning AA process is still alive here, so the helper can only be waiting.
            // If its revoke file cannot be written, terminating it prevents a delayed install.
            try
            {
                if (helper != null && !helper.HasExited) { helper.Kill(); if (!helper.WaitForExit(2000)) throw new IOException("更新助手未退出。"); }
            }
            catch (Exception stopError)
            {
                Volatile.Write(ref snapshot, new(RevisionUpdateApplyState.Failed, "无法确认更新已撤销，请保持 AA 打开。", error.Message + " " + stopError.Message, true));
                throw new IOException("无法撤销更新助手。", stopError);
            }
        }
        Set(RevisionUpdateApplyState.Cancelled, "更新已取消，原安装未改动。");
    }
    private void Signal(string name)
    {
        if (taskDirectory == null || token == null) throw new InvalidOperationException("更新尚未准备好。");
        string path = Within(taskDirectory, name), temp = Within(taskDirectory, name + ".new");
        File.WriteAllText(temp, token, new UTF8Encoding(false));
        if (File.Exists(path)) File.Replace(temp, path, null, true); else File.Move(temp, path);
    }
    private void Set(RevisionUpdateApplyState state, string message, string error = "") => Volatile.Write(ref snapshot, new(state, message, error));
    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));
    private static void CopyVerified(string source, string destination, long size, string hash, long maximum, CancellationToken cancellationToken)
    {
        NoLinks(source);
        using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (size <= 0 || size > maximum || input.Length != size || hash.Length != 64) throw new IOException("更新文件大小或校验字段无效。");
        using var memory = new MemoryStream(); input.CopyTo(memory); byte[] bytes = memory.ToArray();
        if (!string.Equals(Hash(bytes), hash, StringComparison.OrdinalIgnoreCase)) throw new IOException("下载的更新文件校验失败。");
        cancellationToken.ThrowIfCancellationRequested();
        using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None); output.Write(bytes); output.Flush(true);
    }
    private static void WriteResource(string name, string path)
    {
        using var input = typeof(RevisionUpdateApplySession).Assembly.GetManifestResourceStream(name) ?? throw new IOException("本地插件缺少受信更新助手资源。");
        using var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None); input.CopyTo(output); output.Flush(true);
    }
    private static string Within(string directory, string relative)
    {
        string path = Path.GetFullPath(Path.Combine(directory, relative.Replace('/', Path.DirectorySeparatorChar)));
        if (!path.StartsWith(Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new IOException("更新路径超出允许范围。");
        NoLinks(path); return path;
    }
    private static void NoLinks(string path)
    {
        for (string? current = Path.GetFullPath(path); !string.IsNullOrEmpty(current); current = Path.GetDirectoryName(current))
        {
            FileAttributes attributes;
            try { attributes = File.GetAttributes(current); } catch (FileNotFoundException) { continue; } catch (DirectoryNotFoundException) { continue; }
            if ((attributes & FileAttributes.ReparsePoint) != 0) throw new IOException("更新路径不能包含链接或目录联接。");
        }
    }
    public void Dispose()
    {
        if (disposed) return;
        disposed = true; if (!confirmed) Cancel(); helper?.Dispose();
        // Status and backups intentionally survive AA exit for failure diagnosis/recovery.
    }
}
