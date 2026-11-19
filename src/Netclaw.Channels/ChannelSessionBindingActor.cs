// -----------------------------------------------------------------------
// <copyright file="ChannelSessionBindingActor.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Threading.Channels;
using Akka.Actor;
using Akka.Event;
using Akka.Persistence;
using Microsoft.Extensions.AI;
using Netclaw.Actors.Channels;
using Netclaw.Actors.Protocol;
using Netclaw.Actors.Reminders;
using Netclaw.Channels.Telemetry;
using Netclaw.Configuration;
using Netclaw.Security;
using Netclaw.Tools;
using static Netclaw.Actors.Reminders.ReminderProtocol;
using static Netclaw.Actors.Sessions.SessionProtocol;

namespace Netclaw.Channels;

internal abstract class ChannelSessionBindingActor<TPromptId> : ReceivePersistentActor, IWithTimers
    where TPromptId : struct
{
    protected readonly SessionId _sessionId;
    protected readonly ILoggingAdapter _log;
    private readonly SessionPipelineHandle _handle;
    private readonly ThreadGapHydrationEngine? _hydrationEngine;
    private readonly ChannelOutputEngine<PendingApprovalRequest<TPromptId>, TPromptId> _outputEngine;
    protected readonly ApprovalResponseFlow<PendingApprovalRequest<TPromptId>, TPromptId> _approvalFlow;
    private bool _hydrationPending;
    private readonly List<PendingApprovalRequest<TPromptId>> _pendingApprovalRequests = [];
    private string? _cursor;
    private readonly ISessionPipeline _pipeline;
    private readonly IPromptInjectionDetector _detector;
    private readonly SessionIngressGate? _ingressGate;
    private readonly TimeProvider _timeProvider;
    private readonly IThreadHistoryFetcher? _historyFetcher;
    private readonly string _channelId;
    private readonly ChannelType _channelType;
    private readonly IComparer<string> _cursorComparer;
    private TransportResult.Failure? _heldFailedPost;
    protected readonly SafeTransportCall _safeCall;
    private static readonly object ReinitializeTimerKey = new();
    private static readonly TimeSpan ReinitializeDelay = TimeSpan.FromSeconds(2);
    protected static readonly TimeSpan OperationTimeout = TimeSpan.FromSeconds(10);

    public ITimerScheduler Timers { get; set; } = null!;

    protected ChannelSessionBindingActor(
        SessionId sessionId,
        ISessionPipeline pipeline,
        IPromptInjectionDetector detector,
        SessionIngressGate? ingressGate,
        TimeProvider timeProvider,
        IThreadHistoryFetcher? historyFetcher,
        ChannelType channelType,
        string channelId,
        IComparer<string> cursorComparer,
        ILoggingAdapter log)
    {
        _sessionId = sessionId;
        _pipeline = pipeline;
        _detector = detector;
        _ingressGate = ingressGate;
        _timeProvider = timeProvider;
        _historyFetcher = historyFetcher;
        _channelType = channelType;
        _channelId = channelId;
        _cursorComparer = cursorComparer;
        _log = log;
        _handle = new SessionPipelineHandle(_pipeline, _log, $"{_channelType.ToWireValue()}-session");
        _safeCall = new SafeTransportCall(_channelType, _timeProvider);
        _outputEngine = new ChannelOutputEngine<PendingApprovalRequest<TPromptId>, TPromptId>(
            cursorComparer: _cursorComparer,
            pendingRequests: _pendingApprovalRequests,
            createPendingRequest: request => new PendingApprovalRequest<TPromptId>(request),
            isApprovalRequest: IsApprovalRequest,
            renderTextOutput: RenderTextOutput,
            renderErrorOutput: RenderErrorOutput,
            postTextAsync: PostOutputTextAsync,
            uploadFileAsync: UploadOutputFileAsync,
            postApprovalPromptAsync: PostApprovalPromptAsync,
            readPromptIdValue: FormatPromptId,
            onApprovalPromptFailedAsync: SendApprovalPromptUnavailableAsync,
            redrawClosedPromptAsync: RedrawClosedApprovalPromptAsync,
            persistPromptTracked: tracked => Persist(tracked, ApplyPendingApprovalPromptTracked),
            handleChannelSpecificOutputAsync: HandleChannelSpecificOutputAsync,
            advanceCursor: AdvanceCursor,
            postEmptyTurnFallbackAsync: PostEmptyTurnFallbackAsync,
            onEmptyTurnSuppressedAsync: ReportHeldFailedPostAsync);
        if (_historyFetcher is not null)
        {
            _hydrationEngine = new ThreadGapHydrationEngine(
                sessionId: _sessionId,
                channelType: _channelType,
                historyFetcher: _historyFetcher,
                injectionDetector: _detector,
                classifierSourceContext: $"{_channelType.ToWireValue()}-backfill",
                cursorComparer: _cursorComparer,
                cursorKeySelector: NormalizeCursor,
                isAuthorizedSender: IsAuthorizedSender,
                log: _log,
                readCursor: () => _cursor,
                readInputQueue: () => _handle.InputQueue,
                readIngressClosedReason: () => _ingressGate?.ClosedReason,
                warnBackfillDetectorUnavailableAsync: () => PostTextAsync(
                    ":warning: I couldn't safely analyze some earlier thread messages, so they were excluded from context."),
                onBackfillEnqueued: _outputEngine.AdvancePendingCursorForEnqueuedTurn,
                setHydrationPending: pending => _hydrationPending = pending);
        }
        _approvalFlow = new ApprovalResponseFlow<PendingApprovalRequest<TPromptId>, TPromptId>(
            sessionId: _sessionId,
            channelType: _channelType,
            channelName: _channelType.ToString(),
            pipeline: _pipeline,
            operationTimeout: OperationTimeout,
            pendingRequests: _pendingApprovalRequests,
            hasObservedApprovalRequest: () => _outputEngine.HasObservedApprovalRequest,
            postNoticeAsync: text => PostTextAsync(text),
            persistPromptCleared: callId => Persist(
                new PendingApprovalPromptCleared { CallId = callId.Value },
                ApplyPendingApprovalPromptCleared),
            renderResolvedPromptAsync: TryResolveApprovalPromptAsync,
            redrawClosedPromptAsync: RedrawClosedApprovalPromptAsync,
            log: _log);
        Recover<CursorAdvanced>(ApplyCursorAdvanced);
        Recover<PendingApprovalPromptTracked>(ApplyPendingApprovalPromptTracked);
        Recover<PendingApprovalPromptCleared>(ApplyPendingApprovalPromptCleared);
        // Recovery can finish before pipeline initialization. Hydrating must receive the stashed trigger.
        Recover<RecoveryCompleted>(_ => Self.Tell(PerformHydration.Instance));
        Initializing();
    }

    private SessionPipelineOptions BuildOptions() => new()
    {
        ChannelType = _channelType,
        Filter = OutputFilter.Text | OutputFilter.Files | OutputFilter.ProcessingState
    };
    protected abstract void RegisterProviderCommands();
    protected virtual bool IsApprovalRequest(ToolInteractionRequest request) =>
        string.Equals(request.Kind, "approval", StringComparison.OrdinalIgnoreCase);
    protected virtual string? RenderTextOutput(TextOutput output) => output.Text;
    protected virtual string RenderErrorOutput(ErrorOutput output) => $":warning: {output.Message}";
    protected abstract Task<TransportResult> PostTextAsync(string text);
    protected abstract Task<TransportResult> UploadFileAsync(FileOutput file);
    protected abstract Task<TPromptId?> PostApprovalPromptAsync(ToolInteractionRequest request);
    protected abstract string FormatPromptId(TPromptId id);
    protected abstract TPromptId ParsePromptId(string value);
    protected abstract Task RedrawClosedApprovalPromptAsync(PendingApprovalRequest<TPromptId> pending, string closeText);
    protected abstract Task TryResolveApprovalPromptAsync(TPromptId? promptId, ToolInteractionRequest? request,
        ToolCallId callId, string selectedKey, string senderId, string? persistedToolName, string? persistedDisplayText);
    protected abstract Task HandleChannelSpecificOutputAsync(SessionOutput output);
    protected abstract string? NormalizeCursor(string? value);
    protected virtual bool TryReadStoredCursor(string raw, out string? cursor)
    {
        cursor = raw;
        return true;
    }
    protected abstract bool IsAuthorizedSender(string senderId);
    protected abstract ChannelDeliveryTargetInfo BuildDefaultDeliveryTarget();
    protected virtual void BeforeDrain() { }
    protected virtual void BeforePipelineReinitialize() { }
    protected abstract TimeSpan InitializationTimeout { get; }
    protected abstract void PersistCursor(CursorAdvanced value, Action<CursorAdvanced> apply);
    protected virtual Task PostPromptUnavailableNoticeAsync(ToolInteractionRequest request) => Task.CompletedTask;

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
                Stash.UnstashAll();
            }
            catch (Exception ex)
            {
                _log.Error(ex, "Failed to initialize {Channel} session pipeline; stopping actor", _channelType);
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
        RegisterProviderCommands();
        CommandAsync<DeliverTrustedSessionTurn>(HandleTrustedReminderAsync);
        CommandAsync<OutputReceived>(HandleOutputReceivedAsync);
        Command<OutputStreamTerminated>(msg =>
        {
            if (msg.Generation != _handle.Generation)
                return;
            var reason = msg.Cause is null ? "completed" : $"faulted: {msg.Cause.Message}";
            _log.Warning("Output stream terminated ({Reason}); reinitializing pipeline", reason);
            RequestPipelineReinitialization(reason);
        });
        CommandAsync<ReinitializePipeline>(async msg =>
        {
            BeforePipelineReinitialize();
            _heldFailedPost = null;
            _outputEngine.ResetForPipelineReinitialize();
            await _handle.ReinitializeAsync(
                msg.Reason,
                () => Timers.StartSingleTimer(
                    ReinitializeTimerKey,
                    new ReinitializePipeline("retry after failed reinit"),
                    ReinitializeDelay));
        });
    }

    protected async Task<bool> AcceptIngressAsync()
    {
        if (_ingressGate?.ClosedReason is not { } reason)
            return true;
        _log.Info("Rejecting {Channel} inbound message while restart drain is active", _channelType);
        await PostTextAsync(reason);
        return false;
    }

    protected bool InputQueueReady()
    {
        if (_handle.InputQueue is not null)
            return true;
        _log.Warning("Input queue is not initialized; dropping inbound message");
        return false;
    }

    protected async Task<bool> ScanLiveTextAsync(string? text, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(text))
            return true;
        var classification = await PromptClassifier.ClassifyAsync(
            _detector, text, $"{_channelType.ToWireValue()}-live", _log, cancellationToken);
        switch (classification.Outcome)
        {
            case ClassificationOutcome.Block:
                _log.Warning("Blocked {Channel} message due to prompt injection risk: {Reason}", _channelType, classification.Reason);
                ChannelTelemetry.For(_channelType).RecordEventDropped("prompt_injection_high");
                await PostTextAsync(":warning: Message blocked by prompt-injection policy.");
                return false;
            case ClassificationOutcome.DetectorUnavailable:
                _log.Warning("Prompt injection detector unavailable for live message; dropping");
                ChannelTelemetry.For(_channelType).RecordEventDropped("prompt_injection_detector_unavailable");
                await PostTextAsync(":warning: I couldn't safely analyze your message — please try again in a moment.");
                return false;
            case ClassificationOutcome.Allow:
                return true;
            default:
                throw new InvalidOperationException($"Unknown classification outcome: {classification.Outcome}");
        }
    }

    protected async Task<ChannelInput> PrepareLiveInputAsync(
        ChannelInput input, bool mentionRequired, CancellationToken cancellationToken)
    {
        if (!_hydrationPending && !_outputEngine.TurnInFlight && mentionRequired)
            _hydrationPending = true;
        if (_hydrationPending && IsAuthorizedSender(input.SenderId.Value) && _hydrationEngine is { } engine)
            return await engine.ApplyDeferredHydrationAsync(input, input.MessageId!, cancellationToken);
        return input;
    }

    protected async Task WriteLiveInputAsync(ChannelInput input, CancellationToken cancellationToken)
    {
        await _handle.InputQueue!.WriteAsync(input, cancellationToken);
        _outputEngine.AdvancePendingCursorForEnqueuedTurn(NormalizeCursor(input.MessageId));
        ChannelTelemetry.For(_channelType).RecordMessageEnqueued();
    }

    protected void RequestPipelineReinitialization(string reason) => Self.Tell(new ReinitializePipeline(reason));

    protected async Task EnsureInitializedAsync()
    {
        if (_handle.IsInitialized)
            return;
        var self = Self;
        using var initCts = new CancellationTokenSource(InitializationTimeout, _timeProvider);
        await _handle.InitializeWithChannelAsync(
            Context,
            _sessionId,
            BuildOptions(),
            output => self.Tell(new OutputReceived(output)),
            (generation, cause) => self.Tell(new OutputStreamTerminated(generation, cause)),
            initCts.Token);
    }

    private async Task HandleTrustedReminderAsync(DeliverTrustedSessionTurn message)
    {
        var ackTarget = Sender;
        if (message.SessionId != _sessionId)
        {
            _log.Warning("Dropping DeliverTrustedSessionTurn with mismatching session id actual={Actual} expected={Expected}",
                message.SessionId.Value, _sessionId.Value);
            ackTarget.Tell(CommandNack.For(_sessionId, "Session id mismatch"));
            return;
        }
        if (_ingressGate?.ClosedReason is { } ingressClosedReason)
        {
            _log.Info("Rejecting Mode B reminder while restart drain is active");
            ackTarget.Tell(CommandNack.For(_sessionId, ingressClosedReason));
            return;
        }
        var writer = _handle.InputQueue;
        if (writer is null)
        {
            _log.Warning("Input queue is not initialized; rejecting Mode B reminder");
            var reason = $"{_channelType} session pipeline not initialized";
            ackTarget.Tell(CommandNack.For(_sessionId, reason));
            return;
        }
        var input = new ChannelInput
        {
            SenderId = message.Source.SenderId,
            ChannelId = _channelId,
            MessageId = message.Source.MessageId,
            Audience = message.Source.Audience,
            Boundary = message.Source.Boundary,
            Principal = message.Source.Principal,
            Provenance = message.Source.Provenance,
            Contents = [new TextContent(message.Content)],
            ReceivedAt = _timeProvider.GetUtcNow(),
            DefaultDeliveryTarget = BuildDefaultDeliveryTarget(),
            RequestedDeliveryTarget = message.Source.RequestedDeliveryTarget,
            ReminderId = message.Source.ReminderId,
            AckTarget = ackTarget
        };
        try
        {
            using var writeCts = new CancellationTokenSource(OperationTimeout, _timeProvider);
            await writer.WriteAsync(input, writeCts.Token);
            _log.Debug("reminder_mode_b_dispatch session={Session} reminder={Reminder}", _sessionId.Value, message.Source.ReminderId);
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

    private async Task HandleOutputReceivedAsync(OutputReceived msg)
    {
        if (msg.Output is SessionDeactivated)
        {
            BeforeDrain();
            await _handle.DrainAsync();
            Context.Stop(Self);
            return;
        }
        var clearedPrompts = await _outputEngine.HandleOutputAsync(msg.Output);
        if (msg.Output is TurnCompleted)
            _heldFailedPost = null;
        if (clearedPrompts.Count > 0)
            PersistAll(clearedPrompts, ApplyPendingApprovalPromptCleared);
    }

    private async Task SendApprovalPromptUnavailableAsync(ToolInteractionRequest request)
    {
        var callId = request.CallId;
        _log.Warning("Refusing {CallId} ({ToolName}) because the approval prompt could not be posted", callId, request.ToolName);
        try
        {
            await _pipeline.SendFeedbackAsync(new ToolInteractionResponse
            {
                SessionId = _sessionId,
                CallId = callId,
                SelectedKey = ApprovalOptionKeys.PromptUnavailableKey,
                SenderId = request.RequesterSenderId ?? new SenderId(string.Empty)
            });
        }
        catch (Exception ex)
        {
            _log.Error(ex, "Failed to send prompt-unavailable feedback for call {CallId}", callId);
        }
        await PostPromptUnavailableNoticeAsync(request);
    }

    private async Task<bool> PostOutputTextAsync(string text) => RecordOutputResult(await PostTextAsync(text));

    private async Task<bool> UploadOutputFileAsync(FileOutput file) => RecordOutputResult(await UploadFileAsync(file));

    private bool RecordOutputResult(TransportResult result)
    {
        if (result is TransportResult.Failure failure)
        {
            _heldFailedPost ??= failure;
            return false;
        }
        return true;
    }

    private async Task PostEmptyTurnFallbackAsync(TurnCompleted completed)
    {
        _heldFailedPost = null;
        _log.Warning("Turn completed without visible output; posting fallback reply");
        await PostOutputTextAsync(":warning: I didn't manage to produce a reply. Please try rephrasing or sending your message again.");
        await ReportHeldFailedPostAsync(completed);
    }

    private async Task ReportHeldFailedPostAsync(TurnCompleted completed)
    {
        if (_heldFailedPost is not { } held)
            return;
        _heldFailedPost = null;
        try
        {
            await _pipeline.SendFeedbackAsync(new DeliveryFailed
            {
                SessionId = _sessionId,
                TurnNumber = completed.TurnNumber,
                ChannelType = _channelType,
                FailureKind = held.Kind,
                ErrorMessage = held.Message
            });
        }
        catch (Exception ex)
        {
            _log.Error(ex, "Failed to send delivery feedback to session; propagating to trigger pipeline reinit");
            throw;
        }
    }

    private void AdvanceCursor(string candidate)
    {
        if (_cursor is not null && _cursorComparer.Compare(candidate, _cursor) <= 0)
        {
            _log.Debug("Session cursor did not advance session={Session} postId={PostId}", _sessionId.Value, candidate);
            return;
        }
        PersistCursor(new CursorAdvanced(candidate), ApplyCursorAdvanced);
    }

    protected bool IsStaleCursor(string? candidate)
    {
        if (candidate is null)
            return false;

        return (_cursor is not null && _cursorComparer.Compare(candidate, _cursor) <= 0)
            || (_outputEngine.PendingCursor is { } pending && _cursorComparer.Compare(candidate, pending) <= 0);
    }

    private void ApplyCursorAdvanced(CursorAdvanced advanced)
    {
        if (!TryReadStoredCursor(advanced.Cursor, out var cursor))
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
        _outputEngine.MarkApprovalRequestObserved();
        PendingApprovalRecovery.ApplyTracked<PendingApprovalRequest<TPromptId>, TPromptId>(
            _pendingApprovalRequests,
            tracked,
            ParsePromptId,
            (callId, requesterSenderId, requesterPrincipal, optionKeys, promptId, lifetime, toolName, displayText) =>
                new PendingApprovalRequest<TPromptId>(callId, requesterSenderId, requesterPrincipal, optionKeys, promptId, lifetime, toolName, displayText));
    }

    private void ApplyPendingApprovalPromptCleared(PendingApprovalPromptCleared cleared)
        => PendingApprovalRecovery.ApplyCleared<PendingApprovalRequest<TPromptId>, TPromptId>(_pendingApprovalRequests, cleared);

    private sealed record InitializePipeline : INoSerializationVerificationNeeded
    {
        public static readonly InitializePipeline Instance = new();
    }

    private sealed record PerformHydration : INoSerializationVerificationNeeded
    {
        public static readonly PerformHydration Instance = new();
    }

    private sealed record OutputReceived(SessionOutput Output) : INoSerializationVerificationNeeded;
    private sealed record OutputStreamTerminated(int Generation, Exception? Cause) : INoSerializationVerificationNeeded;
    private sealed record ReinitializePipeline(string Reason) : INoSerializationVerificationNeeded;
}
