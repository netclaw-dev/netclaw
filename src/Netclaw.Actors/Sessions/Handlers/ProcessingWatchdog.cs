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
    private static readonly object ProgressTimerKey = new();
    private long _operationId;
    private string? _operationName;
    private TimeSpan? _noProgressDeadline;

    public long CurrentOperationId => _operationId;
    public string? CurrentOperationName => _operationName;

    /// <summary>
    /// Start a new watchdog timer for the given operation. When
    /// <paramref name="noProgressDeadline"/> is provided, a second, independent
    /// deadline is armed that only substantive output resets — content-free
    /// keepalives never refresh it (see <see cref="OnStreamProgress"/>). This
    /// bounds a backend that streams keepalives forever without ever producing a
    /// real token, which the liveness timer alone cannot catch because keepalives
    /// refresh it. Pass null to bound the operation by liveness only.
    /// </summary>
    public void Start(string operationName, TimeSpan timeout, ITimerScheduler timers, TimeSpan? noProgressDeadline = null)
    {
        _operationId++;
        _operationName = operationName;
        _noProgressDeadline = noProgressDeadline;

        timers.StartSingleTimer(
            TimerKey,
            new ProcessingWatchdogExpired(_operationId, operationName),
            timeout);

        // Always clear a stale deadline from the previous operation, then re-arm
        // for this one. The no-progress expiry carries NoProgress=true so the
        // handler distinguishes it from a liveness tick.
        timers.Cancel(ProgressTimerKey);
        if (noProgressDeadline is { } deadline)
        {
            timers.StartSingleTimer(
                ProgressTimerKey,
                new ProcessingWatchdogExpired(_operationId, operationName, NoProgress: true),
                deadline);
        }
    }

    /// <summary>
    /// Cancel both watchdog timers and clear the operation name.
    /// </summary>
    public void Stop(ITimerScheduler timers)
    {
        timers.Cancel(TimerKey);
        timers.Cancel(ProgressTimerKey);
        _operationName = null;
        _noProgressDeadline = null;
    }

    /// <summary>
    /// Apply a streaming progress signal under the two-phase budget shared by the
    /// session and sub-agent paths. On the first substantive delta, promote from the
    /// generous prefill budget to the tighter inter-delta budget; otherwise refresh
    /// whichever budget is currently in force — the prefill budget until the first
    /// substantive delta (so a content-free keepalive cannot shrink the
    /// wait-for-first-token window), the inter-delta budget after. A substantive
    /// delta also resets the no-progress deadline (if armed); keepalives never do,
    /// which is what lets that deadline catch a heartbeat-only wedge. Returns the
    /// updated "already promoted" flag for the caller to thread back in.
    /// </summary>
    public bool OnStreamProgress(
        bool isSubstantive,
        bool alreadyPromoted,
        TimeSpan prefillTimeout,
        TimeSpan interDeltaTimeout,
        ITimerScheduler timers)
    {
        if (isSubstantive)
            RestartProgressTimer(timers);

        if (isSubstantive && !alreadyPromoted)
        {
            RestartLlmTimer(interDeltaTimeout, timers);
            return true;
        }

        RestartLlmTimer(alreadyPromoted ? interDeltaTimeout : prefillTimeout, timers);
        return alreadyPromoted;
    }

    private void RestartLlmTimer(TimeSpan timeout, ITimerScheduler timers)
    {
        if (_operationName is not LlmCall)
            return;

        timers.StartSingleTimer(
            TimerKey,
            new ProcessingWatchdogExpired(_operationId, _operationName),
            timeout);
    }

    private void RestartProgressTimer(ITimerScheduler timers)
    {
        if (_operationName is not LlmCall || _noProgressDeadline is not { } deadline)
            return;

        timers.StartSingleTimer(
            ProgressTimerKey,
            new ProcessingWatchdogExpired(_operationId, _operationName, NoProgress: true),
            deadline);
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
