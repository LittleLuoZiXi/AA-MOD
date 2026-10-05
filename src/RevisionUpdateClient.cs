using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace AzureArchive.RevisionCompare;

internal enum RevisionUpdateCheckState { NoUpdate, Available, Failed, Cancelled }
internal sealed record RevisionUpdateInfo(string Version)
{
    // A manifest can announce a version, but cannot supply a navigation or download target.
    internal string Url => RevisionUpdateClient.BranchUrl;
}
internal sealed record RevisionUpdateCheckResult(RevisionUpdateCheckState State, RevisionUpdateInfo? Update, string Error);

// Pure .NET: no Unity objects, file writes, installation, polling, or UI callbacks.
internal sealed class RevisionUpdateClient : IDisposable
{
    internal const string ProductName = "AzureArchiveRevisionCompare";
    internal const string BranchUrl = "https://github.com/LittleLuoZiXi/AA-MOD/tree/mods/revision-compare";
    internal const string FeedUrl = "https://raw.githubusercontent.com/LittleLuoZiXi/AA-MOD/refs/heads/mods/revision-compare/manifest.json";
    internal const string ContentsApiUrl = "https://api.github.com/repos/LittleLuoZiXi/AA-MOD/contents/manifest.json?ref=mods%2Frevision-compare";
    internal const string DescriptorUrl = "https://raw.githubusercontent.com/LittleLuoZiXi/AA-MOD/refs/heads/mods/revision-compare/updates/revision-compare/stable.json";
    internal const string DescriptorApiUrl = "https://api.github.com/repos/LittleLuoZiXi/AA-MOD/contents/updates/revision-compare/stable.json?ref=mods%2Frevision-compare";
    internal const int MaximumManifestBytes = 65536;
    private const int MaximumApiBytes = 131072;
    private readonly HttpClient _http;
    private readonly TimeSpan _timeout;
    private readonly string _currentVersion;
    private readonly object _gate = new();
    private readonly CancellationTokenSource _shutdown = new();
    private Task<RevisionUpdateCheckResult>? _check;
    private bool _disposed;

    internal RevisionUpdateClient(string currentVersion)
        : this(CreateHandler(), TimeSpan.FromSeconds(10), currentVersion) { }

    // Injection changes the transport only; both permitted endpoints remain fixed.
    internal RevisionUpdateClient(HttpMessageHandler handler, TimeSpan timeout, string currentVersion)
    {
        if (handler == null) throw new ArgumentNullException(nameof(handler));
        if (timeout <= TimeSpan.Zero || timeout.TotalMilliseconds > int.MaxValue) throw new ArgumentOutOfRangeException(nameof(timeout));
        _currentVersion = currentVersion;
        _timeout = timeout;
        _http = new HttpClient(handler, true) { Timeout = Timeout.InfiniteTimeSpan };
    }

