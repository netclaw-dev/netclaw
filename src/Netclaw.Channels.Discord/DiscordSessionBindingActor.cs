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
using Netclaw.Security;
using IOPath = System.IO.Path;

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
    private readonly List<PendingApprovalRequest> _pendingApprovalRequests = [];

    private static readonly TimeSpan PipelineInitTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan ReinitializeDelay = TimeSpan.FromSeconds(2);
    private static readonly object ReinitializeTimerKey = new();
    private static readonly TimeSpan IdlePassivationTimeout = TimeSpan.FromHours(1);
    private bool _deliveredThisTurn;
    private int _turnNumber;
    private string? _lastSetThreadName;
    private ulong? _cursorSnowflake;
    private ulong? _pendingCursorSnowflake;

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
        _promptInjectionDetector = dependencies.PromptInjectionDetector ?? new NullPromptInjectionDetector();

        _log = Context.GetLogger()
            .WithContext("Adapter", "discord")
            .WithContext("SessionId", _sessionId.Value)
            .WithContext("DiscordChannelId", _channelId.Value)
            .WithContext("DiscordThreadOrMessageId", _threadOrMessageId.Value);

        _handle = new SessionPipelineHandle(_dependencies.Pipeline, _log, "discord-session");

        Recover<CursorAdvanced>(ApplyCursorAdvanced);

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
        DefaultAudience = TrustAudience.Team,
        DefaultBoundary = SecurityPolicyDefaults.TrustedInstanceBoundary,
        DefaultPrincipal = PrincipalClassification.UntrustedExternal,
        DefaultProvenance = new SourceProvenance
        {
            TransportAuthenticity = TransportAuthenticity.Verified,
            PayloadTaint = PayloadTaint.Public,
            SourceKind = "discord",
            SourceScope = _channelId.Value
        },
        Filter = OutputFilter.Text | OutputFilter.Files
    };

    private void Initializing()
    {
        CommandAsync<InitializePipeline>(async _ =>
        {
            try
            {
                await EnsureInitializedAsync();
                Become(Active);
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

    private void Active()
    {
        CommandAsync<DiscordThreadInbound>(HandleInboundAsync);
        CommandAsync<DiscordApprovalResponse>(HandleApprovalResponseAsync);
        CommandAsync<DeliverTrustedSessionTurn>(HandleTrustedReminderAsync);
        CommandAsync<OutputReceived>(HandleOutputReceivedAsync);

        Command<OutputStreamTerminated>(msg =>
        {
            if (msg.Generation != _handle.Generation)
                return;

            var reason = msg.Cause is null
                ? "completed"
                : $"faulted: {msg.Cause.Message}";

            _log.Warning("Discord output stream terminated ({Reason}); reinitializing pipeline", reason);
            Self.Tell(new ReinitializePipeline(reason));
        });

        CommandAsync<ReinitializePipeline>(async msg =>
        {
            _deliveredThisTurn = false;
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
                _log.Info("Discord session idle but {0} approval(s) pending; deferring passivation", _pendingApprovalRequests.Count);
                return;
            }

            _log.Info("Discord session idle for 1 hour, passivating");
            RunTask(async () =>
            {
                await _handle.DrainAsync();
                Context.Stop(Self);
            });
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
            && ToolInteractionResponseParser.TryParseApprovalResponse(message.Text, out var selectedKey)
            && selectedKey is not null
            && await TryHandleTextApprovalResponseAsync(message, selectedKey))
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
            _log.Warning("Discord input queue is not initialized; dropping inbound message");
            return;
        }

        var liveContents = new List<AIContent>();
        if (!string.IsNullOrWhiteSpace(message.Text))
            liveContents.Add(new TextContent(message.Text));

        if (hasAttachments)
            await ProcessInboundAttachmentsAsync(message.Attachments!, message.Audience, liveContents, inboundCts.Token);

        if (liveContents.Count == 0)
            return;

        var (mergedContents, backfillDetectorUnavailable, adoptedSpeakerIds, projection, adoptedEntries) = await BuildInputContentsAsync(message, liveContents, inboundCts.Token);

        if (backfillDetectorUnavailable)
            await SafeReplyAsync(BackfillDetectorWarning);

        var input = new ChannelInput
        {
            SenderId = message.SenderId.Value,
            ChannelId = message.ChannelId.Value,
            MessageId = message.EventId.Value,
            Audience = message.Audience,
            Boundary = SecurityPolicyDefaults.TrustedInstanceBoundary,
            Principal = message.Principal,
            Provenance = message.Provenance,
            Contents = mergedContents,
            ReceivedAt = message.ReceivedAt,
            ExecutableText = message.Text,
            HasAdoptedContext = adoptedSpeakerIds.Count > 0,
            AdoptedSpeakerIds = adoptedSpeakerIds,
            AdoptedContextProjection = projection,
            AdoptedContextLowerBound = _cursorSnowflake?.ToString(),
            AdoptedContextUpperBound = message.EventId.Value,
            AdoptedContextEntries = adoptedEntries
        };

        try
        {
            using var writeCts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await writer.WriteAsync(input, writeCts.Token);
            ChannelTelemetry.For(ChannelType.Discord).RecordMessageEnqueued();

            if (TryParseSnowflake(message.EventId.Value) is { } eventSnowflake)
            {
                if (_pendingCursorSnowflake is not { } pending || eventSnowflake > pending)
                    _pendingCursorSnowflake = eventSnowflake;
            }
        }
        catch (OperationCanceledException)
        {
            _log.Warning("Timed out enqueueing Discord message for session {0}", _sessionId.Value);
            Self.Tell(new ReinitializePipeline("input queue write timeout"));
        }
        catch (ChannelClosedException)
        {
            _log.Warning("Discord input queue closed for session {0}", _sessionId.Value);
            Self.Tell(new ReinitializePipeline("input queue closed"));
        }
    }

    private async Task<(IReadOnlyList<AIContent> Contents, bool BackfillDetectorUnavailable, IReadOnlyList<string> AdoptedSpeakerIds, string? Projection, IReadOnlyList<ChannelInput.AdoptedContextEntry> AdoptedEntries)> BuildInputContentsAsync(
        DiscordThreadInbound message,
        List<AIContent> liveContents,
        CancellationToken cancellationToken)
    {
        if (_dependencies.ThreadHistoryFetcher is not { } fetcher)
            return (liveContents, false, [], null, []);

        IReadOnlyList<ChannelInput> history;
        try
        {
            history = await fetcher.FetchThreadHistoryAsync(_sessionId, cancellationToken);
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "Thread history fetch failed for session {0}", _sessionId.Value);
            return (liveContents, false, [], null, []);
        }

        if (history.Count == 0)
            return (liveContents, false, [], null, []);

        var cursor = _cursorSnowflake;

        // Phase 1: filter by cursor bounds (cheap, sync).
        var candidates = new List<ChannelInput>(history.Count);
        foreach (var item in history)
        {
            if (TryParseSnowflake(item.MessageId ?? string.Empty) is not { } itemSnowflake)
                continue;

            // Keep the cursor message itself during fresh-runtime hydration.
            // If the daemon restarts while a turn is still in-flight, the
            // session actor may recover with no completed turns even though the
            // cursor already advanced to the last inbound user message.
            if (cursor is { } c && itemSnowflake < c)
                continue;

            candidates.Add(item);
        }

        if (candidates.Count == 0)
        {
            _log.Info(
                "Thread history hydrated fetched={FetchedCount} gapCount=0 cursor={Cursor} session={Session}",
                history.Count, cursor?.ToString() ?? "none", _sessionId.Value);
            return (liveContents, false, [], null, []);
        }

        // Phase 2: classify candidates in parallel for prompt injection risk.
        var classifications = await Task.WhenAll(
            candidates.Select(c => ClassifyGapMessageAsync(c, cancellationToken)));

        // Phase 3: assemble gap preserving chronological order.
        var safe = new List<AdoptedContextMessage>(candidates.Count);
        var blockedForRisk = 0;
        var detectorUnavailable = false;

        for (var i = 0; i < candidates.Count; i++)
        {
            switch (classifications[i].Outcome)
            {
                case ClassificationOutcome.Allow:
                    var authority = _dependencies.Options.AllowedUserIds.Length == 0
                        || _dependencies.Options.AllowedUserIds.Contains(candidates[i].SenderId, StringComparer.Ordinal)
                        ? AdoptedMessageAuthority.Authorized
                        : AdoptedMessageAuthority.Pending;
                    safe.Add(new AdoptedContextMessage(candidates[i], authority));
                    break;

                case ClassificationOutcome.Block:
                    blockedForRisk++;
                    _log.Warning(
                        "Dropped backfill message due to prompt injection risk sender={SenderId} messageId={MessageId} reason={Reason}",
                        candidates[i].SenderId,
                        candidates[i].MessageId ?? "none",
                        classifications[i].Reason ?? "high-risk pattern detected");
                    break;

                case ClassificationOutcome.DetectorUnavailable:
                    blockedForRisk++;
                    detectorUnavailable = true;
                    break;
            }
        }

        _log.Info(
            "Thread history hydrated fetched={FetchedCount} gapCount={GapCount} allowed={AllowedCount} blockedHighRisk={BlockedHighRiskCount} cursor={Cursor} session={Session}",
            history.Count, candidates.Count, safe.Count, blockedForRisk, cursor?.ToString() ?? "none", _sessionId.Value);

        if (safe.Count == 0)
            return (liveContents, detectorUnavailable, [], null, []);

        var merged = MergeHistoryWithLiveContents(safe, liveContents, message);

        return (
            merged.Contents,
            detectorUnavailable,
            merged.SpeakerIds,
            merged.Projection,
            merged.Entries);
    }

    private Task<Classification> ClassifyGapMessageAsync(ChannelInput input, CancellationToken cancellationToken)
    {
        var text = string.Join("\n", input.Contents
            .OfType<TextContent>()
            .Select(t => t.Text)
            .Where(t => !string.IsNullOrWhiteSpace(t)));

        return PromptClassifier.ClassifyAsync(
            _promptInjectionDetector, text, "discord-backfill", _log, cancellationToken);
    }

    private static AdoptedContextMergeResult MergeHistoryWithLiveContents(
        IReadOnlyList<AdoptedContextMessage> history,
        IReadOnlyList<AIContent> liveContents,
        DiscordThreadInbound message)
        => AdoptedContextContentBuilder.MergeWithCurrentMessage(
            history,
            liveContents,
            message.SenderId.Value,
            message.ReceivedAt);

    private async Task<bool> TryHandleTextApprovalResponseAsync(DiscordThreadInbound message, string selectedKey)
    {
        var (result, pending) = ResolvePendingRequest(message.SenderId, callId: null);

        if (result is ApprovalLookupResult.NotFound)
            return false;

        if (result is ApprovalLookupResult.WrongRequester)
        {
            await SafeReplyAsync(WrongRequesterWarning);
            return true;
        }

        _pendingApprovalRequests.Remove(pending!);

        await _dependencies.Pipeline.SendFeedbackAsync(new ToolInteractionResponse
        {
            SessionId = _sessionId,
            CallId = pending!.CallId,
            SelectedKey = selectedKey,
            SenderId = message.SenderId.Value
        });

        await TryResolveApprovalPromptAsync(pending!, selectedKey, message.SenderId.Value);
        return true;
    }

    private async Task HandleApprovalResponseAsync(DiscordApprovalResponse message)
    {
        var (result, pending) = ResolvePendingRequest(message.SenderId, message.CallId);

        if (result is ApprovalLookupResult.WrongRequester)
        {
            await SafeReplyAsync(WrongRequesterWarning);
            return;
        }

        if (result is ApprovalLookupResult.NotFound)
        {
            _log.Info("Ignoring Discord approval response for unknown call id {0}", message.CallId);
            ChannelTelemetry.For(ChannelType.Discord).RecordExtra("interactionErrors", "unknown_call_id");
            return;
        }

        _pendingApprovalRequests.Remove(pending!);

        await _dependencies.Pipeline.SendFeedbackAsync(new ToolInteractionResponse
        {
            SessionId = _sessionId,
            CallId = message.CallId,
            SelectedKey = message.SelectedKey,
            SenderId = message.SenderId.Value
        });

        await TryResolveApprovalPromptAsync(pending!, message.SelectedKey, message.SenderId.Value);
    }

    private async Task TryResolveApprovalPromptAsync(
        PendingApprovalRequest pending,
        string selectedKey,
        string senderId)
    {
        if (pending.PromptMessageId is not { } promptMessageId)
            return;

        try
        {
            var resolvedText = DiscordApprovalPromptBuilder.BuildResolvedPromptText(
                pending.Request,
                selectedKey,
                senderId);

            using var cts = new CancellationTokenSource(OperationTimeout);
            await _dependencies.ReplyClient.UpdateMessageAsync(
                _replyChannelId,
                promptMessageId,
                resolvedText,
                removeComponents: true,
                cts.Token);
        }
        catch (Exception ex)
        {
            _log.Warning(
                ex,
                "Failed to update resolved approval prompt for call {CallId} messageId={MessageId}",
                pending.CallId,
                promptMessageId.Value);
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
            _log.Warning("Discord input queue is not initialized; rejecting Mode B reminder");
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
            ReminderId = message.Source.ReminderId,
            AckTarget = ackTarget
        };

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
            _log.Warning("Discord input queue closed; rejecting Mode B reminder for session {0}", _sessionId.Value);
            ackTarget.Tell(CommandNack.For(_sessionId, "Pipeline input queue closed"));
        }
    }

    private enum ApprovalLookupResult { Matched, WrongRequester, NotFound }

    private (ApprovalLookupResult Result, PendingApprovalRequest? Pending) ResolvePendingRequest(
        DiscordUserId senderId, string? callId)
    {
        if (callId is not null)
        {
            var byCallId = _pendingApprovalRequests.LastOrDefault(p =>
                string.Equals(p.CallId, callId, StringComparison.Ordinal));
            if (byCallId is null)
                return (ApprovalLookupResult.NotFound, null);
            if (!ApprovalButtonValueCodec.CanApprove(byCallId.RequesterPrincipal, byCallId.RequesterSenderId?.Value, senderId.Value))
                return (ApprovalLookupResult.WrongRequester, null);
            return (ApprovalLookupResult.Matched, byCallId);
        }

        if (_pendingApprovalRequests.Count == 0)
            return (ApprovalLookupResult.NotFound, null);

        var bySender = _pendingApprovalRequests.LastOrDefault(p =>
            ApprovalButtonValueCodec.CanApprove(p.RequesterPrincipal, p.RequesterSenderId?.Value, senderId.Value));
        return bySender is not null
            ? (ApprovalLookupResult.Matched, bySender)
            : (ApprovalLookupResult.WrongRequester, null);
    }

    private async Task HandleOutputReceivedAsync(OutputReceived msg)
    {
        switch (msg.Output)
        {
            case TextOutput textOutput:
                await SafeReplyAsync(textOutput.Text);
                _deliveredThisTurn = true;
                break;

            case ErrorOutput error:
                await SafeReplyAsync($":warning: {error.Message}");
                _deliveredThisTurn = true;
                break;

            case FileOutput file:
                await SafeReplyAsync($":paperclip: Produced file `{file.FileName}` ({file.MimeType}).");
                _deliveredThisTurn = true;
                break;

            case ToolInteractionRequest request when string.Equals(request.Kind, "approval", StringComparison.OrdinalIgnoreCase):
                var pendingApproval = new PendingApprovalRequest(request);
                _pendingApprovalRequests.Add(pendingApproval);

                var promptMessageId = await SafeReplyWithButtonsAsync(request);
                if (promptMessageId is not null)
                {
                    pendingApproval.PromptMessageId = promptMessageId;
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
                if (completed.Outcome == TurnOutcome.Completed && _pendingCursorSnowflake is { } pendingSnowflake)
                    AdvanceCursor(pendingSnowflake);
                _pendingCursorSnowflake = null;

                if (!string.IsNullOrWhiteSpace(completed.SourceReminderId) && _deliveredThisTurn)
                {
                    Context.System.EventStream.Publish(new ReminderDeliveryObserved(
                        completed.SourceReminderId,
                        ChannelType.Discord,
                        completed.TimestampMs));
                }

                if (!_deliveredThisTurn)
                    await SafeReplyAsync(EmptyTurnFallbackText);

                _turnNumber = completed.TurnNumber;
                _pendingApprovalRequests.Clear();
                _deliveredThisTurn = false;
                break;
        }
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

    private async Task SafeReplyAsync(string text)
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
                return;
            }
        }
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
            _log.Error(ex, "Failed to send delivery feedback to session");
        }
    }

    private async Task SendApprovalDenyOnFailureAsync(string callId)
    {
        var pending = _pendingApprovalRequests.LastOrDefault(p =>
            string.Equals(p.CallId, callId, StringComparison.Ordinal));
        if (pending is not null)
            _pendingApprovalRequests.Remove(pending);

        try
        {
            await _dependencies.Pipeline.SendFeedbackAsync(new ToolInteractionResponse
            {
                SessionId = _sessionId,
                CallId = callId,
                SelectedKey = ApprovalOptionKeys.Deny,
                SenderId = "system"
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
                "discord_attachments_rejected count={Count} limit={Limit} audience={Audience} reason=too-many-files",
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
                case AttachmentIngestResult.Accepted accepted:
                    acceptedLines.Add(accepted.Line);
                    if (accepted.Inline is { } inline)
                        dataContents.Add(inline);
                    break;

                case AttachmentIngestResult.Rejected rejected:
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

    private async Task<AttachmentIngestResult> TryIngestSingleAttachmentAsync(
        DiscordFileReference file,
        TrustAudience audience,
        ChannelAttachmentPolicy policy,
        bool inlineImages,
        string inboxDir,
        string stagingDir,
        CancellationToken cancellationToken)
    {
        var category = AttachmentCategories.FromMime(file.MimeType);

        if (!policy.Allows(category))
        {
            _log.Warning(
                "discord_attachment_rejected name={Name} mime={Mime} audience={Audience} category={Category} reason=category-not-allowed",
                file.Name, file.MimeType, audience, category);
            return new AttachmentIngestResult.Rejected(
                $"`{file.Name}` ({category}) isn't allowed in {audience} channels. " +
                "Please DM me if you want to share this class of file.");
        }

        if (file.Size > policy.MaxFileBytes)
        {
            _log.Warning(
                "discord_attachment_rejected name={Name} mime={Mime} audience={Audience} size={Size} limit={Limit} reason=too-large",
                file.Name, file.MimeType, audience, file.Size, policy.MaxFileBytes);
            return new AttachmentIngestResult.Rejected(
                $"`{file.Name}` ({FormatBytes(file.Size)}) exceeds the {FormatBytes(policy.MaxFileBytes)} per-file limit.");
        }

        if (!DiscordAttachmentUrlTrust.IsAllowedAttachmentDomain(file.Url))
        {
            _log.Warning(
                "discord_attachment_rejected name={Name} url={Url} reason=untrusted-domain",
                file.Name, file.Url);
            return new AttachmentIngestResult.Rejected(
                $"`{file.Name}` has an untrusted URL domain and was skipped.");
        }

        AttachmentDownloadResult downloadResult;
        try
        {
            using var downloadCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            downloadCts.CancelAfter(OperationTimeout);
            downloadResult = await StreamingAttachmentDownloader.DownloadToFileAsync(
                _dependencies.HttpClient!, file.Url, configureRequest: null,
                stagingDir, policy.MaxFileBytes, downloadCts.Token,
                (ex, path) => _log.Error(ex, "Failed to clean up staged download file {0}", path));
        }
        catch (AttachmentTooLargeException ex)
        {
            _log.Warning(
                "discord_attachment_rejected name={Name} mime={Mime} audience={Audience} size={Size} limit={Limit} reason=too-large-during-download",
                file.Name, file.MimeType, audience, ex.BytesReceived, ex.MaxBytes);
            return new AttachmentIngestResult.Rejected(
                $"`{file.Name}` ({FormatBytes(ex.BytesReceived)}) exceeds the {FormatBytes(ex.MaxBytes)} per-file limit.");
        }
        catch (OperationCanceledException ex)
        {
            _log.Warning(ex,
                "discord_attachment_rejected name={Name} mime={Mime} reason=download-timeout",
                file.Name, file.MimeType);
            return new AttachmentIngestResult.Rejected(
                $"Timed out downloading `{file.Name}`. Please try again.");
        }
        catch (Exception ex)
        {
            _log.Warning(ex,
                "discord_attachment_rejected name={Name} mime={Mime} reason=download-failed",
                file.Name, file.MimeType);
            return new AttachmentIngestResult.Rejected(
                $"Couldn't download `{file.Name}` — please try again later.");
        }

        if (downloadResult.BytesWritten == 0)
        {
            _log.Warning(
                "discord_attachment_rejected name={Name} mime={Mime} reason=empty-download",
                file.Name, file.MimeType);
            TryDeleteTemp(downloadResult.FilePath);
            return new AttachmentIngestResult.Rejected(
                $"`{file.Name}` downloaded as zero bytes.");
        }

        ContentScanResult scanResult;
        try
        {
            using var scanCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            scanCts.CancelAfter(OperationTimeout);
            scanResult = await _dependencies.ContentScanner.ScanFileAsync(
                downloadResult.FilePath, file.Name, file.MimeType, scanCts.Token);
        }
        catch (Exception ex)
        {
            _log.Warning(ex,
                "discord_attachment_rejected name={Name} mime={Mime} reason=scan-exception",
                file.Name, file.MimeType);
            TryDeleteTemp(downloadResult.FilePath);
            return new AttachmentIngestResult.Rejected(
                $"Couldn't scan `{file.Name}` — please try again later.");
        }

        if (!scanResult.IsAllowed)
        {
            _log.Warning(
                "discord_attachment_rejected name={Name} mime={Mime} reason=scan-blocked error={ScanError} message={ScanMessage}",
                file.Name, file.MimeType, scanResult.Error?.ToString(), scanResult.Message ?? scanResult.Error?.ToString());

            TryDeleteTemp(downloadResult.FilePath);

            if (scanResult.Error == ContentScanError.ScanFailure)
            {
                return new AttachmentIngestResult.Rejected(
                    $"Couldn't scan `{file.Name}` — please try again later.");
            }

            return new AttachmentIngestResult.Rejected(
                $"Content scanner rejected `{file.Name}`: {scanResult.Message ?? scanResult.Error?.ToString()}.");
        }

        string inboxPath;
        try
        {
            inboxPath = InboxWriter.SanitizeReserveAndMove(
                inboxDir, file.Name, downloadResult.FilePath);
        }
        catch (InboxWriter.CollisionExhaustedException ex)
        {
            _log.Warning(ex,
                "discord_attachment_rejected name={Name} reason=collision-exhausted",
                file.Name);
            TryDeleteTemp(downloadResult.FilePath);
            return new AttachmentIngestResult.Rejected(
                $"Too many attachments named `{file.Name}` in this session — please rename and try again.");
        }
        catch (Exception ex)
        {
            _log.Error(ex,
                "discord_attachment_rejected name={Name} reason=inbox-write-failed",
                file.Name);
            TryDeleteTemp(downloadResult.FilePath);
            return new AttachmentIngestResult.Rejected(
                $"Couldn't save `{file.Name}` — please try again later.");
        }

        var (inlined, note) = AttachmentIngressFormatting.ResolveInlineDecision(category, inlineImages);

        var relativePath = $"{SessionDirectoryHelper.InboxSubdirectory}/{IOPath.GetFileName(inboxPath)}";
        var line = AttachmentIngressFormatting.BuildAttachmentLine(
            file.Name, file.MimeType, downloadResult.BytesWritten, relativePath, inlined, note);

        DataContent? inlineContent = null;
        if (inlined)
        {
            var inlineBytes = await File.ReadAllBytesAsync(inboxPath, cancellationToken);
            inlineContent = new DataContent(inlineBytes, file.MimeType);
        }

        _log.Info(
            "discord_attachment_accepted name={Name} mime={Mime} size={Size} audience={Audience} category={Category} inlined={Inlined}",
            file.Name, file.MimeType, downloadResult.BytesWritten, audience, category, inlined);

        return new AttachmentIngestResult.Accepted(line, inlineContent);
    }

    private void TryDeleteTemp(string tempPath)
    {
        try
        {
            if (File.Exists(tempPath))
                File.Delete(tempPath);
        }
        catch (Exception ex)
        {
            _log.Error(ex, "Failed to clean up staged attachment file {Path}", tempPath);
        }
    }

    private static string FormatBytes(long size) => AttachmentIngressFormatting.FormatBytes(size);

    private abstract record AttachmentIngestResult
    {
        public sealed record Accepted(string Line, DataContent? Inline) : AttachmentIngestResult;

        public sealed record Rejected(string UserFacingReason) : AttachmentIngestResult;
    }

    internal static List<string> ChunkMessage(string text)
    {
        if (text.Length <= MaxDiscordMessageLength)
            return [text];

        var chunks = new List<string>();
        var remaining = text.AsSpan();
        while (remaining.Length > 0)
        {
            if (remaining.Length <= MaxDiscordMessageLength)
            {
                chunks.Add(remaining.ToString());
                break;
            }

            var splitAt = MaxDiscordMessageLength;
            var newlineIdx = remaining[..splitAt].LastIndexOf('\n');
            if (newlineIdx > 0)
                splitAt = newlineIdx + 1;

            chunks.Add(remaining[..splitAt].ToString());
            remaining = remaining[splitAt..];
        }

        return chunks;
    }

    private void AdvanceCursor(ulong candidateSnowflake)
    {
        if (_cursorSnowflake is { } c && candidateSnowflake <= c)
        {
            _log.Debug("Discord session cursor did not advance session={Session} snowflake={Snowflake}",
                _sessionId.Value, candidateSnowflake);
            return;
        }

        Persist(new CursorAdvanced(candidateSnowflake.ToString()), ApplyCursorAdvanced);
    }

    private void ApplyCursorAdvanced(CursorAdvanced advanced)
    {
        if (!ulong.TryParse(advanced.Cursor, out var snowflake))
        {
            _log.Warning("Corrupt cursor value during recovery, skipping: {Cursor}", advanced.Cursor);
            return;
        }

        _cursorSnowflake = snowflake;

        if (!IsRecovering && LastSequenceNr > 1 && LastSequenceNr % 10 == 0)
            DeleteMessages(LastSequenceNr - 1);
    }

    private static ulong? TryParseSnowflake(string value)
        => ulong.TryParse(value, out var id) ? id : null;

    private sealed record InitializePipeline
    {
        public static readonly InitializePipeline Instance = new();
    }

    private sealed record OutputReceived(SessionOutput Output);

    private sealed record OutputStreamTerminated(int Generation, Exception? Cause);

    private sealed record ReinitializePipeline(string Reason);
}
