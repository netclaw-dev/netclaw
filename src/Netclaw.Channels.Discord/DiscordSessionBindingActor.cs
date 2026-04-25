using System.Text;
using System.Threading.Channels;
using Akka.Actor;
using Akka.Event;
using Microsoft.Extensions.AI;
using Netclaw.Actors.Channels;
using Netclaw.Actors.Protocol;
using Netclaw.Actors.Reminders;
using Netclaw.Channels.Telemetry;
using Netclaw.Configuration;
using Netclaw.Security;

namespace Netclaw.Channels.Discord;

internal sealed class DiscordSessionBindingActor : ReceiveActor, IWithUnboundedStash, IWithTimers
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
    private bool _threadHistoryFetchAttempted;

    public IStash Stash { get; set; } = null!;
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

        Initializing();
    }

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
        ReceiveAsync<InitializePipeline>(async _ =>
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

        ReceiveAny(msg =>
        {
            if (msg is not InitializePipeline)
                Stash.Stash();
        });
    }

    private void Active()
    {
        ReceiveAsync<DiscordThreadInbound>(HandleInboundAsync);
        ReceiveAsync<DiscordApprovalResponse>(HandleApprovalResponseAsync);
        ReceiveAsync<DeliverTrustedSessionTurn>(HandleTrustedReminderAsync);
        ReceiveAsync<OutputReceived>(HandleOutputReceivedAsync);

        Receive<OutputStreamTerminated>(msg =>
        {
            if (msg.Generation != _handle.Generation)
                return;

            var reason = msg.Cause is null
                ? "completed"
                : $"faulted: {msg.Cause.Message}";

            _log.Warning("Discord output stream terminated ({Reason}); reinitializing pipeline", reason);
            Self.Tell(new ReinitializePipeline(reason));
        });

        ReceiveAsync<ReinitializePipeline>(async msg =>
        {
            _deliveredThisTurn = false;
            await _handle.ReinitializeAsync(
                msg.Reason,
                () => Timers.StartSingleTimer(
                    ReinitializeTimerKey,
                    new ReinitializePipeline("retry after failed reinit"),
                    ReinitializeDelay));
        });

        Receive<ReceiveTimeout>(_ =>
        {
            if (_pendingApprovalRequests.Count > 0)
            {
                _log.Info("Discord session idle but {0} approval(s) pending; deferring passivation", _pendingApprovalRequests.Count);
                return;
            }

            _log.Info("Discord session idle for 1 hour, passivating");
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

    private async Task HandleInboundAsync(DiscordThreadInbound message)
    {
        if (string.IsNullOrWhiteSpace(message.Text))
            return;

        if (ToolInteractionResponseParser.TryParseApprovalResponse(message.Text, out var selectedKey)
            && selectedKey is not null
            && await TryHandleTextApprovalResponseAsync(message, selectedKey))
        {
            return;
        }

        using var inboundCts = new CancellationTokenSource(InboundProcessingTimeout);
        var classification = await PromptClassifier.ClassifyAsync(
            _promptInjectionDetector, message.Text, "discord-live", _log, inboundCts.Token);
        switch (classification.Outcome)
        {
            case ClassificationOutcome.Block:
                _log.Warning("Blocked Discord message due to prompt injection risk: {Reason}", classification.Reason);
                ChannelTelemetry.RecordDiscordEventDropped("prompt_injection_high");
                await SafeReplyAsync(LiveInjectionBlockedWarning);
                return;

            case ClassificationOutcome.DetectorUnavailable:
                _log.Warning("Prompt injection detector unavailable for live message — dropping");
                ChannelTelemetry.RecordDiscordEventDropped("prompt_injection_detector_unavailable");
                await SafeReplyAsync(LiveDetectorUnavailableWarning);
                return;

            case ClassificationOutcome.Allow:
                break;
        }

        var writer = _handle.InputQueue;
        if (writer is null)
        {
            _log.Warning("Discord input queue is not initialized; dropping inbound message");
            return;
        }

        var liveContents = new List<AIContent> { new TextContent(message.Text) };
        var mergedContents = await BuildInputContentsAsync(liveContents, inboundCts.Token);

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
            ReceivedAt = message.ReceivedAt
        };

        try
        {
            using var writeCts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await writer.WriteAsync(input, writeCts.Token);
            ChannelTelemetry.RecordDiscordMessageEnqueued();
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

    private async Task<IReadOnlyList<AIContent>> BuildInputContentsAsync(
        List<AIContent> liveContents,
        CancellationToken cancellationToken)
    {
        if (_threadHistoryFetchAttempted || _dependencies.ThreadHistoryFetcher is not { } fetcher)
            return liveContents;

        _threadHistoryFetchAttempted = true;

        IReadOnlyList<ChannelInput> history;
        try
        {
            history = await fetcher.FetchThreadHistoryAsync(_sessionId, cancellationToken);
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "Thread history fetch failed for session {0}", _sessionId.Value);
            return liveContents;
        }

        if (history.Count == 0)
            return liveContents;

        _log.Info("Thread history hydrated fetched={FetchedCount} session={Session}",
            history.Count, _sessionId.Value);

        return MergeHistoryWithLiveContents(history, liveContents);
    }

    private static List<AIContent> MergeHistoryWithLiveContents(
        IReadOnlyList<ChannelInput> history,
        IReadOnlyList<AIContent> liveContents)
    {
        var sb = new StringBuilder();
        sb.AppendLine("[thread history — messages exchanged before this inbound event]");
        sb.AppendLine();

        foreach (var item in history)
        {
            var ts = item.ReceivedAt == default ? string.Empty : $", {item.ReceivedAt:yyyy-MM-dd HH:mm} UTC";
            sb.AppendLine($"<user: {item.SenderId}{ts}>");

            foreach (var content in item.Contents)
            {
                if (content is TextContent text && !string.IsNullOrWhiteSpace(text.Text))
                    sb.AppendLine(text.Text);
            }

            sb.AppendLine();
        }

        sb.AppendLine("[end thread history]");

        var liveText = string.Join("\n", liveContents
            .OfType<TextContent>()
            .Select(t => t.Text)
            .Where(t => !string.IsNullOrWhiteSpace(t)));

        var mergedText = string.IsNullOrWhiteSpace(liveText)
            ? sb.ToString()
            : $"{sb}\n\n{liveText}";

        var merged = new List<AIContent> { new TextContent(mergedText) };

        foreach (var content in liveContents)
        {
            if (content is not TextContent)
                merged.Add(content);
        }

        return merged;
    }

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

        await SafeReplyAsync(DiscordApprovalPromptBuilder.BuildDecisionStatus(selectedKey));
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
            ChannelTelemetry.RecordDiscordInteractionError("unknown_call_id");
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

        await SafeReplyAsync(DiscordApprovalPromptBuilder.BuildDecisionStatus(message.SelectedKey));
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
            if (byCallId.RequesterPrincipal is not PrincipalClassification.VerifiedAutomation
                && byCallId.RequesterSenderId is not null && byCallId.RequesterSenderId != senderId)
                return (ApprovalLookupResult.WrongRequester, null);
            return (ApprovalLookupResult.Matched, byCallId);
        }

        if (_pendingApprovalRequests.Count == 0)
            return (ApprovalLookupResult.NotFound, null);

        var bySender = _pendingApprovalRequests.LastOrDefault(p =>
            p.RequesterPrincipal is PrincipalClassification.VerifiedAutomation
            || p.RequesterSenderId is null || p.RequesterSenderId == senderId);
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
                _pendingApprovalRequests.Add(new PendingApprovalRequest(
                    request.CallId,
                    request.RequesterSenderId is null ? null : new DiscordUserId(request.RequesterSenderId),
                    request.RequesterPrincipal));

                var (promptText, buttons) = DiscordApprovalPromptBuilder.BuildButtonPrompt(request);
                await SafeReplyWithButtonsAsync(promptText, buttons, request);
                break;

            case TurnCompleted completed:
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

    private async Task SafeReplyWithButtonsAsync(
        string text,
        IReadOnlyList<DiscordButtonSpec> buttons,
        ToolInteractionRequest request)
    {
        var startedAt = _dependencies.TimeProvider.GetTimestamp();
        try
        {
            var postMessage = BuildPostMessage(text, buttons: buttons);
            var result = await _dependencies.ReplyClient.PostReplyAsync(postMessage);
            ApplyThreadPromotion(result);
            var duration = _dependencies.TimeProvider.GetElapsedTime(startedAt).TotalMilliseconds;
            ChannelTelemetry.RecordDiscordReplyPosted(duration);
            ChannelTelemetry.RecordDiscordApprovalFallbackActivated("button_prompt");
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "Failed posting Discord button prompt; falling back to text-only");
            ChannelTelemetry.RecordDiscordApprovalFallbackActivated("text_prompt");
            try
            {
                var fallbackText = DiscordApprovalPromptBuilder.BuildTextPrompt(request);
                var postMessage = BuildPostMessage(fallbackText);
                var result = await _dependencies.ReplyClient.PostReplyAsync(postMessage);
                ApplyThreadPromotion(result);
            }
            catch (Exception textEx)
            {
                _log.Error(textEx, "Failed posting text-only approval fallback; auto-denying request");
                await SendApprovalDenyOnFailureAsync(request.CallId);
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
                ChannelTelemetry.RecordDiscordReplyPosted(duration);
            }
            catch (Exception ex)
            {
                var duration = _dependencies.TimeProvider.GetElapsedTime(startedAt).TotalMilliseconds;
                _log.Warning(ex, "Failed posting Discord reply for session {0}", _sessionId.Value);
                ChannelTelemetry.RecordDiscordReplyFailed(duration);
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

    private sealed record InitializePipeline
    {
        public static readonly InitializePipeline Instance = new();
    }

    private sealed record OutputReceived(SessionOutput Output);

    private sealed record OutputStreamTerminated(int Generation, Exception? Cause);

    private sealed record ReinitializePipeline(string Reason);
}
