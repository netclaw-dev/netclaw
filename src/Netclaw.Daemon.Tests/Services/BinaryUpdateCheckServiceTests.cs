// -----------------------------------------------------------------------
// <copyright file="BinaryUpdateCheckServiceTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Netclaw.Configuration;
using Netclaw.Configuration.Feeds;
using Netclaw.Configuration.Security;
using Netclaw.Daemon.Services;
using Netclaw.Tests.Utilities;
using NSec.Cryptography;
using Xunit;

namespace Netclaw.Daemon.Tests.Services;

public sealed class BinaryUpdateCheckServiceTests : IDisposable
{
    private readonly DisposableTempDir _dir = new();
    private readonly NetclawPaths _paths;
    private readonly Key _testSigningKey;
    private readonly byte[] _testPublicKeyBlob;

    public BinaryUpdateCheckServiceTests()
    {
        _paths = new NetclawPaths(_dir.Path);
        _paths.EnsureDirectoriesExist();

        // Generate a test signing keypair for each test
        _testSigningKey = Key.Create(SignatureAlgorithm.Ed25519,
            new KeyCreationParameters { ExportPolicy = KeyExportPolicies.AllowPlaintextExport });

        var pubKeyRaw = _testSigningKey.Export(KeyBlobFormat.RawPublicKey);
        _testPublicKeyBlob = new byte[42];
        _testPublicKeyBlob[0] = 0x45; _testPublicKeyBlob[1] = 0x64; // "Ed"
        // Key ID: test bytes
        byte[] testKeyId = [0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07, 0x08];
        Array.Copy(testKeyId, 0, _testPublicKeyBlob, 2, 8);
        Array.Copy(pubKeyRaw, 0, _testPublicKeyBlob, 10, 32);

        // Set test key override so MinisignVerifier uses our test key
        MinisignVerifier.TestPublicKeyOverride = _testPublicKeyBlob;

        // Clear static cache between tests to avoid cross-test interference
        UpdateCheckService.ResetCache();
    }

    // ═══════════════════════════════════════════════════════════════
    // BinaryUpdateCheckService (daemon hosted service) tests
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task CheckAndNotify_CachesResultWhenUpdateAvailable()
    {
        var manifest = CreateManifest("0.2.0", UpdateCheckService.GetCurrentRid());
        var handler = CreateSignedHandler(manifest);

        var sut = CreateDaemonService(handler, currentVersion: "0.1.0");
        await sut.CheckAndNotifyAsync(CancellationToken.None);

        var cached = UpdateCheckService.GetLastResult();
        Assert.NotNull(cached);
        Assert.True(cached!.IsUpdateAvailable);
    }

    [Fact]
    public async Task CheckAndNotify_EmitsUpdateAvailableAlert()
    {
        var manifest = CreateManifest("0.2.0", UpdateCheckService.GetCurrentRid());
        var handler = CreateSignedHandler(manifest);
        var sink = new FakeNotificationSink();

        var sut = CreateDaemonService(handler, currentVersion: "0.1.0", sink: sink);
        await sut.CheckAndNotifyAsync(CancellationToken.None);

        Assert.Single(sink.Alerts);
        var alert = sink.Alerts[0];
        Assert.Equal(AlertType.UpdateAvailable, alert.Category);
        Assert.Equal("update.available", alert.Type);
        Assert.Equal(AlertSeverity.Info, alert.Severity);
        Assert.Contains("0.2.0", alert.Summary);
    }

    [Fact]
    public async Task CheckAndNotify_DoesNotEmitAlertWhenUpToDate()
    {
        var manifest = CreateManifest("0.1.0", UpdateCheckService.GetCurrentRid());
        var handler = CreateSignedHandler(manifest);
        var sink = new FakeNotificationSink();

        var sut = CreateDaemonService(handler, currentVersion: "0.1.0", sink: sink);
        await sut.CheckAndNotifyAsync(CancellationToken.None);

        Assert.Empty(sink.Alerts);
    }

    [Fact]
    public async Task CheckAndNotify_GracefullyHandlesNetworkFailure()
    {
        var handler = new FakeHttpMessageHandler();
        handler.AddErrorResponse(FeedConstants.BinaryManifestUrl, HttpStatusCode.ServiceUnavailable);

        var sut = CreateDaemonService(handler, currentVersion: "0.1.0");
        await sut.CheckAndNotifyAsync(CancellationToken.None);

        var cached = UpdateCheckService.GetLastResult();
        Assert.NotNull(cached);
        Assert.False(cached!.IsUpdateAvailable);
    }

    [Fact]
    public async Task CheckAndNotify_HandlesTimeoutGracefully()
    {
        var handler = new FakeHttpMessageHandler();
        // No response configured → 404, which triggers HttpRequestException

        var sut = CreateDaemonService(handler, currentVersion: "0.1.0");
        await sut.CheckAndNotifyAsync(CancellationToken.None);
    }

