// -----------------------------------------------------------------------
// <copyright file="LoggingRegistrationExtensions.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Netclaw.Configuration;

namespace Netclaw.Daemon.Configuration;

public static class LoggingRegistrationExtensions
{
    public static LogLevel ConfigureNetclawLogging(this WebApplicationBuilder builder, NetclawPaths? paths = null)
    {
        var level = ResolveLogLevel(builder.Configuration);
        var consoleEnabled = builder.Configuration.GetValue("Logging:Console:Enabled", false);
        var resolvedPaths = paths ?? new NetclawPaths();

        builder.Logging.ClearProviders();
        builder.Logging.AddConfiguration(builder.Configuration.GetSection("Logging"));
        if (consoleEnabled)
            builder.Logging.AddSimpleConsole(options => options.SingleLine = true);

        // Always write to a rolling daemon.log in ~/.netclaw/logs/. The provider must be
        // constructed eagerly so MEL can see it via AddProvider — a
        // Services.AddSingleton<ILoggerProvider>(factory) registration here is not picked
        // up by the LoggerFactory in this hosting setup. This sink is daemon-global only;
        // per-session lines are published explicitly to the session-log dispatcher.
        Directory.CreateDirectory(resolvedPaths.LogsDirectory);
        var provider = new RollingFileLoggerProvider(resolvedPaths.DaemonLogPath);
        builder.Logging.AddProvider(provider);
        builder.Services.AddSingleton(provider);

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
