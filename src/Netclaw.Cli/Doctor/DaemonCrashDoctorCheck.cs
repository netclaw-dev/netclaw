using Netclaw.Configuration;

namespace Netclaw.Cli.Doctor;

public sealed class DaemonCrashDoctorCheck(
    NetclawPaths paths,
    TimeProvider? timeProvider = null) : IDoctorCheck
{
    private const string CheckName = "Daemon Crash Logs";
    private static readonly TimeSpan RecentWindow = TimeSpan.FromDays(7);
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;

    public Task<DoctorCheckResult> RunAsync(CancellationToken cancellationToken = default)
    {
        var now = _timeProvider.GetUtcNow();
        var recentCrashes = FindRecentDaemonCrashes(paths.LogsDirectory, now).ToList();
        if (recentCrashes.Count == 0)
        {
            return Task.FromResult(DoctorCheckResult.Pass(
                CheckName,
                "No recent daemon crash logs found."));
        }

        var latest = recentCrashes[0];
        var occurredAt = CrashLogHelper.TryParseCrashTimestamp(latest.Name)
            ?.ToString("u", System.Globalization.CultureInfo.InvariantCulture)
            ?? latest.LastWriteTimeUtc.ToString("u", System.Globalization.CultureInfo.InvariantCulture);

        var restartedSinceLatestCrash = CrashLogHelper.IsCrashLogStale(latest, paths.PidFilePath);
        var restartNote = restartedSinceLatestCrash
            ? "daemon appears to have restarted since this crash"
            : "daemon has not recorded a newer PID timestamp since this crash";

        return Task.FromResult(DoctorCheckResult.Warning(
            CheckName,
            $"Detected {recentCrashes.Count} daemon crash log(s) in the last {(int)RecentWindow.TotalDays} days (latest: {latest.Name}, {occurredAt}; {restartNote}).",
            "Inspect ~/.netclaw/logs/crash-*.log for stack traces, run `netclaw daemon status`, and verify notification targets received `daemon.crashing` alerts."));
    }

    private static IEnumerable<FileInfo> FindRecentDaemonCrashes(string logsDirectory, DateTimeOffset now)
    {
        if (!Directory.Exists(logsDirectory))
            return [];

        var cutoff = now.UtcDateTime - RecentWindow;
        return new DirectoryInfo(logsDirectory)
            .GetFiles("crash-*.log", SearchOption.TopDirectoryOnly)
            .Where(f => f.LastWriteTimeUtc >= cutoff)
            .OrderByDescending(f => f.LastWriteTimeUtc)
            .Where(IsDaemonCrashLog);
    }

    private static bool IsDaemonCrashLog(FileInfo crashLog)
    {
        try
        {
            using var stream = crashLog.OpenRead();
            using var reader = new StreamReader(stream);
            var firstLine = reader.ReadLine();
            return !string.IsNullOrWhiteSpace(firstLine)
                   && firstLine.Contains("Netclaw daemon", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

}
