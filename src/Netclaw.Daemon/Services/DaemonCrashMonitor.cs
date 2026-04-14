using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Netclaw.Configuration;
using Netclaw.Daemon.Gateway;

namespace Netclaw.Daemon.Services;

internal sealed class DaemonCrashMonitor : IDisposable
{
    private readonly TimeProvider _timeProvider;
    private readonly string _logsDirectory;
    private readonly UnhandledExceptionEventHandler _unhandledHandler;
    private readonly EventHandler<UnobservedTaskExceptionEventArgs> _unobservedTaskHandler;
    private IServiceProvider? _services;

    private DaemonCrashMonitor(string logsDirectory, TimeProvider? timeProvider = null)
    {
        _logsDirectory = logsDirectory;
        _timeProvider = timeProvider ?? TimeProvider.System;

        _unhandledHandler = HandleUnhandledException;
        _unobservedTaskHandler = HandleUnobservedTaskException;

        AppDomain.CurrentDomain.UnhandledException += _unhandledHandler;
        TaskScheduler.UnobservedTaskException += _unobservedTaskHandler;
    }

    public static DaemonCrashMonitor Register(NetclawPaths paths, TimeProvider? timeProvider = null)
        => new(paths.LogsDirectory, timeProvider);

    public void AttachServices(IServiceProvider services)
    {
        _services = services;
    }

    public void DetachServices()
    {
        _services = null;
    }

    public void RecordTopLevelException(Exception exception)
    {
        HandleCrash("daemon-main", exception, isTerminating: true, isUnobservedTask: false);
    }

    public void Dispose()
    {
        AppDomain.CurrentDomain.UnhandledException -= _unhandledHandler;
        TaskScheduler.UnobservedTaskException -= _unobservedTaskHandler;
    }

    private void HandleUnhandledException(object sender, UnhandledExceptionEventArgs args)
    {
        var exception = args.ExceptionObject as Exception
            ?? new InvalidOperationException($"Unhandled non-exception object: {args.ExceptionObject?.GetType().FullName ?? "null"}");

        HandleCrash("daemon-unhandled", exception, args.IsTerminating, isUnobservedTask: false);
    }

    private void HandleUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs args)
    {
        HandleCrash("daemon-unobserved", args.Exception, isTerminating: false, isUnobservedTask: true);
        args.SetObserved();
    }

    private void HandleCrash(string reason, Exception exception, bool isTerminating, bool isUnobservedTask)
    {
        try
        {
            var context = BuildContext(reason, isTerminating, isUnobservedTask);
            var crashLogPath = CrashLogWriter.TryWrite(
                exception,
                reason,
                timeProvider: _timeProvider,
                logsDirectory: _logsDirectory,
                context: context);

            if (!string.IsNullOrWhiteSpace(crashLogPath))
                context["crash_log_path"] = crashLogPath;

            TryNotifyCrashing(reason, exception, crashLogPath, context);
        }
        catch (Exception ex)
        {
            TryLogMonitorFailure("Crash monitor failed while handling process exception.", ex);
        }
    }

    private Dictionary<string, string> BuildContext(string reason, bool isTerminating, bool isUnobservedTask)
    {
        var context = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["reason"] = reason,
            ["is_terminating"] = isTerminating.ToString(),
            ["is_unobserved_task"] = isUnobservedTask.ToString(),
            ["pid"] = Environment.ProcessId.ToString(),
            ["managed_thread_id"] = Environment.CurrentManagedThreadId.ToString(),
            ["working_directory"] = Environment.CurrentDirectory,
        };

        try
        {
            using var process = Process.GetCurrentProcess();
            var startTimeUtc = process.StartTime.ToUniversalTime();
            var uptime = _timeProvider.GetUtcNow() - startTimeUtc;

            context["process_uptime_seconds"] = Math.Max(0, (long)uptime.TotalSeconds).ToString();
            context["working_set_mb"] = (process.WorkingSet64 / (1024 * 1024)).ToString();
            context["private_memory_mb"] = (process.PrivateMemorySize64 / (1024 * 1024)).ToString();
            context["gc_total_memory_mb"] = (GC.GetTotalMemory(forceFullCollection: false) / (1024 * 1024)).ToString();
        }
        catch (Exception ex)
        {
            TryLogMonitorFailure("Failed to capture process metrics for crash diagnostics.", ex);
        }

        var latestTurn = CrashContextSnapshot.GetLatest();
        if (latestTurn is not null)
        {
            context["latest_session_id"] = latestTurn.SessionId;
            if (!string.IsNullOrWhiteSpace(latestTurn.TurnId))
                context["latest_turn_id"] = latestTurn.TurnId!;
            if (!string.IsNullOrWhiteSpace(latestTurn.MessageId))
                context["latest_message_id"] = latestTurn.MessageId!;
            if (!string.IsNullOrWhiteSpace(latestTurn.ChannelType))
                context["latest_channel_type"] = latestTurn.ChannelType!;
            context["latest_turn_observed_at_utc"] = latestTurn.ObservedAtUtc.ToString("O");
        }

        TryPopulateCatalogContext(context);
        return context;
    }

    private void TryPopulateCatalogContext(IDictionary<string, string> context)
    {
        var services = _services;
        if (services is null)
            return;

        try
        {
            var startClock = services.GetService<DaemonStartClock>();
            if (startClock is not null)
                context["daemon_started_at_utc"] = startClock.StartedAt.ToString("O");

            var sessionCatalog = services.GetService<SessionCatalogService>();
            if (sessionCatalog is null)
                return;

            var stats = sessionCatalog.GetStats();
            context["session_total"] = stats.TotalSessions.ToString();
            context["session_active"] = stats.ActiveSessions.ToString();
            context["session_turn_total"] = stats.TotalTurns.ToString();

            var latest = sessionCatalog.ListRecent(1).FirstOrDefault();
            if (latest is null)
                return;

            context["catalog_latest_session"] = latest.PersistenceId;
            context["catalog_latest_status"] = latest.Status;
            context["catalog_latest_turn_count"] = latest.TurnCount.ToString();
            context["catalog_latest_last_activity_ms"] = latest.LastActivity.ToString();
        }
        catch (Exception ex)
        {
            TryLogMonitorFailure("Failed to capture session catalog context for crash diagnostics.", ex);
        }
    }

    private void TryNotifyCrashing(
        string reason,
        Exception exception,
        string? crashLogPath,
        IReadOnlyDictionary<string, string> context)
    {
        var services = _services;
        if (services is null)
            return;

        try
        {
            var notifier = services.GetService<DaemonLifecycleNotifier>();
            notifier?.NotifyCrashing(reason, exception, crashLogPath, context);
        }
        catch (Exception ex)
        {
            TryLogMonitorFailure("Failed to emit daemon crashing notification.", ex);
        }
    }

    private void TryLogMonitorFailure(string message, Exception exception)
    {
        var services = _services;

        try
        {
            var loggerFactory = services?.GetService<ILoggerFactory>();
            var logger = loggerFactory?.CreateLogger("Netclaw.Daemon.CrashMonitor");
            if (logger is not null)
            {
                logger.LogWarning(exception, "{Message}", message);
                return;
            }
        }
        catch (Exception loggingFailure)
        {
            Console.Error.WriteLine($"[Netclaw.Daemon.CrashMonitor] Failed to write monitor warning via logger factory: {loggingFailure}");
        }

        Console.Error.WriteLine($"[Netclaw.Daemon.CrashMonitor] {message} {exception}");
    }
}
