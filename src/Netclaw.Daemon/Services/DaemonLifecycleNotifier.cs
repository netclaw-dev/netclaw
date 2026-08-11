// -----------------------------------------------------------------------
// <copyright file="DaemonLifecycleNotifier.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Globalization;
using System.Text;
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

        _sink.Emit(OperationalAlert.Create(
            _timeProvider,
            type: "daemon.started",
            category: AlertType.DaemonStarted,
            summary: "Netclaw daemon started",
            severity: AlertSeverity.Info,
            source: pid.ToString(CultureInfo.InvariantCulture),
            context: new Dictionary<string, string>
            {
                ["pid"] = pid.ToString(CultureInfo.InvariantCulture),
            }));
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
        // `reason` reaches this method straight from the HTTP query string
        // (ShutdownDaemonRequest in LifecycleEndpointRouteBuilderExtensions), so a
        // caller can inject CR/LF into log lines. Sanitize before logging or
        // propagating into the alert context. (cs/log-forging)
        var safeReason = SanitizeReason(reason);
        _logger.LogInformation("Netclaw daemon stopping (PID {Pid}, reason: {Reason})", pid, safeReason);

        var context = new Dictionary<string, string>
        {
            ["pid"] = pid.ToString(CultureInfo.InvariantCulture),
            ["reason"] = safeReason,
        };

        if (additionalContext is not null)
        {
            foreach (var pair in additionalContext)
                context[pair.Key] = pair.Value;
        }

        _sink.Emit(OperationalAlert.Create(
            _timeProvider,
            type: "daemon.stopping",
            category: AlertType.DaemonStopping,
            summary: $"Netclaw daemon stopping: {safeReason}",
            severity: AlertSeverity.Info,
            source: pid.ToString(CultureInfo.InvariantCulture),
            context: context));
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
        // Today every caller passes a code-side constant for `reason`, but
        // sanitizing here keeps the contract symmetric with NotifyShutdown and
        // forecloses on future callers that might forward HTTP input.
        var safeReason = SanitizeReason(reason);

        _logger.LogCritical(
            exception,
            "Netclaw daemon crashing (PID {Pid}, reason: {Reason}, exceptionType: {ExceptionType})",
            pid,
            safeReason,
            exceptionType);

        var context = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["pid"] = pid.ToString(CultureInfo.InvariantCulture),
            ["reason"] = safeReason,
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
            _sink.Emit(OperationalAlert.Create(
                _timeProvider,
                type: "daemon.crashing",
                category: AlertType.DaemonCrashed,
                summary: $"Netclaw daemon crashing: {safeReason} ({exceptionType})",
                severity: AlertSeverity.Critical,
                source: pid.ToString(CultureInfo.InvariantCulture),
                context: context));
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

    private const int MaxReasonChars = 200;

    // Returns true for chars that, if left in a log line or alert summary, can
    // act as a line terminator for at least one downstream consumer:
    // - char.IsControl covers Cc (CR/LF/NUL/ESC/etc.)
    // - U+2028 (LINE SEPARATOR, Zl) and U+2029 (PARAGRAPH SEPARATOR, Zp) are NOT
    //   in Cc and so are not caught by IsControl, but JSON-line readers, many
    //   log shippers, and pre-ES2019 JSON parsers still split on them.
    private static bool IsLogLineBreak(char c)
        => char.IsControl(c) || c is '\u2028' or '\u2029';

    private static string SanitizeReason(string reason)
    {
        if (string.IsNullOrEmpty(reason))
            return string.Empty;

        var hasBreak = false;
        foreach (var ch in reason)
        {
            if (IsLogLineBreak(ch))
            {
                hasBreak = true;
                break;
            }
        }

        if (!hasBreak)
            return TrimAtCharBoundary(reason, MaxReasonChars);

        // Strip — don't space-replace. Space would let a CR/LF payload still
        // pass through as a plausible field separator to key=value structured
        // log parsers ("reason=ok\nlevel=critical" → "reason=ok level=critical").
        var buf = new StringBuilder(reason.Length);
        foreach (var ch in reason)
        {
            if (!IsLogLineBreak(ch))
                buf.Append(ch);
        }
        return TrimAtCharBoundary(buf.ToString(), MaxReasonChars);
    }

    private static string TrimAtCharBoundary(string value, int maxChars)
    {
        if (value.Length <= maxChars)
            return value;

        // Don't split a surrogate pair at the truncation boundary — a dangling
        // high surrogate makes downstream UTF-8 encoders emit U+FFFD or throw.
        var cut = maxChars;
        if (char.IsHighSurrogate(value[cut - 1]))
            cut -= 1;
        return value[..cut];
    }
}
