// -----------------------------------------------------------------------
// <copyright file="SessionInputFlow.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Actors.Channels;
using Netclaw.Actors.Jobs;
using Netclaw.Actors.Protocol;
using Netclaw.Actors.Reminders;
using Netclaw.Configuration;
using Netclaw.Tools;
using static Netclaw.Actors.Sessions.SessionProtocol;

namespace Netclaw.Actors.Sessions.Handlers;

internal enum InputPlacement { Activate, BufferDuringModel, BufferDuringCompaction }

internal abstract record AdmissionDecision
{
    internal sealed record Existing : AdmissionDecision;
    internal sealed record Reject(string Reason) : AdmissionDecision;
    internal sealed record Duplicate : AdmissionDecision;
    internal sealed record Persist(InputAdmitted Event) : AdmissionDecision;
}

internal sealed record BufferedInputBatch(
    IReadOnlyList<SendUserMessage> Messages,
    bool StartsNewTurn);

internal readonly record struct SelectedInputFacts(
    IReadOnlyList<InputId> Ids,
    ReminderId? ReminderId,
    BackgroundJobId? BackgroundJobId);

internal readonly record struct TurnAuthorityFacts(
    TurnContext? Context,
    MessageSource? Source,
    EffectiveTrustContext? Trust);

internal readonly record struct TurnTelemetryFacts(
    TurnId? TurnId,
    string? MessageId,
    ChannelType? ChannelType);

internal enum InputProgressAction { Restart, FollowUp, Ready }
internal readonly record struct AfterTurnResult(InputProgressAction Action, BufferedInputBatch? Batch);

internal readonly record struct CompactionContinuation(
    InputProgressAction Action,
    bool ResumeToolLoop,
    BufferedInputBatch? Batch,
    IWithSessionId? DeferredApproval);

internal readonly record struct ToolBatchInputAdvance(
    BufferedInputBatch? Batch,
    ToolBatchNextAction Next);

/// <summary>
/// Owns transient input order, selected IDs, source reservations, and turn authority.
/// The session journal remains the only durable input owner.
/// </summary>
internal sealed class SessionInputFlow
{
    private readonly List<(SendUserMessage Message, bool IsReplay)> _buffer = [];
    private readonly List<InputId> _selectedIds = [];
    private readonly InFlightTurnDedup _inFlightDedup = new();
    private MessageSource? _source;
    private TurnContext? _context;
    private EffectiveTrustContext? _trust;
    private TurnTelemetryFacts _telemetry;
    private bool _emittedText;
    private bool _resumeToolLoopAfterCompaction;
    private IWithSessionId? _deferredApproval;
    private int _compactionOverflowRetryCount;

    public int SelectedCount => _selectedIds.Count;
    public bool EmittedText => _emittedText;
    public bool HasBufferedContinuation => _buffer.Count > 0 || _deferredApproval is not null;
    public TurnAuthorityFacts Authority => new(_context, _source, _trust);
    public TurnTelemetryFacts Telemetry => _telemetry;
    public SelectedInputFacts SelectedInputs =>
        new(_selectedIds.ToArray(), _source?.ReminderId, _source?.BackgroundJobId);

    public bool IsReminderDuplicate(ReminderId? reminderId, SessionState state)
    {
        if (reminderId is not { } id || string.IsNullOrEmpty(id.Value))
            return false;

        return state.ProcessedReminderIds.Contains(id)
               || _inFlightDedup.IsReminderInFlight(id)
               || _buffer.Any(item => item.Message.Source?.ReminderId == id);
    }

    public bool IsBackgroundJobDuplicate(BackgroundJobId? backgroundJobId, SessionState state)
    {
        if (backgroundJobId is not { } id || string.IsNullOrEmpty(id.Value))
            return false;

        return state.ProcessedBackgroundJobIds.Contains(id)
               || _inFlightDedup.IsBackgroundJobInFlight(id)
               || _buffer.Any(item => item.Message.Source?.BackgroundJobId == id);
    }

