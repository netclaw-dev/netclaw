using Netclaw.Cli.Daemon;
using Netclaw.Configuration;
using Xunit;

namespace Netclaw.Cli.Tests.Cli;

/// <summary>
/// Unit tests for <see cref="DaemonClientFactory"/>: verifies that the access token
/// provider is attached for non-loopback endpoints and omitted for loopback endpoints.
/// </summary>
public sealed class DaemonClientFactoryTests : IDisposable
{
    private readonly string _tempDir;
    private readonly NetclawPaths _paths;

    public DaemonClientFactoryTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"netclaw-factory-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
        _paths = new NetclawPaths(_tempDir);
    }

    public void Dispose() => Directory.Delete(_tempDir, recursive: true);

    // ── IsLoopback ────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("http://127.0.0.1:5199")]
    [InlineData("http://localhost:5199")]
    [InlineData("http://LOCALHOST:5199")]
    public void IsLoopback_ReturnsTrue_ForLoopbackEndpoints(string endpoint)
    {
        Assert.True(DaemonClientFactory.IsLoopback(endpoint));
    }

    [Theory]
    [InlineData("http://192.168.1.100:5199")]
    [InlineData("http://myserver.example.com:5199")]
    [InlineData("http://10.0.0.5:5199")]
    public void IsLoopback_ReturnsFalse_ForNonLoopbackEndpoints(string endpoint)
    {
        Assert.False(DaemonClientFactory.IsLoopback(endpoint));
    }

    // ── Token provider: loopback ──────────────────────────────────────────────

    [Fact]
    public void CreateAccessTokenProvider_ReturnsNull_ForLoopbackEndpoint()
    {
        // Write a device token — should be ignored for loopback
        WriteDeviceToken("test-token-value");

        var provider = DaemonClientFactory.CreateAccessTokenProvider(
            "http://127.0.0.1:5199", _paths);

        Assert.Null(provider);
    }

    // ── Token provider: non-loopback ──────────────────────────────────────────

    [Fact]
    public async Task CreateAccessTokenProvider_ReturnsProvider_ForNonLoopbackWithToken()
    {
        WriteDeviceToken("my-secret-device-token");

        var provider = DaemonClientFactory.CreateAccessTokenProvider(
            "http://192.168.1.100:5199", _paths);

        Assert.NotNull(provider);
        var token = await provider();
        Assert.Equal("my-secret-device-token", token);
    }

    [Fact]
    public void CreateAccessTokenProvider_ReturnsNull_ForNonLoopbackWithNoToken()
    {
        // No secrets.json → no token → null provider
        var provider = DaemonClientFactory.CreateAccessTokenProvider(
            "http://192.168.1.100:5199", _paths);

        Assert.Null(provider);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private void WriteDeviceToken(string token)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_paths.SecretsPath)!);
        var json = System.Text.Json.JsonSerializer.Serialize(
            new Dictionary<string, string> { ["DeviceToken"] = token },
            new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(_paths.SecretsPath, json);
    }
}
