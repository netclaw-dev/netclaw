// -----------------------------------------------------------------------
// <copyright file="MattermostSessionBindingActor.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Text;
using System.Threading.Channels;
using Akka.Actor;
using Akka.Event;
using Akka.Persistence;
using Microsoft.Extensions.AI;
using Netclaw.Actors.Channels;
using Netclaw.Actors.Protocol;
using Netclaw.Actors.Reminders;
using Netclaw.Channels;
using Netclaw.Channels.Telemetry;
using Netclaw.Configuration;
using Netclaw.Media;
using Netclaw.Security;
using Netclaw.Tools;
using IOPath = System.IO.Path;
using static Netclaw.Actors.Sessions.SessionProtocol;
using static Netclaw.Actors.Reminders.ReminderProtocol;

namespace Netclaw.Channels.Mattermost;

internal sealed class MattermostSessionBindingActor : ReceivePersistentActor, IWithTimers
{
    private readonly SessionId _sessionId;
    private readonly MattermostChannelId _channelId;
    private readonly MattermostRootPostId _rootPostId;

    private const string EmptyTurnFallbackText =
        ":warning: I didn't manage to produce a reply. Please try rephrasing or sending your message again.";
    private const string LiveInjectionBlockedWarning =
        ":warning: Message blocked by prompt-injection policy.";
    private const string LiveDetectorUnavailableWarning =
        ":warning: I couldn't safely analyze your message -- please try again in a moment.";
    private const string WrongRequesterWarning =
        ":warning: Only the requesting user can approve this tool action.";
    private const string BackfillDetectorWarning =
        ":warning: I couldn't safely analyze some earlier thread messages, so they were excluded from context.";

    private const int MaxMattermostPostLength = 16_000;

    private readonly MattermostGatewayDependencies _dependencies;
    private readonly IPromptInjectionDetector _promptInjectionDetector;
    private readonly SessionPipelineHandle _handle;
    private readonly ILoggingAdapter _log;

    // Null when the gateway supplies no thread-history fetcher. That is a real
    // runtime state (an instance without history access), not a disabled check:
    // with no fetcher there is no gap to hydrate, so both hydration paths no-op.
    private readonly ThreadGapHydrationEngine? _hydrationEngine;
    private readonly List<PendingApprovalRequest> _pendingApprovalRequests = [];
    private readonly ApprovalResponseFlow<PendingApprovalRequest, MattermostPostId> _approvalFlow;

    // Gates the text-approval cold path. The gate rule lives with the cold path
    // in ApprovalResponseFlow.
    private bool _hasObservedApprovalRequest;

    private static readonly TimeSpan PipelineInitTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan ReinitializeDelay = TimeSpan.FromSeconds(2);
    private static readonly object ReinitializeTimerKey = new();
    private static readonly TimeSpan IdlePassivationTimeout = TimeSpan.FromHours(1);
    private bool _deliveredThisTurn;
    // True when a content post/upload this turn was attempted but failed (the
    // model produced output, the transport rejected it). Distinct from "nothing
    // was produced" — it suppresses the empty-turn fallback so a failed post
    // isn't followed by a misleading "I didn't manage to produce a reply".
    private bool _postFailedThisTurn;
    // Reply targets for in-flight reminder delivery confirmations, keyed by
    // reminder delivery key. Captured from DeliverTrustedSessionTurn; each is
    // told a ReminderDeliveryResult on its turn's TurnCompleted and removed.
    // Keyed (not a single field) because multiple reminders can target the
    // same session concurrently — a single field would be clobbered.
    private readonly Dictionary<ReminderId, IActorRef> _reminderDeliveryObservers = new();
    private TurnNumber _turnNumber;
    private string? _cursorPostId;
    private string? _pendingCursorPostId;

    // Reliable in-flight-turn signal for the mention re-arm guard: set when the
    // main inbound is enqueued (unlike _pendingCursorPostId, which is only set for
    // inbounds with a non-empty post id), cleared when the turn ends or the
    // pipeline reinitializes. Without it, a null-id in-flight turn would leave
    // _pendingCursorPostId unset and let a following mention re-arm mid-turn —
    // the PR #733 no-duplicate hazard.
    private bool _turnInFlight;

    // Set when PerformOneShotHydrationAsync fetched a non-empty thread gap but
    // found no authorized trigger to anchor a turn. This is the proactive-thread
    // case: the binding actor's lifetime began when the agent posted the thread
    // root, so the one-shot hydration ran before any authorized human inbound
    // existed. While set, the first authorized inbound performs the deferred
    // hydration instead of taking the fetch-free path; it is cleared once that
    // hydration completes.
    private bool _hydrationPending;

    public ITimerScheduler Timers { get; set; } = null!;

