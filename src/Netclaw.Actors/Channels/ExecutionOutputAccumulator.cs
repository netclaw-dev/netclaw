// -----------------------------------------------------------------------
// <copyright file="ExecutionOutputAccumulator.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Text;
using Netclaw.Actors.Protocol;
using Netclaw.Configuration;
using Netclaw.Tools;

namespace Netclaw.Actors.Channels;

/// <summary>
/// What the caller should do after processing an output via
/// <see cref="ExecutionOutputAccumulator.ProcessOutput"/>.
/// </summary>
public enum OutputAction
{
    /// <summary>Output was accumulated; no caller action needed.</summary>
    Continue,

    /// <summary>Turn completed; caller should finalize and stop.</summary>
    TurnCompleted,

    /// <summary>Error received; caller should report failure and stop.</summary>
    Error
}

/// <summary>
/// Tracks output accumulation and notification result state for fire-and-forget
/// execution actors (reminders, webhooks). Pure C# �� no Akka dependency.
/// </summary>
public sealed class ExecutionOutputAccumulator
{
    private readonly ToolName _notificationToolName;
    private readonly Action<string, string, bool>? _onNotifyTracked;
    private readonly StringBuilder _buffer = new();
    private bool _sawTextDelta;
    private bool _notifyAttempted;
    private bool _notifyFailed;
    private string? _notifyFailureDetail;

    /// <summary>
    /// Creates an accumulator.
    /// </summary>
    /// <param name="notificationToolName">
    /// The tool whose results are tracked for notification success/failure.
    /// The accumulator has no channel knowledge — callers supply the tool they care about.
    /// </param>
    /// <param name="onNotifyTracked">
    /// Optional callback invoked when a matching tool result is processed.
    /// Parameters: (toolName, callId, succeeded).
    /// </param>
    public ExecutionOutputAccumulator(
        ToolName notificationToolName,
        Action<string, string, bool>? onNotifyTracked = null)
    {
        _notificationToolName = notificationToolName;
        _onNotifyTracked = onNotifyTracked;
    }

    /// <summary>
    /// The error message from the most recent <see cref="ErrorOutput"/>,
    /// or <c>null</c> if no error has been received.
    /// </summary>
    public string? LastErrorMessage { get; private set; }

    /// <summary>
    /// The underlying exception from the most recent <see cref="ErrorOutput"/>,
    /// or <c>null</c> if no error has been received or the error had no cause.
    /// </summary>
    public Exception? LastErrorCause { get; private set; }

    /// <summary>
    /// The <see cref="ErrorCategory"/> of the most recent error, if any.
    /// </summary>
    public ErrorCategory? LastErrorCategory { get; private set; }

    /// <summary>
    /// Returns the accumulated text output, trimmed.
    /// </summary>
    public string GetAccumulatedText() => _buffer.ToString().Trim();

    /// <summary>
    /// Whether a notification tool was invoked during this execution.
    /// </summary>
    public bool NotifyAttempted => _notifyAttempted;

    /// <summary>
    /// Whether the most recent notification attempt failed.
    /// </summary>
    public bool NotifyFailed => _notifyFailed;

    /// <summary>
    /// Processes a <see cref="SessionOutput"/> and returns the action the caller should take.
    /// </summary>
    public OutputAction ProcessOutput(SessionOutput output)
    {
        switch (output)
        {
            case TextDeltaOutput delta:
                _buffer.Append(delta.Delta);
                _sawTextDelta = true;
                return OutputAction.Continue;

            case TextOutput text:
                if (!_sawTextDelta)
                    _buffer.Append(text.Text);
                return OutputAction.Continue;

            case ToolResultOutput toolResult:
                TrackNotificationResult(toolResult);
                return OutputAction.Continue;

            case BufferFlush:
                return OutputAction.Continue;

            case TurnCompleted:
                return OutputAction.TurnCompleted;

            case ErrorOutput err:
                LastErrorMessage = err.Message;
                LastErrorCause = err.Cause;
                LastErrorCategory = err.Category;
                return OutputAction.Error;

            default:
                return OutputAction.Continue;
        }
    }

    /// <summary>
    /// Evaluates delivery requirements to determine if the execution should be
    /// considered a failure due to notification issues. Returns <c>null</c> on
    /// success, or an error message string describing the notification failure.
    /// </summary>
    /// <param name="requiresChannelDelivery">Whether this is a Channel delivery that expects a notification tool call.</param>
    /// <param name="deliveryRequired">Whether delivery is required for success.</param>
    public string? BuildNotifyFailureMessage(bool requiresChannelDelivery, bool deliveryRequired)
    {
        if (!requiresChannelDelivery)
            return null;

        if (!_notifyAttempted)
        {
            if (!deliveryRequired)
                return null;

            return "Channel delivery was required but no notification tool was invoked.";
        }

        if (_notifyFailed)
            return _notifyFailureDetail ?? "Notification tool returned an unspecified error.";

        return null;
    }

    private void TrackNotificationResult(ToolResultOutput toolResult)
    {
        if (!string.Equals(toolResult.ToolName.Value, _notificationToolName.Value, StringComparison.Ordinal))
            return;

        _notifyAttempted = true;

        var result = toolResult.Result?.Trim() ?? string.Empty;
        if (result.StartsWith("Error", StringComparison.OrdinalIgnoreCase))
        {
            _notifyFailed = true;
            _notifyFailureDetail = result;
            _onNotifyTracked?.Invoke(toolResult.ToolName.Value, toolResult.CallId.Value, false);
            return;
        }

        _notifyFailed = false;
        _notifyFailureDetail = null;
        _onNotifyTracked?.Invoke(toolResult.ToolName.Value, toolResult.CallId.Value, true);
    }
}
