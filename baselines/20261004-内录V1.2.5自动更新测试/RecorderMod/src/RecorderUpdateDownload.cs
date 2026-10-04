using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

namespace AzureArchive.Recorder;

internal enum RecorderDownloadState { Idle, Downloading, Pausing, Paused, Verifying, Completed, Cancelling, Cancelled, Failed }
internal sealed record RecorderDownloadSnapshot(RecorderDownloadState State, long Bytes, long Total, string Error, string? VerifiedFilePath);

// A job owns exactly two filenames in a fresh private directory. It never writes to the MOD.
internal sealed class RecorderUpdateDownload : IDisposable
{
    private readonly object _gate = new();
    private readonly RecorderUpdateInfo _update;
    private readonly HttpClient _http;
    private readonly string _directory, _partialPath, _verifiedPath;
    private RecorderDownloadSnapshot _snapshot;
    private CancellationTokenSource? _attempt;
    private Task _worker = Task.CompletedTask;
    private StopIntent _stop;
    private bool _resumeRequested, _bodyComplete, _disposed;
    private string? _etag;
    private enum StopIntent { None, Pause, Cancel }

    internal RecorderUpdateDownload(RecorderUpdateInfo update)
        : this(update, RecorderUpdateNetwork.CreateHandler(), Path.Combine(Path.GetTempPath(), "AzureArchiveRecorderUpdates")) { }

    // Only the deterministic local tests supply a handler and a temporary output root.
    internal RecorderUpdateDownload(RecorderUpdateInfo update, HttpMessageHandler testHandler, string testTempRoot)
    {
        RecorderUpdateNetwork.ValidateInfo(update);
        _update = update;
        _http = new HttpClient(testHandler, true) { Timeout = Timeout.InfiniteTimeSpan };
        _directory = Path.Combine(Path.GetFullPath(testTempRoot), Guid.NewGuid().ToString("N"));
        _partialPath = Path.Combine(_directory, "update.partial");
        _verifiedPath = Path.Combine(_directory, "update.zip");
        _snapshot = new(RecorderDownloadState.Idle, 0, update.Size, "", null);
    }

    internal RecorderDownloadSnapshot Snapshot { get { lock (_gate) return _snapshot; } }
    // Completion of the currently running attempt, including pause/cancel cleanup.
    internal Task Completion { get { lock (_gate) return _worker; } }
    internal void Start()
    {
        lock (_gate)
        {
            if (_disposed || _snapshot.State != RecorderDownloadState.Idle) return;
            BeginLocked();
        }
    }
    internal void Resume()
    {
        lock (_gate)
        {
            if (_disposed) return;
            if (_snapshot.State == RecorderDownloadState.Pausing) { _resumeRequested = true; return; }
            if (_snapshot.State != RecorderDownloadState.Paused && _snapshot.State != RecorderDownloadState.Failed) return;
            BeginLocked();
        }
    }
    internal void Pause()
    {
        lock (_gate)
        {
            if (_snapshot.State != RecorderDownloadState.Downloading && _snapshot.State != RecorderDownloadState.Verifying) return;
            _stop = StopIntent.Pause;
            _snapshot = _snapshot with { State = RecorderDownloadState.Pausing };
            _attempt?.Cancel();
        }
    }
    internal void Cancel()
    {
        lock (_gate)
        {
            if (_snapshot.State == RecorderDownloadState.Cancelled || _snapshot.State == RecorderDownloadState.Cancelling) return;
            _resumeRequested = false;
            _stop = StopIntent.Cancel;
            _snapshot = _snapshot with { State = RecorderDownloadState.Cancelling, VerifiedFilePath = null, Error = "" };
            if (_attempt != null) _attempt.Cancel();
            else _worker = Task.Run(FinishCancellation);
        }
    }
    private void BeginLocked()
    {
        _stop = StopIntent.None;
        _resumeRequested = false;
        _attempt = new CancellationTokenSource();
        _snapshot = _snapshot with { State = RecorderDownloadState.Downloading, Error = "", VerifiedFilePath = null };
        var token = _attempt.Token;
        _worker = Task.Run(() => RunAttemptAsync(token));
    }

