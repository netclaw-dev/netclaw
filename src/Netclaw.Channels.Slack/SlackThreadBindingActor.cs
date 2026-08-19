// -----------------------------------------------------------------------
// <copyright file="SlackThreadBindingActor.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Text;
using System.Threading.Channels;
using System.Threading.Tasks;
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
using SlackNet.Blocks;
using static Netclaw.Actors.Sessions.SessionProtocol;
using static Netclaw.Actors.Reminders.ReminderProtocol;

namespace Netclaw.Channels.Slack;

internal sealed class SlackThreadBindingActor : ReceivePersistentActor, IWithTimers
{
    private readonly SessionId _sessionId;
    private readonly SlackChannelId _channelId;
    private readonly SlackThreadTs _threadTs;
    private readonly SlackGatewayDependencies _dependencies;
    private readonly IPromptInjectionDetector _promptInjectionDetector;
    private readonly ILoggingAdapter _log;

    // Slack subscribes to OutputFilter.Text (final assembled text), not TextStreaming.
    private const string EmptyTurnFallbackText = ":warning: I didn't manage to produce a reply. Please try rephrasing or sending your message again.";
    // No streaming state needed — Slack receives TextOutput only, not TextDeltaOutput.

    // Slack tells the session about a delivery failure at turn completion, not
    // at post time, so it keeps the failure descriptor of the last failed post.
    // The engine's per-turn failure flag gates every read of this field, and
    // both turn-completion hooks clear it.
    private PostResult? _lastFailedPost;
    private readonly List<PendingApprovalRequest> _pendingApprovalRequests = [];
    private readonly ApprovalResponseFlow<PendingApprovalRequest, SlackEventTs> _approvalFlow;
    private readonly ChannelOutputEngine<PendingApprovalRequest, SlackEventTs> _outputEngine;

    private readonly SessionPipelineHandle _handle;
    private readonly ThreadGapHydrationEngine _hydrationEngine;
    private SlackEventTs? _cursorTs;

    // A Slack ts is decimal seconds, so two ts values order by numeric value,
    // not by ordinal text. SlackEventTs.CompareTo owns that rule; the hydration
    // engine and the output engine compare cursor keys through this wrapper.
    private static readonly IComparer<string> CursorComparer =
        Comparer<string>.Create((x, y) => new SlackEventTs(x).CompareTo(new SlackEventTs(y)));

    private volatile bool _processingIndicatorActive;
    private readonly object _processingIndicatorRenderLock = new();
    private Task _processingIndicatorRenderTail = Task.CompletedTask;

    // Set when PerformOneShotHydrationAsync fetched a non-empty thread gap but
    // found no authorized trigger to anchor a turn. This is the proactive-thread
    // case: the binding actor's lifetime began when the agent posted the thread
    // root, so the one-shot hydration ran before any authorized human inbound
    // existed. While set, the first authorized inbound performs the deferred
    // hydration instead of taking the fetch-free path; it is cleared once that
    // hydration completes.
    private bool _hydrationPending;
    private static readonly object ReinitializeTimerKey = new();
    private static readonly TimeSpan InboundProcessingTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan OperationTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan ProcessingIndicatorTimeout = TimeSpan.FromSeconds(1);
    private const string BackfillDetectorWarning = ":warning: I couldn't safely analyze some earlier thread messages, so they were excluded from context.";
    private const string LiveDetectorUnavailableWarning = ":warning: I couldn't safely analyze your message — please try again in a moment.";
    private const string LiveInjectionBlockedWarning = ":warning: Message blocked by prompt-injection policy.";
    private const string WrongRequesterWarning = ":warning: Only the requesting user can approve this tool action.";

    public ITimerScheduler Timers { get; set; } = null!;

