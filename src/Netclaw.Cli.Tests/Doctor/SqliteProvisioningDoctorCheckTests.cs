// -----------------------------------------------------------------------
// <copyright file="SqliteProvisioningDoctorCheckTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Text.Json;
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
        WriteConfig(paths, provider: "sqlite");

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
        WriteConfig(paths, provider: "sqlite");

        var crashPath = Path.Combine(paths.LogsDirectory, "crash-20260304-170000.log");
        await File.WriteAllTextAsync(crashPath,
            "System.InvalidOperationException: Something unrelated to sqlite", TestContext.Current.CancellationToken);

        var check = new SqliteProvisioningDoctorCheck(paths);
        var result = await check.RunAsync(TestContext.Current.CancellationToken);

        Assert.Equal(DoctorSeverity.Pass, result.Severity);
    }

    [Fact]
    public async Task ReturnsPass_WhenPersistenceProviderIsNotSqlite()
    {
        var paths = CreateTempPaths();
        WriteConfig(paths, provider: "inmemory");

        var crashPath = Path.Combine(paths.LogsDirectory, "crash-20260304-170001.log");
        await File.WriteAllTextAsync(crashPath,
            "System.DllNotFoundException: Unable to load shared library 'e_sqlite3'", TestContext.Current.CancellationToken);

        var check = new SqliteProvisioningDoctorCheck(paths);
        var result = await check.RunAsync(TestContext.Current.CancellationToken);

        Assert.Equal(DoctorSeverity.Pass, result.Severity);
        Assert.Contains("not SQLite", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    private static NetclawPaths CreateTempPaths()
    {
        var basePath = Path.Combine(Path.GetTempPath(), "netclaw-tests", Guid.NewGuid().ToString("N"));
        var paths = new NetclawPaths(basePath);
        paths.EnsureDirectoriesExist();
        return paths;
    }

    private static void WriteConfig(NetclawPaths paths, string provider)
    {
        var config = new Dictionary<string, object>
        {
            ["Persistence"] = new Dictionary<string, object>
            {
                ["Provider"] = provider
            }
        };

        File.WriteAllText(paths.NetclawConfigPath,
            JsonSerializer.Serialize(config, new JsonSerializerOptions { WriteIndented = true }));
    }
}