    private async Task RunAttemptAsync(CancellationToken token)
    {
        Exception? failure = null;
        try
        {
            token.ThrowIfCancellationRequested();
            Directory.CreateDirectory(_directory);
            if (!_bodyComplete) await ReceiveAsync(token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            lock (_gate)
            {
                token.ThrowIfCancellationRequested();
                _snapshot = _snapshot with { State = RecorderDownloadState.Verifying };
            }
            using (var file = new FileStream(_partialPath, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, true))
            {
                if (file.Length != _update.Size) throw new InvalidDataException("更新包长度不完整，请重新下载。");
                using var hash = SHA256.Create();
                var bytes = await hash.ComputeHashAsync(file, token).ConfigureAwait(false);
                if (!Convert.ToHexString(bytes).Equals(_update.Sha256, StringComparison.OrdinalIgnoreCase))
                {
                    _bodyComplete = false;
                    throw new InvalidDataException("更新包 SHA-256 校验失败，请终止后重新下载。");
                }
            }
            lock (_gate)
            {
                token.ThrowIfCancellationRequested();
                File.Move(_partialPath, _verifiedPath, false);
                _snapshot = new(RecorderDownloadState.Completed, _update.Size, _update.Size, "", _verifiedPath);
            }
        }
        catch (Exception ex) { failure = ex; }
        finally
        {
            lock (_gate)
            {
                _attempt?.Dispose();
                _attempt = null;
                if (_stop == StopIntent.Cancel) FinishCancellationLocked();
                else if (_stop == StopIntent.Pause)
                {
                    _snapshot = _snapshot with { State = RecorderDownloadState.Paused, Error = "", VerifiedFilePath = null, Bytes = PartialLength() };
                    if (_resumeRequested && !_disposed) BeginLocked();
                }
                else if (failure != null)
                {
                    _snapshot = _snapshot with
                    {
                        State = RecorderDownloadState.Failed,
                        Bytes = PartialLength(),
                        Error = failure is OperationCanceledException ? "下载连接超时，请继续下载或稍后重试。" : RecorderUpdateNetwork.Explain(failure, "下载更新"),
                        VerifiedFilePath = null
                    };
                }
            }
        }
    }

    private async Task ReceiveAsync(CancellationToken token)
    {
        using var file = new FileStream(_partialPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read, 65536, true);
        var offset = file.Length;
        // Without a strong validator, or without proof of body EOF, restart safely.
        if (offset > _update.Size || offset == _update.Size || (offset > 0 && _etag == null))
        { file.SetLength(0); offset = 0; _etag = null; }
        PublishBytes(offset);
        using var response = await SendAsync(offset, token).ConfigureAwait(false);
        if (response.Content.Headers.ContentEncoding.Count != 0) throw new InvalidDataException("下载服务器返回了不支持的压缩内容。");
        if (response.StatusCode == HttpStatusCode.PartialContent)
        {
            var range = response.Content.Headers.ContentRange;
            if (offset == 0 || range == null || range.Unit != "bytes" || !range.HasRange || !range.HasLength ||
                range.From != offset || range.To != _update.Size - 1 || range.Length != _update.Size ||
                response.Headers.ETag == null || response.Headers.ETag.IsWeak || response.Headers.ETag.ToString() != _etag)
                throw new InvalidDataException("服务器的续传范围或文件标识不匹配，请终止后重新下载。");
        }
        else if (response.StatusCode == HttpStatusCode.OK)
        {
            // If-Range may legitimately result in 200; do not append a whole body to a partial file.
            file.SetLength(0);
            offset = 0;
            PublishBytes(0);
        }
        else throw new InvalidDataException("下载服务器返回异常状态（" + (int)response.StatusCode + "），请稍后重试。");
        var remaining = _update.Size - offset;
        if (response.Content.Headers.ContentLength.HasValue && response.Content.Headers.ContentLength.Value != remaining)
            throw new InvalidDataException("下载内容长度与更新清单不一致。");
        _etag = response.Headers.ETag != null && !response.Headers.ETag.IsWeak ? response.Headers.ETag.ToString() : null;
        file.Position = offset;
        using var stream = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
        var block = new byte[65536];
        while (true)
        {
            using var readTimeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            readTimeout.CancelAfter(TimeSpan.FromSeconds(30));
            var count = await stream.ReadAsync(block.AsMemory(), readTimeout.Token).ConfigureAwait(false);
            if (count == 0) break;
            if (file.Position + count > _update.Size) throw new InvalidDataException("下载内容超过更新清单声明的大小，已拒绝该更新包。");
            await file.WriteAsync(block.AsMemory(0, count), token).ConfigureAwait(false);
            PublishBytes(file.Position);
        }
        if (file.Position != _update.Size) throw new InvalidDataException("下载意外中断，更新包尚未收取完整，可以继续下载。");
        await file.FlushAsync(token).ConfigureAwait(false);
        _bodyComplete = true;
    }

    private async Task<HttpResponseMessage> SendAsync(long offset, CancellationToken token)
    {
        var uri = new Uri(_update.DownloadUrl);
        for (var redirect = 0; redirect <= 5; redirect++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            request.Headers.UserAgent.ParseAdd("AzureArchiveRecorder/1.2.5");
            if (offset > 0)
            {
                request.Headers.Range = new RangeHeaderValue(offset, null);
                request.Headers.IfRange = new RangeConditionHeaderValue(EntityTagHeaderValue.Parse(_etag!));
            }
            using var headerTimeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            headerTimeout.CancelAfter(TimeSpan.FromSeconds(15));
            var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, headerTimeout.Token).ConfigureAwait(false);
            var code = (int)response.StatusCode;
            if (code != 301 && code != 302 && code != 303 && code != 307 && code != 308) return response;
            var location = response.Headers.Location;
            response.Dispose();
            if (location == null || !Uri.TryCreate(uri, location, out var next) || !RecorderUpdateNetwork.IsDownloadRedirect(next))
                throw new InvalidDataException("下载服务器尝试跳转到未允许的地址，已停止下载。");
            uri = next;
        }
        throw new InvalidDataException("下载服务器跳转次数过多，已停止下载。");
    }

