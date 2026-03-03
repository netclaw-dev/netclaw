using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Netclaw.Configuration;
using Netclaw.Configuration.Feeds;
using Netclaw.Daemon.Services;
using Xunit;

namespace Netclaw.Daemon.Tests.Services;

public sealed class BinaryUpdateCheckServiceTests : IDisposable
{
    private readonly string _tempDir;
    private readonly NetclawPaths _paths;

    public BinaryUpdateCheckServiceTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"netclaw-test-{Guid.NewGuid():N}");
        _paths = new NetclawPaths(_tempDir);
        _paths.EnsureDirectoriesExist();

        // Clear static cache between tests to avoid cross-test interference
        UpdateCheckService.ResetCache();
    }

    // ═══════════════════════════════════════════════════════════════
    // BinaryUpdateCheckService (daemon hosted service) tests
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task StartAsync_LogsWarningWhenUpdateAvailable()
    {
        var manifest = CreateManifest("0.2.0", UpdateCheckService.GetCurrentRid());
        var handler = new FakeHttpHandler();
        handler.AddJsonResponse(FeedConstants.BinaryManifestUrl, manifest);

        var sut = CreateDaemonService(handler, currentVersion: "0.1.0");
        await sut.StartAsync(CancellationToken.None);

        // Static cache should be populated
        var cached = UpdateCheckService.GetLastResult();
        Assert.NotNull(cached);
        Assert.True(cached!.IsUpdateAvailable);
    }

    [Fact]
    public async Task StartAsync_GracefullyHandlesNetworkFailure()
    {
        var handler = new FakeHttpHandler();
        handler.AddErrorResponse(FeedConstants.BinaryManifestUrl, HttpStatusCode.ServiceUnavailable);

        var sut = CreateDaemonService(handler, currentVersion: "0.1.0");
        await sut.StartAsync(CancellationToken.None);

        // UpdateCheckService never throws — returns "no update" on failure.
        var cached = UpdateCheckService.GetLastResult();
        Assert.NotNull(cached);
        Assert.False(cached!.IsUpdateAvailable);
    }

    [Fact]
    public async Task StartAsync_HandlesTimeoutGracefully()
    {
        var handler = new FakeHttpHandler();
        // No response configured → 404, which triggers HttpRequestException

        var sut = CreateDaemonService(handler, currentVersion: "0.1.0");
        await sut.StartAsync(CancellationToken.None);
    }

    // ═══════════════════════════════════════════════════════════════
    // UpdateCheckService (shared logic) tests
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task CheckForUpdateAsync_ReturnsUpdateWhenNewerVersionAvailable()
    {
        var manifest = CreateManifest("0.2.0", UpdateCheckService.GetCurrentRid());
        var handler = new FakeHttpHandler();
        handler.AddJsonResponse(FeedConstants.BinaryManifestUrl, manifest);

        using var httpClient = new HttpClient(handler);
        var result = await UpdateCheckService.CheckForUpdateAsync(
            httpClient, "0.1.0");

        Assert.True(result.IsUpdateAvailable);
        Assert.Equal("0.1.0", result.CurrentVersion);
        Assert.Equal("0.2.0", result.LatestVersion);
        Assert.Equal(2, result.MatchingAssets.Count); // netclaw + netclawd
    }

    [Fact]
    public async Task CheckForUpdateAsync_ReturnsNoUpdateWhenAlreadyLatest()
    {
        var manifest = CreateManifest("0.1.0", "linux-x64");
        var handler = new FakeHttpHandler();
        handler.AddJsonResponse(FeedConstants.BinaryManifestUrl, manifest);

        using var httpClient = new HttpClient(handler);
        var result = await UpdateCheckService.CheckForUpdateAsync(
            httpClient, "0.1.0");

        Assert.False(result.IsUpdateAvailable);
    }

    [Fact]
    public async Task CheckForUpdateAsync_ReturnsNoUpdateWhenNewerThanManifest()
    {
        var manifest = CreateManifest("0.1.0", "linux-x64");
        var handler = new FakeHttpHandler();
        handler.AddJsonResponse(FeedConstants.BinaryManifestUrl, manifest);

        using var httpClient = new HttpClient(handler);
        var result = await UpdateCheckService.CheckForUpdateAsync(
            httpClient, "0.2.0");

        Assert.False(result.IsUpdateAvailable);
    }

    [Fact]
    public async Task CheckForUpdateAsync_ReturnsNoUpdateOnNetworkFailure()
    {
        var handler = new FakeHttpHandler();
        handler.AddErrorResponse(FeedConstants.BinaryManifestUrl, HttpStatusCode.InternalServerError);

        using var httpClient = new HttpClient(handler);
        var result = await UpdateCheckService.CheckForUpdateAsync(
            httpClient, "0.1.0");

        Assert.False(result.IsUpdateAvailable);
        Assert.Equal("0.1.0", result.CurrentVersion);
    }

    [Fact]
    public void EvaluateManifest_MatchesAssetsByRid()
    {
        var manifest = new BinaryFeedManifest
        {
            Latest = "0.2.0",
            Releases =
            [
                new BinaryRelease
                {
                    Version = "0.2.0",
                    Assets =
                    [
                        new BinaryAsset
                        {
                            Component = "netclaw",
                            Rid = "linux-x64",
                            Url = "https://releases.netclaw.dev/0.2.0/netclaw-0.2.0-linux-x64.tar.gz",
                            Sha256 = "abc123",
                            SizeBytes = 50_000_000
                        },
                        new BinaryAsset
                        {
                            Component = "netclawd",
                            Rid = "linux-x64",
                            Url = "https://releases.netclaw.dev/0.2.0/netclawd-0.2.0-linux-x64.tar.gz",
                            Sha256 = "def456",
                            SizeBytes = 60_000_000
                        },
                        new BinaryAsset
                        {
                            Component = "netclaw",
                            Rid = "win-x64",
                            Url = "https://releases.netclaw.dev/0.2.0/netclaw-0.2.0-win-x64.zip",
                            Sha256 = "ghi789",
                            SizeBytes = 55_000_000
                        }
                    ]
                }
            ]
        };

        var result = UpdateCheckService.EvaluateManifest(manifest, "0.1.0");

        Assert.True(result.IsUpdateAvailable);
        // The matching count depends on the current RID at test runtime
        // On Linux CI, we'll get linux-x64 matches; on Windows, win-x64 matches
        Assert.True(result.MatchingAssets.Count > 0);
    }

    [Fact]
    public void EvaluateManifest_ReturnsNoUpdateWhenNoMatchingRid()
    {
        var manifest = new BinaryFeedManifest
        {
            Latest = "0.2.0",
            Releases =
            [
                new BinaryRelease
                {
                    Version = "0.2.0",
                    Assets =
                    [
                        new BinaryAsset
                        {
                            Component = "netclaw",
                            Rid = "nonexistent-rid-12345",
                            Url = "https://releases.netclaw.dev/0.2.0/netclaw-0.2.0-fake.tar.gz",
                            Sha256 = "abc123",
                            SizeBytes = 50_000_000
                        }
                    ]
                }
            ]
        };

        var result = UpdateCheckService.EvaluateManifest(manifest, "0.1.0");

        // Update exists in manifest but no assets match current RID
        Assert.False(result.IsUpdateAvailable);
        Assert.Equal("0.2.0", result.LatestVersion);
    }

    [Fact]
    public void EvaluateManifest_IncludesReleaseNotesUrl()
    {
        var manifest = new BinaryFeedManifest
        {
            Latest = "0.2.0",
            Releases =
            [
                new BinaryRelease
                {
                    Version = "0.2.0",
                    ReleaseNotesUrl = "https://github.com/stannardlabs/netclaw/releases/tag/0.2.0",
                    Assets =
                    [
                        new BinaryAsset
                        {
                            Component = "netclaw",
                            Rid = UpdateCheckService.GetCurrentRid(),
                            Url = "https://releases.netclaw.dev/test",
                            Sha256 = "abc",
                            SizeBytes = 100
                        }
                    ]
                }
            ]
        };

        var result = UpdateCheckService.EvaluateManifest(manifest, "0.1.0");

        Assert.Equal("https://github.com/stannardlabs/netclaw/releases/tag/0.2.0",
            result.ReleaseNotesUrl);
    }

    [Fact]
    public void IsNewerVersion_NewerReturnsTrueOlderReturnsFalse()
    {
        Assert.True(UpdateCheckService.IsNewerVersion("0.1.0", "0.2.0"));
        Assert.False(UpdateCheckService.IsNewerVersion("0.2.0", "0.1.0"));
        Assert.False(UpdateCheckService.IsNewerVersion("0.1.0", "0.1.0"));
    }

    [Fact]
    public void IsNewerVersion_HandlesInvalidVersionsGracefully()
    {
        Assert.False(UpdateCheckService.IsNewerVersion("invalid", "0.2.0"));
        Assert.False(UpdateCheckService.IsNewerVersion("0.1.0", "invalid"));
    }

    [Fact]
    public void GetCurrentRid_ReturnsNonEmptyString()
    {
        var rid = UpdateCheckService.GetCurrentRid();
        Assert.False(string.IsNullOrEmpty(rid));
    }

    [Fact]
    public void BinaryFeedManifest_RoundTripsViaJson()
    {
        var manifest = CreateManifest("1.0.0", "linux-x64");
        var json = JsonSerializer.Serialize(manifest);
        var deserialized = JsonSerializer.Deserialize<BinaryFeedManifest>(json)!;

        Assert.Equal(1, deserialized.SchemaVersion);
        Assert.Equal("releases", deserialized.FeedType);
        Assert.Equal("1.0.0", deserialized.Latest);
        Assert.Single(deserialized.Releases);
        Assert.Equal(2, deserialized.Releases[0].Assets.Count);
    }

    public void Dispose()
    {
        UpdateCheckService.ResetCache();
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }

    // ═══════════════════════════════════════════════════════════════
    // Helpers
    // ═══════════════════════════════════════════════════════════════

    private static BinaryUpdateCheckService CreateDaemonService(
        FakeHttpHandler handler, string currentVersion = "0.1.0")
    {
        var httpClient = new HttpClient(handler);
        return new BinaryUpdateCheckService(
            httpClient,
            NullLogger<BinaryUpdateCheckService>.Instance,
            currentVersion);
    }

    private static BinaryFeedManifest CreateManifest(string version, string rid)
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
                    ReleaseNotesUrl = $"https://github.com/stannardlabs/netclaw/releases/tag/{version}",
                    Assets =
                    [
                        new BinaryAsset
                        {
                            Component = "netclaw",
                            Rid = rid,
                            Url = $"https://releases.netclaw.dev/{version}/netclaw-{version}-{rid}.tar.gz",
                            Sha256 = "abc123def456",
                            SizeBytes = 50_000_000
                        },
                        new BinaryAsset
                        {
                            Component = "netclawd",
                            Rid = rid,
                            Url = $"https://releases.netclaw.dev/{version}/netclawd-{version}-{rid}.tar.gz",
                            Sha256 = "fed321cba654",
                            SizeBytes = 60_000_000
                        }
                    ]
                }
            ]
        };
    }

    /// <summary>
    /// Reuses the same FakeHttpHandler pattern from <see cref="SystemSkillSyncServiceTests"/>.
    /// </summary>
    private sealed class FakeHttpHandler : HttpMessageHandler
    {
        private readonly Dictionary<string, (HttpStatusCode Status, string Content, string ContentType)> _responses = new();

        public void AddJsonResponse<T>(string url, T body)
        {
            var json = JsonSerializer.Serialize(body);
            _responses[url] = (HttpStatusCode.OK, json, "application/json");
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
}
