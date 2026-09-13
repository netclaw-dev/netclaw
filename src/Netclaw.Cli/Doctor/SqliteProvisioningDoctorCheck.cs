// -----------------------------------------------------------------------
// <copyright file="SqliteProvisioningDoctorCheck.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Configuration;

namespace Netclaw.Cli.Doctor;

public sealed class SqliteProvisioningDoctorCheck(NetclawPaths paths) : IDoctorCheck
{
    private const string CheckName = "SQLite Provisioning";

    public Task<DoctorCheckResult> RunAsync(CancellationToken cancellationToken = default)
    {
        var latestCrash = FindLatestCrashLog(paths.LogsDirectory);
        if (latestCrash is null)
        {
            return Task.FromResult(DoctorCheckResult.Pass(
                CheckName,
                "No recent daemon crash log indicates SQLite provisioning failure."));
        }

        string crashText;
        try
        {
            crashText = File.ReadAllText(latestCrash.FullName);
        }
        catch (Exception ex)
        {
            return Task.FromResult(DoctorCheckResult.Warning(
                CheckName,
                $"Could not read latest crash log ({latestCrash.Name}): {ex.Message}",
                "Check file permissions under ~/.netclaw/logs and retry `netclaw doctor`."));
        }

        if (!LooksLikeSqliteProvisioningFailure(crashText))
        {
            return Task.FromResult(DoctorCheckResult.Pass(
                CheckName,
                "No SQLite provisioning failure found in the latest daemon crash log."));
        }

        // If the daemon was restarted after the crash, the crash log is stale.
        if (CrashLogHelper.IsCrashLogStale(latestCrash, paths.PidFilePath))
        {
            return Task.FromResult(DoctorCheckResult.Pass(
                CheckName,
                $"SQLite crash detected ({latestCrash.Name}) but daemon has been restarted since."));
        }

        var occurredAt = CrashLogHelper.TryParseCrashTimestamp(latestCrash.Name)
            ?.ToString("u", System.Globalization.CultureInfo.InvariantCulture)
            ?? latestCrash.LastWriteTimeUtc.ToString("u", System.Globalization.CultureInfo.InvariantCulture);

        return Task.FromResult(DoctorCheckResult.Error(
            CheckName,
            $"Daemon failed provisioning SQLite ({latestCrash.Name}, {occurredAt}).",
            "The daemon could not initialize SQLite (for example missing e_sqlite3 in a single-file build). Update/reinstall Netclaw binaries, then run `netclaw daemon start`."));
    }

    private static FileInfo? FindLatestCrashLog(string logsDirectory)
    {
        if (!Directory.Exists(logsDirectory))
            return null;

        var files = new DirectoryInfo(logsDirectory)
            .GetFiles("crash-*.log", SearchOption.TopDirectoryOnly);

        return files
            .OrderByDescending(f => f.LastWriteTimeUtc)
            .FirstOrDefault();
    }

    private static bool LooksLikeSqliteProvisioningFailure(string crashText)
    {
        if (string.IsNullOrWhiteSpace(crashText))
            return false;

        return crashText.Contains("Microsoft.Data.Sqlite.SqliteConnection", StringComparison.Ordinal)
            || crashText.Contains("SQLitePCL", StringComparison.Ordinal)
            || crashText.Contains("e_sqlite3", StringComparison.Ordinal)
            || crashText.Contains("SchemaMigrator.MigrateAsync", StringComparison.Ordinal);
    }

}