    public MattermostSessionBindingActor(
        SessionId sessionId,
        MattermostChannelId channelId,
        MattermostRootPostId rootPostId,
        MattermostGatewayDependencies dependencies)
    {
        _sessionId = sessionId;
        _channelId = channelId;
        _rootPostId = rootPostId;
        _dependencies = dependencies;
        // Fail loud rather than substituting a no-op detector — a no-op reports
        // every input as safe, silently disabling injection scanning. A null
        // here means broken gateway wiring.
        _promptInjectionDetector = dependencies.PromptInjectionDetector
            ?? throw new InvalidOperationException(
                "MattermostGatewayDependencies.PromptInjectionDetector is not wired; "
                + "prompt-injection scanning cannot be silently disabled.");

        _log = Context.GetLogger()
            .WithContext("Adapter", "mattermost")
            .WithContext(NetclawLogProperties.SessionId, _sessionId.Value)
            .WithContext("MattermostChannelId", _channelId.Value)
            .WithContext("MattermostRootPostId", _rootPostId.Value);

        _handle = new SessionPipelineHandle(_dependencies.Pipeline, _log, "mattermost-session");

        if (_dependencies.ThreadHistoryFetcher is { } historyFetcher)
        {
            _hydrationEngine = new ThreadGapHydrationEngine(
                sessionId: _sessionId,
                channelType: ChannelType.Mattermost,
                historyFetcher: historyFetcher,
                injectionDetector: _promptInjectionDetector,
                classifierSourceContext: "mattermost-backfill",
                // Mattermost post IDs are lexicographically sortable strings.
                cursorComparer: StringComparer.Ordinal,
                cursorKeySelector: messageId => string.IsNullOrEmpty(messageId) ? null : messageId,
                isAuthorizedSender: IsAuthorizedSender,
                log: _log,
                readCursor: () => _cursorPostId,
                readInputQueue: () => _handle.InputQueue,
                readIngressClosedReason: () => _dependencies.IngressGate?.ClosedReason,
                warnBackfillDetectorUnavailableAsync: () => SafeReplyAsync(BackfillDetectorWarning),
                onBackfillEnqueued: AdvancePendingCursorForEnqueuedTurn,
                setHydrationPending: pending => _hydrationPending = pending);
        }

        _approvalFlow = new ApprovalResponseFlow<PendingApprovalRequest, MattermostPostId>(
            sessionId: _sessionId,
            channelType: ChannelType.Mattermost,
            channelName: "Mattermost",
            pipeline: _dependencies.Pipeline,
            operationTimeout: OperationTimeout,
            pendingRequests: _pendingApprovalRequests,
            matchOrder: ApprovalMatchOrder.Newest,
            hasObservedApprovalRequest: () => _hasObservedApprovalRequest,
            postWrongRequesterWarningAsync: () => SafeReplyAsync(WrongRequesterWarning),
            persistPromptCleared: callId => Persist(
                new PendingApprovalPromptCleared { CallId = callId.Value },
                ApplyPendingApprovalPromptCleared),
            renderResolvedPromptAsync: TryResolveApprovalPromptAsync,
            log: _log);

        Recover<CursorAdvanced>(ApplyCursorAdvanced);
        Recover<PendingApprovalPromptTracked>(ApplyPendingApprovalPromptTracked);
        Recover<PendingApprovalPromptCleared>(ApplyPendingApprovalPromptCleared);
        // After journal replay completes, queue a one-shot hydration. The
        // self-tell lands in the mailbox after InitializePipeline (from
        // PreStart), so the actor finishes pipeline init first, then
        // transitions into Hydrating and processes PerformHydration.
        Recover<RecoveryCompleted>(_ => Self.Tell(PerformHydration.Instance));

        Initializing();
    }

    public override string PersistenceId => $"mattermost-session-cursor-{Uri.EscapeDataString(_sessionId.Value)}";

    public static Props CreateProps(
        SessionId sessionId,
        MattermostChannelId channelId,
        MattermostRootPostId rootPostId,
        MattermostGatewayDependencies dependencies)
        => Props.Create(() => new MattermostSessionBindingActor(
            sessionId,
            channelId,
            rootPostId,
            dependencies));

    protected override void PreStart()
    {
        Self.Tell(InitializePipeline.Instance);
        base.PreStart();
    }

    protected override void PostStop()
    {
        _handle.Dispose();
        base.PostStop();
    }

    private SessionPipelineOptions BuildOptions() => new()
    {
        ChannelType = ChannelType.Mattermost,
        Filter = OutputFilter.Text | OutputFilter.Files
    };

    private void Initializing()
    {
        CommandAsync<InitializePipeline>(async _ =>
        {
            try
            {
                await EnsureInitializedAsync();
                Become(Hydrating);
                // RecoveryCompleted can beat pipeline initialization on slower
                // dispatchers and get stashed here. Move it into Hydrating so
                // one-shot hydration cannot strand the actor in startup; live
                // inbounds are re-stashed by Hydrating until hydration finishes.
                Stash.UnstashAll();
            }
            catch (Exception ex)
            {
                _log.Error(ex, "Failed to initialize Mattermost session pipeline; stopping actor");
                Context.Stop(Self);
            }
        });

        CommandAny(msg =>
        {
            if (msg is not InitializePipeline)
                Stash.Stash();
        });
    }

    private void Hydrating()
    {
        CommandAsync<PerformHydration>(async _ =>
        {
            try
            {
                if (_hydrationEngine is { } engine)
                    await engine.PerformOneShotHydrationAsync();
            }
            catch (Exception ex)
            {
                _log.Warning(ex, "Thread history hydration threw; continuing without backfill");
            }
            finally
            {
                Become(Active);
                Stash.UnstashAll();
            }
        });

        CommandAny(_ => Stash.Stash());
    }

