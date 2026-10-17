// -----------------------------------------------------------------------
// <copyright file="OAuthStateStoreTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Microsoft.Extensions.Configuration;
using Netclaw.Tests.Utilities;
using Xunit;

namespace Netclaw.Configuration.Tests;

public sealed class OAuthStateStoreTests : IDisposable
{
    private static readonly DateTimeOffset Expiry = DateTimeOffset.Parse("2026-06-01T01:00:00+00:00");

    private readonly DisposableTempDir _dir = new();
    private readonly NetclawPaths _paths;

    public OAuthStateStoreTests()
    {
        _paths = new NetclawPaths(_dir.Path);
        _paths.EnsureDirectoriesExist();
    }

    [Fact]
    public void A_provider_without_an_entry_is_not_found()
    {
        Assert.False(OAuthStateStore.TryGetExpiry(_paths, "copilot", out _));

        OAuthStateStore.SetExpiry(_paths, "other", Expiry);

        Assert.False(OAuthStateStore.TryGetExpiry(_paths, "copilot", out _));
    }

    [Fact]
    public void The_provider_name_matches_without_case()
    {
        OAuthStateStore.SetExpiry(_paths, "Copilot", Expiry);
        OAuthStateStore.SetExpiry(_paths, "copilot", Expiry.AddHours(1));

        Assert.True(OAuthStateStore.TryGetExpiry(_paths, "COPILOT", out var expiry));
        Assert.Equal(Expiry.AddHours(1), expiry);
        using var state = System.Text.Json.JsonDocument.Parse(File.ReadAllText(_paths.OAuthStatePath));
        Assert.Single(state.RootElement.GetProperty("Providers").EnumerateObject());
    }

    [Fact]
    public void Rename_moves_the_entry_and_Remove_deletes_it()
    {
        OAuthStateStore.SetExpiry(_paths, "old", Expiry);
        OAuthStateStore.SetExpiry(_paths, "keep", Expiry.AddDays(1));

        OAuthStateStore.Rename(_paths, "old", "new");

        Assert.False(OAuthStateStore.TryGetExpiry(_paths, "old", out _));
        Assert.True(OAuthStateStore.TryGetExpiry(_paths, "new", out var moved));
        Assert.Equal(Expiry, moved);

        OAuthStateStore.Remove(_paths, "new");

        Assert.False(OAuthStateStore.TryGetExpiry(_paths, "new", out _));
        Assert.True(OAuthStateStore.TryGetExpiry(_paths, "keep", out var kept));
        Assert.Equal(Expiry.AddDays(1), kept);
    }

    [Fact]
    public void Remove_and_Rename_do_not_create_the_file()
    {
        OAuthStateStore.Remove(_paths, "copilot");
        OAuthStateStore.Rename(_paths, "copilot", "other");

        Assert.False(File.Exists(_paths.OAuthStatePath));
    }

    [Fact]
    public void A_file_that_is_not_JSON_fails_loudly()
    {
        File.WriteAllText(_paths.OAuthStatePath, "{ broken");

        var read = Assert.Throws<InvalidDataException>(() => OAuthStateStore.TryGetExpiry(_paths, "copilot", out _));
        var write = Assert.Throws<InvalidDataException>(() => OAuthStateStore.SetExpiry(_paths, "copilot", Expiry));

        Assert.StartsWith("Couldn't read oauth-state.json:", read.Message);
        Assert.StartsWith("Couldn't read oauth-state.json:", write.Message);
        Assert.Equal("{ broken", File.ReadAllText(_paths.OAuthStatePath));
    }

    [Fact]
    public void An_expiry_that_is_not_a_date_fails_loudly()
    {
        File.WriteAllText(_paths.OAuthStatePath, """{ "Providers": { "copilot": { "OAuthTokenExpiry": "garbage" } } }""");

        Assert.Throws<InvalidDataException>(() => OAuthStateStore.TryGetExpiry(_paths, "copilot", out _));
    }

    [Fact]
    public void The_daemon_sources_read_the_state_after_secrets_and_before_the_environment()
    {
        File.WriteAllText(_paths.NetclawConfigPath, """{ "Providers": { "copilot": { "Type": "github-copilot", "OAuthTokenExpiry": "2026-01-01T00:00:00+00:00" } } }""");
        OAuthStateStore.SetExpiry(_paths, "copilot", Expiry);

        var configuration = new ConfigurationBuilder().AddNetclawDaemonSources(_paths).Build();
        var providers = ProviderConfigurationLoader.Load(configuration.GetSection("Providers"));

        Assert.Equal(Expiry, providers["copilot"].OAuthTokenExpiry);
    }

