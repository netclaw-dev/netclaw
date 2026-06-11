// -----------------------------------------------------------------------
// <copyright file="ProviderConfigurationLoaderTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Microsoft.Extensions.Configuration;
using Netclaw.Configuration.Providers;
using Netclaw.Configuration.Secrets;
using Netclaw.Tests.Utilities;
using Xunit;

namespace Netclaw.Configuration.Tests;

/// <summary>
/// Checks that <see cref="ProviderConfigurationLoader"/> loads provider API keys
/// and OAuth tokens correctly. Secrets in <c>secrets.json</c> are saved in a
/// scrambled form that starts with <c>ENC:</c>. The loader must turn them back
/// into the real values before the daemon uses them. These tests make sure
/// scrambled values come out unscrambled, and plain values are left alone.
/// </summary>
[Collection(SensitiveStringStaticStateCollection.Name)]
public sealed class ProviderConfigurationLoaderTests : IDisposable
{
    private readonly DisposableTempDir _dir = new();
    private readonly ISecretsProtector? _previousProtector;

    public ProviderConfigurationLoaderTests()
    {
        _previousProtector = SensitiveStringTypeConverter.Protector;
    }

    public void Dispose()
    {
        SensitiveStringTypeConverter.Protector = _previousProtector;
        _dir.Dispose();
    }


    [Fact]
    public void Load_PreservesVendorOptionsBag()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Providers:ollama:Type"] = "ollama",
                ["Providers:ollama:VendorOptions:DisableThinking"] = "true",
                ["Providers:openrouter:Type"] = "openrouter",
                ["Providers:openrouter:VendorOptions:Nested:Mode"] = "auto",
                ["Providers:openrouter:VendorOptions:Nested:Retries"] = "2",
                ["Providers:openrouter:VendorOptions:Tags:0"] = "search",
                ["Providers:openrouter:VendorOptions:Tags:1"] = "chat"
            })
            .Build();

        var providers = ProviderConfigurationLoader.Load(configuration.GetSection("Providers"));

        Assert.True(providers["ollama"].VendorOptions?["DisableThinking"]?.GetValue<bool>());
        Assert.Equal("auto", providers["openrouter"].VendorOptions?["Nested"]?["Mode"]?.GetValue<string>());
        Assert.Equal(2L, providers["openrouter"].VendorOptions?["Nested"]?["Retries"]?.GetValue<long>());
        Assert.Equal("search", providers["openrouter"].VendorOptions?["Tags"]?[0]?.GetValue<string>());
        Assert.Equal("chat", providers["openrouter"].VendorOptions?["Tags"]?[1]?.GetValue<string>());
    }

    [Fact]
    public void GetVendorOptions_DeserializesTypedOptions()
    {
        var entry = new ProviderEntry
        {
            Type = "ollama",
            VendorOptions = new System.Text.Json.Nodes.JsonObject
            {
                ["DisableThinking"] = true,
                ["Mode"] = "fast"
            }
        };

        var options = entry.GetVendorOptions<TestVendorOptions>();

        Assert.NotNull(options);
        Assert.True(options!.DisableThinking);
        Assert.Equal("fast", options.Mode);
    }

    [Fact]
    public void Load_DecryptsEncApiKey()
    {
        var paths = new NetclawPaths(_dir.Path);
        paths.EnsureDirectoriesExist();
        var protector = SecretsProtection.CreateProtector(paths);
        SensitiveStringTypeConverter.Protector = protector;

        var encrypted = protector.Protect("sk-or-real-key");
        Assert.StartsWith("ENC:", encrypted, StringComparison.Ordinal);

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Providers:openrouter:Type"] = "openrouter",
                ["Providers:openrouter:ApiKey"] = encrypted,
            })
            .Build();

        var providers = ProviderConfigurationLoader.Load(configuration.GetSection("Providers"));

        Assert.Equal("sk-or-real-key", providers["openrouter"].ApiKey?.Value);
    }

    [Fact]
    public void Load_DecryptsEncOAuthTokens()
    {
        var paths = new NetclawPaths(_dir.Path);
        paths.EnsureDirectoriesExist();
        var protector = SecretsProtection.CreateProtector(paths);
        SensitiveStringTypeConverter.Protector = protector;

        var encryptedAccess = protector.Protect("oauth-access-abc");
        var encryptedRefresh = protector.Protect("oauth-refresh-xyz");
        var encryptedAccountId = protector.Protect("account-123");

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Providers:anthropic:Type"] = "anthropic",
                ["Providers:anthropic:AuthMethod"] = "OAuthDevice",
                ["Providers:anthropic:OAuthAccessToken"] = encryptedAccess,
                ["Providers:anthropic:OAuthRefreshToken"] = encryptedRefresh,
                ["Providers:anthropic:OAuthAccountId"] = encryptedAccountId,
            })
            .Build();

        var providers = ProviderConfigurationLoader.Load(configuration.GetSection("Providers"));

        Assert.Equal("oauth-access-abc", providers["anthropic"].OAuthAccessToken?.Value);
        Assert.Equal("oauth-refresh-xyz", providers["anthropic"].OAuthRefreshToken?.Value);
        Assert.Equal("account-123", providers["anthropic"].OAuthAccountId?.Value);
    }

    [Fact]
    public void Load_ParsesOAuthTokenExpiry()
    {
        // The CLI writes expiry via DateTimeOffset.ToString("o") (ISO 8601 round-trip).
        // The loader must round-trip that back to the same DateTimeOffset.
        var expiry = new DateTimeOffset(2026, 7, 4, 12, 30, 0, TimeSpan.FromHours(-4));

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Providers:anthropic:Type"] = "anthropic",
                ["Providers:anthropic:OAuthTokenExpiry"] = expiry.ToString("o"),
            })
            .Build();

        var providers = ProviderConfigurationLoader.Load(configuration.GetSection("Providers"));

        Assert.Equal(expiry, providers["anthropic"].OAuthTokenExpiry);
    }

    [Fact]
    public void Load_PassesPlaintextSecretsThrough()
    {
        // Migration paths and tests sometimes write plaintext directly. Anything
        // without the ENC: prefix must be treated as already-plaintext.
        var paths = new NetclawPaths(_dir.Path);
        paths.EnsureDirectoriesExist();
        SensitiveStringTypeConverter.Protector = SecretsProtection.CreateProtector(paths);

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Providers:openai:Type"] = "openai",
                ["Providers:openai:ApiKey"] = "sk-plaintext",
            })
            .Build();

        var providers = ProviderConfigurationLoader.Load(configuration.GetSection("Providers"));

        Assert.Equal("sk-plaintext", providers["openai"].ApiKey?.Value);
    }

    private sealed class TestVendorOptions : IVendorOptions
    {
        public bool DisableThinking { get; set; }
        public string? Mode { get; set; }
    }
}
