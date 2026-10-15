// -----------------------------------------------------------------------
// <copyright file="SessionExecutionLifetime.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Akka.Actor;
using Netclaw.Actors.Channels;
using Netclaw.Actors.Protocol;
using Netclaw.Actors.Reminders;
using static Netclaw.Actors.Sessions.SessionProtocol;

namespace Netclaw.Actors.Sessions.Handlers;

internal sealed record ModelStoppedForRestart(long CallId, Exception? Failure)
    : INoSerializationVerificationNeeded;

internal sealed record ToolPipelineStoppedForRestart(Task WorkTask, Exception? Failure)
    : INoSerializationVerificationNeeded;

internal sealed record RestartModelGraceExpired(long CallId) : INoSerializationVerificationNeeded;

internal enum RestartAction { Wait, EnterPassivation, StopToolTask, StartModelGrace }
internal enum ModelStopAction { Ignore, CloseInputs, EnterPassivation }
internal enum ToolStopAction { Ignore, FailTurn, EnterPassivation }
internal enum ReminderEligibility { None, Eligible, EmittedText }
internal enum PassivationWork { NoAction, WaitForReap, AttemptSnapshot }
internal readonly record struct PassivationStart(long? ReapEpoch, bool RequestDistillation);
internal sealed record FinalStopResult(IActorRef? ReplyTo, ReminderDefinition? Reminder, string? OmissionReason);
internal sealed record RestartContextResult(TurnContext? Context, string? Reason);

/// <summary>
/// Owns model and tool resources, restart drain, and passivation policy on the session actor thread.
/// Task stop acknowledgements require the exact call ID or task reference.
/// </summary>
internal sealed class SessionExecutionLifetime(ITimerScheduler timers, IActorRef actor)
{
    internal const string RestartReminderInstruction =
        "Resume the work that was interrupted by the daemon restart.";

    private static readonly TimeSpan RestartModelGracePeriod = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan RestartReminderLifetime = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan PassivationGracePeriod = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan PassivationFinalStopDelay = TimeSpan.FromMilliseconds(100);
    private static readonly object RestartModelGraceTimerKey = new();
    private static readonly object PassivationTimerKey = new();
    private static readonly object PassivationFinalStopTimerKey = new();

    private enum AttemptMilestone { Distillation, Prerequisite, SnapshotReady, Grace, FinalEffectsClaimed }

    private sealed class PassivationAttempt(long? reapEpoch)
    {
        public long? ReapEpoch { get; } = reapEpoch;
        public AttemptMilestone Milestone { get; set; } = AttemptMilestone.Distillation;
    }

    private sealed class ReapOperation(long epoch)
    {
        public long Epoch { get; } = epoch;
        public bool AwaitingCommit { get; set; }
    }

    private readonly ProcessingWatchdog _watchdog = new();
    private CancellationTokenSource? _modelCts;
    private Task? _modelTask;
    private long _modelCallId;
    private bool _modelStopRequested;
    private bool _anyContentStreamed;
    private CancellationTokenSource? _toolCts;
    private Task? _toolTask;
    private bool _toolStopRequested;
    private IActorRef? _restartDrainReplyTo;
    private DateTimeOffset? _restartInterruptionAt;
    private long _jobReapEpoch;
    private ReapOperation? _reap;
    private PassivationAttempt? _attempt;

    public bool RestartDrainRequested => _restartDrainReplyTo is not null;
    public bool NeedsAbruptStopNotification => _attempt?.Milestone != AttemptMilestone.FinalEffectsClaimed;

    public RestartAction RequestRestart(
        IActorRef replyTo,
        SessionPhase phase,
        int selectedInputCount,
        bool hasBufferedContinuation,
        bool canParkDurableApprovals)
    {
        _restartDrainReplyTo = replyTo;
        if (phase == SessionPhase.Ready)
            return RestartAction.EnterPassivation;

        if (TryStopDurableApprovalWaits(phase, hasBufferedContinuation, canParkDurableApprovals))
            return RestartAction.StopToolTask;

        if (phase == SessionPhase.Processing && HasLiveModel && selectedInputCount > 0)
        {
            timers.StartSingleTimer(
                RestartModelGraceTimerKey,
                new RestartModelGraceExpired(_modelCallId),
                RestartModelGracePeriod);
            return RestartAction.StartModelGrace;
        }

        return RestartAction.Wait;
    }

    public bool TryStopDurableApprovalWaits(
        SessionPhase phase,
        bool hasBufferedContinuation,
        bool canParkDurableApprovals)
        => RestartDrainRequested
           && phase == SessionPhase.Processing
           && !hasBufferedContinuation
           && canParkDurableApprovals
           && RequestToolStop();