    public SlackThreadBindingActor(
        SessionId sessionId,
        SlackChannelId channelId,
        SlackThreadTs threadTs,
        SlackGatewayDependencies dependencies)
    {
        _sessionId = sessionId;
        _channelId = channelId;
        _threadTs = threadTs;
        _dependencies = dependencies;
        // Fail loud rather than substituting a no-op detector — a no-op reports
        // every input as safe, silently disabling injection scanning. A null
        // here means broken gateway wiring.
        _promptInjectionDetector = dependencies.PromptInjectionDetector
            ?? throw new InvalidOperationException(
                "SlackGatewayDependencies.PromptInjectionDetector is not wired; "
                + "prompt-injection scanning cannot be silently disabled.");
        _handle = new SessionPipelineHandle(dependencies.Pipeline, Context.GetLogger(), "slack-thread");
        _log = Context.GetLogger()
            .WithContext("Adapter", "slack")
            .WithContext(NetclawLogProperties.SessionId, _sessionId.Value)
            .WithContext("SlackChannelId", _channelId)
            .WithContext("SlackThreadTs", _threadTs);

        _outputEngine = new ChannelOutputEngine<PendingApprovalRequest, SlackEventTs>(
            channelType: ChannelType.Slack,
            channelName: "Slack",
            cursorComparer: CursorComparer,
            pendingRequests: _pendingApprovalRequests,
            createPendingRequest: request => new PendingApprovalRequest(request),
            // Slack renders every interaction request as an approval prompt.
            // Discord and Mattermost render only Kind == "approval".
            isApprovalRequest: _ => true,
            renderTextOutput: textOutput =>
            {
                var fullText = textOutput.Text?.Trim();
                return string.IsNullOrWhiteSpace(fullText) ? null : fullText;
            },
            renderErrorOutput: error => $":warning: {error.Message} (ref: {error.CorrelationId.ToString("N")[..8]})",
            postTextAsync: PostOutputTextAsync,
            uploadFileAsync: UploadOutputFileAsync,
            postApprovalPromptAsync: HandleApprovalRequestAsync,
            readPromptIdValue: promptMessageTs => promptMessageTs.Value,
            onApprovalPromptFailedAsync: SendApprovalDenyOnFailureAsync,
            persistPromptTracked: tracked => Persist(tracked, ApplyPendingApprovalPromptTracked),
            handleChannelSpecificOutputAsync: HandleChannelSpecificOutputAsync,
            advanceCursor: cursor => AdvanceCursor(new SlackEventTs(cursor)),
            postEmptyTurnFallbackAsync: PostEmptyTurnFallbackAsync,
            onEmptyTurnSuppressedAsync: NotifyFailedTurnAsync,
            readObservedAtMs: _ => _dependencies.TimeProvider.GetUtcNow().ToUnixTimeMilliseconds());

        _hydrationEngine = new ThreadGapHydrationEngine(
            sessionId: _sessionId,
            channelType: ChannelType.Slack,
            historyFetcher: _dependencies.ThreadHistoryFetcher,
            injectionDetector: _promptInjectionDetector,
            classifierSourceContext: "slack-backfill",
            cursorComparer: CursorComparer,
            cursorKeySelector: messageId => new SlackEventId(messageId ?? string.Empty).TryGetEventTs()?.Value,
            isAuthorizedSender: senderId =>
                SlackAclPolicy.IsAllowedUser(new SlackUserId(senderId), _dependencies.Options),
            log: _log,
            readCursor: () => _cursorTs?.Value,
            readInputQueue: () => _handle.InputQueue,
            readIngressClosedReason: () => _dependencies.IngressGate?.ClosedReason,
            warnBackfillDetectorUnavailableAsync: () => SafePostAsync(BackfillDetectorWarning),
            onBackfillEnqueued: _outputEngine.AdvancePendingCursorForEnqueuedTurn,
            setHydrationPending: pending => _hydrationPending = pending);

        _approvalFlow = new ApprovalResponseFlow<PendingApprovalRequest, SlackEventTs>(
            sessionId: _sessionId,
            channelType: ChannelType.Slack,
            channelName: "Slack",
            pipeline: _dependencies.Pipeline,
            operationTimeout: OperationTimeout,
            pendingRequests: _pendingApprovalRequests,
            // Slack resolves the earliest matching prompt, which is the order its
            // own lookup used before the extraction. Discord and Mattermost
            // resolve the most recent one.
            matchOrder: ApprovalMatchOrder.Oldest,
            hasObservedApprovalRequest: () => _outputEngine.HasObservedApprovalRequest,
            postWrongRequesterWarningAsync: () => SafePostAsync(WrongRequesterWarning),
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

        Context.SetReceiveTimeout(TimeSpan.FromHours(1));
    }

    public static Props CreateProps(
        SessionId sessionId,
        SlackChannelId channelId,
        SlackThreadTs threadTs,
        SlackGatewayDependencies dependencies) =>
        Props.Create(() => new SlackThreadBindingActor(sessionId, channelId, threadTs, dependencies));

    public override string PersistenceId => $"slack-thread-cursor-{Uri.EscapeDataString(_sessionId.Value)}";

    protected override void PreStart()
    {
        Self.Tell(InitializePipeline.Instance);
        base.PreStart();
    }

    protected override void PostStop()
    {
        QueueProcessingIndicatorClearIfActive();
        _handle.Dispose();
        base.PostStop();
    }

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
                _log.Error(ex, "Failed to initialize Slack thread pipeline; stopping actor");
                Context.Stop(Self);
            }
        });

        CommandAny(_ =>
        {
                Stash.Stash();
        });
    }

    private void Hydrating()
    {
        CommandAsync<PerformHydration>(async _ =>
        {
            try
            {
                await _hydrationEngine.PerformOneShotHydrationAsync();
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
        CommandAsync<SlackThreadInbound>(HandleInboundAsync);
        CommandAsync<SlackApprovalResponse>(HandleApprovalResponseAsync);
        CommandAsync<StartProactiveThread>(HandleProactiveThreadAsync);
        CommandAsync<DeliverTrustedSessionTurn>(HandleTrustedReminderAsync);
        CommandAsync<ThreadOutput>(HandleOutputAsync);
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
        CommandAsync<ReinitializePipeline>(async msg => await ReinitializePipelineAsync(msg.Reason));
        Command<ReceiveTimeout>(_ =>
        {
            if (_pendingApprovalRequests.Count > 0)
            {
                _log.Info("Thread idle but {0} approval(s) are pending; deferring passivation", _pendingApprovalRequests.Count);
                return;
            }

            _log.Info("Thread idle for 1 hour, passivating");
            RunTask(async () =>
            {
                await _handle.DrainAsync();
                Context.Stop(Self);
            });
        });
    }

    private async Task HandleProactiveThreadAsync(StartProactiveThread message)
    {
        _log.Info("Initializing proactive thread pipeline for session {0}", message.SessionId.Value);
        await EnsureInitializedAsync();
        Sender.Tell(new ProactiveThreadAck(message.SessionId));
    }

    /// <summary>
    /// Mode B reminder re-entry delivery. Skips the prompt-injection scan
    /// and ACL check because the reminder's audience was validated at
    /// mint time and the content is generated by the local agent itself.
    /// </summary>
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
            _log.Warning("Thread input queue is not initialized; rejecting Mode B reminder");
            ackTarget.Tell(CommandNack.For(_sessionId, "Slack thread pipeline not initialized"));
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
            _outputEngine.TrackReminderDeliveryObserver(reminderKey, deliveryObserver);

        try
        {
            using var writeCts = new CancellationTokenSource(OperationTimeout);
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
            _log.Warning("Thread input queue closed; rejecting Mode B reminder for session {0}", _sessionId.Value);
            ackTarget.Tell(CommandNack.For(_sessionId, "Pipeline input queue closed"));
        }
    }

    private async Task HandleInboundAsync(SlackThreadInbound message)
    {
        var inboundLog = _log
            .WithContext("TurnId", message.TurnId)
            .WithContext("SlackEventId", message.EventId.Value);

        using var inboundCts = new CancellationTokenSource(InboundProcessingTimeout);

        try
        {
            inboundLog.Info("turn_received textChars={TextLength} fileCount={FileCount}",
                message.Text?.Length ?? 0,
                message.Files?.Count ?? 0);

            var currentTs = new SlackEventId(message.EventId.Value).TryGetEventTs();

            if (!string.IsNullOrWhiteSpace(message.Text)
                && await TryHandleTextApprovalResponseAsync(message))
            {
                return;
            }

            if (!string.IsNullOrWhiteSpace(message.Text))
            {
                var classification = await PromptClassifier.ClassifyAsync(
                    _promptInjectionDetector, message.Text, "slack-live", _log, inboundCts.Token);
                switch (classification.Outcome)
                {
                    case ClassificationOutcome.Block:
                        _log.Warning("Blocked Slack message due to prompt injection risk: {Reason}", classification.Reason);
                        ChannelTelemetry.For(ChannelType.Slack).RecordEventDropped("prompt_injection_high");
                        await SafePostAsync(LiveInjectionBlockedWarning);
                        return;

                    case ClassificationOutcome.DetectorUnavailable:
                        _log.Warning("Prompt injection detector unavailable for live message — dropping");
                        ChannelTelemetry.For(ChannelType.Slack).RecordEventDropped("prompt_injection_detector_unavailable");
                        await SafePostAsync(LiveDetectorUnavailableWarning);
                        return;

                    case ClassificationOutcome.Allow:
                        break;
                }
            }

            // Build content list: text + attachment announcements + inline DataContent
            var contents = new List<AIContent>();
            if (!string.IsNullOrEmpty(message.Text))
                contents.Add(new TextContent(message.Text));

            if (message.Files is { Count: > 0 })
            {
                await ProcessInboundAttachmentsAsync(
                    message.Files,
                    message.Audience,
                    contents,
                    inboundCts.Token);
            }

            if (contents.Count == 0)
            {
                _log.Debug("No content to enqueue after file processing");
                return;
            }

            if (_dependencies.IngressGate?.ClosedReason is { } ingressClosedReason)
            {
                _log.Info("Rejecting Slack inbound message while restart drain is active");
                await SafePostAsync(ingressClosedReason);
                return;
            }

            var writer = _handle.InputQueue;
            if (writer is null)
            {
                _log.Warning("Thread input queue is not initialized; dropping inbound message");
                return;
            }

            if (IsStaleInboundEvent(currentTs))
            {
                _log.Info("Dropping stale Slack inbound event eventId={EventId} cursor={Cursor}",
                    message.EventId.Value,
                    _cursorTs?.Value ?? "none");
                ChannelTelemetry.For(ChannelType.Slack).RecordEventDropped("stale_event");
                return;
            }

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
                && !_outputEngine.TurnInFlight
                && _dependencies.Options.MentionRequiredInThreadFor(_channelId.Value))
            {
                _hydrationPending = true;
            }

            var input = BuildInputForInbound(message, contents);
            if (_hydrationPending
                && SlackAclPolicy.IsAllowedUser(new SlackUserId(message.SenderId.Value), _dependencies.Options))
            {
                input = await _hydrationEngine.ApplyDeferredHydrationAsync(
                    input, message.EventId.Value, inboundCts.Token);
            }

            try
            {
                using var queueWriteCts = CancellationTokenSource.CreateLinkedTokenSource(inboundCts.Token);
                queueWriteCts.CancelAfter(OperationTimeout);

                await writer.WriteAsync(input, queueWriteCts.Token);

                // Defer cursor persistence until TurnCompleted confirms durable turn recording,
                // otherwise a crash between cursor and turn persist loses messages.
                _outputEngine.AdvancePendingCursorForEnqueuedTurn(currentTs?.Value);

                QueueProcessingIndicatorRefreshIfActive();
            }
            catch (OperationCanceledException ex)
            {
                _log.Warning(ex, "Timed out enqueueing message for session {0}", _sessionId.Value);
                Self.Tell(new ReinitializePipeline("input queue write timeout"));
                return;
            }
            catch (ChannelClosedException ex)
            {
                _log.Warning(ex, "Thread input queue closed for session {0}", _sessionId.Value);
                Self.Tell(new ReinitializePipeline("input queue write failed"));
                return;
            }

            inboundLog.Info("turn_enqueued contentItems={ContentCount}", input.Contents.Count);
            ChannelTelemetry.For(ChannelType.Slack).RecordMessageEnqueued();
        }
        catch (OperationCanceledException ex)
        {
            inboundLog.Warning(ex, "turn_enqueue_timeout");
        }
        catch (Exception ex)
        {
            inboundLog.Error(ex, "turn_enqueue_failed");
        }
    }

    private Task<AttachmentDownloadResult> DownloadSlackFileToDirectoryAsync(
        SlackFileReference file, string targetDirectory, long maxBytes, CancellationToken ct)
        => SlackFileDownloader.DownloadToFileAsync(
            _dependencies.HttpClient!, file.UrlPrivateDownload, _dependencies.Options.BotToken,
            targetDirectory, maxBytes, ct,
            onCleanupFailure: (ex, path) => _log.Error(ex, "Failed to clean up staged download file {0}", path));

    /// <summary>
    /// Applies the canonical cross-channel attachment ingress pipeline to
    /// the inbound Slack file list: audience-gated policy check, per-file
    /// and per-message cap checks, download, scan, inbox write, and
    /// capability-gated inlining. Appends one batched
    /// <c>[attachment]</c> <see cref="TextContent"/> block plus any
    /// inlined <see cref="DataContent"/> items to <paramref name="contents"/>.
    /// Every rejection path posts a user-visible reply — no silent drops.
    /// </summary>
    private async Task ProcessInboundAttachmentsAsync(
        IReadOnlyList<SlackFileReference> files,
        TrustAudience audience,
        List<AIContent> contents,
        CancellationToken cancellationToken)
    {
        if (_dependencies.HttpClient is null)
        {
            _log.Warning(
                "Slack HTTP client is not configured; rejecting {Count} inbound attachment(s)",
                files.Count);
            await SafePostAsync(":warning: I can't download attachments right now — HTTP client is not configured.");
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
            await SafePostAsync(
                $":warning: I can only accept up to {policy.MaxFilesPerMessage} attachments per message. " +
                $"Please split your upload and try again. Text content was delivered.");
            return;
        }

        // Resolve the capability view once per message — the active model's
        // InputModalities determine whether images get inlined as DataContent
        // versus path-only announcements. PDFs are always path-only: no
        // provider plugin currently serializes application/pdf inline, and
        // the agent can always read them from inbox/ via shell_execute +
        // pdftotext or other file tools.
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
                file,
                audience,
                policy,
                inlineImages,
                inboxDir,
                stagingDir,
                cancellationToken);

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
            await SafePostAsync(joined);
        }
    }

    private Task<AttachmentIngestOutcome> TryIngestSingleAttachmentAsync(
        SlackFileReference file,
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
            (staging, maxBytes, ct) => DownloadSlackFileToDirectoryAsync(file, staging, maxBytes, ct),
            cancellationToken);

    private SessionPipelineOptions BuildOptions() => new()
    {
        ChannelType = Actors.Channels.ChannelType.Slack,
        Filter = OutputFilter.Text | OutputFilter.Files | OutputFilter.ProcessingState
    };

    private async Task EnsureInitializedAsync()
    {
        if (_handle.IsInitialized)
            return;

        var self = Self;
        using var initCts = new CancellationTokenSource(OperationTimeout);
        await _handle.InitializeWithChannelAsync(
            Context,
            _sessionId,
            BuildOptions(),
            output => self.Tell(new ThreadOutput(output)),
            (gen, cause) => self.Tell(new OutputStreamTerminated(gen, cause)),
            initCts.Token);
    }

    private ChannelInput BuildInputForInbound(
        SlackThreadInbound triggeringMessage,
        IReadOnlyList<AIContent> liveContents)
    {
        // Live inbound path is fetch-free. Thread history is hydrated once per
        // actor lifetime in PerformOneShotHydrationAsync (driven by the
        // RecoveryCompleted handler); the only exception is a deferred
        // hydration, which BuildInputWithDeferredHydrationAsync completes on
        // the first authorized inbound. By the time we get here the session
        // already has the historical context it needs. Re-fetching on every
        // inbound was the cause of the duplicate-image bug: in-flight turns
        // left _cursorTs lagging ingestion (per PR #733), so the gap window
        // kept re-including in-flight messages and re-emitting their
        // DataContent on every inbound.
        var baseInput = new ChannelInput
        {
            SenderId = triggeringMessage.SenderId,
            ChannelId = _channelId.Value,
            MessageId = triggeringMessage.EventId.Value,
            Audience = triggeringMessage.Audience,
            Boundary = TrustBoundary.TrustedInstance,
            Principal = triggeringMessage.Principal,
            Provenance = triggeringMessage.Provenance,
            Contents = liveContents,
            ReceivedAt = triggeringMessage.ReceivedAt,
            ExecutableText = triggeringMessage.Text,
            DefaultDeliveryTarget = BuildDefaultDeliveryTarget()
        };

        return baseInput;
    }

    private ChannelDeliveryTargetInfo BuildDefaultDeliveryTarget()
        => new(
            ChannelType.Slack.ToWireValue(),
            "destination",
            _channelId.Value,
            _channelId.Value,
            _threadTs.Value);

    private void AdvanceCursor(SlackEventTs candidateTs)
    {
        if (_cursorTs is { } c && candidateTs.CompareTo(c) <= 0)
        {
            _log.Debug("Thread cursor did not advance stream={StreamKey} ts={Ts}", _sessionId.Value, candidateTs.Value);
            return;
        }

        PersistAsync(new CursorAdvanced(candidateTs.Value), ApplyCursorAdvanced);
    }

    private bool IsStaleInboundEvent(SlackEventTs? eventTs)
    {
        if (eventTs is not { } ts)
            return false;

        if (_cursorTs is { } c && ts.CompareTo(c) <= 0)
            return true;

        if (_outputEngine.PendingCursor is { } p && ts.CompareTo(new SlackEventTs(p)) <= 0)
            return true;

        return false;
    }

    private void ApplyCursorAdvanced(CursorAdvanced advanced)
    {
        _cursorTs = new SlackEventTs(advanced.Cursor);

        // Skip journal truncation during recovery replay — we only need to run
        // it when new events are being persisted.
        if (!IsRecovering && LastSequenceNr > 1 && LastSequenceNr % 10 == 0)
            DeleteMessages(LastSequenceNr - 1);
    }

    private void ApplyPendingApprovalPromptTracked(PendingApprovalPromptTracked tracked)
    {
        _outputEngine.MarkApprovalRequestObserved();
        PendingApprovalRecovery.ApplyTracked<PendingApprovalRequest, SlackEventTs>(
            _pendingApprovalRequests,
            tracked,
            wrapPromptId: value => new SlackEventTs(value),
            createRequest: (callId, requesterSenderId, requesterPrincipal, optionKeys, promptId, toolName, displayText) =>
                new PendingApprovalRequest(callId, requesterSenderId, requesterPrincipal, optionKeys, promptId, toolName, displayText));
    }

    private void ApplyPendingApprovalPromptCleared(PendingApprovalPromptCleared cleared)
        => PendingApprovalRecovery.ApplyCleared<PendingApprovalRequest, SlackEventTs>(_pendingApprovalRequests, cleared);

    private async Task ReinitializePipelineAsync(string reason)
    {
        QueueProcessingIndicatorClearIfActive();
        // Slack drops the cursor key of the abandoned turn; Discord and
        // Mattermost keep theirs across a reinitialize.
        _outputEngine.DiscardPendingCursor();
        // Reset the per-turn delivery state: a reinit aborts the in-flight
        // turn, and a stale delivered flag would otherwise leak into the next
        // turn and falsely report a later reminder as delivered.
        _lastFailedPost = null;
        _outputEngine.ResetForPipelineReinitialize(reason);
        await _handle.ReinitializeAsync(
            reason,
            () => Timers.StartSingleTimer(
                ReinitializeTimerKey,
                new ReinitializePipeline("retry after failed reinit"),
                TimeSpan.FromSeconds(2)));
    }

    private async Task HandleOutputAsync(ThreadOutput threadOutput)
    {
        var clearedPrompts = await _outputEngine.HandleOutputAsync(threadOutput.Output);
        if (clearedPrompts.Count > 0)
            PersistAll(clearedPrompts, ApplyPendingApprovalPromptCleared);
    }

    /// <summary>
    /// Handles the outputs the shared engine leaves to the channel. Slack
    /// renders a processing indicator; other output types have no Slack effect.
    /// BufferFlush and TextDeltaOutput never arrive, because Slack subscribes to
    /// OutputFilter.Text (final assembled text), not TextStreaming.
    /// </summary>
    private async Task HandleChannelSpecificOutputAsync(SessionOutput output)
    {
        if (output is ProcessingStateOutput processing)
            await RenderProcessingStateAsync(processing);
    }

    /// <summary>
    /// Posts turn output text for the shared engine. Slack keeps the failure
    /// descriptor instead of telling the session now, because it reports a
    /// delivery failure once at turn completion.
    /// </summary>
    private async Task<bool> PostOutputTextAsync(string text)
    {
        var result = await SafePostAsync(text);
        if (!result.Success)
            _lastFailedPost = result;
        return result.Success;
    }

    /// <summary>Uploads turn output for the shared engine. See <see cref="PostOutputTextAsync"/>.</summary>
    private async Task<bool> UploadOutputFileAsync(FileOutput file)
    {
        var result = await SafeUploadFileAsync(file);
        if (!result.Success)
            _lastFailedPost = result;
        return result.Success;
    }

    private async Task PostEmptyTurnFallbackAsync()
    {
        _lastFailedPost = null;
        _log.Warning("Turn completed without visible Slack output; posting fallback reply");
        await SafePostAsync(EmptyTurnFallbackText);
    }

    /// <summary>
    /// Tells the session that the turn produced output the transport rejected.
    /// Slack defers this report to turn completion and sends it once with the
    /// completed turn's number. Discord and Mattermost report each failed post
    /// inline instead, so their engine hook does nothing here.
    /// </summary>
    private async Task NotifyFailedTurnAsync(TurnCompleted completed)
    {
        var failedPost = _lastFailedPost;
        _lastFailedPost = null;

        if (failedPost is { ShouldNotifySession: true, FailureKind: { } failureKind, ErrorMessage: { } errorMessage })
        {
            _log.Warning(
                "Turn completed with Slack delivery failure kind={FailureKind}; notifying session",
                failureKind);
            await NotifyDeliveryFailedAsync(completed.TurnNumber, failureKind, errorMessage);
        }
        else
        {
            // Defensive: a failed post always carries a failure kind, so this
            // branch is unreachable today. Keep the empty-turn reply as the
            // safe outcome if that ever stops holding.
            await PostEmptyTurnFallbackAsync();
        }
    }

    private Task RenderProcessingStateAsync(ProcessingStateOutput output)
    {
        _processingIndicatorActive = output.IsProcessing;
        var requirement = output.IsRequired
            ? ChannelOutputRequirement.Required
            : ChannelOutputRequirement.Optional;
        var request = new ChannelOutputRenderRequest(
            BuildOutputRenderTarget(),
            output,
            ChannelOutputEffectKind.ProcessingIndicator,
            requirement);

        var renderTask = QueueProcessingStateRender(request, output.IsRequired);
        return output.IsRequired ? renderTask : Task.CompletedTask;
    }

    private Task QueueProcessingStateRender(ChannelOutputRenderRequest request, bool isRequired)
    {
        lock (_processingIndicatorRenderLock)
        {
            _processingIndicatorRenderTail = RenderAfterPreviousAsync(
                _processingIndicatorRenderTail,
                request,
                isRequired);
            return _processingIndicatorRenderTail;
        }
    }

    private async Task RenderAfterPreviousAsync(
        Task previous,
        ChannelOutputRenderRequest request,
        bool isRequired)
    {
        try
        {
            await previous.ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // A failed required render is reported to its caller. It must not
            // poison the queue and prevent newer state from reaching Slack.
            _log.Warning(ex, "Previous required Slack processing indicator render failed; continuing with newer state");
        }

        await RenderProcessingStateRequestAsync(request, isRequired).ConfigureAwait(false);
    }

    private void QueueProcessingIndicatorClearIfActive()
    {
        if (!_processingIndicatorActive)
            return;

        _ = RenderProcessingStateAsync(new ProcessingStateOutput(false)
        {
            SessionId = _sessionId
        });
    }

    private void QueueProcessingIndicatorRefreshIfActive()
    {
        if (!_processingIndicatorActive)
            return;

        // Slack clears assistant thread status when the app sends a reply; keep
        // long-running turns visible after Slack-side thread activity while the
        // session still reports Processing. See SlackProcessingOutputRenderer.
        _ = RenderProcessingStateAsync(new ProcessingStateOutput(true)
        {
            SessionId = _sessionId
        });
    }

    private async Task RenderProcessingStateRequestAsync(
        ChannelOutputRenderRequest request,
        bool isRequired)
    {
        try
        {
            await RenderOutputWithTimeoutAsync(_dependencies.ChannelRegistry, request);
        }
        catch (Exception ex) when (!isRequired)
        {
            _log.Warning(ex, "Failed rendering optional Slack processing indicator");
        }
    }

    private static async Task RenderOutputWithTimeoutAsync(
        IChannelRegistry registry,
        ChannelOutputRenderRequest request)
    {
        using var renderCts = new CancellationTokenSource(ProcessingIndicatorTimeout);
        var renderTask = registry.RenderOutputAsync(request, renderCts.Token).AsTask();
        try
        {
            await renderTask.WaitAsync(ProcessingIndicatorTimeout);
        }
        finally
        {
            if (!renderTask.IsCompleted)
                ObserveLateProcessingRender(renderTask);
        }
    }

    private static void ObserveLateProcessingRender(Task renderTask)
    {
        _ = renderTask.ContinueWith(
            static task =>
            {
                _ = task.Exception;
            },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private ChannelDeliveryTarget BuildOutputRenderTarget()
    {
        var channelKey = ChannelDescriptorKey.FromChannelType(ChannelType.Slack);
        return new ChannelDeliveryTarget(
            channelKey,
            new ResolvedChannelAddress(
                channelKey,
                ChannelAddressKind.Destination,
                _channelId.Value,
                _channelId.Value),
            _threadTs.Value);
    }

    private Task<bool> TryHandleTextApprovalResponseAsync(SlackThreadInbound message)
        => _approvalFlow.TryHandleTextApprovalResponseAsync(message.Text, message.SenderId.Value);

    // Slack block actions reach the binding one way through the gateway, so the
    // flow gets no synchronous-reply hook here.
    private Task HandleApprovalResponseAsync(SlackApprovalResponse message)
        => _approvalFlow.HandleApprovalResponseAsync(
            message.CallId,
            message.SelectedKey,
            message.SenderId.Value,
            message.PromptMessageTs);

    private async Task<PostResult> SafePostAsync(string text)
    {
        var startedAt = _dependencies.TimeProvider.GetTimestamp();
        try
        {
            using var cts = new CancellationTokenSource(OperationTimeout);
            await _dependencies.ReplyClient.PostThreadReplyAsync(new SlackPostMessage(
                ChannelId: _channelId,
                ThreadTs: _threadTs,
                Text: text), cts.Token);

            _log.Info("Posted Slack reply message");
            ChannelTelemetry.For(ChannelType.Slack).RecordReplyPosted(_dependencies.TimeProvider.GetElapsedTime(startedAt).TotalMilliseconds);
            QueueProcessingIndicatorRefreshIfActive();
            return PostResult.Ok;
        }
        catch (OperationCanceledException ex)
        {
            _log.Error(ex, "Timed out posting Slack reply for session {0}", _sessionId.Value);
            ChannelTelemetry.For(ChannelType.Slack).RecordReplyFailed(_dependencies.TimeProvider.GetElapsedTime(startedAt).TotalMilliseconds);
            return new PostResult($"Timed out posting reply: {ex.Message}", DeliveryFailureKind.TransportFailure);
        }
        catch (SlackMessageDeliveryException ex)
        {
            _log.Warning("Delivery rejected for session {SessionId} error={ErrorCode} kind={FailureKind}",
                _sessionId.Value, ex.ErrorCode ?? "unknown", ex.FailureKind);
            ChannelTelemetry.For(ChannelType.Slack).RecordReplyRejected(ex.ErrorCode);
            return new PostResult(ex.Message, ex.FailureKind);
        }
        catch (Exception ex)
        {
            _log.Error(ex, "Failed posting Slack reply for session {0}", _sessionId.Value);
            ChannelTelemetry.For(ChannelType.Slack).RecordReplyFailed(_dependencies.TimeProvider.GetElapsedTime(startedAt).TotalMilliseconds);
            return new PostResult(ex.Message, DeliveryFailureKind.Unknown);
        }
    }

    private async Task NotifyDeliveryFailedAsync(Actors.Protocol.TurnNumber turnNumber, DeliveryFailureKind failureKind, string errorMessage)
    {
        try
        {
            await _dependencies.Pipeline.SendFeedbackAsync(new DeliveryFailed
            {
                SessionId = _sessionId,
                TurnNumber = turnNumber,
                ChannelType = Actors.Channels.ChannelType.Slack,
                FailureKind = failureKind,
                ErrorMessage = errorMessage
            });
        }
        catch (Exception ex)
        {
            _log.Error(ex, "Failed to send delivery feedback to session; propagating to trigger pipeline reinit");
            throw;
        }
    }

    private sealed record PostResult(string? ErrorMessage = null, DeliveryFailureKind? FailureKind = null)
    {
        public static readonly PostResult Ok = new();

        public bool Success => ErrorMessage is null;

        public bool ShouldNotifySession => FailureKind is not null;
    }

    private async Task<SlackEventTs?> HandleApprovalRequestAsync(ToolInteractionRequest request)
    {
        try
        {
            var text = SlackApprovalBlockBuilder.BuildApprovalText(request);
            var blocks = SlackApprovalBlockBuilder.BuildApprovalBlocks(request);

            using var cts = new CancellationTokenSource(OperationTimeout);
            var promptMessageTs = await _dependencies.ReplyClient.PostThreadReplyWithTsAsync(
                new SlackPostMessage(_channelId, _threadTs, text, blocks),
                cts.Token);

            _log.Info("Posted approval request for tool {ToolName} call={CallId}",
                request.ToolName, request.CallId);

            return new SlackEventTs(promptMessageTs);
        }
        catch (Exception ex)
        {
            _log.Error(ex, "Failed to post approval request for {CallId}", request.CallId);
            return null;
        }
    }

    private async Task SendApprovalDenyOnFailureAsync(ToolInteractionRequest request)
    {
        _log.Warning(
            "Auto-denying approval for {CallId} ({ToolName}) because the Slack prompt could not be posted",
            request.CallId,
            request.ToolName);

        try
        {
            await _dependencies.Pipeline.SendFeedbackAsync(new ToolInteractionResponse
            {
                SessionId = _sessionId,
                CallId = request.CallId,
                SelectedKey = ApprovalOptionKeys.DenyKey,
                SenderId = request.RequesterSenderId ?? new Netclaw.Actors.Protocol.SenderId(string.Empty)
            });
        }
        catch (Exception ex)
        {
            _log.Error(ex, "Failed to send auto-deny feedback for {CallId}", request.CallId);
        }

        await SafePostAsync(
            $":warning: I couldn't post the approval prompt for `{request.ToolName}`. The action was automatically denied — please ask me to try again.");
    }

    private async Task TryResolveApprovalPromptAsync(
        SlackEventTs? promptMessageTs,
        ToolInteractionRequest? request,
        ToolCallId callId,
        string selectedKey,
        string senderId,
        string? persistedToolName = null,
        string? persistedDisplayText = null)
    {
        if (promptMessageTs is not { } promptTs)
            return;

        try
        {
            string text;
            IReadOnlyList<Block> blocks;
            if (request is not null)
            {
                // Hot path: binding still holds the original request, render the full
                // resolved block with verb/location detail.
                text = SlackApprovalBlockBuilder.BuildResolvedApprovalText(
                    request,
                    selectedKey,
                    senderId);
                blocks = SlackApprovalBlockBuilder.BuildResolvedApprovalBlocks(
                    request,
                    selectedKey,
                    senderId);
            }
            else
            {
                // Cold-spawn path: only the payload-provided message TS plus what was
                // journaled with PendingApprovalPromptTracked. When the journal carried
                // tool name + display text, the builder renders them; otherwise it
                // falls back to a generic banner (pre-field journal entries).
                text = SlackApprovalBlockBuilder.BuildResolvedApprovalTextWithoutRequest(
                    selectedKey,
                    senderId,
                    toolName: persistedToolName,
                    displayText: persistedDisplayText);
                blocks = SlackApprovalBlockBuilder.BuildResolvedApprovalBlocksWithoutRequest(
                    selectedKey,
                    senderId,
                    toolName: persistedToolName,
                    displayText: persistedDisplayText);
            }

            using var cts = new CancellationTokenSource(OperationTimeout);
            await _dependencies.ReplyClient.UpdateThreadMessageAsync(
                _channelId,
                promptTs,
                text,
                blocks,
                cts.Token);
        }
        catch (Exception ex)
        {
            _log.Warning(
                ex,
                "Failed to update resolved approval prompt for call {CallId} messageTs={MessageTs}",
                callId,
                promptMessageTs);
        }
    }

    private async Task<PostResult> SafeUploadFileAsync(FileOutput file)
    {
        var startedAt = _dependencies.TimeProvider.GetTimestamp();
        try
        {
            if (!File.Exists(file.FilePath))
            {
                _log.Warning("File not found for upload: {Path}", file.FilePath);
                return new PostResult($"File not found for upload: {file.FilePath}", DeliveryFailureKind.Unknown);
            }

            using var cts = new CancellationTokenSource(OperationTimeout);
            await _dependencies.ReplyClient.UploadFileToThreadAsync(
                _channelId,
                _threadTs,
                file.FilePath,
                file.FileName,
                cts.Token);

            _log.Info("Uploaded file to Slack thread: {FileName}", file.FileName);
            return PostResult.Ok;
        }
        catch (OperationCanceledException ex)
        {
            _log.Error(ex, "Timed out uploading file {FileName} to Slack thread", file.FileName);
            ChannelTelemetry.For(ChannelType.Slack).RecordReplyFailed(_dependencies.TimeProvider.GetElapsedTime(startedAt).TotalMilliseconds);
            return new PostResult($"Timed out uploading file: {ex.Message}", DeliveryFailureKind.TransportFailure);
        }
        catch (SlackMessageDeliveryException ex)
        {
            _log.Warning("Delivery rejected for file upload {FileName} session={SessionId} error={ErrorCode} kind={FailureKind}",
                file.FileName, _sessionId.Value, ex.ErrorCode ?? "unknown", ex.FailureKind);
            ChannelTelemetry.For(ChannelType.Slack).RecordReplyRejected(ex.ErrorCode);
            return new PostResult(ex.Message, ex.FailureKind);
        }
        catch (Exception ex)
        {
            _log.Error(ex, "Failed to upload file {FileName} to Slack thread", file.FileName);
            ChannelTelemetry.For(ChannelType.Slack).RecordReplyFailed(_dependencies.TimeProvider.GetElapsedTime(startedAt).TotalMilliseconds);
            return new PostResult(ex.Message, DeliveryFailureKind.Unknown);
        }
    }

    private sealed record ThreadOutput(SessionOutput Output) : INoSerializationVerificationNeeded;
    private sealed record OutputStreamTerminated(int Generation, Exception? Cause) : INoSerializationVerificationNeeded;
    private sealed record ReinitializePipeline(string Reason) : INoSerializationVerificationNeeded;
    private sealed class PendingApprovalRequest : Netclaw.Channels.PendingApprovalRequest<SlackEventTs>
    {
        public PendingApprovalRequest(ToolInteractionRequest request) : base(request)
        {
        }

        public PendingApprovalRequest(
            ToolCallId callId,
            string? requesterSenderId,
            PrincipalClassification? requesterPrincipal,
            IReadOnlyList<string> optionKeys,
            SlackEventTs? promptMessageTs,
            string? toolName = null,
            string? displayText = null)
            : base(callId, requesterSenderId, requesterPrincipal, optionKeys, promptMessageTs, toolName, displayText)
        {
        }
    }

    private sealed record InitializePipeline : INoSerializationVerificationNeeded
    {
        public static InitializePipeline Instance { get; } = new();
    }

    private sealed record PerformHydration : INoSerializationVerificationNeeded
    {
        public static PerformHydration Instance { get; } = new();
    }
}