    private void Active()
    {
        CommandAsync<MattermostThreadInbound>(HandleInboundAsync);
        CommandAsync<MattermostApprovalResponse>(HandleApprovalResponseAsync);
        CommandAsync<DeliverTrustedSessionTurn>(HandleTrustedReminderAsync);
        CommandAsync<OutputReceived>(HandleOutputReceivedAsync);

        Command<OutputStreamTerminated>(msg =>
        {
            if (msg.Generation != _handle.Generation)
                return;

            var reason = msg.Cause is null
                ? "completed"
                : $"faulted: {msg.Cause.Message}";

            _log.Warning("Output stream terminated ({Reason}); reinitializing pipeline", reason);
            Self.Tell(new ReinitializePipeline(reason));
        });

        CommandAsync<ReinitializePipeline>(async msg =>
        {
            _deliveredThisTurn = false;
            _postFailedThisTurn = false;
            // A reinit abandons any in-flight turn before its TurnCompleted; clear
            // the mention re-arm guard so the next mention can re-hydrate instead of
            // being blocked by a stuck flag.
            _turnInFlight = false;
            // A reinit aborts any in-flight reminder turn before its
            // TurnCompleted. Report those as not-delivered now so the
            // execution actor redelivers immediately instead of stalling
            // until the backstop timeout.
            FailPendingReminderDeliveries($"Mattermost pipeline reinitialized: {msg.Reason}");
            await _handle.ReinitializeAsync(
                msg.Reason,
                () => Timers.StartSingleTimer(
                    ReinitializeTimerKey,
                    new ReinitializePipeline("retry after failed reinit"),
                    ReinitializeDelay));
        });

        Command<ReceiveTimeout>(_ =>
        {
            if (_pendingApprovalRequests.Count > 0)
            {
                _log.Info("Session idle but {0} approval(s) pending; deferring passivation", _pendingApprovalRequests.Count);
                return;
            }

            _log.Info("Session idle for 1 hour, passivating");
            Context.Stop(Self);
        });

        Context.SetReceiveTimeout(IdlePassivationTimeout);
    }

    private async Task EnsureInitializedAsync()
    {
        if (_handle.IsInitialized)
            return;

        var self = Self;
        using var initCts = new CancellationTokenSource(PipelineInitTimeout);
        await _handle.InitializeWithChannelAsync(
            Context,
            _sessionId,
            BuildOptions(),
            output => self.Tell(new OutputReceived(output)),
            (generation, cause) => self.Tell(new OutputStreamTerminated(generation, cause)),
            initCts.Token);
    }

    private static readonly TimeSpan InboundProcessingTimeout = TimeSpan.FromSeconds(30);

    private async Task HandleInboundAsync(MattermostThreadInbound message)
    {
        if (_dependencies.IngressGate?.ClosedReason is { } ingressClosedReason)
        {
            _log.Info("Rejecting Mattermost inbound message while restart drain is active");
            await SafeReplyAsync(ingressClosedReason);
            return;
        }

        var hasAttachments = message.Attachments is { Count: > 0 };
        if (string.IsNullOrWhiteSpace(message.Text) && !hasAttachments)
            return;

        if (!string.IsNullOrWhiteSpace(message.Text)
            && await TryHandleTextApprovalResponseAsync(message))
        {
            return;
        }

        using var inboundCts = new CancellationTokenSource(InboundProcessingTimeout);

        if (!string.IsNullOrWhiteSpace(message.Text))
        {
            var classification = await PromptClassifier.ClassifyAsync(
                _promptInjectionDetector, message.Text, "mattermost-live", _log, inboundCts.Token);
            switch (classification.Outcome)
            {
                case ClassificationOutcome.Block:
                    _log.Warning("Blocked Mattermost message due to prompt injection risk: {Reason}", classification.Reason);
                    ChannelTelemetry.For(ChannelType.Mattermost).RecordEventDropped("prompt_injection_high");
                    await SafeReplyAsync(LiveInjectionBlockedWarning);
                    return;

                case ClassificationOutcome.DetectorUnavailable:
                    _log.Warning("Prompt injection detector unavailable for live message -- dropping");
                    ChannelTelemetry.For(ChannelType.Mattermost).RecordEventDropped("prompt_injection_detector_unavailable");
                    await SafeReplyAsync(LiveDetectorUnavailableWarning);
                    return;

                case ClassificationOutcome.Allow:
                    break;
            }
        }

        var writer = _handle.InputQueue;
        if (writer is null)
        {
            _log.Warning("Input queue is not initialized; dropping inbound message");
            return;
        }

        var liveContents = new List<AIContent>();
        if (!string.IsNullOrWhiteSpace(message.Text))
            liveContents.Add(new TextContent(message.Text));

        if (hasAttachments)
            await ProcessInboundAttachmentsAsync(message.Attachments!, message.Audience, liveContents, inboundCts.Token);

        if (liveContents.Count == 0)
            return;

        // Live inbound path is fetch-free. Thread history is hydrated once per
        // actor lifetime by the hydration engine (driven by the
        // RecoveryCompleted handler); the only exception is a deferred
        // hydration, which the engine completes on the first authorized
        // inbound. By the time we get here the session already has the
        // historical context it needs.
        var input = new ChannelInput
        {
            SenderId = new SenderId(message.SenderId.Value),
            ChannelId = message.ChannelId.Value,
            MessageId = message.EventId.Value,
            Audience = message.Audience,
            Boundary = TrustBoundary.TrustedInstance,
            Principal = message.Principal,
            Provenance = message.Provenance,
            Contents = liveContents,
            ReceivedAt = message.ReceivedAt,
            ExecutableText = message.Text,
            DefaultDeliveryTarget = BuildDefaultDeliveryTarget()
        };

        // Re-arm thread-history hydration on a tap-gated mention so the gap the
        // tap held since the last completed turn is backfilled. Under
        // MentionRequiredInThread the conversation actor forwards only mentions here,
        // so every inbound is a deliberate re-entry. _turnInFlight guards against a
        // re-arm while a prior turn is still processing, preserving the PR #733
        // no-duplicate invariant; the deferred hydration no-ops on an empty gap.
        //
        // Two accepted trade-offs of reusing the existing backfill (vs. a new
        // drop-tracking side channel): (1) one thread-history fetch per mention on an
        // active thread even when nothing accumulated — the actor cannot know the gap
        // is empty without fetching, and mention-gating makes mentions deliberate so
        // the cost is bounded; (2) a mention arriving while a turn is in flight takes
        // the fetch-free path, so chatter in that window is adopted on the next idle
        // mention, not this one (and can be skipped if the cursor later advances past
        // it under rapid concurrent mentions).
        if (!_hydrationPending
            && !_turnInFlight
            && _dependencies.Options.MentionRequiredInThreadFor(_channelId.Value))
        {
            _hydrationPending = true;
        }

        if (_hydrationPending
            && IsAuthorizedSender(message.SenderId.Value)
            && _hydrationEngine is { } engine)
        {
            input = await engine.ApplyDeferredHydrationAsync(input, message.EventId.Value, inboundCts.Token);
        }

        try
        {
            using var writeCts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await writer.WriteAsync(input, writeCts.Token);
            ChannelTelemetry.For(ChannelType.Mattermost).RecordMessageEnqueued();

            var eventId = message.EventId.Value;
            AdvancePendingCursorForEnqueuedTurn(string.IsNullOrEmpty(eventId) ? null : eventId);
        }
        catch (OperationCanceledException)
        {
            _log.Warning("Timed out enqueueing message for session {0}", _sessionId.Value);
            Self.Tell(new ReinitializePipeline("input queue write timeout"));
        }
        catch (ChannelClosedException)
        {
            _log.Warning("Input queue closed for session {0}", _sessionId.Value);
            Self.Tell(new ReinitializePipeline("input queue closed"));
        }
    }

