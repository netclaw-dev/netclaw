// -----------------------------------------------------------------------
// <copyright file="ApprovalResponseFlow.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Akka.Event;
using Netclaw.Actors.Channels;
using Netclaw.Actors.Protocol;
using Netclaw.Channels.Telemetry;
using Netclaw.Tools;
using static Netclaw.Actors.Sessions.SessionProtocol;

namespace Netclaw.Channels;

/// <summary>
/// Redraws a resolved approval prompt on the channel. Each channel supplies
/// its own renderer because the prompt is a transport-specific message.
/// </summary>
/// <param name="promptId">The prompt locator, or <c>null</c> when the binding never captured one.</param>
/// <param name="request">The original request on the hot path; <c>null</c> on the cold-spawn path.</param>
/// <param name="callId">The tool call the prompt belongs to.</param>
/// <param name="selectedKey">The option the sender selected.</param>
/// <param name="senderId">The sender that resolved the prompt.</param>
/// <param name="persistedToolName">Tool name from the journal, for the cold-spawn redraw.</param>
/// <param name="persistedDisplayText">Display text from the journal, for the cold-spawn redraw.</param>
public delegate Task ResolvedApprovalPromptRenderer<TPromptId>(
    TPromptId? promptId,
    ToolInteractionRequest? request,
    ToolCallId callId,
    string selectedKey,
    string senderId,
    string? persistedToolName,
    string? persistedDisplayText)
    where TPromptId : struct;

