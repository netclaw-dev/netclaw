// -----------------------------------------------------------------------
// <copyright file="LoggingRegistrationExtensions.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Microsoft.Extensions.Logging;

namespace Netclaw.Daemon.Configuration;

public static class LoggingRegistrationExtensions
{
    public static LogLevel ConfigureNetclawLogging(this WebApplicationBuilder builder)
    {
        var level = ResolveLogLevel(builder.Configuration);
        var consoleEnabled = builder.Configuration.GetValue("Logging:Console:Enabled", false);

        builder.Logging.ClearProviders();
        if (consoleEnabled)
            builder.Logging.AddSimpleConsole(options => options.SingleLine = true);

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
