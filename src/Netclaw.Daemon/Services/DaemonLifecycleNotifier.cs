using System.Globalization;
using Microsoft.Extensions.Logging;
using Netclaw.Configuration;

namespace Netclaw.Daemon.Services;

/// <summary>
/// Fires operational webhook notifications for daemon lifecycle events (start/stop).
/// Called directly by the shutdown endpoint, <see cref="ConfigWatcherService"/>, and
/// the <see cref="Microsoft.Extensions.Hosting.IHostApplicationLifetime.ApplicationStarted"/> hook.
/// </summary>
public sealed class DaemonLifecycleNotifier
{
    private readonly IOperationalNotificationSink _sink;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<DaemonLifecycleNotifier> _logger;

    public DaemonLifecycleNotifier(
        IOperationalNotificationSink sink,
        TimeProvider timeProvider,
        ILogger<DaemonLifecycleNotifier> logger)
    {
        _sink = sink;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    /// <summary>
    /// Announces that the daemon has started and is ready to accept work.
    /// </summary>
    public void NotifyStarted()
    {
        var pid = Environment.ProcessId;
        _logger.LogInformation("Netclaw daemon started (PID {Pid})", pid);

        _sink.Emit(new OperationalAlert
        {
            AlertId = Guid.NewGuid().ToString("N")[..12],
            Type = "daemon.started",
            Category = AlertType.DaemonStarted,
            Severity = "info",
            Summary = "Netclaw daemon started",
            Timestamp = _timeProvider.GetUtcNow(),
            Source = pid.ToString(CultureInfo.InvariantCulture),
            Context = new Dictionary<string, string>
            {
                ["pid"] = pid.ToString(CultureInfo.InvariantCulture),
            },
        });
    }

    /// <summary>
    /// Announces that the daemon is about to stop, with a reason describing why.
    /// </summary>
    /// <param name="reason">
    /// Human-readable reason string (e.g., "cli-stop", "update", "config-reload").
    /// </param>
    public void NotifyShutdown(string reason, IReadOnlyDictionary<string, string>? additionalContext = null)
    {
        var pid = Environment.ProcessId;
        _logger.LogInformation("Netclaw daemon stopping (PID {Pid}, reason: {Reason})", pid, reason);

        var context = new Dictionary<string, string>
        {
            ["pid"] = pid.ToString(CultureInfo.InvariantCulture),
            ["reason"] = reason,
        };

        if (additionalContext is not null)
        {
            foreach (var pair in additionalContext)
                context[pair.Key] = pair.Value;
        }

        _sink.Emit(new OperationalAlert
        {
            AlertId = Guid.NewGuid().ToString("N")[..12],
            Type = "daemon.stopping",
            Category = AlertType.DaemonStopping,
            Severity = "info",
            Summary = $"Netclaw daemon stopping: {reason}",
            Timestamp = _timeProvider.GetUtcNow(),
            Source = pid.ToString(CultureInfo.InvariantCulture),
            Context = context,
        });
    }

    /// <summary>
    /// Announces that the daemon encountered a process-level crash path.
    /// Emission is best-effort and must not throw.
    /// </summary>
    public void NotifyCrashing(
        string reason,
        Exception exception,
        string? crashLogPath = null,
        IReadOnlyDictionary<string, string>? additionalContext = null)
    {
        var pid = Environment.ProcessId;
        var exceptionType = exception.GetType().FullName ?? exception.GetType().Name;
        var exceptionMessage = string.IsNullOrWhiteSpace(exception.Message)
            ? "(no exception message)"
            : exception.Message;

        _logger.LogCritical(
            exception,
            "Netclaw daemon crashing (PID {Pid}, reason: {Reason}, exceptionType: {ExceptionType})",
            pid,
            reason,
            exceptionType);

        var context = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["pid"] = pid.ToString(CultureInfo.InvariantCulture),
            ["reason"] = reason,
            ["exceptionType"] = exceptionType,
            ["exceptionMessage"] = Trim(exceptionMessage, 400),
        };

        if (!string.IsNullOrWhiteSpace(crashLogPath))
            context["crashLogPath"] = crashLogPath!;

        if (additionalContext is not null)
        {
            foreach (var pair in additionalContext)
                context[pair.Key] = pair.Value;
        }

        try
        {
            _sink.Emit(new OperationalAlert
            {
                AlertId = Guid.NewGuid().ToString("N")[..12],
                Type = "daemon.crashing",
                Category = AlertType.DaemonCrashed,
                Severity = "critical",
                Summary = $"Netclaw daemon crashing: {reason} ({exceptionType})",
                Timestamp = _timeProvider.GetUtcNow(),
                Source = pid.ToString(CultureInfo.InvariantCulture),
                Context = context,
            });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to emit daemon crashing alert");
        }
    }

    private static string Trim(string value, int maxChars)
    {
        if (value.Length <= maxChars)
            return value;

        return value[..maxChars];
    }
}
