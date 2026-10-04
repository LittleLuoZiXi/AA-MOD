using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace AzureArchive.Recorder;

internal sealed record RecorderUpdateInfo
{
    public int SchemaVersion { get; init; }
    public string Product { get; init; } = "";
    public string Version { get; init; } = "";
    public string MinimumVersion { get; init; } = "";
    public string HostSha256 { get; init; } = "";
    public string BaselinePath { get; init; } = "";
    public string DownloadUrl { get; init; } = "";
    public string Sha256 { get; init; } = "";
    public long Size { get; init; }
}

internal enum RecorderUpdateCheckState { NoUpdate, Available, Failed, Cancelled }
internal sealed record RecorderUpdateCheckResult(RecorderUpdateCheckState State, RecorderUpdateInfo? Update, string Error);

// No Unity, renderer, recording, or GPU dependencies. The only production feed is fixed here.
internal sealed class RecorderUpdateClient : IDisposable
{
    internal const string FeedUrl = "https://raw.githubusercontent.com/LittleLuoZiXi/AA-MOD/refs/heads/mods/recorder/updates/recorder/stable.json";
    private const int MaximumFeedBytes = 65536;
    private readonly HttpClient _http;
    private readonly TimeSpan _timeout;

    internal RecorderUpdateClient() : this(RecorderUpdateNetwork.CreateHandler(), TimeSpan.FromSeconds(10)) { }
    // Handler injection is used by local deterministic tests; it cannot change the production source.
    internal RecorderUpdateClient(HttpMessageHandler testHandler, TimeSpan timeout)
    {
        _http = new HttpClient(testHandler, true) { Timeout = Timeout.InfiniteTimeSpan };
        _timeout = timeout;
    }

    internal async Task<RecorderUpdateCheckResult> CheckAsync(string currentVersion, string hostSha256, CancellationToken cancellationToken)
    {
        try
        {
            if (!RecorderNumericVersion.TryParse(currentVersion, out var current) || !RecorderUpdateNetwork.IsSha256(hostSha256))
                throw new InvalidDataException("当前内录版本或本体校验值无效，已跳过更新检查。");
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(_timeout);
            using var request = new HttpRequestMessage(HttpMethod.Get, FeedUrl);
            request.Headers.UserAgent.ParseAdd("AzureArchiveRecorder/1.2.4");
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
            if (response.StatusCode != HttpStatusCode.OK)
                throw new InvalidDataException("更新服务器返回异常状态（" + (int)response.StatusCode + "），请稍后重试。");
            if (response.Content.Headers.ContentLength > MaximumFeedBytes)
                throw new InvalidDataException("更新信息超过允许大小。");
            if (response.Content.Headers.ContentEncoding.Count != 0)
                throw new InvalidDataException("更新服务器返回了不支持的压缩内容。");
            using var stream = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
            using var buffer = new MemoryStream();
            var block = new byte[8192];
            while (true)
            {
                var count = await stream.ReadAsync(block.AsMemory(), timeout.Token).ConfigureAwait(false);
                if (count == 0) break;
                if (buffer.Length + count > MaximumFeedBytes) throw new InvalidDataException("更新信息超过允许大小。");
                buffer.Write(block, 0, count);
            }
            var info = JsonSerializer.Deserialize<RecorderUpdateInfo>(buffer.ToArray(), new JsonSerializerOptions { PropertyNameCaseInsensitive = true, MaxDepth = 16 })
                ?? throw new InvalidDataException("更新信息为空。");
            RecorderUpdateNetwork.ValidateInfo(info);
            RecorderNumericVersion.TryParse(info.Version, out var available);
            RecorderNumericVersion.TryParse(info.MinimumVersion, out var minimum);
            if (available.CompareTo(current) <= 0 || minimum.CompareTo(current) > 0 ||
                !string.Equals(info.HostSha256, hostSha256, StringComparison.OrdinalIgnoreCase))
                return new(RecorderUpdateCheckState.NoUpdate, null, "");
            return new(RecorderUpdateCheckState.Available, info, "");
        }
        catch (OperationCanceledException)
        {
            return cancellationToken.IsCancellationRequested
                ? new(RecorderUpdateCheckState.Cancelled, null, "更新检查已取消。")
                : new(RecorderUpdateCheckState.Failed, null, "更新检查超时，请稍后重试。");
        }
        catch (Exception ex)
        {
            return new(RecorderUpdateCheckState.Failed, null, RecorderUpdateNetwork.Explain(ex, "检查更新"));
        }
    }

    public void Dispose() => _http.Dispose();
}

