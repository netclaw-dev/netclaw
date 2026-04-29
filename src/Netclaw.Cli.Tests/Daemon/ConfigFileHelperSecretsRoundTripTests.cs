// -----------------------------------------------------------------------
// <copyright file="ConfigFileHelperSecretsRoundTripTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Text.Json;
using Netclaw.Cli.Config;
using Netclaw.Configuration;
using Netclaw.Tests.Utilities;
using Xunit;

namespace Netclaw.Cli.Tests.Daemon;

/// <summary>
/// Verifies that <see cref="ConfigFileHelper"/> correctly persists and retrieves
/// a device token (via <see cref="ConfigFileHelper.WriteSecretsFile"/>) and a
/// daemon endpoint (via <see cref="ConfigFileHelper.WriteConfigFile"/>) through
/// an encrypted secrets round-trip.
///
/// These are offline tests that exercise the config-file helpers directly,
/// without invoking any CLI commands or network operations.
/// </summary>
public sealed class ConfigFileHelperSecretsRoundTripTests : IDisposable
{
    private readonly DisposableTempDir _dir = new();
    private readonly NetclawPaths _paths;

    public ConfigFileHelperSecretsRoundTripTests()
    {
        _paths = new NetclawPaths(_dir.Path);
        _paths.EnsureDirectoriesExist();
    }

    public void Dispose() => _dir.Dispose();

    [Fact]
    public void DeviceToken_WrittenToSecrets_DecryptsCorrectly_And_Endpoint_WrittenToConfig_RoundsTrip()
    {
        var token = "abc123def456";
        var endpoint = "http://my-server:5199";

        // Write token to secrets.json and endpoint to netclaw.json
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
