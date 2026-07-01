// -----------------------------------------------------------------------
// <copyright file="LoggingRegistrationExtensions.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Akka.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Netclaw.Actors.Hosting;
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

        // This provider owns the local partition of the log stream: session-tagged lines go
        // to per-session session.log files, everything else to daemon.log (see
        // RollingFileLoggerProvider). It must be constructed eagerly so MEL sees it via
        // AddProvider — a Services.AddSingleton<ILoggerProvider>(factory) registration here is
        // not picked up by the LoggerFactory in this hosting setup. The session-log dispatcher
        // is wired in post-build by SessionLogDispatcherWiringService once Akka.Hosting has
        // registered the actor system.
        Directory.CreateDirectory(resolvedPaths.LogsDirectory);
        var provider = new RollingFileLoggerProvider(resolvedPaths.DaemonLogPath);
        builder.Logging.AddProvider(provider);
        builder.Services.AddSingleton(provider);
        builder.Services.AddHostedService<SessionLogDispatcherWiringService>();

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

/// <summary>
/// Hooks the session-log dispatcher into <see cref="RollingFileLoggerProvider"/> once
/// Akka.Hosting has registered <c>SessionLogDispatcherActorKey</c>. The provider is built during
/// host construction (before Akka), so the dispatcher can only be attached post-start.
/// </summary>
internal sealed class SessionLogDispatcherWiringService : IHostedService
{
    private readonly RollingFileLoggerProvider _provider;
    private readonly IRequiredActor<SessionLogDispatcherActorKey> _dispatcher;

    public SessionLogDispatcherWiringService(
        RollingFileLoggerProvider provider,
        IRequiredActor<SessionLogDispatcherActorKey> dispatcher)
    {
        _provider = provider;
        _dispatcher = dispatcher;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _provider.AttachSessionDispatcher(_dispatcher.GetAsync(cancellationToken));
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
