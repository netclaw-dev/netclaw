// -----------------------------------------------------------------------
// <copyright file="ProcessingWatchdog.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Akka.Actor;

namespace Netclaw.Actors.Sessions.Handlers;

/// <summary>
/// Manages the processing watchdog timer that detects stuck LLM calls and
/// compaction operations. Tool-call liveness is handled per call by the
/// tool-execution pipeline, not here.
/// </summary>
internal sealed class ProcessingWatchdog
{
    public const string LlmCall = "llm-call";
    public const string Compaction = "compaction";

    private static readonly object TimerKey = new();
    private long _operationId;
    private string? _operationName;

    public long CurrentOperationId => _operationId;
    public string? CurrentOperationName => _operationName;

    /// <summary>
    /// Start a new watchdog timer for the given operation.
    /// </summary>
    public void Start(string operationName, TimeSpan timeout, ITimerScheduler timers)
    {
        _operationId++;
        _operationName = operationName;

        timers.StartSingleTimer(
            TimerKey,
            new ProcessingWatchdogExpired(_operationId, operationName),
            timeout);
    }

    /// <summary>
    /// Cancel the watchdog timer and clear the operation name.
    /// </summary>
    public void Stop(ITimerScheduler timers)
    {
        timers.Cancel(TimerKey);
        _operationName = null;
    }

    /// <summary>
    /// Switch from the generous prefill budget to the tighter inter-delta timeout.
    /// Called once when the first real streaming delta arrives.
    /// </summary>
    public void Promote(TimeSpan interDeltaTimeout, ITimerScheduler timers)
        => RestartLlmTimer(interDeltaTimeout, timers);

    /// <summary>
    /// Refresh the watchdog timer for an active LLM call (streaming keepalive).
    /// Only refreshes if the current operation is <see cref="LlmCall"/>.
    /// </summary>
    public void Refresh(TimeSpan timeout, ITimerScheduler timers)
        => RestartLlmTimer(timeout, timers);

    private void RestartLlmTimer(TimeSpan timeout, ITimerScheduler timers)
    {
        if (_operationName is not LlmCall)
            return;

        timers.StartSingleTimer(
            TimerKey,
            new ProcessingWatchdogExpired(_operationId, _operationName),
            timeout);
    }

    /// <summary>
    /// Check whether the given watchdog expiration message matches the current operation.
    /// </summary>
    public bool IsCurrent(ProcessingWatchdogExpired msg)
        => msg.OperationId == _operationId
           && string.Equals(msg.OperationName, _operationName, StringComparison.Ordinal);

    /// <summary>
    /// Check whether the current operation matches the expected name and ID.
    /// Used for compaction completion validation.
    /// </summary>
    public bool IsCurrentOperation(string expectedName, long operationId)
        => _operationName == expectedName && _operationId == operationId;
}
