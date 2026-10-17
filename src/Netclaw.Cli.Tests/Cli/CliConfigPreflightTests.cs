// -----------------------------------------------------------------------
// <copyright file="CliConfigPreflightTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Text.Json.Nodes;
using Netclaw.Cli;
using Netclaw.Cli.Doctor;
using Netclaw.Configuration;
using Netclaw.Tests.Utilities;
using Xunit;

namespace Netclaw.Cli.Tests.Cli;

[Collection(LegacyModelEnvironmentCollection.Name)]
public sealed class CliConfigPreflightTests : IDisposable
{
    private readonly DisposableTempDir _dir = new();
    private readonly NetclawPaths _paths;
    private readonly string? _originalDaemonEndpoint;

    public CliConfigPreflightTests()
    {
        _originalDaemonEndpoint = Environment.GetEnvironmentVariable("NETCLAW_DAEMON_ENDPOINT");
        Environment.SetEnvironmentVariable("NETCLAW_DAEMON_ENDPOINT", null);
        _paths = new NetclawPaths(_dir.Path);
        _paths.EnsureDirectoriesExist();
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("NETCLAW_DAEMON_ENDPOINT", _originalDaemonEndpoint);
        _dir.Dispose();
    }

    [Fact]
    public void TryWriteMissingConfig_Text_PrintsInitGuidanceAndBlocksCommand()
    {
        using var writer = new StringWriter();

        var blocked = CliConfigPreflight.TryWriteMissingConfig(_paths, jsonOutput: false, writer, out var exitCode);

        Assert.True(blocked);
        Assert.Equal(1, exitCode);
        Assert.Equal(CliConfigPreflight.MissingConfigMessage, writer.ToString().Trim());
    }

    [Fact]
    public void An_oauth_state_file_that_cannot_be_read_is_named_in_the_preflight_message()
    {
        File.WriteAllText(_paths.OAuthStatePath, "{ broken");

        var unreadable = CliConfigPreflight.TryDescribeUnreadableConfig(_paths, out var message, out var fix);

        Assert.True(unreadable);
        Assert.StartsWith("Couldn't read oauth-state.json:", message);
        Assert.Contains(_paths.OAuthStatePath, fix);
    }

    [Fact]
    public void The_oauth_state_advice_names_both_ways_to_get_the_dates_back_and_promises_no_refresh_rewrite()
    {
        File.WriteAllText(_paths.OAuthStatePath, "{ broken");

        Assert.True(CliConfigPreflight.TryDescribeUnreadableConfig(_paths, out _, out var fix));

        Assert.StartsWith($"Delete {_paths.OAuthStatePath}.", fix);
        Assert.Contains("`netclaw doctor --fix`", fix);
        Assert.Contains("`netclaw provider`", fix);
        Assert.DoesNotContain("writes it again", fix);
    }

    [Fact]
    public async Task Deleting_an_unreadable_oauth_state_file_and_running_doctor_fix_brings_the_dates_back()
    {
        File.WriteAllText(_paths.NetclawConfigPath, """
            { "configVersion": 1, "Providers": { "copilot": { "Type": "github-copilot", "OAuthTokenExpiry": "2027-03-03T00:00:00+00:00" } } }
            """);
        File.WriteAllText(_paths.OAuthStatePath, "{ broken");
        Assert.True(CliConfigPreflight.TryDescribeUnreadableConfig(_paths, out _, out _));
        File.Delete(_paths.OAuthStatePath);
        var service = new DoctorFixService(_paths, Path.Combine(_paths.BasePath, "unused.service"), systemdEnabled: false);

        await service.ApplyAsync(await service.BuildPlanAsync(TestContext.Current.CancellationToken), TestContext.Current.CancellationToken);

        Assert.True(OAuthStateStore.TryGetExpiry(_paths, "copilot", out var expiry));
        Assert.Equal(DateTimeOffset.Parse("2027-03-03T00:00:00+00:00"), expiry);
        Assert.False(CliConfigPreflight.TryDescribeUnreadableConfig(_paths, out _, out _));
    }

