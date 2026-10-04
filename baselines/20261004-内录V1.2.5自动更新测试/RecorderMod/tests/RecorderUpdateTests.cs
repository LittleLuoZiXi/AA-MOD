using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AzureArchive.Recorder;

public static class RecorderUpdateTests
{
    private static int _checks;
    private static readonly byte[] Payload = Enumerable.Range(0, 24000).Select(i => (byte)(i % 251)).ToArray();
    private const string HostHash = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA";
    private static RecorderUpdateInfo Info => new()
    {
        SchemaVersion = 1, Product = "AzureArchiveRecorder", Version = "1.2.4", MinimumVersion = "1.2.3",
        HostSha256 = HostHash, BaselinePath = "BepInEx/plugins/AzureArchive.Recorder.dll",
        DownloadUrl = "https://github.com/LittleLuoZiXi/AA-MOD/releases/download/recorder-v1.2.4/recorder.zip",
        Sha256 = Convert.ToHexString(SHA256.HashData(Payload)), Size = Payload.Length
    };
    private static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception("FAIL: " + message);
        _checks++; Console.WriteLine("PASS: " + message);
    }
    public static void Run(string root) => RunAsync(root).GetAwaiter().GetResult();
    public static int Main(string[] args) { Run(args[0]); return 0; }
    private static async Task RunAsync(string root)
    {
        Directory.CreateDirectory(root);
        VersionTests();
        await FeedTests();
        await FixedJsonContractTest();
        await FullDownload(root);
        await ResumeTest(root, "206", HttpStatusCode.PartialContent, false, false, true, false);
        await ResumeTest(root, "200-restart", HttpStatusCode.OK, false, false, true, false);
        await ResumeTest(root, "416-rejected", HttpStatusCode.RequestedRangeNotSatisfiable, false, false, true, false);
        await ResumeTest(root, "bad-range", HttpStatusCode.PartialContent, true, false, true, false);
        await ResumeTest(root, "bad-etag", HttpStatusCode.PartialContent, false, true, true, false);
        await ResumeTest(root, "no-etag-restart", HttpStatusCode.OK, false, false, false, false);
        await ResumeTest(root, "weak-etag-restart", HttpStatusCode.OK, false, false, false, false, true);
        await ResumeTest(root, "rapid-resume", HttpStatusCode.PartialContent, false, false, true, true);
        await FailureTests(root);
        await ShortReadResume(root);
        await CancellationTests(root);
        await RedirectTests(root);
        Console.WriteLine("Recorder update checks passed: " + _checks);
    }
    private static void VersionTests()
    {
        RecorderNumericVersion.TryParse("1.10.0", out var a); RecorderNumericVersion.TryParse("1.9.99", out var b);
        Check(a.CompareTo(b) > 0, "Versions use numeric ordering across multi-digit components");
        RecorderNumericVersion.TryParse("1.2.3", out a); RecorderNumericVersion.TryParse("1.2.3.0", out b);
        Check(a.CompareTo(b) == 0, "Missing fourth numeric component equals zero");
        RecorderNumericVersion.TryParse("1.2.3.1", out b); Check(b.CompareTo(a) > 0, "Fourth component orders numerically");
        foreach (var invalid in new[] { "1.2", "1.2.3.4.5", "01.2.3", "1.02.3", "1.2.3-beta", "v1.2.3", " 1.2.3", "1.2.3 ", "1.2.-3", "1.2.+3", "1.2.2147483648", "1.2.9999999999999999999999999", "1..3", "１.2.3" })
            Check(!RecorderNumericVersion.TryParse(invalid, out _), "Reject malformed version: " + invalid);
        Check(RecorderNumericVersion.TryParse("2147483647.0.0", out _), "Largest supported component is accepted");
        foreach (var badUrl in new[] { "http://github.com/LittleLuoZiXi/AA-MOD/releases/download/v1/x.zip", "https://github.com/other/AA-MOD/releases/download/v1/x.zip", "https://github.com/LittleLuoZiXi/other/releases/download/v1/x.zip", "https://github.com.evil.test/LittleLuoZiXi/AA-MOD/releases/download/v1/x.zip", "https://name@github.com/LittleLuoZiXi/AA-MOD/releases/download/v1/x.zip", "https://github.com:444/LittleLuoZiXi/AA-MOD/releases/download/v1/x.zip", "https://github.com/LittleLuoZiXi/AA-MOD/releases/download/v1/x.zip?redirect=evil", "https://github.com/LittleLuoZiXi/AA-MOD/releases/download/v1/a%2fb.zip" })
            Check(!RecorderUpdateNetwork.IsReleaseUrl(new Uri(badUrl)), "Reject noncanonical download URL: " + badUrl);
    }
    private static async Task<RecorderUpdateCheckResult> Feed(RecorderUpdateInfo info, string version = "1.2.3", string host = HostHash)
    {
        using var client = new RecorderUpdateClient(new Handler((req, _) =>
        {
            Check(req.RequestUri!.AbsoluteUri == RecorderUpdateClient.FeedUrl, "Feed source remains fixed under test injection");
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(info)) });
        }), TimeSpan.FromSeconds(2));
        return await client.CheckAsync(version, host, CancellationToken.None);
    }
    private static async Task FixedJsonContractTest()
    {
        using var encoded = JsonDocument.Parse(JsonSerializer.Serialize(Info));
        var expected = new[] { "SchemaVersion", "Product", "Version", "MinimumVersion", "HostSha256", "BaselinePath", "DownloadUrl", "Sha256", "Size" }.OrderBy(name => name).ToArray();
        Check(encoded.RootElement.EnumerateObject().Select(property => property.Name).OrderBy(name => name).SequenceEqual(expected), "Serialized feed field names retain the external JSON contract after protection");
        var json = "{\"SchemaVersion\":1,\"Product\":\"AzureArchiveRecorder\",\"Version\":\"1.2.4\",\"MinimumVersion\":\"1.2.3\",\"HostSha256\":\"" + HostHash +
            "\",\"BaselinePath\":\"mods/AzureArchiveRecorder/1.2.4/manifest.json\",\"DownloadUrl\":\"https://github.com/LittleLuoZiXi/AA-MOD/releases/download/recorder-v1.2.4/recorder.zip\",\"Sha256\":\"" + Info.Sha256 + "\",\"Size\":" + Payload.Length + "}";
        using var client = new RecorderUpdateClient(new Handler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json) })), TimeSpan.FromSeconds(2));
        var result = await client.CheckAsync("1.2.3", HostHash, CancellationToken.None);
        Check(result.State == RecorderUpdateCheckState.Available && result.Update?.Version == "1.2.4", "Literal external JSON is deserialized correctly after protection without relying on the same serializer");
    }
    private static async Task FeedTests()
    {
        Check((await Feed(Info)).State == RecorderUpdateCheckState.Available, "Compatible newer version is offered");
        Check((await Feed(Info, "1.2.4")).State == RecorderUpdateCheckState.NoUpdate, "Equal version is not offered");
        Check((await Feed(Info, "1.10.0")).State == RecorderUpdateCheckState.NoUpdate, "Downgrade is not offered");
        Check((await Feed(Info, "1.2.2")).State == RecorderUpdateCheckState.NoUpdate, "Minimum version gate rejects incompatible installations");
        Check((await Feed(Info, "1.2.3", new string('B', 64))).State == RecorderUpdateCheckState.NoUpdate, "Host hash gate rejects incompatible game binaries");
        Check((await Feed(Info with { SchemaVersion = 2 })).State == RecorderUpdateCheckState.Failed, "Unknown feed schema rejected");
        Check((await Feed(Info with { Product = "OtherProduct" })).State == RecorderUpdateCheckState.Failed, "Wrong product rejected");
        Check((await Feed(Info with { Size = RecorderUpdateNetwork.MaximumPackageBytes + 1 })).State == RecorderUpdateCheckState.Failed, "Package size cap enforced");
        Check((await Feed(Info with { BaselinePath = "../outside.dll" })).State == RecorderUpdateCheckState.Failed, "Traversal baseline path rejected");
        using var oversized = new RecorderUpdateClient(new Handler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new StreamContent(new NonSeekStream(new byte[65537])) })), TimeSpan.FromSeconds(2));
        Check((await oversized.CheckAsync("1.2.3", HostHash, CancellationToken.None)).State == RecorderUpdateCheckState.Failed, "Streaming oversized feed rejected without Content-Length");
        using var timeout = new RecorderUpdateClient(new Handler(async (_, token) => { await Task.Delay(Timeout.Infinite, token); return new HttpResponseMessage(); }), TimeSpan.FromMilliseconds(30));
        var timed = await timeout.CheckAsync("1.2.3", HostHash, CancellationToken.None);
        Check(timed.State == RecorderUpdateCheckState.Failed && timed.Error.Contains("超时"), "Timeout returns readable failure without throwing");
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        Check((await timeout.CheckAsync("1.2.3", HostHash, cancellation.Token)).State == RecorderUpdateCheckState.Cancelled, "Cancelled checks return cancellation state");
    }
    private static HttpResponseMessage Response(HttpStatusCode status, byte[] body, long? from = null, string? etag = "\"stable-v1\"", bool knownLength = true)
    {
        var response = new HttpResponseMessage(status) { Content = knownLength ? new ByteArrayContent(body) : new StreamContent(new NonSeekStream(body)) };
        if (etag != null) response.Headers.ETag = EntityTagHeaderValue.Parse(etag);
        if (from.HasValue) response.Content.Headers.ContentRange = new ContentRangeHeaderValue(from.Value, Payload.Length - 1, Payload.Length);
        return response;
    }
    private static async Task WaitState(RecorderUpdateDownload job, params RecorderDownloadState[] states)
    {
        var until = DateTime.UtcNow.AddSeconds(5);
        while (!states.Contains(job.Snapshot.State))
        {
            if (DateTime.UtcNow > until) throw new Exception("Timed out waiting for state: " + job.Snapshot);
            await Task.Delay(5);
        }
    }
    private static async Task WaitBytes(RecorderUpdateDownload job, long minimum)
    {
        var until = DateTime.UtcNow.AddSeconds(5);
        while (job.Snapshot.Bytes < minimum)
        { if (DateTime.UtcNow > until) throw new Exception("Timed out waiting for bytes: " + job.Snapshot); await Task.Delay(5); }
    }
    private static async Task FullDownload(string root)
    {
        using var job = new RecorderUpdateDownload(Info, new Handler((_, _) => Task.FromResult(Response(HttpStatusCode.OK, Payload))), Path.Combine(root, "full"));
        Parallel.For(0, 8, _ => job.Start());
        await WaitState(job, RecorderDownloadState.Completed);
        var snapshot = job.Snapshot;
        Check(snapshot.Bytes == Payload.Length && snapshot.Total == Payload.Length, "Completed download exposes exact progress");
        Check(snapshot.VerifiedFilePath != null && File.ReadAllBytes(snapshot.VerifiedFilePath).SequenceEqual(Payload), "Only verified complete file is exposed");
        Check(snapshot.State == RecorderDownloadState.Completed, "Snapshot is immutable");
    }
    private static async Task ResumeTest(string root, string name, HttpStatusCode resumeStatus, bool badRange, bool badEtag, bool initialEtag, bool quickResume, bool weakInitialEtag = false)
    {
        var calls = 0;
        var rangeObserved = false;
        var ifRangeObserved = false;
        var handler = new Handler((request, _) =>
        {
            if (Interlocked.Increment(ref calls) == 1)
            {
                var first = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new BlockingStream(Payload.Take(4096).ToArray())) };
                first.Content.Headers.ContentLength = Payload.Length;
                if (initialEtag) first.Headers.ETag = EntityTagHeaderValue.Parse("\"stable-v1\"");
                else if (weakInitialEtag) first.Headers.ETag = EntityTagHeaderValue.Parse("W/\"stable-v1\"");
                return Task.FromResult(first);
            }
            rangeObserved = request.Headers.Range?.Ranges.Single().From == 4096;
            ifRangeObserved = request.Headers.IfRange?.EntityTag?.ToString() == "\"stable-v1\"";
            return Task.FromResult(resumeStatus == HttpStatusCode.OK ? Response(resumeStatus, Payload, null, "\"changed-v2\"") :
                Response(resumeStatus, Payload.Skip(4096).ToArray(), badRange ? 4095 : 4096, badEtag ? "\"changed-v2\"" : "\"stable-v1\""));
        });
        using var job = new RecorderUpdateDownload(Info, handler, Path.Combine(root, name));
        job.Start(); await WaitBytes(job, 4096);
        job.Pause();
        if (quickResume) job.Resume();
        else
        {
            await WaitState(job, RecorderDownloadState.Paused);
            var paused = job.Snapshot;
            Check(paused.Bytes == 4096 && paused.VerifiedFilePath == null, name + ": pause preserves partial bytes without exposing a package");
            job.Resume();
        }
        await WaitState(job, RecorderDownloadState.Completed, RecorderDownloadState.Failed);
        Check(rangeObserved == initialEtag && ifRangeObserved == initialEtag, name + ": continuation uses strong ETag If-Range or safely restarts");
        if (badRange || badEtag || resumeStatus == HttpStatusCode.RequestedRangeNotSatisfiable)
            Check(job.Snapshot.State == RecorderDownloadState.Failed && job.Snapshot.VerifiedFilePath == null, name + ": bad continuation never becomes complete");
        else
            Check(job.Snapshot.State == RecorderDownloadState.Completed && File.ReadAllBytes(job.Snapshot.VerifiedFilePath!).SequenceEqual(Payload), name + ": exact payload verified after continuation");
    }
    private static async Task ShortReadResume(string root)
    {
        var calls = 0;
        var rangeValid = false;
        using var job = new RecorderUpdateDownload(Info, new Handler((request, _) =>
        {
            if (++calls == 1) return Task.FromResult(Response(HttpStatusCode.OK, Payload.Take(10000).ToArray(), knownLength: false));
            rangeValid = request.Headers.Range?.Ranges.Single().From == 10000 && request.Headers.IfRange?.EntityTag?.ToString() == "\"stable-v1\"";
            return Task.FromResult(Response(HttpStatusCode.PartialContent, Payload.Skip(10000).ToArray(), 10000));
        }), Path.Combine(root, "short-resume"));
        job.Start(); await WaitState(job, RecorderDownloadState.Failed);
        Check(job.Snapshot.Bytes == 10000 && job.Snapshot.VerifiedFilePath == null, "Short read retains only the received partial bytes");
        job.Resume(); await WaitState(job, RecorderDownloadState.Completed, RecorderDownloadState.Failed);
        Check(rangeValid && job.Snapshot.State == RecorderDownloadState.Completed, "Interrupted short read can resume with exact Range and If-Range");
    }
    private static async Task FailureTests(string root)
    {
        foreach (var sample in new[] { "short", "long", "hash", "http", "length" })
        {
            var bytes = sample == "short" ? Payload.Take(10000).ToArray() : sample == "long" ? Payload.Concat(new byte[] { 42 }).ToArray() : Payload.ToArray();
            if (sample == "hash") bytes[0] ^= 0xff;
            var status = sample == "http" ? HttpStatusCode.ServiceUnavailable : HttpStatusCode.OK;
            using var job = new RecorderUpdateDownload(Info, new Handler((_, _) =>
            {
                var result = Response(status, bytes, knownLength: sample == "length");
                if (sample == "length") result.Content.Headers.ContentLength = Payload.Length + 10;
                return Task.FromResult(result);
            }), Path.Combine(root, sample));
            job.Start(); await WaitState(job, RecorderDownloadState.Failed);
            Check(job.Snapshot.VerifiedFilePath == null && job.Snapshot.Error.Length > 0, sample + ": rejected with readable failure and no verified file");
        }
    }
    private static async Task CancellationTests(string root)
    {
        var folder = Path.Combine(root, "cancel"); Directory.CreateDirectory(folder);
        var sibling = Path.Combine(folder, "unrelated.txt"); File.WriteAllText(sibling, "keep");
        using var job = new RecorderUpdateDownload(Info, new Handler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new StreamContent(new BlockingStream(Payload.Take(4096).ToArray())) })), folder);
        job.Start(); await WaitBytes(job, 4096);
        var ownDirectory = Directory.GetDirectories(folder).Single();
        var unexpected = Path.Combine(ownDirectory, "do-not-delete.txt"); File.WriteAllText(unexpected, "keep this too");
        job.Cancel(); await WaitState(job, RecorderDownloadState.Cancelled); await job.Completion;
        Check(job.Snapshot.VerifiedFilePath == null && !File.Exists(Path.Combine(ownDirectory, "update.partial")), "Cancel removes its own partial file");
        Check(File.ReadAllText(sibling) == "keep" && File.ReadAllText(unexpected) == "keep this too", "Cancel preserves unrelated files even inside its temporary directory");
        Check(job.Snapshot.Error.Length > 0, "Unexpected directory content reports cleanup limitation without recursive deletion");
        using var idle = new RecorderUpdateDownload(Info, new Handler((_, _) => throw new Exception("Network must not run")), Path.Combine(root, "cancel-idle"));
        idle.Cancel(); await idle.Completion;
        Check(idle.Snapshot.State == RecorderDownloadState.Cancelled && !Directory.Exists(Path.Combine(root, "cancel-idle")), "Cancel before start never creates files or performs network work");
    }
    private static async Task RedirectTests(string root)
    {
        foreach (var pair in new[] { ("official", "https://release-assets.githubusercontent.com/github-production-release-asset/123/asset?signature=test", true),
            ("external", "https://evil.test/asset.zip", false), ("http", "http://release-assets.githubusercontent.com/github-production-release-asset/123/asset", false) })
        {
            var calls = 0;
            using var job = new RecorderUpdateDownload(Info, new Handler((_, _) =>
            {
                if (++calls > 1) return Task.FromResult(Response(HttpStatusCode.OK, Payload));
                var redirect = new HttpResponseMessage(HttpStatusCode.Found); redirect.Headers.Location = new Uri(pair.Item2); return Task.FromResult(redirect);
            }), Path.Combine(root, "redirect-" + pair.Item1));
            job.Start(); await WaitState(job, RecorderDownloadState.Completed, RecorderDownloadState.Failed);
            Check((job.Snapshot.State == RecorderDownloadState.Completed) == pair.Item3 && calls == (pair.Item3 ? 2 : 1), "Redirect allowlist: " + pair.Item1);
        }
    }
    private sealed class Handler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> _send;
        internal Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) { _send = send; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => _send(request, cancellationToken);
    }
    private class NonSeekStream : Stream
    {
        protected readonly byte[] Bytes;
        protected int PositionValue;
        internal NonSeekStream(byte[] bytes) { Bytes = bytes; }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count)
        { var length = Math.Min(count, Bytes.Length - PositionValue); Array.Copy(Bytes, PositionValue, buffer, offset, length); PositionValue += length; return length; }
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        { cancellationToken.ThrowIfCancellationRequested(); var count = Math.Min(buffer.Length, Bytes.Length - PositionValue); Bytes.AsMemory(PositionValue, count).CopyTo(buffer); PositionValue += count; return new ValueTask<int>(count); }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
    private sealed class BlockingStream : NonSeekStream
    {
        internal BlockingStream(byte[] initialBytes) : base(initialBytes) { }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (PositionValue < Bytes.Length) return await base.ReadAsync(buffer, cancellationToken);
            await Task.Delay(Timeout.Infinite, cancellationToken); return 0;
        }
    }
}



