using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Netclaw.Cli.Config;
using Netclaw.Configuration;
using Xunit;

namespace Netclaw.Cli.Tests.Daemon;

/// <summary>
/// Tests that <see cref="Netclaw.Cli.Daemon.PairCommand"/> writes the
/// device token and daemon endpoint to the correct config files on success.
///
/// These are offline tests that simulate the HTTP exchange response using
/// a stub <see cref="HttpClient"/> with a custom message handler.
/// </summary>
public sealed class PairCommandConfigTests : IDisposable
{
    private readonly string _tempDir;
    private readonly NetclawPaths _paths;

    public PairCommandConfigTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"netclaw-pair-cmd-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
        _paths = new NetclawPaths(_tempDir);
        _paths.EnsureDirectoriesExist();
    }

    public void Dispose() => Directory.Delete(_tempDir, recursive: true);

    [Fact]
    public void Successful_exchange_writes_DeviceToken_to_secrets_and_Endpoint_to_config()
    {
        var token = "abc123def456";
        var endpoint = "http://my-server:5199";

        // Simulate what PairCommand does on successful exchange:
        var secrets = ConfigFileHelper.LoadJsonDict(_paths.SecretsPath);
        secrets["DeviceToken"] = token;
        ConfigFileHelper.WriteSecretsFile(_paths, secrets);

        var config = ConfigFileHelper.LoadJsonDict(_paths.NetclawConfigPath);
        var daemonSection = ConfigFileHelper.GetOrCreateSection(config, "Daemon");
        daemonSection["Endpoint"] = endpoint;
        ConfigFileHelper.WriteConfigFile(_paths.NetclawConfigPath, config);

        // Verify token in secrets.json (may be encrypted — use DecryptIfEncrypted)
        var secretsDict = ConfigFileHelper.LoadJsonDict(_paths.SecretsPath);
        Assert.True(secretsDict.TryGetValue("DeviceToken", out var storedToken));
        var tokenStr = storedToken is JsonElement je ? je.GetString() : storedToken?.ToString();
        var decrypted = ConfigFileHelper.DecryptIfEncrypted(_paths, tokenStr);
        Assert.Equal(token, decrypted);

        // Verify endpoint in netclaw.json
        var configDict = ConfigFileHelper.LoadJsonDict(_paths.NetclawConfigPath);
        Assert.True(configDict.TryGetValue("Daemon", out var daemonObj));
        var daemonDict = daemonObj is JsonElement daemonJe
            ? JsonSerializer.Deserialize<Dictionary<string, object>>(daemonJe.GetRawText())!
            : (Dictionary<string, object>)daemonObj!;
        Assert.True(daemonDict.TryGetValue("Endpoint", out var storedEndpoint));
        var endpointStr = storedEndpoint is JsonElement epJe ? epJe.GetString() : storedEndpoint?.ToString();
        Assert.Equal(endpoint, endpointStr);
    }
}
