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

internal enum InputPlacement { Activate, BufferDuringModel, BufferDuringCompaction, BufferDuringApproval }

internal abstract record AdmissionDecision
{
    // This command already has an admission ID and can continue without another journal write.
    internal sealed record Existing : AdmissionDecision;
    internal sealed record Reject(string Reason) : AdmissionDecision;
    // The durable source key already exists, so the actor acknowledges without execution.
    internal sealed record Duplicate : AdmissionDecision;
    internal sealed record Persist(InputAdmitted Event) : AdmissionDecision;
}

// AdoptsAuthority: the batch starts a turn for its own requester. The flow then holds the authority of the first
// input in the batch, and the actor must derive trust for it before the next model call.
internal sealed record BufferedInputBatch(
    IReadOnlyList<SendUserMessage> Messages,
    bool StartsNewTurn,
    bool AdoptsAuthority);

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
/// Only the session actor uses this transient input order, selection, and authority state.
/// The session journal remains the only durable input owner.
/// </summary>
internal sealed class SessionInputFlow
{
    private readonly List<(SendUserMessage Message, bool IsReplay)> _buffer = [];
    private readonly List<InputId> _selectedIds = [];
    private readonly InFlightTurnDedup _inFlightDedup = new();
    // The executable context can clear while source attribution and telemetry remain for late diagnostics.
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
    // Input closure events copy IDs before journal callbacks clear or remove the selection.
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
        if (command.AdmittedInputId is { } admittedId)
            return state.IsLegacyInputQuarantined(admittedId)
                ? new AdmissionDecision.Reject("The legacy input is quarantined and cannot execute.")
                : new AdmissionDecision.Existing();

        var context = TurnContext.FromMessageSource(
            sessionId,
            command.Source?.TurnId ?? new TurnId(IdGen.ShortId()),
            command.Source);
        var evt = new InputAdmitted
        {
            SessionId = sessionId,
            InputId = InputId.New(),
            SourceBackgroundJobId = command.Source?.BackgroundJobId,
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
            // Akka's serializer cannot restore ToRecord's interface-backed collection. Persist a concrete array.
            TurnContext = context.ToRecord() with
            {
                AdoptedSpeakerIds = context.AdoptedSpeakerIds.ToArray()
            },
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
        // The journal callback applies the event before this owner reserves or places the input.
        var authority = RecordedAuthority(admitted);
        _inFlightDedup.ReserveReminder(command.Source?.ReminderId);
        _inFlightDedup.ReserveBackgroundJob(command.Source?.BackgroundJobId);
        var accepted = command with { AdmittedInputId = admitted.InputId, AdmittedAuthority = authority };
        PlaceAccepted(accepted, placement);
        return accepted;
    }

    // Actor restart can requeue an admitted command. Keep its ID and recorded authority without another journal write.
    public void AdmissionAlreadyCommitted(SendUserMessage command, InputPlacement placement)
        => PlaceAccepted(command, placement);

    private void PlaceAccepted(SendUserMessage command, InputPlacement placement)
    {
        // A recovery can rebuild an input that an actor restart also requeues. Keep one copy.
        if (placement != InputPlacement.Activate && !IsBuffered(command.AdmittedInputId))
            _buffer.Add((command, false));
    }

    private bool IsBuffered(InputId? id)
        => id is { } inputId && _buffer.Any(item => item.Message.AdmittedInputId == inputId);

