// -----------------------------------------------------------------------
// <copyright file="RetentionConfigViewModelTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Netclaw.Cli.Config;
using Netclaw.Cli.Tui;
using Netclaw.Cli.Tui.Config;
using Netclaw.Configuration;
using Netclaw.Tests.Utilities;
using Xunit;

namespace Netclaw.Cli.Tests.Tui.Config;

public sealed class RetentionConfigViewModelTests : IDisposable
{
    private readonly DisposableTempDir _dir = new();
    private readonly NetclawPaths _paths;

    public RetentionConfigViewModelTests()
    {
        _paths = new NetclawPaths(_dir.Path);
        _paths.EnsureDirectoriesExist();
        File.WriteAllText(_paths.NetclawConfigPath, """{ "configVersion": 1, "Daemon": { "Port": 5299 } }""");
    }

    public void Dispose() => _dir.Dispose();

    [Fact]
    public void Dashboard_entry_routes_to_the_retention_editor()
    {
        using var vm = new ConfigDashboardViewModel(new ConfigDashboardNavigationState());
        string? route = null;
        vm.RouteRequested = r => route = r;

        vm.Activate(vm.Items.Single(static item => item.Label == "Data Retention"));

        Assert.Equal("/retention", route);
    }

    [Fact]
    public void There_is_one_row_for_each_retention_setting()
    {
        using var vm = new RetentionConfigViewModel(_paths);

        Assert.Equal(RetentionSettings.All, vm.Rows.Select(static r => r.Setting));
    }

    [Fact]
    public void An_unset_key_reads_as_the_default()
    {
        using var vm = new RetentionConfigViewModel(_paths);

        Assert.Equal("keep 14 days (default)", vm.DisplayValue(vm.Rows[0]));
    }

    [Theory]
    [InlineData("0", "keep forever")]
    [InlineData("1", "keep 1 day")]
    [InlineData("30", "keep 30 days")]
    public void A_set_key_reads_as_its_value(string stored, string expected)
    {
        File.WriteAllText(_paths.NetclawConfigPath, $$"""{ "configVersion": 1, "Retention": { "Logs": { "Days": {{stored}} } } }""");
        using var vm = new RetentionConfigViewModel(_paths);

        Assert.Equal(expected, vm.DisplayValue(vm.Rows[0]));
    }

    [Fact]
    public void Save_writes_only_the_changed_key_and_the_daemon_reads_it()
    {
        using var vm = new RetentionConfigViewModel(_paths);
        vm.AppendText("30");

        Assert.True(vm.Save());

        using var doc = JsonDocument.Parse(File.ReadAllText(_paths.NetclawConfigPath));
        Assert.Equal(30, doc.RootElement.GetProperty("Retention").GetProperty("Logs").GetProperty("Days").GetInt32());
        Assert.Equal(5299, doc.RootElement.GetProperty("Daemon").GetProperty("Port").GetInt32());
        Assert.Contains("A running daemon applies the change automatically.", vm.Status.Value.Text, StringComparison.Ordinal);

        // The daemon resolves the days through this same call on the same file.
        var daemonView = new ConfigurationBuilder().AddJsonFile(_paths.NetclawConfigPath).Build();
        Assert.Equal(30, RetentionPolicy.ResolveDays(daemonView, RetentionSettings.Logs.ConfigKey, RetentionSettings.Logs.DefaultDays, out _));
        Assert.Equal("keep 30 days", vm.DisplayValue(vm.Rows[0]));
    }

    [Fact]
    public void Saving_the_default_leaves_an_unset_key_unset()
    {
        var before = File.ReadAllText(_paths.NetclawConfigPath);
        using var vm = new RetentionConfigViewModel(_paths);
        vm.AppendText("014"); // a different draft from the stored text, the same number as the default

        Assert.True(vm.Save());

        Assert.Equal(before, File.ReadAllText(_paths.NetclawConfigPath));
        Assert.Equal("Data retention is unchanged.", vm.Status.Value.Text);
    }