    // ═══════════════════════════════════════════════════════════════
    // UpdateCheckService (shared logic) tests
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task CheckForUpdateAsync_ReturnsUpdateWhenNewerVersionAvailable()
    {
        var manifest = CreateManifest("0.2.0", UpdateCheckService.GetCurrentRid());
        var handler = CreateSignedHandler(manifest);

        using var httpClient = new HttpClient(handler);
        var result = await UpdateCheckService.CheckForUpdateAsync(
            httpClient, "0.1.0", TestContext.Current.CancellationToken);

        Assert.True(result.IsUpdateAvailable);
        Assert.Equal("0.1.0", result.CurrentVersion);
        Assert.Equal("0.2.0", result.LatestVersion);
        Assert.Equal(2, result.MatchingAssets.Count); // netclaw + netclawd
    }

    [Fact]
    public async Task CheckForUpdateAsync_ReturnsNoUpdateWhenAlreadyLatest()
    {
        var manifest = CreateManifest("0.1.0", "linux-x64");
        var handler = CreateSignedHandler(manifest);

        using var httpClient = new HttpClient(handler);
        var result = await UpdateCheckService.CheckForUpdateAsync(
            httpClient, "0.1.0", TestContext.Current.CancellationToken);

        Assert.False(result.IsUpdateAvailable);
    }

    [Fact]
    public async Task CheckForUpdateAsync_ReturnsNoUpdateWhenNewerThanManifest()
    {
        var manifest = CreateManifest("0.1.0", "linux-x64");
        var handler = CreateSignedHandler(manifest);

        using var httpClient = new HttpClient(handler);
        var result = await UpdateCheckService.CheckForUpdateAsync(
            httpClient, "0.2.0", TestContext.Current.CancellationToken);

        Assert.False(result.IsUpdateAvailable);
    }

    [Fact]
    public async Task CheckForUpdateAsync_ReturnsNoUpdateOnNetworkFailure()
    {
        var handler = new FakeHttpMessageHandler();
        handler.AddErrorResponse(FeedConstants.BinaryManifestUrl, HttpStatusCode.InternalServerError);

        using var httpClient = new HttpClient(handler);
        var result = await UpdateCheckService.CheckForUpdateAsync(
            httpClient, "0.1.0", TestContext.Current.CancellationToken);

        Assert.False(result.IsUpdateAvailable);
        Assert.Equal("0.1.0", result.CurrentVersion);
    }

    [Fact]
    public async Task CheckForUpdateAsync_UsesDedicatedReleasesManifestEndpoint()
    {
        var manifest = CreateManifest("0.2.0", UpdateCheckService.GetCurrentRid());
        var handler = CreateSignedHandler(manifest);

        using var httpClient = new HttpClient(handler);
        var result = await UpdateCheckService.CheckForUpdateAsync(
            httpClient, "0.1.0", TestContext.Current.CancellationToken);

        Assert.True(result.IsUpdateAvailable);
        Assert.Equal("0.2.0", result.LatestVersion);
    }

    [Fact]
    public async Task CheckForUpdateAsync_ReturnsNoUpdateOnMissingSignature()
    {
        var manifest = CreateManifest("0.2.0", UpdateCheckService.GetCurrentRid());
        var handler = new FakeHttpMessageHandler();
        handler.AddJsonResponse(FeedConstants.BinaryManifestUrl, manifest);
        // No signature configured — will 404

        using var httpClient = new HttpClient(handler);
        var result = await UpdateCheckService.CheckForUpdateAsync(
            httpClient, "0.1.0", TestContext.Current.CancellationToken);

        // Signature failure treated as network failure → no update
        Assert.False(result.IsUpdateAvailable);
    }

    [Fact]
    public async Task FetchVerifiedManifestAsync_ReturnsSignatureFailureOnMissingSignature()
    {
        var manifest = CreateManifest("0.2.0", UpdateCheckService.GetCurrentRid());
        var handler = new FakeHttpMessageHandler();
        handler.AddJsonResponse(FeedConstants.BinaryManifestUrl, manifest);
        // No signature URL → 404

        using var httpClient = new HttpClient(handler);
        var result = await UpdateCheckService.FetchVerifiedManifestAsync(httpClient, TestContext.Current.CancellationToken);

        Assert.False(result.IsSuccess);
        Assert.Equal(ManifestFetchStatus.SignatureFailure, result.Status);
    }