/// <summary>
/// Shared approval-response handling for the channel binding actors. The flow
/// owns text-approval parsing, the cold-spawn text path, pending-prompt
/// resolution, and the requester identity check. Slack, Discord, and Mattermost
/// supply the transport effects; the algorithm has one implementation here.
/// </summary>
/// <remarks>
/// The flow holds no Akka state. It does not persist an event and it does not
/// touch the actor context. Each actor-owned effect (prompt redraw, warning
/// post, journal write) goes through a callback that the binding actor supplies
/// at construction. All callbacks run on the actor's own message-processing
/// path, so they are safe to read and write actor state.
/// </remarks>
public sealed class ApprovalResponseFlow<TRequest, TPromptId>
    where TRequest : PendingApprovalRequest<TPromptId>
    where TPromptId : struct
{
    private readonly SessionId _sessionId;
    private readonly ChannelType _channelType;
    private readonly string _channelName;
    private readonly ISessionPipeline _pipeline;
    private readonly TimeSpan _operationTimeout;
    private readonly List<TRequest> _pendingRequests;
    private readonly Func<bool> _hasObservedApprovalRequest;
    private readonly Func<Task> _postWrongRequesterWarningAsync;
    private readonly Action<ToolCallId> _persistPromptCleared;
    private readonly ResolvedApprovalPromptRenderer<TPromptId> _renderResolvedPromptAsync;
    private readonly ILoggingAdapter _log;

    /// <summary>
    /// Creates the flow. Every dependency is required: the pending-request list
    /// and the warning poster carry the requester identity check, so the flow
    /// cannot run with either of them absent.
    /// </summary>
    /// <param name="sessionId">The session this binding serves.</param>
    /// <param name="channelType">Telemetry category for the channel.</param>
    /// <param name="channelName">Channel name for the log text, for example <c>Slack</c>.</param>
    /// <param name="pipeline">The session pipeline that carries approval feedback.</param>
    /// <param name="operationTimeout">Deadline for one feedback round trip.</param>
    /// <param name="pendingRequests">
    /// The actor's own pending-approval list. The flow reads it and removes the
    /// entry it resolves; the actor keeps adding to it and replaying it.
    /// </param>
    /// <param name="hasObservedApprovalRequest">Reads the cold-path gate.</param>
    /// <param name="postWrongRequesterWarningAsync">Posts the channel's wrong-requester warning.</param>
    /// <param name="persistPromptCleared">
    /// Journals <c>PendingApprovalPromptCleared</c> for the resolved call. The
    /// actor owns the persistence call; the flow never touches Akka persistence.
    /// </param>
    /// <param name="renderResolvedPromptAsync">Redraws the resolved prompt on the channel.</param>
    /// <param name="log">The binding actor's logger, with its adapter context fields.</param>
    public ApprovalResponseFlow(
        SessionId sessionId,
        ChannelType channelType,
        string channelName,
        ISessionPipeline pipeline,
        TimeSpan operationTimeout,
        List<TRequest> pendingRequests,
        Func<bool> hasObservedApprovalRequest,
        Func<Task> postWrongRequesterWarningAsync,
        Action<ToolCallId> persistPromptCleared,
        ResolvedApprovalPromptRenderer<TPromptId> renderResolvedPromptAsync,
        ILoggingAdapter log)
    {
        _sessionId = sessionId;
        _channelType = channelType;
        _channelName = channelName;
        _pipeline = pipeline;
        _operationTimeout = operationTimeout;
        _pendingRequests = pendingRequests;
        _hasObservedApprovalRequest = hasObservedApprovalRequest;
        _postWrongRequesterWarningAsync = postWrongRequesterWarningAsync;
        _persistPromptCleared = persistPromptCleared;
        _renderResolvedPromptAsync = renderResolvedPromptAsync;
        _log = log;
    }

    /// <summary>
    /// Handles an inbound text message that can be an approval reply, for
    /// example "A" or "deny".
    /// </summary>
    /// <returns>
    /// <c>true</c> when the flow consumed the message, so the actor must not
    /// send it to the session as ordinary conversation.
    /// </returns>
    public async Task<bool> TryHandleTextApprovalResponseAsync(string? text, string senderId)
    {
        var (result, pending) = PendingApprovalLookup.Resolve<TRequest, TPromptId>(
            _pendingRequests, senderId, callId: null);

        if (result is ApprovalLookupResult.NotFound)
        {
            // The cold path runs only for a binding that has never observed any
            // ToolInteractionRequest. Such a binding treats an inbound
            // "A"/"B"/"C" as a possible cold approval reply for a session that
            // restarted out from under it. Once we've observed at least one
            // prompt, subsequent ambiguous text from the user is ordinary
            // conversation, not an approval reply, so the cold path stays off.
            // This does NOT gate button clicks — those always route to the
            // session, which is the authority on CallId staleness.
            return !_hasObservedApprovalRequest()
                && await TryHandleColdTextApprovalResponseAsync(text, senderId);
        }

        if (result is ApprovalLookupResult.WrongRequester)
        {
            await _postWrongRequesterWarningAsync();
            return true;
        }

        if (!ToolInteractionResponseParser.TryParseApprovalResponse(
                text ?? string.Empty,
                pending!.Options,
                out var selectedKey)
            || selectedKey is null)
        {
            return false;
        }

        ISessionResponse feedbackResult;
        try
        {
            using var feedbackCts = new CancellationTokenSource(_operationTimeout);
            feedbackResult = await _pipeline.SendFeedbackAndWaitAsync(
                new ToolInteractionResponse
                {
                    SessionId = _sessionId,
                    CallId = pending.CallId,
                    SelectedKey = new ApprovalOptionKey(selectedKey),
                    SenderId = new SenderId(senderId)
                }, feedbackCts.Token);
        }
        catch (Exception ex)
        {
            _log.Error(ex, "Failed to route {Channel} text approval response for call {CallId}", _channelName, pending.CallId);
            return true;
        }

        switch (feedbackResult)
        {
            case CommandNack nack:
                if (string.Equals(nack.Reason, ApprovalNackReasons.WrongRequester, StringComparison.Ordinal))
                    await _postWrongRequesterWarningAsync();
                _log.Info(
                    "Session rejected {Channel} text approval response for call {CallId} reason={Reason}; skipping redraw",
                    _channelName,
                    pending.CallId,
                    nack.Reason ?? "<none>");
                return true;

            case not CommandAck:
                _log.Warning(
                    "{Channel} text approval response for call {CallId} returned unexpected feedback result {ResultType}",
                    _channelName,
                    pending.CallId,
                    feedbackResult.GetType().Name);
                return true;
        }

        ClearResolved(pending);

        await _renderResolvedPromptAsync(
            pending.PromptId,
            pending.Request,
            pending.CallId,
            selectedKey,
            senderId,
            pending.ToolName,
            pending.DisplayText);

        _log.Info(
            "Recorded {Channel} text approval response for call {CallId} sender={SenderId} selection={SelectedKey}",
            _channelName,
            pending.CallId,
            senderId,
            selectedKey);
        return true;
    }

    /// <summary>
    /// Handles an interactive approval response, for example a button click.
    /// </summary>
    /// <param name="callId">The tool call the response answers.</param>
    /// <param name="selectedKey">The option the sender selected.</param>
    /// <param name="senderId">The sender that answered.</param>
    /// <param name="payloadPromptId">
    /// The prompt locator carried on the response payload. It is the redraw
    /// target when the binding holds no pending entry for the call.
    /// </param>
    /// <param name="respondSynchronously">
    /// Mattermost only: its interactive-message webhook asks the binding over
    /// HTTP and needs the session's verdict back on the same request. Discord
    /// gateway events and Slack interactions are one-way, so those channels
    /// pass nothing and the flow sends no synchronous reply.
    /// </param>
    public async Task HandleApprovalResponseAsync(
        ToolCallId callId,
        string selectedKey,
        string senderId,
        TPromptId? payloadPromptId,
        Action<ISessionResponse>? respondSynchronously = null)
    {
        var (result, pending) = PendingApprovalLookup.Resolve<TRequest, TPromptId>(
            _pendingRequests, senderId, callId);

        // CanApprove fast-path: if the binding still holds the original request we can
        // post the wrong-requester warning locally without round-tripping through the
        // session. When the binding has been cold-spawned (no local pending entry) the
        // session re-runs CanApprove against its own pending-call state, and the wait
        // below blocks the redraw until the session has actually accepted the click —
        // see #939 + #979.
        if (result is ApprovalLookupResult.WrongRequester)
        {
            await _postWrongRequesterWarningAsync();
            respondSynchronously?.Invoke(CommandNack.For(_sessionId, ApprovalNackReasons.WrongRequester));
            return;
        }

        // Wait for the session before redrawing. This is the security gate that
        // prevents (a) a non-requester click destroying the prompt UI on the
        // cold-spawn path, and (b) a stale re-click overwriting an already-resolved
        // banner — both surfaced by the #939 code review. The session is the
        // authority on whether the call is still pending and whether the sender is
        // allowed. Only redraw on CommandAck.
        ISessionResponse feedbackResult;
        try
        {
            using var feedbackCts = new CancellationTokenSource(_operationTimeout);
            feedbackResult = await _pipeline.SendFeedbackAndWaitAsync(
                new ToolInteractionResponse
                {
                    SessionId = _sessionId,
                    CallId = callId,
                    SelectedKey = new ApprovalOptionKey(selectedKey),
                    SenderId = new SenderId(senderId)
                }, feedbackCts.Token);
        }
        catch (Exception ex)
        {
            _log.Error(ex, "Failed to route {Channel} approval response for call {CallId}", _channelName, callId);
            respondSynchronously?.Invoke(CommandNack.For(_sessionId, ApprovalNackReasons.PersistFailed));
            return;
        }

        CommandAck ack;
        switch (feedbackResult)
        {
            case CommandNack nack:
                // Session rejected (wrong requester, unknown call, stale resolution).
                // For wrong-requester surface the warning; for any other reason just
                // log and DO NOT redraw — the prompt UI must stay consistent with the
                // session's authoritative state.
                if (string.Equals(nack.Reason, ApprovalNackReasons.WrongRequester, StringComparison.Ordinal))
                    await _postWrongRequesterWarningAsync();
                _log.Info(
                    "Session rejected {Channel} approval response for call {CallId} reason={Reason}; skipping redraw",
                    _channelName,
                    callId,
                    nack.Reason ?? "<none>");
                respondSynchronously?.Invoke(nack);
                return;

            case CommandAck ok:
                ack = ok;
                break;

            default:
                // Unreachable: ISessionResponse is implemented only by CommandAck
                // and CommandNack. Kept as a defensive guard so an unexpected
                // future implementer surfaces a structured Nack instead of an
                // unobservable null reference.
                _log.Warning(
                    "{Channel} approval response for call {CallId} returned unexpected feedback result {ResultType}",
                    _channelName,
                    callId,
                    feedbackResult.GetType().Name);
                respondSynchronously?.Invoke(CommandNack.For(_sessionId, ApprovalNackReasons.PersistFailed));
                return;
        }

        if (pending is not null)
        {
            ClearResolved(pending);
            // Prefer the captured prompt locator; fall back to the payload locator
            // when the capture failed (a narrow race while the prompt was posted).
            await _renderResolvedPromptAsync(
                pending.PromptId ?? payloadPromptId,
                pending.Request,
                pending.CallId,
                selectedKey,
                senderId,
                pending.ToolName,
                pending.DisplayText);

            _log.Info(
                "Recorded {Channel} approval response for call {CallId} sender={SenderId} selection={SelectedKey}",
                _channelName,
                pending.CallId,
                senderId,
                selectedKey);
        }
        else if (payloadPromptId is { } resolvedPayloadPromptId)
        {
            // Cold-spawn redraw — no local pending entry exists for this CallId
            // (the journal didn't replay a PendingApprovalPromptTracked event for
            // it, typically because the binding was re-created without recovery
            // or the entry was cleared before the click landed). The response
            // payload still carries the prompt's locator and the session has
            // accepted the response; render the generic banner so the buttons
            // clear. NOTE: pre-0.21 journals that DO replay (with no tool name /
            // display text) take the upper `pending is not null` branch instead
            // — they render the generic banner via the !IsNullOrEmpty fallback
            // inside the builder, not via this branch.
            await _renderResolvedPromptAsync(
                resolvedPayloadPromptId,
                request: null,
                callId,
                selectedKey,
                senderId,
                persistedToolName: null,
                persistedDisplayText: null);

            _log.Info(
                "Forwarded {Channel} approval response for call {CallId} to session without local pending entry; redrew prompt via payload promptId={PromptId}",
                _channelName,
                callId,
                resolvedPayloadPromptId);
        }
        else
        {
            // Text-reply on a cold-spawned binding: the approval still routes,
            // but no prompt locator is available, so the redraw is impossible.
            // Remaining #939 gap.
            _log.Info(
                "Forwarded {Channel} approval response for call {CallId} to session without local pending entry; redraw skipped",
                _channelName,
                callId);
            ChannelTelemetry.For(_channelType).RecordExtra("interactionErrors", "cold_spawn_redraw_skipped");
        }

        respondSynchronously?.Invoke(ack);
    }

    /// <summary>
    /// Forwards an approval-shaped text message for a binding that holds no
    /// prompt state of its own. The session is the authority on whether an
    /// approval is pending, so the reply decides whether the message is
    /// consumed or falls through to normal ingress.
    /// </summary>
    private async Task<bool> TryHandleColdTextApprovalResponseAsync(string? text, string senderId)
    {
        if (!ToolInteractionResponseParser.LooksLikeApprovalResponse(text ?? string.Empty))
            return false;

        using var feedbackCts = new CancellationTokenSource(_operationTimeout);
        try
        {
            var reply = await _pipeline.SendFeedbackAndWaitAsync(new ToolInteractionTextResponse
            {
                SessionId = _sessionId,
                Text = text ?? string.Empty,
                SenderId = new SenderId(senderId)
            }, feedbackCts.Token);

            if (reply is CommandAck)
            {
                _log.Info(
                    "Forwarded cold {Channel} text approval response from sender={SenderId} without local pending prompt state",
                    _channelName,
                    senderId);
                return true;
            }

            // approval_no_history means the session has never had an approval request.
            // The message was a false-positive from LooksLikeApprovalResponse.
            // Don't consume — let it fall through to normal LLM ingress. See #1164.
            if (reply is CommandNack { Reason: ApprovalNackReasons.NoHistory })
                return false;

            return reply is CommandNack;
        }
        catch (Exception ex)
        {
            _log.Error(
                ex,
                "Failed to route cold {Channel} text approval response from sender {SenderId}",
                _channelName,
                senderId);
            return false;
        }
    }

    private void ClearResolved(TRequest pending)
    {
        _pendingRequests.Remove(pending);
        _persistPromptCleared(pending.CallId);
    }
}