    public AdmissionDecision PrepareAdmission(
        SendUserMessage command,
        SessionState state,
        SessionId sessionId,
        long admittedAtMs)
    {
        if (command.AdmittedInputId is not null)
            return new AdmissionDecision.Existing();

        var context = TurnContext.FromMessageSource(
            sessionId,
            command.Source?.TurnId ?? new TurnId(IdGen.ShortId()),
            command.Source);
        var evt = new InputAdmitted
        {
            SessionId = sessionId,
            InputId = InputId.New(),
            SourceMessageId = command.Source?.MessageId
                ?? command.Source?.ReminderId?.Value
                ?? command.Source?.BackgroundJobId?.Value,
            UserMessage = new SerializableChatMessage
            {
                Role = ChatRole.User,
                Content = command.Content ?? string.Empty,
                MediaReferences = command.MediaReferences.ToArray()
            },
            ExecutableText = command.Source?.ExecutableText,
            TurnContext = context.ToRecord(),
            AdmittedAtMs = admittedAtMs
        };

        if (!TurnContext.TryFromRecord(evt.TurnContext, out _, out var reason))
            return new AdmissionDecision.Reject(reason ?? "invalid stored authority");

        var sourceKey = SessionState.SourceMessageKey(evt);
        if (sourceKey is not null && state.RecentSourceMessageKeys.Contains(sourceKey))
            return new AdmissionDecision.Duplicate();

        return new AdmissionDecision.Persist(evt);
    }

    public SendUserMessage AdmissionCommitted(
        InputAdmitted admitted,
        SendUserMessage command,
        InputPlacement placement)
    {
        _inFlightDedup.ReserveReminder(command.Source?.ReminderId);
        _inFlightDedup.ReserveBackgroundJob(command.Source?.BackgroundJobId);
        var accepted = command with { AdmittedInputId = admitted.InputId };
        PlaceAccepted(accepted, placement);
        return accepted;
    }

    public void AdmissionAlreadyCommitted(SendUserMessage command, InputPlacement placement)
        => PlaceAccepted(command, placement);

    private void PlaceAccepted(SendUserMessage command, InputPlacement placement)
    {
        if (placement != InputPlacement.Activate)
            _buffer.Add((command, false));
    }

    public void ActivateInput(
        SendUserMessage command,
        IReadOnlyList<InputAdmitted> pending,
        TrustContextDeriver? deriver)
    {
        var id = command.AdmittedInputId
            ?? throw new InvalidOperationException("The accepted input has no admission ID.");
        var admitted = pending.FirstOrDefault(input => input.InputId == id)
            ?? throw new InvalidOperationException($"Journal input {id} is missing before turn activation.");
        if (!TurnContext.TryFromRecord(admitted.TurnContext, out var context, out var reason)
            || context is null)
            throw new InvalidOperationException($"Journal input {id} has invalid authority: {reason}");

        _emittedText = false;
        _selectedIds.Clear();
        _selectedIds.Add(id);
        _source = command.Source;
        _context = context;
        _trust = deriver?.DeriveFromTurnContext(context);
        _telemetry = new TurnTelemetryFacts(
            context.TurnId,
            command.Source?.MessageId,
            command.Source?.ChannelType);
    }

    public void ActivateRecoveredInput(
        MessageSource? source,
        TurnContext context,
        TrustContextDeriver? deriver)
    {
        _emittedText = false;
        _selectedIds.Clear();
        _source = source;
        RestoreTurnAuthority(context, deriver);
        _inFlightDedup.ReserveReminder(source?.ReminderId);
    }

    public string? BeginSlashResume(InputAdmitted first)
    {
        if (first.ExecutableText is not { } executableText
            || string.IsNullOrWhiteSpace(executableText)
            || executableText.TrimStart()[0] != '/')
            return null;

        _selectedIds.Add(first.InputId);
        return executableText;
    }

    public bool CompleteSlashResume(
        bool handled,
        IReadOnlyList<InputAdmitted> pending,
        SessionId sessionId,
        SessionPhase phase)
    {
        if (!handled)
        {
            _selectedIds.Clear();
            return false;
        }

        if (phase != SessionPhase.Processing)
            return true;

        foreach (var item in pending.Skip(1))
        {
            _buffer.Add((new SendUserMessage
            {
                SessionId = sessionId,
                Content = item.UserMessage.Content ?? string.Empty,
                MediaReferences = item.UserMessage.MediaReferences,
                AdmittedInputId = item.InputId
            }, false));
        }

        return true;
    }

    public IReadOnlyList<SerializableChatMessage> ResumePendingInputs(IReadOnlyList<InputAdmitted> pending)
    {
        _selectedIds.AddRange(pending.Select(static item => item.InputId));
        return pending.Select(static item => item.UserMessage).ToArray();
    }

