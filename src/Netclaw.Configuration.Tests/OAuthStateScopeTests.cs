// -----------------------------------------------------------------------
// <copyright file="OAuthStateScopeTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Microsoft.Extensions.Configuration;
using Netclaw.Tests.Utilities;
using Xunit;

namespace Netclaw.Configuration.Tests;

public sealed class OAuthStateScopeTests : IDisposable
{
    private readonly DisposableTempDir _dir = new();
    private readonly NetclawPaths _paths;

    public OAuthStateScopeTests()
    {
        _paths = new NetclawPaths(_dir.Path);
        _paths.EnsureDirectoriesExist();
        File.WriteAllText(_paths.NetclawConfigPath, """
            { "Providers": { "p": { "Type": "openai", "Endpoint": "https://api.openai.com" } },
              "Security": { "Posture": "Strict" } }
            """);
        File.WriteAllText(_paths.SecretsPath, """{ "Providers": { "p": { "ApiKey": "real-key" } } }""");
    }

    [Theory]
    [InlineData("""{ "Providers": { "p": { "OAuthTokenExpiry": null, "Endpoint": "https://attacker.example" } } }""", "Providers:p:Endpoint")]
    [InlineData("""{ "Providers": { "p": { "ApiKey": "other-key" } } }""", "Providers:p:ApiKey")]
    [InlineData("""{ "Providers": { "p": { "OAuthTokenExpiry": null } }, "Security": { "Posture": "Permissive" } }""", "Security")]
    [InlineData("""{ "providers": { "P": { "oauthrefreshtoken": "x" } } }""", "providers:P:oauthrefreshtoken")]
    public void A_key_other_than_the_token_expiry_is_rejected_at_load_and_named(string state, string key)
    {
        File.WriteAllText(_paths.OAuthStatePath, state);

        var daemon = Assert.Throws<InvalidDataException>(() => new ConfigurationBuilder().AddNetclawDaemonSources(_paths).Build());
        var message = DaemonConfigurationSources.DescribeLoadFailure(daemon);
        Assert.StartsWith("Couldn't read oauth-state.json:", message);
        Assert.Contains($"'{key}'", message);

        var store = Assert.Throws<InvalidDataException>(() => OAuthStateStore.TryGetExpiry(_paths, "p", out _));
        Assert.StartsWith("Couldn't read oauth-state.json:", store.Message);
        Assert.Contains($"'{key}'", store.Message);
    }

    [Fact]
    public void A_file_with_only_token_expiry_keys_leaves_every_other_value_alone()
    {
        File.WriteAllText(_paths.OAuthStatePath, """
            { "Providers": { "p": { "OAuthTokenExpiry": "2026-06-01T01:00:00+00:00" }, "q": null } }
            """);

        var configuration = new ConfigurationBuilder().AddNetclawDaemonSources(_paths).Build();

        Assert.Equal("https://api.openai.com", configuration["Providers:p:Endpoint"]);
        Assert.Equal("real-key", configuration["Providers:p:ApiKey"]);
        Assert.Equal("Strict", configuration["Security:Posture"]);
        Assert.Equal("2026-06-01T01:00:00+00:00", configuration["Providers:p:OAuthTokenExpiry"]);
    }

    public void Dispose() => _dir.Dispose();
}
