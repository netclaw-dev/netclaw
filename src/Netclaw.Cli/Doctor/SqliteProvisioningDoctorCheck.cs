using System.Globalization;
using System.Text.Json.Nodes;
using Netclaw.Configuration;

namespace Netclaw.Cli.Doctor;

public sealed class SqliteProvisioningDoctorCheck(NetclawPaths paths) : IDoctorCheck
{
    private const string CheckName = "SQLite Provisioning";

    public Task<DoctorCheckResult> RunAsync(CancellationToken cancellationToken = default)
    {
        if (!UsesSqlite(paths))
        {
            return Task.FromResult(DoctorCheckResult.Pass(
                CheckName,
                "Persistence provider is not SQLite."));
        }

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
        if (IsCrashLogStale(latestCrash, paths.PidFilePath))
        {
            return Task.FromResult(DoctorCheckResult.Pass(
                CheckName,
                $"SQLite crash detected ({latestCrash.Name}) but daemon has been restarted since."));
        }

        var occurredAt = TryParseCrashTimestamp(latestCrash.Name)
            ?.ToString("u", CultureInfo.InvariantCulture)
            ?? latestCrash.LastWriteTimeUtc.ToString("u", CultureInfo.InvariantCulture);

        return Task.FromResult(DoctorCheckResult.Error(
            CheckName,
            $"Daemon failed provisioning SQLite ({latestCrash.Name}, {occurredAt}).",
            "The daemon could not initialize SQLite (for example missing e_sqlite3 in a single-file build). Update/reinstall Netclaw binaries, then run `netclaw daemon start`."));
    }

    private static bool UsesSqlite(NetclawPaths paths)
    {
        var (root, readError) = DoctorJsonConfigReader.TryReadConfig(paths);
        if (readError is not null)
            return true; // Default behavior is SQLite when config is missing/invalid.

        var provider = root?["Persistence"]?["Provider"]?.GetValue<string>();
        if (string.IsNullOrWhiteSpace(provider))
            return true;

        return provider.Equals("sqlite", StringComparison.OrdinalIgnoreCase);
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

    /// <summary>
    /// Returns true if the PID file was written after the crash log, meaning the daemon
    /// was restarted since the crash and the crash log is stale.
    /// </summary>
    private static bool IsCrashLogStale(FileInfo crashLog, string pidFilePath)
    {
        var pidFile = new FileInfo(pidFilePath);
        return pidFile.Exists && pidFile.LastWriteTimeUtc > crashLog.LastWriteTimeUtc;
    }

    private static DateTimeOffset? TryParseCrashTimestamp(string fileName)
    {
        // crash-YYYYMMDD-HHMMSS.log
        var stem = Path.GetFileNameWithoutExtension(fileName);
        const string prefix = "crash-";
        if (!stem.StartsWith(prefix, StringComparison.Ordinal))
            return null;

        var payload = stem[prefix.Length..];
        if (!DateTimeOffset.TryParseExact(
                payload,
                "yyyyMMdd-HHmmss",
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal,
                out var parsed))
            return null;

        return parsed.ToUniversalTime();
    }
}
