using System.Net;
using System.Text.Json;
using Netclaw.Cli.Update;
using Netclaw.Configuration.Feeds;
using Netclaw.Configuration.Security;
using NSec.Cryptography;
using Xunit;

namespace Netclaw.Cli.Tests.Cli;

[Collection("Update verification")]
public sealed class StatusUpdateCheckerTests : IDisposable
{
    private readonly Key _testSigningKey;
    private readonly byte[] _testPublicKeyBlob;

    public StatusUpdateCheckerTests()
    {
        _testSigningKey = Key.Create(SignatureAlgorithm.Ed25519,
            new KeyCreationParameters { ExportPolicy = KeyExportPolicies.AllowPlaintextExport });

        var pubKeyRaw = _testSigningKey.Export(KeyBlobFormat.RawPublicKey);
        _testPublicKeyBlob = new byte[42];
        _testPublicKeyBlob[0] = 0x45; _testPublicKeyBlob[1] = 0x64; // "Ed"
        byte[] testKeyId = [0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07, 0x08];
        Array.Copy(testKeyId, 0, _testPublicKeyBlob, 2, 8);
        Array.Copy(pubKeyRaw, 0, _testPublicKeyBlob, 10, 32);

        MinisignVerifier.TestPublicKeyOverride = _testPublicKeyBlob;
        UpdateCheckService.ResetCache();
    }

    public void Dispose()
    {
        MinisignVerifier.TestPublicKeyOverride = null;
        UpdateCheckService.ResetCache();
        _testSigningKey.Dispose();
    }

    [Fact]
    public async Task CheckAsync_ReturnsUpdateAvailable_WhenNewerVersionExists()
    {
        var manifest = CreateManifest("0.9.0", UpdateCheckService.GetCurrentRid());
        var handler = CreateSignedHandler(manifest);

        using var httpClient = new HttpClient(handler);
        var result = await StatusUpdateChecker.CheckAsync(httpClient, "0.1.0", TestContext.Current.CancellationToken);

        Assert.Equal("update-available", result.State);
        Assert.Equal("0.1.0", result.CurrentVersion);
        Assert.Equal("0.9.0", result.LatestVersion);
    }

    [Fact]
    public async Task CheckAsync_ReturnsUpToDate_WhenAlreadyLatest()
    {
        var manifest = CreateManifest("0.1.0", UpdateCheckService.GetCurrentRid());
        var handler = CreateSignedHandler(manifest);

        using var httpClient = new HttpClient(handler);
        var result = await StatusUpdateChecker.CheckAsync(httpClient, "0.1.0", TestContext.Current.CancellationToken);

        Assert.Equal("up-to-date", result.State);
        Assert.Null(result.LatestVersion);
    }

    [Fact]
    public async Task CheckAsync_ReturnsUnknown_OnNetworkError()
    {
        var handler = new FakeHttpHandler();
        handler.AddErrorResponse(FeedConstants.BinaryManifestUrl, HttpStatusCode.ServiceUnavailable);

        using var httpClient = new HttpClient(handler);
        var result = await StatusUpdateChecker.CheckAsync(httpClient, "0.1.0", TestContext.Current.CancellationToken);

        Assert.Equal("unknown", result.State);
        Assert.Equal("0.1.0", result.CurrentVersion);
        Assert.Null(result.LatestVersion);
    }

