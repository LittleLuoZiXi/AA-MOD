using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AzureArchive.Recorder;

namespace BepInEx { public static class Paths { public static string GameRootPath => throw new InvalidOperationException("Production AA must never be used by fixture tests."); } }

public static class RecorderUpdateSessionTests
{
    private const string Version = "1.2.5";
    private const string Prefix = "mods/AzureArchiveRecorder/" + Version + "/";
    private static int _passed;
    public static int Main(string[] args) { Run(args[0]); return 0; }
    public static void Run(string output)
    {
        Directory.CreateDirectory(output);
        RunAsync(output).GetAwaiter().GetResult();
        Console.WriteLine("Recorder update session: " + _passed + " checks passed. No AA/helper executable was launched.");
    }
    private static void Check(bool condition, string label) { if (!condition) throw new Exception("FAIL: " + label); _passed++; Console.WriteLine("PASS: " + label); }
    private static async Task RunAsync(string output)
    {
        // Doorstop tests run before any fixture work and restore process-local state.
        var previousDisable = Environment.GetEnvironmentVariable("DOORSTOP_DISABLE");
        var previousSentinel = Environment.GetEnvironmentVariable("AA_RECORDER_UPDATE_TEST_ENV");
        try
        {
            foreach (var inherited in new[] { "TRUE", "FALSE" })
            {
                Environment.SetEnvironmentVariable("DOORSTOP_DISABLE", inherited);
                Environment.SetEnvironmentVariable("AA_RECORDER_UPDATE_TEST_ENV", "keep-original");
                using var inheritedTest = new Fixture(output, "inherited-disable-" + inherited);
                await inheritedTest.Session.PrepareAndStartAsync(inheritedTest.Info, inheritedTest.Zip);
                Check(inheritedTest.Start != null && inheritedTest.Session.Snapshot.State == RecorderUpdateSessionState.Applying, "inherited environment still dispatches verified helper");
                Check(!inheritedTest.Start!.Environment.ContainsKey("DOORSTOP_DISABLE"), "helper must not inherit Doorstop disable marker " + inherited);
                Check(inheritedTest.Start.Environment["AA_RECORDER_UPDATE_TEST_ENV"] == "keep-original", "helper retains unrelated environment");
                Check(Environment.GetEnvironmentVariable("DOORSTOP_DISABLE") == inherited, "running AA process environment is not changed");
                Check(inheritedTest.Start.WorkingDirectory == inheritedTest.TaskRoot, "helper keeps its validated task working directory");
            }
        }
        finally
        {
            Environment.SetEnvironmentVariable("DOORSTOP_DISABLE", previousDisable);
            Environment.SetEnvironmentVariable("AA_RECORDER_UPDATE_TEST_ENV", previousSentinel);
        }
        using (var good = new Fixture(output, "valid"))
        {
            Check(!await good.Session.RequestRestartAsync(), "restart denied before installation");
            await good.Session.PrepareAndStartAsync(good.Info, good.Zip);
            Check(good.Session.Snapshot.State == RecorderUpdateSessionState.Applying, "complete validated package starts helper");
            Check(good.Start != null && !good.Start.UseShellExecute && good.Start.CreateNoWindow && good.Start.WindowStyle == ProcessWindowStyle.Hidden, "helper is hidden without shell execution");
            Check(good.Start!.ArgumentList.Count == 2 && good.Start.ArgumentList[0] == "--job" && good.Start.FileName == Path.Combine(good.TaskRoot, "更新内录MOD.exe"), "exact helper name and job argument");
            using var job = JsonDocument.Parse(File.ReadAllText(good.Start.ArgumentList[1]));
            var data = job.RootElement;
            Check(data.GetProperty("Root").GetString() == good.Root && data.GetProperty("ParentPid").GetInt32() == Environment.ProcessId && data.GetProperty("ParentStartUtcTicks").GetInt64() > 0, "job binds actual root and parent identity");
            Check(data.GetProperty("PayloadPath").GetString() == Path.Combine(good.TaskRoot, "payload.zip") && data.GetProperty("PayloadSize").GetInt64() == good.Info.Size && data.GetProperty("PayloadSha256").GetString() == good.Info.Sha256, "job binds verified package hash and size");
            Check(Guid.TryParseExact(Path.GetFileName(good.TaskRoot), "N", out _) && data.GetProperty("Token").GetString()!.Length == 64, "private GUID-N task with random 64-hex token");
            File.Delete(good.Zip);
            Check(File.Exists(Path.Combine(good.TaskRoot, "payload.zip")), "download can be released after coordinator copies payload");
            Check(!File.Exists(Path.Combine(good.TaskRoot, "restart.json")) && !await good.Session.RequestRestartAsync(), "applying does not authorize restart");
            await good.Status("Applying"); await good.Session.RefreshAsync();
            Check(good.Session.Snapshot.BlocksRecording && good.Session.Snapshot.State == RecorderUpdateSessionState.Applying, "applying blocks recording");
            await good.Status("Completed"); await good.Session.RefreshAsync();
            Check(good.Session.Snapshot.CanRequestRestart && good.Session.Snapshot.Message == "内录MOD更新完成，AA即将重启" && good.Session.Snapshot.BlocksRecording, "completion uses exact prompt and continues blocking recording");
            Check(!File.Exists(Path.Combine(good.TaskRoot, "restart.json")), "completion alone never writes restart authorization");
            Check(await good.Session.RequestRestartAsync(), "explicit restart confirmation succeeds");
            using var restart = JsonDocument.Parse(File.ReadAllText(Path.Combine(good.TaskRoot, "restart.json")));
            Check(restart.RootElement.GetProperty("Token").GetString() == data.GetProperty("Token").GetString(), "restart token exactly matches job");
            Check(await good.Session.RequestRestartAsync() && good.Session.Snapshot.State == RecorderUpdateSessionState.RestartRequested, "restart request is idempotent");
        }
        foreach (var bad in new[] { "digest", "size", "manifest-version", "manifest-root", "missing-helper", "entry-digest", "extra-entry", "duplicate-entry", "case-duplicate", "traversal", "backslash", "reserved", "trailing-dot", "symbolic-link", "directory", "empty-helper", "inner-manifest", "bomb", "extra-version-file", "runtime-source", "legacy-escape", "legacy-helper-old", "legacy-helper-future", "generated-escape", "duplicate-property", "host-digest", "host-path", "target-junction" })
        {
            using var test = new Fixture(output, bad, bad);
            await test.Session.PrepareAndStartAsync(test.Info, test.Zip);
            Check(test.Start == null && test.Session.Snapshot.State == RecorderUpdateSessionState.Failed && !test.Session.Snapshot.BlocksRecording && test.Session.Snapshot.Error.Length > 0, "reject " + bad + " before running helper");
        }
        using (var test = new Fixture(output, "empty-runtime", "empty-entry"))
        {
            await test.Session.PrepareAndStartAsync(test.Info, test.Zip);
            Check(test.Session.Snapshot.State == RecorderUpdateSessionState.Applying, "legitimate empty runtime metadata accepted with verified empty hash");
        }
        foreach (var uncertain in new[] { "missing", "true" })
        {
            using var test = new Fixture(output, "rollback-" + uncertain);
            await test.Session.PrepareAndStartAsync(test.Info, test.Zip);
            await File.WriteAllTextAsync(Path.Combine(test.TaskRoot, "status.json"), uncertain == "missing" ? "{\"State\":\"Failed\",\"Error\":\"rollback unknown\"}" : "{\"State\":\"Failed\",\"InstallationUncertain\":true}");
            await test.Session.RefreshAsync();
            Check(test.Session.Snapshot.State == RecorderUpdateSessionState.Failed && test.Session.Snapshot.BlocksRecording, "uncertain rollback " + uncertain + " continues blocking recording");
        }
        using (var test = new Fixture(output, "legacy-helper", "legacy-helper"))
        {
            await test.Session.PrepareAndStartAsync(test.Info, test.Zip);
            Check(test.Session.Snapshot.State == RecorderUpdateSessionState.Applying, "1.2.4 updater may be retained as legacy ownership during 1.2.5 update");
            await test.Status("Completed"); await test.Session.RefreshAsync();
            await test.Status("Failed", "Restart failed after applying"); await test.Session.RefreshAsync();
            Check(test.Session.Snapshot.State == RecorderUpdateSessionState.Failed && test.Session.Snapshot.BlocksRecording, "failure after observed completion can never release recording lock");
        }
        using (var test = new Fixture(output, "cancel"))
        {
            using var cancel = new CancellationTokenSource(); cancel.Cancel();
            try { await test.Session.PrepareAndStartAsync(test.Info, test.Zip, cancel.Token); } catch (OperationCanceledException) { }
            Check(test.Start == null && !test.Session.Snapshot.BlocksRecording, "cancellation never launches helper");
        }
        using (var test = new Fixture(output, "rolled-back"))
        {
            await test.Session.PrepareAndStartAsync(test.Info, test.Zip); await test.Status("Failed", "安装失败，已回滚。"); await test.Session.RefreshAsync();
            Check(test.Session.Snapshot.State == RecorderUpdateSessionState.Failed && !test.Session.Snapshot.BlocksRecording && !await test.Session.RequestRestartAsync(), "confirmed installer failure releases recording and denies restart");
        }
        foreach (var state in new[] { "Restarting", "Unexpected", "malformed", "duplicate", "dead", "completed-dead" })
        {
            using var test = new Fixture(output, "status-" + state);
            await test.Session.PrepareAndStartAsync(test.Info, test.Zip);
            if (state == "malformed") await File.WriteAllTextAsync(Path.Combine(test.TaskRoot, "status.json"), "bad JSON");
            else if (state == "duplicate") await File.WriteAllTextAsync(Path.Combine(test.TaskRoot, "status.json"), "{\"State\":\"Applying\",\"State\":\"Completed\"}");
            else if (state == "dead") test.Helper.HasExited = true;
            else if (state == "completed-dead") { await test.Status("Completed"); test.Helper.HasExited = true; }
            else await test.Status(state);
            await test.Session.RefreshAsync();
            Check(test.Session.Snapshot.State == RecorderUpdateSessionState.Failed && test.Session.Snapshot.BlocksRecording && !await test.Session.RequestRestartAsync(), "unsafe status " + state + " blocks recording and restart");
        }
    }
    private sealed class FakeHelper : IRecorderUpdateHelper { public bool HasExited { get; set; } public void Dispose() { } }
    private sealed class Owned { public string Path { get; set; } = ""; public string Sha256 { get; set; } = ""; }
    private sealed class Receipt
    {
        public string Product { get; set; } = "AzureArchiveRecorder"; public string Version { get; set; } = RecorderUpdateSessionTests.Version;
        public string Root { get; set; } = ""; public List<Owned> Files { get; set; } = new(); public List<Owned> LegacyFiles { get; set; } = new(); public List<Owned> GeneratedFiles { get; set; } = new();
    }
    private sealed class Fixture : IDisposable
    {
        internal string Root, Zip;
        internal RecorderUpdateInfo Info;
        internal RecorderUpdateSession Session;
        internal FakeHelper Helper = new();
        internal ProcessStartInfo? Start;
        internal string TaskRoot => Start!.WorkingDirectory;
        internal Fixture(string output, string name, string bad = "")
        {
            var directory = Path.Combine(output, name); Directory.CreateDirectory(directory);
            Root = Path.Combine(directory, "AA"); Directory.CreateDirectory(Path.Combine(Root, "AzureArchive_Data"));
            var exe = Path.Combine(Root, "AzureArchive.exe"); File.WriteAllText(exe, "inert fixture; never executable");
            Zip = Path.Combine(directory, "verified.zip");
            var bytes = new Dictionary<string, byte[]>(StringComparer.Ordinal)
            {
                [Prefix + "AzureArchive.Recorder.dll"] = Encoding.UTF8.GetBytes("fixture DLL"),
                [Prefix + "manifest.json"] = Encoding.UTF8.GetBytes("{\"name\":\"AzureArchiveRecorder\",\"version_number\":\"" + (bad == "inner-manifest" ? "1.2.6" : Version) + "\"}"),
                [Prefix + "更新内录MOD.exe"] = Encoding.UTF8.GetBytes("inert helper; no process started"),
                ["mods/AzureArchiveRecorder/runtime/ffmpeg/ffmpeg.exe"] = Encoding.UTF8.GetBytes("inert FFmpeg")
            };
            string? special = bad switch
            {
                "traversal" => "mods/AzureArchiveRecorder/runtime/../../outside.exe",
                "backslash" => "mods/AzureArchiveRecorder/runtime/x\\outside.exe",
                "reserved" => "mods/AzureArchiveRecorder/runtime/CON.txt",
                "trailing-dot" => "mods/AzureArchiveRecorder/runtime/name.",
                "symbolic-link" => "mods/AzureArchiveRecorder/runtime/link",
                "directory" => "mods/AzureArchiveRecorder/runtime/folder/",
                "empty-entry" => "mods/AzureArchiveRecorder/runtime/empty",
                "bomb" => "mods/AzureArchiveRecorder/runtime/zeros.bin",
                "extra-version-file" => Prefix + "surprise.exe",
                "runtime-source" => "mods/AzureArchiveRecorder/runtime/evil.ps1",
                _ => null
            };
            if (special != null) bytes.Add(special, bad == "bomb" ? new byte[2 * 1024 * 1024] : bad == "empty-entry" ? Array.Empty<byte>() : new byte[] { 7 });
            if (bad == "missing-helper") bytes.Remove(Prefix + "更新内录MOD.exe");
            if (bad == "empty-helper") bytes[Prefix + "更新内录MOD.exe"] = Array.Empty<byte>();
            var receipt = new Receipt { Files = bytes.Select(pair => new Owned { Path = pair.Key, Sha256 = Hash(pair.Value) }).ToList() };
            if (bad == "manifest-version") receipt.Version = "1.2.6";
            if (bad == "manifest-root") receipt.Root = Root;
            if (bad == "entry-digest") receipt.Files[0].Sha256 = new string('0', 64);
            if (bad == "legacy-escape") receipt.LegacyFiles.Add(new Owned { Path = "profiles/user/file.txt", Sha256 = new string('0', 64) });
            if (bad is "legacy-helper" or "legacy-helper-old" or "legacy-helper-future") receipt.LegacyFiles.Add(new Owned
            {
                Path = "mods/AzureArchiveRecorder/" + (bad == "legacy-helper-old" ? "1.2.3" : bad == "legacy-helper-future" ? Version : "1.2.4") + "/更新内录MOD.exe",
                Sha256 = new string('0', 64)
            });
            if (bad == "generated-escape") receipt.GeneratedFiles.Add(new Owned { Path = "profiles/user/file.txt", Sha256 = new string('0', 64) });
            using (var stream = File.Create(Zip))
            using (var zip = new ZipArchive(stream, ZipArchiveMode.Create))
            {
                foreach (var pair in bytes)
                {
                    var entry = zip.CreateEntry(pair.Key, CompressionLevel.Optimal);
                    if (bad == "symbolic-link" && pair.Key == special) entry.ExternalAttributes = unchecked((int)0xA1FF0000);
                    using var target = entry.Open(); target.Write(pair.Value);
                }
                if (bad == "extra-entry") { using var extra = zip.CreateEntry("unexpected.exe").Open(); extra.WriteByte(8); }
                if (bad is "duplicate-entry" or "case-duplicate") { using var extra = zip.CreateEntry(bad == "duplicate-entry" ? bytes.First().Key : bytes.First().Key.ToUpperInvariant()).Open(); extra.WriteByte(8); }
                var json = JsonSerializer.Serialize(receipt);
                if (bad == "duplicate-property") json = "{\"Version\":\"1.2.6\"," + json.Substring(1);
                using var writer = new StreamWriter(zip.CreateEntry("payload-manifest.json").Open(), new UTF8Encoding(false)); writer.Write(json);
            }
            Info = new RecorderUpdateInfo
            {
                SchemaVersion = 1, Product = "AzureArchiveRecorder", Version = Version, MinimumVersion = "1.2.4", HostSha256 = Hash(File.ReadAllBytes(exe)),
                BaselinePath = "versions/1.2.5", DownloadUrl = "https://github.com/LittleLuoZiXi/AA-MOD/releases/download/recorder-v1.2.5/update.zip",
                Sha256 = Hash(File.ReadAllBytes(Zip)), Size = new FileInfo(Zip).Length
            };
            if (bad == "digest") Info = Info with { Sha256 = new string('0', 64) };
            if (bad == "size") Info = Info with { Size = Info.Size + 1 };
            if (bad == "host-digest") Info = Info with { HostSha256 = new string('0', 64) };
            if (bad == "target-junction")
            {
                var mods = Path.Combine(Root, "mods"); var elsewhere = Path.Combine(directory, "elsewhere"); Directory.CreateDirectory(elsewhere);
                // A junction requires no developer-mode/symlink privilege on Windows.
                var process = Process.Start(new ProcessStartInfo("cmd.exe", "/c mklink /J \"" + mods + "\" \"" + elsewhere + "\"") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true });
                process!.WaitForExit(); if (process.ExitCode != 0) throw new Exception("Fixture junction creation failed: " + process.StandardError.ReadToEnd()); process.Dispose();
            }
            Session = RecorderUpdateSession.CreateForTests(Root, bad == "host-path" ? Path.Combine(directory, "AzureArchive.exe") : exe,
                Hash(File.ReadAllBytes(exe)), Path.Combine(directory, "temp", "AzureArchiveRecorderApply"), start => { Start = start; return Helper; });
        }
        internal Task Status(string state, string error = "") => File.WriteAllTextAsync(Path.Combine(TaskRoot, "status.json"), JsonSerializer.Serialize(new { State = state, Error = error, Message = "", InstallationUncertain = false }));
        public void Dispose() => Session.Dispose();
    }
    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));
}