    // One startup check per instance. The first caller's token owns that check; subsequent
    // calls reuse its task (including failures/cancellation) and never start another request.
    internal Task<RevisionUpdateCheckResult> CheckAsync(CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            if (_check != null) return _check;
            if (_disposed) return Task.FromResult(new RevisionUpdateCheckResult(RevisionUpdateCheckState.Cancelled, null, "更新检查已结束。"));
            return _check = CheckCoreAsync(cancellationToken);
        }
    }

    // Fetch only after an explicit update action. Source-only releases remain honestly unavailable.
    internal async Task<RevisionUpdateDescriptorResult> GetDownloadInfoAsync(RevisionUpdateInfo update, CancellationToken cancellationToken)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _shutdown.Token);
            timeout.CancelAfter(_timeout);
            var token = timeout.Token;
            token.ThrowIfCancellationRequested();
            if (update == null || !RevisionSemanticVersion.TryParse(_currentVersion, out var current) ||
                !RevisionSemanticVersion.TryParse(update.Version, out var offered) || offered.CompareTo(current) <= 0)
                return new(RevisionUpdateDescriptorState.Unavailable, null, "此版本不高于当前版本，无需下载。");
            byte[] body;
            try { body = await ReadEndpointAsync(DescriptorUrl, MaximumManifestBytes, false, token).ConfigureAwait(false); }
            catch (Exception ex) when (ex is HttpRequestException || ex is IOException)
            {
                token.ThrowIfCancellationRequested();
                body = ReadApiManifest(await ReadEndpointAsync(DescriptorApiUrl, MaximumApiBytes, true, token).ConfigureAwait(false));
            }
            token.ThrowIfCancellationRequested();
            var descriptor = RevisionUpdateDownload.ParseDescriptor(body);
            if (!RevisionSemanticVersion.TryParse(descriptor.Version, out var version) || version.ToString() != offered.ToString())
                throw new InvalidDataException("下载清单版本与本次提示版本不一致，请下次启动 AA 后重新检查。");
            return new(RevisionUpdateDescriptorState.Ready, descriptor, "");
        }
        catch (OperationCanceledException)
        {
            return cancellationToken.IsCancellationRequested || _shutdown.IsCancellationRequested
                ? new(RevisionUpdateDescriptorState.Cancelled, null, "更新已取消。")
                : new(RevisionUpdateDescriptorState.Failed, null, "获取下载清单超时。");
        }
        catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.NotFound || ex.StatusCode == HttpStatusCode.Gone)
        { return new(RevisionUpdateDescriptorState.Unavailable, null, "当前分支尚未提供可下载更新文件，暂时无法直接更新。"); }
        catch (Exception ex)
        {
            return new(RevisionUpdateDescriptorState.Failed, null, ex is InvalidDataException ? ex.Message : "获取下载清单失败，请稍后重试。");
        }
    }

    private async Task<RevisionUpdateCheckResult> CheckCoreAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _shutdown.Token);
            timeout.CancelAfter(_timeout);
            var token = timeout.Token;
            token.ThrowIfCancellationRequested();
            if (!RevisionSemanticVersion.TryParse(_currentVersion, out var current))
                throw new InvalidDataException("当前 MOD 版本号无效，已跳过更新检查。");

            byte[] manifest;
            try
            {
                manifest = await ReadEndpointAsync(FeedUrl, MaximumManifestBytes, false, token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is HttpRequestException || ex is IOException)
            {
                // One fixed fallback for raw-host availability; the same deadline covers both.
                token.ThrowIfCancellationRequested();
                var body = await ReadEndpointAsync(ContentsApiUrl, MaximumApiBytes, true, token).ConfigureAwait(false);
                manifest = ReadApiManifest(body);
            }

            token.ThrowIfCancellationRequested();
            var available = ReadManifest(manifest);
            if (available.CompareTo(current) <= 0)
                return new(RevisionUpdateCheckState.NoUpdate, null, "");
            return new(RevisionUpdateCheckState.Available, new(available.ToString()), "");
        }
        catch (OperationCanceledException)
        {
            return cancellationToken.IsCancellationRequested || _shutdown.IsCancellationRequested
                ? new(RevisionUpdateCheckState.Cancelled, null, "更新检查已取消。")
                : new(RevisionUpdateCheckState.Failed, null, "更新检查超时。");
        }
        catch (Exception ex)
        {
            var error = ex switch
            {
                InvalidDataException => ex.Message,
                JsonException => "更新清单不是有效的 JSON。",
                HttpRequestException => "无法连接更新服务器。",
                IOException => "读取更新清单失败。",
                ObjectDisposedException => "更新检查已结束。",
                _ => "检查更新失败。"
            };
            return new(RevisionUpdateCheckState.Failed, null, error);
        }
    }

    private async Task<byte[]> ReadEndpointAsync(string url, int limit, bool api, CancellationToken token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.UserAgent.ParseAdd(ProductName);
        if (api) request.Headers.Accept.ParseAdd("application/vnd.github.raw+json");
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
        if (response.StatusCode != HttpStatusCode.OK)
            throw new HttpRequestException("更新服务器返回异常状态。", null, response.StatusCode);
        if (response.Content.Headers.ContentLength > limit) throw new InvalidDataException("更新清单超过允许大小。");
        if (response.Content.Headers.ContentEncoding.Count != 0) throw new InvalidDataException("更新清单使用了不支持的压缩格式。");
        using var stream = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
        using var buffer = new MemoryStream();
        var block = new byte[8192];
        while (true)
        {
            var count = await stream.ReadAsync(block.AsMemory(), token).ConfigureAwait(false);
            if (count == 0) break;
            if (buffer.Length + count > limit) throw new InvalidDataException("更新清单超过允许大小。");
            buffer.Write(block, 0, count);
        }
        return buffer.ToArray();
    }

    private static byte[] ReadApiManifest(byte[] body)
    {
        using var document = JsonDocument.Parse(body, new JsonDocumentOptions { MaxDepth = 16 });
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object) throw new InvalidDataException("更新清单格式无效。");
        // GitHub honors the raw Accept header, or can return the contents API JSON envelope.
        if (!root.TryGetProperty("encoding", out _) && !root.TryGetProperty("content", out _))
        {
            if (body.Length > MaximumManifestBytes) throw new InvalidDataException("更新清单超过允许大小。");
            return body;
        }
        if (RequiredString(root, "encoding") != "base64") throw new InvalidDataException("更新清单编码无效。");
        var content = RequiredString(root, "content");
        byte[] decoded;
        try { decoded = Convert.FromBase64String(content); }
        catch (FormatException) { throw new InvalidDataException("更新清单编码无效。"); }
        if (decoded.Length > MaximumManifestBytes) throw new InvalidDataException("更新清单超过允许大小。");
        return decoded;
    }

    internal static RevisionSemanticVersion ReadManifest(byte[] bytes)
    {
        using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 16 });
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object || RequiredString(root, "name") != ProductName)
            throw new InvalidDataException("更新清单的产品名称不匹配。");
        if (!RevisionSemanticVersion.TryParse(RequiredString(root, "version_number"), out var version))
            throw new InvalidDataException("更新清单的版本号无效。");
        return version;
    }

    private static string RequiredString(JsonElement root, string key)
    {
        string? value = null;
        foreach (var property in root.EnumerateObject())
        {
            if (property.Name != key) continue;
            if (value != null || property.Value.ValueKind != JsonValueKind.String)
                throw new InvalidDataException("更新清单的必需字段重复或无效。");
            value = property.Value.GetString();
        }
        return value ?? throw new InvalidDataException("更新清单缺少必需字段。");
    }

    internal static HttpMessageHandler CreateHandler() => new SocketsHttpHandler
    {
        AllowAutoRedirect = false,
        AutomaticDecompression = DecompressionMethods.None,
        ConnectTimeout = TimeSpan.FromSeconds(10),
        PooledConnectionLifetime = TimeSpan.FromMinutes(5)
    };

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _shutdown.Cancel();
            _http.Dispose();
            // CheckCoreAsync may still observe this token; leave the small CTS alive for it.
        }
    }
}