    public long? PlanRestartModelStop(RestartModelGraceExpired message, SessionPhase phase, int selectedInputCount)
        => RestartDrainRequested
           && phase == SessionPhase.Processing
           && selectedInputCount > 0
           && CanDispatchPreparedModel(message.CallId)
            ? message.CallId
            : null;

    public ModelStopAction ObserveModelStop(ModelStoppedForRestart message, bool emittedText)
    {
        if (!AcceptModelStop(message))
            return ModelStopAction.Ignore;

        return message.Failure is not null || emittedText
            ? ModelStopAction.CloseInputs
            : ModelStopAction.EnterPassivation;
    }

    public ToolStopAction ObserveToolStop(ToolPipelineStoppedForRestart message)
    {
        if (!AcceptToolStop(message))
            return ToolStopAction.Ignore;

        return message.Failure is not null
            ? ToolStopAction.FailTurn
            : ToolStopAction.EnterPassivation;
    }

    public ReminderEligibility MarkRestartReminderEligible(
        int pendingInputCount,
        bool emittedText,
        int selectedInputCount,
        DateTimeOffset now)
    {
        if (pendingInputCount == 0)
            return ReminderEligibility.None;

        if (emittedText && selectedInputCount > 0)
            return ReminderEligibility.EmittedText;

        _restartInterruptionAt = now;
        return ReminderEligibility.Eligible;
    }

    public void ClearRestartReminderEligibility() => _restartInterruptionAt = null;

    public PassivationStart BeginPassivation(bool hasTrackedJobs, bool managerAvailable, bool observerAvailable)
    {
        long? reapEpoch = null;
        if (hasTrackedJobs && managerAvailable)
        {
            reapEpoch = ++_jobReapEpoch;
            _reap = new ReapOperation(reapEpoch.Value);
        }

        _attempt = new PassivationAttempt(reapEpoch);
        var requestDistillation = !RestartDrainRequested && observerAvailable;
        if (requestDistillation)
            timers.StartSingleTimer(PassivationTimerKey, new PassivationTimeout(), PassivationGracePeriod);

        return new PassivationStart(reapEpoch, requestDistillation);
    }

    public void CompleteDistillation() => timers.Cancel(PassivationTimerKey);

    public PassivationWork CompletePassivationWork(SessionPhase phase)
    {
        if (phase != SessionPhase.Passivating || _attempt is null
            || _attempt.Milestone is AttemptMilestone.SnapshotReady
                or AttemptMilestone.Grace or AttemptMilestone.FinalEffectsClaimed)
            return PassivationWork.NoAction;

        if (_attempt.ReapEpoch is { } epoch && _reap?.Epoch == epoch)
        {
            _attempt.Milestone = AttemptMilestone.Prerequisite;
            return PassivationWork.WaitForReap;
        }

        _attempt.Milestone = AttemptMilestone.SnapshotReady;
        return PassivationWork.AttemptSnapshot;
    }

    public bool ObserveReapReply(long epoch)
    {
        if (_reap is not { } reap || reap.Epoch != epoch || reap.AwaitingCommit)
            return false;

        // One manager reply can cause one journal write. The callback owns the completion.
        reap.AwaitingCommit = true;
        return true;
    }

    public PassivationWork ReapCommitted(long epoch, SessionPhase phase)
    {
        if (_reap is not { } reap || reap.Epoch != epoch || !reap.AwaitingCommit)
            return PassivationWork.NoAction;

        _reap = null;
        if (phase != SessionPhase.Passivating || _attempt is null
            || _attempt.ReapEpoch != epoch || _attempt.Milestone != AttemptMilestone.Prerequisite)
            return PassivationWork.NoAction;

        _attempt.Milestone = AttemptMilestone.SnapshotReady;
        return PassivationWork.AttemptSnapshot;
    }

    public bool AfterFinalSnapshotAttempt(SessionPhase phase)
    {
        if (phase != SessionPhase.Passivating || _attempt?.Milestone != AttemptMilestone.SnapshotReady)
            throw new InvalidOperationException("The passivation attempt is not ready for a final snapshot.");

        if (RestartDrainRequested)
            return true;

        _attempt.Milestone = AttemptMilestone.Grace;
        timers.StartSingleTimer(
            PassivationFinalStopTimerKey,
            new PassivationFinalStop(),
            PassivationFinalStopDelay);
        return false;
    }

