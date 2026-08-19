// -----------------------------------------------------------------------
// <copyright file="ChannelOutputEngine.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Akka.Actor;
using Netclaw.Actors.Channels;
using Netclaw.Actors.Protocol;
using Netclaw.Actors.Reminders;
using static Netclaw.Actors.Sessions.SessionProtocol;
using static Netclaw.Actors.Reminders.ReminderProtocol;

namespace Netclaw.Channels;

/// <summary>
/// Shared session-output handling for the channel binding actors. The engine
/// owns the per-turn delivery bookkeeping: the pending cursor, the
/// turn-in-flight signal, the reminder delivery observers, the empty-turn
/// fallback decision, and the pending-approval prompt list. Slack, Discord, and
/// Mattermost supply the transport effects; the algorithm has one
/// implementation here.
/// </summary>
/// <remarks>
/// The engine holds no Akka state and never calls Akka persistence. It returns
/// the <see cref="PendingApprovalPromptCleared"/> events a completed turn
/// produces, and the binding actor persists them. Each actor-owned effect
/// (post, upload, prompt redraw, journal write) goes through a callback that
/// the binding actor supplies at construction. All callbacks run on the actor's
/// own message-processing path, so they are safe to read and write actor state.
/// </remarks>
public sealed class ChannelOutputEngine<TRequest, TPromptId>
    where TRequest : PendingApprovalRequest<TPromptId>
    where TPromptId : struct
{
    private static readonly PendingApprovalPromptCleared[] NoClearedPrompts = [];

    private readonly ChannelType _channelType;
    private readonly string _channelName;
    private readonly IComparer<string> _cursorComparer;
    private readonly List<TRequest> _pendingRequests;
    private readonly Func<ToolInteractionRequest, TRequest> _createPendingRequest;
    private readonly Func<ToolInteractionRequest, bool> _isApprovalRequest;
    private readonly Func<TextOutput, string?> _renderTextOutput;
    private readonly Func<ErrorOutput, string> _renderErrorOutput;
    private readonly Func<string, Task<bool>> _postTextAsync;
    private readonly Func<FileOutput, Task<bool>> _uploadFileAsync;
    private readonly Func<ToolInteractionRequest, Task<TPromptId?>> _postApprovalPromptAsync;
    private readonly Func<TPromptId, string> _readPromptIdValue;
    private readonly Func<ToolInteractionRequest, Task> _onApprovalPromptFailedAsync;
    private readonly Action<PendingApprovalPromptTracked> _persistPromptTracked;
    private readonly Func<SessionOutput, Task> _handleChannelSpecificOutputAsync;
    private readonly Action<string> _advanceCursor;
    private readonly Func<Task> _postEmptyTurnFallbackAsync;
    private readonly Func<TurnCompleted, Task> _onEmptyTurnSuppressedAsync;
    private readonly Func<TurnCompleted, long?> _readObservedAtMs;

    // Reply targets for in-flight reminder delivery confirmations, keyed by
    // reminder delivery key. Captured from DeliverTrustedSessionTurn; each is
    // told a ReminderDeliveryResult on its turn's TurnCompleted and removed.
    // Keyed (not a single field) because multiple reminders can target the
    // same session concurrently — a single field would be clobbered.
    private readonly Dictionary<ReminderId, IActorRef> _reminderDeliveryObservers = new();

    private bool _deliveredThisTurn;

    // True when a content post/upload this turn was attempted but failed (the
    // model produced output, the transport rejected it). Distinct from "nothing
    // was produced" — it suppresses the empty-turn fallback so a failed post
    // isn't followed by a misleading "I didn't manage to produce a reply".
    private bool _postFailedThisTurn;

    /// <summary>
    /// Creates the engine. Every dependency is required. The pending-request
    /// list is the actor's own list, which the approval-response flow also
    /// reads, so the engine cannot run with it absent.
    /// </summary>
    /// <param name="channelType">Telemetry and reminder-result category for the channel.</param>
    /// <param name="channelName">Channel name for generated text, for example <c>Slack</c>.</param>
    /// <param name="cursorComparer">Orders two cursor keys of this channel.</param>
    /// <param name="pendingRequests">
    /// The actor's own pending-approval list. The engine appends the prompts it
    /// posts and clears the list when a turn completes.
    /// </param>
    /// <param name="createPendingRequest">Creates the channel's pending-approval record.</param>
    /// <param name="isApprovalRequest">
    /// Decides whether a <see cref="ToolInteractionRequest"/> is an approval
    /// prompt this binding renders. Discord and Mattermost require
    /// <c>Kind == "approval"</c>; Slack accepts every interaction request.
    /// </param>
    /// <param name="renderTextOutput">
    /// Formats a <see cref="TextOutput"/> for the transport. A <c>null</c>
    /// result means the output carries nothing to post, so the engine skips it
    /// and records neither a delivery nor a failure.
    /// </param>
    /// <param name="renderErrorOutput">Formats an <see cref="ErrorOutput"/> for the transport.</param>
    /// <param name="postTextAsync">Posts text; returns <c>true</c> when the whole post succeeded.</param>
    /// <param name="uploadFileAsync">Uploads a file; returns <c>true</c> on success.</param>
    /// <param name="postApprovalPromptAsync">
    /// Posts the approval prompt and returns its locator, or <c>null</c> when
    /// the prompt could not be posted.
    /// </param>
    /// <param name="readPromptIdValue">Reads the persisted string form of a prompt locator.</param>
    /// <param name="onApprovalPromptFailedAsync">
    /// Handles a prompt that could not be posted. Every channel routes an
    /// auto-deny so the blocked tool call unwinds instead of waiting forever.
    /// </param>
    /// <param name="persistPromptTracked">
    /// Journals <c>PendingApprovalPromptTracked</c> for a posted prompt. The
    /// actor owns the persistence call; the engine never touches Akka persistence.
    /// </param>
    /// <param name="handleChannelSpecificOutputAsync">
    /// Handles output types only some channels support, such as
    /// <see cref="SessionTitleOutput"/> and <see cref="ProcessingStateOutput"/>.
    /// A channel without the capability ignores the output here.
    /// </param>
    /// <param name="advanceCursor">Persists the cursor a completed turn confirmed.</param>
    /// <param name="postEmptyTurnFallbackAsync">Posts the channel's empty-turn fallback text.</param>
    /// <param name="onEmptyTurnSuppressedAsync">
    /// Runs instead of the fallback when the turn produced output that failed
    /// to post. Discord and Mattermost already told the session inline, so
    /// their hook does nothing more; Slack tells the session here.
    /// </param>
    /// <param name="readObservedAtMs">
    /// Reads the observation timestamp for the reminder delivery result.
    /// </param>
    public ChannelOutputEngine(
        ChannelType channelType,
        string channelName,
        IComparer<string> cursorComparer,
        List<TRequest> pendingRequests,
        Func<ToolInteractionRequest, TRequest> createPendingRequest,
        Func<ToolInteractionRequest, bool> isApprovalRequest,
        Func<TextOutput, string?> renderTextOutput,
        Func<ErrorOutput, string> renderErrorOutput,
        Func<string, Task<bool>> postTextAsync,
        Func<FileOutput, Task<bool>> uploadFileAsync,
        Func<ToolInteractionRequest, Task<TPromptId?>> postApprovalPromptAsync,
        Func<TPromptId, string> readPromptIdValue,
        Func<ToolInteractionRequest, Task> onApprovalPromptFailedAsync,
        Action<PendingApprovalPromptTracked> persistPromptTracked,
        Func<SessionOutput, Task> handleChannelSpecificOutputAsync,
        Action<string> advanceCursor,
        Func<Task> postEmptyTurnFallbackAsync,
        Func<TurnCompleted, Task> onEmptyTurnSuppressedAsync,
        Func<TurnCompleted, long?> readObservedAtMs)
    {
        _channelType = channelType;
        _channelName = channelName;
        _cursorComparer = cursorComparer;
        _pendingRequests = pendingRequests;
        _createPendingRequest = createPendingRequest;
        _isApprovalRequest = isApprovalRequest;
        _renderTextOutput = renderTextOutput;
        _renderErrorOutput = renderErrorOutput;
        _postTextAsync = postTextAsync;
        _uploadFileAsync = uploadFileAsync;
        _postApprovalPromptAsync = postApprovalPromptAsync;
        _readPromptIdValue = readPromptIdValue;
        _onApprovalPromptFailedAsync = onApprovalPromptFailedAsync;
        _persistPromptTracked = persistPromptTracked;
        _handleChannelSpecificOutputAsync = handleChannelSpecificOutputAsync;
        _advanceCursor = advanceCursor;
        _postEmptyTurnFallbackAsync = postEmptyTurnFallbackAsync;
        _onEmptyTurnSuppressedAsync = onEmptyTurnSuppressedAsync;
        _readObservedAtMs = readObservedAtMs;
    }

    /// <summary>
    /// True once this binding has seen at least one approval request. The
    /// text-approval cold path reads it; the actor also sets it during journal
    /// replay through <see cref="MarkApprovalRequestObserved"/>.
    /// </summary>
    public bool HasObservedApprovalRequest { get; private set; }

    /// <summary>
    /// Reliable in-flight-turn signal for the mention re-arm guard: set when a
    /// turn is enqueued (unlike the pending cursor, which is only set for
    /// inbounds with a usable cursor key), cleared when the turn ends or the
    /// pipeline reinitializes. Without it, a keyless in-flight turn would leave
    /// the pending cursor unset and let a following mention re-arm mid-turn —
    /// the PR #733 no-duplicate hazard.
    /// </summary>
    public bool TurnInFlight { get; private set; }

    /// <summary>
    /// The highest cursor key of the turn in flight. It becomes the persisted
    /// cursor only when the turn completes.
    /// </summary>
    public string? PendingCursor { get; private set; }

    /// <summary>
    /// Turn number of the most recently completed turn. Channels that report a
    /// delivery failure outside a turn boundary use it as the failure's turn.
    /// </summary>
    public TurnNumber LastCompletedTurnNumber { get; private set; }

    /// <summary>Records that the journal replayed an approval prompt.</summary>
    public void MarkApprovalRequestObserved() => HasObservedApprovalRequest = true;

    /// <summary>
    /// Records that a turn went into the input queue. The turn-in-flight flag
    /// guards the mention re-arm path; the pending cursor keeps the highest
    /// cursor key of the turn.
    /// </summary>
    public void AdvancePendingCursorForEnqueuedTurn(string? candidateCursor)
    {
        TurnInFlight = true;

        if (candidateCursor is null)
            return;

        if (PendingCursor is not { } pending || _cursorComparer.Compare(candidateCursor, pending) > 0)
            PendingCursor = candidateCursor;
    }

    /// <summary>
    /// Drops the cursor key of an abandoned turn so a later turn cannot commit
    /// it. Slack calls this on a pipeline reinitialize; Discord and Mattermost
    /// keep their pending cursor across a reinitialize.
    /// </summary>
    public void DiscardPendingCursor() => PendingCursor = null;

    /// <summary>
    /// Registers the reply target that waits for a delivery-gated reminder's
    /// outcome. Keyed by the per-fire reminder delivery id so a second
    /// concurrent reminder to this session cannot overwrite the first's
    /// observer before its turn reaches <see cref="TurnCompleted"/>.
    /// </summary>
    public void TrackReminderDeliveryObserver(ReminderId reminderKey, IActorRef observer)
        => _reminderDeliveryObservers[reminderKey] = observer;

    /// <summary>
    /// Clears the per-turn delivery state after a pipeline reinitialize. The
    /// reinitialize abandons any in-flight turn before its
    /// <see cref="TurnCompleted"/>, so the mention re-arm guard is released and
    /// every waiting reminder observer is told the delivery did not happen. The
    /// execution actor then redelivers immediately instead of stalling until
    /// its backstop timeout.
    /// </summary>
    public void ResetForPipelineReinitialize(string reason)
    {
        _deliveredThisTurn = false;
        _postFailedThisTurn = false;
        TurnInFlight = false;
        FailPendingReminderDeliveries($"{_channelName} pipeline reinitialized: {reason}");
    }

    /// <summary>
    /// Handles one session output. Returns the
    /// <see cref="PendingApprovalPromptCleared"/> events the caller must
    /// persist; the list is empty for every output except a completed turn that
    /// still held prompts.
    /// </summary>
    public async Task<IReadOnlyList<PendingApprovalPromptCleared>> HandleOutputAsync(SessionOutput output)
    {
        switch (output)
        {
            case TextOutput textOutput:
                if (_renderTextOutput(textOutput) is { } text)
                    RecordDeliveryOutcome(await _postTextAsync(text));
                break;

            case ErrorOutput errorOutput:
                RecordDeliveryOutcome(await _postTextAsync(_renderErrorOutput(errorOutput)));
                break;

            case FileOutput fileOutput:
                RecordDeliveryOutcome(await _uploadFileAsync(fileOutput));
                break;

            case ToolInteractionRequest request when _isApprovalRequest(request):
                await HandleApprovalRequestAsync(request);
                break;

            case TurnCompleted completed:
                return await CompleteTurnAsync(completed);

            default:
                await _handleChannelSpecificOutputAsync(output);
                break;
        }

        return NoClearedPrompts;
    }

    private void RecordDeliveryOutcome(bool succeeded)
    {
        if (succeeded)
            _deliveredThisTurn = true;
        else
            _postFailedThisTurn = true;
    }

    private async Task HandleApprovalRequestAsync(ToolInteractionRequest request)
    {
        HasObservedApprovalRequest = true;
        var pendingApproval = _createPendingRequest(request);
        _pendingRequests.Add(pendingApproval);

        var promptId = await _postApprovalPromptAsync(request);
        if (promptId is { } postedPromptId)
        {
            pendingApproval.PromptId = postedPromptId;
            _persistPromptTracked(BuildTracked(pendingApproval, _readPromptIdValue(postedPromptId)));
            return;
        }

        // Posting the approval prompt failed. Drop the pending entry and route a
        // deny back to the session so the blocked tool task can unwind instead
        // of waiting on the infinite-timeout completion source.
        _pendingRequests.Remove(pendingApproval);
        await _onApprovalPromptFailedAsync(request);
    }

    private async Task<IReadOnlyList<PendingApprovalPromptCleared>> CompleteTurnAsync(TurnCompleted completed)
    {
        if (completed.Outcome == TurnOutcome.Completed && PendingCursor is { } pendingCursor)
            _advanceCursor(pendingCursor);
        PendingCursor = null;
        TurnInFlight = false;

        if (completed.SourceReminderId is { } sourceReminderKey
            && !string.IsNullOrWhiteSpace(sourceReminderKey.Value)
            && _reminderDeliveryObservers.Remove(sourceReminderKey, out var reminderObserver))
        {
            reminderObserver.Tell(new ReminderDeliveryResult(
                sourceReminderKey,
                _channelType,
                Delivered: _deliveredThisTurn,
                FailureReason: _deliveredThisTurn ? null : $"{_channelName} post did not succeed",
                ObservedAtMs: _readObservedAtMs(completed)));
        }

        if (!_deliveredThisTurn)
        {
            // Only post the empty-turn fallback when the turn genuinely
            // produced nothing. A failed post means a reply WAS produced, so
            // "I didn't manage to produce a reply" would mislead and would
            // double up with the redelivered one. The channel decides what to
            // do instead, because the channels differ on when they tell the
            // session about the failure.
            if (_postFailedThisTurn)
                await _onEmptyTurnSuppressedAsync(completed);
            else
                await _postEmptyTurnFallbackAsync();
        }

        LastCompletedTurnNumber = completed.TurnNumber;

        var clearedPrompts = _pendingRequests
            .Select(pending => new PendingApprovalPromptCleared { CallId = pending.CallId.Value })
            .ToArray();
        _pendingRequests.Clear();
        _deliveredThisTurn = false;
        _postFailedThisTurn = false;
        return clearedPrompts;
    }

    /// <summary>
    /// Tells every in-flight reminder observer that delivery did not happen,
    /// then clears them.
    /// </summary>
    private void FailPendingReminderDeliveries(string reason)
    {
        if (_reminderDeliveryObservers.Count == 0)
            return;

        foreach (var (key, observer) in _reminderDeliveryObservers)
            observer.Tell(new ReminderDeliveryResult(key, _channelType, Delivered: false, FailureReason: reason));

        _reminderDeliveryObservers.Clear();
    }

    private static PendingApprovalPromptTracked BuildTracked(TRequest pending, string promptId)
        => new()
        {
            CallId = pending.CallId.Value,
            RequesterSenderId = pending.RequesterSenderId,
            RequesterPrincipal = pending.RequesterPrincipal,
            OptionKeys = pending.OptionKeys,
            PromptId = promptId,
            ToolName = pending.ToolName,
            // Preserve null-vs-set semantics on the wire: Truncate returns
            // string.Empty for null input, which would round-trip as
            // DisplayText="" with HasDisplayText=true.
            DisplayText = string.IsNullOrEmpty(pending.DisplayText)
                ? null
                : ApprovalDisplayTextFormatter.Truncate(
                    pending.DisplayText,
                    PendingApprovalPromptTracked.MaxPersistedDisplayTextChars)
        };
}
