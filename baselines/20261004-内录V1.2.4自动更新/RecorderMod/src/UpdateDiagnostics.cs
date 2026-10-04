using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AzureArchive.Automation;
using BepInEx;
using UnityEngine;
using Object = UnityEngine.Object;

namespace AzureArchive.Recorder;

// Explicit UI-only simulation. Production URLs and install/restart operations have no command-line override.
internal static class UpdateDiagnostics
{
    internal static readonly bool Enabled = Environment.GetCommandLineArgs().Contains("--aa-recorder-update-probe");
    private static bool trusted, rejected, installIntercepted, restartIntercepted;
    private static volatile bool allowDownloadCompletion;
    private static int stage, downloadCount, requestCount;
    private static float readyAt, installReadyAt;
    private static long pausedBytes;
    private static string evidence = "", project = "", downloadRoot = "", statusBeforeFailureProbe = "";
    private static readonly List<string> checks = new();
    private static byte[] payload => Fixture.Payload;
    private static RecorderUpdateDownload? lastDownload;
    private static RecorderUpdateCheckResult? nextCheck;
    private static RecorderUpdateInfo info => Fixture.Info;
    private static class Fixture
    {
    internal static readonly byte[] Payload = Enumerable.Range(0, 1024 * 1024).Select(value => (byte)(value % 251)).ToArray();
    internal static readonly RecorderUpdateInfo Info = new()
    {
        SchemaVersion = 1, Product = "AzureArchiveRecorder", Version = "1.2.5", MinimumVersion = "1.2.3",
        HostSha256 = RecorderUpdateSession.SupportedHostSha256,
        BaselinePath = "mods/AzureArchiveRecorder/1.2.4/manifest.json",
        DownloadUrl = "https://github.com/LittleLuoZiXi/AA-MOD/releases/download/recorder-v1.2.5/ui-probe-synthetic.zip",
        Sha256 = Convert.ToHexString(SHA256.HashData(Payload)), Size = Payload.Length
    };

    }