    // Mattermost authorization basis for adopted-context: an empty AllowedUserIds
    // list means the instance is unrestricted; otherwise the sender must be listed.
    private bool IsAuthorizedSender(string senderId)
        => _dependencies.Options.AllowedUserIds.Length == 0
            || _dependencies.Options.AllowedUserIds.Contains(senderId, StringComparer.Ordinal);

    private Task<bool> TryHandleTextApprovalResponseAsync(MattermostThreadInbound message)
        => _approvalFlow.TryHandleTextApprovalResponseAsync(message.Text, message.SenderId.Value);

    // The Mattermost interactive-message webhook asks the binding over HTTP and
    // waits for the session's verdict, so this is the one channel that registers
    // the synchronous-reply hook.
    private Task HandleApprovalResponseAsync(MattermostApprovalResponse message)
    {
        var replyTo = Sender;
        return _approvalFlow.HandleApprovalResponseAsync(
            message.CallId,
            message.SelectedKey,
            message.SenderId.Value,
            message.PromptPostId,
            respondSynchronously: response => ReplyIfExpected(replyTo, response));
    }

    private async Task TryResolveApprovalPromptAsync(
        MattermostPostId? promptPostId,
        ToolInteractionRequest? request,
        Netclaw.Tools.ToolCallId callId,
        string selectedKey,
        string senderId,
        string? persistedToolName = null,
        string? persistedDisplayText = null)
    {
        if (promptPostId is not { } postId)
            return;

        try
        {
            // Hot path uses the in-memory request. Cold-spawn path uses the persisted
            // tool name + display text when present (PendingApprovalPromptTracked
            // carried them); legacy journals without the fields fall back to the
            // generic banner.
            var resolvedAttachment = request is not null
                ? MattermostApprovalPromptBuilder.BuildResolvedAttachment(request, selectedKey, senderId)
                : MattermostApprovalPromptBuilder.BuildResolvedAttachmentWithoutRequest(
                    selectedKey,
                    senderId,
                    toolName: persistedToolName,
                    displayText: persistedDisplayText);

            using var cts = new CancellationTokenSource(OperationTimeout);
            await _dependencies.ReplyClient.UpdatePostAsync(
                postId,
                resolvedAttachment.Text ?? string.Empty,
                [resolvedAttachment],
                cts.Token);
        }
        catch (Exception ex)
        {
            _log.Warning(
                ex,
                "Failed to update resolved approval prompt for call {CallId} postId={PostId}",
                callId,
                postId.Value);
        }
    }

    private async Task HandleTrustedReminderAsync(DeliverTrustedSessionTurn message)
    {
        var ackTarget = Sender;

        if (message.SessionId != _sessionId)
        {
            _log.Warning(
                "Dropping DeliverTrustedSessionTurn with mismatching session id actual={Actual} expected={Expected}",
                message.SessionId.Value, _sessionId.Value);
            ackTarget.Tell(CommandNack.For(_sessionId, "Session id mismatch"));
            return;
        }

        if (_dependencies.IngressGate?.ClosedReason is { } ingressClosedReason)
        {
            _log.Info("Rejecting Mode B reminder while restart drain is active");
            ackTarget.Tell(CommandNack.For(_sessionId, ingressClosedReason));
            return;
        }

        var writer = _handle.InputQueue;
        if (writer is null)
        {
            _log.Warning("Input queue is not initialized; rejecting Mode B reminder");
            ackTarget.Tell(CommandNack.For(_sessionId, "Mattermost session pipeline not initialized"));
            return;
        }

        var input = new ChannelInput
        {
            SenderId = message.Source.SenderId,
            ChannelId = _channelId.Value,
            MessageId = message.Source.MessageId,
            Audience = message.Source.Audience,
            Boundary = message.Source.Boundary,
            Principal = message.Source.Principal,
            Provenance = message.Source.Provenance,
            Contents = [new TextContent(message.Content)],
            ReceivedAt = _dependencies.TimeProvider.GetUtcNow(),
            DefaultDeliveryTarget = BuildDefaultDeliveryTarget(),
            RequestedDeliveryTarget = message.Source.RequestedDeliveryTarget,
            ReminderId = message.Source.ReminderId,
            AckTarget = ackTarget
        };

        // Only delivery-gated (DeliveryRequired) reminders carry a
        // DeliveryObserver. Key it by the per-fire reminder delivery id so a
        // second concurrent reminder to this session can't overwrite the
        // first's observer before its turn reaches TurnCompleted.
        if (message.Source.DeliveryObserver is { } deliveryObserver
            && message.Source.ReminderId is { } reminderKey
            && !string.IsNullOrWhiteSpace(reminderKey.Value))
            _reminderDeliveryObservers[reminderKey] = deliveryObserver;

        try
        {
            using var writeCts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await writer.WriteAsync(input, writeCts.Token);
            _log.Debug(
                "reminder_mode_b_dispatch session={Session} reminder={Reminder}",
                _sessionId.Value, message.Source.ReminderId);
        }
        catch (OperationCanceledException)
        {
            _log.Warning("Timed out enqueueing Mode B reminder for session {0}", _sessionId.Value);
            ackTarget.Tell(CommandNack.For(_sessionId, "Pipeline enqueue timeout"));
        }
        catch (ChannelClosedException)
        {
            _log.Warning("Input queue closed; rejecting Mode B reminder for session {0}", _sessionId.Value);
            ackTarget.Tell(CommandNack.For(_sessionId, "Pipeline input queue closed"));
        }
    }

