// -----------------------------------------------------------------------
// <copyright file="DiscordSessionBindingActor.cs" company="Petabridge, LLC">
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

namespace Netclaw.Channels.Discord;

internal sealed class DiscordSessionBindingActor : ReceivePersistentActor, IWithTimers
{
    private readonly SessionId _sessionId;
    private readonly DiscordChannelId _channelId;
    private DiscordReplyChannelId _replyChannelId;
    private readonly DiscordThreadOrMessageId _threadOrMessageId;
    private DiscordMessageId? _rootMessageId;
    private bool _threadCreated;
    private const int MaxDiscordMessageLength = 2000;
    private const string EmptyTurnFallbackText =
        ":warning: I didn't manage to produce a reply. Please try rephrasing or sending your message again.";
    private const string LiveInjectionBlockedWarning =
        ":warning: Message blocked by prompt-injection policy.";
    private const string LiveDetectorUnavailableWarning =
        ":warning: I couldn't safely analyze your message — please try again in a moment.";
    private const string WrongRequesterWarning =
        ":warning: Only the requesting user can approve this tool action.";
    private const string BackfillDetectorWarning =
        ":warning: I couldn't safely analyze some earlier thread messages, so they were excluded from context.";

    private readonly DiscordGatewayDependencies _dependencies;
    private readonly IPromptInjectionDetector _promptInjectionDetector;
    private readonly SessionPipelineHandle _handle;
    private readonly ILoggingAdapter _log;

    // Null when the gateway supplies no thread-history fetcher. That is a real
    // runtime state (an instance without history access), not a disabled check:
    // with no fetcher there is no gap to hydrate, so both hydration paths no-op.
    private readonly ThreadGapHydrationEngine? _hydrationEngine;
    private readonly List<PendingApprovalRequest> _pendingApprovalRequests = [];
    private readonly ApprovalResponseFlow<PendingApprovalRequest, DiscordMessageId> _approvalFlow;

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
    private Netclaw.Actors.Protocol.TurnNumber _turnNumber;
    private string? _lastSetThreadName;
    // Snowflake cursors in canonical decimal string form, which is also the
    // persisted CursorAdvanced form. NormalizeSnowflake produces every value,
    // so CursorComparer can order them.
    private static readonly SnowflakeCursorComparer CursorComparer = SnowflakeCursorComparer.Instance;
    private string? _cursor;
    private string? _pendingCursor;

    // Reliable in-flight-turn signal for the mention re-arm guard: set when the
    // main inbound is enqueued (unlike _pendingCursor, which is only set
    // for inbounds with a parseable snowflake), cleared when the turn ends or the
    // pipeline reinitializes. Without it, a null-snowflake in-flight turn would
    // leave _pendingCursor unset and let a following mention re-arm
    // mid-turn — the PR #733 no-duplicate hazard.
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

