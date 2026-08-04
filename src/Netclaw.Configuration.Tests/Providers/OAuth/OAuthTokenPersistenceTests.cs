// -----------------------------------------------------------------------
// <copyright file="OAuthTokenPersistenceTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Text.Json;
using Netclaw.Configuration;
using Netclaw.Configuration.Secrets;
using Netclaw.Providers.OAuth;
using Netclaw.Tests.Utilities;
using Xunit;

namespace Netclaw.Configuration.Tests.Providers.OAuth;

public sealed class OAuthTokenPersistenceTests
{
    [Fact]
    public void PersistTokens_PreservesExistingRefreshTokenAndAccountIdWhenResultOmitsThem()
    {
        using var dir = new DisposableTempDir();
        var paths = new NetclawPaths(dir.Path);
        paths.EnsureDirectoriesExist();
        // Init-based installs always have netclaw.json; expiry persistence
        // updates it (and never creates it — see the env-only test below).
        File.WriteAllText(paths.NetclawConfigPath, "{}");

        OAuthTokenPersistence.PersistTokens(
            paths,
            "openai",
            new OAuthDeviceFlowResult(
                new SensitiveString("access-1"),
                new SensitiveString("refresh-1"),
                DateTimeOffset.Parse("2026-06-01T00:00:00+00:00"),
                new SensitiveString("account-1")),
            new NullSecretsProtector());

        // A partial refresh that returns only a new access token must NOT wipe the
        // previously-stored refresh token or ChatGPT account id (Codex needs them).
        OAuthTokenPersistence.PersistTokens(
            paths,
            "openai",
            new OAuthDeviceFlowResult(
                new SensitiveString("access-2"),
                null,
                null),
            new NullSecretsProtector());

        using var doc = JsonDocument.Parse(File.ReadAllText(paths.SecretsPath));
        var provider = doc.RootElement
            .GetProperty("Providers")
            .GetProperty("openai");

        Assert.Equal("access-2", provider.GetProperty("OAuthAccessToken").GetString());
        Assert.Equal("refresh-1", provider.GetProperty("OAuthRefreshToken").GetString());
        Assert.Equal("account-1", provider.GetProperty("OAuthAccountId").GetString());

        // Expiry, by contrast, IS cleared when omitted — a stale expiry would make the
        // fresh token look already-expired.
        using var configDoc = JsonDocument.Parse(File.ReadAllText(paths.NetclawConfigPath));
        var configProvider = configDoc.RootElement
            .GetProperty("Providers")
            .GetProperty("openai");

        Assert.False(configProvider.TryGetProperty("OAuthTokenExpiry", out _));
    }

    [Fact]
    public void PersistTokens_OverwritesExistingFieldsWhenResultProvidesThem()
    {
        using var dir = new DisposableTempDir();
        var paths = new NetclawPaths(dir.Path);
        paths.EnsureDirectoriesExist();

        OAuthTokenPersistence.PersistTokens(
            paths,
            "openai",
            new OAuthDeviceFlowResult(
                new SensitiveString("access-1"),
                new SensitiveString("refresh-1"),
                null,
                new SensitiveString("account-1")),
            new NullSecretsProtector());

        OAuthTokenPersistence.PersistTokens(
            paths,
            "openai",
            new OAuthDeviceFlowResult(
                new SensitiveString("access-2"),
                new SensitiveString("refresh-2"),
                null,
                new SensitiveString("account-2")),
            new NullSecretsProtector());

        using var doc = JsonDocument.Parse(File.ReadAllText(paths.SecretsPath));
        var provider = doc.RootElement
            .GetProperty("Providers")
            .GetProperty("openai");

        Assert.Equal("access-2", provider.GetProperty("OAuthAccessToken").GetString());
        Assert.Equal("refresh-2", provider.GetProperty("OAuthRefreshToken").GetString());
        Assert.Equal("account-2", provider.GetProperty("OAuthAccountId").GetString());
    }

    [Fact]
    public void PersistTokens_DoesNotCreateConfigFileOnEnvOnlyInstance()
    {
        using var dir = new DisposableTempDir();
        var paths = new NetclawPaths(dir.Path);
        paths.EnsureDirectoriesExist();
        // No netclaw.json — an instance configured purely via NETCLAW_ env
        // vars. A token refresh must not silently materialize a config file
        // (turns the deployment stateful; throws on a read-only home).

        OAuthTokenPersistence.PersistTokens(
            paths,
            "copilot",
            new OAuthDeviceFlowResult(
                new SensitiveString("access-1"),
                new SensitiveString("refresh-1"),
                DateTimeOffset.Parse("2026-06-01T00:00:00+00:00"),
                null),
            new NullSecretsProtector());

        // Secrets still persist (they live in secrets.json), but the expiry —
        // refresh-timing metadata that belongs in netclaw.json — is skipped
        // rather than creating the file.
        Assert.True(File.Exists(paths.SecretsPath));
        Assert.False(File.Exists(paths.NetclawConfigPath));
    }
}
