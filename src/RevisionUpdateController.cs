using System;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using AzureArchive.Automation;
using Studio.Scripts.OperationManagement;
using UnityEngine;

namespace AzureArchive.RevisionCompare;

internal sealed class RevisionUpdateController : IDisposable
{
    private readonly CancellationTokenSource cancellation = new();
    private readonly RevisionUpdateClient? client;
    private readonly Task<RevisionUpdateCheckResult> checkTask;
    private readonly Action<string> log;
    private readonly Func<IRevisionUpdateFlow> createFlow;
    private readonly Func<string?> blockedReason;
    private readonly Action quit;
    private readonly NativeRevisionUpdateUi ui;
    private RevisionUpdateInfo? available;
    private IRevisionUpdateFlow? flow;
    private CancellationTokenSource? updateCancellation;
    private Task? preparation;
    private Task<bool>? authorization;
    private bool consumed, offered, disposed, exitRequested;
    private float nextOffer;
    internal bool IsVisible => ui.IsVisible;
    internal bool CheckCompleted => consumed;
    internal string Failure { get; private set; } = "";

    internal RevisionUpdateController(Action<string> log,
        Func<CancellationToken, Task<RevisionUpdateCheckResult>>? check = null,
        Func<IRevisionUpdateFlow>? createFlow = null, Func<string?>? blockedReason = null, Action? quit = null)
    {
        this.log = log;
        this.createFlow = createFlow ?? (() => new RevisionUpdateFlow());
        this.blockedReason = blockedReason ?? GetBlockedReason;
        this.quit = quit ?? (() => Application.Quit());
        ui = new NativeRevisionUpdateUi(StartUpdate, Dismiss, log);
        if (check == null)
        {
            client = new RevisionUpdateClient(RevisionComparePlugin.Version);
            check = client.CheckAsync;
        }
        var token = cancellation.Token;
        checkTask = Task.Run(() => check(token), token);
        log("RevisionCompare: checking the dedicated GitHub branch for a newer version.");
    }

    internal bool Update()
    {
        if (disposed) return false;
        ui.Update();
        if (!consumed && checkTask.IsCompleted)
        {
            consumed = true;
            try
            {
                var result = checkTask.GetAwaiter().GetResult();
                if (result.State == RevisionUpdateCheckState.Available && result.Update != null)
                {
                    available = result.Update;
                    log("RevisionCompare: newer version available: " + available.Version);
                }
                else if (result.State == RevisionUpdateCheckState.Failed)
                    log("RevisionCompare: update check skipped: " + result.Error);
                else if (result.State == RevisionUpdateCheckState.NoUpdate)
                    log("RevisionCompare: no newer version on the update branch.");
            }
            catch (OperationCanceledException) { }
            catch (Exception error) { log("RevisionCompare: update check skipped: " + error.Message); }
        }
        if (!offered && available != null && Time.realtimeSinceStartup >= nextOffer)
        {
            nextOffer = Time.realtimeSinceStartup + 1;
            try { offered = ui.TryShow(RevisionComparePlugin.Version, available.Version); }
            catch (Exception error)
            {
                offered = true; ui.Hide();
                log("RevisionCompare: update notification unavailable: " + error.Message);
            }
        }
        UpdateInstallation();
        if (ui.IsVisible && Input.GetKeyDown(KeyCode.Escape))
        {
            if (ui.CanDismiss) Dismiss();
            return true;
        }
        return false;
    }