    public DiscordSessionBindingActor(
        SessionId sessionId,
        DiscordChannelId channelId,
        DiscordReplyChannelId replyChannelId,
        DiscordThreadOrMessageId threadOrMessageId,
        DiscordMessageId? rootMessageId,
        DiscordGatewayDependencies dependencies)
    {
        _sessionId = sessionId;
        _channelId = channelId;
        _replyChannelId = replyChannelId;
        _threadOrMessageId = threadOrMessageId;
        _rootMessageId = rootMessageId;
        _dependencies = dependencies;
        // Fail loud rather than substituting a no-op detector — a no-op reports
        // every input as safe, silently disabling injection scanning. A null
        // here means broken gateway wiring.
        _promptInjectionDetector = dependencies.PromptInjectionDetector
            ?? throw new InvalidOperationException(
                "DiscordGatewayDependencies.PromptInjectionDetector is not wired; "
                + "prompt-injection scanning cannot be silently disabled.");

        _log = Context.GetLogger()
            .WithContext("Adapter", "discord")
            .WithContext(NetclawLogProperties.SessionId, _sessionId.Value)
            .WithContext("DiscordChannelId", _channelId.Value)
            .WithContext("DiscordThreadOrMessageId", _threadOrMessageId.Value);

        _handle = new SessionPipelineHandle(_dependencies.Pipeline, _log, "discord-session");

        if (_dependencies.ThreadHistoryFetcher is { } historyFetcher)
        {
            _hydrationEngine = new ThreadGapHydrationEngine(
                sessionId: _sessionId,
                channelType: ChannelType.Discord,
                historyFetcher: historyFetcher,
                injectionDetector: _promptInjectionDetector,
                classifierSourceContext: "discord-backfill",
                cursorComparer: CursorComparer,
                cursorKeySelector: NormalizeSnowflake,
                isAuthorizedSender: IsAuthorizedSender,
                log: _log,
                readCursor: () => _cursor,
                readInputQueue: () => _handle.InputQueue,
                readIngressClosedReason: () => _dependencies.IngressGate?.ClosedReason,
                warnBackfillDetectorUnavailableAsync: () => SafeReplyAsync(BackfillDetectorWarning),
                onBackfillEnqueued: AdvancePendingCursorForEnqueuedTurn,
                setHydrationPending: pending => _hydrationPending = pending);
        }

        _approvalFlow = new ApprovalResponseFlow<PendingApprovalRequest, DiscordMessageId>(
            sessionId: _sessionId,
            channelType: ChannelType.Discord,
            channelName: "Discord",
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
        // After journal replay completes, queue a one-shot hydration. Recovery
        // can beat pipeline initialization on slower dispatchers; Initializing
        // unstashes after switching to Hydrating so the hydration trigger cannot
        // strand the actor in startup.
        Recover<RecoveryCompleted>(_ => Self.Tell(PerformHydration.Instance));

        Initializing();
    }

    public override string PersistenceId => $"discord-session-cursor-{Uri.EscapeDataString(_sessionId.Value)}";

    public static Props CreateProps(
        SessionId sessionId,
        DiscordChannelId channelId,
        DiscordReplyChannelId replyChannelId,
        DiscordThreadOrMessageId threadOrMessageId,
        DiscordMessageId? rootMessageId,
        DiscordGatewayDependencies dependencies)
        => Props.Create(() => new DiscordSessionBindingActor(
            sessionId,
            channelId,
            replyChannelId,
            threadOrMessageId,
            rootMessageId,
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
        ChannelType = ChannelType.Discord,
        Filter = OutputFilter.Text | OutputFilter.Files | OutputFilter.ProcessingState
    };

    private void Initializing()
    {
        CommandAsync<InitializePipeline>(async _ =>
        {
            try
            {
                await EnsureInitializedAsync();
                Become(Hydrating);
                // RecoveryCompleted can be stashed while pipeline initialization
                // is still running. Move it into Hydrating; live inbounds are
                // re-stashed there until hydration finishes.
                Stash.UnstashAll();
            }
            catch (Exception ex)
            {
                _log.Error(ex, "Failed to initialize Discord session pipeline; stopping actor");
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
        CommandAsync<DiscordThreadInbound>(HandleInboundAsync);
        CommandAsync<DiscordApprovalResponse>(HandleApprovalResponseAsync);
        CommandAsync<DeliverTrustedSessionTurn>(HandleTrustedReminderAsync);
        CommandAsync<StartProactiveThread>(HandleProactiveThreadAsync);
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
            FailPendingReminderDeliveries($"Discord pipeline reinitialized: {msg.Reason}");
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
            RunTask(async () =>
            {
                await _handle.DrainAsync();
                Context.Stop(Self);
            });
        });

        Context.SetReceiveTimeout(IdlePassivationTimeout);
    }

    /// <summary>
    /// Wires a proactively-created thread: ensures the session pipeline is
    /// initialized and acknowledges. The thread root (the bot's posted message)
    /// is recovered as adopted context on the first authorized reply via the
    /// deferred re-armed hydration path — see <see cref="PerformOneShotHydrationAsync"/>.
    /// </summary>
    private async Task HandleProactiveThreadAsync(StartProactiveThread message)
    {
        _replyChannelId = message.ReplyChannelId;
        _threadCreated = message.DirectMessageUserId is not null || message.RootMessageId is null;
        _rootMessageId = message.RootMessageId;

        _log.Info("Initializing proactive thread pipeline for session {0}", message.SessionId.Value);
        await EnsureInitializedAsync();
        Sender.Tell(new ProactiveThreadAck(message.SessionId));
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

    private async Task HandleInboundAsync(DiscordThreadInbound message)
    {
        if (_dependencies.IngressGate?.ClosedReason is { } ingressClosedReason)
        {
            _log.Info("Rejecting Discord inbound message while restart drain is active");
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
                _promptInjectionDetector, message.Text, "discord-live", _log, inboundCts.Token);
            switch (classification.Outcome)
            {
                case ClassificationOutcome.Block:
                    _log.Warning("Blocked Discord message due to prompt injection risk: {Reason}", classification.Reason);
                    ChannelTelemetry.For(ChannelType.Discord).RecordEventDropped("prompt_injection_high");
                    await SafeReplyAsync(LiveInjectionBlockedWarning);
                    return;

                case ClassificationOutcome.DetectorUnavailable:
                    _log.Warning("Prompt injection detector unavailable for live message — dropping");
                    ChannelTelemetry.For(ChannelType.Discord).RecordEventDropped("prompt_injection_detector_unavailable");
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
            SenderId = new Netclaw.Actors.Protocol.SenderId(message.SenderId.Value),
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
            ChannelTelemetry.For(ChannelType.Discord).RecordMessageEnqueued();

            AdvancePendingCursorForEnqueuedTurn(NormalizeSnowflake(message.EventId.Value));
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

    // Discord authorization basis for adopted-context: an empty AllowedUserIds
    // list means the instance is unrestricted; otherwise the sender must be listed.
    private bool IsAuthorizedSender(string senderId)
        => _dependencies.Options.AllowedUserIds.Length == 0
            || _dependencies.Options.AllowedUserIds.Contains(senderId, StringComparer.Ordinal);

    private Task<bool> TryHandleTextApprovalResponseAsync(DiscordThreadInbound message)
        => _approvalFlow.TryHandleTextApprovalResponseAsync(message.Text, message.SenderId.Value);

    // Discord gateway interactions are one-way telemetry from the websocket, so
    // the flow gets no synchronous-reply hook here.
    private Task HandleApprovalResponseAsync(DiscordApprovalResponse message)
        => _approvalFlow.HandleApprovalResponseAsync(
            message.CallId,
            message.SelectedKey,
            message.SenderId.Value,
            message.PromptMessageId);

    private async Task TryResolveApprovalPromptAsync(
        DiscordMessageId? promptMessageId,
        ToolInteractionRequest? request,
        Netclaw.Tools.ToolCallId callId,
        string selectedKey,
        string senderId,
        string? persistedToolName = null,
        string? persistedDisplayText = null)
    {
        if (promptMessageId is not { } messageId)
            return;

        try
        {
            // Hot path uses the in-memory request. Cold-spawn path uses the persisted
            // tool name + display text when present (PendingApprovalPromptTracked
            // carried them); legacy journals without the fields fall back to the
            // generic banner.
            var resolvedText = request is not null
                ? DiscordApprovalPromptBuilder.BuildResolvedPromptText(request, selectedKey, senderId)
                : DiscordApprovalPromptBuilder.BuildResolvedPromptTextWithoutRequest(
                    selectedKey,
                    senderId,
                    toolName: persistedToolName,
                    displayText: persistedDisplayText);

            using var cts = new CancellationTokenSource(OperationTimeout);
            await _dependencies.ReplyClient.UpdateMessageAsync(
                _replyChannelId,
                messageId,
                resolvedText,
                removeComponents: true,
                cts.Token);
        }
        catch (Exception ex)
        {
            _log.Warning(
                ex,
                "Failed to update resolved approval prompt for call {CallId} messageId={MessageId}",
                callId,
                messageId.Value);
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
            ackTarget.Tell(CommandNack.For(_sessionId, "Discord session pipeline not initialized"));
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
            ChannelType.Discord.ToWireValue(),
            "destination",
            _channelId.Value,
            _channelId.Value,
            _threadOrMessageId.Value);

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

            case ProcessingStateOutput processing:
                await RenderProcessingStateAsync(processing);
                break;

            case ToolInteractionRequest request when string.Equals(request.Kind, "approval", StringComparison.OrdinalIgnoreCase):
                _hasObservedApprovalRequest = true;
                var pendingApproval = new PendingApprovalRequest(request);
                _pendingApprovalRequests.Add(pendingApproval);

                var promptMessageId = await SafeReplyWithButtonsAsync(request);
                if (promptMessageId is not null)
                {
                    pendingApproval.PromptMessageId = promptMessageId;
                    Persist(new PendingApprovalPromptTracked
                    {
                        CallId = pendingApproval.CallId.Value,
                        RequesterSenderId = pendingApproval.RequesterSenderId,
                        RequesterPrincipal = pendingApproval.RequesterPrincipal,
                        OptionKeys = pendingApproval.OptionKeys,
                        PromptId = promptMessageId.Value.Value,
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

            case SessionTitleOutput titleOutput:
                if (_threadCreated && titleOutput.Title != _lastSetThreadName)
                    await SafeSetThreadNameAsync(titleOutput.Title);
                break;

            case TurnCompleted completed:
                if (completed.Outcome == TurnOutcome.Completed && _pendingCursor is { } pendingCursor)
                    AdvanceCursor(pendingCursor);
                _pendingCursor = null;
                _turnInFlight = false;

                if (completed.SourceReminderId is { } sourceReminderKey
                    && !string.IsNullOrWhiteSpace(sourceReminderKey.Value)
                    && _reminderDeliveryObservers.Remove(sourceReminderKey, out var reminderObserver))
                {
                    reminderObserver.Tell(new ReminderDeliveryResult(
                        sourceReminderKey,
                        ChannelType.Discord,
                        Delivered: _deliveredThisTurn,
                        FailureReason: _deliveredThisTurn ? null : "Discord post did not succeed",
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

    private async Task RenderProcessingStateAsync(ProcessingStateOutput output)
    {
        var requirement = output.IsRequired
            ? ChannelOutputRequirement.Required
            : ChannelOutputRequirement.Optional;
        var request = new ChannelOutputRenderRequest(
            BuildOutputRenderTarget(),
            output,
            ChannelOutputEffectKind.ProcessingIndicator,
            requirement);

        try
        {
            await _dependencies.ChannelRegistry.RenderOutputAsync(request);
        }
        catch (Exception ex) when (!output.IsRequired)
        {
            _log.Warning(ex, "Failed rendering optional Discord processing indicator");
        }
    }

    private ChannelDeliveryTarget BuildOutputRenderTarget()
    {
        var channelKey = ChannelDescriptorKey.FromChannelType(ChannelType.Discord);
        return new ChannelDeliveryTarget(
            channelKey,
            new ResolvedChannelAddress(
                channelKey,
                ChannelAddressKind.Destination,
                _replyChannelId.Value,
                _replyChannelId.Value),
            _threadOrMessageId.Value);
    }

    private async Task<DiscordMessageId?> SafeReplyWithButtonsAsync(ToolInteractionRequest request)
    {
        var (promptText, buttons) = DiscordApprovalPromptBuilder.BuildButtonPrompt(request);
        var startedAt = _dependencies.TimeProvider.GetTimestamp();
        try
        {
            var postMessage = BuildPostMessage(promptText, buttons: buttons);
            var result = await _dependencies.ReplyClient.PostReplyAsync(postMessage);
            ApplyThreadPromotion(result);
            var duration = _dependencies.TimeProvider.GetElapsedTime(startedAt).TotalMilliseconds;
            ChannelTelemetry.For(ChannelType.Discord).RecordReplyPosted(duration);
            ChannelTelemetry.For(ChannelType.Discord).RecordExtra("approvalFallbackActivated", "button_prompt");
            return result.MessageId;
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "Failed posting Discord button prompt; falling back to text-only");
            ChannelTelemetry.For(ChannelType.Discord).RecordExtra("approvalFallbackActivated", "text_prompt");
            try
            {
                var fallbackText = DiscordApprovalPromptBuilder.BuildTextPrompt(request);
                var postMessage = BuildPostMessage(fallbackText);
                var result = await _dependencies.ReplyClient.PostReplyAsync(postMessage);
                ApplyThreadPromotion(result);
                return result.MessageId;
            }
            catch (Exception textEx)
            {
                _log.Error(textEx, "Failed posting text-only approval fallback; auto-denying request");
                await SendApprovalDenyOnFailureAsync(request.CallId);
                return null;
            }
        }
    }

    /// <summary>
    /// Posts <paramref name="text"/> (chunked) to Discord. Returns true only
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
                var result = await _dependencies.ReplyClient.PostReplyAsync(postMessage);
                ApplyThreadPromotion(result);
                var duration = _dependencies.TimeProvider.GetElapsedTime(startedAt).TotalMilliseconds;
                ChannelTelemetry.For(ChannelType.Discord).RecordReplyPosted(duration);
            }
            catch (Exception ex)
            {
                var duration = _dependencies.TimeProvider.GetElapsedTime(startedAt).TotalMilliseconds;
                _log.Warning(ex, "Failed posting Discord reply for session {0}", _sessionId.Value);
                ChannelTelemetry.For(ChannelType.Discord).RecordReplyFailed(duration);
                await NotifyDeliveryFailedAsync(DeliveryFailureKind.TransportFailure, ex.Message);
                return false;
            }
        }

        return true;
    }

    private DiscordPostMessage BuildPostMessage(
        string text,
        IReadOnlyList<DiscordButtonSpec>? buttons = null)
    {
        if (!_threadCreated && _rootMessageId is not null)
        {
            return new DiscordPostMessage(
                ReplyChannelId: _replyChannelId,
                Text: text,
                Buttons: buttons,
                CreateThreadOnMessage: _rootMessageId,
                ThreadName: "Netclaw");
        }

        return new DiscordPostMessage(
            ReplyChannelId: _replyChannelId,
            Text: text,
            Buttons: buttons);
    }

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

            using var cts = new CancellationTokenSource(OperationTimeout);
            await _dependencies.ReplyClient.UploadFileAsync(
                new DiscordFileUpload(
                    _replyChannelId,
                    file.FilePath,
                    file.FileName,
                    $":paperclip: {file.FileName}",
                    _threadCreated ? null : _rootMessageId),
                cts.Token);

            var duration = _dependencies.TimeProvider.GetElapsedTime(startedAt).TotalMilliseconds;
            ChannelTelemetry.For(ChannelType.Discord).RecordReplyPosted(duration);
            _log.Info("Uploaded file to Discord session: {FileName}", file.FileName);
            return true;
        }
        catch (OperationCanceledException ex)
        {
            var duration = _dependencies.TimeProvider.GetElapsedTime(startedAt).TotalMilliseconds;
            _log.Error(ex, "Timed out uploading file {FileName} to Discord session", file.FileName);
            ChannelTelemetry.For(ChannelType.Discord).RecordReplyFailed(duration);
            await NotifyDeliveryFailedAsync(DeliveryFailureKind.TransportFailure, ex.Message);
            return false;
        }
        catch (Exception ex)
        {
            var duration = _dependencies.TimeProvider.GetElapsedTime(startedAt).TotalMilliseconds;
            _log.Error(ex, "Failed to upload file {FileName} to Discord session", file.FileName);
            ChannelTelemetry.For(ChannelType.Discord).RecordReplyFailed(duration);
            await NotifyDeliveryFailedAsync(DeliveryFailureKind.TransportFailure, ex.Message);
            return false;
        }
    }

    private async Task SafeSetThreadNameAsync(string title)
    {
        try
        {
            await _dependencies.ReplyClient.SetThreadNameAsync(_replyChannelId, title);
            _lastSetThreadName = title;
            _log.Debug("Set Discord thread name to '{0}'", title);
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "Failed to set Discord thread name for session {0}", _sessionId.Value);
        }
    }

    private void ApplyThreadPromotion(DiscordPostResult result)
    {
        if (result.CreatedThreadId is { } threadId)
        {
            _replyChannelId = threadId;
            _threadCreated = true;
            _rootMessageId = null;
            _log.Info("Promoted Discord session to thread reply_channel={0}", threadId.Value);
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
            observer.Tell(new ReminderDeliveryResult(key, ChannelType.Discord, Delivered: false, FailureReason: reason));

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
                ChannelType = ChannelType.Discord,
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

    private async Task SendApprovalDenyOnFailureAsync(Netclaw.Tools.ToolCallId callId)
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
                SenderId = new Netclaw.Actors.Protocol.SenderId("system")
            });
        }
        catch (Exception ex)
        {
            _log.Error(ex, "Failed to send auto-deny feedback for call {CallId}", callId);
        }
    }

    private static readonly TimeSpan OperationTimeout = TimeSpan.FromSeconds(10);

    private async Task ProcessInboundAttachmentsAsync(
        IReadOnlyList<DiscordFileReference> files,
        TrustAudience audience,
        List<AIContent> contents,
        CancellationToken cancellationToken)
    {
        if (_dependencies.HttpClient is null)
        {
            _log.Warning(
                "Discord HTTP client is not configured; rejecting {Count} inbound attachment(s)",
                files.Count);
            await SafeReplyAsync(":warning: I can't download attachments right now — HTTP client is not configured.");
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
                $"Please split your upload and try again. Text content was delivered.");
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
        DiscordFileReference file,
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
            preDownloadGate: () =>
            {
                if (DiscordAttachmentUrlTrust.IsAllowedAttachmentDomain(file.Url))
                    return null;
                _log.Warning(
                    "attachment_rejected name={Name} url={Url} reason=untrusted-domain",
                    file.Name, file.Url);
                return $"`{file.Name}` has an untrusted URL domain and was skipped.";
            });

    internal static List<string> ChunkMessage(string text) =>
        MessageChunker.Chunk(text, MaxDiscordMessageLength);

    /// <summary>
    /// Records that a turn went into the input queue. The turn-in-flight flag
    /// guards the mention re-arm path; the pending cursor keeps the highest
    /// snowflake of the turn and only becomes the persisted cursor on
    /// TurnCompleted.
    /// </summary>
    private void AdvancePendingCursorForEnqueuedTurn(string? candidateCursor)
    {
        _turnInFlight = true;

        if (candidateCursor is null)
            return;

        if (_pendingCursor is not { } pending || CursorComparer.Compare(candidateCursor, pending) > 0)
            _pendingCursor = candidateCursor;
    }

    private void AdvanceCursor(string candidateCursor)
    {
        if (_cursor is { } c && CursorComparer.Compare(candidateCursor, c) <= 0)
        {
            _log.Debug("Session cursor did not advance session={Session} snowflake={Snowflake}",
                _sessionId.Value, candidateCursor);
            return;
        }

        Persist(new CursorAdvanced(candidateCursor), ApplyCursorAdvanced);
    }

    private void ApplyCursorAdvanced(CursorAdvanced advanced)
    {
        if (NormalizeSnowflake(advanced.Cursor) is not { } cursor)
        {
            _log.Warning("Corrupt cursor value during recovery, skipping: {Cursor}", advanced.Cursor);
            return;
        }

        _cursor = cursor;

        if (!IsRecovering && LastSequenceNr > 1 && LastSequenceNr % 10 == 0)
            DeleteMessages(LastSequenceNr - 1);
    }

    private void ApplyPendingApprovalPromptTracked(PendingApprovalPromptTracked tracked)
    {
        _hasObservedApprovalRequest = true;
        PendingApprovalRecovery.ApplyTracked<PendingApprovalRequest, DiscordMessageId>(
            _pendingApprovalRequests,
            tracked,
            wrapPromptId: value => new DiscordMessageId(value),
            createRequest: (callId, requesterSenderId, requesterPrincipal, optionKeys, promptId, toolName, displayText) =>
                new PendingApprovalRequest(callId, requesterSenderId, requesterPrincipal, optionKeys, promptId, toolName, displayText));
    }

    private void ApplyPendingApprovalPromptCleared(PendingApprovalPromptCleared cleared)
        => PendingApprovalRecovery.ApplyCleared<PendingApprovalRequest, DiscordMessageId>(_pendingApprovalRequests, cleared);

    /// <summary>
    /// Returns the canonical decimal form of a Discord snowflake, or null
    /// when the value is not a snowflake and therefore has no order. The
    /// <see cref="ulong"/> round-trip is a validation and normalization
    /// step, not cursor state: it rejects the same inputs the previous
    /// numeric cursor rejected, and it strips a form such as a leading zero
    /// that <see cref="SnowflakeCursorComparer"/> cannot order.
    /// </summary>
    private static string? NormalizeSnowflake(string? value)
        => ulong.TryParse(value, out var id) ? id.ToString() : null;

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