    [Fact]
    public void Zero_is_saved_and_reads_as_keep_forever()
    {
        using var vm = new RetentionConfigViewModel(_paths);
        vm.AppendText("0");

        Assert.True(vm.Save());

        Assert.Equal("keep forever", vm.DisplayValue(vm.Rows[0]));
        Assert.Equal(0, RetentionConfigStore.Read(_paths, RetentionSettings.Logs).Days);
    }

    [Theory]
    [InlineData("")]
    [InlineData("abc")]
    [InlineData("36501")]
    [InlineData("-1")]
    public void Save_rejects_an_invalid_value_before_writing(string typed)
    {
        var before = File.ReadAllText(_paths.NetclawConfigPath);
        using var vm = new RetentionConfigViewModel(_paths);
        vm.AppendText("1");
        vm.Backspace();
        vm.AppendText(typed);

        Assert.False(vm.Save());

        Assert.Equal(ConfigStatusTone.Error, vm.Status.Value.Tone);
        Assert.Contains("0 to 36500", vm.Status.Value.Text, StringComparison.Ordinal);
        Assert.Equal(before, File.ReadAllText(_paths.NetclawConfigPath));
    }

    [Fact]
    public void Typing_replaces_the_saved_number_once_then_extends_the_new_one()
    {
        using var vm = new RetentionConfigViewModel(_paths);

        vm.AppendText("1");
        vm.AppendText("4");
        vm.AppendText("5");

        Assert.Equal("145", vm.Rows[0].Draft.Value);
    }

    [Fact]
    public void Save_writes_into_the_key_spelling_the_file_already_has()
    {
        File.WriteAllText(_paths.NetclawConfigPath, """{ "retention": { "logs": { "days": 7 } } }""");
        using var vm = new RetentionConfigViewModel(_paths);
        Assert.Equal("keep 7 days", vm.DisplayValue(vm.Rows[0]));
        vm.AppendText("30");

        Assert.True(vm.Save());

        using var doc = JsonDocument.Parse(File.ReadAllText(_paths.NetclawConfigPath));
        var retention = Assert.Single(doc.RootElement.EnumerateObject(), p => p.Name.Equals("retention", StringComparison.OrdinalIgnoreCase));
        Assert.Equal("retention", retention.Name);
        Assert.Equal(30, retention.Value.GetProperty("logs").GetProperty("days").GetInt32());
    }

    [Fact]
    public void Save_surfaces_a_write_failure_without_throwing()
    {
        using var vm = new RetentionConfigViewModel(_paths);
        vm.AppendText("30");
        File.Delete(_paths.NetclawConfigPath);
        Directory.CreateDirectory(_paths.NetclawConfigPath);

        Assert.False(vm.Save());
        Assert.Equal(ConfigStatusTone.Error, vm.Status.Value.Tone);
    }

    [Fact]
    public void A_malformed_config_opens_the_editor_with_the_default_and_an_error()
    {
        File.WriteAllText(_paths.NetclawConfigPath, "{ not json");

        using var vm = new RetentionConfigViewModel(_paths);

        Assert.Equal(ConfigStatusTone.Error, vm.Status.Value.Tone);
        Assert.Equal("keep 14 days (default)", vm.DisplayValue(vm.Rows[0]));
    }

    [Fact]
    public void Dashboard_summary_shows_each_setting()
    {
        using var vm = new ConfigDashboardViewModel(new ConfigDashboardNavigationState(), _paths);
        var item = vm.Items.Single(static i => i.Label == "Data Retention");
        Assert.Equal("logs 14d", vm.StatusFor(item));

        File.WriteAllText(_paths.NetclawConfigPath, """{ "configVersion": 1, "Retention": { "Logs": { "Days": 0 } } }""");
        Assert.Equal("logs forever", vm.StatusFor(item));
    }
}