    public bool IsCurrentFinalStop(SessionPhase phase)
        => phase == SessionPhase.Passivating && _attempt?.Milestone == AttemptMilestone.Grace;

    public void AbortPassivation()
    {
        if (_attempt?.Milestone == AttemptMilestone.FinalEffectsClaimed)
            return;

        timers.Cancel(PassivationTimerKey);
        timers.Cancel(PassivationFinalStopTimerKey);
        _attempt = null;
    }

    public FinalStopResult? ClaimFinalStop(SessionPhase phase, SessionId sessionId, IReadOnlyList<InputAdmitted> pending)
    {
        if (phase != SessionPhase.Passivating || _attempt is null
            || _attempt.Milestone is not (AttemptMilestone.Grace or AttemptMilestone.SnapshotReady))
            return null;

        var (reminder, reason) = BuildRestartReminder(sessionId, pending);
        _attempt.Milestone = AttemptMilestone.FinalEffectsClaimed;
        return new FinalStopResult(_restartDrainReplyTo, reminder, reason);
    }

    public RestartContextResult ValidateRestartResume(IReadOnlyList<InputAdmitted> pending)
    {
        TurnContext? first = null;
        var reason = "no pending input";
        foreach (var input in pending)
        {
            if (!TurnContext.TryFromRecord(input.TurnContext, out var candidate, out reason)
                || candidate is null)
                return new RestartContextResult(null, reason);

            if (first is null)
            {
                first = candidate;
                continue;
            }

            if (!first.HasSameAuthority(candidate))
                return new RestartContextResult(null, "pending inputs have different authority");
        }

        return new RestartContextResult(first, first is null ? reason : null);
    }

    private (ReminderDefinition? Reminder, string? Reason) BuildRestartReminder(
        SessionId sessionId,
        IReadOnlyList<InputAdmitted> pending)
    {
        if (_restartInterruptionAt is not { } interruptedAt || pending.Count == 0)
            return (null, null);

        var contextResult = ValidateRestartResume(pending);
        if (contextResult.Context is not { } context)
            return (null, contextResult.Reason ?? "invalid stored authority");

        if (context.ChannelType is not { } channelType)
            return (null, "the turn has no channel type");

        return (new ReminderDefinition
        {
            Id = ReminderIdGenerator.Generate("restart resume"),
            Title = "Resume after daemon restart",
            Instructions = RestartReminderInstruction,
            Schedule = new ReminderSchedule { Type = ReminderScheduleType.OneShot, FireAt = interruptedAt },
            Delivery = new ReminderDelivery
            {
                Kind = DeliveryKind.CurrentSession,
                SessionId = sessionId.Value,
                OriginChannelType = channelType
            },
            DeliveryRequired = true,
            Audience = context.Audience,
            Boundary = context.Boundary,
            CreatedBy = "system",
            CreatedAt = interruptedAt,
            UpdatedAt = interruptedAt,
            ExpiresAt = interruptedAt + RestartReminderLifetime
        }, null);
    }

    public long CurrentModelCallId => _modelCallId;
    public bool HasLiveModel => _modelCts is not null;
    public bool HasContentStreamed => _anyContentStreamed;
    public bool IsCurrentWatchdog(ProcessingWatchdogExpired message) => _watchdog.IsCurrent(message);
    public bool IsCurrentCompaction(long operationId)
        => _watchdog.IsCurrentOperation(ProcessingWatchdog.Compaction, operationId);

    public (long CallId, CancellationToken Token) BeginModel()
    {
        CancelAndDisposeModel();
        _modelTask = null;
        _modelCts = new CancellationTokenSource();
        _modelCallId++;
        _anyContentStreamed = false;
        return (_modelCallId, _modelCts.Token);
    }

    public bool CanDispatchPreparedModel(long callId)
        => callId == _modelCallId && _modelCts is not null && !_modelStopRequested;

    public (long CallId, CancellationToken Token) ModelDispatch()
    {
        if (!CanDispatchPreparedModel(_modelCallId))
            throw new InvalidOperationException("The current model call cannot dispatch.");

        return (_modelCallId, _modelCts!.Token);
    }

    public void AttachModel(long callId, Task task)
    {
        if (!CanDispatchPreparedModel(callId) || _modelTask is { IsCompleted: false })
            throw new InvalidOperationException("The model task does not match a live call.");

        _modelTask = task;
    }

    public bool AcceptModelResult(long callId)
    {
        if (callId != _modelCallId || _modelStopRequested)
            return false;

        _modelTask = null;
        StopWatchdog();
        CancelAndDisposeModel();
        return true;
    }

