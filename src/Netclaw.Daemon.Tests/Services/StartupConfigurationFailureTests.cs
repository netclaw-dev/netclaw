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

    /// <summary>
    /// The message can quote a config value or an SDK error. This writer runs before the host
    /// exists, so it must build the redaction stage itself.
    /// </summary>
    [Fact]
    public void Report_RedactsASecretInTheMessage()
    {
        var paths = new NetclawPaths(_dir.Path);
        paths.EnsureDirectoriesExist();
        using var stderr = new StringWriter();

        StartupConfigurationFailure.Report(paths, "Provider rejected DB_PASSWORD=hunter2 in config.", stderr);

        Assert.DoesNotContain("hunter2", stderr.ToString(), StringComparison.Ordinal);
        var daemonLog = File.ReadAllText(Directory.GetFiles(paths.LogsDirectory, "daemon*.log").Single());
        Assert.Contains("DB_PASSWORD=***REDACTED***", daemonLog, StringComparison.Ordinal);
        Assert.DoesNotContain("hunter2", daemonLog, StringComparison.Ordinal);
    }
}
