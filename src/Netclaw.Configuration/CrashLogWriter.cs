// -----------------------------------------------------------------------
// <copyright file="CrashLogWriter.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
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
            var effectiveLogsDirectory = logsDirectory
                ?? Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                    ".netclaw", "logs");

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