    /// <summary>
    /// Rebuilds the waiting queue from the journal after recovery. The session acknowledged each pending input, and the
    /// journal is the only durable copy, so a restart must not drop one. The caller passes only inputs that wait for an
    /// open approval. Each input keeps its recorded authority. The transport source is rebuilt from the record, so a
    /// reminder or a background job keeps its id (the record's <c>SourceMessageId</c>).
    /// </summary>
    public int RestoreWaitingInputs(IReadOnlyList<InputAdmitted> pending, SessionId sessionId)
    {
        var restored = 0;
        foreach (var admitted in pending)
        {
            if (IsBuffered(admitted.InputId))
                continue;

            var authority = RecordedAuthority(admitted);
            var source = RecordedSource(admitted, authority);
            _inFlightDedup.ReserveReminder(source?.ReminderId);
            _inFlightDedup.ReserveBackgroundJob(source?.BackgroundJobId);
            _buffer.Add((new SendUserMessage
            {
                SessionId = sessionId,
                Content = admitted.UserMessage.Content ?? string.Empty,
                MediaReferences = admitted.UserMessage.MediaReferences,
                Source = source,
                AdmittedInputId = admitted.InputId,
                AdmittedAuthority = authority
            }, false));
            restored++;
        }

        return restored;
    }

