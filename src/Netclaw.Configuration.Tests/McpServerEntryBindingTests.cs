// -----------------------------------------------------------------------
// <copyright file="McpServerEntryBindingTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Microsoft.Extensions.Configuration;
using Netclaw.Configuration.Secrets;
using Netclaw.Tests.Utilities;
using Xunit;

namespace Netclaw.Configuration.Tests;

/// <summary>
/// Pins the daemon-side binding behavior for <see cref="McpServerEntry.Headers"/>
/// and <see cref="McpServerEntry.EnvironmentVariables"/>. Before SensitiveString
/// wrapping, the daemon was binding the raw ciphertext (<c>ENC:…</c>) verbatim
/// into <see cref="Dictionary{TKey,TValue}"/>, then forwarding it as the literal
/// <c>Authorization</c> header / env value. Any HTTP MCP server that authenticates
/// from the first byte rejected the resulting garbage with 401 (see issue #1118).
/// </summary>
[Collection(SensitiveStringStaticStateCollection.Name)]
public sealed class McpServerEntryBindingTests : IDisposable
{
    private readonly DisposableTempDir _dir = new();
    private readonly ISecretsProtector? _previousProtector;

    public McpServerEntryBindingTests()
    {
        _previousProtector = SensitiveStringTypeConverter.Protector;
    }

    public void Dispose()
    {
        SensitiveStringTypeConverter.Protector = _previousProtector;
        _dir.Dispose();
    }

    [Fact]
    public void Headers_bound_from_configuration_decrypt_ENC_values()
    {
        var paths = new NetclawPaths(_dir.Path);
        paths.EnsureDirectoriesExist();
        var protector = SecretsProtection.CreateProtector(paths);
        SensitiveStringTypeConverter.Protector = protector;

        var encrypted = protector.Protect("Bearer real-token-abc");
        Assert.StartsWith("ENC:", encrypted, StringComparison.Ordinal);

        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["McpServers:atlassian:Transport"] = "http",
                ["McpServers:atlassian:Url"] = "https://mcp.example.com/v1/mcp",
                ["McpServers:atlassian:Headers:Authorization"] = encrypted,
            })
            .Build();

        var bound = config.GetSection("McpServers")
            .Get<Dictionary<string, McpServerEntry>>() ?? [];

        var entry = Assert.Contains("atlassian", bound);
        var header = Assert.Contains("Authorization", entry.Headers!);
        Assert.Equal("Bearer real-token-abc", header.Value);
    }

    [Fact]
    public void EnvironmentVariables_bound_from_configuration_decrypt_ENC_values()
    {
        var paths = new NetclawPaths(_dir.Path);
        paths.EnsureDirectoriesExist();
        var protector = SecretsProtection.CreateProtector(paths);
        SensitiveStringTypeConverter.Protector = protector;

        var encrypted = protector.Protect("sk-very-real-key");

        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["McpServers:weather:Transport"] = "stdio",
                ["McpServers:weather:Command"] = "weather-mcp",
                ["McpServers:weather:EnvironmentVariables:API_KEY"] = encrypted,
            })
            .Build();

        var bound = config.GetSection("McpServers")
            .Get<Dictionary<string, McpServerEntry>>() ?? [];

        var entry = Assert.Contains("weather", bound);
        var env = Assert.Contains("API_KEY", entry.EnvironmentVariables!);
        Assert.Equal("sk-very-real-key", env.Value);
    }

    [Fact]
    public void Headers_bound_from_plaintext_values_pass_through_unchanged()
    {
        // Some test fixtures and migration paths may write plaintext Headers
        // directly. The SensitiveString converter must treat any value that
        // lacks the ENC: prefix as already-plaintext and leave it alone.
        var paths = new NetclawPaths(_dir.Path);
        paths.EnsureDirectoriesExist();
        SensitiveStringTypeConverter.Protector = SecretsProtection.CreateProtector(paths);

        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["McpServers:test:Transport"] = "http",
                ["McpServers:test:Url"] = "https://example.com/mcp",
                ["McpServers:test:Headers:Authorization"] = "Bearer plaintext-token",
            })
            .Build();

        var bound = config.GetSection("McpServers")
            .Get<Dictionary<string, McpServerEntry>>() ?? [];

        Assert.Equal("Bearer plaintext-token", bound["test"].Headers!["Authorization"].Value);
    }

    [Fact]
    public void IsOAuthCapable_identifies_the_one_server_kind_that_can_run_netclaw_mcp_auth()
    {
        // The daemon publishes the auth remedy from this rule and `netclaw doctor` prints
        // it from the same rule. Two copies would drift and name a command the operator
        // cannot run. The header key comparison ignores case: a hand-edited config or a
        // provider example can spell it any way.
        var stdio = new McpServerEntry { Transport = "stdio", Command = "local-server" };
        var staticHeader = new McpServerEntry
        {
            Transport = "http",
            Url = "https://example.com/mcp",
            Headers = new Dictionary<string, SensitiveString>(StringComparer.Ordinal)
            {
                ["authorization"] = new("Bearer operator-key"),
            },
        };
        var oauthCapable = new McpServerEntry { Transport = "http", Url = "https://example.com/mcp" };

        Assert.False(stdio.IsOAuthCapable);
        Assert.False(staticHeader.IsOAuthCapable);
        Assert.True(staticHeader.HasConfiguredAuthorizationHeader);
        Assert.True(oauthCapable.IsOAuthCapable);
    }

    [Fact]
    public void Computed_auth_scheme_properties_stay_out_of_the_written_config()
    {
        // `netclaw mcp add` writes the serialized entry into netclaw.json, and the config
        // schema sets additionalProperties: false. A computed property without JsonIgnore
        // would make every new server fail schema validation.
        var json = System.Text.Json.JsonSerializer.Serialize(
            new McpServerEntry { Transport = "http", Url = "https://example.com/mcp" });

        Assert.DoesNotContain("IsOAuthCapable", json, StringComparison.Ordinal);
        Assert.DoesNotContain("HasConfiguredAuthorizationHeader", json, StringComparison.Ordinal);
    }
}