    [Fact]
    public void A_null_state_entry_hides_the_retired_key_in_netclaw_json()
    {
        File.WriteAllText(_paths.NetclawConfigPath, """{ "Providers": { "copilot": { "Type": "github-copilot", "OAuthTokenExpiry": "2026-01-01T00:00:00+00:00" } } }""");
        OAuthStateStore.SetExpiry(_paths, "copilot", null);

        var configuration = new ConfigurationBuilder().AddNetclawDaemonSources(_paths).Build();
        var providers = ProviderConfigurationLoader.Load(configuration.GetSection("Providers"));

        Assert.Null(providers["copilot"].OAuthTokenExpiry);
        Assert.Empty(OAuthStateStore.FindRetiredExpiryInUse(_paths));
    }

    [Fact]
    public void An_old_netclaw_json_expiry_is_read_until_the_state_has_an_entry()
    {
        File.WriteAllText(_paths.NetclawConfigPath, """
            // A comment the operator wrote.
            { "Providers": { "a": { "Type": "github-copilot", "OAuthTokenExpiry": "2026-01-01T00:00:00+00:00" },
                             "b": { "Type": "github-copilot", "OAuthTokenExpiry": "2026-01-01T00:00:00+00:00" },
                             "c": { "Type": "github-copilot" } }, }
            """);
        OAuthStateStore.SetExpiry(_paths, "b", Expiry);

        var configuration = new ConfigurationBuilder().AddNetclawDaemonSources(_paths).Build();
        var providers = ProviderConfigurationLoader.Load(configuration.GetSection("Providers"));

        Assert.Equal(DateTimeOffset.Parse("2026-01-01T00:00:00+00:00"), providers["a"].OAuthTokenExpiry);
        Assert.Equal(["a"], OAuthStateStore.FindRetiredExpiryInUse(_paths));
    }

    [Fact]
    public void An_empty_expiry_means_no_expiry_for_the_store_and_for_the_binder()
    {
        File.WriteAllText(_paths.NetclawConfigPath, """{ "Providers": { "copilot": { "Type": "github-copilot", "OAuthTokenExpiry": "2026-01-01T00:00:00+00:00" } } }""");
        File.WriteAllText(_paths.OAuthStatePath, """{ "Providers": { "copilot": { "OAuthTokenExpiry": "" } } }""");

        Assert.True(OAuthStateStore.TryGetExpiry(_paths, "copilot", out var stored));
        Assert.Null(stored);

        var configuration = new ConfigurationBuilder().AddNetclawDaemonSources(_paths).Build();
        var providers = ProviderConfigurationLoader.Load(configuration.GetSection("Providers"));
        Assert.Null(providers["copilot"].OAuthTokenExpiry);
    }

    [Fact]
    public void An_expiry_that_is_not_a_date_stops_the_load_and_names_oauth_state_json()
    {
        File.WriteAllText(_paths.OAuthStatePath, """{ "Providers": { "copilot": { "OAuthTokenExpiry": "garbage" } } }""");

        var daemon = Assert.Throws<InvalidDataException>(() => new ConfigurationBuilder().AddNetclawDaemonSources(_paths).Build());

        var message = DaemonConfigurationSources.DescribeLoadFailure(daemon);
        Assert.StartsWith("Couldn't read oauth-state.json:", message);
        Assert.Contains("Providers:copilot:OAuthTokenExpiry", message);
    }

    [Fact]
    public void Comments_and_trailing_commas_are_read_by_the_store_and_by_the_configuration_source()
    {
        File.WriteAllText(_paths.OAuthStatePath, """
            // An operator comment.
            { "Providers": { "copilot": { "OAuthTokenExpiry": "2026-06-01T01:00:00+00:00", }, }, }
            """);

        Assert.True(OAuthStateStore.TryGetExpiry(_paths, "copilot", out var stored));
        Assert.Equal(Expiry, stored);
        var configuration = new ConfigurationBuilder().AddNetclawDaemonSources(_paths).Build();
        Assert.Equal(Expiry, ProviderConfigurationLoader.Load(configuration.GetSection("Providers"))["copilot"].OAuthTokenExpiry);

        OAuthStateStore.SetExpiry(_paths, "copilot", Expiry.AddHours(1));

        Assert.True(OAuthStateStore.TryGetExpiry(_paths, "copilot", out var updated));
        Assert.Equal(Expiry.AddHours(1), updated);
    }

    public void Dispose() => _dir.Dispose();
}
