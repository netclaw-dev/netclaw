// -----------------------------------------------------------------------
// <copyright file="HealthCheckStepViewModelTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Cli.Daemon;
using Netclaw.Cli.Tui;
using Netclaw.Cli.Tui.Wizard;
using Netclaw.Cli.Tui.Wizard.Steps;
using Netclaw.Configuration;
using Netclaw.Providers;
using Netclaw.Tests.Utilities;
using Xunit;

namespace Netclaw.Cli.Tests.Tui.Wizard;

public sealed class HealthCheckStepViewModelTests : IDisposable
{
    private readonly DisposableTempDir _dir = new();
    private readonly NetclawPaths _paths;

    public HealthCheckStepViewModelTests()
    {
        _paths = new NetclawPaths(_dir.Path);
        _paths.EnsureDirectoriesExist();
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("NETCLAW_DAEMON_PATH", null);
        _dir.Dispose();
    }

    [Fact]
    public async Task RunWithOrchestrator_PreservesSpecificStartupFailureMessage()
    {
        var fakeBinaryPath = Path.Combine(
            _dir.Path,
            OperatingSystem.IsWindows() ? "fake-netclawd.cmd" : "fake-netclawd.sh");
        await File.WriteAllTextAsync(
            fakeBinaryPath,
            OperatingSystem.IsWindows()
                ? "@echo off\r\nexit /b 1\r\n"
                : "#!/usr/bin/env bash\nexit 1\n",
            TestContext.Current.CancellationToken);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(
                fakeBinaryPath,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
        Environment.SetEnvironmentVariable("NETCLAW_DAEMON_PATH", fakeBinaryPath);

        var expectedMessage =
            "Daemon startup aborted: Invalid reverse-proxy topology: Daemon.Host '127.0.0.1' is loopback.";
        var crashLogPath = Path.Combine(_paths.LogsDirectory, "crash-test.log");
        await File.WriteAllTextAsync(
            crashLogPath,
            $"{expectedMessage}{Environment.NewLine}",
            TestContext.Current.CancellationToken);
        File.SetLastWriteTimeUtc(crashLogPath, DateTime.UtcNow.AddMinutes(1));

        var daemonManager = new DaemonManager(_paths, TimeProvider.System);
        using var step = new HealthCheckStepViewModel(
            daemonManager,
            daemonApi: null,
            navigationState: new ChatNavigationState());
        using var exposureStep = new ExposureModeStepViewModel
        {
            SelectedMode = ExposureMode.ReverseProxy
        };

        using var context = new WizardContext
        {
            Paths = _paths,
            Registry = new ProviderDescriptorRegistry([]),
            RequestRedraw = () => { }
        };

        step.OnEnter(context, NavigationDirection.Forward);
        exposureStep.OnEnter(context, NavigationDirection.Forward);

        using var orchestrator = new WizardOrchestrator([exposureStep, step], context);

        await step.RunWithOrchestrator(orchestrator);

        Assert.NotEmpty(step.Results);
        var failure = Assert.Single(step.Results, result => result.Passed is false);
        Assert.Contains(expectedMessage, failure.Label, StringComparison.Ordinal);
        Assert.DoesNotContain("Daemon did not become ready", failure.Label, StringComparison.Ordinal);
        Assert.Contains(crashLogPath, failure.Label, StringComparison.Ordinal);
        Assert.Equal("Setup complete with warnings. Run `netclaw daemon start` to begin.", context.StatusMessage.Value);
    }
}