    public void BufferReplay(SendUserMessage command) => _buffer.Insert(0, (command, true));

    private BufferedInputBatch? DrainBufferedBatch()
    {
        if (_buffer.Count == 0)
            return null;

        var startsNewTurn = _buffer.Any(static item => !item.IsReplay);
        if (startsNewTurn)
            _emittedText = false;

        var messages = _buffer.Select(static item => item.Message).ToArray();
        foreach (var message in messages)
        {
            if (message.AdmittedInputId is { } id && !_selectedIds.Contains(id))
                _selectedIds.Add(id);
        }

        _buffer.Clear();
        return new BufferedInputBatch(messages, startsNewTurn);
    }

    public AfterTurnResult AdvanceAfterTurn(bool restartDrainRequested)
    {
        if (restartDrainRequested)
            return new AfterTurnResult(InputProgressAction.Restart, null);

        var batch = DrainBufferedBatch();
        return batch is null
            ? new AfterTurnResult(InputProgressAction.Ready, null)
            : new AfterTurnResult(InputProgressAction.FollowUp, batch);
    }

    public ToolBatchInputAdvance AdvanceAfterToolBatch(
        BatchCompletion.Ready completion,
        bool compactionDue)
    {
        var batch = DrainBufferedBatch();
        var next = completion.ChooseNext(batch?.StartsNewTurn == true, compactionDue);
        return new ToolBatchInputAdvance(batch, next);
    }

    public void MarkTextEmitted() => _emittedText = true;
    public void ToolBatchCommitted() { _selectedIds.Clear(); _emittedText = false; }

    public void TurnCommitted(TurnRecorded evt)
    {
        ReleaseRecordedSources(evt);
        _emittedText = false;
    }

    public void RoutedTurnCommitted(TurnRecorded evt) => ReleaseRecordedSources(evt);

    private void ReleaseRecordedSources(TurnRecorded evt)
    {
        _inFlightDedup.CompleteReminder(evt.SourceReminderId);
        _inFlightDedup.CompleteBackgroundJob(evt.SourceBackgroundJobId);
        _selectedIds.Clear();
    }

    public void InputsClosed(InputClosed evt) => _selectedIds.RemoveAll(evt.InputIds.Contains);

    public void FailedTurn()
    {
        _inFlightDedup.CompleteReminder(_source?.ReminderId);
        _inFlightDedup.CompleteBackgroundJob(_source?.BackgroundJobId);
        _context = null;
    }

    public void SetTurnContext(TurnContext context) => _context = context;
    public void ClearTurnContext() => _context = null;

    public void RestoreTurnAuthority(TurnContext context, TrustContextDeriver? deriver)
    {
        _context = context;
        _trust = deriver?.DeriveFromTurnContext(context);
        _telemetry = new TurnTelemetryFacts(context.TurnId, null, context.ChannelType);
    }

    public bool TryStartOverflowRecovery()
    {
        if (_compactionOverflowRetryCount > 0)
        {
            _compactionOverflowRetryCount = 0;
            return false;
        }

        _compactionOverflowRetryCount++;
        return true;
    }

    public void ResetOverflowRetry() => _compactionOverflowRetryCount = 0;

    public void DeferApproval(IWithSessionId response) => _deferredApproval = response;
    public void ResumeToolLoopAfterCompaction() => _resumeToolLoopAfterCompaction = true;

    public CompactionContinuation AdvanceAfterCompaction(bool restartDrainRequested)
    {
        if (restartDrainRequested)
        {
            _deferredApproval = null;
            _resumeToolLoopAfterCompaction = false;
            return new CompactionContinuation(InputProgressAction.Restart, false, null, null);
        }

        var resume = _resumeToolLoopAfterCompaction;
        _resumeToolLoopAfterCompaction = false;
        var batch = DrainBufferedBatch();
        var deferred = _deferredApproval;
        _deferredApproval = null;
        var action = resume || batch is not null
            ? InputProgressAction.FollowUp
            : InputProgressAction.Ready;
        return new CompactionContinuation(action, resume, batch, deferred);
    }

    public int ClearBufferedForRestart()
    {
        var count = _buffer.Count;
        _buffer.Clear();
        return count;
    }

    public IReadOnlyList<SendUserMessage> PrepareActorRestartReplay()
    {
        var messages = _buffer.Select(static item => item.Message).ToArray();
        _buffer.Clear();
        return messages;
    }
}