internal readonly struct RecorderNumericVersion : IComparable<RecorderNumericVersion>
{
    private readonly int _major, _minor, _patch, _revision;
    private RecorderNumericVersion(int major, int minor, int patch, int revision)
    { _major = major; _minor = minor; _patch = patch; _revision = revision; }

    // Reject labels, signs, whitespace, omitted components, overflows and ambiguous leading zeroes.
    internal static bool TryParse(string? value, out RecorderNumericVersion version)
    {
        version = default;
        if (value == null || value.Length > 43) return false;
        var parts = value.Split('.');
        if (parts.Length != 3 && parts.Length != 4) return false;
        var numbers = new int[4];
        for (var i = 0; i < parts.Length; i++)
        {
            if (parts[i].Length == 0 || (parts[i].Length > 1 && parts[i][0] == '0')) return false;
            long number = 0;
            foreach (var digit in parts[i])
            {
                if (digit < '0' || digit > '9') return false;
                number = number * 10 + digit - '0';
                if (number > int.MaxValue) return false;
            }
            numbers[i] = (int)number;
        }
        version = new(numbers[0], numbers[1], numbers[2], numbers[3]);
        return true;
    }
    public int CompareTo(RecorderNumericVersion other)
    {
        var result = _major.CompareTo(other._major); if (result != 0) return result;
        result = _minor.CompareTo(other._minor); if (result != 0) return result;
        result = _patch.CompareTo(other._patch); if (result != 0) return result;
        return _revision.CompareTo(other._revision);
    }
}

internal static class RecorderUpdateNetwork
{
    internal const long MaximumPackageBytes = 256L * 1024 * 1024;
    internal static HttpMessageHandler CreateHandler() => new SocketsHttpHandler
    {
        AllowAutoRedirect = false,
        AutomaticDecompression = DecompressionMethods.None,
        ConnectTimeout = TimeSpan.FromSeconds(10),
        PooledConnectionLifetime = TimeSpan.FromMinutes(5)
        // Certificate and hostname validation use the platform TLS defaults.
    };

    internal static bool IsSha256(string? value)
    {
        if (value == null || value.Length != 64) return false;
        foreach (var character in value)
            if (!(character >= '0' && character <= '9') && !(character >= 'a' && character <= 'f') && !(character >= 'A' && character <= 'F')) return false;
        return true;
    }

    internal static bool IsReleaseUrl(Uri uri)
    {
        if (!IsHttps(uri) || !string.Equals(uri.Host, "github.com", StringComparison.OrdinalIgnoreCase) || uri.Query.Length != 0) return false;
        var segments = uri.AbsolutePath.Split('/');
        return segments.Length == 7 && segments[1].Equals("LittleLuoZiXi", StringComparison.OrdinalIgnoreCase) &&
            segments[2].Equals("AA-MOD", StringComparison.OrdinalIgnoreCase) && segments[3] == "releases" && segments[4] == "download" &&
            SafeSegment(segments[5]) && SafeSegment(segments[6]);
    }
    private static bool SafeSegment(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return false;
        var decoded = Uri.UnescapeDataString(value);
        return decoded != "." && decoded != ".." && decoded.IndexOfAny(new[] { '/', '\\', '\0', '\r', '\n' }) < 0;
    }
    private static bool IsHttps(Uri uri) => uri.IsAbsoluteUri && uri.Scheme == Uri.UriSchemeHttps && uri.Port == 443 && uri.UserInfo.Length == 0 && uri.Fragment.Length == 0;
    internal static bool IsDownloadRedirect(Uri uri) => IsReleaseUrl(uri) ||
        (IsHttps(uri) && string.Equals(uri.Host, "release-assets.githubusercontent.com", StringComparison.OrdinalIgnoreCase) &&
         uri.AbsolutePath.StartsWith("/github-production-release-asset/", StringComparison.Ordinal));

    internal static void ValidateInfo(RecorderUpdateInfo info)
    {
        if (info.SchemaVersion != 1 || info.Product != "AzureArchiveRecorder") throw new InvalidDataException("更新信息的格式或产品不匹配。");
        if (!RecorderNumericVersion.TryParse(info.Version, out var version) || !RecorderNumericVersion.TryParse(info.MinimumVersion, out var minimum) || minimum.CompareTo(version) > 0)
            throw new InvalidDataException("更新版本号或最低兼容版本无效。");
        if (!IsSha256(info.HostSha256) || !IsSha256(info.Sha256)) throw new InvalidDataException("更新校验值无效。");
        if (info.Size <= 0 || info.Size > MaximumPackageBytes) throw new InvalidDataException("更新包大小不在允许范围内。");
        if (!Uri.TryCreate(info.DownloadUrl, UriKind.Absolute, out var uri) || !IsReleaseUrl(uri)) throw new InvalidDataException("更新包下载地址不在允许的官方仓库内。");
        if (string.IsNullOrWhiteSpace(info.BaselinePath) || info.BaselinePath.Length > 200 || info.BaselinePath.StartsWith("/") ||
            info.BaselinePath.IndexOfAny(new[] { '\\', ':', '\0' }) >= 0)
            throw new InvalidDataException("更新包基线路径无效。");
        foreach (var part in info.BaselinePath.Split('/'))
            if (part.Length == 0 || part == "." || part == "..") throw new InvalidDataException("更新包基线路径无效。");
    }

    internal static string Explain(Exception exception, string action)
    {
        if (exception is InvalidDataException) return exception.Message;
        if (exception is HttpRequestException) return action + "失败，无法连接更新服务器，请检查网络后重试。";
        if (exception is UnauthorizedAccessException) return action + "失败，无法写入临时目录，请检查文件权限。";
        if (exception is IOException) return action + "失败，临时文件读写异常或网络连接中断，请稍后重试。";
        if (exception is JsonException) return "更新信息不是有效的 JSON 格式。";
        if (exception is ObjectDisposedException) return action + "已结束。";
        return action + "失败，请稍后重试。";
    }
}
