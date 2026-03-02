namespace Netclaw.Configuration;

public static class CrashLogWriter
{
    public static void Write(
        Exception ex,
        string processName,
        TimeProvider? timeProvider = null,
        TextWriter? errorWriter = null,
        string? logsDirectory = null)
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

            var crashPath = Path.Combine(effectiveLogsDirectory,
                $"crash-{now:yyyyMMdd-HHmmss}.log");

            File.WriteAllText(crashPath,
                $"""
                Netclaw {processName} crash at {now:O}

                {ex}
                """);

            errorWriter.WriteLine($"Fatal error — crash log written to {crashPath}");
        }
        catch
        {
            errorWriter.WriteLine($"Fatal error (could not write crash log): {ex}");
        }
    }
}
