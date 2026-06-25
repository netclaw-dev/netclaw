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
    private bool _postedThisTurn;
    private bool _uploadedFileThisTurn;
    private PostResult? _lastFailedPost;
    // Reply targets for in-flight reminder delivery confirmations, keyed by
    // reminder delivery key. Captured from DeliverTrustedSessionTurn; each is
    // told a ReminderDeliveryResult on its turn's TurnCompleted and removed.
    // Keyed (not a single field) because multiple reminders can target the
    // same session concurrently — a single field would be clobbered.
    private readonly Dictionary<ReminderId, IActorRef> _reminderDeliveryObservers = new();
    private readonly List<PendingApprovalRequest> _pendingApprovalRequests = [];

    // Gates the text-approval cold path (TryHandleColdTextApprovalResponseAsync).
    // A binding that has never observed any ToolInteractionRequest treats an
    // inbound "A"/"B"/"C" as a possible cold approval reply for a session that
    // restarted out from under it. Once we've observed at least one prompt,
    // subsequent ambiguous text from the user is ordinary conversation, not an
    // approval reply, so the cold path stays off. This does NOT gate button
    // clicks — those always route to the session, which is the authority on
    // CallId staleness.
    private bool _hasObservedApprovalRequest;

    private readonly SessionPipelineHandle _handle;
    private SlackEventTs? _cursorTs;
    private SlackEventTs? _pendingCursorTs;

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
    private const string BackfillDetectorWarning = ":warning: I couldn't safely analyze some earlier thread messages, so they were excluded from context.";
    private const string LiveDetectorUnavailableWarning = ":warning: I couldn't safely analyze your message — please try again in a moment.";
    private const string LiveInjectionBlockedWarning = ":warning: Message blocked by prompt-injection policy.";

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
            .WithContext("SessionId", _sessionId.Value)
            .WithContext("SlackChannelId", _channelId)
            .WithContext("SlackThreadTs", _threadTs);

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
                await PerformOneShotHydrationAsync();
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
            _reminderDeliveryObservers[reminderKey] = deliveryObserver;

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

            var buildResult = _hydrationPending
                && SlackAclPolicy.IsAllowedUser(new SlackUserId(message.SenderId.Value), _dependencies.Options)
                ? await BuildInputWithDeferredHydrationAsync(message, contents, currentTs, inboundCts.Token)
                : BuildInputForInbound(message, contents);
            var input = buildResult.Input;

            if (buildResult.BackfillDetectorUnavailable)
                await SafePostAsync(BackfillDetectorWarning);

            try
            {
                using var queueWriteCts = CancellationTokenSource.CreateLinkedTokenSource(inboundCts.Token);
                queueWriteCts.CancelAfter(OperationTimeout);

                await writer.WriteAsync(input, queueWriteCts.Token);

                // Defer cursor persistence until TurnCompleted confirms durable turn recording,
                // otherwise a crash between cursor and turn persist loses messages.
                if (currentTs is { } ts)
                {
                    if (_pendingCursorTs is not { } pending || ts.CompareTo(pending) > 0)
                        _pendingCursorTs = ts;
                }
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
        Filter = OutputFilter.Text | OutputFilter.Files
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

    private InboundBuildResult BuildInputForInbound(
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

        return new InboundBuildResult(baseInput, false);
    }

    private ChannelDeliveryTargetInfo BuildDefaultDeliveryTarget()
        => new(
            ChannelType.Slack.ToWireValue(),
            "destination",
            _channelId.Value,
            _channelId.Value,
            _threadTs.Value);

    /// <summary>
    /// One-shot thread history hydration. Runs once per actor lifetime, in the
    /// Hydrating behavior immediately after pipeline initialization. Fetches
    /// thread history, computes the gap relative to the recovered cursor, and
    /// if there is an authorized message in the gap, synthesizes one backfill
    /// <see cref="ChannelInput"/> using that message as the trigger and older
    /// gap messages as adopted context. Hands the synthesized input to the
    /// session pipeline through the normal input-queue path.
    /// </summary>
    private async Task PerformOneShotHydrationAsync()
    {
        using var cts = new CancellationTokenSource(InboundProcessingTimeout);

        IReadOnlyList<ChannelInput> history;
        try
        {
            history = await _dependencies.ThreadHistoryFetcher.FetchThreadHistoryAsync(_sessionId, cts.Token);
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "Thread history fetch failed for session {SessionId}", _sessionId.Value);
            return;
        }

        if (history.Count == 0)
        {
            _log.Info("Thread history hydration: empty thread, no backfill cursor={Cursor}", _cursorTs?.Value ?? "none");
            return;
        }

        var cursor = _cursorTs;
        var candidates = new List<ChannelInput>(history.Count);
        foreach (var item in history)
        {
            if (new SlackEventId(item.MessageId ?? string.Empty).TryGetEventTs() is not { } itemTs)
                continue;

            // Strict: only include messages newer than the cursor. PR #733's
            // "cursor advances only on TurnCompleted" guarantees that
            // ts == cursor means the session already has that message persisted
            // — re-including it here on a restart hydration would duplicate it.
            // The in-flight-crash case is handled too: a turn that didn't
            // complete leaves the cursor un-advanced, so the message has
            // ts > cursor and is correctly included in the gap.
            if (cursor is { } c && itemTs.CompareTo(c) <= 0)
                continue;

            candidates.Add(item);
        }

        if (candidates.Count == 0)
        {
            _log.Info(
                "Thread history hydration: cursor already at thread head fetched={FetchedCount} cursor={Cursor}",
                history.Count, cursor?.Value ?? "none");
            return;
        }

        var classified = await ClassifyGapAsync(candidates, cts.Token);
        var gap = classified.Gap;

        _log.Info(
            "Thread history hydration fetched={FetchedCount} gapCount={GapCount} blockedHighRisk={BlockedHighRiskCount} cursor={Cursor}",
            history.Count, gap.Count, classified.BlockedForRisk, cursor?.Value ?? "none");

        if (classified.DetectorUnavailable)
            await SafePostAsync(BackfillDetectorWarning);

        if (gap.Count == 0)
            return;

        // Locate the most recent authorized message in the gap — it plays the
        // role of the "current authorized message" that adopted-context normally
        // anchors around. Without one we have no authorized trigger to enqueue;
        // we transition to Active and let the next live authorized inbound be
        // the trigger (the cursor stays put, so staleness still drops anything
        // already seen).
        AdoptedContextMessage? trigger = null;
        for (var i = gap.Count - 1; i >= 0; i--)
        {
            if (gap[i].AuthorityAtInclusion == AdoptedMessageAuthority.Authorized)
            {
                trigger = gap[i];
                break;
            }
        }

        if (trigger is null)
        {
            // Deferred: a non-empty gap with no authorized trigger. The
            // proactive-thread case — the binding actor's lifetime began when
            // the agent posted the thread root, so this hydration ran before
            // any authorized human inbound existed. Re-arm so the first
            // authorized inbound performs this hydration (adopting the gap,
            // e.g. the bot-authored root) instead of taking the fetch-free path.
            _hydrationPending = true;
            _log.Info("Thread history hydration: no authorized message in gap; re-armed for next authorized inbound");
            return;
        }

        var adoptedContext = new List<AdoptedContextMessage>();
        foreach (var item in gap)
        {
            if (ReferenceEquals(item, trigger))
                break;
            adoptedContext.Add(item);
        }

        // Build the synthesized inbound. The trigger's ChannelInput already
        // carries its own text+attachment contents (loaded by the history
        // fetcher), so we treat those as the "live" contents for the merge.
        var triggerInput = trigger.Input;
        var triggerTs = new SlackEventId(triggerInput.MessageId ?? string.Empty).TryGetEventTs();
        var backfillInput = MergeAdoptedContext(triggerInput, adoptedContext, cursor);

        if (_dependencies.IngressGate?.ClosedReason is { } ingressClosedReason)
        {
            _log.Info("Skipping hydration backfill enqueue while restart drain is active: {Reason}", ingressClosedReason);
            return;
        }

        var writer = _handle.InputQueue;
        if (writer is null)
        {
            _log.Warning("Input queue is not initialized; skipping hydration backfill");
            return;
        }

        try
        {
            using var writeCts = CancellationTokenSource.CreateLinkedTokenSource(cts.Token);
            writeCts.CancelAfter(OperationTimeout);
            await writer.WriteAsync(backfillInput, writeCts.Token);

            if (triggerTs is { } ts)
            {
                if (_pendingCursorTs is not { } pending || ts.CompareTo(pending) > 0)
                    _pendingCursorTs = ts;
            }

            _log.Info(
                "hydration_backfill_enqueued trigger={TriggerMessageId} adoptedCount={AdoptedCount}",
                triggerInput.MessageId,
                adoptedContext.Count);
            ChannelTelemetry.For(ChannelType.Slack).RecordMessageEnqueued();
        }
        catch (OperationCanceledException ex)
        {
            _log.Warning(ex, "Timed out enqueueing hydration backfill for session {SessionId}", _sessionId.Value);
        }
        catch (ChannelClosedException ex)
        {
            _log.Warning(ex, "Input queue closed while enqueueing hydration backfill for session {SessionId}", _sessionId.Value);
        }
    }

    private Task<Classification> ClassifyGapMessageAsync(ChannelInput input, CancellationToken cancellationToken)
    {
        var text = string.Join("\n", input.Contents
            .OfType<TextContent>()
            .Select(t => t.Text)
            .Where(t => !string.IsNullOrWhiteSpace(t)));

        return PromptClassifier.ClassifyAsync(
            _promptInjectionDetector, text, "slack-backfill", _log, cancellationToken);
    }

    private readonly record struct GapClassification(
        List<AdoptedContextMessage> Gap,
        int BlockedForRisk,
        bool DetectorUnavailable);

    /// <summary>
    /// Runs prompt-injection classification over candidate gap messages and
    /// captures each surviving message's authority-at-inclusion. Blocked
    /// messages are dropped; detector-unavailable messages are also dropped
    /// and surface a caller-visible flag.
    /// </summary>
    private async Task<GapClassification> ClassifyGapAsync(
        IReadOnlyList<ChannelInput> candidates,
        CancellationToken cancellationToken)
    {
        var classifications = await Task.WhenAll(
            candidates.Select(c => ClassifyGapMessageAsync(c, cancellationToken)));

        var gap = new List<AdoptedContextMessage>(candidates.Count);
        var blockedForRisk = 0;
        var detectorUnavailable = false;
        for (var i = 0; i < candidates.Count; i++)
        {
            var item = candidates[i];
            switch (classifications[i].Outcome)
            {
                case ClassificationOutcome.Allow:
                    var authority = SlackAclPolicy.IsAllowedUser(new SlackUserId(item.SenderId.Value), _dependencies.Options)
                        ? AdoptedMessageAuthority.Authorized
                        : AdoptedMessageAuthority.Pending;
                    gap.Add(new AdoptedContextMessage(item, authority));
                    break;

                case ClassificationOutcome.Block:
                    blockedForRisk++;
                    _log.Warning(
                        "Dropped backfill message due to prompt injection risk sender={SenderId} messageId={MessageId} reason={Reason}",
                        item.SenderId,
                        item.MessageId ?? "none",
                        classifications[i].Reason ?? "high-risk pattern detected");
                    break;

                case ClassificationOutcome.DetectorUnavailable:
                    blockedForRisk++;
                    detectorUnavailable = true;
                    break;
            }
        }

        return new GapClassification(gap, blockedForRisk, detectorUnavailable);
    }

    /// <summary>
    /// Merges <paramref name="adoptedContext"/> as the adopted-context window
    /// preceding <paramref name="triggerInput"/> (the executable message) and
    /// returns the trigger input with adopted-context metadata populated.
    /// </summary>
    private static ChannelInput MergeAdoptedContext(
        ChannelInput triggerInput,
        List<AdoptedContextMessage> adoptedContext,
        SlackEventTs? cursor)
    {
        var merged = AdoptedContextContentBuilder.MergeWithCurrentMessage(
            adoptedContext,
            triggerInput.Contents,
            triggerInput.SenderId.Value,
            triggerInput.ReceivedAt);

        return triggerInput with
        {
            Contents = merged.Contents,
            HasThirdPartyAdoptedContext = merged.SpeakerIds.Any(
                id => !string.Equals(id, triggerInput.SenderId.Value, StringComparison.Ordinal)),
            AdoptedSpeakerIds = merged.SpeakerIds,
            AdoptedContextProjection = merged.Projection,
            AdoptedContextLowerBound = cursor?.Value,
            AdoptedContextUpperBound = triggerInput.MessageId,
            AdoptedContextEntries = merged.Entries
        };
    }

    /// <summary>
    /// Completes a thread-history hydration that <see cref="PerformOneShotHydrationAsync"/>
    /// deferred for lack of an authorized trigger (<see cref="_hydrationPending"/>).
    /// This authorized inbound is the executable trigger; the thread gap strictly
    /// before it — most importantly a proactively-posted bot-authored root —
    /// is fetched, classified, and merged as its adopted-context window. On
    /// fetch failure the turn proceeds without an adopted window and hydration
    /// stays re-armed so a later authorized inbound retries.
    /// </summary>
    private async Task<InboundBuildResult> BuildInputWithDeferredHydrationAsync(
        SlackThreadInbound message,
        IReadOnlyList<AIContent> liveContents,
        SlackEventTs? liveTs,
        CancellationToken cancellationToken)
    {
        var baseResult = BuildInputForInbound(message, liveContents);

        // Without the live message's ordering key the gap below it cannot be
        // bounded; leave hydration re-armed and take the fetch-free path.
        if (liveTs is not { } liveEventTs)
            return baseResult;

        IReadOnlyList<ChannelInput> history;
        try
        {
            history = await _dependencies.ThreadHistoryFetcher.FetchThreadHistoryAsync(_sessionId, cancellationToken);
        }
        catch (Exception ex)
        {
            // Non-fatal: execute the turn without an adopted window and keep
            // hydration re-armed so a later authorized inbound retries.
            _log.Warning(ex, "Re-armed thread history fetch failed for session {SessionId}", _sessionId.Value);
            return baseResult;
        }

        // Fetch succeeded: hydration is complete. Only a fetch failure (caught
        // above) keeps the flag armed — classify/merge outcomes never re-arm.
        _hydrationPending = false;

        var cursor = _cursorTs;
        var candidates = new List<ChannelInput>(history.Count);
        foreach (var item in history)
        {
            if (new SlackEventId(item.MessageId ?? string.Empty).TryGetEventTs() is not { } itemTs)
                continue;

            // Strictly above the watermark and strictly below the live inbound:
            // the live inbound is the executable message, not adopted context.
            if (cursor is { } c && itemTs.CompareTo(c) <= 0)
                continue;
            if (itemTs.CompareTo(liveEventTs) >= 0)
                continue;

            candidates.Add(item);
        }

        if (candidates.Count == 0)
            return baseResult;

        var classified = await ClassifyGapAsync(candidates, cancellationToken);
        if (classified.Gap.Count == 0)
            return new InboundBuildResult(baseResult.Input, classified.DetectorUnavailable);

        var mergedInput = MergeAdoptedContext(baseResult.Input, classified.Gap, cursor);
        _log.Info(
            "deferred_hydration_adopted gapCount={GapCount} trigger={TriggerMessageId}",
            classified.Gap.Count,
            baseResult.Input.MessageId);

        return new InboundBuildResult(mergedInput, classified.DetectorUnavailable);
    }

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

        if (_pendingCursorTs is { } p && ts.CompareTo(p) <= 0)
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
        _hasObservedApprovalRequest = true;

        var existing = _pendingApprovalRequests.LastOrDefault(p => p.CallId.Value == tracked.CallId);
        if (existing is not null)
        {
            existing.PromptMessageTs = new SlackEventTs(tracked.PromptId);
            return;
        }

        _pendingApprovalRequests.Add(new PendingApprovalRequest(
            new ToolCallId(tracked.CallId),
            tracked.RequesterSenderId,
            tracked.RequesterPrincipal,
            tracked.OptionKeys,
            new SlackEventTs(tracked.PromptId),
            toolName: tracked.ToolName,
            displayText: tracked.DisplayText));
    }

    private void ApplyPendingApprovalPromptCleared(PendingApprovalPromptCleared cleared)
        => _pendingApprovalRequests.RemoveAll(p => p.CallId.Value == cleared.CallId);

    private readonly record struct InboundBuildResult(ChannelInput Input, bool BackfillDetectorUnavailable);

    private async Task ReinitializePipelineAsync(string reason)
    {
        _pendingCursorTs = null;
        // Reset per-turn delivery flags: a reinit aborts the in-flight turn,
        // and a stale _postedThisTurn=true would otherwise leak into the next
        // turn and falsely report a later reminder as delivered.
        _postedThisTurn = false;
        _uploadedFileThisTurn = false;
        _lastFailedPost = null;
        // Report any in-flight reminder turn as not-delivered now so the
        // execution actor redelivers immediately rather than stalling until
        // the backstop timeout.
        FailPendingReminderDeliveries($"Slack pipeline reinitialized: {reason}");
        await _handle.ReinitializeAsync(
            reason,
            () => Timers.StartSingleTimer(
                ReinitializeTimerKey,
                new ReinitializePipeline("retry after failed reinit"),
                TimeSpan.FromSeconds(2)));
    }

    private async Task HandleOutputAsync(ThreadOutput threadOutput)
    {
        switch (threadOutput.Output)
        {
            case TextOutput text:
            {
                var fullText = text.Text?.Trim();
                if (!string.IsNullOrWhiteSpace(fullText))
                {
                    var result = await SafePostAsync(fullText);
                    if (result.Success)
                        _postedThisTurn = true;
                    else
                        _lastFailedPost = result;
                }

                break;
            }

            case FileOutput file:
                var uploadResult = await SafeUploadFileAsync(file);
                if (!uploadResult.Success)
                    _lastFailedPost = uploadResult;
                break;

            // BufferFlush and TextDeltaOutput are not received — Slack subscribes
            // to OutputFilter.Text (final assembled text), not TextStreaming.

            case ToolInteractionRequest interaction:
                _hasObservedApprovalRequest = true;
                var pendingApproval = new PendingApprovalRequest(interaction);
                _pendingApprovalRequests.Add(pendingApproval);
                var promptMessageTs = await HandleApprovalRequestAsync(interaction);
                if (promptMessageTs is not null)
                {
                    pendingApproval.PromptMessageTs = promptMessageTs;
                    Persist(new PendingApprovalPromptTracked
                    {
                        CallId = pendingApproval.CallId.Value,
                        RequesterSenderId = pendingApproval.RequesterSenderId,
                        RequesterPrincipal = pendingApproval.RequesterPrincipal,
                        OptionKeys = pendingApproval.OptionKeys,
                        PromptId = promptMessageTs.Value.Value,
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
                    // Posting the approval prompt failed. Drop the pending entry and
                    // route a deny back to the session so the blocked tool task can
                    // unwind instead of waiting on the infinite-timeout TCS.
                    _pendingApprovalRequests.Remove(pendingApproval);
                    await SendApprovalDenyOnFailureAsync(interaction);
                }
                break;

            case ErrorOutput err:
                var refId = err.CorrelationId.ToString("N")[..8];
                var errorResult = await SafePostAsync($":warning: {err.Message} (ref: {refId})");
                if (errorResult.Success)
                    _postedThisTurn = true;
                else
                    _lastFailedPost = errorResult;
                break;

            case TurnCompleted completed:
                if (completed.Outcome == TurnOutcome.Completed && _pendingCursorTs is { } pendingTs)
                    AdvanceCursor(pendingTs);
                _pendingCursorTs = null;

                if (completed.SourceReminderId is { } sourceReminderKey
                    && !string.IsNullOrWhiteSpace(sourceReminderKey.Value)
                    && _reminderDeliveryObservers.Remove(sourceReminderKey, out var reminderObserver))
                {
                    var delivered = _postedThisTurn || _uploadedFileThisTurn;
                    reminderObserver.Tell(new ReminderDeliveryResult(
                        sourceReminderKey,
                        ChannelType.Slack,
                        Delivered: delivered,
                        FailureReason: delivered ? null : "Slack post did not succeed",
                        ObservedAtMs: _dependencies.TimeProvider.GetUtcNow().ToUnixTimeMilliseconds()));
                }

                if (!_postedThisTurn && !_uploadedFileThisTurn)
                {
                    if (_lastFailedPost is { ShouldNotifySession: true, FailureKind: { } failureKind, ErrorMessage: { } errorMessage })
                    {
                        _log.Warning(
                            "Turn completed with Slack delivery failure kind={FailureKind}; notifying session",
                            failureKind);
                        await NotifyDeliveryFailedAsync(completed.TurnNumber, failureKind, errorMessage);
                    }
                    else
                    {
                        _log.Warning("Turn completed without visible Slack output; posting fallback reply");
                        await SafePostAsync(EmptyTurnFallbackText);
                    }
                }

                _postedThisTurn = false;
                _uploadedFileThisTurn = false;
                _lastFailedPost = null;
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

                break;
        }
    }

    private async Task<bool> TryHandleTextApprovalResponseAsync(SlackThreadInbound message)
    {
        if (_pendingApprovalRequests.Count == 0)
        {
            return !_hasObservedApprovalRequest
                && await TryHandleColdTextApprovalResponseAsync(message);
        }

        var pendingIndex = _pendingApprovalRequests.FindIndex(request =>
            ApprovalButtonValueCodec.CanApprove(request.RequesterPrincipal, request.RequesterSenderId, message.SenderId.Value));

        if (pendingIndex < 0)
        {
            await SafePostAsync(":warning: Only the requesting user can approve this tool action.");
            return true;
        }

        var pending = _pendingApprovalRequests[pendingIndex];

        if (!ToolInteractionResponseParser.TryParseApprovalResponse(
                message.Text ?? string.Empty,
                pending.Options,
                out var selectedKey)
            || selectedKey is null)
        {
            return false;
        }

        try
        {
            using var feedbackCts = new CancellationTokenSource(OperationTimeout);
            var feedbackResult = await _dependencies.Pipeline.SendFeedbackAndWaitAsync(
                new ToolInteractionResponse
                {
                    SessionId = _sessionId,
                    CallId = pending.CallId,
                    SelectedKey = new Actors.Protocol.ApprovalOptionKey(selectedKey),
                    SenderId = message.SenderId
                }, feedbackCts.Token);

            switch (feedbackResult)
            {
                case CommandNack nack:
                    if (string.Equals(nack.Reason, ApprovalNackReasons.WrongRequester, StringComparison.Ordinal))
                        await SafePostAsync(":warning: Only the requesting user can approve this tool action.");
                    _log.Info(
                        "Session rejected Slack text approval response for call {CallId} reason={Reason}; skipping redraw",
                        pending.CallId,
                        nack.Reason ?? "<none>");
                    return true;

                case not CommandAck:
                    _log.Warning(
                        "Slack text approval response for call {CallId} returned unexpected feedback result {ResultType}",
                        pending.CallId,
                        feedbackResult.GetType().Name);
                    return true;
            }

            _pendingApprovalRequests.RemoveAt(pendingIndex);
            Persist(new PendingApprovalPromptCleared
            {
                CallId = pending.CallId.Value
            }, ApplyPendingApprovalPromptCleared);

            await TryResolveApprovalPromptAsync(
                pending.PromptMessageTs,
                pending.Request,
                pending.CallId,
                selectedKey,
                message.SenderId.Value,
                persistedToolName: pending.ToolName,
                persistedDisplayText: pending.DisplayText);

            _log.Info(
                "Recorded Slack approval response for call {CallId} sender={SenderId} selection={SelectedKey}",
                pending.CallId,
                message.SenderId,
                selectedKey);
        }
        catch (Exception ex)
        {
            _log.Error(ex, "Failed to route Slack approval response for call {CallId}", pending.CallId);
        }

        return true;
    }

    private async Task<bool> TryHandleColdTextApprovalResponseAsync(SlackThreadInbound message)
    {
        if (!ToolInteractionResponseParser.LooksLikeApprovalResponse(message.Text ?? string.Empty))
            return false;

        using var feedbackCts = new CancellationTokenSource(OperationTimeout);
        try
        {
            var reply = await _dependencies.Pipeline.SendFeedbackAndWaitAsync(new ToolInteractionTextResponse
            {
                SessionId = _sessionId,
                Text = message.Text ?? string.Empty,
                SenderId = message.SenderId
            }, feedbackCts.Token);

            if (reply is CommandAck)
            {
                _log.Info(
                    "Forwarded cold Slack text approval response from sender={SenderId} without local pending prompt state",
                    message.SenderId);
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
            _log.Error(ex, "Failed to route cold Slack text approval response from sender {SenderId}", message.SenderId);
            return false;
        }
    }

    private async Task HandleApprovalResponseAsync(SlackApprovalResponse message)
    {
        var pendingIndex = _pendingApprovalRequests.FindIndex(request =>
            request.CallId == message.CallId);
        var pending = pendingIndex >= 0 ? _pendingApprovalRequests[pendingIndex] : null;

        // CanApprove fast-path: if the binding still holds the original request we can
        // post the wrong-requester warning locally without round-tripping through the
        // session. When the binding has been cold-spawned (no local pending entry) the
        // session re-runs CanApprove against its own pending-call state, and the wait
        // below blocks the redraw until the session has actually accepted the click —
        // see #939 + #979.
        if (pending is not null && !ApprovalButtonValueCodec.CanApprove(
                pending.RequesterPrincipal,
                pending.RequesterSenderId,
                message.SenderId.Value))
        {
            await SafePostAsync(":warning: Only the requesting user can approve this tool action.");
            return;
        }

        // Wait for the session before redrawing. This is the security gate that
        // prevents (a) a non-requester click destroying the prompt UI on the
        // cold-spawn path, and (b) a stale re-click overwriting an
        // already-resolved banner — both surfaced by the #939 code review. The
        // session is the authority on whether the call is still pending and
        // whether the sender is allowed. Only redraw on CommandAck.
        ISessionResponse feedbackResult;
        try
        {
            using var feedbackCts = new CancellationTokenSource(OperationTimeout);
            feedbackResult = await _dependencies.Pipeline.SendFeedbackAndWaitAsync(
                new ToolInteractionResponse
                {
                    SessionId = _sessionId,
                    CallId = message.CallId,
                    SelectedKey = new Actors.Protocol.ApprovalOptionKey(message.SelectedKey),
                    SenderId = message.SenderId
                }, feedbackCts.Token);
        }
        catch (Exception ex)
        {
            _log.Error(ex, "Failed to route Slack button approval response for call {CallId}", message.CallId);
            return;
        }

        switch (feedbackResult)
        {
            case CommandNack nack:
                // Session rejected (wrong requester, unknown call, stale resolution).
                // For wrong-requester surface the warning; for any other reason just
                // log and DO NOT redraw — the prompt UI must stay consistent with the
                // session's authoritative state.
                if (string.Equals(nack.Reason, ApprovalNackReasons.WrongRequester, StringComparison.Ordinal))
                    await SafePostAsync(":warning: Only the requesting user can approve this tool action.");
                _log.Info(
                    "Session rejected Slack approval response for call {CallId} reason={Reason}; skipping redraw",
                    message.CallId,
                    nack.Reason ?? "<none>");
                return;

            case CommandAck:
                break;

            default:
                // ISessionResponse is sealed-by-convention to Ack/Nack. Defensive guard.
                _log.Warning(
                    "Slack approval response for call {CallId} returned unexpected feedback result {ResultType}",
                    message.CallId,
                    feedbackResult.GetType().Name);
                return;
        }

        if (pending is not null)
        {
            _pendingApprovalRequests.RemoveAt(pendingIndex);
            Persist(new PendingApprovalPromptCleared
            {
                CallId = pending.CallId.Value
            }, ApplyPendingApprovalPromptCleared);
            // Prefer the captured TS, but fall back to the payload's TS when capture
            // failed (post raced or returned an empty TS). The payload TS is reliable
            // for a button click since Slack populates it in the envelope.
            var promptTs = pending.PromptMessageTs ?? message.PromptMessageTs;
            await TryResolveApprovalPromptAsync(
                promptTs,
                pending.Request,
                pending.CallId,
                message.SelectedKey,
                message.SenderId.Value,
                persistedToolName: pending.ToolName,
                persistedDisplayText: pending.DisplayText);

            _log.Info(
                "Recorded Slack button approval response for call {CallId} sender={SenderId} selection={SelectedKey}",
                pending.CallId,
                message.SenderId,
                message.SelectedKey);
        }
        else if (message.PromptMessageTs is { } payloadPromptTs)
        {
            // Cold-spawn redraw — no local pending entry exists for this CallId
            // (the journal didn't replay a PendingApprovalPromptTracked event for
            // it, typically because the binding was re-created without recovery
            // or the entry was cleared before the click landed). The click
            // payload still carries the prompt's message TS and the session has
            // accepted the response; render the generic banner so the buttons
            // clear. NOTE: pre-0.21 journals that DO replay (with no tool name /
            // display text) take the upper `pending is not null` branch instead
            // — they render the generic banner via the !IsNullOrEmpty fallback
            // inside the builder, not via this branch.
            await TryResolveApprovalPromptAsync(
                payloadPromptTs,
                request: null,
                message.CallId,
                message.SelectedKey,
                message.SenderId.Value);

            _log.Info(
                "Forwarded Slack button approval response for call {CallId} to session without local pending entry; redrew prompt via payload messageTs={MessageTs}",
                message.CallId,
                payloadPromptTs);
        }
        else
        {
            // Text-reply on a cold-spawned binding: approval routed but no message
            // TS is available, so the redraw is impossible. Remaining #939 gap.
            _log.Info(
                "Forwarded Slack button approval response for call {CallId} to session without local pending entry; redraw skipped",
                message.CallId);
            ChannelTelemetry.For(ChannelType.Slack).RecordExtra("interactionErrors", "cold_spawn_redraw_skipped");
        }
    }

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
            observer.Tell(new ReminderDeliveryResult(key, ChannelType.Slack, Delivered: false, FailureReason: reason));

        _reminderDeliveryObservers.Clear();
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

            _uploadedFileThisTurn = true;
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
    private sealed class PendingApprovalRequest
    {
        public PendingApprovalRequest(ToolInteractionRequest request)
        {
            Request = request;
            CallId = request.CallId;
            RequesterSenderId = request.RequesterSenderId?.Value;
            RequesterPrincipal = request.RequesterPrincipal;
            Options = request.Options;
            OptionKeys = request.Options.Select(option => option.Key.Value).ToArray();
            ToolName = request.ToolName.Value;
            DisplayText = request.DisplayText;
        }

        public PendingApprovalRequest(
            ToolCallId callId,
            string? requesterSenderId,
            PrincipalClassification? requesterPrincipal,
            IReadOnlyList<string> optionKeys,
            SlackEventTs? promptMessageTs,
            string? toolName = null,
            string? displayText = null)
        {
            Request = null;
            CallId = callId;
            RequesterSenderId = requesterSenderId;
            RequesterPrincipal = requesterPrincipal;
            OptionKeys = [.. optionKeys];
            Options = OptionKeys
                .Select(key => new ToolInteractionOption(new ApprovalOptionKey(key), ApprovalOptionKeys.LabelFor(key)))
                .ToArray();
            PromptMessageTs = promptMessageTs;
            ToolName = toolName;
            DisplayText = displayText;
        }

        public ToolInteractionRequest? Request { get; }
        public ToolCallId CallId { get; }
        public string? RequesterSenderId { get; }
        public PrincipalClassification? RequesterPrincipal { get; }
        public IReadOnlyList<ToolInteractionOption> Options { get; }
        public IReadOnlyList<string> OptionKeys { get; }

        /// <summary>
        /// Tool name. Populated from <see cref="ToolInteractionRequest.ToolName"/>
        /// on the hot path and from the persisted
        /// <see cref="PendingApprovalPromptTracked.ToolName"/> on the cold-spawn
        /// recovery path. Null only for journal entries written before the field
        /// was added.
        /// </summary>
        public string? ToolName { get; }

        /// <summary>
        /// Display text (already truncated to the persisted ceiling on the
        /// cold-spawn path). Null only for legacy journal entries.
        /// </summary>
        public string? DisplayText { get; }

        public SlackEventTs? PromptMessageTs { get; set; }
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