    private ChannelDeliveryTargetInfo BuildDefaultDeliveryTarget()
        => new(
            ChannelType.Mattermost.ToWireValue(),
            "destination",
            _channelId.Value,
            _channelId.Value,
            _rootPostId.Value);

    private async Task HandleOutputReceivedAsync(OutputReceived msg)
    {
        switch (msg.Output)
        {
            case TextOutput textOutput:
                if (await SafeReplyAsync(textOutput.Text))
                    _deliveredThisTurn = true;
                else
                    _postFailedThisTurn = true;
                break;

            case ErrorOutput error:
                if (await SafeReplyAsync($":warning: {error.Message}"))
                    _deliveredThisTurn = true;
                else
                    _postFailedThisTurn = true;
                break;

            case FileOutput file:
                if (await SafeUploadFileAsync(file))
                    _deliveredThisTurn = true;
                else
                    _postFailedThisTurn = true;
                break;

            case ToolInteractionRequest request when string.Equals(request.Kind, "approval", StringComparison.OrdinalIgnoreCase):
                _hasObservedApprovalRequest = true;
                var pendingApproval = new PendingApprovalRequest(request);
                _pendingApprovalRequests.Add(pendingApproval);

                var promptPostId = await SafeReplyWithApprovalPromptAsync(request);
                if (promptPostId is not null)
                {
                    pendingApproval.PromptPostId = promptPostId;
                    Persist(new PendingApprovalPromptTracked
                    {
                        CallId = pendingApproval.CallId.Value,
                        RequesterSenderId = pendingApproval.RequesterSenderId,
                        RequesterPrincipal = pendingApproval.RequesterPrincipal,
                        OptionKeys = pendingApproval.OptionKeys,
                        PromptId = promptPostId.Value.Value,
                        ToolName = pendingApproval.ToolName,
                        // Preserve null-vs-set semantics on the wire: Truncate
                        // returns string.Empty for null input, which would round-
                        // trip as DisplayText="" with HasDisplayText=true.
                        DisplayText = string.IsNullOrEmpty(pendingApproval.DisplayText)
                            ? null
                            : ApprovalDisplayTextFormatter.Truncate(
                                pendingApproval.DisplayText,
                                PendingApprovalPromptTracked.MaxPersistedDisplayTextChars)
                    }, ApplyPendingApprovalPromptTracked);
                }
                else
                {
                    _pendingApprovalRequests.Remove(pendingApproval);
                }
                break;

            // Mattermost threads don't support renaming, so SessionTitleOutput is ignored.

            case TurnCompleted completed:
                if (completed.Outcome == TurnOutcome.Completed && _pendingCursorPostId is { } pendingCursor)
                    AdvanceCursor(pendingCursor);
                _pendingCursorPostId = null;
                _turnInFlight = false;

                if (completed.SourceReminderId is { } sourceReminderKey
                    && !string.IsNullOrWhiteSpace(sourceReminderKey.Value)
                    && _reminderDeliveryObservers.Remove(sourceReminderKey, out var reminderObserver))
                {
                    reminderObserver.Tell(new ReminderDeliveryResult(
                        sourceReminderKey,
                        ChannelType.Mattermost,
                        Delivered: _deliveredThisTurn,
                        FailureReason: _deliveredThisTurn ? null : "Mattermost post did not succeed",
                        ObservedAtMs: completed.TimestampMs));
                }

                // Only post the empty-turn fallback when the turn genuinely
                // produced nothing. A failed post already notified the session
                // (SafeReplyAsync -> NotifyDeliveryFailedAsync); posting "I
                // didn't manage to produce a reply" on top would be misleading
                // (a reply WAS produced) and double up with the redelivered one.
                if (!_deliveredThisTurn && !_postFailedThisTurn)
                    await SafeReplyAsync(EmptyTurnFallbackText);

                _turnNumber = completed.TurnNumber;
                var clearedPrompts = _pendingApprovalRequests
                    .Select(pending => new PendingApprovalPromptCleared
                    {
                        CallId = pending.CallId.Value
                    })
                    .ToArray();
                if (clearedPrompts.Length > 0)
                {
                    PersistAll(
                        clearedPrompts,
                        cleared => ApplyPendingApprovalPromptCleared(cleared));
                }
                _pendingApprovalRequests.Clear();
                _deliveredThisTurn = false;
                _postFailedThisTurn = false;
                break;
        }
    }

