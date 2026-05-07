// -----------------------------------------------------------------------
// <copyright file="DaemonCrashDoctorCheckTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Microsoft.Extensions.Time.Testing;
using Netclaw.Cli.Doctor;
using Netclaw.Configuration;
using Xunit;

namespace Netclaw.Cli.Tests.Doctor;

public sealed class DaemonCrashDoctorCheckTests
{
    [Fact]
    public async Task ReturnsWarning_WhenRecentDaemonCrashLogExists()
    {
        var paths = CreateTempPaths();
        var now = new DateTimeOffset(2026, 4, 14, 18, 30, 0, TimeSpan.Zero);

        var crashPath = Path.Combine(paths.LogsDirectory, "crash-20260414-182900.log");
        await File.WriteAllTextAsync(
            crashPath,
            "Netclaw daemon-unhandled crash at 2026-04-14T18:29:00.0000000+00:00\n\nSystem.InvalidOperationException: boom",
            TestContext.Current.CancellationToken);

        var check = new DaemonCrashDoctorCheck(paths, new FakeTimeProvider(now));
        var result = await check.RunAsync(TestContext.Current.CancellationToken);

        Assert.Equal(DoctorSeverity.Warning, result.Severity);
        Assert.Contains("crash-20260414-182900.log", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReturnsPass_WhenOnlyCliCrashLogsExist()
    {
        var paths = CreateTempPaths();
        var now = new DateTimeOffset(2026, 4, 14, 18, 30, 0, TimeSpan.Zero);

        var crashPath = Path.Combine(paths.LogsDirectory, "crash-20260414-182900.log");
        await File.WriteAllTextAsync(
            crashPath,
            "Netclaw CLI crash at 2026-04-14T18:29:00.0000000+00:00\n\nSystem.InvalidOperationException: cli failure",
            TestContext.Current.CancellationToken);

        var check = new DaemonCrashDoctorCheck(paths, new FakeTimeProvider(now));
        var result = await check.RunAsync(TestContext.Current.CancellationToken);

        Assert.Equal(DoctorSeverity.Pass, result.Severity);
    }

    [Fact]
    public async Task ReturnsPass_WhenDaemonCrashLogIsOutsideRecentWindow()
    {
        var paths = CreateTempPaths();
        var now = new DateTimeOffset(2026, 4, 14, 18, 30, 0, TimeSpan.Zero);

        var crashPath = Path.Combine(paths.LogsDirectory, "crash-20260401-080000.log");
        await File.WriteAllTextAsync(
            crashPath,
            "Netclaw daemon crash at 2026-04-01T08:00:00.0000000+00:00\n\nSystem.InvalidOperationException: old",
            TestContext.Current.CancellationToken);

        File.SetLastWriteTimeUtc(crashPath, new DateTime(2026, 4, 1, 8, 0, 0, DateTimeKind.Utc));

        var check = new DaemonCrashDoctorCheck(paths, new FakeTimeProvider(now));
        var result = await check.RunAsync(TestContext.Current.CancellationToken);

        Assert.Equal(DoctorSeverity.Pass, result.Severity);
    }

    private static NetclawPaths CreateTempPaths()
    {
        var basePath = Path.Combine(Path.GetTempPath(), "netclaw-tests", Guid.NewGuid().ToString("N"));
        var paths = new NetclawPaths(basePath);
        paths.EnsureDirectoriesExist();
        return paths;
    }

}