    private void PublishBytes(long bytes)
    { lock (_gate) _snapshot = _snapshot with { Bytes = bytes }; }
    private long PartialLength()
    { try { return File.Exists(_partialPath) ? new FileInfo(_partialPath).Length : 0; } catch { return _snapshot.Bytes; } }
    private void FinishCancellation()
    { lock (_gate) FinishCancellationLocked(); }
    private void FinishCancellationLocked()
    {
        try
        {
            // Delete only this job's exact two files; never recurse or enumerate the caller's directory.
            if (File.Exists(_partialPath)) File.Delete(_partialPath);
            if (File.Exists(_verifiedPath)) File.Delete(_verifiedPath);
            if (Directory.Exists(_directory)) Directory.Delete(_directory, false);
            _snapshot = new(RecorderDownloadState.Cancelled, 0, _update.Size, "", null);
        }
        catch (Exception ex)
        {
            _snapshot = _snapshot with { State = RecorderDownloadState.Cancelled, VerifiedFilePath = null, Error = RecorderUpdateNetwork.Explain(ex, "清理更新临时文件") };
        }
    }
    public void Dispose()
    {
        Task worker;
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            Cancel();
            worker = _worker;
        }
        // Disposing from the UI never waits for network reads or hashing.
        _ = worker.ContinueWith(_ => _http.Dispose(), CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }
}