    private async Task<MattermostPostId?> SafeReplyWithApprovalPromptAsync(ToolInteractionRequest request)
    {
        var callbackUrl = _dependencies.CallbackUrl;

        if (!string.IsNullOrEmpty(callbackUrl))
        {
            return await TryPostButtonPromptAsync(request, callbackUrl);
        }

        return await TryPostTextPromptAsync(request);
    }

    private async Task<MattermostPostId?> TryPostButtonPromptAsync(
        ToolInteractionRequest request,
        string callbackUrl)
    {
        var promptCorrelationId = Guid.NewGuid().ToString("N");
        var (promptText, attachments) = MattermostApprovalPromptBuilder.BuildButtonPrompt(
            request,
            callbackUrl,
            _channelId.Value,
            _rootPostId.Value,
            promptCorrelationId,
            _dependencies.CallbackActionStore);
        var startedAt = _dependencies.TimeProvider.GetTimestamp();
        try
        {
            var postMessage = BuildPostMessage(promptText, attachments: attachments);
            var result = await _dependencies.ReplyClient.PostReplyAsync(postMessage);
            var duration = _dependencies.TimeProvider.GetElapsedTime(startedAt).TotalMilliseconds;
            ChannelTelemetry.For(ChannelType.Mattermost).RecordReplyPosted(duration);
            ChannelTelemetry.For(ChannelType.Mattermost).RecordExtra("approvalFallbackActivated", "button_prompt");
            // Bind the actual prompt post ID to the action tokens we just minted so
            // the callback endpoint can verify payload.PostId on the way back in.
            // Closes the forgery vector tracked under #939's review. The action
            // store is null in text-only mode (no callback URL); this branch isn't
            // reachable in that case.
            if (result.PostId is { } postId && _dependencies.CallbackActionStore is { } store)
            {
                store.AssociatePromptPostId(promptCorrelationId, postId.Value);
            }
            return result.PostId;
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "Failed posting Mattermost button prompt; falling back to text-only");
            ChannelTelemetry.For(ChannelType.Mattermost).RecordExtra("approvalFallbackActivated", "text_prompt");
            return await TryPostTextPromptAsync(request);
        }
    }

    private async Task<MattermostPostId?> TryPostTextPromptAsync(ToolInteractionRequest request)
    {
        var promptText = MattermostApprovalPromptBuilder.BuildTextPrompt(request);
        var startedAt = _dependencies.TimeProvider.GetTimestamp();
        try
        {
            var postMessage = BuildPostMessage(promptText);
            var result = await _dependencies.ReplyClient.PostReplyAsync(postMessage);
            var duration = _dependencies.TimeProvider.GetElapsedTime(startedAt).TotalMilliseconds;
            ChannelTelemetry.For(ChannelType.Mattermost).RecordReplyPosted(duration);
            return result.PostId;
        }
        catch (Exception ex)
        {
            _log.Error(ex, "Failed posting Mattermost approval prompt; auto-denying request");
            ChannelTelemetry.For(ChannelType.Mattermost).RecordExtra("approvalFallbackActivated", "auto_deny");
            await SendApprovalDenyOnFailureAsync(request.CallId);
            return null;
        }
    }

    /// <summary>
    /// Posts <paramref name="text"/> (chunked) to Mattermost. Returns true only
    /// when every chunk posted successfully; false if any chunk failed (after
    /// notifying the session of the delivery failure). Callers that gate
    /// reminder delivery confirmation on real delivery must honor the result.
    /// </summary>
    private async Task<bool> SafeReplyAsync(string text)
    {
        var chunks = ChunkMessage(text);
        foreach (var chunk in chunks)
        {
            var startedAt = _dependencies.TimeProvider.GetTimestamp();
            try
            {
                var postMessage = BuildPostMessage(chunk);
                await _dependencies.ReplyClient.PostReplyAsync(postMessage);
                var duration = _dependencies.TimeProvider.GetElapsedTime(startedAt).TotalMilliseconds;
                ChannelTelemetry.For(ChannelType.Mattermost).RecordReplyPosted(duration);
            }
            catch (Exception ex)
            {
                var duration = _dependencies.TimeProvider.GetElapsedTime(startedAt).TotalMilliseconds;
                _log.Warning(ex, "Failed posting Mattermost reply for session {0}", _sessionId.Value);
                ChannelTelemetry.For(ChannelType.Mattermost).RecordReplyFailed(duration);
                await NotifyDeliveryFailedAsync(DeliveryFailureKind.TransportFailure, ex.Message);
                return false;
            }
        }

        return true;
    }

    private MattermostPostMessage BuildPostMessage(
        string text,
        IReadOnlyList<string>? fileIds = null,
        IReadOnlyList<MattermostAttachment>? attachments = null)
        => new(
            ChannelId: _channelId,
            Text: text,
            RootPostId: _rootPostId.IsEmpty ? null : new MattermostPostId(_rootPostId.Value),
            FileIds: fileIds,
            Attachments: attachments);

    private async Task<bool> SafeUploadFileAsync(FileOutput file)
    {
        var startedAt = _dependencies.TimeProvider.GetTimestamp();
        try
        {
            if (!File.Exists(file.FilePath))
            {
                _log.Warning("File not found for upload: {Path}", file.FilePath);
                await NotifyDeliveryFailedAsync(DeliveryFailureKind.Unknown, $"File not found for upload: {file.FilePath}");
                return false;
            }

            using var uploadCts = new CancellationTokenSource(OperationTimeout);
            var fileId = await _dependencies.ReplyClient.UploadFileAsync(
                _channelId,
                file.FilePath,
                file.FileName,
                uploadCts.Token);

            using var postCts = new CancellationTokenSource(OperationTimeout);
            var postMessage = BuildPostMessage($":paperclip: {file.FileName}", fileIds: [fileId]);
            await _dependencies.ReplyClient.PostReplyAsync(postMessage, postCts.Token);

            var duration = _dependencies.TimeProvider.GetElapsedTime(startedAt).TotalMilliseconds;
            ChannelTelemetry.For(ChannelType.Mattermost).RecordReplyPosted(duration);
            _log.Info("Uploaded file to Mattermost thread: {FileName}", file.FileName);
            return true;
        }
        catch (OperationCanceledException ex)
        {
            var duration = _dependencies.TimeProvider.GetElapsedTime(startedAt).TotalMilliseconds;
            _log.Error(ex, "Timed out uploading file {FileName} to Mattermost thread", file.FileName);
            ChannelTelemetry.For(ChannelType.Mattermost).RecordReplyFailed(duration);
            await NotifyDeliveryFailedAsync(DeliveryFailureKind.TransportFailure, ex.Message);
            return false;
        }
        catch (Exception ex)
        {
            var duration = _dependencies.TimeProvider.GetElapsedTime(startedAt).TotalMilliseconds;
            _log.Error(ex, "Failed to upload file {FileName} to Mattermost thread", file.FileName);
            ChannelTelemetry.For(ChannelType.Mattermost).RecordReplyFailed(duration);
            await NotifyDeliveryFailedAsync(DeliveryFailureKind.TransportFailure, ex.Message);
            return false;
        }
    }

    /// <summary>
    /// Tells every in-flight reminder observer that delivery did not happen,
    /// then clears them. Called when a turn can no longer reach TurnCompleted
    /// (e.g. pipeline reinit), so the execution actor fails fast and redelivers
    /// rather than waiting out its backstop timeout.
    /// </summary>
    private void FailPendingReminderDeliveries(string reason)
    {
        if (_reminderDeliveryObservers.Count == 0)
            return;

        foreach (var (key, observer) in _reminderDeliveryObservers)
            observer.Tell(new ReminderDeliveryResult(key, ChannelType.Mattermost, Delivered: false, FailureReason: reason));

        _reminderDeliveryObservers.Clear();
    }

    private async Task NotifyDeliveryFailedAsync(DeliveryFailureKind failureKind, string errorMessage)
    {
        try
        {
            await _dependencies.Pipeline.SendFeedbackAsync(new DeliveryFailed
            {
                SessionId = _sessionId,
                TurnNumber = _turnNumber,
                ChannelType = ChannelType.Mattermost,
                FailureKind = failureKind,
                ErrorMessage = errorMessage
            });
        }
        catch (Exception ex)
        {
            // A dead feedback pipe means the session never learns the turn
            // failed. Rethrow so supervision restarts the actor and
            // re-creates the pipeline, same as the Slack binding actor.
            _log.Error(ex, "Failed to send delivery feedback to session; propagating to trigger pipeline reinit");
            throw;
        }
    }

    private async Task SendApprovalDenyOnFailureAsync(ToolCallId callId)
    {
        var pending = _pendingApprovalRequests.LastOrDefault(p =>
            p.CallId == callId);
        if (pending is not null)
            _pendingApprovalRequests.Remove(pending);

        try
        {
            await _dependencies.Pipeline.SendFeedbackAsync(new ToolInteractionResponse
            {
                SessionId = _sessionId,
                CallId = callId,
                SelectedKey = ApprovalOptionKeys.DenyKey,
                SenderId = new SenderId("system")
            });
        }
        catch (Exception ex)
        {
            _log.Error(ex, "Failed to send auto-deny feedback for call {CallId}", callId);
        }
    }

    private void ReplyIfExpected(IActorRef replyTo, object response)
    {
        if (replyTo.IsNobody() || Equals(replyTo, ActorRefs.Nobody) || Equals(replyTo, Context.System.DeadLetters))
            return;

        replyTo.Tell(response);
    }

    private static readonly TimeSpan OperationTimeout = TimeSpan.FromSeconds(10);

    private async Task ProcessInboundAttachmentsAsync(
        IReadOnlyList<MattermostFileReference> files,
        TrustAudience audience,
        List<AIContent> contents,
        CancellationToken cancellationToken)
    {
        if (_dependencies.HttpClient is null)
        {
            _log.Warning(
                "Mattermost HTTP client is not configured; rejecting {Count} inbound attachment(s)",
                files.Count);
            await SafeReplyAsync(":warning: I can't download attachments right now -- HTTP client is not configured.");
            return;
        }

        var profile = ToolAudienceProfileDefaults.GetResolvedProfile(_dependencies.AudienceProfiles, audience);
        var policy = profile.ChannelAttachments ?? ChannelAttachmentPolicy.Empty;

        if (files.Count > policy.MaxFilesPerMessage)
        {
            _log.Warning(
                "attachments_rejected count={Count} limit={Limit} audience={Audience} reason=too-many-files",
                files.Count,
                policy.MaxFilesPerMessage,
                audience);
            await SafeReplyAsync(
                $":warning: I can only accept up to {policy.MaxFilesPerMessage} attachments per message. " +
                "Please split your upload and try again. Text content was delivered.");
            return;
        }

        var modelCapabilities = _dependencies.ModelCapabilities;
        var inlineImages = modelCapabilities.InputModalities.HasFlag(ModelModality.Image);

        var acceptedLines = new List<string>(files.Count);
        var dataContents = new List<DataContent>();
        var rejections = new List<string>();

        var inboxDir = SessionDirectoryHelper.GetOrCreateInboxDirectory(_sessionId, _dependencies.Paths.SessionsDirectory);
        var stagingDir = SessionDirectoryHelper.GetOrCreateAttachmentStagingDirectory(_sessionId, _dependencies.Paths.SessionsDirectory);

        foreach (var file in files)
        {
            var attachmentResult = await TryIngestSingleAttachmentAsync(
                file, audience, policy, inlineImages, inboxDir, stagingDir, cancellationToken);

            switch (attachmentResult)
            {
                case AttachmentIngestOutcome.Accepted accepted:
                    acceptedLines.Add(accepted.Line);
                    if (accepted.Inline is { } inline)
                        dataContents.Add(inline);
                    break;

                case AttachmentIngestOutcome.Rejected rejected:
                    rejections.Add(rejected.UserFacingReason);
                    break;
            }
        }

        if (acceptedLines.Count > 0)
        {
            contents.Add(new TextContent(string.Join('\n', acceptedLines)));
            contents.AddRange(dataContents);
        }

        if (rejections.Count > 0)
        {
            var joined = rejections.Count == 1
                ? rejections[0]
                : ":warning: Some attachments were not accepted:\n  - " + string.Join("\n  - ", rejections);
            await SafeReplyAsync(joined);
        }
    }

    private Task<AttachmentIngestOutcome> TryIngestSingleAttachmentAsync(
        MattermostFileReference file,
        TrustAudience audience,
        ChannelAttachmentPolicy policy,
        bool inlineImages,
        string inboxDir,
        string stagingDir,
        CancellationToken cancellationToken)
        => AttachmentIngressPipeline.IngestAsync(
            new AttachmentIngressRequest(file.Name, file.MimeType, file.Size),
            audience,
            policy,
            inlineImages,
            inboxDir,
            stagingDir,
            OperationTimeout,
            _dependencies.ContentScanner,
            _log,
            (staging, maxBytes, ct) => StreamingAttachmentDownloader.DownloadToFileAsync(
                _dependencies.HttpClient!, file.Url, configureRequest: null,
                staging, maxBytes, ct,
                (ex, path) => _log.Error(ex, "Failed to clean up staged download file {0}", path)),
            cancellationToken,
            // Mattermost attachment URLs must originate from the configured server.
            preDownloadGate: () =>
            {
                if (string.IsNullOrEmpty(_dependencies.ServerUrl))
                {
                    _log.Warning(
                        "attachment_rejected name={Name} reason=no-server-url-configured",
                        file.Name);
                    return $"`{file.Name}` was rejected because no Mattermost server URL is configured for URL trust validation.";
                }

                if (!MattermostAttachmentUrlTrust.IsAllowedAttachmentUrl(file.Url, _dependencies.ServerUrl))
                {
                    _log.Warning(
                        "attachment_rejected name={Name} url={Url} reason=untrusted-url",
                        file.Name, file.Url);
                    return $"`{file.Name}` has an untrusted URL and was skipped.";
                }

                return null;
            });

    internal static List<string> ChunkMessage(string text) =>
        MessageChunker.Chunk(text, MaxMattermostPostLength);

    /// <summary>
    /// Records that a turn went into the input queue. The turn-in-flight flag
    /// guards the mention re-arm path; the pending cursor keeps the highest
    /// post id of the turn and only becomes the persisted cursor on
    /// TurnCompleted.
    /// </summary>
    private void AdvancePendingCursorForEnqueuedTurn(string? candidatePostId)
    {
        _turnInFlight = true;

        if (candidatePostId is null)
            return;

        if (_pendingCursorPostId is null
            || string.CompareOrdinal(candidatePostId, _pendingCursorPostId) > 0)
            _pendingCursorPostId = candidatePostId;
    }

    private void AdvanceCursor(string candidatePostId)
    {
        if (_cursorPostId is not null && string.CompareOrdinal(candidatePostId, _cursorPostId) <= 0)
        {
            _log.Debug("Session cursor did not advance session={Session} postId={PostId}",
                _sessionId.Value, candidatePostId);
            return;
        }

        Persist(new CursorAdvanced(candidatePostId), ApplyCursorAdvanced);
    }

    private void ApplyCursorAdvanced(CursorAdvanced advanced)
    {
        _cursorPostId = advanced.Cursor;

        if (!IsRecovering && LastSequenceNr > 1 && LastSequenceNr % 10 == 0)
            DeleteMessages(LastSequenceNr - 1);
    }

    private void ApplyPendingApprovalPromptTracked(PendingApprovalPromptTracked tracked)
    {
        _hasObservedApprovalRequest = true;
        PendingApprovalRecovery.ApplyTracked<PendingApprovalRequest, MattermostPostId>(
            _pendingApprovalRequests,
            tracked,
            wrapPromptId: value => new MattermostPostId(value),
            createRequest: (callId, requesterSenderId, requesterPrincipal, optionKeys, promptId, toolName, displayText) =>
                new PendingApprovalRequest(callId, requesterSenderId, requesterPrincipal, optionKeys, promptId, toolName, displayText));
    }

    private void ApplyPendingApprovalPromptCleared(PendingApprovalPromptCleared cleared)
        => PendingApprovalRecovery.ApplyCleared<PendingApprovalRequest, MattermostPostId>(_pendingApprovalRequests, cleared);

    private sealed record InitializePipeline
    {
        public static readonly InitializePipeline Instance = new();
    }

    private sealed record PerformHydration
    {
        public static readonly PerformHydration Instance = new();
    }

    private sealed record OutputReceived(SessionOutput Output);

    private sealed record OutputStreamTerminated(int Generation, Exception? Cause);

    private sealed record ReinitializePipeline(string Reason);
}
