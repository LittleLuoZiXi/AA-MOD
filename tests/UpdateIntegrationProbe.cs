using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AzureArchive.Automation;
using BepInEx;
using HarmonyLib;
using UnityEngine;
using Object = UnityEngine.Object;

namespace AzureArchive.RevisionCompare;

// Compiled only with -IncludeProbe and gated to a marked isolated host.
// Every controller receives a fake check, fake update flow and fake quit action.
internal static class UpdateIntegrationProbe
{
    private static bool enabled, done;
    private static string root = "", evidence = "";
    private static string? blocked;
    private static float started, next;
    private static int stage, checkCalls, flowCreations, quitCalls;
    private static readonly List<string> checks = new();
    private static readonly List<FakeFlow> flows = new();
    private static readonly TaskCompletionSource<RevisionUpdateCheckResult> firstResult = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static FakeFlow? queuedFlow, currentFlow;
    private static readonly string InitialOfferedVersion = FutureVersion(1);
    private static readonly string FlowOfferedVersion = FutureVersion(2);

    private static string FutureVersion(int minorOffset)
    {
        var current = System.Version.Parse(RevisionComparePlugin.Version.Split('-')[0].Split('+')[0]);
        return current.Major + "." + (current.Minor + minorOffset) + ".0";
    }

    internal static void Install(Harmony harmony)
    {
        enabled = Environment.GetCommandLineArgs().Contains("--revision-update-probe");
        if (!enabled) return;
        root = Path.GetFullPath(Paths.GameRootPath);
        using var marker = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "RevisionTestHost.json")));
        if (marker.RootElement.GetProperty("kind").GetString() != "RevisionCompareIsolatedHost" ||
            !string.Equals(root.TrimEnd('\\'), marker.RootElement.GetProperty("root").GetString(), StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Invalid isolated update-test host.");
        evidence = Path.Combine(root, "evidence");
        Directory.CreateDirectory(evidence);
        harmony.Patch(AccessTools.PropertyGetter(typeof(Application), nameof(Application.persistentDataPath)),
            prefix: new HarmonyMethod(typeof(UpdateIntegrationProbe), nameof(PersistentPath)));
        Application.runInBackground = true;
    }

    private static bool PersistentPath(ref string __result)
    {
        if (!enabled) return true;
        __result = Path.Combine(root, "UserData"); return false;
    }

    internal static RevisionUpdateController? CreateController()
    {
        if (enabled)
            return new RevisionUpdateController(Log,
                token => { Interlocked.Increment(ref checkCalls); return firstResult.Task; },
                CreateFlow, () => blocked, () => quitCalls++);
        if (Environment.GetCommandLineArgs().Contains("--revision-compare-probe"))
            return Create(new(RevisionUpdateCheckState.NoUpdate, null, ""));
        return null;
    }

    private static RevisionUpdateController Create(RevisionUpdateCheckResult result)
        => new(Log, token => Task.FromResult(result), CreateFlow, () => blocked, () => quitCalls++);

    private static IRevisionUpdateFlow CreateFlow()
    {
        var result = queuedFlow ?? throw new InvalidOperationException("The probe did not authorize a fake download flow.");
        queuedFlow = null; currentFlow = result; flowCreations++; flows.Add(result); return result;
    }

    private static void QueueFlow() => queuedFlow = new FakeFlow();
    private static void Log(string message) => RevisionComparePlugin.Instance.Log.LogInfo(message);
    private static RevisionUpdateCheckResult Available() => new(RevisionUpdateCheckState.Available, new(FlowOfferedVersion), "");
    private static void ReplaceController(RevisionCompareBehaviour owner, RevisionUpdateCheckResult result)
    { owner.Updater?.Dispose(); owner.Updater = Create(result); }

    internal static void Tick(RevisionCompareBehaviour owner)
    {
        if (!enabled || done) return;
        try
        {
            if (started == 0) started = Time.realtimeSinceStartup;
            if (Time.realtimeSinceStartup - started > 120) throw new TimeoutException("Update probe timed out at stage " + stage);
            if (Time.realtimeSinceStartup < next) return;
            switch (stage)
            {
                case 0:
                    var settings = Object.FindObjectOfType<UserSettings>();
                    if (settings == null || !settings.isReady || AuthoringWorkbench.Instance == null) return;
                    Check(Application.companyName == "aarevtest" && Application.productName == "AARevCompare", "Dedicated test application identity");
                    Check(settings.settingFilePath.StartsWith(root, StringComparison.OrdinalIgnoreCase), "Settings isolated in test host");
                    Check(settings.WorkspacePath.StartsWith(root, StringComparison.OrdinalIgnoreCase), "Workspace isolated in test host");
                    Check(AuthoringEditorSession.Current == null, "AA startup remains at home without opening a project");
                    Check(owner.Updater != null && !owner.Updater.IsVisible && !owner.Updater.CheckCompleted, "Home screen starts while the injected update check is pending");
                    Check(checkCalls == 1, "Startup starts one background check");
                    firstResult.SetResult(new(RevisionUpdateCheckState.Available, new(InitialOfferedVersion), ""));
                    Go(1, 2); break;
                case 1:
                    if (owner.Updater?.IsVisible != true) return;
                    Check(owner.Updater.CheckCompleted, "Available update result is consumed on the main thread");
                    Check(GameObject.Find("AARevisionCompare_UpdateNotice") != null, "Native update popup appears on the home screen");
                    Check(Labels().Contains(RevisionComparePlugin.Version) && Labels().Contains(InitialOfferedVersion), "Popup displays local and offered versions");
                    Check(Button("AARevisionCompare_ViewUpdate").label.text == "更新版本" && Button("AARevisionCompare_DismissUpdate").label.text == "暂不更新", "Initial actions are Update version and Not now");
                    Check(Labels().Contains("下载完成后将重启 AA 应用更新。") && !Labels().Contains("GitHub"), "Prompt describes direct download and restart instead of browser navigation");
                    Check(AuthoringEditorSession.Current == null && flowCreations == 0 && quitCalls == 0, "Showing the offer starts no download, project or restart");
                    Go(101, .6f); break;
                case 101:
                    SaveFrame("update-prompt.ppm");
                    Click("AARevisionCompare_DismissUpdate");
                    Check(!owner.Updater!.IsVisible, "Not now closes the popup");
                    Check(flowCreations == 0 && quitCalls == 0, "Not now performs no download or quit");
                    Go(2, 2); break;
                case 2:
                    Check(!owner.Updater!.IsVisible && checkCalls == 1, "Dismissed offer stays closed without another startup check");
                    ReplaceController(owner, new(RevisionUpdateCheckState.NoUpdate, new(RevisionComparePlugin.Version), ""));
                    Go(3, 1.5f); break;
                case 3:
                    Check(owner.Updater!.CheckCompleted && !owner.Updater.IsVisible, "Same-version result stays silent");
                    ReplaceController(owner, new(RevisionUpdateCheckState.NoUpdate, new("0.9.0"), ""));
                    Go(4, 1.5f); break;
                case 4:
                    Check(owner.Updater!.CheckCompleted && !owner.Updater.IsVisible, "Lower-version result stays silent");
                    ReplaceController(owner, new(RevisionUpdateCheckState.Failed, null, "离线测试"));
                    Go(5, 1.5f); break;
                case 5:
                    Check(owner.Updater!.CheckCompleted && !owner.Updater.IsVisible, "Offline check failure stays silent");
                    ReplaceController(owner, new(RevisionUpdateCheckState.Cancelled, null, "取消检查测试"));
                    Go(6, 1.5f); break;
                case 6:
                    Check(owner.Updater!.CheckCompleted && !owner.Updater.IsVisible, "Cancelled update check stays silent");
                    blocked = "当前工程有未保存的修改（模拟），请先保存工程。";
                    ReplaceController(owner, Available());
                    Go(7, 2); break;
                case 7:
                    if (!owner.Updater!.IsVisible) return;
                    Click("AARevisionCompare_ViewUpdate");
                    Check(owner.Updater.Failure.Contains("未保存"), "Unsaved-work guard explains why update cannot start");
                    Check(flowCreations == 0 && quitCalls == 0, "Unsaved-work guard refuses download creation and quit");
                    Check(!Button("AARevisionCompare_ViewUpdate").disabled && Button("AARevisionCompare_ViewUpdate").label.text == "重试更新", "Blocked update remains retryable");
                    blocked = null; QueueFlow();
                    Click("AARevisionCompare_ViewUpdate");
                    Check(flowCreations == 1 && currentFlow!.PrepareCalls == 1 && currentFlow.Version == FlowOfferedVersion, "Explicit update click starts one fake preparation for the offered version");
                    currentFlow!.SetDownloading(50);
                    Go(8, .5f); break;
                case 8:
                    Check(owner.Updater!.IsVisible && Labels().Contains("50%"), "Native popup displays 50 percent download progress");
                    var bar = GameObject.Find("AARevisionCompare_UpdateProgress")?.GetComponent<UITexture>();
                    var track = GameObject.Find("AARevisionCompare_UpdateProgressTrack")?.GetComponent<UITexture>();
                    Check(bar != null && track != null && Math.Abs(bar.width * 2 - track.width) <= 2, "Native progress bar has the expected halfway width");
                    Check(Button("AARevisionCompare_ViewUpdate").disabled && !Button("AARevisionCompare_DismissUpdate").disabled && Button("AARevisionCompare_DismissUpdate").label.text == "取消", "Download disables update while keeping cancellation available");
                    SaveFrame("update-downloading.ppm");
                    InvokeEvenIfDisabled("AARevisionCompare_ViewUpdate");
                    Check(flowCreations == 1 && currentFlow!.PrepareCalls == 1, "Disabled update action cannot start a duplicate download");
                    currentFlow!.SetVerifying();
                    Go(9, .5f); break;
                case 9:
                    Check(Labels().Contains("校验") && Button("AARevisionCompare_ViewUpdate").disabled, "Verification has its own native status and keeps update disabled");
                    Click("AARevisionCompare_DismissUpdate");
                    Check(!owner.Updater!.IsVisible && quitCalls == 0, "Cancelling preparation closes the popup without quitting");
                    Check(currentFlow!.CancelCalls > 0 && currentFlow.PreparationCompleted && currentFlow.AuthorizationCompleted, "Cancellation completes every pending fake task");
                    Go(10, 1); break;
                case 10:
                    Check(!owner.Updater!.IsVisible && currentFlow!.DisposeCalls > 0, "Cancelled preparation remains dismissed and is disposed");
                    ReplaceController(owner, Available());
                    Go(11, 2); break;
                case 11:
                    if (!owner.Updater!.IsVisible) return;
                    QueueFlow(); Click("AARevisionCompare_ViewUpdate");
                    currentFlow!.FailPreparation("模拟下载文件校验失败");
                    Go(12, .5f); break;
                case 12:
                    Check(owner.Updater!.Failure.Contains("校验失败") && Labels().Contains("校验失败"), "Preparation failure is explained in the native popup");
                    Check(quitCalls == 0 && !Button("AARevisionCompare_ViewUpdate").disabled && Button("AARevisionCompare_ViewUpdate").label.text == "重试更新", "Preparation failure offers retry without quitting");
                    Check(!Button("AARevisionCompare_DismissUpdate").disabled && Button("AARevisionCompare_DismissUpdate").label.text == "关闭", "Failed preparation can be closed");
                    QueueFlow(); Click("AARevisionCompare_ViewUpdate");
                    Check(flowCreations == 3 && currentFlow!.PrepareCalls == 1, "Retry creates one fresh preparation flow");
                    currentFlow!.BecomeReady();
                    Go(13, .5f); break;
                case 13:
                    Check(currentFlow!.AuthorizeCalls == 1 && !currentFlow.AuthorizationCompleted && quitCalls == 0, "Ready files request helper authorization but do not quit before acknowledgment");
                    Check(Button("AARevisionCompare_ViewUpdate").disabled && Button("AARevisionCompare_DismissUpdate").disabled, "Authorization wait disables both native actions");
                    InvokeEvenIfDisabled("AARevisionCompare_DismissUpdate");
                    owner.Updater!.Dismiss();
                    Check(owner.Updater.IsVisible && currentFlow.CancelCalls == 0 && !currentFlow.AuthorizationCompleted, "Authorization wait rejects button and controller dismissal");
                    currentFlow.CompleteAuthorization(false, "模拟安装助手未确认授权");
                    Go(14, .5f); break;
                case 14:
                    Check(owner.Updater!.Failure.Contains("未确认授权") && quitCalls == 0, "Refused helper acknowledgment reports failure without quitting");
                    Check(!Button("AARevisionCompare_ViewUpdate").disabled && Button("AARevisionCompare_ViewUpdate").label.text == "重试更新", "Rejected authorization remains retryable");
                    QueueFlow(); Click("AARevisionCompare_ViewUpdate");
                    currentFlow!.BecomeReady();
                    Go(15, .5f); break;
                case 15:
                    Check(currentFlow!.AuthorizeCalls == 1 && quitCalls == 0, "Retry again waits for explicit helper acknowledgment");
                    blocked = "下载后出现未保存的修改（模拟），已停止重启。";
                    currentFlow.CompleteAuthorization(true);
                    Go(16, .5f); break;
                case 16:
                    Check(owner.Updater!.Failure.Contains("未保存") && quitCalls == 0, "Work-state recheck after acknowledgment prevents an unsafe quit");
                    Check(currentFlow!.CancelCalls > 0, "Failed final safety check retracts the fake helper authorization");
                    blocked = null; QueueFlow(); Click("AARevisionCompare_ViewUpdate");
                    currentFlow!.BecomeReady();
                    Go(17, .5f); break;
                case 17:
                    Check(currentFlow!.AuthorizeCalls == 1 && !currentFlow.AuthorizationCompleted && quitCalls == 0, "Final attempt still cannot quit until helper acknowledgment arrives");
                    Check(Button("AARevisionCompare_ViewUpdate").disabled && Button("AARevisionCompare_DismissUpdate").disabled, "Final handoff locks both native controls");
                    currentFlow.CompleteAuthorization(true);
                    Go(18, .5f); break;
                case 18:
                    Check(quitCalls == 1 && currentFlow!.CancelCalls == 0, "Successful acknowledged handoff invokes the injected quit exactly once");
                    Check(owner.Updater!.IsVisible && Labels().Contains("正在重启 AA"), "Native popup displays the restart handoff status");
                    Check(Button("AARevisionCompare_ViewUpdate").disabled && Button("AARevisionCompare_DismissUpdate").disabled, "Authorized handoff keeps all update actions disabled");
                    Go(19, 1); break;
                case 19:
                    Check(quitCalls == 1 && flowCreations == 5, "Later frames do not duplicate quit or update preparation");
                    Check(AuthoringEditorSession.Current == null, "Every simulated update leaves personal projects unopened");
                    owner.Updater!.Dispose(); owner.Updater = null;
                    Go(20, .5f); break;
                case 20:
                    Check(currentFlow!.DisposeCalls > 0 && currentFlow.CancelCalls == 0, "Successful handoff disposal preserves authorization");
                    Check(!Object.FindObjectsOfType<UI.MXButton>().Any(x => x.name == "AARevisionCompare_ViewUpdate" && x.gameObject.activeInHierarchy), "Disposal removes the native update UI");
                    Check(flows.All(x => x.PreparationCompleted && x.AuthorizationCompleted), "No fake preparation or authorization tasks remain pending");
                    Finish(true, null); break;
            }
        }
        catch (Exception error) { Finish(false, error.ToString()); }
    }

    private static void Check(bool success, string text)
    {
        if (!success) throw new InvalidOperationException(text);
        checks.Add(text);
        File.AppendAllText(Path.Combine(evidence, "progress.txt"), "PASS: " + text + Environment.NewLine);
    }
    private static void Go(int value, float delay) { stage = value; next = Time.realtimeSinceStartup + delay; }
    private static UI.MXButton Button(string name) =>
        Object.FindObjectsOfType<UI.MXButton>().Single(x => x.name == name && x.gameObject.activeInHierarchy);
    private static string Labels() => string.Join("\n", GameObject.Find("AARevisionCompare_UpdateNotice").GetComponentsInChildren<UILabel>(true)
        .Where(x => x.enabled && x.gameObject.activeInHierarchy).Select(x => x.text));
    private static void Click(string name)
    {
        var button = Button(name);
        Check(!button.disabled, "Native button is enabled: " + name);
        UIEventListener.Get(button.gameObject).onClick.Invoke(button.gameObject);
    }
    private static void InvokeEvenIfDisabled(string name)
    {
        var button = Button(name);
        UIEventListener.Get(button.gameObject).onClick.Invoke(button.gameObject);
    }
    private static void SaveFrame(string name) =>
        typeof(IntegrationProbe).GetMethod("SaveFrame", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null,
            new object[] { Path.Combine(evidence, name) });
    private static void Finish(bool passed, string? error)
    {
        done = true;
        firstResult.TrySetResult(new(RevisionUpdateCheckState.Cancelled, null, "Probe finished"));
        foreach (var flow in flows) if (!flow.PreparationCompleted || !flow.AuthorizationCompleted) flow.Cancel();
        File.WriteAllText(Path.Combine(evidence, "result.json"), JsonSerializer.Serialize(
            new { passed, updateOnly = true, stage, checks, fakeFlows = flowCreations, simulatedQuitCalls = quitCalls, error },
            new JsonSerializerOptions { WriteIndented = true }));
        // Only the marked test host exits here. The controller's quit action
        // above never reaches Application.Quit or starts another AA process.
        Application.Quit(passed ? 0 : 2);
    }

    private sealed class FakeFlow : IRevisionUpdateFlow
    {
        private readonly TaskCompletionSource<bool> preparation = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<bool> authorization = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private RevisionUpdateFlowSnapshot snapshot = new(RevisionUpdateFlowState.Idle, 0, "");
        private CancellationTokenRegistration preparationCancellation, authorizationCancellation;
        private bool accepted;
        internal int PrepareCalls, AuthorizeCalls, CancelCalls, DisposeCalls;
        internal string Version = "";
        internal bool PreparationCompleted => preparation.Task.IsCompleted;
        internal bool AuthorizationCompleted => authorization.Task.IsCompleted;
        public RevisionUpdateFlowSnapshot Snapshot => Volatile.Read(ref snapshot);
        private void Set(RevisionUpdateFlowState state, float percent, string message)
            => Volatile.Write(ref snapshot, new(state, percent, message));
        public Task PrepareAsync(RevisionUpdateInfo update, CancellationToken token)
        {
            Interlocked.Increment(ref PrepareCalls); Version = update.Version;
            SetDownloading(0); preparationCancellation = token.Register(Cancel); return preparation.Task;
        }
        public Task<bool> AuthorizeExitAsync(CancellationToken token)
        {
            Interlocked.Increment(ref AuthorizeCalls);
            authorizationCancellation = token.Register(Cancel); return authorization.Task;
        }
        internal void SetDownloading(float percent) => Set(RevisionUpdateFlowState.Downloading, percent, "正在下载模拟更新…");
        internal void SetVerifying() => Set(RevisionUpdateFlowState.Verifying, 100, "正在校验模拟更新文件…");
        internal void FailPreparation(string message)
        { Set(RevisionUpdateFlowState.Failed, 100, message); preparation.TrySetResult(false); }
        internal void BecomeReady()
        { Set(RevisionUpdateFlowState.Ready, 100, "模拟更新文件已准备好。"); preparation.TrySetResult(true); }
        internal void CompleteAuthorization(bool success, string message = "模拟安装助手已确认授权。")
        {
            accepted = success;
            Set(success ? RevisionUpdateFlowState.ExitAuthorized : RevisionUpdateFlowState.Failed, 100, message);
            authorization.TrySetResult(success);
        }
        public void Cancel()
        {
            Interlocked.Increment(ref CancelCalls); accepted = false;
            Set(RevisionUpdateFlowState.Cancelled, 0, "模拟更新已取消。");
            preparation.TrySetCanceled(); authorization.TrySetCanceled();
        }
        public void Dispose()
        {
            if (Interlocked.Increment(ref DisposeCalls) != 1) return;
            if (!accepted) Cancel();
            preparationCancellation.Dispose(); authorizationCancellation.Dispose();
        }
    }
}
