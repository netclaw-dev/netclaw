// -----------------------------------------------------------------------
// <copyright file="RetentionCommandTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Microsoft.Extensions.Configuration;
using Netclaw.Cli.Config;
using Netclaw.Configuration;
using Netclaw.Tests.Utilities;
using Xunit;

namespace Netclaw.Cli.Tests.Config;

[Collection(Netclaw.Cli.Tests.RetentionEnvironmentCollection.Name)]
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

    [Fact]
    public void A_retention_option_does_not_open_the_dashboard()
    {
        Assert.True(ConfigCommand.Handle(["config", "retention", "--logs-days", "5"], _paths, out var exitCode, _output, _error));
        Assert.Equal(0, exitCode);
    }

    [Theory]
    [InlineData("--help")]
    [InlineData("retention")]
    [InlineData("extra")]
    public void Any_argument_is_handled_without_the_dashboard(string argument)
    {
        Assert.True(ConfigCommand.Handle(["config", argument], _paths, out _, _output, _error));
    }

    [Fact]
    public void No_argument_opens_the_dashboard_when_there_is_a_config()
    {
        Assert.False(ConfigCommand.Handle(["config"], _paths, out var exitCode, _output, _error));
        Assert.Equal(0, exitCode);
    }

    [Fact]
    public void Setting_a_value_that_is_already_stored_does_not_rewrite_the_file()
    {
        const string compact = """{"configVersion":1,"Retention":{"Logs":{"Days":30}}}""";
        File.WriteAllText(_paths.NetclawConfigPath, compact);

        Assert.Equal(0, Run("--logs-days", "30"));

        Assert.Equal(compact, File.ReadAllText(_paths.NetclawConfigPath));
        Assert.DoesNotContain("applies the change", _output.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("30.0")]
    [InlineData("\"abc\"")]
    [InlineData("true")]
    [InlineData("99999999999")]
    public void A_stored_value_that_is_not_an_integer_is_reported_and_any_valid_value_replaces_it(string stored)
    {
        File.WriteAllText(_paths.NetclawConfigPath, $$"""{ "configVersion": 1, "Retention": { "Logs": { "Days": {{stored}} } } }""");

        Assert.Equal(0, Run());

        Assert.Equal("Daemon and crash logs: keep 14 days (default)" + Environment.NewLine, _output.ToString());
        Assert.StartsWith("warning: Retention:Logs:Days value '", _error.ToString(), StringComparison.Ordinal);
        Assert.EndsWith("' is not an integer; using the default of 14 days." + Environment.NewLine, _error.ToString(), StringComparison.Ordinal);

        // The default is a valid value too: setting it must rewrite the bad text.
        Assert.Equal(0, Run("--logs-days", "14"));
        Assert.Equal(new RetentionValue(14, true), RetentionConfigStore.Read(_paths, RetentionSettings.Logs));
    }

    [Fact]
    public void A_flat_colon_key_is_updated_in_place()
    {
        File.WriteAllText(_paths.NetclawConfigPath, """{ "Retention:Logs:Days": 5 }""");

        Assert.Equal(0, Run());
        Assert.Contains("keep 5 days", _output.ToString(), StringComparison.Ordinal);
        Assert.Equal(0, Run("--logs-days", "9"));

        var text = File.ReadAllText(_paths.NetclawConfigPath);
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(text, "Days"));
        Assert.Equal("9", new ConfigurationBuilder().AddJsonFile(_paths.NetclawConfigPath).Build()["Retention:Logs:Days"]);
    }

    [Fact]
    public void A_spelling_the_command_cannot_edit_fails_without_writing()
    {
        const string halfFlat = """{ "Retention:Logs": { "Days": 5 } }""";
        File.WriteAllText(_paths.NetclawConfigPath, halfFlat);

        Assert.Equal(1, Run("--logs-days", "9"));

        Assert.Equal("Could not use netclaw.json: Retention:Logs:Days is set in a spelling this command cannot edit. Edit netclaw.json by hand." + Environment.NewLine, _error.ToString());
        Assert.Equal(halfFlat, File.ReadAllText(_paths.NetclawConfigPath));
    }

    [Fact]
    public void An_environment_override_is_named_in_a_warning()
    {
        const string name = "NETCLAW_Retention__Logs__Days";
        Environment.SetEnvironmentVariable(name, "2");
        try
        {
            Assert.Equal(0, Run("--logs-days", "30"));

            Assert.Equal($"warning: {name} is set and overrides netclaw.json for the daemon." + Environment.NewLine, _error.ToString());
        }
        finally
        {
            Environment.SetEnvironmentVariable(name, null);
        }
    }

    [Theory]
    [InlineData("""{"configVersion":1,"Daemon":{"ExposureMode":"local"},"Retention:Logs":{"Days":""}}""")]
    [InlineData("""{"configVersion":1,"Retention:Logs":{"Days":null}}""")]
    [InlineData("""{"configVersion":1,"Retention:Logs":{"Days":" "}}""")]
    [InlineData("""{"configVersion":1,"Retention:Logs":{"Days":{}}}""")]
    [InlineData("""{"configVersion":1,"Retention:Logs":{"Days":[]}}""")]
    [InlineData("""{"configVersion":1,"Retention":{"Logs:Days":null}}""")]
    [InlineData("""{"configVersion":1,"Retention":{"Logs:Days":""}}""")]
    public void A_blank_value_in_a_spelling_it_cannot_edit_is_refused_and_nothing_is_written(string content)
    {
        File.WriteAllText(_paths.NetclawConfigPath, content);

        Assert.Equal(1, Run("--logs-days", "9"));

        Assert.Equal(
            "Could not use netclaw.json: Retention:Logs:Days is set in a spelling this command cannot edit. Edit netclaw.json by hand." + Environment.NewLine,
            _error.ToString());
        Assert.Equal(content, File.ReadAllText(_paths.NetclawConfigPath));
    }

    [Fact]
    public void A_duplicate_from_the_final_version_assignment_is_refused_before_persistence()
    {
        const string content = """{"configversion":1}""";
        File.WriteAllText(_paths.NetclawConfigPath, content);

        Assert.Equal(1, Run("--logs-days", "9"));

        Assert.StartsWith("Could not use netclaw.json: The new netclaw.json would not load:", _error.ToString(), StringComparison.Ordinal);
        Assert.Equal(content, File.ReadAllText(_paths.NetclawConfigPath));
        Assert.Equal("1", new ConfigurationBuilder().AddJsonFile(_paths.NetclawConfigPath).Build()["configVersion"]);
    }

    [Fact]
    public void A_flat_key_is_matched_without_case()
    {
        File.WriteAllText(_paths.NetclawConfigPath, """{"configVersion":1,"retention:logs:days":3}""");

        Assert.Equal(0, Run("--logs-days", "9"));

        var text = File.ReadAllText(_paths.NetclawConfigPath);
        Assert.Equal(9, RetentionConfigStore.Read(_paths, RetentionSettings.Logs).Days);
        Assert.DoesNotContain("\"Retention\"", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Showing_the_value_accepts_comments_and_trailing_commas()
    {
        const string content = "{\n  // kept for a month\n  \"configVersion\": 1,\n  \"Retention\": { \"Logs\": { \"Days\": 30, }, },\n}";
        File.WriteAllText(_paths.NetclawConfigPath, content);

        Assert.Equal(0, Run());

        Assert.Equal("Daemon and crash logs: keep 30 days" + Environment.NewLine, _output.ToString());
        Assert.Equal(content, File.ReadAllText(_paths.NetclawConfigPath));
    }

    [Theory]
    [InlineData("NETCLAW_RETENTION__LOGS__DAYS", "30")]
    [InlineData("netclaw_retention__logs__days", "30")]
    [InlineData("Netclaw_Retention__Logs__Days", "30")]
    [InlineData("NETCLAW_Retention:Logs:Days", "30")]
    [InlineData("NETCLAW_Retention__Logs__Days", "")]
    public void An_environment_variable_in_any_casing_is_reported_by_its_own_name(string name, string value)
    {
        Environment.SetEnvironmentVariable(name, value);
        try
        {
            Assert.Equal(0, Run());

            Assert.Contains($"warning: {name} is set and overrides netclaw.json for the daemon.", _error.ToString(), StringComparison.Ordinal);
        }
        finally
        {
            Environment.SetEnvironmentVariable(name, null);
        }
    }
}
