using Microsoft.Extensions.Logging;

namespace Netclaw.Daemon.Configuration;

public static class LoggingRegistrationExtensions
{
    public static LogLevel ConfigureNetclawLogging(this WebApplicationBuilder builder)
    {
        var level = ResolveLogLevel(builder.Configuration);
        var consoleEnabled = builder.Configuration.GetValue("Logging:Console:Enabled", false);

        builder.Logging.ClearProviders();
        
        // Guard rail: daemon should never write to console in production.
        // All logging must go to file-only sinks to avoid colliding with CLI TUI.
        if (consoleEnabled)
        {
            // Log a one-time warning via stderr (only happens at startup)
            Console.Error.WriteLine(
                "[WARN] Console logging enabled in daemon - this is not supported in production. " +
                "All logging should go to file. Disabling console logging.");
        }

        // Always write to a rolling log file in ~/.netclaw/logs/
        var logsDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".netclaw", "logs");
        Directory.CreateDirectory(logsDir);
        var logFilePath = Path.Combine(logsDir, "daemon.log");
        builder.Logging.AddProvider(new RollingFileLoggerProvider(logFilePath));

        builder.Logging.SetMinimumLevel(level);
        return level;
    }

    private static LogLevel ResolveLogLevel(IConfiguration configuration)
    {
        var configured = configuration["Logging:LogLevel:Default"];
        if (Enum.TryParse<LogLevel>(configured, ignoreCase: true, out var level))
            return level;

        return LogLevel.Information;
    }
}
