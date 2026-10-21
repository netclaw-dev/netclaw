// -----------------------------------------------------------------------
// <copyright file="ChannelOutputEngine.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Akka.Actor;
using Netclaw.Actors.Channels;
using Netclaw.Actors.Protocol;
using static Netclaw.Actors.Sessions.SessionProtocol;

namespace Netclaw.Channels;

/// <summary>
/// Shared session-output handling for the channel binding actors. The engine
/// owns the per-turn delivery bookkeeping: the pending cursor, the
/// turn-in-flight signal, the empty-turn
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
    private readonly Func<TurnCompleted, Task> _postEmptyTurnFallbackAsync;
    private readonly Func<TurnCompleted, Task> _onEmptyTurnSuppressedAsync;

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
    /// Handles a prompt that could not be posted. Every channel routes a
    /// <c>prompt_unavailable</c> refusal so the blocked tool call unwinds
    /// instead of waiting forever, and the model does not read it as a user
    /// decision.
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
    /// <param name="postEmptyTurnFallbackAsync">
    /// Posts the channel's empty-turn fallback text. It receives the completed
    /// turn, so a failed fallback post can report the turn that it answers.
    /// </param>
    /// <param name="onEmptyTurnSuppressedAsync">
    /// Runs instead of the fallback when the turn produced output that failed
    /// to post and no output posted. Every channel tells the session about the
    /// failure here, with the number of the completed turn. The engine does not
    /// run it for a turn with a posted output.
    /// </param>
    public ChannelOutputEngine(
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
        Func<TurnCompleted, Task> postEmptyTurnFallbackAsync,
        Func<TurnCompleted, Task> onEmptyTurnSuppressedAsync)
    {
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
    /// Clears the per-turn state after a pipeline reinitialize. The
    /// reinitialize abandons any in-flight turn before its
    /// <see cref="TurnCompleted"/>, so the mention re-arm guard is released.
    /// <para>
    /// The pending cursor of the abandoned turn goes too. A later turn must not
    /// commit it: the cursor would move past messages that no session ever
    /// processed, and gap hydration excludes every message at or before the
    /// cursor. The abandoned turn's messages stay in the gap instead, so the
    /// next hydration re-includes them.
    /// </para>
    /// </summary>
    public void ResetForPipelineReinitialize()
    {
        _deliveredThisTurn = false;
        _postFailedThisTurn = false;
        TurnInFlight = false;
        PendingCursor = null;
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

        if (!_deliveredThisTurn)
        {
            // Only post the empty-turn fallback when the turn genuinely
            // produced nothing. A failed post means a reply WAS produced, so
            // "I didn't manage to produce a reply" would mislead and would
            // double up with the redelivered one. The channel tells the session
            // about the failure, with this turn's number.
            // A quiet end is a deliberate end with no reply. The fallback is Netclaw text, and the person must see
            // only text of the model. The end is a normal turn otherwise, so the typing indicator clears.
            if (_postFailedThisTurn)
                await _onEmptyTurnSuppressedAsync(completed);
            else if (!completed.QuietEnd)
                await _postEmptyTurnFallbackAsync(completed);
        }

        // A turn end clears only the prompts of the turn. A child prompt stays until an answer or a close (#2523).
        var turnPrompts = _pendingRequests
            .Where(static pending => pending.Lifetime == ToolInteractionLifetime.UntilTurnEnd)
            .ToArray();
        var clearedPrompts = turnPrompts
            .Select(pending => new PendingApprovalPromptCleared { CallId = pending.CallId.Value })
            .ToArray();
        _pendingRequests.RemoveAll(pending => turnPrompts.Contains(pending));
        _deliveredThisTurn = false;
        _postFailedThisTurn = false;
        return clearedPrompts;
    }

    private static PendingApprovalPromptTracked BuildTracked(TRequest pending, string promptId)
        => new()
        {
            CallId = pending.CallId.Value,
            RequesterSenderId = pending.RequesterSenderId,
            RequesterPrincipal = pending.RequesterPrincipal,
            OptionKeys = pending.OptionKeys,
            PromptId = promptId,
            Lifetime = pending.Lifetime,
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