    public bool AcceptModelProgress(
        long callId,
        bool substantive,
        TimeSpan prefillTimeout,
        TimeSpan interDeltaTimeout)
    {
        if (callId != _modelCallId || _modelStopRequested)
            return false;

        _anyContentStreamed = _watchdog.OnStreamProgress(
            substantive, _anyContentStreamed, prefillTimeout, interDeltaTimeout, timers);
        return true;
    }

    public void StartModelWatchdog(TimeSpan prefillTimeout, TimeSpan noProgressTimeout)
        => _watchdog.Start(ProcessingWatchdog.LlmCall, prefillTimeout, timers, noProgressTimeout);

    public void StopModelForTimeout()
    {
        StopWatchdog();
        CancelAndDisposeModel();
    }

    public bool RequestModelStop(long callId)
    {
        if (callId != _modelCallId || _modelCts is null || _modelStopRequested)
            return false;

        _modelStopRequested = true;
        StopWatchdog();
        _modelCts.Cancel();
        if (_modelTask is { IsCompleted: false } task)
            _ = ReportModelStopAsync(task, actor, callId);
        else
            actor.Tell(new ModelStoppedForRestart(callId, null));
        return true;
    }

    public bool AcceptModelStop(ModelStoppedForRestart message)
    {
        if (!_modelStopRequested || message.CallId != _modelCallId)
            return false;

        _modelCallId++;
        _modelTask = null;
        CancelAndDisposeModel();
        _modelStopRequested = false;
        return true;
    }

    public CancellationToken BeginTool()
    {
        CancelAndDisposeTool();
        _toolTask = null;
        _toolCts = new CancellationTokenSource();
        return _toolCts.Token;
    }

    public void AttachTool(Task task)
    {
        if (_toolCts is null || _toolTask is { IsCompleted: false })
            throw new InvalidOperationException("The tool task does not match a live batch.");

        _toolTask = task;
    }

    // A synthetic correction can send the batch message without a tool task.
    // The message does not prove that an ordinary pipeline task returned.
    public void OnToolBatchMessage()
    {
        StopWatchdog();
        CancelAndDisposeTool();
        _toolTask = null;
    }

    public void OnToolFailure()
    {
        StopWatchdog();
        CancelAndDisposeTool();
    }

    public bool CanRequestToolStop()
        => !_toolStopRequested && _toolCts is not null && _toolTask is { IsCompleted: false };

    public bool RequestToolStop()
    {
        if (!CanRequestToolStop())
            return false;

        _toolStopRequested = true;
        _toolCts!.Cancel();
        _ = ReportToolPipelineStopAsync(_toolTask!, actor);
        return true;
    }

    public bool AcceptToolStop(ToolPipelineStoppedForRestart message)
        => _toolStopRequested && ReferenceEquals(message.WorkTask, _toolTask);

    public void ClearToolTracking()
    {
        _toolTask = null;
        _toolStopRequested = false;
    }

    public void ReleaseTool() => CancelAndDisposeTool();

    public void ReleaseAll()
    {
        CancelAndDisposeModel();
        CancelAndDisposeTool();
    }

    public long StartCompactionWatchdog(TimeSpan timeout)
    {
        _watchdog.Start(ProcessingWatchdog.Compaction, timeout, timers);
        return _watchdog.CurrentOperationId;
    }

    public void StopWatchdog() => _watchdog.Stop(timers);

    private void CancelAndDisposeModel()
    {
        _modelCts?.Cancel();
        _modelCts?.Dispose();
        _modelCts = null;
    }

    private void CancelAndDisposeTool()
    {
        _toolCts?.Cancel();
        _toolCts?.Dispose();
        _toolCts = null;
    }

    private static async Task ReportModelStopAsync(Task task, IActorRef recipient, long callId)
    {
        try
        {
            await task.ConfigureAwait(false);
            recipient.Tell(new ModelStoppedForRestart(callId, null));
        }
        catch (Exception ex)
        {
            recipient.Tell(new ModelStoppedForRestart(callId, ex));
        }
    }

    private static async Task ReportToolPipelineStopAsync(Task task, IActorRef recipient)
    {
        try
        {
            await task.ConfigureAwait(false);
            recipient.Tell(new ToolPipelineStoppedForRestart(task, null));
        }
        catch (OperationCanceledException)
        {
            recipient.Tell(new ToolPipelineStoppedForRestart(task, null));
        }
        catch (Exception ex)
        {
            recipient.Tell(new ToolPipelineStoppedForRestart(task, ex));
        }
    }
}
