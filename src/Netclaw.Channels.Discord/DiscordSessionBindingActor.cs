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
    private readonly DiscordReplyChannelId _replyChannelId;
    private readonly DiscordThreadOrMessageId _threadOrMessageId;
    private readonly DiscordMessageId? _rootMessageId;
    private const int MaxDiscordMessageLength = 2000;
    private const string EmptyTurnFallbackText =
        ":warning: I didn't manage to produce a reply. Please try rephrasing or sending your message again.";
    private const string LiveInjectionBlockedWarning =
        ":warning: Message blocked by prompt-injection policy.";
    private const string LiveDetectorUnavailableWarning =
        ":warning: I couldn't safely analyze your message — please try again in a moment.";

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

        var classification = await ClassifyAsync(message.Text, "discord-live");
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

        var input = new ChannelInput
        {
            SenderId = message.SenderId.Value,
            ChannelId = message.ChannelId.Value,
            MessageId = message.EventId.Value,
            Audience = message.Audience,
            Boundary = SecurityPolicyDefaults.TrustedInstanceBoundary,
            Principal = message.Principal,
            Provenance = message.Provenance,
            Contents = [new TextContent(message.Text)],
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

    private async Task<bool> TryHandleTextApprovalResponseAsync(DiscordThreadInbound message, string selectedKey)
    {
        var pending = ResolvePendingRequest(message.SenderId, callId: null);
        if (pending is null)
            return false;

        _pendingApprovalRequests.Remove(pending);

        await _dependencies.Pipeline.SendFeedbackAsync(new ToolInteractionResponse
        {
            SessionId = _sessionId,
            CallId = pending.CallId,
            SelectedKey = selectedKey,
            SenderId = message.SenderId.Value
        });

        await SafeReplyAsync(DiscordApprovalPromptBuilder.BuildDecisionStatus(selectedKey));
        return true;
    }

    private async Task HandleApprovalResponseAsync(DiscordApprovalResponse message)
    {
        var pending = ResolvePendingRequest(message.SenderId, message.CallId);
        if (pending is null)
        {
            _log.Info("Ignoring Discord approval response for unknown call id {0}", message.CallId);
            ChannelTelemetry.RecordDiscordInteractionError("unknown_call_id");
            return;
        }

        _pendingApprovalRequests.Remove(pending);

        await _dependencies.Pipeline.SendFeedbackAsync(new ToolInteractionResponse
        {
            SessionId = _sessionId,
            CallId = message.CallId,
            SelectedKey = message.SelectedKey,
            SenderId = message.SenderId.Value
        });

        await SafeReplyAsync(DiscordApprovalPromptBuilder.BuildDecisionStatus(message.SelectedKey));
    }

    private PendingApprovalRequest? ResolvePendingRequest(DiscordUserId senderId, string? callId)
    {
        if (callId is not null)
        {
            return _pendingApprovalRequests.LastOrDefault(p =>
                string.Equals(p.CallId, callId, StringComparison.Ordinal)
                && (p.RequesterSenderId is null || p.RequesterSenderId == senderId));
        }

        return _pendingApprovalRequests.LastOrDefault(p =>
            p.RequesterSenderId is null || p.RequesterSenderId == senderId);
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
                    request.RequesterSenderId is null ? null : new DiscordUserId(request.RequesterSenderId)));

                ChannelTelemetry.RecordDiscordApprovalFallbackActivated("text_prompt");
                await SafeReplyAsync(DiscordApprovalPromptBuilder.BuildTextPrompt(request));
                _deliveredThisTurn = true;
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

    private async Task SafeReplyAsync(string text)
    {
        var chunks = ChunkMessage(text);
        foreach (var chunk in chunks)
        {
            var startedAt = _dependencies.TimeProvider.GetTimestamp();
            try
            {
                await _dependencies.ReplyClient.PostReplyAsync(new DiscordPostMessage(
                    ReplyChannelId: _replyChannelId,
                    Text: chunk,
                    RootMessageId: _rootMessageId));
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

    private async Task<Classification> ClassifyAsync(string? text, string sourceContext)
    {
        if (string.IsNullOrWhiteSpace(text))
            return new Classification(ClassificationOutcome.Allow, null);

        PromptInjectionResult detection;
        try
        {
            detection = await _promptInjectionDetector.DetectAsync(text, sourceContext);
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "Prompt injection detector failed for source={Source}", sourceContext);
            return new Classification(ClassificationOutcome.DetectorUnavailable, ex.Message);
        }

        if (detection.Risk != PromptInjectionRisk.High)
            return new Classification(ClassificationOutcome.Allow, null);

        var reason = string.IsNullOrWhiteSpace(detection.Message)
            ? "High-risk prompt injection pattern detected"
            : detection.Message;
        return new Classification(ClassificationOutcome.Block, reason);
    }

    private enum ClassificationOutcome { Allow, Block, DetectorUnavailable }

    private readonly record struct Classification(ClassificationOutcome Outcome, string? Reason);

    private sealed record InitializePipeline
    {
        public static readonly InitializePipeline Instance = new();
    }

    private sealed record OutputReceived(SessionOutput Output);

    private sealed record OutputStreamTerminated(int Generation, Exception? Cause);

    private sealed record ReinitializePipeline(string Reason);
}