    private static string Argument(string name)
    {
        var args = Environment.GetCommandLineArgs();
        var index = Array.IndexOf(args, name);
        if (index < 0 || index + 1 >= args.Length) throw new InvalidOperationException("Missing isolated update probe argument: " + name);
        return args[index + 1];
    }
    private static void NoLinks(string path)
    {
        for (string? cursor = Path.GetFullPath(path); !string.IsNullOrEmpty(cursor); cursor = Path.GetDirectoryName(cursor))
            if ((File.Exists(cursor) || Directory.Exists(cursor)) && (File.GetAttributes(cursor) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidOperationException("Linked probe path is forbidden: " + cursor);
    }
    private static string Within(string root, string path)
    {
        var full = Path.GetFullPath(path);
        if (!full.StartsWith(Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Probe path escapes the isolated host: " + full);
        NoLinks(full); return full;
    }
    private static bool Reject(Exception error)
    {
        rejected = true; stage = 99;
        RecorderPlugin.Instance.Log.LogError("Update UI probe refused before diagnostic writes: " + error.Message);
        Application.Quit(1); return false;
    }
    private static bool EstablishTrust()
    {
        if (rejected) return false;
        if (trusted) return true;
        try
        {
            var root = Path.GetFullPath(Paths.GameRootPath).TrimEnd(Path.DirectorySeparatorChar);
            NoLinks(root);
            if (Application.companyName != "aa123test" || Application.productName != "AARecTest123" ||
                !Application.persistentDataPath.Replace('\\', '/').EndsWith("/aa123test/AARecTest123", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Native Unity identity is not the dedicated test application.");
            var markerPath = Within(root, Path.Combine(root, "RecorderChoiceTestRoot.json"));
            using var marker = JsonDocument.Parse(File.ReadAllText(markerPath));
            var item = marker.RootElement;
            if (item.GetProperty("schema_version").GetInt32() != 1 || item.GetProperty("kind").GetString() != "RecorderChoiceSmokeIsolatedCopy" ||
                !string.Equals(Path.GetFullPath(item.GetProperty("root").GetString()!).TrimEnd(Path.DirectorySeparatorChar), root, StringComparison.OrdinalIgnoreCase) ||
                item.GetProperty("native_company").GetString() != "aa123test" || item.GetProperty("native_product").GetString() != "AARecTest123" ||
                string.Equals(Path.GetFullPath(item.GetProperty("source_game").GetString()!).TrimEnd(Path.DirectorySeparatorChar), root, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("The isolated-host marker is invalid.");
            var activeProfile = Within(root, Path.Combine(root, "ActiveProfile.txt"));
            if (File.ReadAllText(activeProfile).Trim() != "Update124") throw new InvalidOperationException("The dedicated Update124 profile is not active.");
            evidence = Within(Path.Combine(root, "smoke-runs"), Argument("--aa-recorder-smoke-evidence"));
            project = Within(evidence, Argument("--aa-recorder-update-project"));
            if (!File.Exists(project)) throw new InvalidOperationException("The synthetic test project is missing.");
            downloadRoot = Within(evidence, Path.Combine(evidence, "simulated-downloads"));
            var settings = Object.FindObjectOfType<UserSettings>();
            if (settings == null) return false;
            if (!settings.settingFilePath.Replace('\\', '/').Contains("/aa123test/AARecTest123/", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("AA settings are not using the isolated native data directory.");
            Within(root, settings.WorkspacePath);
            trusted = true;
            Directory.CreateDirectory(evidence);
            Check(true, "Strict marker, native identity, persistent data, Update124 profile, workspace and evidence-path guards passed before diagnostic writes");
            return true;
        }
        catch (Exception error) { return Reject(error); }
    }
    internal static void RequireTrusted()
    {
        if (!Enabled || !EstablishTrust()) throw new InvalidOperationException("This update probe requires the verified isolated AA host.");
    }

    // These are the only five call sites needed by the ordinary UI.
    internal static bool TryCreateCheck(out Task<RecorderUpdateCheckResult> result)
    {
        result = null!;
        if (!Enabled) return false;
        if (!EstablishTrust())
        {
            result = Task.FromResult(new RecorderUpdateCheckResult(RecorderUpdateCheckState.Cancelled, null, "隔离更新界面测试未通过宿主校验。"));
            return true; // Never fall back to real HTTP in a rejected diagnostic launch.
        }
        result = Task.FromResult(nextCheck ?? new RecorderUpdateCheckResult(RecorderUpdateCheckState.NoUpdate, null, ""));
        nextCheck = null;
        return true;
    }
    internal static RecorderUpdateDownload CreateDownload(RecorderUpdateInfo update)
    {
        if (!Enabled) return new RecorderUpdateDownload(update);
        RequireTrusted();
        if (update != info) throw new InvalidOperationException("Only the fixed in-memory diagnostic fixture is accepted.");
        allowDownloadCompletion = false;
        lastDownload = new RecorderUpdateDownload(update, new MemoryHandler(), downloadRoot);
        downloadCount++;
        return lastDownload;
    }
    internal static bool TryBeginInstall(NativeRecorderUi ui)
    {
        if (!Enabled) return false;
        RequireTrusted();
        Check(lastDownload?.Snapshot.State == RecorderDownloadState.Completed, "Real downloader verified all synthetic bytes before simulated installation");
        installIntercepted = true; installReadyAt = Time.realtimeSinceStartup + .6f;
        ui.BeginSimulatedUpdateInstall();
        return true;
    }
    internal static bool TryConfirmRestart(NativeRecorderUi ui)
    {
        if (!Enabled) return false;
        RequireTrusted();
        if (ui.UpdateState == NativeRecorderUi.UpdatePanelState.Completed)
        {
            restartIntercepted = true;
            Check(true, "Native OK callback reached the intercepted restart request; no helper, restart token or replacement AA process was created");
        }
        return true;
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
        checks.Add(message); RecorderPlugin.Instance.Log.LogInfo("Update UI probe: " + message);
    }
    private static UI.MXButton Button(string name) => Object.FindObjectsOfType<UI.MXButton>(true).First(button => button.name == name && button.gameObject.activeInHierarchy);
    private static void Click(string name) => UiDiagnostics.Click(name);
    private static bool VisibleText(string text) => Object.FindObjectsOfType<UILabel>().Any(label => label.gameObject.activeInHierarchy && label.isVisible && label.text == text);
    private static string[] ActiveUpdateButtons() => Object.FindObjectsOfType<UI.MXButton>(true)
        .Where(button => button.name.StartsWith("AARecorder_Update", StringComparison.Ordinal) && button.gameObject.activeInHierarchy)
        .Select(button => button.name).OrderBy(name => name).ToArray();
    private static void Capture(string name)
    {
        UiDiagnostics.SaveFrame(Path.Combine(evidence, name + ".ppm"));
        File.WriteAllText(Path.Combine(evidence, name + ".json"), JsonSerializer.Serialize(new
        {
            simulatedCheck = true, inMemoryDownload = true, simulatedInstallAndRestart = true,
            state = RecorderBehaviour.Instance!.Ui!.UpdateState.ToString(),
            buttons = Object.FindObjectsOfType<UI.MXButton>(true).Where(button => button.gameObject.activeInHierarchy && button.name.StartsWith("AARecorder_Update", StringComparison.Ordinal))
                .Select(button => new { button.name, label = button.label.text, button.disabled }),
            labels = Object.FindObjectsOfType<UILabel>().Where(label => label.gameObject.activeInHierarchy && UiDiagnostics.PathOf(label.transform).Contains("/UpdateConfirmation/"))
                .Select(label => new { label.text, label.isVisible, label.fontSize, label.width, label.height })
        }));
    }
    private static void CheckRecordingRestored(string context)
    {
        var ui = RecorderBehaviour.Instance!.Ui!;
        Check(ui.IsVisible && !ui.UpdateModalVisible && !ui.UpdateBlocksRecording, context + ": update modal and recording block released");
        Check(!Button("AARecorder_Start").disabled && !Button("AARecorder_Fps").disabled, context + ": recording and FPS controls enabled");
        UiDiagnostics.Click("AARecorder_Start", false); // Native raycast; no recording is started by this UI-only probe.
        var config = RecorderPlugin.Instance; var prior = config.Fps.Value;
        Click("AARecorder_Fps");
        Check(config.Fps.Value != prior, context + ": native ordinary-control callback works again");
        for (var attempt = 0; config.Fps.Value != prior && attempt < 6; attempt++) Click("AARecorder_Fps");
        Check(config.Fps.Value == prior, context + ": original test-profile FPS restored");
    }
    private static void OpenOffer(NativeRecorderUi ui)
    {
        if (ui.IsVisible) Click("AARecorder_Close");
        nextCheck = new(RecorderUpdateCheckState.Available, info, "");
        ui.ToggleSettings();
    }
    private static void CheckInputLock(NativeRecorderUi ui)
    {
        var owner = RecorderBehaviour.Instance!; var config = RecorderPlugin.Instance;
        Check(ui.UpdateModalVisible && ui.UpdateBlocksRecording && Button("AARecorder_Start").disabled, "Offer blocks the underlying recording entry");
        var fps = config.Fps.Value;
        UIEventListener.Get(Button("AARecorder_Fps").gameObject).OnClick();
        UIEventListener.Get(Button("AARecorder_Start").gameObject).OnClick();
        owner.StartFromOptions(false, Path.Combine(evidence, "must-not-record"));
        Check(config.Fps.Value == fps && !owner.Busy, "Underlying FPS, start-button callbacks and direct recording request are blocked by the update modal");
        var background = Button("AARecorder_Start");
        var camera = UICamera.FindCameraForLayer(background.gameObject.layer);
        var point = camera.cachedCamera.WorldToScreenPoint(background.transform.position);
        var hit = UICamera.Raycast(point);
        var target = UICamera.lastHit.collider?.GetComponentInParent<UI.MXButton>();
        Check(hit && (target == null || target.Pointer != background.Pointer), "Native update shield intercepts pointer hit testing over the recording button");
    }

    internal static void Update()
    {
        if (!Enabled || stage == 99) return;
        if (!EstablishTrust()) return;
        try
        {
            var owner = RecorderBehaviour.Instance;
            if (owner?.Ui == null || (stage == 0 && !DiagnosticHostReady.Check(Object.FindObjectOfType<ScenarioResourceManager>()))) return;
            var ui = owner.Ui;
            if (installIntercepted && ui.UpdateState == NativeRecorderUi.UpdatePanelState.Installing && Time.realtimeSinceStartup >= installReadyAt)
            {
                ui.CompleteSimulatedUpdateInstall();
                // NGUI must publish its newly activated OK widget before native raycast/click verification.
                readyAt = Time.realtimeSinceStartup + .5f;
                return;
            }
            if (Time.realtimeSinceStartup < readyAt) return;
            switch (stage)
            {
                case 0:
                    if (!Object.FindObjectsOfType<Transform>(true).Any(transform => transform.name == "UIPopup_Settings")) return;
                    // Build the persistent panel while the native Catalog template exists, before entering Studio.
                    if (ui.ScreenCamera == null) { ui.ToggleSettings(); ui.ToggleSettings(); }
                    Check(ui.ScreenCamera != null, "Persistent recorder UI was initialized from the native Catalog settings template before opening Studio");
                    AuthoringWorkbench.OpenProject(project); stage = 1; readyAt = Time.realtimeSinceStartup + 3; return;
                case 1:
                    var current = AuthoringEditorSession.Current;
                    if (current == null || !string.Equals(Path.GetFullPath(current.SourcePath), project, StringComparison.OrdinalIgnoreCase)) return;
                    if (ui.ScreenCamera == null) return;
                    Check(!current.Dirty && !current.SaveInProgress, "Synthetic project opened cleanly in the actual AA editor");
                    AuthoringMcpPanel.HideCurrent();
                    statusBeforeFailureProbe = owner.Status;
                    owner.SetStatus("保存设置失败：诊断保留信息");
                    nextCheck = new(RecorderUpdateCheckState.Failed, null, "诊断模拟：更新检查网络不可用。");
                    ui.ToggleSettings(); stage = 14; readyAt = Time.realtimeSinceStartup + .5f; return;
                case 14:
                    Check(owner.Status == "保存设置失败：诊断保留信息", "A failed background update check preserves the existing save-settings error message");
                    Check(!ui.UpdateModalVisible && !ui.UpdateBlocksRecording, "Failed background update check leaves ordinary recording available");
                    owner.SetStatus(statusBeforeFailureProbe);
                    OpenOffer(ui); stage = 2; readyAt = Time.realtimeSinceStartup + 1; return;
                case 2:
                    if (ui.TutorialVisible) Click("AARecorder_TutorialSkip");
                    if (ui.UpdateState != NativeRecorderUi.UpdatePanelState.Offer) return;
                    Check(VisibleText(NativeRecorderUi.UpdateOfferPrompt), "Exact Chinese new-version prompt is visibly rendered");
                    Check(ActiveUpdateButtons().SequenceEqual(new[] { "AARecorder_UpdateNo", "AARecorder_UpdateYes" }), "Offer contains exactly update and decline buttons");
                    CheckInputLock(ui); Capture("update-offer"); Click("AARecorder_UpdateNo");
                    stage = 3; readyAt = Time.realtimeSinceStartup + .5f; return;
                case 3:
                    CheckRecordingRestored("Decline update"); Capture("update-declined");
                    OpenOffer(ui); stage = 4; readyAt = Time.realtimeSinceStartup + .5f; return;
                case 4:
                    if (ui.UpdateState != NativeRecorderUi.UpdatePanelState.Offer) return;
                    Click("AARecorder_UpdateYes"); stage = 5; readyAt = Time.realtimeSinceStartup + .5f; return;
                case 5:
                    if (lastDownload == null || lastDownload.Snapshot.Bytes == 0) return;
                    Check(ui.UpdateState == NativeRecorderUi.UpdatePanelState.Download && ui.UpdateBlocksRecording, "Download progress holds recording input lock");
                    Check(ActiveUpdateButtons().SequenceEqual(new[] { "AARecorder_UpdateCancel", "AARecorder_UpdatePause" }), "Downloading has exactly pause and terminate actions");
                    Check(VisibleText("正在下载新版本…"), "Chinese downloading status is visible");
                    Capture("update-downloading"); Click("AARecorder_UpdatePause"); stage = 6; readyAt = Time.realtimeSinceStartup + .4f; return;
                case 6:
                    if (lastDownload!.Snapshot.State != RecorderDownloadState.Paused) return;
                    pausedBytes = lastDownload.Snapshot.Bytes;
                    Check(pausedBytes > 0 && pausedBytes < lastDownload.Snapshot.Total, "Pause preserves a genuine partial download");
                    Check(Button("AARecorder_UpdatePause").label.text == "继续" && VisibleText("下载已暂停"), "Paused UI exposes Chinese resume action");
                    Capture("update-paused"); stage = 7; readyAt = Time.realtimeSinceStartup + .4f; return;
                case 7:
                    Check(lastDownload!.Snapshot.Bytes == pausedBytes, "Paused downloader remains stable across rendered frames");
                    Click("AARecorder_UpdatePause"); stage = 8; readyAt = Time.realtimeSinceStartup + .5f; return;
                case 8:
                    if (lastDownload!.Snapshot.Bytes <= pausedBytes) return;
                    Check(lastDownload.Snapshot.State == RecorderDownloadState.Downloading && Button("AARecorder_UpdatePause").label.text == "暂停", "Resume uses the real downloader and advances progress");
                    Capture("update-resumed"); Click("AARecorder_UpdateCancel"); stage = 9; readyAt = Time.realtimeSinceStartup + .5f; return;
                case 9:
                    if (ui.UpdateState != NativeRecorderUi.UpdatePanelState.None) return;
                    Check(lastDownload!.Snapshot.State == RecorderDownloadState.Cancelled && !Directory.EnumerateFiles(downloadRoot, "*", SearchOption.AllDirectories).Any(), "Terminate cancels the real task and removes only its temporary download files");
                    CheckRecordingRestored("Terminate update"); Capture("update-terminated");
                    OpenOffer(ui); stage = 10; readyAt = Time.realtimeSinceStartup + .5f; return;
                case 10:
                    if (ui.UpdateState != NativeRecorderUi.UpdatePanelState.Offer) return;
                    Click("AARecorder_UpdateYes"); stage = 11; readyAt = Time.realtimeSinceStartup + .5f; return;
                case 11:
                    if (lastDownload!.Snapshot.Bytes == 0) return;
                    allowDownloadCompletion = true; stage = 12; return;
                case 12:
                    if (ui.UpdateState != NativeRecorderUi.UpdatePanelState.Completed) return;
                    Check(installIntercepted && VisibleText(NativeRecorderUi.UpdateCompletedPrompt), "Exact Chinese completion prompt is visible after simulated installation");
                    Check(ui.UpdateBlocksRecording && ActiveUpdateButtons().SequenceEqual(new[] { "AARecorder_UpdateOK" }), "Completion keeps recording blocked and exposes exactly one OK button");
                    Check(Button("AARecorder_UpdateOK").label.text == "OK", "The sole completion action is labeled OK");
                    Check(!Object.FindObjectsOfType<UI.MXButton>(true).Any(button => button.name == "AARecorder_Close" && button.gameObject.activeInHierarchy), "Completion cannot be dismissed through the underlying close button");
                    Capture("update-completed"); Click("AARecorder_UpdateOK"); stage = 13; readyAt = Time.realtimeSinceStartup + .3f; return;
                case 13:
                    Check(restartIntercepted && downloadCount == 2 && requestCount >= 3 && !owner.Busy, "Native flow completed decline, pause/resume/terminate and success/OK without recording or launching an installer");
                    File.WriteAllText(Path.Combine(evidence, "update-ui-result.json"), JsonSerializer.Serialize(new
                    {
                        passed = true, checks = checks.ToArray(), nativeUiCallbacks = true, nativeRaycasts = true,
                        simulatedUpdateCheck = true, downloadTransport = "real RecorderUpdateDownload with fixed in-memory handler",
                        syntheticPayloadNotInstallable = true, simulatedInstall = installIntercepted, interceptedRestart = restartIntercepted,
                        realNetworkAccess = false, realInstallation = false, realRestart = false, recordingExecuted = false,
                        dlssDownloadOrExecution = false, desktopInputUsed = false, downloadCount, requestCount,
                        evidence, project, nativeIdentity = "aa123test/AARecTest123", profile = "Update124"
                    }));
                    stage = 99; Application.Quit(0); return;
            }
        }
        catch (Exception error)
        {
            stage = 99; lastDownload?.Dispose();
            if (trusted) File.WriteAllText(Path.Combine(evidence, "update-ui-error.txt"), error.ToString());
            RecorderPlugin.Instance.Log.LogError(error); Application.Quit(1);
        }
    }

    private sealed class MemoryHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Interlocked.Increment(ref requestCount);
            if (request.RequestUri?.AbsoluteUri != info.DownloadUrl) throw new InvalidOperationException("Unexpected diagnostic URL.");
            var from = request.Headers.Range?.Ranges.Single().From ?? 0;
            if (from > 0 && request.Headers.IfRange?.EntityTag?.ToString() != "\"update-ui-fixture\"") throw new InvalidOperationException("Expected strong If-Range validator.");
            var response = new HttpResponseMessage(from > 0 ? HttpStatusCode.PartialContent : HttpStatusCode.OK)
            { Content = new StreamContent(new FixtureStream(checked((int)from))) };
            response.Content.Headers.ContentLength = payload.Length - from;
            response.Headers.ETag = EntityTagHeaderValue.Parse("\"update-ui-fixture\"");
            if (from > 0) response.Content.Headers.ContentRange = new ContentRangeHeaderValue(from, payload.Length - 1, payload.Length);
            return Task.FromResult(response);
        }
    }
    private sealed class FixtureStream : Stream
    {
        private int position, sent;
        internal FixtureStream(int from) { position = from; }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            while (sent >= 256 * 1024 && !allowDownloadCompletion) await Task.Delay(50, cancellationToken).ConfigureAwait(false);
            await Task.Delay(80, cancellationToken).ConfigureAwait(false);
            var count = Math.Min(buffer.Length, payload.Length - position);
            payload.AsMemory(position, count).CopyTo(buffer); position += count; sent += count;
            return count;
        }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}

internal sealed partial class NativeRecorderUi
{
    // These two state transitions are reachable only from the strict isolated diagnostic hooks.
    internal void BeginSimulatedUpdateInstall()
    {
        UpdateDiagnostics.RequireTrusted();
        updateDownload?.Dispose(); updateDownload = null;
        updatePanelState = UpdatePanelState.Installing; RefreshUpdatePanel(); UpdateVisibility();
    }
    internal void CompleteSimulatedUpdateInstall()
    {
        UpdateDiagnostics.RequireTrusted();
        updatePanelState = UpdatePanelState.Completed; RefreshUpdatePanel(); UpdateVisibility();
    }
}





