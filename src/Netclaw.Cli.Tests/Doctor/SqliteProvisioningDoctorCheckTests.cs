// -----------------------------------------------------------------------
// <copyright file="SqliteProvisioningDoctorCheckTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Cli.Doctor;
using Netclaw.Configuration;
using Xunit;

namespace Netclaw.Cli.Tests.Doctor;

public sealed class SqliteProvisioningDoctorCheckTests
{
    [Fact]
    public async Task ReturnsError_WhenLatestCrashLogContainsSqliteProvisioningFailure()
    {
        var paths = CreateTempPaths();
        var crashPath = Path.Combine(paths.LogsDirectory, "crash-20260304-164011.log");
        await File.WriteAllTextAsync(crashPath,
            "System.DllNotFoundException: Unable to load shared library 'e_sqlite3'\n" +
            "at Microsoft.Data.Sqlite.SqliteConnection..cctor()\n" +
            "at Netclaw.Daemon.Services.SchemaMigrator.MigrateAsync(String sqlitePath, CancellationToken cancellationToken)", TestContext.Current.CancellationToken);

        var check = new SqliteProvisioningDoctorCheck(paths);
        var result = await check.RunAsync(TestContext.Current.CancellationToken);

        Assert.Equal(DoctorSeverity.Error, result.Severity);
        Assert.Contains("SQLite", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ReturnsPass_WhenLatestCrashLogIsNotSqliteRelated()
    {
        var paths = CreateTempPaths();
        var crashPath = Path.Combine(paths.LogsDirectory, "crash-20260304-170000.log");
        await File.WriteAllTextAsync(crashPath,
            "System.InvalidOperationException: Something unrelated to sqlite", TestContext.Current.CancellationToken);

        var check = new SqliteProvisioningDoctorCheck(paths);
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
