// -----------------------------------------------------------------------
// <copyright file="StartupConfigurationFailureTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Configuration;
using Netclaw.Daemon.Services;
using Netclaw.Tests.Utilities;
using Xunit;

namespace Netclaw.Daemon.Tests.Services;

public sealed class StartupConfigurationFailureTests : IDisposable
{
    private readonly DisposableTempDir _dir = new();

    public void Dispose() => _dir.Dispose();

    [Fact]
    public void Report_WritesOneLineToStderrAndTheDaemonLog_WithoutACrashLog()
    {
        var paths = new NetclawPaths(_dir.Path);
        paths.EnsureDirectoriesExist();
        using var stderr = new StringWriter();

        StartupConfigurationFailure.Report(paths, "Models:Roles is set but Models:Definitions is missing or empty.", stderr);

        Assert.Equal("error: Models:Roles is set but Models:Definitions is missing or empty." + Environment.NewLine, stderr.ToString());
        var daemonLog = Directory.GetFiles(paths.LogsDirectory, "daemon*.log").Single();
        Assert.Contains("Models:Roles is set but Models:Definitions is missing or empty.", File.ReadAllText(daemonLog));
        Assert.Empty(Directory.GetFiles(paths.LogsDirectory, "crash-*"));
    }
}
