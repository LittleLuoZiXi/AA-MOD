using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace AzureArchive.RevisionCompare;

// Uses the real client/download code. Live mode only reads the published feed;
// it never installs a MOD, launches AA, or announces a synthetic remote version.
public static class ReleaseFeedTests
{
    public static string Run(string project, bool live) => VerifyAsync(project, live).GetAwaiter().GetResult();

    private static async Task<string> VerifyAsync(string project, bool live)
    {
        var results = new List<string>();
        var manifestBytes = File.ReadAllBytes(Path.Combine(project, "manifest.json"));
        using var manifest = JsonDocument.Parse(manifestBytes);
        var version = manifest.RootElement.GetProperty("version_number").GetString()!;
        var descriptorBytes = File.ReadAllBytes(Path.Combine(project, "updates/revision-compare/stable.json"));
        var localDescriptor = RevisionUpdateDownload.ParseDescriptor(descriptorBytes);
        Require(localDescriptor.Version == version, "Local descriptor version differs from root manifest");
        var responses = new Dictionary<string, byte[]>
        {
            [RevisionUpdateClient.FeedUrl] = manifestBytes,
            [RevisionUpdateClient.DescriptorUrl] = descriptorBytes
        };
        foreach (var file in localDescriptor.Files)
            responses[file.Url] = File.ReadAllBytes(Path.Combine(project, "updates/revision-compare/files", version, file.Name));
        HttpMessageHandler Transport() => live ? RevisionUpdateClient.CreateHandler() : new LocalHandler(responses);
        using var client = new RevisionUpdateClient(Transport(), TimeSpan.FromSeconds(10), version);
        var check = await client.CheckAsync(CancellationToken.None).ConfigureAwait(false);
        Require(check.State == RevisionUpdateCheckState.NoUpdate, "Same version must remain quiet: " + check.State + " " + check.Error);
        results.Add("Installed release stays quiet on the same remote version");
        using var http = new HttpClient(Transport()) { Timeout = TimeSpan.FromSeconds(20) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd(RevisionUpdateClient.ProductName);
        var publishedManifest = await http.GetByteArrayAsync(RevisionUpdateClient.FeedUrl).ConfigureAwait(false);
        Require(publishedManifest.SequenceEqual(manifestBytes), "Published root manifest differs from the staged release");
        var received = await http.GetByteArrayAsync(RevisionUpdateClient.DescriptorUrl).ConfigureAwait(false);
        var descriptor = RevisionUpdateDownload.ParseDescriptor(received);
        Require(received.SequenceEqual(descriptorBytes), "Published stable.json differs from the staged release");
        Require(descriptor.Version == version, "Published descriptor does not match the release");
        results.Add("Descriptor schema, version, fixed branch paths and staged bytes match");
        using var downloader = new RevisionUpdateDownload(descriptor, Transport(), Path.Combine(project, "evidence/release-feed-downloads"), TimeSpan.FromSeconds(40));
        var downloaded = await downloader.DownloadAsync(CancellationToken.None).ConfigureAwait(false);
        Require(downloaded.State == RevisionDownloadState.Completed && downloaded.Update != null, downloaded.Error);
        foreach (var file in descriptor.Files)
            Require(File.ReadAllBytes(Path.Combine(downloaded.Update!.Directory, file.Name)).SequenceEqual(responses[file.Url]), "Downloaded release bytes differ: " + file.Name);
        results.Add("Both real release payloads download and pass size, SHA256 and manifest validation");
        return JsonSerializer.Serialize(new { passed = true, live, version, checks = results, installed = false });
    }

    private static void Require(bool condition, string error) { if (!condition) throw new Exception(error); }
    private sealed class LocalHandler : HttpMessageHandler
    {
        private readonly Dictionary<string, byte[]> _responses;
        internal LocalHandler(Dictionary<string, byte[]> responses) { _responses = responses; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if (!_responses.TryGetValue(request.RequestUri!.AbsoluteUri, out var bytes)) throw new InvalidOperationException("Unexpected endpoint: " + request.RequestUri);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) });
        }
    }
}
