using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace AzureArchive.RevisionCompare;

// Every request uses a stub transport. Running these checks cannot contact GitHub or AA.
public static class RevisionUpdateClientTests
{
    public static string Run() => RunAsync().GetAwaiter().GetResult();

    private static async Task<string> RunAsync()
    {
        var checks = new List<object>();
        var passed = true;
        async Task Check(string name, Func<Task> test)
        {
            try { await test().ConfigureAwait(false); checks.Add(new { name, passed = true, error = "" }); }
            catch (Exception ex) { passed = false; checks.Add(new { name, passed = false, error = ex.Message }); }
        }
        await Check("higher version offers only fixed branch URL", async () =>
        {
            using var handler = new StubHandler((_, _) => Reply(Manifest("1.1.0", "https://example.invalid/untrusted")));
            using var client = Client(handler);
            var result = await client.CheckAsync(CancellationToken.None);
            Assert(result.State == RevisionUpdateCheckState.Available && result.Update?.Version == "1.1.0", "higher version missing");
            Assert(result.Update!.Url == RevisionUpdateClient.BranchUrl, "manifest controlled navigation target");
            Assert(handler.Count == 1 && handler.Uris[0] == RevisionUpdateClient.FeedUrl, "unexpected raw request");
        });
        await Check("first updater release V1.1 stays quiet on V1.1 feed", () => Expect("1.1.0", "1.1.0", RevisionUpdateCheckState.NoUpdate));
        await Check("V1.1 accepts future V1.2 feed and direct download", async () =>
        {
            var fixture = Payload("1.2.0");
            using var handler = new StubHandler((request, _) => Reply(request.RequestUri!.AbsoluteUri == RevisionUpdateClient.FeedUrl
                ? Manifest("1.2.0") : JsonSerializer.Serialize(fixture.Descriptor)));
            using var client = Client(handler, "1.1.0");
            var available = await client.CheckAsync(CancellationToken.None);
            Assert(available.State == RevisionUpdateCheckState.Available && available.Update?.Version == "1.2.0", "future version not offered");
            var info = await client.GetDownloadInfoAsync(available.Update!, CancellationToken.None);
            Assert(info.State == RevisionUpdateDescriptorState.Ready && info.Descriptor != null, info.Error);
            using var root = new TestDirectory();
            using var download = new RevisionUpdateDownload(info.Descriptor!, PayloadHandler(fixture), root.Path);
            var result = await download.DownloadAsync(CancellationToken.None);
            Assert(result.State == RevisionDownloadState.Completed && result.Update?.Version == "1.2.0", result.Error);
        });
        await Check("V1.0 and 1.0.0 are equal", () => Expect("V1.0", "1.0.0", RevisionUpdateCheckState.NoUpdate));
        await Check("build metadata does not create an update", () => Expect("1.0.0+new.01", "1.0.0+old", RevisionUpdateCheckState.NoUpdate));
        await Check("older release never offers downgrade", () => Expect("0.99.99", "1.0.0", RevisionUpdateCheckState.NoUpdate));
        await Check("numeric minor version 1.10 exceeds 1.9", () => Expect("1.10.0", "1.9.0", RevisionUpdateCheckState.Available));
        await Check("stable exceeds same-core prerelease", () => Expect("1.0.0", "1.0.0-rc.10", RevisionUpdateCheckState.Available));
        await Check("same-core prerelease cannot replace stable", () => Expect("1.0.0-rc.10", "1.0.0", RevisionUpdateCheckState.NoUpdate));
        await Check("higher-core prerelease follows semver precedence", () => Expect("1.1.0-alpha", "1.0.0", RevisionUpdateCheckState.Available));
        await Check("semver precedence and arbitrary numeric width", () =>
        {
            var sequence = new[] { "1.0.0-alpha", "1.0.0-alpha.1", "1.0.0-alpha.beta", "1.0.0-beta", "1.0.0-beta.2", "1.0.0-beta.11", "1.0.0-rc.1", "1.0.0", "1.0.1", "1.9.0", "1.10.0", "999999999999999999999.0.0" };
            for (var i = 1; i < sequence.Length; i++)
            {
                Assert(RevisionSemanticVersion.TryParse(sequence[i - 1], out var left), "parse left");
                Assert(RevisionSemanticVersion.TryParse(sequence[i], out var right), "parse right");
                Assert(left.CompareTo(right) < 0 && right.CompareTo(left) > 0, sequence[i]);
            }
            return Task.CompletedTask;
        });
        await Check("ambiguous or malformed versions are rejected", () =>
        {
            foreach (var text in new[] { "", "1", " 1.0.0", "1.0.0 ", "1.0.0.0", "01.0.0", "1.00.0", "1.0.01", "1.-1.0", "1.0.0-", "1.0.0-a..b", "1.0.0-01", "1.0.0-a.01", "1.0.0-中文", "1.0.0+", "1.0.0+a+b", "1.0.0+abc_def", new string('1', 257) })
                Assert(!RevisionSemanticVersion.TryParse(text, out _), "accepted " + text);
            Assert(RevisionSemanticVersion.TryParse("v1.2-alpha.0+build.01", out var normalized) && normalized.ToString() == "1.2.0-alpha.0+build.01", "valid shorthand normalization");
            return Task.CompletedTask;
        });
        await Check("invalid current version skips network", async () =>
        {
            using var handler = new StubHandler((_, _) => Reply(Manifest("2.0.0")));
            using var client = Client(handler, "invalid");
            Assert((await client.CheckAsync(CancellationToken.None)).State == RevisionUpdateCheckState.Failed && handler.Count == 0, "invalid current version issued request");
        });
        await Check("invalid manifests never offer an update", async () =>
        {
            foreach (var body in new[] {
                "null", "[]", "{}", "not json",
                "{\"name\":\"azurearchiverevisioncompare\",\"version_number\":\"9.0.0\"}",
                "{\"name\":\"AzureArchiveRevisionCompare\",\"version_number\":9}",
                "{\"name\":\"AzureArchiveRevisionCompare\",\"version_number\":\"9\"}",
                "{\"name\":\"AzureArchiveRevisionCompare\",\"name\":\"AzureArchiveRevisionCompare\",\"version_number\":\"9.0.0\"}",
                "{\"name\":\"AzureArchiveRevisionCompare\",\"version_number\":\"9.0.0\",\"version_number\":\"9.0.0\"}" })
            {
                using var handler = new StubHandler((_, _) => Reply(body));
                using var client = Client(handler);
                var result = await client.CheckAsync(CancellationToken.None);
                Assert(result.State == RevisionUpdateCheckState.Failed && result.Update == null, "accepted " + body);
            }
        });
        await Check("raw unavailable falls back to fixed API raw response", async () =>
        {
            using var handler = new StubHandler((request, _) =>
            {
                if (request.RequestUri!.Host == "raw.githubusercontent.com") return Reply("", HttpStatusCode.NotFound);
                Assert(request.RequestUri.OriginalString == RevisionUpdateClient.ContentsApiUrl, "changed API endpoint");
                Assert(request.Headers.Accept.ToString() == "application/vnd.github.raw+json", "missing raw Accept");
                Assert(request.Headers.UserAgent.ToString() == RevisionUpdateClient.ProductName, "missing user agent");
                return Reply(Manifest("V1.1"));
            });
            using var client = Client(handler);
            var result = await client.CheckAsync(CancellationToken.None);
            Assert(result.State == RevisionUpdateCheckState.Available && result.Update?.Version == "1.1.0" && handler.Count == 2, "fallback failed");
        });
        await Check("API base64 envelope is supported", async () =>
        {
            using var handler = new StubHandler((request, _) => request.RequestUri!.Host == "raw.githubusercontent.com"
                ? Task.FromException<HttpResponseMessage>(new HttpRequestException("offline"))
                : Reply(JsonSerializer.Serialize(new { encoding = "base64", content = Convert.ToBase64String(Encoding.UTF8.GetBytes(Manifest("2.0.0")), Base64FormattingOptions.InsertLineBreaks) })));
            using var client = Client(handler);
            Assert((await client.CheckAsync(CancellationToken.None)).State == RevisionUpdateCheckState.Available && handler.Count == 2, "base64 fallback failed");
        });
        await Check("invalid API envelopes and oversized decoded body are rejected", async () =>
        {
            foreach (var body in new[] {
                "{\"encoding\":\"base64\",\"content\":\"???\"}",
                "{\"encoding\":\"other\",\"content\":\"\"}",
                "{\"encoding\":\"base64\",\"encoding\":\"base64\",\"content\":\"\"}",
                JsonSerializer.Serialize(new { encoding = "base64", content = Convert.ToBase64String(new byte[RevisionUpdateClient.MaximumManifestBytes + 1]) }) })
            {
                using var handler = new StubHandler((request, _) => request.RequestUri!.Host == "raw.githubusercontent.com" ? Reply("", HttpStatusCode.NotFound) : Reply(body));
                using var client = Client(handler);
                Assert((await client.CheckAsync(CancellationToken.None)).State == RevisionUpdateCheckState.Failed, "bad envelope accepted");
            }
        });
        await Check("offline ends after two fixed attempts", async () =>
        {
            using var handler = new StubHandler((_, _) => Task.FromException<HttpResponseMessage>(new HttpRequestException("private network error detail")));
            using var client = Client(handler);
            var result = await client.CheckAsync(CancellationToken.None);
            Assert(result.State == RevisionUpdateCheckState.Failed && result.Update == null && handler.Count == 2, "offline result");
            Assert(!result.Error.Contains("private"), "network detail leaked");
        });
        await Check("redirect or non-success status cannot announce a version", async () =>
        {
            using var handler = new StubHandler((_, _) => Reply(Manifest("9.0.0"), HttpStatusCode.Redirect));
            using var client = Client(handler);
            Assert((await client.CheckAsync(CancellationToken.None)).State == RevisionUpdateCheckState.Failed && handler.Count == 2, "redirect accepted");
        });
        await Check("oversized known-length and streamed responses are bounded", async () =>
        {
            foreach (var chunked in new[] { false, true })
            {
                using var handler = new StubHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = chunked ? new StreamContent(new NonSeekableMemoryStream(new byte[140000])) : new ByteArrayContent(new byte[140000])
                }));
                using var client = Client(handler);
                var result = await client.CheckAsync(CancellationToken.None);
                Assert(result.State == RevisionUpdateCheckState.Failed && result.Update == null, "oversized response accepted");
            }
        });
        await Check("compressed responses are rejected", async () =>
        {
            using var handler = new StubHandler((_, _) =>
            {
                var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(Manifest("2.0.0")) };
                response.Content.Headers.ContentEncoding.Add("gzip");
                return Task.FromResult(response);
            });
            using var client = Client(handler);
            Assert((await client.CheckAsync(CancellationToken.None)).State == RevisionUpdateCheckState.Failed, "compression accepted");
        });
        await Check("timeout during request reports failure without retry", async () =>
        {
            using var handler = new StubHandler(async (_, token) => { await Task.Delay(Timeout.Infinite, token); return new HttpResponseMessage(); });
            using var client = new RevisionUpdateClient(handler, TimeSpan.FromMilliseconds(50), "1.0.0");
            var result = await client.CheckAsync(CancellationToken.None);
            Assert(result.State == RevisionUpdateCheckState.Failed && result.Error.Contains("超时") && handler.Count == 1, "timeout result");
        });
        await Check("timeout also covers body streaming", async () =>
        {
            using var handler = new StubHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new WaitingStream()) }));
            using var client = new RevisionUpdateClient(handler, TimeSpan.FromMilliseconds(50), "1.0.0");
            var result = await client.CheckAsync(CancellationToken.None);
            Assert(result.State == RevisionUpdateCheckState.Failed && result.Error.Contains("超时") && handler.Count == 1, "body timeout result");
        });
        await Check("cancelled before startup sends no request", async () =>
        {
            using var handler = new StubHandler((_, _) => Reply(Manifest("2.0.0")));
            using var client = Client(handler);
            using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
            Assert((await client.CheckAsync(cancellation.Token)).State == RevisionUpdateCheckState.Cancelled && handler.Count == 0, "pre-cancel result");
        });
        await Check("external cancellation cancels pending check", async () =>
        {
            using var handler = new StubHandler(async (_, token) => { await Task.Delay(Timeout.Infinite, token); return new HttpResponseMessage(); });
            using var client = Client(handler);
            using var cancellation = new CancellationTokenSource();
            var pending = client.CheckAsync(cancellation.Token); cancellation.Cancel();
            Assert((await pending).State == RevisionUpdateCheckState.Cancelled && handler.Count == 1, "cancel result");
        });
        await Check("concurrent and later calls reuse one startup task", async () =>
        {
            var ready = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
            using var handler = new StubHandler((_, _) => ready.Task);
            using var client = Client(handler);
            var first = client.CheckAsync(CancellationToken.None);
            var second = client.CheckAsync(CancellationToken.None);
            Assert(ReferenceEquals(first, second) && handler.Count == 1, "concurrent duplicate request");
            ready.SetResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(Manifest("2.0.0")) });
            await first;
            Assert(ReferenceEquals(first, client.CheckAsync(CancellationToken.None)) && handler.Count == 1, "rechecked completed check");
        });
        await Check("failed startup check is not polled again", async () =>
        {
            using var handler = new StubHandler((_, _) => Reply("", HttpStatusCode.ServiceUnavailable));
            using var client = Client(handler);
            var first = client.CheckAsync(CancellationToken.None); await first;
            Assert(ReferenceEquals(first, client.CheckAsync(CancellationToken.None)) && handler.Count == 2, "failed check restarted");
        });
        await Check("dispose cancels in-flight check and is idempotent", async () =>
        {
            using var handler = new StubHandler(async (_, token) => { await Task.Delay(Timeout.Infinite, token); return new HttpResponseMessage(); });
            var client = Client(handler);
            var pending = client.CheckAsync(CancellationToken.None); client.Dispose(); client.Dispose();
            Assert((await pending).State == RevisionUpdateCheckState.Cancelled && handler.Count == 1, "dispose did not cancel");
        });
        await Check("disposed client never starts a request", async () =>
        {
            using var handler = new StubHandler((_, _) => Reply(Manifest("2.0.0")));
            var client = Client(handler); client.Dispose();
            Assert((await client.CheckAsync(CancellationToken.None)).State == RevisionUpdateCheckState.Cancelled && handler.Count == 0, "disposed client requested");
        });
        await Check("download descriptor validates offered product/version and fixed files", async () =>
        {
            var fixture = Payload();
            using var handler = new StubHandler((_, _) => Reply(JsonSerializer.Serialize(fixture.Descriptor)));
            using var client = Client(handler);
            var result = await client.GetDownloadInfoAsync(new("1.1.0"), CancellationToken.None);
            Assert(result.State == RevisionUpdateDescriptorState.Ready && result.Descriptor?.Files.Count == 2 && handler.Uris[0] == RevisionUpdateClient.DescriptorUrl, "descriptor rejected");
        });
        await Check("source-only release is unavailable, never downloaded successfully", async () =>
        {
            using var handler = new StubHandler((_, _) => Reply("", HttpStatusCode.NotFound));
            using var client = Client(handler);
            var result = await client.GetDownloadInfoAsync(new("1.1.0"), CancellationToken.None);
            Assert(result.State == RevisionUpdateDescriptorState.Unavailable && result.Descriptor == null && result.Error.Contains("尚未提供") && handler.Count == 2, "source-only release claimed ready");
        });
        await Check("same or lower offered version never fetches download metadata", async () =>
        {
            using var handler = new StubHandler((_, _) => Reply(""));
            using var client = Client(handler);
            foreach (var version in new[] { "0.9.0", "V1.0", "1.0.0+metadata", "1.0.0-rc.1" })
                Assert((await client.GetDownloadInfoAsync(new(version), CancellationToken.None)).State == RevisionUpdateDescriptorState.Unavailable, "downgrade offered");
            Assert(handler.Count == 0, "same/older network request");
        });
        await Check("download metadata may not move past startup offer", async () =>
        {
            using var handler = new StubHandler((_, _) => Reply(JsonSerializer.Serialize(Payload("1.2.0").Descriptor)));
            using var client = Client(handler);
            Assert((await client.GetDownloadInfoAsync(new("1.1.0"), CancellationToken.None)).State == RevisionUpdateDescriptorState.Failed, "changed version accepted");
        });
        await Check("descriptor API fallback supports matching base64 source", async () =>
        {
            using var handler = new StubHandler((request, _) => request.RequestUri!.OriginalString == RevisionUpdateClient.DescriptorUrl
                ? Reply("", HttpStatusCode.NotFound)
                : Reply(JsonSerializer.Serialize(new { encoding = "base64", content = Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(Payload().Descriptor)) })));
            using var client = Client(handler);
            Assert((await client.GetDownloadInfoAsync(new("1.1.0"), CancellationToken.None)).State == RevisionUpdateDescriptorState.Ready, "descriptor fallback failed");
            Assert(handler.Uris[1] == RevisionUpdateClient.DescriptorApiUrl, "descriptor source changed");
        });
        await Check("descriptor rejects malicious paths, sources, hashes and lengths before writing", () =>
        {
            var good = Payload().Descriptor;
            var first = good.Files[0];
            var invalid = new List<RevisionUpdateDescriptor>
            {
                good with { Product = "Other" }, good with { SchemaVersion = 2 }, good with { Version = "invalid" },
                good with { Files = new[] { first } }, good with { Files = new[] { first, first } }
            };
            foreach (var file in new[] {
                first with { Name = "../victim.dll" }, first with { Name = "C:\\victim.dll" }, first with { Name = "archive.zip" },
                first with { Url = "https://example.invalid/AzureArchive.RevisionCompare.dll" },
                first with { Url = first.Url.Replace("mods/revision-compare", "main") },
                first with { Url = first.Url + "?redirect=other" }, first with { Url = first.Url.Replace("files/1.1.0/", "files/1.2.0/") },
                first with { Url = first.Url.Replace("files/1.1.0/", "files/1.1.0/../1.1.0/") },
                first with { Url = "https://github.com/LittleLuoZiXi/AA-MOD/releases/download/v1.1.0/" + first.Name },
                first with { Size = 0 }, first with { Size = RevisionUpdateDownload.MaximumAssemblyBytes + 1 }, first with { Sha256 = "not-hash" } })
                invalid.Add(good with { Files = new[] { file, good.Files[1] } });
            foreach (var descriptor in invalid)
            {
                bool rejected = false;
                try { RevisionUpdateDownload.ValidateDescriptor(descriptor); } catch (InvalidDataException) { rejected = true; }
                Assert(rejected, "unsafe descriptor accepted");
            }
            var json = JsonSerializer.Serialize(good).Replace("\"SchemaVersion\":1", "\"SchemaVersion\":1,\"SchemaVersion\":1");
            bool duplicateRejected = false;
            try { RevisionUpdateDownload.ParseDescriptor(Encoding.UTF8.GetBytes(json)); } catch (InvalidDataException) { duplicateRejected = true; }
            Assert(duplicateRejected, "duplicate metadata accepted");
            return Task.CompletedTask;
        });
        await Check("download writes only verified direct files and reports progress", async () =>
        {
            using var root = new TestDirectory();
            var sentinel = Path.Combine(root.Path, "unrelated.txt"); File.WriteAllText(sentinel, "keep");
            var fixture = Payload();
            using var handler = PayloadHandler(fixture);
            var downloader = new RevisionUpdateDownload(fixture.Descriptor, handler, root.Path);
            var pending = downloader.DownloadAsync(CancellationToken.None);
            Assert(ReferenceEquals(pending, downloader.DownloadAsync(CancellationToken.None)), "duplicate download task");
            var result = await pending;
            Assert(result.State == RevisionDownloadState.Completed && result.Update != null, result.Error);
            Assert(File.ReadAllBytes(result.Update!.AssemblyPath).SequenceEqual(fixture.Bodies[RevisionUpdateDownload.AssemblyName]), "DLL content changed");
            Assert(File.ReadAllBytes(result.Update.ManifestPath).SequenceEqual(fixture.Bodies[RevisionUpdateDownload.ManifestName]), "manifest content changed");
            Assert(Directory.GetFiles(result.Update.Directory).Length == 2 && !Directory.GetFiles(result.Update.Directory).Any(path => path.EndsWith(".partial")), "non-atomic output left behind");
            Assert(downloader.Snapshot.Bytes == downloader.Snapshot.Total && downloader.Snapshot.State == RevisionDownloadState.Completed, "progress incomplete");
            downloader.Dispose();
            Assert(!Directory.Exists(result.Update.Directory) && File.ReadAllText(sentinel) == "keep", "dispose touched unrelated file or retained payload");
            Assert((await downloader.DownloadAsync(CancellationToken.None)).State == RevisionDownloadState.Cancelled, "disposed job returned deleted success");
        });
        await Check("download descriptor is isolated from caller list mutations", async () =>
        {
            using var root = new TestDirectory();
            var fixture = Payload();
            var files = fixture.Descriptor.Files.ToArray();
            using var handler = PayloadHandler(fixture);
            using var downloader = new RevisionUpdateDownload(fixture.Descriptor with { Files = files }, handler, root.Path);
            files[0] = files[0] with { Url = "https://example.invalid/evil" };
            Assert((await downloader.DownloadAsync(CancellationToken.None)).State == RevisionDownloadState.Completed, "caller mutation changed task");
        });
        await Check("download API fallback accepts raw and base64 bodies from same path/ref", async () =>
        {
            foreach (var envelope in new[] { false, true })
            {
                using var root = new TestDirectory();
                var fixture = Payload();
                using var handler = new StubHandler((request, _) =>
                {
                    if (request.RequestUri!.Host == "raw.githubusercontent.com") return Reply("", HttpStatusCode.NotFound);
                    Assert(request.RequestUri.Query == "?ref=mods%2Frevision-compare", "API ref changed");
                    var name = System.IO.Path.GetFileName(request.RequestUri.AbsolutePath);
                    return envelope ? Reply(JsonSerializer.Serialize(new { encoding = "base64", content = Convert.ToBase64String(fixture.Bodies[name]) }))
                        : Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(fixture.Bodies[name]) });
                });
                using var downloader = new RevisionUpdateDownload(fixture.Descriptor, handler, root.Path);
                var result = await downloader.DownloadAsync(CancellationToken.None);
                Assert(result.State == RevisionDownloadState.Completed && handler.Count == 4, result.Error);
            }
        });
        await Check("hash mismatch, short body and streamed oversize clean all owned files", async () =>
        {
            foreach (var kind in new[] { "hash", "short", "oversize" })
            {
                using var root = new TestDirectory();
                var fixture = Payload();
                using var handler = new StubHandler((request, _) =>
                {
                    var name = System.IO.Path.GetFileName(request.RequestUri!.AbsolutePath);
                    var body = fixture.Bodies[name].ToArray();
                    if (name == RevisionUpdateDownload.ManifestName)
                    {
                        if (kind == "hash") body[0] ^= 1;
                        if (kind == "short") body = body.Take(body.Length - 1).ToArray();
                        if (kind == "oversize") body = body.Concat(new byte[] { 0 }).ToArray();
                    }
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new NonSeekableMemoryStream(body)) });
                });
                using var downloader = new RevisionUpdateDownload(fixture.Descriptor, handler, root.Path);
                var result = await downloader.DownloadAsync(CancellationToken.None);
                Assert(result.State == RevisionDownloadState.Failed && result.Update == null && Directory.GetDirectories(root.Path).Length == 0, "failed payload retained: " + kind);
            }
        });
        await Check("correctly hashed payload manifest must still match product and version", async () =>
        {
            foreach (var invalidManifest in new[] { Manifest("1.0.0"), "{\"name\":\"Other\",\"version_number\":\"1.1.0\"}" })
            {
                using var root = new TestDirectory();
                var fixture = Payload(manifestOverride: invalidManifest);
                using var handler = PayloadHandler(fixture);
                using var downloader = new RevisionUpdateDownload(fixture.Descriptor, handler, root.Path);
                Assert((await downloader.DownloadAsync(CancellationToken.None)).State == RevisionDownloadState.Failed && Directory.GetDirectories(root.Path).Length == 0, "mismatched manifest accepted");
            }
        });
        await Check("download refuses redirects without requesting their target", async () =>
        {
            using var root = new TestDirectory();
            using var handler = new StubHandler((_, _) =>
            {
                var response = new HttpResponseMessage(HttpStatusCode.Redirect); response.Headers.Location = new Uri("https://example.invalid/evil");
                return Task.FromResult(response);
            });
            using var downloader = new RevisionUpdateDownload(Payload().Descriptor, handler, root.Path);
            Assert((await downloader.DownloadAsync(CancellationToken.None)).State == RevisionDownloadState.Failed && handler.Count == 2 && Directory.GetDirectories(root.Path).Length == 0, "redirect followed");
        });
        await Check("download cancellation and timeout remove partial staging", async () =>
        {
            foreach (var cancel in new[] { true, false })
            {
                using var root = new TestDirectory();
                var started = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                using var cancellation = new CancellationTokenSource();
                using var handler = new StubHandler((_, _) =>
                {
                    started.TrySetResult(true);
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new WaitingStream()) });
                });
                using var downloader = new RevisionUpdateDownload(Payload().Descriptor, handler, root.Path, cancel ? TimeSpan.FromSeconds(2) : TimeSpan.FromMilliseconds(50));
                var pending = downloader.DownloadAsync(cancellation.Token);
                await started.Task; if (cancel) cancellation.Cancel();
                var result = await pending;
                Assert(result.State == (cancel ? RevisionDownloadState.Cancelled : RevisionDownloadState.Failed) && Directory.GetDirectories(root.Path).Length == 0, "cancel/timeout staging retained");
                if (!cancel) Assert(result.Error.Contains("超时"), "timeout hidden");
            }
        });
        return JsonSerializer.Serialize(new { passed, count = checks.Count, checks }, new JsonSerializerOptions { WriteIndented = true });
    }

    private static RevisionUpdateClient Client(HttpMessageHandler handler, string current = "1.0.0") => new(handler, TimeSpan.FromSeconds(2), current);
    private static string Manifest(string version, string url = "") => JsonSerializer.Serialize(new { name = RevisionUpdateClient.ProductName, version_number = version, website_url = url });
    private static Task<HttpResponseMessage> Reply(string body, HttpStatusCode status = HttpStatusCode.OK) => Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") });
    private static async Task Expect(string available, string current, RevisionUpdateCheckState state)
    {
        using var handler = new StubHandler((_, _) => Reply(Manifest(available)));
        using var client = Client(handler, current);
        var result = await client.CheckAsync(CancellationToken.None);
        Assert(result.State == state && (state == RevisionUpdateCheckState.Available || result.Update == null), "unexpected state " + result.State);
    }
    private static void Assert(bool value, string message) { if (!value) throw new InvalidOperationException(message); }

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> _reply;
        internal int Count;
        internal readonly List<string> Uris = new();
        internal StubHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> reply) => _reply = reply;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var uri = request.RequestUri!.OriginalString;
            Assert(uri == RevisionUpdateClient.FeedUrl || uri == RevisionUpdateClient.ContentsApiUrl || uri == RevisionUpdateClient.DescriptorUrl || uri == RevisionUpdateClient.DescriptorApiUrl ||
                uri.StartsWith("https://raw.githubusercontent.com/LittleLuoZiXi/AA-MOD/refs/heads/mods/revision-compare/updates/revision-compare/files/", StringComparison.Ordinal) ||
                uri.StartsWith("https://api.github.com/repos/LittleLuoZiXi/AA-MOD/contents/updates/revision-compare/files/", StringComparison.Ordinal), "unapproved endpoint");
            Interlocked.Increment(ref Count); Uris.Add(uri);
            return _reply(request, cancellationToken);
        }
    }
    private static (RevisionUpdateDescriptor Descriptor, Dictionary<string, byte[]> Bodies) Payload(string version = "1.1.0", string? manifestOverride = null)
    {
        var bodies = new Dictionary<string, byte[]>
        {
            [RevisionUpdateDownload.AssemblyName] = Encoding.UTF8.GetBytes("MZ synthetic DLL bytes; never executed"),
            [RevisionUpdateDownload.ManifestName] = Encoding.UTF8.GetBytes(manifestOverride ?? Manifest(version))
        };
        var files = bodies.Select(pair => new RevisionUpdateFile(pair.Key,
            "https://raw.githubusercontent.com/LittleLuoZiXi/AA-MOD/refs/heads/mods/revision-compare/updates/revision-compare/files/" + version + "/" + pair.Key,
            Convert.ToHexString(SHA256.HashData(pair.Value)), pair.Value.LongLength)).ToArray();
        return (new() { SchemaVersion = 1, Product = RevisionUpdateClient.ProductName, Version = version, Files = files }, bodies);
    }
    private static StubHandler PayloadHandler((RevisionUpdateDescriptor Descriptor, Dictionary<string, byte[]> Bodies) fixture) =>
        new((request, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(fixture.Bodies[System.IO.Path.GetFileName(request.RequestUri!.AbsolutePath)]) }));
    private sealed class TestDirectory : IDisposable
    {
        internal string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "RevisionCompareDownloadTests", Guid.NewGuid().ToString("N"));
        internal TestDirectory() => Directory.CreateDirectory(Path);
        public void Dispose()
        {
            // These tests create only one level of fresh GUID staging directories. Never recurse.
            foreach (var directory in Directory.GetDirectories(Path))
            { foreach (var file in Directory.GetFiles(directory)) File.Delete(file); Directory.Delete(directory, false); }
            foreach (var file in Directory.GetFiles(Path)) File.Delete(file);
            Directory.Delete(Path, false);
        }
    }
    private sealed class NonSeekableMemoryStream : MemoryStream
    {
        internal NonSeekableMemoryStream(byte[] bytes) : base(bytes) { }
        public override bool CanSeek => false;
    }
    private sealed class WaitingStream : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        { await Task.Delay(Timeout.Infinite, cancellationToken); return 0; }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
