// -----------------------------------------------------------------------
// <copyright file="CrashLogWriter.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Globalization;
using System.Text.RegularExpressions;

namespace Netclaw.Configuration;

public static class CrashLogWriter
{
    public static string? TryWrite(
        Exception ex,
        string processName,
        TimeProvider? timeProvider = null,
        TextWriter? errorWriter = null,
        string? logsDirectory = null,
        IReadOnlyDictionary<string, string>? context = null)
    {
        ArgumentNullException.ThrowIfNull(ex);
        if (string.IsNullOrWhiteSpace(processName))
            throw new ArgumentException("Process name cannot be empty.", nameof(processName));

        errorWriter ??= Console.Error;
        var now = (timeProvider ?? TimeProvider.System).GetUtcNow();

        try
        {
            var effectiveLogsDirectory = logsDirectory ?? new NetclawPaths().LogsDirectory;

            Directory.CreateDirectory(effectiveLogsDirectory);

            var basePath = Path.Combine(effectiveLogsDirectory,
                $"crash-{now:yyyyMMdd-HHmmss}.log");
            var crashPath = EnsureUniquePath(basePath, now);

            File.WriteAllText(crashPath, BuildCrashLogContent(processName, now, ex, context));

            errorWriter.WriteLine($"Fatal error — crash log written to {crashPath}");
            return crashPath;
        }
        catch
        {
            errorWriter.WriteLine($"Fatal error (could not write crash log): {ex}");
            return null;
        }
    }

    public static void Write(
        Exception ex,
        string processName,
        TimeProvider? timeProvider = null,
        TextWriter? errorWriter = null,
        string? logsDirectory = null,
        IReadOnlyDictionary<string, string>? context = null)
    {
        _ = TryWrite(ex, processName, timeProvider, errorWriter, logsDirectory, context);
    }

    private static string BuildCrashLogContent(
        string processName,
        DateTimeOffset timestamp,
        Exception ex,
        IReadOnlyDictionary<string, string>? context)
    {
        var writer = new StringWriter();
        writer.WriteLine($"Netclaw {processName} crash at {timestamp:O}");
        writer.WriteLine();

        if (context is { Count: > 0 })
        {
            writer.WriteLine("Context:");
            foreach (var kv in context.OrderBy(static x => x.Key, StringComparer.Ordinal))
                writer.WriteLine($"{kv.Key}: {kv.Value}");
            writer.WriteLine();
        }

        writer.WriteLine(ex.ToString());
        return writer.ToString();
    }

    /// <summary>
    /// Parses a crash log file name back to its UTC timestamp. Accepts exactly what
    /// <see cref="TryWrite"/> produces: <c>crash-yyyyMMdd-HHmmss.log</c>, optionally with the
    /// numeric uniqueness suffix <c>-{pid}-{fff}-{n}</c> or <c>-{pid}-{guid32}</c>. Anything else,
    /// including a file a user named <c>crash-20200101-000000-notes.log</c>, does not parse.
    /// </summary>
    public static bool TryParseFileName(string fileName, out DateTimeOffset timestamp)
    {
        timestamp = default;
        var match = CrashFileNamePattern.Match(fileName);
        return match.Success
            && DateTimeOffset.TryParseExact(
                match.Groups[1].Value,
                "yyyyMMdd-HHmmss",
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out timestamp);
    }

    private static readonly Regex CrashFileNamePattern = new(
        "^crash-([0-9]{8}-[0-9]{6})(-[0-9]+-[0-9]{3}-[0-9]+|-[0-9]+-[0-9a-f]{32})?\\.log$",
        RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);

    private static string EnsureUniquePath(string basePath, DateTimeOffset now)
    {
        if (!File.Exists(basePath))
            return basePath;

        var directory = Path.GetDirectoryName(basePath) ?? string.Empty;
        var baseName = Path.GetFileNameWithoutExtension(basePath);
        var extension = Path.GetExtension(basePath);

        for (var i = 1; i <= 1000; i++)
        {
            var candidate = Path.Combine(
                directory,
                $"{baseName}-{Environment.ProcessId}-{now:fff}-{i}{extension}");
            if (!File.Exists(candidate))
                return candidate;
        }

        return Path.Combine(
            directory,
            $"{baseName}-{Environment.ProcessId}-{Guid.NewGuid():N}{extension}");
    }
}
