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
    public void NotifyShutdown(string reason)
    {
        var pid = Environment.ProcessId;
        _logger.LogInformation("Netclaw daemon stopping (PID {Pid}, reason: {Reason})", pid, reason);

        _sink.Emit(new OperationalAlert
        {
            AlertId = Guid.NewGuid().ToString("N")[..12],
            Type = "daemon.stopping",
            Category = AlertType.DaemonStopping,
            Severity = "info",
            Summary = $"Netclaw daemon stopping: {reason}",
            Timestamp = _timeProvider.GetUtcNow(),
            Source = pid.ToString(CultureInfo.InvariantCulture),
            Context = new Dictionary<string, string>
            {
                ["pid"] = pid.ToString(CultureInfo.InvariantCulture),
                ["reason"] = reason,
            },
        });
    }
}