    [Fact]
    public async Task FetchVerifiedManifestAsync_ReturnsSignatureFailureOnTamperedManifest()
    {
        var manifest = CreateManifest("0.2.0", UpdateCheckService.GetCurrentRid());
        var handler = new FakeHttpMessageHandler();

        // Return the manifest but sign different content
        var json = JsonSerializer.Serialize(manifest);
        handler.AddResponse(FeedConstants.BinaryManifestUrl, HttpStatusCode.OK, json, "application/json");

        var wrongSig = SignContent("different content");
        handler.AddResponse(FeedConstants.BinaryManifestSignatureUrl, HttpStatusCode.OK, wrongSig, "text/plain");

        using var httpClient = new HttpClient(handler);
        var result = await UpdateCheckService.FetchVerifiedManifestAsync(httpClient, TestContext.Current.CancellationToken);

        Assert.False(result.IsSuccess);
        Assert.Equal(ManifestFetchStatus.SignatureFailure, result.Status);
    }

    [Fact]
    public async Task FetchVerifiedManifestAsync_ReturnsPlatformUnavailableWhenVerifierCannotRun()
    {
        MinisignVerifier.TestVerifyResultOverride = MinisignVerifier.VerifyResult.PlatformUnavailable;
        var manifest = CreateManifest("0.2.0", UpdateCheckService.GetCurrentRid());
        var handler = CreateSignedHandler(manifest);

        using var httpClient = new HttpClient(handler);
        var result = await UpdateCheckService.FetchVerifiedManifestAsync(httpClient, TestContext.Current.CancellationToken);

        Assert.False(result.IsSuccess);
        Assert.Equal(ManifestFetchStatus.PlatformUnavailable, result.Status);
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
                        },
                        new BinaryAsset
                        {
                            Component = "netclaw",
                            Rid = "osx-arm64",
                            Url = "https://releases.netclaw.dev/0.2.0/netclaw-0.2.0-osx-arm64.tar.gz",
                            Sha256 = "jkl012",
                            SizeBytes = 52_000_000
                        }
                    ]
                }
            ]
        };

        var result = UpdateCheckService.EvaluateManifest(manifest, "0.1.0");

        Assert.True(result.IsUpdateAvailable);
        // The matching count depends on the current RID at test runtime; the
        // manifest carries an asset for every RID the CI matrix runs on
        // (linux-x64, win-x64, osx-arm64).
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
                    ReleaseNotesUrl = "https://github.com/netclaw-dev/netclaw/releases/tag/0.2.0",
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

        Assert.Equal("https://github.com/netclaw-dev/netclaw/releases/tag/0.2.0",
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
        MinisignVerifier.TestPublicKeyOverride = null;
        MinisignVerifier.TestVerifyResultOverride = null;
        UpdateCheckService.ResetCache();
        _testSigningKey.Dispose();
        _dir.Dispose();
    }

    // ═══════════════════════════════════════════════════════════════
    // Helpers
    // ═══════════════════════════════════════════════════════════════

    private static BinaryUpdateCheckService CreateDaemonService(
        FakeHttpMessageHandler handler,
        string currentVersion = "0.1.0",
        FakeNotificationSink? sink = null)
    {
        var httpClient = new HttpClient(handler);
        return new BinaryUpdateCheckService(
            httpClient,
            NullLogger<BinaryUpdateCheckService>.Instance,
            currentVersion,
            selfUpdateDisabled: false,
            sink);
    }

    /// <summary>
    /// Creates a FakeHttpMessageHandler that returns a properly signed manifest.
    /// </summary>
    private FakeHttpMessageHandler CreateSignedHandler(BinaryFeedManifest manifest)
    {
        var handler = new FakeHttpMessageHandler();
        var json = JsonSerializer.Serialize(manifest);
        handler.AddResponse(FeedConstants.BinaryManifestUrl, HttpStatusCode.OK, json, "application/json");

        var sigContent = SignContent(json);
        handler.AddResponse(FeedConstants.BinaryManifestSignatureUrl, HttpStatusCode.OK, sigContent, "text/plain");

        return handler;
    }

    /// <summary>
    /// Signs content with the test key and returns minisign signature file content.
    /// </summary>
    private string SignContent(string content)
    {
        var data = System.Text.Encoding.UTF8.GetBytes(content);
        var signature = SignatureAlgorithm.Ed25519.Sign(_testSigningKey, data);

        // Build minisign signature blob: "ED" (2) + keyId (8) + signature (64) = 74 bytes
        var sigBlob = new byte[74];
        sigBlob[0] = 0x45; sigBlob[1] = 0x44; // "ED" (standard)
        Array.Copy(_testPublicKeyBlob, 2, sigBlob, 2, 8); // Copy key ID
        Array.Copy(signature, 0, sigBlob, 10, 64);

        return $"untrusted comment: test signature\n{Convert.ToBase64String(sigBlob)}\ntrusted comment: test\ndGVzdA==\n";
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
                    ReleaseNotesUrl = $"https://github.com/netclaw-dev/netclaw/releases/tag/{version}",
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

    private sealed class FakeNotificationSink : IOperationalNotificationSink
    {
        public List<OperationalAlert> Alerts { get; } = [];
        public void Emit(OperationalAlert alert) => Alerts.Add(alert);
    }
}
