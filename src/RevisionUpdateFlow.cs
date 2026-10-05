using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BepInEx;

namespace AzureArchive.RevisionCompare;

internal enum RevisionUpdateFlowState { Idle, Downloading, Verifying, Ready, Failed, Cancelled, ExitAuthorized }
internal sealed record RevisionUpdateFlowSnapshot(RevisionUpdateFlowState State, float Percent, string Message);
internal interface IRevisionUpdateFlow : IDisposable
{
    RevisionUpdateFlowSnapshot Snapshot { get; }
    Task PrepareAsync(RevisionUpdateInfo update, CancellationToken token);
    Task<bool> AuthorizeExitAsync(CancellationToken token);
    void Cancel();
}

// HTTP and filesystem work runs on workers. This class never accesses Unity.
internal sealed class RevisionUpdateFlow : IRevisionUpdateFlow
{
    private readonly CancellationTokenSource cancellation = new();
    private readonly RevisionUpdateClient client = new(RevisionComparePlugin.Version);
    private readonly RevisionUpdateApplySession apply = new(Paths.GameRootPath, RevisionComparePlugin.Version);
    private RevisionUpdateDownload? download;
    private RevisionUpdateFlowSnapshot snapshot = new(RevisionUpdateFlowState.Idle, 0, "");
    private bool exitAuthorized, disposed;
    public RevisionUpdateFlowSnapshot Snapshot
    {
        get
        {
            var current = Volatile.Read(ref snapshot);
            if (current.State == RevisionUpdateFlowState.Downloading && download != null)
            {
                var progress = download.Snapshot;
                return progress.State == RevisionDownloadState.Verifying
                    ? new(RevisionUpdateFlowState.Verifying, 100, "正在校验下载文件…")
                    : new(RevisionUpdateFlowState.Downloading, progress.Total > 0 ? (float)(100.0 * progress.Bytes / progress.Total) : -1, "正在下载新版本…");
            }
            return current;
        }
    }
    private void Set(RevisionUpdateFlowState state, string message, float percent = -1)
        => Volatile.Write(ref snapshot, new(state, percent, message));

    public Task PrepareAsync(RevisionUpdateInfo update, CancellationToken token) => Task.Run(async () =>
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, cancellation.Token);
        var ct = linked.Token;
        try
        {
            Set(RevisionUpdateFlowState.Downloading, "正在获取更新文件信息…");
            var info = await client.GetDownloadInfoAsync(update, ct).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            if (info.State != RevisionUpdateDescriptorState.Ready || info.Descriptor == null)
                throw new InvalidOperationException(info.Error);
            download = new RevisionUpdateDownload(info.Descriptor);
            var result = await download.DownloadAsync(ct).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            if (result.State != RevisionDownloadState.Completed || result.Update == null)
                throw new InvalidOperationException(result.Error);
            Set(RevisionUpdateFlowState.Verifying, "正在校验当前安装并准备更新…");
            var dll = info.Descriptor.Files.Single(file => file.Name == "AzureArchive.RevisionCompare.dll");
            var manifest = info.Descriptor.Files.Single(file => file.Name == "manifest.json");
            await apply.PrepareAndStartAsync(new RevisionUpdateApplyPayload(result.Update.Version,
                result.Update.AssemblyPath, result.Update.ManifestPath, dll.Sha256, dll.Size,
                manifest.Sha256, manifest.Size), ct).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            if (apply.Snapshot.State != RevisionUpdateApplyState.AwaitingApproval)
                throw new InvalidOperationException(apply.Snapshot.Error);
            download.Dispose(); download = null;
            Set(RevisionUpdateFlowState.Ready, "更新文件已准备好。", 100);
        }
        catch (OperationCanceledException) { apply.Cancel(); Set(RevisionUpdateFlowState.Cancelled, "更新已取消。"); }
        catch (Exception error) { apply.Cancel(); Set(RevisionUpdateFlowState.Failed, error.Message); }
    }, CancellationToken.None);

    public Task<bool> AuthorizeExitAsync(CancellationToken token) => Task.Run(async () =>
    {
        try
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, cancellation.Token);
            bool accepted = await apply.AuthorizeExitAsync(linked.Token).ConfigureAwait(false);
            exitAuthorized = accepted;
            Set(accepted ? RevisionUpdateFlowState.ExitAuthorized : RevisionUpdateFlowState.Failed,
                accepted ? "正在等待 AA 退出…" : apply.Snapshot.Error);
            return accepted;
        }
        catch (Exception error) { Set(RevisionUpdateFlowState.Failed, error.Message); return false; }
    }, CancellationToken.None);
    public void Cancel()
    {
        if (disposed) return;
        cancellation.Cancel(); apply.Cancel(); exitAuthorized = false;
    }
    public void Dispose()
    {
        if (disposed) return;
        if (!exitAuthorized) Cancel();
        disposed = true;
        download?.Dispose(); client.Dispose(); apply.Dispose(); cancellation.Dispose();
    }
}
