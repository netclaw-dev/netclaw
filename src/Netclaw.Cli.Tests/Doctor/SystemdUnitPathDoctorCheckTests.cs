// -----------------------------------------------------------------------
// <copyright file="SystemdUnitPathDoctorCheckTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Cli.Daemon;
using Netclaw.Cli.Doctor;
using Xunit;

namespace Netclaw.Cli.Tests.Doctor;

public sealed class SystemdUnitPathDoctorCheckTests
{
    [Fact]
    public async Task ReturnsPass_WhenPlatformDisabled()
    {
        var (unitPath, _) = WriteUnitDir();
        File.WriteAllText(unitPath, "[Service]\nExecStart=/opt/netclaw/netclawd\n");
        var check = new SystemdUnitPathDoctorCheck(unitPath, enabledOnThisPlatform: false);

        var result = await check.RunAsync(TestContext.Current.CancellationToken);

        Assert.Equal(DoctorSeverity.Pass, result.Severity);
        Assert.Contains("Not applicable", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReturnsPass_WhenUnitFileDoesNotExist()
    {
        var unitPath = Path.Combine(Path.GetTempPath(), "netclaw-tests", Guid.NewGuid().ToString("N"), "netclaw.service");
        var check = new SystemdUnitPathDoctorCheck(unitPath, enabledOnThisPlatform: true);

        var result = await check.RunAsync(TestContext.Current.CancellationToken);

        Assert.Equal(DoctorSeverity.Pass, result.Severity);
        Assert.Contains("No systemd user service installed", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReturnsWarning_WhenExecStartMissing()
    {
        var (unitPath, _) = WriteUnitDir();
        File.WriteAllText(unitPath, "[Service]\nType=simple\nEnvironmentFile=-/tmp/daemon.env\n");
        var check = new SystemdUnitPathDoctorCheck(unitPath, enabledOnThisPlatform: true);

        var result = await check.RunAsync(TestContext.Current.CancellationToken);

        Assert.Equal(DoctorSeverity.Warning, result.Severity);
        Assert.Contains("Could not determine the daemon install directory", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReturnsPass_WhenLegacyInlinePathIncludesInstallDir()
    {
        // A functional legacy (pre-#1544) unit: inline Environment=PATH= that resolves the
        // install dir (e.g. after an in-place binary upgrade without reinstall). Must NOT
        // false-alarm; pass with a migration note.
        var (unitPath, _) = WriteUnitDir();
        File.WriteAllText(unitPath, """
            [Service]
            ExecStart=/opt/netclaw/netclawd
            Environment=PATH=/opt/netclaw:/usr/bin
            """);
        var check = new SystemdUnitPathDoctorCheck(unitPath, enabledOnThisPlatform: true);

        var result = await check.RunAsync(TestContext.Current.CancellationToken);

        Assert.Equal(DoctorSeverity.Pass, result.Severity);
        Assert.Contains("Legacy unit", result.Message, StringComparison.Ordinal);
        Assert.Contains("daemon install", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReturnsWarning_WhenLegacyUnitLacksUsablePath()
    {
        // Legacy unit whose inline PATH does NOT include the install dir → genuinely
        // broken, route to reinstall.
        var (unitPath, _) = WriteUnitDir();
        File.WriteAllText(unitPath, """
            [Service]
            ExecStart=/opt/netclaw/netclawd
            Environment=PATH=/usr/bin:/bin
            """);
        var check = new SystemdUnitPathDoctorCheck(unitPath, enabledOnThisPlatform: true);

        var result = await check.RunAsync(TestContext.Current.CancellationToken);

        Assert.Equal(DoctorSeverity.Warning, result.Severity);
        Assert.Contains("does not supply the daemon's shell-tool PATH", result.Message, StringComparison.Ordinal);
        Assert.Contains("daemon install", result.Remediation!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReturnsWarning_WhenEnvironmentFileMissingOnDisk()
    {
        var (unitPath, dir) = WriteUnitDir();
        var envPath = Path.Combine(dir, "daemon.env"); // referenced but never written
        File.WriteAllText(unitPath, DaemonManager.BuildDaemonUnitContent(
            "/opt/netclaw/netclawd", "/opt/netclaw/netclaw", envPath));
        var check = new SystemdUnitPathDoctorCheck(unitPath, enabledOnThisPlatform: true);

        var result = await check.RunAsync(TestContext.Current.CancellationToken);

        Assert.Equal(DoctorSeverity.Warning, result.Severity);
        Assert.Contains("is missing", result.Message, StringComparison.Ordinal);
        Assert.Contains("doctor --fix", result.Remediation!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReturnsWarning_WhenEnvPathMissingInstallDir()
    {
        var (unitPath, dir) = WriteUnitDir();
        var envPath = Path.Combine(dir, "daemon.env");
        File.WriteAllText(envPath, "PATH=/usr/local/bin:/usr/bin\n"); // no /opt/netclaw
        File.WriteAllText(unitPath, DaemonManager.BuildDaemonUnitContent(
            "/opt/netclaw/netclawd", "/opt/netclaw/netclaw", envPath));
        var check = new SystemdUnitPathDoctorCheck(unitPath, enabledOnThisPlatform: true);

        var result = await check.RunAsync(TestContext.Current.CancellationToken);

        Assert.Equal(DoctorSeverity.Warning, result.Severity);
        Assert.Contains("does not include the daemon's install directory", result.Message, StringComparison.Ordinal);
        Assert.Contains("/opt/netclaw", result.Message, StringComparison.Ordinal);
        Assert.Contains("doctor --fix", result.Remediation!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReturnsPass_WhenWiredAndInstallDirPresent()
    {
        // Producer→consumer contract: the exact artifacts install writes
        // (env file via Render, unit via BuildDaemonUnitContent) are accepted by the check.
        var (unitPath, dir) = WriteUnitDir();
        const string installDir = "/home/user/.local/bin";
        var envPath = Path.Combine(dir, "daemon.env");
        File.WriteAllText(envPath, DaemonPathEnvironmentFile.Render(installDir, "/usr/local/bin:/usr/bin"));
        File.WriteAllText(unitPath, DaemonManager.BuildDaemonUnitContent(
            $"{installDir}/netclawd", $"{installDir}/netclaw", envPath));
        var check = new SystemdUnitPathDoctorCheck(unitPath, enabledOnThisPlatform: true);

        var result = await check.RunAsync(TestContext.Current.CancellationToken);

        Assert.Equal(DoctorSeverity.Pass, result.Severity);
        Assert.Contains(installDir, result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ParsesExecStart_StrippingArguments()
    {
        var (unitPath, dir) = WriteUnitDir();
        const string installDir = "/opt/netclaw";
        var envPath = Path.Combine(dir, "daemon.env");
        File.WriteAllText(envPath, DaemonPathEnvironmentFile.Render(installDir, "/usr/bin"));
        // ExecStart carries an argument — install dir is still the binary's parent.
        File.WriteAllText(unitPath, $"""
            [Service]
            ExecStart={installDir}/netclawd --foreground
            EnvironmentFile=-{envPath}
            """);
        var check = new SystemdUnitPathDoctorCheck(unitPath, enabledOnThisPlatform: true);

        var result = await check.RunAsync(TestContext.Current.CancellationToken);

        Assert.Equal(DoctorSeverity.Pass, result.Severity);
    }

    private static (string unitPath, string dir) WriteUnitDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "netclaw-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return (Path.Combine(dir, "netclaw.service"), dir);
    }
}