    [Fact]
    public async Task CheckAsync_ReturnsUnknown_OnTimeout()
    {
        var handler = new SlowHttpHandler(); // blocks until cancellation, triggering the 200ms timeout

        using var httpClient = new HttpClient(handler);
        var result = await StatusUpdateChecker.CheckAsync(
            httpClient, "0.1.0", timeout: TimeSpan.FromMilliseconds(200), cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal("unknown", result.State);
        Assert.Equal("0.1.0", result.CurrentVersion);
    }

    [Fact]
    public async Task CheckAsync_IncludesReleaseNotesUrl_WhenUpdateAvailable()
    {
        var manifest = CreateManifest("0.9.0", UpdateCheckService.GetCurrentRid(),
            releaseNotesUrl: "https://github.com/stannardlabs/netclaw/releases/tag/0.9.0");
        var handler = CreateSignedHandler(manifest);

        using var httpClient = new HttpClient(handler);
        var result = await StatusUpdateChecker.CheckAsync(httpClient, "0.1.0", TestContext.Current.CancellationToken);

        Assert.Equal("update-available", result.State);
        Assert.Equal("https://github.com/stannardlabs/netclaw/releases/tag/0.9.0", result.ReleaseNotesUrl);
    }

    // ─── Helpers ────────────────────────────────────────────────────────────────

    private FakeHttpHandler CreateSignedHandler(BinaryFeedManifest manifest)
    {
        var handler = new FakeHttpHandler();
        var json = JsonSerializer.Serialize(manifest);
        handler.AddResponse(FeedConstants.BinaryManifestUrl, HttpStatusCode.OK, json, "application/json");

        var sigContent = SignContent(json);
        handler.AddResponse(FeedConstants.BinaryManifestSignatureUrl, HttpStatusCode.OK, sigContent, "text/plain");

        return handler;
    }

    private string SignContent(string content)
    {
        var data = System.Text.Encoding.UTF8.GetBytes(content);
        var signature = SignatureAlgorithm.Ed25519.Sign(_testSigningKey, data);

        var sigBlob = new byte[74];
        sigBlob[0] = 0x45; sigBlob[1] = 0x44; // "ED"
        Array.Copy(_testPublicKeyBlob, 2, sigBlob, 2, 8);
        Array.Copy(signature, 0, sigBlob, 10, 64);

        return $"untrusted comment: test signature\n{Convert.ToBase64String(sigBlob)}\ntrusted comment: test\ndGVzdA==\n";
    }

    private static BinaryFeedManifest CreateManifest(string version, string rid, string? releaseNotesUrl = null)
    {
        return new BinaryFeedManifest
        {
            Latest = version,
            UpdatedAt = DateTimeOffset.UtcNow,
            Releases =
            [
                new BinaryRelease
                {
                    Version = version,
                    ReleasedAt = DateTimeOffset.UtcNow,
                    ReleaseNotesUrl = releaseNotesUrl,
                    Assets =
                    [
                        new BinaryAsset
                        {
                            Component = "netclaw",
                            Rid = rid,
                            Url = $"https://releases.netclaw.dev/{version}/netclaw-{version}-{rid}.tar.gz",
                            Sha256 = "abc123",
                            SizeBytes = 50_000_000
                        }
                    ]
                }
            ]
        };
    }

    private sealed class FakeHttpHandler : HttpMessageHandler
    {
        private readonly Dictionary<string, (HttpStatusCode Status, string Content, string ContentType)> _responses = new();

        public void AddJsonResponse<T>(string url, T body)
        {
            var json = JsonSerializer.Serialize(body);
            _responses[url] = (HttpStatusCode.OK, json, "application/json");
        }

        public void AddResponse(string url, HttpStatusCode status, string content, string contentType)
        {
            _responses[url] = (status, content, contentType);
        }

        public void AddErrorResponse(string url, HttpStatusCode status)
        {
            _responses[url] = (status, string.Empty, "text/plain");
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var url = request.RequestUri!.ToString();
            if (!_responses.TryGetValue(url, out var entry))
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));

            var response = new HttpResponseMessage(entry.Status)
            {
                Content = new StringContent(entry.Content, System.Text.Encoding.UTF8, entry.ContentType)
            };
            return Task.FromResult(response);
        }
    }

    /// <summary>
    /// Blocks the HTTP request until the cancellation token fires (simulating a slow/hung server).
    /// </summary>
    private sealed class SlowHttpHandler : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var tcs = new TaskCompletionSource<HttpResponseMessage>();
            using var reg = cancellationToken.Register(
                () => tcs.TrySetCanceled(cancellationToken));
            return await tcs.Task;
        }
    }
}