    [Fact]
    public void An_oauth_state_file_with_a_foreign_key_is_named_in_the_preflight_message()
    {
        File.WriteAllText(_paths.OAuthStatePath, """{ "Providers": { "p": { "Endpoint": "https://attacker.example" } } }""");

        var unreadable = CliConfigPreflight.TryDescribeUnreadableConfig(_paths, out var message, out _);

        Assert.True(unreadable);
        Assert.StartsWith("Couldn't read oauth-state.json:", message);
        Assert.Contains("'Providers:p:Endpoint'", message);
    }

    [Fact]
    public void TryWriteMissingConfig_Json_PrintsNotConfiguredPayload()
    {
        using var writer = new StringWriter();

        var blocked = CliConfigPreflight.TryWriteMissingConfig(_paths, jsonOutput: true, writer, out var exitCode);

        Assert.True(blocked);
        Assert.Equal(1, exitCode);

        var json = JsonNode.Parse(writer.ToString())!.AsObject();
        Assert.Equal("not-configured", json["overall"]!.GetValue<string>());
        Assert.Equal(CliConfigPreflight.MissingConfigMessage, json["message"]!.GetValue<string>());
    }

    [Fact]
    public void TryWriteMissingConfig_ConfigExists_AllowsCommand()
    {
        File.WriteAllText(_paths.NetclawConfigPath, "{}");
        using var writer = new StringWriter();

        var blocked = CliConfigPreflight.TryWriteMissingConfig(_paths, jsonOutput: false, writer, out var exitCode);

        Assert.False(blocked);
        Assert.Equal(0, exitCode);
        Assert.Equal(string.Empty, writer.ToString());
    }

    [Fact]
    public void TryWriteMissingConfig_RemoteEndpointEnvVar_AllowsCommand()
    {
        Environment.SetEnvironmentVariable("NETCLAW_DAEMON_ENDPOINT", "http://127.0.0.1:5299");
        try
        {
            using var writer = new StringWriter();

            var blocked = CliConfigPreflight.TryWriteMissingConfig(_paths, jsonOutput: false, writer, out var exitCode);

            // The daemon is explicitly remote — its config lives on the daemon
            // host, so a missing local config must not block the command.
            Assert.False(blocked);
            Assert.Equal(0, exitCode);
            Assert.Equal(string.Empty, writer.ToString());
        }
        finally
        {
            Environment.SetEnvironmentVariable("NETCLAW_DAEMON_ENDPOINT", null);
        }
    }

    [Fact]
    public void TryWriteMissingConfig_PairedClientEndpoint_AllowsCommand()
    {
        Netclaw.Cli.Config.ClientConfigFile.WriteEndpoint(_paths, "https://daemon.example.net:5299");
        using var writer = new StringWriter();

        var blocked = CliConfigPreflight.TryWriteMissingConfig(_paths, jsonOutput: false, writer, out var exitCode);

        Assert.False(blocked);
        Assert.Equal(0, exitCode);
        Assert.Equal(string.Empty, writer.ToString());
    }

    [Fact]
    public void TryWriteMissingChatConfig_HeadlessJson_PrintsJsonPayload()
    {
        using var writer = new StringWriter();

        var blocked = CliConfigPreflight.TryWriteMissingChatConfig(
            _paths,
            mode: "headless",
            chatJsonOutput: true,
            writer,
            out var exitCode);

        var json = JsonNode.Parse(writer.ToString())!.AsObject();

        Assert.True(blocked);
        Assert.Equal(1, exitCode);
        Assert.Equal("not-configured", json["overall"]!.GetValue<string>());
        Assert.Equal(CliConfigPreflight.MissingConfigMessage, json["message"]!.GetValue<string>());
    }
}
