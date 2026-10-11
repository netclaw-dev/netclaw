// -----------------------------------------------------------------------
// <copyright file="DaemonCrashDoctorCheckTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Tests.Utilities;
using Microsoft.Extensions.Time.Testing;
using Netclaw.Cli.Doctor;
using Netclaw.Configuration;
using Xunit;

namespace Netclaw.Cli.Tests.Doctor;

public sealed class DaemonCrashDoctorCheckTests : IDisposable
{
    private readonly DisposableTempDir _temp = new();

    public void Dispose() => _temp.Dispose();

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

    [Fact]
    public async Task Notes_a_retention_shorter_than_the_window()
    {
        var paths = CreateTempPaths();
        await File.WriteAllTextAsync(paths.NetclawConfigPath,
            """{ "configVersion": 1, "Retention": { "Logs": { "Days": 3 } } }""", TestContext.Current.CancellationToken);

        var result = await new DaemonCrashDoctorCheck(paths, new FakeTimeProvider(DateTimeOffset.Parse("2026-04-14T18:30:00Z")))
            .RunAsync(TestContext.Current.CancellationToken);

        Assert.Contains("Log retention is set to 3 days", result.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("""{ "configVersion": 1, "Retention": { "Logs": { "Days": 0 } } }""")]
    [InlineData("""{ "configVersion": 1, "Retention": { "Logs": { "Days": 7 } } }""")]
    [InlineData("""{ "configVersion": 1 }""")]
    public async Task Stays_silent_when_retention_covers_the_window(string json)
    {
        var paths = CreateTempPaths();
        await File.WriteAllTextAsync(paths.NetclawConfigPath, json, TestContext.Current.CancellationToken);

        var result = await new DaemonCrashDoctorCheck(paths, new FakeTimeProvider(DateTimeOffset.Parse("2026-04-14T18:30:00Z")))
            .RunAsync(TestContext.Current.CancellationToken);

        Assert.DoesNotContain("retention", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    private NetclawPaths CreateTempPaths()
    {
        var basePath = Path.Combine(_temp.Path, Guid.NewGuid().ToString("N"));
        var paths = new NetclawPaths(basePath);
        paths.EnsureDirectoriesExist();
        return paths;
    }

}
