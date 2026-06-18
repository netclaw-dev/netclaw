// -----------------------------------------------------------------------
// <copyright file="ConfigCommandTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Cli.Config;
using Netclaw.Configuration;
using Netclaw.Tests.Utilities;
using Xunit;

namespace Netclaw.Cli.Tests.Config;

public sealed class ConfigCommandTests : IDisposable
{
    private readonly DisposableTempDir _dir = new();
    private readonly NetclawPaths _paths;
    private readonly StringWriter _output = new();
    private readonly StringWriter _error = new();

    public ConfigCommandTests()
    {
        _paths = new NetclawPaths(_dir.Path);
        _paths.EnsureDirectoriesExist();
    }

    public void Dispose()
    {
        _output.Dispose();
        _error.Dispose();
        _dir.Dispose();
    }

    [Fact]
    public void Help_describes_post_install_dashboard()
    {
        var exitCode = ConfigCommand.Run(["config", "--help"], _paths, _output, _error);

        Assert.Equal(0, exitCode);
        Assert.Contains("main post-install settings dashboard", _output.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.Contains("netclaw init", _output.ToString(), StringComparison.Ordinal);
        Assert.Equal(string.Empty, _error.ToString());
    }

    [Fact]
    public void Missing_install_refuses_before_tui_startup()
    {
        var exitCode = ConfigCommand.Run(["config"], _paths, _output, _error);

        Assert.Equal(1, exitCode);
        Assert.Equal(ConfigCommand.MissingConfigMessage + Environment.NewLine, _error.ToString());
        Assert.Equal(string.Empty, _output.ToString());
    }

    [Fact]
    public void Configured_install_allows_dashboard_launch()
    {
        File.WriteAllText(_paths.NetclawConfigPath, "{\"configVersion\":1}");

        var exitCode = ConfigCommand.Run(["config"], _paths, _output, _error);

        Assert.Equal(0, exitCode);
        Assert.Equal(string.Empty, _output.ToString());
        Assert.Equal(string.Empty, _error.ToString());
    }

    [Fact]
    public void Unexpected_arguments_return_usage_error()
    {
        var exitCode = ConfigCommand.Run(["config", "extra"], _paths, _output, _error);

        Assert.Equal(1, exitCode);
        Assert.Contains("Usage: netclaw config", _output.ToString(), StringComparison.Ordinal);
    }
}
