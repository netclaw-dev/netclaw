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
    // A reminder keeps at most one waiting fire in a session. The newer fire is acknowledged and not stored.
    internal sealed record Dropped(ReminderFireKey Fire, ReminderFireKey Waiting) : AdmissionDecision;
    internal sealed record Persist(InputAdmitted Event) : AdmissionDecision;
    // While the child consent pause applies, the newer fire replaces the waiting fire. The owner closes the waiting
    // fire with InputClosed and then stores the newer fire, so one turn of the reminder runs after the pause.
    internal sealed record ReplaceWaitingFire(InputAdmitted Event, InputId Waiting) : AdmissionDecision;
}

// AdoptsAuthority: the batch starts a turn for its own requester. The flow then holds the authority of the first
// input in the batch, and the actor must derive trust for it before the next model call.
// JoinsAutomationTurn: automated input joins a running turn. Either the requester of the turn is automation (a
// reminder, a background job, a webhook), whose later inputs have the same fixed system sender, or every input of
// the batch is a child result notice, which is automated input by its kind and brings no new person input.
internal sealed record BufferedInputBatch(
    IReadOnlyList<SendUserMessage> Messages,
    bool StartsNewTurn,
    bool AdoptsAuthority,
    bool JoinsAutomationTurn);

// NoticeRunIds: the child runs that the notices of the running turn name, in input order. They stay for the whole
// turn, also after a tool batch closes the selected inputs, and end when the turn is recorded.
internal readonly record struct SelectedInputFacts(
    IReadOnlyList<InputId> Ids,
    ReminderId? ReminderId,
    BackgroundJobId? BackgroundJobId,
    IReadOnlyList<SubAgentRunId> NoticeRunIds);

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
    private readonly List<SubAgentRunId> _noticeRunIds = [];
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
        new(_selectedIds.ToArray(), _source?.ReminderId, _source?.BackgroundJobId, _noticeRunIds.ToArray());

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
        long admittedAtMs,
        bool childConsentPaused)
    {
        if (command.AdmittedInputId is { } admittedId)
            return state.IsLegacyInputQuarantined(admittedId)
                ? new AdmissionDecision.Reject("The legacy input is quarantined and cannot execute.")
                : new AdmissionDecision.Existing();

        if (command.Notice is { } notice)
            return PrepareNoticeAdmission(command, notice, state, sessionId, admittedAtMs);

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

        // While the child consent pause applies (also with a parent prompt open), the newer fire replaces the waiting
        // fire. Elsewhere the drop rule below keeps the waiting fire.
        if (childConsentPaused && TryFindWaitingFire(evt, state, out _, out var held))
            return new AdmissionDecision.ReplaceWaitingFire(evt, WaitingInputOf(held, state));

        if (TryFindWaitingFire(evt, state, out var fire, out var waiting))
            return new AdmissionDecision.Dropped(fire, waiting);

        return new AdmissionDecision.Persist(evt);
    }

    // Drop rule: a fire waits only while this flow's queue holds it. Recovery rebuilds that queue for approval waits.
    // A journal row that nothing will run (a crash, a restart with two requesters, an expired restart reminder) is not
    // waiting, so it must not block a later fire. An input that a turn selected has left the queue. A newer fire of
    // its reminder gets its own turn. An input whose key is not a fire key has an unknown owner, so it is never
    // dropped and never blocks.
    private bool TryFindWaitingFire(
        InputAdmitted incoming,
        SessionState state,
        out ReminderFireKey fire,
        out ReminderFireKey waiting)
    {
        waiting = default;
        if (!TryReadFireKey(incoming, out fire))
            return false;

        foreach (var pending in state.PendingInputs)
        {
            if (IsBuffered(pending.InputId)
                && TryReadFireKey(pending, out waiting)
                && waiting.DefinitionId == fire.DefinitionId)
                return true;
        }

        return false;
    }

    private InputId WaitingInputOf(ReminderFireKey waiting, SessionState state)
        => state.PendingInputs.First(pending => IsBuffered(pending.InputId)
                                                && TryReadFireKey(pending, out var key) && key == waiting).InputId;

    /// <summary>The reminder fires that wait in the queue, in admission order.</summary>
    public IReadOnlyList<ReminderFireKey> WaitingFires(SessionState state)
        => state.PendingInputs
            .Where(pending => IsBuffered(pending.InputId))
            .Select(pending => TryReadFireKey(pending, out var key) ? key : (ReminderFireKey?)null)
            .OfType<ReminderFireKey>()
            .ToArray();

    private static bool TryReadFireKey(InputAdmitted input, out ReminderFireKey key)
    {
        key = default;
        return input.TurnContext.SourceKind == ReminderExecutionActor.ReminderSourceKind
               && ReminderFireKey.TryParse(input.SourceMessageId, out key);
    }

    // The owner built the notice from the state of a run. The reducer validates it against that state before the write,
    // so a second notice for a run, or a notice with another authority, fails here and never reaches the journal.
    private static AdmissionDecision PrepareNoticeAdmission(
        SendUserMessage command,
        ChildNoticeInput notice,
        SessionState state,
        SessionId sessionId,
        long admittedAtMs)
    {
        var evt = new InputAdmitted
        {
            SessionId = sessionId,
            InputId = InputId.New(),
            UserMessage = new SerializableChatMessage { Role = ChatRole.User, Content = command.Content ?? string.Empty },
            TurnContext = notice.Context,
            ChildNotice = notice.Notice,
            AdmittedAtMs = admittedAtMs
        };
        if (!TurnContext.TryFromRecord(evt.TurnContext, out _, out var reason))
            return new AdmissionDecision.Reject(reason ?? "invalid stored authority");
        try
        {
            _ = state.Apply(evt);
        }
        catch (InvalidOperationException error)
        {
            return new AdmissionDecision.Reject(error.Message);
        }

        return new AdmissionDecision.Persist(evt);
    }

    /// <summary>
    /// Builds an approval notice input. The notice is automation that the runtime made: the principal
    /// VerifiedAutomation, the sender and the source kind <c>child-consent</c>, and the authenticity LocalProcess. It
    /// takes the session, audience, boundary, channel type, and delivery targets of the run owner, so the reply goes
    /// to the thread of the owner. It never takes the requester of the owner, and it cannot open a prompt.
    /// </summary>
    public static InputAdmitted PrepareConsentNotice(
        SessionId sessionId,
        TurnContextRecord owner,
        string text,
        long admittedAtMs)
    {
        var context = new TurnContextRecord
        {
            SessionId = sessionId,
            TurnId = IdGen.ShortId(),
            Audience = owner.Audience,
            Boundary = owner.Boundary,
            ChannelType = owner.ChannelType,
            RequesterSenderId = new SenderId(ChildConsentNotice.Kind),
            RequesterPrincipal = PrincipalClassification.VerifiedAutomation,
            TransportAuthenticity = TransportAuthenticity.LocalProcess,
            PayloadTaint = PayloadTaint.Trusted,
            SourceKind = ChildConsentNotice.Kind,
            DefaultDeliveryTarget = owner.DefaultDeliveryTarget,
            RequestedDeliveryTarget = owner.RequestedDeliveryTarget,
            SupportsInteractiveApproval = false
        };
        if (owner.SessionId != sessionId)
            throw new InvalidOperationException($"The run owner of the notice belongs to session {owner.SessionId.Value}.");
        if (!TurnContext.TryFromRecord(context, out var notice, out var reason) || notice is not { IsChildConsentNotice: true })
            throw new InvalidOperationException($"The approval notice context is invalid: {reason ?? "not a notice"}");

        return new InputAdmitted
        {
            SessionId = sessionId,
            InputId = InputId.New(),
            UserMessage = new SerializableChatMessage { Role = ChatRole.User, Content = text },
            TurnContext = context,
            AdmittedAtMs = admittedAtMs
        };
    }

    // At most one approval notice waits. A newer notice replaces it (design D4: the text is true for the session).
    public InputId? WaitingConsentNotice()
        => _buffer.Where(static queued => !queued.IsReplay && BufferedAuthority(queued.Message).IsChildConsentNotice)
            .Select(static queued => queued.Message.AdmittedInputId)
            .FirstOrDefault();

    // The noticed runs belong to the open turn. A close of a waiting input (a replaced reminder fire, or an approval
    // notice that a newer notice or a recovery closed) does not end that turn, so it keeps them. A reminder turn and
    // a notice turn never hold noticed runs, so the rule also keeps nothing wrong when such a close ends a turn. A
    // close that names no pending input (a legacy row) clears them, as before. Live code and replay use this rule.
    public static bool ClosesNoticedTurn(InputClosed closed, SessionState before)
    {
        var inputs = before.PendingInputs.Where(input => closed.InputIds.Contains(input.InputId)).ToArray();
        return inputs.Length == 0 || !inputs.All(static input => TryReadFireKey(input, out _)
                                                                 || (TurnContext.TryFromRecord(input.TurnContext, out var context, out _)
                                                                     && context is { IsChildConsentNotice: true }));
    }

    // An approval notice goes in front of the other waiting input, so the model reads the state of the pause before
    // a held input runs. A replay of the running turn stays first.
    public void ConsentNoticeCommitted(InputAdmitted admitted)
    {
        var authority = RecordedAuthority(admitted);
        var position = _buffer.FindIndex(static queued => !queued.IsReplay);
        _buffer.Insert(position < 0 ? _buffer.Count : position, (new SendUserMessage
        {
            SessionId = admitted.SessionId,
            Content = admitted.UserMessage.Content ?? string.Empty,
            Source = RecordedSource(admitted, authority),
            AdmittedInputId = admitted.InputId,
            AdmittedAuthority = authority
        }, false));
    }

    public SendUserMessage AdmissionCommitted(
        InputAdmitted admitted,
        SendUserMessage command,
        InputPlacement placement)
    {
        // The journal callback applies the event before this owner reserves or places the input.
        var authority = RecordedAuthority(admitted);
        _inFlightDedup.ReserveBackgroundJob(command.Source?.BackgroundJobId);
        var accepted = command with
        {
            AdmittedInputId = admitted.InputId,
            AdmittedAuthority = authority,
            Source = command.Source ?? (admitted.ChildNotice is null ? null : RecordedSource(admitted, authority))
        };
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
            _inFlightDedup.ReserveBackgroundJob(source?.BackgroundJobId);
            _buffer.Add((new SendUserMessage
            {
                SessionId = sessionId,
                Content = admitted.UserMessage.Content ?? string.Empty,
                MediaReferences = admitted.UserMessage.MediaReferences,
                Source = source,
                AdmittedInputId = admitted.InputId,
                AdmittedAuthority = authority,
                // The journal keeps the kind. A rebuilt notice stays automated input.
                Notice = admitted.ChildNotice is { } notice ? new ChildNoticeInput(notice, admitted.TurnContext) : null
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
        _noticeRunIds.Clear();
        AddNoticeRun(command.Notice?.Notice);
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
        _noticeRunIds.Clear();
        RestoreTurnAuthority(context, deriver);
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
                AdmittedAuthority = RecordedAuthority(item),
                Notice = item.ChildNotice is { } notice ? new ChildNoticeInput(notice, item.TurnContext) : null
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
        foreach (var item in pending)
            AddNoticeRun(item.ChildNotice);
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
    private BufferedInputBatch? DrainBufferedBatch(bool continuesTurn, bool childConsentPaused)
    {
        if (_buffer.Count == 0)
            return null;

        if (!continuesTurn)
            MoveHeldInputBehind(childConsentPaused);
        var adoptsAuthority = !continuesTurn && !_buffer[0].IsReplay;
        var owner = adoptsAuthority ? BufferedAuthority(_buffer[0].Message) : _context;
        var count = 0;
        while (count < _buffer.Count
               && PassesPause(_buffer[count], owner, startsTurn: adoptsAuthority && count == 0, childConsentPaused)
               && JoinsTurn(_buffer[count], owner))
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
        foreach (var message in messages)
            AddNoticeRun(message.Notice?.Notice);
        var joinsAutomationTurn = !adoptsAuthority
            && ((owner is { } running && SessionToolWorkflow.IsAutomation(running.RequesterPrincipal))
                || messages.All(static message => message.Notice is not null));
        return new BufferedInputBatch(messages, startsNewTurn, adoptsAuthority, joinsAutomationTurn);
    }

    private static bool JoinsTurn((SendUserMessage Message, bool IsReplay) input, TurnContext? owner)
        => input.IsReplay || (owner is not null && BufferedAuthority(input.Message).HasSameRequester(owner));

    // <child-consent-drain>
    // Security boundary (child consent pause, design D5). While the pause applies, automated input and child result
    // notices are held: a held input never joins a running turn and never starts a turn. A person input is never held.
    // An approval notice is never held, never joins a turn, and nothing joins a notice turn.
    private static bool PassesPause(
        (SendUserMessage Message, bool IsReplay) input, TurnContext? owner, bool startsTurn, bool childConsentPaused)
    {
        if (input.IsReplay)
            return true;
        if (childConsentPaused && IsHeldByPause(input.Message))
            return false;
        return startsTurn
               || (owner?.IsChildConsentNotice != true && !BufferedAuthority(input.Message).IsChildConsentNotice);
    }

    private static bool IsHeldByPause(SendUserMessage message)
        => message.Notice is not null
           || (SessionToolWorkflow.IsAutomation(BufferedAuthority(message).RequesterPrincipal)
               && !BufferedAuthority(message).IsChildConsentNotice);

    // A new turn starts at the first input that the pause does not hold. The held inputs keep their order behind the
    // other inputs and run after the last close.
    private void MoveHeldInputBehind(bool childConsentPaused)
    {
        if (!childConsentPaused)
            return;
        var held = _buffer.Where(static queued => !queued.IsReplay && IsHeldByPause(queued.Message)).ToArray();
        _buffer.RemoveAll(static queued => !queued.IsReplay && IsHeldByPause(queued.Message));
        _buffer.AddRange(held);
    }
    // </child-consent-drain>

    private void AdoptAuthority(SendUserMessage first, TurnContext authority)
    {
        _source = first.Source;
        _noticeRunIds.Clear();
        _context = authority;
        // The old trust belongs to another authority. Tool exposure fails closed until DeriveAdoptedTrust runs.
        _trust = null;
        _telemetry = new TurnTelemetryFacts(authority.TurnId, first.Source?.MessageId, first.Source?.ChannelType);
    }

    private void AddNoticeRun(ChildNotice? notice)
    {
        if (notice is not null)
            AddNoticeRun(notice.RunId);
    }

    private void AddNoticeRun(SubAgentRunId runId)
    {
        if (!_noticeRunIds.Contains(runId))
            _noticeRunIds.Add(runId);
    }

    // Replay rebuilds the noticed runs of the open journal turn from the inputs that its tool batches consumed. An
    // approval re-drive on a new actor instance then keeps the reminder link of the noticed run, as the live actor does.
    public void RestoreNoticeRuns(IEnumerable<SubAgentRunId> runIds)
    {
        foreach (var runId in runIds)
            AddNoticeRun(runId);
    }

    public void ClearNoticeRuns() => _noticeRunIds.Clear();

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

    public AfterTurnResult AdvanceAfterTurn(bool restartDrainRequested, bool childConsentPaused)
    {
        if (restartDrainRequested)
            return new AfterTurnResult(InputProgressAction.Restart, null);

        var batch = DrainBufferedBatch(continuesTurn: false, childConsentPaused);
        return batch is null
            ? new AfterTurnResult(InputProgressAction.Ready, null)
            : new AfterTurnResult(InputProgressAction.FollowUp, batch);
    }

    public ToolBatchInputAdvance AdvanceAfterToolBatch(
        BatchCompletion.Ready completion,
        bool compactionDue,
        bool childConsentPaused)
    {
        var batch = DrainBufferedBatch(continuesTurn: true, childConsentPaused);
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
        _inFlightDedup.CompleteBackgroundJob(evt.SourceBackgroundJobId);
        _selectedIds.Clear();
        // A delivery retry repeats the recorded turn with no input, so it takes the link from the pending retry.
        _noticeRunIds.Clear();
    }

    // A closed input never runs: a turn closed it, or a newer fire of its reminder replaced it in the queue.
    public void InputsClosed(InputClosed evt)
    {
        _selectedIds.RemoveAll(evt.InputIds.Contains);
        _buffer.RemoveAll(item => item.Message.AdmittedInputId is { } id && evt.InputIds.Contains(id));
    }

    public void FailedTurn()
    {
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

    public CompactionContinuation AdvanceAfterCompaction(bool restartDrainRequested, bool childConsentPaused)
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
        var batch = DrainBufferedBatch(continuesTurn: resume, childConsentPaused);
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
