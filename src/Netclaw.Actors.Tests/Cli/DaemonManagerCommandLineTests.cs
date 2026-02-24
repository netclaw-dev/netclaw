using Netclaw.Cli.Daemon;
using Xunit;

namespace Netclaw.Actors.Tests.Cli;

public sealed class DaemonManagerCommandLineTests
{
    [Theory]
    [InlineData("dotnet /opt/netclaw/netclawd.dll")]
    [InlineData("/usr/local/bin/netclawd")]
    [InlineData("C:\\tools\\netclawd.exe")]
    [InlineData("netclawd")]
    public void LooksLikeDaemonCommandLine_returns_true_for_expected_patterns(string commandLine)
    {
        Assert.True(DaemonManager.LooksLikeDaemonCommandLine(commandLine));
    }

    [Theory]
    [InlineData("dotnet /srv/services/other-service.dll")]
    [InlineData("python worker.py")]
    [InlineData("/usr/bin/bash")]
    public void LooksLikeDaemonCommandLine_returns_false_for_non_daemon_processes(string commandLine)
    {
        Assert.False(DaemonManager.LooksLikeDaemonCommandLine(commandLine));
    }
}