    // The journal keeps the authority and the source message id, not the transport source. A source needs a channel
    // and a sender; a record without them had no source, so it gets none.
    private static MessageSource? RecordedSource(InputAdmitted admitted, TurnContext authority)
    {
        if (authority.ChannelType is not { } channelType || authority.RequesterSenderId is not { } senderId)
            return null;

        var sourceKind = authority.Provenance.SourceKind?.Value;
        return new MessageSource
        {
            ChannelType = channelType,
            SenderId = senderId,
            MessageId = admitted.SourceMessageId,
            TurnId = authority.TurnId,
            Audience = authority.Audience,
            Boundary = authority.Boundary,
            Principal = authority.RequesterPrincipal,
            Provenance = authority.Provenance,
            DefaultDeliveryTarget = authority.DefaultDeliveryTarget,
            RequestedDeliveryTarget = authority.RequestedDeliveryTarget,
            HasThirdPartyAdoptedContext = authority.HasThirdPartyAdoptedContext,
            AdoptedSpeakerIds = authority.AdoptedSpeakerIds,
            ExecutableText = admitted.ExecutableText,
            ReminderId = sourceKind == ReminderExecutionActor.ReminderSourceKind && admitted.SourceMessageId is { } reminderKey
                ? new ReminderId(reminderKey)
                : null,
            BackgroundJobId = sourceKind == BackgroundJobManagerActor.SourceKind && admitted.SourceMessageId is { } jobKey
                ? new BackgroundJobId(jobKey)
                : null
        };
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
        var context = RecordedAuthority(admitted);

        // A recovery can rebuild this input into the queue while an actor restart also requeues it. The input runs
        // now, so the queued copy must not run again.
        _buffer.RemoveAll(item => item.Message.AdmittedInputId == id);

        // Use recorded identity and authority so live execution matches journal recovery.
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
                AdmittedInputId = item.InputId,
                AdmittedAuthority = RecordedAuthority(item)
            }, false));
        }

        return true;
    }

    public IReadOnlyList<SerializableChatMessage> ResumePendingInputs(IReadOnlyList<InputAdmitted> pending)
    {
        // The resumed turn consumes every pending input, including any input that recovery rebuilt into the queue.
        var resumed = pending.Select(static item => item.InputId).ToHashSet();
        _buffer.RemoveAll(item => item.Message.AdmittedInputId is { } id && resumed.Contains(id));
        _selectedIds.AddRange(pending.Select(static item => item.InputId));
        return pending.Select(static item => item.UserMessage).ToArray();
    }

    public void BufferReplay(SendUserMessage command) => _buffer.Insert(0, (command, true));

    // Security boundary: a turn has one authority, fixed when the turn starts. A drain takes only the leading
    // buffered inputs of one requester (TurnContext.HasSameRequester). An input from another requester waits for
    // its own turn, so a model call never serves one requester under the authority of another.
    //   continuesTurn: a tool batch or a compaction continues the running turn. Only inputs of the requester that
    //                  owns that turn may join it, and the turn keeps its authority.
    //   otherwise:     the first buffered input starts a new turn, and the flow adopts its recorded authority.
    // A replay is the current turn's own message after an overflow, so it always joins the current turn.
    private BufferedInputBatch? DrainBufferedBatch(bool continuesTurn)
    {
        if (_buffer.Count == 0)
            return null;

        var adoptsAuthority = !continuesTurn && !_buffer[0].IsReplay;
        var owner = adoptsAuthority ? BufferedAuthority(_buffer[0].Message) : _context;
        var count = 0;
        while (count < _buffer.Count && JoinsTurn(_buffer[count], owner))
            count++;
        if (count == 0)
            return null;

        var drained = _buffer.GetRange(0, count);
        _buffer.RemoveRange(0, count);

        // Fresh input resets the turn limits; overflow replay keeps the current limits.
        var startsNewTurn = drained.Any(static item => !item.IsReplay);
        if (startsNewTurn)
            _emittedText = false;

        var messages = drained.Select(static item => item.Message).ToArray();
        foreach (var message in messages)
        {
            if (message.AdmittedInputId is { } id && !_selectedIds.Contains(id))
                _selectedIds.Add(id);
        }

        if (adoptsAuthority)
            AdoptAuthority(messages[0], BufferedAuthority(messages[0]));
        return new BufferedInputBatch(messages, startsNewTurn, adoptsAuthority);
    }

    private static bool JoinsTurn((SendUserMessage Message, bool IsReplay) input, TurnContext? owner)
        => input.IsReplay || (owner is not null && BufferedAuthority(input.Message).HasSameRequester(owner));

    private void AdoptAuthority(SendUserMessage first, TurnContext authority)
    {
        _source = first.Source;
        _context = authority;
        // The old trust belongs to another authority. Tool exposure fails closed until DeriveAdoptedTrust runs.
        _trust = null;
        _telemetry = new TurnTelemetryFacts(authority.TurnId, first.Source?.MessageId, first.Source?.ChannelType);
    }

    public void DeriveAdoptedTrust(TrustContextDeriver? deriver)
        => _trust = _context is { } context ? deriver?.DeriveFromTurnContext(context) : null;

    private static TurnContext BufferedAuthority(SendUserMessage message)
        => message.AdmittedAuthority
           ?? throw new InvalidOperationException(
               $"Buffered input {message.AdmittedInputId?.ToString() ?? "(no admission ID)"} has no recorded authority.");

    private static TurnContext RecordedAuthority(InputAdmitted admitted)
        => TurnContext.TryFromRecord(admitted.TurnContext, out var context, out var reason) && context is not null
            ? context
            : throw new InvalidOperationException($"Journal input {admitted.InputId} has invalid authority: {reason}");

    public AfterTurnResult AdvanceAfterTurn(bool restartDrainRequested)
    {
        if (restartDrainRequested)
            return new AfterTurnResult(InputProgressAction.Restart, null);

        var batch = DrainBufferedBatch(continuesTurn: false);
        return batch is null
            ? new AfterTurnResult(InputProgressAction.Ready, null)
            : new AfterTurnResult(InputProgressAction.FollowUp, batch);
    }

    public ToolBatchInputAdvance AdvanceAfterToolBatch(
        BatchCompletion.Ready completion,
        bool compactionDue)
    {
        var batch = DrainBufferedBatch(continuesTurn: true);
        var next = completion.ChooseNext(compactionDue);
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
        // Recovery restores journal authority and telemetry without inventing a transport message ID.
        _context = context;
        _trust = deriver?.DeriveFromTurnContext(context);
        _telemetry = new TurnTelemetryFacts(context.TurnId, null, context.ChannelType);
    }

    public bool TryStartOverflowRecovery()
    {
        // One overflow can compact and replay this turn; a second overflow fails the turn.
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
        // Restart takes priority over a deferred approval or tool-loop continuation.
        if (restartDrainRequested)
        {
            _deferredApproval = null;
            _resumeToolLoopAfterCompaction = false;
            return new CompactionContinuation(InputProgressAction.Restart, false, null, null);
        }

        var resume = _resumeToolLoopAfterCompaction;
        _resumeToolLoopAfterCompaction = false;
        var batch = DrainBufferedBatch(continuesTurn: resume);
        // The actor replays this response only after it applies the phase transition.
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
