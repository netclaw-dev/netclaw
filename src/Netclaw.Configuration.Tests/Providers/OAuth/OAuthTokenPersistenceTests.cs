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
        // fresh token look already-expired. The entry stays, with a null value, so that it
        // hides a retired OAuthTokenExpiry key in netclaw.json.
        Assert.True(OAuthStateStore.TryGetExpiry(paths, "openai", out var expiry));
        Assert.Null(expiry);
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

    [Theory]
    [InlineData("""{ "Providers": { "copilot": { "Type": "github-copilot", "OAuthTokenExpiry": "2026-06-01T00:00:00.0000000+00:00" } } }""")]
    [InlineData("""
        // The operator's comment.
        { "Providers": { "copilot": { "Type": "github-copilot" } }, }
        """)]
    public void PersistTokens_leaves_netclaw_json_unchanged(string netclawJson)
    {
        using var dir = new DisposableTempDir();
        var paths = new NetclawPaths(dir.Path);
        paths.EnsureDirectoriesExist();
        File.WriteAllText(paths.NetclawConfigPath, netclawJson);
        var before = File.ReadAllBytes(paths.NetclawConfigPath);

        OAuthTokenPersistence.PersistTokens(
            paths,
            "copilot",
            new OAuthDeviceFlowResult(
                new SensitiveString("access-1"),
                new SensitiveString("refresh-1"),
                DateTimeOffset.Parse("2026-06-01T01:00:00+00:00")),
            new NullSecretsProtector());

        Assert.Equal(before, File.ReadAllBytes(paths.NetclawConfigPath));
        using var secrets = JsonDocument.Parse(File.ReadAllText(paths.SecretsPath));
        Assert.Equal(
            "access-1",
            secrets.RootElement.GetProperty("Providers").GetProperty("copilot").GetProperty("OAuthAccessToken").GetString());
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

        Assert.True(File.Exists(paths.SecretsPath));
        Assert.False(File.Exists(paths.NetclawConfigPath));
        Assert.True(OAuthStateStore.TryGetExpiry(paths, "copilot", out var expiry));
        Assert.Equal(DateTimeOffset.Parse("2026-06-01T00:00:00+00:00"), expiry);
    }

    [Fact]
    public void PersistTokens_writes_secrets_before_the_state_file()
    {
        using var dir = new DisposableTempDir();
        var paths = new NetclawPaths(dir.Path);
        paths.EnsureDirectoriesExist();
        Directory.CreateDirectory(paths.OAuthStatePath);

        // Linux and macOS fail the rename onto a directory with IOException. Windows fails it with
        // UnauthorizedAccessException.
        var failure = Record.Exception(() => OAuthTokenPersistence.PersistTokens(
            paths,
            "copilot",
            new OAuthDeviceFlowResult(new SensitiveString("access-1"), new SensitiveString("refresh-1"), DateTimeOffset.Parse("2026-06-01T01:00:00+00:00")),
            new NullSecretsProtector()));
        Assert.True(failure is IOException or UnauthorizedAccessException, $"Unexpected: {failure?.GetType().FullName ?? "no exception"}");

        using var secrets = JsonDocument.Parse(File.ReadAllText(paths.SecretsPath));
        Assert.Equal(
            "refresh-1",
            secrets.RootElement.GetProperty("Providers").GetProperty("copilot").GetProperty("OAuthRefreshToken").GetString());
    }

    [Fact]
    public void LoadTokens_reads_the_expiry_from_oauth_state_json()
    {
        using var dir = new DisposableTempDir();
        var paths = new NetclawPaths(dir.Path);
        paths.EnsureDirectoriesExist();
        var expiry = DateTimeOffset.Parse("2026-06-01T01:00:00+00:00");
        OAuthTokenPersistence.PersistTokens(
            paths,
            "copilot",
            new OAuthDeviceFlowResult(new SensitiveString("access-1"), new SensitiveString("refresh-1"), expiry),
            new NullSecretsProtector());

        var loaded = OAuthTokenPersistence.LoadTokens(paths, "copilot");

        Assert.Equal(expiry, loaded!.ExpiresAt);
    }

    [Fact]
    public void PersistTokens_keeps_each_providers_expiry_apart()
    {
        using var dir = new DisposableTempDir();
        var paths = new NetclawPaths(dir.Path);
        paths.EnsureDirectoriesExist();
        var first = DateTimeOffset.Parse("2026-06-01T01:00:00+00:00");
        var second = DateTimeOffset.Parse("2026-06-02T02:00:00+00:00");

        OAuthTokenPersistence.PersistTokens(paths, "copilot-a",
            new OAuthDeviceFlowResult(new SensitiveString("a"), null, first), new NullSecretsProtector());
        OAuthTokenPersistence.PersistTokens(paths, "copilot-b",
            new OAuthDeviceFlowResult(new SensitiveString("b"), null, second), new NullSecretsProtector());

        Assert.True(OAuthStateStore.TryGetExpiry(paths, "copilot-a", out var a));
        Assert.True(OAuthStateStore.TryGetExpiry(paths, "copilot-b", out var b));
        Assert.Equal(first, a);
        Assert.Equal(second, b);
    }
}
