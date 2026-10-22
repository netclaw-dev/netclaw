// -----------------------------------------------------------------------
// <copyright file="ProviderConfigurationLoaderTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Microsoft.Extensions.Configuration;
using Netclaw.Configuration.Providers;
using Netclaw.Configuration.Secrets;
using Netclaw.Providers.OAuth;
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
    public void Load_LegacyApiKeyWithoutAuthMethod_SelectsApiKeyAuth()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Providers:local:Type"] = "openai-compatible",
                ["Providers:local:ApiKey"] = "legacy-key"
            })
            .Build();

        var providers = ProviderConfigurationLoader.Load(configuration.GetSection("Providers"));

        Assert.Equal(AuthMethod.ApiKey, providers["local"].AuthMethod);
        Assert.Equal("legacy-key", providers["local"].ApiKey?.Value);
    }

    [Fact]
    public void Load_ExplicitNoneWithStaleApiKey_PreservesNone()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Providers:local:Type"] = "openai-compatible",
                ["Providers:local:AuthMethod"] = "None",
                ["Providers:local:ApiKey"] = "stale-key"
            })
            .Build();

        var providers = ProviderConfigurationLoader.Load(configuration.GetSection("Providers"));

        Assert.Equal(AuthMethod.None, providers["local"].AuthMethod);
        Assert.Equal("stale-key", providers["local"].ApiKey?.Value);
    }

    [Fact]
    public void Load_EndpointOnlyProviderWithStaleApiKey_PreservesNone()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Providers:local:Type"] = "ollama",
                ["Providers:local:ApiKey"] = "stale-key"
            })
            .Build();

        var providers = ProviderConfigurationLoader.Load(configuration.GetSection("Providers"));

        Assert.Equal(AuthMethod.None, providers["local"].AuthMethod);
        Assert.Equal("stale-key", providers["local"].ApiKey?.Value);
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

    [Fact]
    public void Load_ReadsTheRetiredOAuthTokenExpiryKeyFromNetclawJson()
    {
        var expiry = new DateTimeOffset(2026, 7, 4, 12, 30, 0, TimeSpan.FromHours(-4));
        var paths = NewConfigPaths();
        File.WriteAllText(paths.NetclawConfigPath, $$"""{ "Providers": { "anthropic": { "Type": "anthropic", "OAuthTokenExpiry": "{{expiry:o}}" } } }""");

        var providers = LoadProviders(paths);

        Assert.Equal(expiry, providers["anthropic"].OAuthTokenExpiry);
    }

    [Fact]
    public void Load_PrefersSecretsJsonOverTheRetiredNetclawJsonKey()
    {
        var oldExpiry = new DateTimeOffset(2026, 7, 4, 12, 30, 0, TimeSpan.Zero);
        var newExpiry = oldExpiry.AddHours(6);
        var paths = NewConfigPaths();
        File.WriteAllText(paths.NetclawConfigPath, $$"""{ "Providers": { "anthropic": { "Type": "anthropic", "OAuthTokenExpiry": "{{oldExpiry:o}}" } } }""");
        File.WriteAllText(paths.SecretsPath, $$"""{ "Providers": { "anthropic": { "OAuthTokenExpiry": "{{newExpiry:o}}" } } }""");

        var providers = LoadProviders(paths);

        Assert.Equal(newExpiry, providers["anthropic"].OAuthTokenExpiry);
    }

    [Fact]
    public void Load_a_null_secrets_json_expiry_hides_the_retired_netclaw_json_key()
    {
        var paths = NewConfigPaths();
        File.WriteAllText(paths.NetclawConfigPath, """{ "Providers": { "anthropic": { "Type": "anthropic", "OAuthTokenExpiry": "2026-07-04T12:30:00.0000000+00:00" } } }""");
        File.WriteAllText(paths.SecretsPath, """{ "Providers": { "anthropic": { "OAuthTokenExpiry": null } } }""");

        var providers = LoadProviders(paths);

        Assert.Null(providers["anthropic"].OAuthTokenExpiry);
    }

    [Fact]
    public void Load_binds_the_expiry_that_PersistTokens_saved_with_the_real_protector()
    {
        // Producer to consumer: the writer must emit the shape the daemon binder reads.
        var expiry = new DateTimeOffset(2026, 7, 4, 12, 30, 0, TimeSpan.FromHours(-4));
        var paths = NewConfigPaths();
        var protector = SecretsProtection.CreateProtector(paths);
        File.WriteAllText(paths.NetclawConfigPath, """{ "Providers": { "anthropic": { "Type": "anthropic", "AuthMethod": "OAuthDevice" } } }""");
        OAuthTokenPersistence.PersistTokens(
            paths,
            "anthropic",
            new OAuthDeviceFlowResult(new SensitiveString("access-1"), new SensitiveString("refresh-1"), expiry),
            protector);
        SensitiveStringTypeConverter.Protector = protector;

        var providers = LoadProviders(paths);

        Assert.Equal(expiry, providers["anthropic"].OAuthTokenExpiry);
        Assert.Equal("access-1", providers["anthropic"].OAuthAccessToken?.Value);
        Assert.Equal("refresh-1", providers["anthropic"].OAuthRefreshToken?.Value);
        Assert.DoesNotContain("access-1", File.ReadAllText(paths.SecretsPath), StringComparison.Ordinal);
    }

    [Fact]
    public void Load_with_an_encrypted_expiry_in_secrets_json_fails_and_names_the_provider()
    {
        // An old build encrypted this leaf. The binder cannot read ciphertext as a date, and a
        // silent null would hide the bad file, so the load fails.
        var paths = NewConfigPaths();
        File.WriteAllText(paths.NetclawConfigPath, """{ "Providers": { "anthropic": { "Type": "anthropic" } } }""");
        File.WriteAllText(paths.SecretsPath, """{ "Providers": { "anthropic": { "OAuthTokenExpiry": "ENC:AAAA" } } }""");

        var failure = Assert.Throws<ModelConfigurationException>(() => LoadProviders(paths));

        Assert.Contains("Providers:anthropic is invalid", failure.Message, StringComparison.Ordinal);
    }

    private NetclawPaths NewConfigPaths()
    {
        var paths = new NetclawPaths(_dir.Path);
        paths.EnsureDirectoriesExist();
        return paths;
    }

    private static IReadOnlyDictionary<string, ProviderEntry> LoadProviders(NetclawPaths paths)
    {
        var configuration = new ConfigurationBuilder().AddNetclawDaemonSources(paths).Build();
        return ProviderConfigurationLoader.Load(configuration.GetSection("Providers"));
    }

    private sealed class TestVendorOptions : IVendorOptions
    {
        public bool DisableThinking { get; set; }
        public string? Mode { get; set; }
    }
}