// SemVer precedence with two permitted display shorthands: v/V prefix and omitted patch.
// Core and prerelease numbers compare by digits, without integer overflow or lexical 1.10 bugs.
internal readonly struct RevisionSemanticVersion : IComparable<RevisionSemanticVersion>
{
    private readonly string? _major, _minor, _patch, _normalized;
    private readonly string[]? _prerelease;
    private RevisionSemanticVersion(string major, string minor, string patch, string[] prerelease, string normalized)
    { _major = major; _minor = minor; _patch = patch; _prerelease = prerelease; _normalized = normalized; }

    internal static bool TryParse(string? value, out RevisionSemanticVersion version)
    {
        version = default;
        if (string.IsNullOrEmpty(value) || value.Length > 256) return false;
        if (value[0] == 'v' || value[0] == 'V') value = value.Substring(1);
        var buildAt = value.IndexOf('+');
        var build = buildAt < 0 ? "" : value.Substring(buildAt + 1);
        if (buildAt >= 0)
        {
            if (!ValidIdentifiers(build, false)) return false;
            value = value.Substring(0, buildAt);
        }
        var prereleaseAt = value.IndexOf('-');
        var prerelease = prereleaseAt < 0 ? "" : value.Substring(prereleaseAt + 1);
        if (prereleaseAt >= 0)
        {
            if (!ValidIdentifiers(prerelease, true)) return false;
            value = value.Substring(0, prereleaseAt);
        }
        var core = value.Split('.');
        if (core.Length != 2 && core.Length != 3) return false;
        foreach (var part in core)
            if (!IsDigits(part) || (part.Length > 1 && part[0] == '0')) return false;
        var patch = core.Length == 3 ? core[2] : "0";
        var normalized = core[0] + "." + core[1] + "." + patch;
        if (prereleaseAt >= 0) normalized += "-" + prerelease;
        if (buildAt >= 0) normalized += "+" + build;
        version = new(core[0], core[1], patch, prereleaseAt < 0 ? Array.Empty<string>() : prerelease.Split('.'), normalized);
        return true;
    }

    private static bool ValidIdentifiers(string value, bool rejectNumericLeadingZero)
    {
        foreach (var part in value.Split('.'))
        {
            if (part.Length == 0) return false;
            foreach (var c in part)
                if (!(c >= '0' && c <= '9') && !(c >= 'A' && c <= 'Z') && !(c >= 'a' && c <= 'z') && c != '-') return false;
            if (rejectNumericLeadingZero && part.Length > 1 && part[0] == '0' && IsDigits(part)) return false;
        }
        return true;
    }
    private static bool IsDigits(string value)
    {
        if (value.Length == 0) return false;
        foreach (var c in value) if (c < '0' || c > '9') return false;
        return true;
    }
    private static int CompareNumber(string left, string right)
    {
        var length = left.Length.CompareTo(right.Length);
        return length != 0 ? length : string.CompareOrdinal(left, right);
    }
    public int CompareTo(RevisionSemanticVersion other)
    {
        var result = CompareNumber(_major ?? "0", other._major ?? "0"); if (result != 0) return result;
        result = CompareNumber(_minor ?? "0", other._minor ?? "0"); if (result != 0) return result;
        result = CompareNumber(_patch ?? "0", other._patch ?? "0"); if (result != 0) return result;
        var left = _prerelease ?? Array.Empty<string>();
        var right = other._prerelease ?? Array.Empty<string>();
        if (left.Length == 0 || right.Length == 0) return (left.Length == 0 ? 1 : 0).CompareTo(right.Length == 0 ? 1 : 0);
        for (var i = 0; i < Math.Min(left.Length, right.Length); i++)
        {
            var leftNumeric = IsDigits(left[i]); var rightNumeric = IsDigits(right[i]);
            result = leftNumeric && rightNumeric ? CompareNumber(left[i], right[i])
                : leftNumeric != rightNumeric ? (leftNumeric ? -1 : 1) : string.CompareOrdinal(left[i], right[i]);
            if (result != 0) return result;
        }
        return left.Length.CompareTo(right.Length);
    }
    public override string ToString() => _normalized ?? "";
}
