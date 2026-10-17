// -----------------------------------------------------------------------
// <copyright file="DaemonEnvironmentFileModeDoctorCheckTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Cli.Doctor;
using Netclaw.Configuration;
using Netclaw.Tests.Utilities;
using Xunit;

namespace Netclaw.Cli.Tests.Doctor;

public sealed class DaemonEnvironmentFileModeDoctorCheckTests : IDisposable
{
    private readonly DisposableTempDir _temp = new();

    public void Dispose() => _temp.Dispose();

    [Fact]
    public async Task Passes_when_there_is_no_daemon_env()
    {
        var result = await new DaemonEnvironmentFileModeDoctorCheck(NewPaths()).RunAsync(TestContext.Current.CancellationToken);

        Assert.Equal(DoctorSeverity.Pass, result.Severity);
    }

    [Theory]
    [InlineData(UnixFileMode.UserRead | UnixFileMode.UserWrite)]
    [InlineData(UnixFileMode.UserRead)]
    public async Task Passes_when_only_the_owner_has_access(UnixFileMode mode)
    {
        if (OperatingSystem.IsWindows())
            return;

        var paths = NewPaths();
        File.WriteAllText(paths.DaemonEnvironmentFilePath, "NETCLAW_HOME=/x/home\n");
        File.SetUnixFileMode(paths.DaemonEnvironmentFilePath, mode);

        var result = await new DaemonEnvironmentFileModeDoctorCheck(paths).RunAsync(TestContext.Current.CancellationToken);

        Assert.Equal(DoctorSeverity.Pass, result.Severity);
    }

    [Theory]
    [InlineData(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead)]
    [InlineData(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.OtherRead)]
    [InlineData(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead)]
    public async Task Warns_with_the_path_and_the_fix_when_group_or_others_have_access(UnixFileMode mode)
    {
        if (OperatingSystem.IsWindows())
            return;

        var paths = NewPaths();
        File.WriteAllText(paths.DaemonEnvironmentFilePath, "NETCLAW_HOME=/x/home\n");
        File.SetUnixFileMode(paths.DaemonEnvironmentFilePath, mode);

        var result = await new DaemonEnvironmentFileModeDoctorCheck(paths).RunAsync(TestContext.Current.CancellationToken);

        Assert.Equal(DoctorSeverity.Warning, result.Severity);
        Assert.Contains(paths.DaemonEnvironmentFilePath, result.Message, StringComparison.Ordinal);
        Assert.Contains("netclaw doctor --fix", result.Remediation, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Warns_and_names_the_path_when_daemon_env_is_a_link_to_a_missing_file()
    {
        if (OperatingSystem.IsWindows())
            return;

        var paths = NewPaths();
        File.CreateSymbolicLink(paths.DaemonEnvironmentFilePath, Path.Combine(_temp.Path, "later", "daemon.env"));

        var result = await new DaemonEnvironmentFileModeDoctorCheck(paths).RunAsync(TestContext.Current.CancellationToken);

        Assert.Equal(DoctorSeverity.Warning, result.Severity);
        Assert.Contains(paths.DaemonEnvironmentFilePath, result.Message, StringComparison.Ordinal);
        Assert.Contains("link", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Warns_and_names_the_path_when_daemon_env_is_a_link_loop()
    {
        if (OperatingSystem.IsWindows())
            return;

        var paths = NewPaths();
        var other = Path.Combine(paths.ConfigDirectory, "other.env");
        File.CreateSymbolicLink(paths.DaemonEnvironmentFilePath, other);
        File.CreateSymbolicLink(other, paths.DaemonEnvironmentFilePath);

        var result = await new DaemonEnvironmentFileModeDoctorCheck(paths).RunAsync(TestContext.Current.CancellationToken);

        Assert.Equal(DoctorSeverity.Warning, result.Severity);
        Assert.Contains(paths.DaemonEnvironmentFilePath, result.Message, StringComparison.Ordinal);
        Assert.Contains("link", result.Message, StringComparison.Ordinal);
    }

    private NetclawPaths NewPaths()
    {
        var paths = new NetclawPaths(Path.Combine(_temp.Path, Guid.NewGuid().ToString("N")));
        paths.EnsureDirectoriesExist();
        return paths;
    }
}
