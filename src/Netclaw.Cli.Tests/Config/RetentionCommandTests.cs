// -----------------------------------------------------------------------
// <copyright file="RetentionCommandTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Cli.Config;
using Netclaw.Configuration;
using Netclaw.Tests.Utilities;
using Xunit;

namespace Netclaw.Cli.Tests.Config;

public sealed class RetentionCommandTests : IDisposable
{
    private readonly DisposableTempDir _dir = new();
    private readonly NetclawPaths _paths;
    private readonly StringWriter _output = new();
    private readonly StringWriter _error = new();

    public RetentionCommandTests()
    {
        _paths = new NetclawPaths(_dir.Path);
        _paths.EnsureDirectoriesExist();
        File.WriteAllText(_paths.NetclawConfigPath, """{ "configVersion": 1 }""");
    }

    public void Dispose()
    {
        _output.Dispose();
        _error.Dispose();
        _dir.Dispose();
    }

    private int Run(params string[] args) => ConfigCommand.Run(["config", "retention", .. args], _paths, _output, _error);

    [Fact]
    public void With_no_option_it_shows_the_default()
    {
        var before = File.ReadAllText(_paths.NetclawConfigPath);

        Assert.Equal(0, Run());

        Assert.Equal("Daemon and crash logs: keep 14 days (default)" + Environment.NewLine, _output.ToString());
        Assert.Equal(before, File.ReadAllText(_paths.NetclawConfigPath));
    }

    [Fact]
    public void Logs_days_sets_the_value_and_says_the_daemon_applies_it()
    {
        Assert.Equal(0, Run("--logs-days", "30"));

        Assert.Equal(
            "Daemon and crash logs: keep 30 days" + Environment.NewLine
            + "A running daemon applies the change automatically." + Environment.NewLine,
            _output.ToString());
        Assert.Equal(30, RetentionConfigStore.Read(_paths, RetentionSettings.Logs).Days);
        Assert.Equal(string.Empty, _error.ToString());
    }

    [Fact]
    public void Zero_keeps_logs_forever()
    {
        Assert.Equal(0, Run("--logs-days", "0"));

        Assert.Contains("keep forever", _output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Setting_the_default_on_an_unset_key_writes_nothing()
    {
        var before = File.ReadAllText(_paths.NetclawConfigPath);

        Assert.Equal(0, Run("--logs-days", "14"));

        Assert.Equal(before, File.ReadAllText(_paths.NetclawConfigPath));
        Assert.Equal("Daemon and crash logs: keep 14 days (default)" + Environment.NewLine, _output.ToString());
    }

    [Theory]
    [InlineData("abc")]
    [InlineData("-1")]
    [InlineData("36501")]
    [InlineData("1.5")]
    [InlineData("")]
    public void An_invalid_value_fails_with_one_line_and_leaves_the_file_alone(string value)
    {
        var before = File.ReadAllText(_paths.NetclawConfigPath);

        Assert.Equal(1, Run("--logs-days", value));

        Assert.Equal("--logs-days: Days must be a whole number from 0 to 36500 (0 keeps the data forever)." + Environment.NewLine, _error.ToString());
        Assert.Equal(string.Empty, _output.ToString());
        Assert.Equal(before, File.ReadAllText(_paths.NetclawConfigPath));
    }

    [Fact]
    public void A_missing_value_fails()
    {
        Assert.Equal(1, Run("--logs-days"));

        Assert.Equal("--logs-days needs a number of days." + Environment.NewLine, _error.ToString());
    }

    [Fact]
    public void An_unknown_option_fails()
    {
        Assert.Equal(1, Run("--nope", "3"));

        Assert.Equal("Unknown option '--nope'. Run `netclaw config retention --help`." + Environment.NewLine, _error.ToString());
    }

    [Fact]
    public void Without_a_config_it_asks_for_init()
    {
        File.Delete(_paths.NetclawConfigPath);

        Assert.Equal(1, Run("--logs-days", "30"));

        Assert.Equal(ConfigCommand.MissingConfigMessage + Environment.NewLine, _error.ToString());
        Assert.False(File.Exists(_paths.NetclawConfigPath));
    }

    [Fact]
    public void Help_lists_every_retention_option()
    {
        Assert.Equal(0, Run("--help"));

        var help = _output.ToString();
        Assert.Contains("Usage: netclaw config retention [options]", help, StringComparison.Ordinal);
        foreach (var setting in RetentionSettings.All)
            Assert.Contains(setting.CliOption, help, StringComparison.Ordinal);
    }

    [Fact]
    public void A_malformed_config_fails_with_a_message()
    {
        File.WriteAllText(_paths.NetclawConfigPath, "{ not json");

        Assert.Equal(1, Run());

        Assert.StartsWith("Could not use netclaw.json:", _error.ToString(), StringComparison.Ordinal);
    }
}
