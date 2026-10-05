using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace AzureArchive.RevisionCompare;

internal enum RevisionUpdateDescriptorState { Ready, Unavailable, Failed, Cancelled }
internal sealed record RevisionUpdateDescriptorResult(RevisionUpdateDescriptorState State, RevisionUpdateDescriptor? Descriptor, string Error);
internal sealed record RevisionUpdateFile(string Name, string Url, string Sha256, long Size);
internal sealed record RevisionUpdateDescriptor
{
    public int SchemaVersion { get; init; }
    public string Product { get; init; } = "";
    public string Version { get; init; } = "";
    public IReadOnlyList<RevisionUpdateFile> Files { get; init; } = Array.Empty<RevisionUpdateFile>();
}
internal enum RevisionDownloadState { Idle, Downloading, Verifying, Completed, Failed, Cancelled }
internal sealed record RevisionDownloadedUpdate(string Version, string Directory, string AssemblyPath, string ManifestPath);
internal sealed record RevisionDownloadSnapshot(RevisionDownloadState State, long Bytes, long Total, string Error, RevisionDownloadedUpdate? Update);
internal sealed record RevisionUpdateDownloadResult(RevisionDownloadState State, RevisionDownloadedUpdate? Update, string Error);

// A download owns only four fixed filenames in its fresh task directory. No writes to AA,
// extraction, process launch or Unity access. Keep it alive until the installer copies its files.
internal sealed class RevisionUpdateDownload : IDisposable
{
    internal const string AssemblyName = "AzureArchive.RevisionCompare.dll";
    internal const string ManifestName = "manifest.json";
    internal const long MaximumAssemblyBytes = 32L * 1024 * 1024;
    private const string RawBranch = "https://raw.githubusercontent.com/LittleLuoZiXi/AA-MOD/refs/heads/mods/revision-compare/";
    private const string ApiBranch = "https://api.github.com/repos/LittleLuoZiXi/AA-MOD/contents/";
    private const string BranchQuery = "?ref=mods%2Frevision-compare";
    private readonly object _gate = new();
    private readonly HttpClient _http;
    private readonly RevisionUpdateDescriptor _descriptor;
    private readonly string _baseDirectory, _directory;
    private readonly TimeSpan _timeout;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly long _total;
    private RevisionDownloadSnapshot _snapshot;
    private Task<RevisionUpdateDownloadResult>? _worker;
    private bool _disposed, _created;

    internal RevisionUpdateDownload(RevisionUpdateDescriptor descriptor)
        : this(descriptor, RevisionUpdateClient.CreateHandler(), Path.Combine(Path.GetTempPath(), "AzureArchiveRevisionUpdates"), TimeSpan.FromMinutes(3)) { }

    // Only deterministic tests choose a transport and temporary base directory.
    internal RevisionUpdateDownload(RevisionUpdateDescriptor descriptor, HttpMessageHandler handler, string testTempRoot, TimeSpan? timeout = null)
    {
        ValidateDescriptor(descriptor);
        RevisionSemanticVersion.TryParse(descriptor.Version, out var version);
        _descriptor = descriptor with { Version = version.ToString(), Files = descriptor.Files.Select(file => file with { }).ToArray() };
        _timeout = timeout ?? TimeSpan.FromMinutes(3);
        if (_timeout <= TimeSpan.Zero || _timeout.TotalMilliseconds > int.MaxValue) throw new ArgumentOutOfRangeException(nameof(timeout));
        _baseDirectory = Path.GetFullPath(testTempRoot);
        _directory = Path.Combine(_baseDirectory, Guid.NewGuid().ToString("N"));
        _http = new HttpClient(handler, true) { Timeout = Timeout.InfiniteTimeSpan };
        _total = _descriptor.Files.Sum(file => file.Size);
        _snapshot = new(RevisionDownloadState.Idle, 0, _total, "", null);
    }

    internal RevisionDownloadSnapshot Snapshot { get { lock (_gate) return _snapshot; } }