    private void StartUpdate()
    {
        if (available == null || preparation != null || authorization != null || exitRequested) return;
        var reason = blockedReason();
        if (reason != null) { Fail(reason); return; }
        ReleaseFlow(); Failure = "";
        updateCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellation.Token);
        try
        {
            flow = createFlow();
            ui.SetDownloading(-1, "正在获取更新文件信息…");
            preparation = flow.PrepareAsync(available, updateCancellation.Token);
        }
        catch (Exception error) { Fail("无法开始更新：" + error.Message); }
    }

    private void UpdateInstallation()
    {
        if (flow == null || exitRequested) return;
        if (preparation != null)
        {
            var snapshot = flow.Snapshot;
            if (snapshot.State == RevisionUpdateFlowState.Downloading)
                ui.SetDownloading(snapshot.Percent, snapshot.Message);
            else if (snapshot.State == RevisionUpdateFlowState.Verifying)
                ui.SetVerifying(snapshot.Message);
            if (!preparation.IsCompleted) return;
            var task = preparation; preparation = null;
            try { task.GetAwaiter().GetResult(); }
            catch (OperationCanceledException) { Dismiss(); return; }
            catch (Exception error) { Fail("更新未完成：" + error.Message); return; }
            snapshot = flow.Snapshot;
            if (snapshot.State == RevisionUpdateFlowState.Cancelled) { Dismiss(); return; }
            if (snapshot.State != RevisionUpdateFlowState.Ready) { Fail(snapshot.Message); return; }
            var reason = blockedReason();
            if (reason != null) { Fail(reason); return; }
            ui.SetWaitingForExit("文件已校验，正在准备重启 AA…");
            authorization = flow.AuthorizeExitAsync(updateCancellation!.Token);
        }
        if (authorization?.IsCompleted == true)
        {
            var task = authorization; authorization = null;
            try
            {
                if (!task.GetAwaiter().GetResult()) { Fail(flow.Snapshot.Message); return; }
                var reason = blockedReason();
                if (reason != null) { Fail(reason); return; }
                exitRequested = true;
                ui.SetWaitingForExit("正在重启 AA，稍后将自动打开更新后的版本…");
                log("RevisionCompare: validated update handed off; restarting AA.");
                quit();
            }
            catch (Exception error) { exitRequested = false; Fail("无法重启以应用更新：" + error.Message); }
        }
    }

    internal void Dismiss()
    {
        if (exitRequested || authorization != null) return;
        offered = true;
        ReleaseFlow();
        ui.Hide();
    }

    private void Fail(string message)
    {
        ReleaseFlow();
        Failure = string.IsNullOrEmpty(message) ? "更新未完成，请稍后重试。" : message;
        ui.ShowFailure(Failure);
        log("RevisionCompare: " + Failure);
    }

    private void ReleaseFlow()
    {
        var oldFlow = flow; var oldCancellation = updateCancellation;
        var oldTasks = new Task[] { preparation ?? Task.CompletedTask, authorization ?? Task.CompletedTask };
        flow = null; updateCancellation = null; preparation = null; authorization = null;
        if (!exitRequested) oldCancellation?.Cancel();
        // Cancel retracts a prepared helper's authorization until AA exits.
        if (!exitRequested) oldFlow?.Cancel();
        _ = Task.WhenAll(oldTasks).ContinueWith(task =>
        {
            if (task.IsFaulted) _ = task.Exception;
            oldFlow?.Dispose(); oldCancellation?.Dispose();
        }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    private static string? GetBlockedReason()
    {
        try
        {
            var session = AuthoringEditorSession.Current;
            if (session != null)
            {
                session.Refresh(false);
                if (session.Dirty || session.SaveInProgress || session.IsApplying || OperationManager.Working)
                    return "当前工程有未保存的修改或正在保存。请先关闭提示并保存工程，再重新启动 AA 更新。";
            }
            // Optional companion MOD: reflection avoids a hard dependency.
            var recorder = AppDomain.CurrentDomain.GetAssemblies().Select(x => x.GetType("AzureArchive.Recorder.RecorderBehaviour", false)).FirstOrDefault(x => x != null);
            if (recorder != null)
            {
                const BindingFlags flags = BindingFlags.Static | BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
                var instance = recorder.GetField("Instance", flags)?.GetValue(null);
                if (instance != null && new[] { "busy", "Armed", "Capturing" }.Any(name => recorder.GetField(name, flags)?.GetValue(instance) is true))
                    return "内录 MOD 正在工作，请等录制或导出结束后再更新。";
            }
            return null;
        }
        catch { return "暂时无法确认工程状态，请保存工程后重新启动 AA 再更新。"; }
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        // A successfully authorized helper must outlive this process.
        if (!exitRequested) cancellation.Cancel();
        ReleaseFlow(); ui.Dispose();
        _ = checkTask.ContinueWith(task =>
        {
            if (task.IsFaulted) _ = task.Exception;
            client?.Dispose(); cancellation.Dispose();
        }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }
}
