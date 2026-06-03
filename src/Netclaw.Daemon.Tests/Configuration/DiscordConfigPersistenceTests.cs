// -----------------------------------------------------------------------
// <copyright file="DiscordConfigPersistenceTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Text.Json;
using System.Text.Json.Nodes;
using Netclaw.Configuration;
using Netclaw.Configuration.Secrets;
using Netclaw.Daemon.Configuration;
using Netclaw.Tests.Utilities;
using Xunit;

namespace Netclaw.Daemon.Tests.Configuration;

public sealed class DiscordConfigPersistenceTests : IDisposable
{
    private readonly DisposableTempDir _dir = new();
    private readonly NetclawPaths _paths;

    public DiscordConfigPersistenceTests()
    {
        _paths = new NetclawPaths(_dir.Path);
        Directory.CreateDirectory(Path.GetDirectoryName(_paths.NetclawConfigPath)!);
    }

    [Fact]
    public void Read_returns_defaults_when_no_files_exist()
    {
        var persistence = new DiscordConfigPersistence(_paths);
        var got = persistence.Read();

        Assert.False(got.Enabled);
        Assert.False(got.BotTokenIsSet);
        Assert.Null(got.DefaultChannelId);
        Assert.True(got.MentionOnly);
        Assert.Empty(got.AllowedChannelIds);
    }

    [Fact]
    public void Write_then_Read_round_trips_non_secret_fields()
    {
        var persistence = new DiscordConfigPersistence(_paths);

        persistence.Write(new DiscordConfigWire.PutRequest
        {
            Enabled = true,
            DefaultChannelId = "123",
            AllowDirectMessages = true,
            MentionOnly = false,
            MentionRequiredInDm = true,
            AllowedChannelIds = ["111", "222"],
            AllowedUserIds = ["u1"],
            ChannelAudiences = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["111"] = "team-ops",
            },
        });

        var got = persistence.Read();
        Assert.True(got.Enabled);
        Assert.Equal("123", got.DefaultChannelId);
        Assert.True(got.AllowDirectMessages);
        Assert.False(got.MentionOnly);
        Assert.True(got.MentionRequiredInDm);
        Assert.Equal(new[] { "111", "222" }, got.AllowedChannelIds);
        Assert.Equal(new[] { "u1" }, got.AllowedUserIds);
        Assert.Equal("team-ops", got.ChannelAudiences["111"]);
        Assert.False(got.BotTokenIsSet);
    }

    [Fact]
    public void Token_null_leaves_existing_secret_untouched()
    {
        File.WriteAllText(_paths.SecretsPath,
            """{ "Discord": { "BotToken": "existing-token" }, "DeviceToken": "keep-me" }""");

        var persistence = new DiscordConfigPersistence(_paths);
        persistence.Write(new DiscordConfigWire.PutRequest { Enabled = true, BotToken = null });

        var secrets = JsonNode.Parse(File.ReadAllText(_paths.SecretsPath))!.AsObject();
        Assert.Equal("existing-token", secrets["Discord"]!["BotToken"]!.GetValue<string>());
        Assert.Equal("keep-me", secrets["DeviceToken"]!.GetValue<string>());

        Assert.True(persistence.Read().BotTokenIsSet);
    }

    [Fact]
    public void Token_empty_clears_secret_but_preserves_unrelated_keys()
    {
        File.WriteAllText(_paths.SecretsPath,
            """{ "Discord": { "BotToken": "existing" }, "DeviceToken": "keep" }""");

        var persistence = new DiscordConfigPersistence(_paths);
        persistence.Write(new DiscordConfigWire.PutRequest { Enabled = false, BotToken = string.Empty });

        var secrets = JsonNode.Parse(File.ReadAllText(_paths.SecretsPath))!.AsObject();
        Assert.False(secrets.ContainsKey("Discord"));
        Assert.Equal("keep", secrets["DeviceToken"]!.GetValue<string>());
        Assert.False(persistence.Read().BotTokenIsSet);
    }

    [Fact]
    public void Token_value_replaces_secret()
    {
        var persistence = new DiscordConfigPersistence(_paths);
        persistence.Write(new DiscordConfigWire.PutRequest { Enabled = true, BotToken = "fresh-token" });

        var secrets = JsonNode.Parse(File.ReadAllText(_paths.SecretsPath))!.AsObject();
        Assert.Equal("fresh-token", secrets["Discord"]!["BotToken"]!.GetValue<string>());
        Assert.True(persistence.Read().BotTokenIsSet);
    }

    [Fact]
    public void Token_write_failure_leaves_config_file_unchanged()
    {
        File.WriteAllText(_paths.NetclawConfigPath,
            """{ "Discord": { "Enabled": false } }""");

        var persistence = new DiscordConfigPersistence(_paths, new ThrowingSecretsProtector());

        Assert.Throws<InvalidOperationException>(() =>
            persistence.Write(new DiscordConfigWire.PutRequest { Enabled = true, BotToken = "fresh-token" }));

        var config = JsonNode.Parse(File.ReadAllText(_paths.NetclawConfigPath))!.AsObject();
        Assert.False(config["Discord"]!["Enabled"]!.GetValue<bool>());
        Assert.False(File.Exists(_paths.SecretsPath));
    }

    [Fact]
    public void Write_preserves_unrelated_top_level_config_keys()
    {
        File.WriteAllText(_paths.NetclawConfigPath,
            """{ "Slack": { "Enabled": true }, "Discord": { "Enabled": false } }""");

        var persistence = new DiscordConfigPersistence(_paths);
        persistence.Write(new DiscordConfigWire.PutRequest { Enabled = true });

        var root = JsonNode.Parse(File.ReadAllText(_paths.NetclawConfigPath))!.AsObject();
        Assert.True(root["Slack"]!["Enabled"]!.GetValue<bool>());
        Assert.True(root["Discord"]!["Enabled"]!.GetValue<bool>());
    }

    [Fact]
    public void PutResponse_always_signals_restart_required()
    {
        var persistence = new DiscordConfigPersistence(_paths);
        var response = persistence.Write(new DiscordConfigWire.PutRequest { Enabled = false });
        Assert.True(response.RestartRequired);
        Assert.Equal(_paths.NetclawConfigPath, response.ConfigPath);
        Assert.Equal(_paths.SecretsPath, response.SecretsPath);
    }

    public void Dispose() => _dir.Dispose();

    private sealed class ThrowingSecretsProtector : ISecretsProtector
    {
        public string Protect(string plaintext) => throw new InvalidOperationException("secret write failed");

        public string Unprotect(string ciphertext) => ciphertext;
    }
}