    // One attempt per object. A retry deliberately creates a fresh task and staging directory.
    internal Task<RevisionUpdateDownloadResult> DownloadAsync(CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            if (_disposed) return Task.FromResult(new RevisionUpdateDownloadResult(RevisionDownloadState.Cancelled, null, "更新下载已结束。"));
            if (_worker != null) return _worker;
            return _worker = Task.Run(() => RunAsync(cancellationToken));
        }
    }

    private async Task<RevisionUpdateDownloadResult> RunAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _shutdown.Token);
            timeout.CancelAfter(_timeout);
            var token = timeout.Token;
            token.ThrowIfCancellationRequested();
            NoLinks(_baseDirectory);
            Directory.CreateDirectory(_baseDirectory);
            NoLinks(_baseDirectory);
            if (Directory.Exists(_directory) || File.Exists(_directory)) throw new IOException("更新临时目录已存在。");
            Directory.CreateDirectory(_directory); _created = true;
            NoLinks(_directory);
            long bytes = 0;
            Set(RevisionDownloadState.Downloading, bytes);
            foreach (var file in _descriptor.Files)
            {
                await ReceiveAsync(file, bytes, token).ConfigureAwait(false);
                bytes += file.Size;
            }
            Set(RevisionDownloadState.Verifying, bytes);
            var manifest = await File.ReadAllBytesAsync(Owned(ManifestName), token).ConfigureAwait(false);
            if (RevisionUpdateClient.ReadManifest(manifest).ToString() != _descriptor.Version)
                throw new InvalidDataException("下载的 MOD 清单与更新版本不一致。");
            var update = new RevisionDownloadedUpdate(_descriptor.Version, _directory, Owned(AssemblyName), Owned(ManifestName));
            lock (_gate)
            {
                token.ThrowIfCancellationRequested();
                _snapshot = new(RevisionDownloadState.Completed, _total, _total, "", update);
            }
            return new(RevisionDownloadState.Completed, update, "");
        }
        catch (Exception ex)
        {
            var cancelled = cancellationToken.IsCancellationRequested || _shutdown.IsCancellationRequested;
            var state = cancelled ? RevisionDownloadState.Cancelled : RevisionDownloadState.Failed;
            var error = cancelled ? "更新下载已取消。" : ex switch
            {
                OperationCanceledException => "更新下载超时，请重试。",
                InvalidDataException => ex.Message,
                HttpRequestException => "无法连接更新下载服务器。",
                _ => "下载更新失败，请重试。"
            };
            var cleanup = Cleanup();
            if (cleanup.Length != 0) error += " " + cleanup;
            lock (_gate) _snapshot = new(state, 0, _total, error, null);
            return new(state, null, error);
        }
    }

    private async Task ReceiveAsync(RevisionUpdateFile file, long completed, CancellationToken token)
    {
        HttpResponseMessage response;
        var api = false;
        try { response = await SendAsync(file.Url, false, token).ConfigureAwait(false); }
        catch (Exception ex) when (ex is HttpRequestException || ex is IOException)
        {
            token.ThrowIfCancellationRequested();
            api = true;
            response = await SendAsync(ApiBranch + file.Url.Substring(RawBranch.Length) + BranchQuery, true, token).ConfigureAwait(false);
        }
        using (response)
        {
            if (response.Content.Headers.ContentEncoding.Count != 0) throw new InvalidDataException("更新文件使用了不支持的压缩格式。");
            NoLinks(_directory);
            var partial = Owned(file.Name + ".partial");
            using (var output = new FileStream(partial, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, true))
            using (var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
            {
                long received = 0;
                if (api)
                {
                    // The API normally honors raw Accept. Its JSON/base64 fallback has a separate
                    // bounded envelope; neither form can exceed the descriptor's decoded length.
                    var body = await ReadApiBodyAsync(response, file.Size, token).ConfigureAwait(false);
                    if (body.LongLength != file.Size) throw new InvalidDataException("下载内容长度与更新清单不一致。");
                    await output.WriteAsync(body.AsMemory(), token).ConfigureAwait(false);
                    hash.AppendData(body); received = body.LongLength;
                    Set(RevisionDownloadState.Downloading, completed + received);
                }
                else
                {
                    if (response.Content.Headers.ContentLength.HasValue && response.Content.Headers.ContentLength.Value != file.Size)
                        throw new InvalidDataException("下载内容长度与更新清单不一致。");
                    using var input = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
                    var block = new byte[65536];
                    while (true)
                    {
                        var count = await input.ReadAsync(block.AsMemory(), token).ConfigureAwait(false);
                        if (count == 0) break;
                        if (received + count > file.Size) throw new InvalidDataException("下载内容超过更新清单声明的大小。");
                        await output.WriteAsync(block.AsMemory(0, count), token).ConfigureAwait(false);
                        hash.AppendData(block, 0, count); received += count;
                        Set(RevisionDownloadState.Downloading, completed + received);
                    }
                }
                if (received != file.Size) throw new InvalidDataException("下载文件不完整。");
                Set(RevisionDownloadState.Verifying, completed + received);
                if (!Convert.ToHexString(hash.GetHashAndReset()).Equals(file.Sha256, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("下载文件的 SHA-256 校验失败。");
                await output.FlushAsync(token).ConfigureAwait(false);
            }
            token.ThrowIfCancellationRequested();
            NoLinks(_directory);
            File.Move(partial, Owned(file.Name), false);
        }
    }

    private async Task<HttpResponseMessage> SendAsync(string url, bool api, CancellationToken token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.UserAgent.ParseAdd(RevisionUpdateClient.ProductName);
        if (api) request.Headers.Accept.ParseAdd("application/vnd.github.raw+json");
        var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.OK) return response;
        var status = response.StatusCode;
        response.Dispose();
        // Automatic redirects are disabled; the only fallback is the same repository path/ref.
        throw new HttpRequestException("下载服务器返回异常状态。", null, status);
    }

    private static async Task<byte[]> ReadApiBodyAsync(HttpResponseMessage response, long size, CancellationToken token)
    {
        var limit = checked(size * 2 + 65536);
        if (response.Content.Headers.ContentLength > limit) throw new InvalidDataException("下载响应超过允许大小。");
        using var input = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
        using var buffer = new MemoryStream();
        var block = new byte[65536];
        while (true)
        {
            var count = await input.ReadAsync(block.AsMemory(), token).ConfigureAwait(false);
            if (count == 0) break;
            if (buffer.Length + count > limit) throw new InvalidDataException("下载响应超过允许大小。");
            buffer.Write(block, 0, count);
        }
        var body = buffer.ToArray();
        var first = 0;
        while (first < body.Length && (body[first] == 32 || body[first] == 9 || body[first] == 10 || body[first] == 13)) first++;
        if (first >= body.Length || body[first] != (byte)'{') return body;
        using var document = JsonDocument.Parse(body, new JsonDocumentOptions { MaxDepth = 16 });
        var root = document.RootElement;
        RejectDuplicates(root);
        if (!root.TryGetProperty("encoding", out _) && !root.TryGetProperty("content", out _)) return body;
        if (!root.TryGetProperty("encoding", out var encoding) || encoding.ValueKind != JsonValueKind.String || encoding.GetString() != "base64" ||
            !root.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.String)
            throw new InvalidDataException("下载响应编码无效。");
        byte[] decoded;
        try { decoded = Convert.FromBase64String(content.GetString()!); }
        catch (FormatException) { throw new InvalidDataException("下载响应编码无效。"); }
        if (decoded.LongLength != size) throw new InvalidDataException("下载内容长度与更新清单不一致。");
        return decoded;
    }

    internal static RevisionUpdateDescriptor ParseDescriptor(byte[] body)
    {
        if (body.Length > RevisionUpdateClient.MaximumManifestBytes) throw new InvalidDataException("下载清单超过允许大小。");
        using var document = JsonDocument.Parse(body, new JsonDocumentOptions { MaxDepth = 16 });
        RejectDuplicates(document.RootElement);
        var descriptor = document.RootElement.Deserialize<RevisionUpdateDescriptor>() ?? throw new InvalidDataException("下载清单为空。");
        ValidateDescriptor(descriptor);
        RevisionSemanticVersion.TryParse(descriptor.Version, out var version);
        return descriptor with { Version = version.ToString(), Files = descriptor.Files.Select(file => file with { }).ToArray() };
    }

    internal static void ValidateDescriptor(RevisionUpdateDescriptor descriptor)
    {
        if (descriptor == null || descriptor.SchemaVersion != 1 || descriptor.Product != RevisionUpdateClient.ProductName ||
            !RevisionSemanticVersion.TryParse(descriptor.Version, out var version) || descriptor.Files == null || descriptor.Files.Count != 2)
            throw new InvalidDataException("下载清单的产品、版本、格式或文件数量无效。");
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var file in descriptor.Files)
        {
            if (file == null || (file.Name != AssemblyName && file.Name != ManifestName) || !seen.Add(file.Name))
                throw new InvalidDataException("下载清单只能包含 MOD DLL 和 manifest.json，且不得重复。");
            var limit = file.Name == ManifestName ? RevisionUpdateClient.MaximumManifestBytes : MaximumAssemblyBytes;
            if (file.Size <= 0 || file.Size > limit || !IsHash(file.Sha256)) throw new InvalidDataException("下载清单的文件长度或 SHA-256 无效。");
            var expected = RawBranch + "updates/revision-compare/files/" + Uri.EscapeDataString(version.ToString()) + "/" + file.Name;
            if (file.Url != expected) throw new InvalidDataException("下载文件地址不在当前版本的固定仓库路径内。");
        }
    }

    private static bool IsHash(string value)
    {
        if (value == null || value.Length != 64) return false;
        foreach (var c in value) if (!(c >= '0' && c <= '9') && !(c >= 'a' && c <= 'f') && !(c >= 'A' && c <= 'F')) return false;
        return true;
    }
    private static void RejectDuplicates(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in value.EnumerateObject())
            {
                if (!names.Add(property.Name)) throw new InvalidDataException("下载清单包含重复字段。");
                RejectDuplicates(property.Value);
            }
        }
        else if (value.ValueKind == JsonValueKind.Array) foreach (var item in value.EnumerateArray()) RejectDuplicates(item);
    }
    private void Set(RevisionDownloadState state, long bytes)
    { lock (_gate) _snapshot = new(state, bytes, _total, "", null); }
    private string Owned(string name) => Path.Combine(_directory, name);
    private static void NoLinks(string path)
    {
        for (string? current = Path.GetFullPath(path); !string.IsNullOrEmpty(current); current = Path.GetDirectoryName(current))
        {
            if (!File.Exists(current) && !Directory.Exists(current)) continue;
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("更新临时目录不得包含链接或重解析点。");
        }
    }
    private string Cleanup()
    {
        if (!_created) return "";
        try
        {
            NoLinks(_directory);
            foreach (var name in new[] { AssemblyName, ManifestName, AssemblyName + ".partial", ManifestName + ".partial" })
            {
                var path = Owned(name); NoLinks(path);
                if (File.Exists(path)) File.Delete(path);
            }
            if (Directory.Exists(_directory)) Directory.Delete(_directory, false);
            _created = false;
            return "";
        }
        catch { return "未能完全清理本次下载的临时文件。"; }
    }

    public void Dispose()
    {
        Task worker;
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _shutdown.Cancel();
            worker = _worker ?? Task.CompletedTask;
        }
        _ = worker.ContinueWith(_ => { Cleanup(); _http.Dispose(); _shutdown.Dispose(); },
            CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }
}
