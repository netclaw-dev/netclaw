// -----------------------------------------------------------------------
// <copyright file="DaemonManagerInstallTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Cli.Daemon;
using Netclaw.Configuration;
using Netclaw.Tests.Utilities;
using Xunit;

namespace Netclaw.Cli.Tests.Cli;

/// <summary>
/// Pins <c>daemon install</c> / <c>daemon uninstall</c> on hosts with and without a user systemd.
/// HOME points at a temp dir so the unit lands there, never in the developer's real
/// <c>~/.config/systemd/user</c>.
/// </summary>
[Collection(LegacyModelEnvironmentCollection.Name)]
public sealed class DaemonManagerInstallTests : IDisposable
{
    public static bool IsLinux => OperatingSystem.IsLinux();

    private readonly DisposableTempDir _dir = new();
    private readonly NetclawPaths _paths;
    private readonly string? _originalHome;
    private readonly string? _originalDaemonPath;
    private readonly string _unitPath;

    public DaemonManagerInstallTests()
    {
        _originalHome = Environment.GetEnvironmentVariable("HOME");
        _originalDaemonPath = Environment.GetEnvironmentVariable("NETCLAW_DAEMON_PATH");

        var home = Path.Combine(_dir.Path, "home");
        Directory.CreateDirectory(home);
        Environment.SetEnvironmentVariable("HOME", home);

        var daemonBinary = Path.Combine(_dir.Path, "netclawd");
        File.WriteAllText(daemonBinary, string.Empty);
        Environment.SetEnvironmentVariable("NETCLAW_DAEMON_PATH", daemonBinary);

        _paths = new NetclawPaths(Path.Combine(_dir.Path, "netclaw-home"));
        _paths.EnsureDirectoriesExist();
        _unitPath = DaemonManager.SystemdUserUnitFilePath;
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("HOME", _originalHome);
        Environment.SetEnvironmentVariable("NETCLAW_DAEMON_PATH", _originalDaemonPath);
        _dir.Dispose();
    }

    [SlopwatchSuppress("SW001", "Service installation is Linux-only.")]
    [Fact(SkipUnless = nameof(IsLinux), Skip = "Service installation is Linux-only.")]
    public async Task Install_WithoutUserSystemd_ExplainsAndWritesNothing()
    {
        var runner = new FakeSystemCommandRunner(
            new SystemCommandResult(1, "Failed to connect to bus: No medium found"));
        var sut = CreateManager(runner);

        var result = await sut.InstallAsync();

        Assert.False(result.Success);
        Assert.Contains("systemd user services are not available on this host", result.Message);
        Assert.Contains("Failed to connect to bus", result.Message);
        Assert.Contains("container supervisor", result.Message);
        Assert.Contains("netclaw daemon start", result.Message);
        Assert.Equal([("systemctl", "--user show-environment")], runner.Commands);
        Assert.False(Directory.Exists(DaemonManager.SystemdUserUnitDirectory));
        Assert.False(File.Exists(_unitPath));
        Assert.False(File.Exists(_paths.DaemonEnvironmentFilePath));
    }

    [SlopwatchSuppress("SW001", "Service installation is Linux-only.")]
    [Fact(SkipUnless = nameof(IsLinux), Skip = "Service installation is Linux-only.")]
    public async Task Install_WithMissingSystemctl_ExplainsAndWritesNothing()
    {
        var runner = new FakeSystemCommandRunner(
            new SystemCommandResult(-1, string.Empty, "No such file or directory"));
        var sut = CreateManager(runner);

        var result = await sut.InstallAsync();

        Assert.False(result.Success);
        Assert.Contains("systemd user services are not available on this host", result.Message);
        Assert.False(File.Exists(_unitPath));
        Assert.False(File.Exists(_paths.DaemonEnvironmentFilePath));
    }

    [SlopwatchSuppress("SW001", "Service installation is Linux-only.")]
    [Fact(SkipUnless = nameof(IsLinux), Skip = "Service installation is Linux-only.")]
    public async Task Install_WithUserSystemd_WritesUnitAndEnablesIt()
    {
        var runner = new FakeSystemCommandRunner();
        var sut = CreateManager(runner);

        var result = await sut.InstallAsync();

        Assert.True(result.Success, result.Message);
        Assert.True(File.Exists(_unitPath));
        Assert.True(File.Exists(_paths.DaemonEnvironmentFilePath));
        Assert.Equal(
            [
                "systemctl --user show-environment",
                "systemctl --user daemon-reload",
                "systemctl --user enable netclaw.service",
                $"loginctl enable-linger {Environment.UserName}"
            ],
            runner.Commands.Select(c => $"{c.Command} {c.Arguments}").ToArray());
    }

    [SlopwatchSuppress("SW001", "Service installation is Linux-only.")]
    [Fact(SkipUnless = nameof(IsLinux), Skip = "Service installation is Linux-only.")]
    public async Task Uninstall_RemovesUnitLeftByAFailedInstall_WhenSystemdIsMissing()
    {
        Directory.CreateDirectory(DaemonManager.SystemdUserUnitDirectory);
        File.WriteAllText(_unitPath, "[Service]\nExecStart=/opt/netclaw/netclawd\n");
        File.WriteAllText(_paths.DaemonEnvironmentFilePath, "PATH=/usr/bin\n");
        var runner = new FakeSystemCommandRunner(
            new SystemCommandResult(-1, string.Empty, "No such file or directory"));
        var sut = CreateManager(runner);

        var result = await sut.UninstallAsync();

        Assert.True(result.Success, result.Message);
        Assert.False(File.Exists(_unitPath));
        Assert.False(File.Exists(_paths.DaemonEnvironmentFilePath));
    }

    private DaemonManager CreateManager(FakeSystemCommandRunner runner)
        => new(_paths, TimeProvider.System, new FakeSupervisor(), runner);

    private sealed class FakeSupervisor : IContainerSupervisor
    {
        public bool IsExternallySupervised => false;
    }

    private sealed class FakeSystemCommandRunner : ISystemCommandRunner
    {
        private readonly SystemCommandResult? _always;

        public FakeSystemCommandRunner(SystemCommandResult? always = null) => _always = always;

        public List<(string Command, string Arguments)> Commands { get; } = [];

        public Task<SystemCommandResult> RunAsync(string command, string arguments)
        {
            Commands.Add((command, arguments));
            return Task.FromResult(_always ?? new SystemCommandResult(0, string.Empty));
        }
    }
}
