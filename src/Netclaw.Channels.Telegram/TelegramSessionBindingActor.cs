// -----------------------------------------------------------------------
// <copyright file="TelegramSessionBindingActor.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Globalization;
using System.Threading.Channels;
using Akka.Actor;
using Akka.Event;
using Akka.Persistence;
using Microsoft.Extensions.AI;
using Netclaw.Actors.Channels;
using Netclaw.Actors.Protocol;
using Netclaw.Actors.Reminders;
using Netclaw.Channels;
using Netclaw.Configuration;
using Netclaw.Tools;
using static Netclaw.Actors.Sessions.SessionProtocol;
using static Netclaw.Actors.Reminders.ReminderProtocol;

namespace Netclaw.Channels.Telegram;

internal sealed class TelegramSessionBindingActor : ReceivePersistentActor, IWithTimers
{
    private static readonly TimeSpan OperationTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan TypingRefreshInterval = TimeSpan.FromSeconds(4);
    private static readonly object TypingTimerKey = new();

    // Same empty-turn fallback text the other channel bindings post; the
    // shared session contract pins the wording.
    internal const string EmptyTurnFallbackText =
        ":warning: I didn't manage to produce a reply. Please try rephrasing or sending your message again.";

    internal const string ExpiredApprovalText = "This approval request expired.";
    internal const string WrongRequesterText = "Only the requester can approve this action.";
    internal const string NoLongerPendingText = "This approval request is no longer pending.";
    internal const string FeedbackFailedText = "Netclaw could not record this decision.";
    internal const string DecisionRecordedText = "Decision recorded.";

    private readonly SessionId _sessionId;
    private readonly TelegramChatId _chatId;
    private readonly int? _messageThreadId;
    private readonly TelegramGatewayDependencies _dependencies;
    private readonly SessionPipelineHandle _handle;
    private readonly ILoggingAdapter _log;
    private readonly SafeTransportCall _safeTransport;
    private readonly ChannelOutputEngine<PendingApprovalRequest<int>, int> _outputEngine;
    private readonly ApprovalResponseFlow<PendingApprovalRequest<int>, int> _approvalFlow;

    // Recovery scratch state for posted approval prompts, keyed by the prompt's
    // Telegram message id. The journal (PendingApprovalPromptTracked/Cleared)
    // is the durable record; replay on cold spawn repopulates this list so a
    // callback resolves by prompt message id after passivation or restart.
    private readonly List<PendingApprovalRequest<int>> _pendingApprovalRequests = [];

    private volatile bool _processingIndicatorActive;

    public ITimerScheduler Timers { get; set; } = null!;

    public TelegramSessionBindingActor(
        SessionId sessionId,
        TelegramChatId chatId,
        TelegramGatewayDependencies dependencies)
    {
        _sessionId = sessionId;
        _chatId = chatId;

        // Forum-topic replies must land in the topic the session lives in.
        // Proactive and reminder sessions use the "chat" fallback key, which is
        // not numeric, so they send without a topic.
        _messageThreadId = SessionIdFormat.TrySplit(sessionId, out _, out var threadKey)
            && int.TryParse(threadKey, out var parsedThreadId)
            ? parsedThreadId
            : null;

        _dependencies = dependencies;
        _log = Context.GetLogger().WithContext("Adapter", "telegram");
        _handle = new SessionPipelineHandle(dependencies.Pipeline, _log, "telegram");

        _safeTransport = new SafeTransportCall(
            ChannelType.Telegram,
            dependencies.TimeProvider,
            NotifyDeliveryFailedAsync);

        _outputEngine = new ChannelOutputEngine<PendingApprovalRequest<int>, int>(
            channelType: ChannelType.Telegram,
            channelName: "Telegram",
            // Telegram sessions have no cursor semantics; the engine's cursor
            // bookkeeping is inert without a cursor key supplier.
            cursorComparer: StringComparer.Ordinal,
            pendingRequests: _pendingApprovalRequests,
            createPendingRequest: request => new PendingApprovalRequest<int>(request),
            isApprovalRequest: request => string.Equals(request.Kind, "approval", StringComparison.OrdinalIgnoreCase),
            renderTextOutput: output => string.IsNullOrWhiteSpace(output.Text) ? null : output.Text,
            renderErrorOutput: output => $"Sorry, NetClaw had a problem: {output.Message}",
            postTextAsync: PostReplyAsync,
            uploadFileAsync: SendFileOutputAsync,
            postApprovalPromptAsync: SendApprovalPromptAsync,
            readPromptIdValue: promptMessageId => promptMessageId.ToString(CultureInfo.InvariantCulture),
            onApprovalPromptFailedAsync: request => SendApprovalDenyOnFailureAsync(request.CallId),
            persistPromptTracked: tracked => Persist(tracked, ApplyPendingApprovalPromptTracked),
            handleChannelSpecificOutputAsync: HandleChannelSpecificOutputAsync,
            advanceCursor: _ => { },
            postEmptyTurnFallbackAsync: () => PostReplyAsync(EmptyTurnFallbackText),
            onEmptyTurnSuppressedAsync: _ => Task.CompletedTask,
            readObservedAtMs: completed => completed.TimestampMs);

        // Telegram approval feedback arrives as button clicks only; the
        // callback answer below carries the wrong-requester warning, so the
        // flow's separate warning post is intentionally a no-op here.
        _approvalFlow = new ApprovalResponseFlow<PendingApprovalRequest<int>, int>(
            sessionId: _sessionId,
            channelType: ChannelType.Telegram,
            channelName: "Telegram",
            pipeline: _dependencies.Pipeline,
            operationTimeout: OperationTimeout,
            pendingRequests: _pendingApprovalRequests,
            hasObservedApprovalRequest: () => _outputEngine.HasObservedApprovalRequest,
            postWrongRequesterWarningAsync: () => Task.CompletedTask,
            persistPromptCleared: callId => Persist(
                new PendingApprovalPromptCleared { CallId = callId.Value },
                ApplyPendingApprovalPromptCleared),
            renderResolvedPromptAsync: ResolveApprovalPromptAsync,
            log: _log);

        Recover<PendingApprovalPromptTracked>(ApplyPendingApprovalPromptTracked);
        Recover<PendingApprovalPromptCleared>(ApplyPendingApprovalPromptCleared);

        CommandAsync<TelegramSessionInbound>(HandleInboundAsync);
        CommandAsync<OutputReceived>(HandleOutputReceivedAsync);
        CommandAsync<TelegramCallbackQuery>(HandleCallbackAsync);
        CommandAsync<StartTelegramProactiveChat>(HandleProactiveChatAsync);
        CommandAsync<DeliverTrustedSessionTurn>(HandleTrustedReminderAsync);
        Command<OutputTerminated>(message =>
        {
            if (message.Generation == _handle.Generation)
                Context.Stop(Self);
        });
        CommandAsync<RefreshTyping>(HandleRefreshTypingAsync);
    }

    public static Props CreateProps(
        SessionId sessionId,
        TelegramChatId chatId,
        TelegramGatewayDependencies dependencies) =>
        Props.Create(() => new TelegramSessionBindingActor(sessionId, chatId, dependencies));

    public override string PersistenceId => $"telegram-session-binding-{Uri.EscapeDataString(_sessionId.Value)}";

    private async Task HandleInboundAsync(TelegramSessionInbound inbound)
    {
        await EnsureInitializedAsync();

        var writer = _handle.InputQueue;
        if (writer is null)
            return;

        var message = inbound.Message;
        var contents = new List<AIContent>();
        if (!string.IsNullOrWhiteSpace(inbound.Text))
            contents.Add(new TextContent(inbound.Text));

        if (message.Files is { Count: > 0 } files)
            await ProcessAttachmentsAsync(files, inbound.AclDecision.Audience, contents);

        if (contents.Count == 0)
            return;

        await writer.WriteAsync(new ChannelInput
        {
            SenderId = new SenderId(message.UserId!.Value.ToString()),
            ChannelId = message.ChatId.ToString(),
            MessageId = message.MessageId.ToString(),
            Audience = inbound.AclDecision.Audience,
            Boundary = TrustBoundary.TrustedInstance,
            Principal = inbound.AclDecision.Principal,
            Provenance = inbound.AclDecision.Provenance,
            Contents = contents,
            ExecutableText = inbound.Text,
            ReceivedAt = DateTimeOffset.UtcNow,
            DefaultDeliveryTarget = new ChannelDeliveryTargetInfo(
                "telegram", "destination", message.ChatId.ToString(), message.ChatId.ToString())
        });
        _outputEngine.AdvancePendingCursorForEnqueuedTurn(null);
    }

    private async Task HandleProactiveChatAsync(StartTelegramProactiveChat message)
    {
        await EnsureInitializedAsync();
        Sender.Tell(new TelegramProactiveChatAck(message.SessionId));
    }

    private async Task HandleTrustedReminderAsync(DeliverTrustedSessionTurn message)
    {
        var ackTarget = Sender;
        if (message.SessionId != _sessionId)
        {
            ackTarget.Tell(CommandNack.For(_sessionId, "Session id mismatch"));
            return;
        }

        if (_dependencies.IngressGate?.ClosedReason is { } closedReason)
        {
            ackTarget.Tell(CommandNack.For(_sessionId, closedReason));
            return;
        }

        await EnsureInitializedAsync();
        var writer = _handle.InputQueue;
        if (writer is null)
        {
            ackTarget.Tell(CommandNack.For(_sessionId, "Telegram session pipeline not initialized"));
            return;
        }

        if (message.Source.DeliveryObserver is { } observer
            && message.Source.ReminderId is { } reminderKey
            && !string.IsNullOrWhiteSpace(reminderKey.Value))
            _outputEngine.TrackReminderDeliveryObserver(reminderKey, observer);

        var input = new ChannelInput
        {
            SenderId = message.Source.SenderId,
            ChannelId = _chatId.Value.ToString(),
            MessageId = message.Source.MessageId,
            Audience = message.Source.Audience,
            Boundary = message.Source.Boundary,
            Principal = message.Source.Principal,
            Provenance = message.Source.Provenance,
            Contents = [new TextContent(message.Content)],
            ReceivedAt = message.Source.ReceivedAt,
            DefaultDeliveryTarget = BuildDefaultDeliveryTarget(),
            RequestedDeliveryTarget = message.Source.RequestedDeliveryTarget,
            ReminderId = message.Source.ReminderId,
            AckTarget = ackTarget
        };

        try
        {
            using var writeCts = new CancellationTokenSource(OperationTimeout);
            await writer.WriteAsync(input, writeCts.Token);
            _outputEngine.AdvancePendingCursorForEnqueuedTurn(null);
        }
        catch (OperationCanceledException)
        {
            ackTarget.Tell(CommandNack.For(_sessionId, "Pipeline enqueue timeout"));
        }
        catch (ChannelClosedException)
        {
            ackTarget.Tell(CommandNack.For(_sessionId, "Pipeline input queue closed"));
        }
    }

    private async Task EnsureInitializedAsync()
    {
        if (_handle.IsInitialized)
            return;

        var self = Self;
        await _handle.InitializeWithChannelAsync(
            Context,
            _sessionId,
            new SessionPipelineOptions
            {
                ChannelType = ChannelType.Telegram,
                Filter = OutputFilter.Full | OutputFilter.ProcessingState
            },
            output => self.Tell(new OutputReceived(output)),
            (generation, cause) => self.Tell(new OutputTerminated(generation, cause)));
    }

    private async Task ProcessAttachmentsAsync(
        IReadOnlyList<TelegramFileReference> files,
        TrustAudience audience,
        List<AIContent> contents)
    {
        var profile = ToolAudienceProfileDefaults.GetResolvedProfile(
            _dependencies.AudienceProfiles,
            audience);
        var policy = profile.ChannelAttachments ?? ChannelAttachmentPolicy.Empty;

        if (files.Count > policy.MaxFilesPerMessage)
        {
            await _dependencies.Transport.SendTextAsync(
                _chatId.Value,
                $"I can only accept up to {policy.MaxFilesPerMessage} files in one message.",
                messageThreadId: _messageThreadId);
            return;
        }

        var inlineImages = _dependencies.ModelCapabilities.InputModalities.HasFlag(ModelModality.Image);
        var storage = _dependencies.StorageResolver.Resolve(_sessionId);
        var inbox = SessionDirectoryHelper.GetOrCreateInboxDirectory(storage);
        var staging = SessionDirectoryHelper.GetOrCreateAttachmentStagingDirectory(storage);
        var acceptedLines = new List<string>();

        foreach (var file in files)
        {
            var result = await AttachmentIngressPipeline.IngestAsync(
                new AttachmentIngressRequest(file.Name, file.MimeType, file.Size),
                audience,
                policy,
                inlineImages,
                inbox,
                staging,
                TimeSpan.FromSeconds(30),
                _dependencies.ContentScanner,
                _log,
                (directory, maxBytes, cancellationToken) =>
                    _dependencies.Transport.DownloadFileAsync(file, directory, maxBytes, cancellationToken),
                CancellationToken.None);

            switch (result)
            {
                case AttachmentIngestOutcome.Accepted accepted:
                    acceptedLines.Add(accepted.Line);
                    if (accepted.Inline is { } inline)
                        contents.Add(inline);
                    break;
                case AttachmentIngestOutcome.Rejected rejected:
                    await _dependencies.Transport.SendTextAsync(
                        _chatId.Value,
                        rejected.UserFacingReason,
                        messageThreadId: _messageThreadId);
                    break;
            }
        }

        if (acceptedLines.Count > 0)
            contents.Add(new TextContent(string.Join('\n', acceptedLines)));
    }

    private async Task HandleOutputReceivedAsync(OutputReceived message)
    {
        var clearedPrompts = await _outputEngine.HandleOutputAsync(message.Output);
        if (clearedPrompts.Count > 0)
            PersistAll(clearedPrompts, ApplyPendingApprovalPromptCleared);
    }

    /// <summary>
    /// Handles the outputs the shared engine leaves to the channel. Telegram
    /// renders a processing indicator and keeps a typing refresh timer alive
    /// while the indicator is active.
    /// </summary>
    private async Task HandleChannelSpecificOutputAsync(SessionOutput output)
    {
        switch (output)
        {
            case ProcessingStateOutput { IsProcessing: true } processing:
                await RenderProcessingStateAsync(processing);
                break;

            case ProcessingStateOutput:
                _processingIndicatorActive = false;
                Timers.Cancel(TypingTimerKey);
                break;
        }
    }

    private void PostReplyFailureLogAsync(Exception ex)
        => _log.Warning(ex, "Failed to deliver Telegram reply to chat {ChatId}", _chatId.Value);

    private Task<bool> PostReplyAsync(string text)
        => _safeTransport.InvokeAsync(
            () => _dependencies.Transport.SendTextAsync(
                _chatId.Value, text, messageThreadId: _messageThreadId),
            PostReplyFailureLogAsync);

    private async Task<int?> SendApprovalPromptAsync(ToolInteractionRequest request)
    {
        // callback_data carries only the option key. Telegram caps callback_data
        // at 64 bytes and the shared ApprovalButtonValueCodec form can exceed
        // that with real call ids. Requester and call identity come from the
        // callback query sender and the pending request resolved by prompt
        // message id — never from the button payload.
        var buttons = request.Options
            .Select(option => new TelegramApprovalButton(option.Label, option.Key.Value))
            .ToArray();

        try
        {
            using var cts = new CancellationTokenSource(OperationTimeout);
            var messageId = await _dependencies.Transport.SendApprovalPromptAsync(
                _chatId.Value,
                TelegramApprovalPromptBuilder.BuildPrompt(request),
                buttons,
                messageThreadId: _messageThreadId,
                cancellationToken: cts.Token);
            _log.Info("Posted Telegram approval prompt for call {CallId}", request.CallId);
            return messageId;
        }
        catch (Exception ex)
        {
            _log.Error(ex, "Failed to post Telegram approval prompt; denying call {CallId}", request.CallId);
            // The engine routes this to the auto-deny hook so the blocked tool
            // call unwinds instead of waiting forever.
            return null;
        }
    }

    private async Task HandleCallbackAsync(TelegramCallbackQuery callback)
    {
        await EnsureInitializedAsync();

        // The prompt message id is the approval identity. An unknown message id
        // or an unknown option key fails closed as an expired approval; nothing
        // routes to the session.
        var pending = _pendingApprovalRequests.FirstOrDefault(p => p.PromptId == callback.MessageId);
        if (pending is null || !pending.OptionKeys.Contains(callback.Data, StringComparer.Ordinal))
        {
            await SafeAnswerCallbackAsync(callback.QueryId, ExpiredApprovalText, showAlert: true);
            return;
        }

        var senderId = callback.UserId.ToString(CultureInfo.InvariantCulture);
        ISessionResponse? verdict = null;
        await _approvalFlow.HandleApprovalResponseAsync(
            pending.CallId,
            callback.Data,
            senderId,
            payloadPromptId: callback.MessageId,
            respondSynchronously: response => verdict = response);

        switch (verdict)
        {
            case CommandAck:
                await SafeAnswerCallbackAsync(callback.QueryId, DecisionRecordedText);
                break;
            case CommandNack { Reason: ApprovalNackReasons.WrongRequester }:
                await SafeAnswerCallbackAsync(callback.QueryId, WrongRequesterText, showAlert: true);
                break;
            case CommandNack { Reason: ApprovalNackReasons.PersistFailed }:
                await SafeAnswerCallbackAsync(callback.QueryId, FeedbackFailedText, showAlert: true);
                break;
            default:
                await SafeAnswerCallbackAsync(callback.QueryId, NoLongerPendingText, showAlert: true);
                break;
        }
    }

    private async Task ResolveApprovalPromptAsync(
        int? promptMessageId,
        ToolInteractionRequest? request,
        ToolCallId callId,
        string selectedKey,
        string senderId,
        string? persistedToolName,
        string? persistedDisplayText)
    {
        if (promptMessageId is not { } messageId)
            return;

        var text = request is not null
            ? TelegramApprovalPromptBuilder.BuildResolvedPrompt(request, selectedKey, senderId)
            : TelegramApprovalPromptBuilder.BuildResolvedPromptWithoutRequest(
                selectedKey,
                senderId,
                persistedToolName);

        try
        {
            using var cts = new CancellationTokenSource(OperationTimeout);
            await _dependencies.Transport.UpdateApprovalPromptAsync(
                _chatId.Value,
                messageId,
                text,
                cts.Token);
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "Failed to update resolved Telegram approval prompt for call {CallId}", callId);
        }
    }

    private async Task SafeAnswerCallbackAsync(string queryId, string text, bool showAlert = false)
    {
        try
        {
            using var cts = new CancellationTokenSource(OperationTimeout);
            await _dependencies.Transport.AnswerCallbackAsync(queryId, text, showAlert, cts.Token);
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "Failed to answer Telegram callback query");
        }
    }

    private async Task SendApprovalDenyOnFailureAsync(ToolCallId callId)
    {
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
            _log.Error(ex, "Failed to send Telegram auto-deny feedback for call {CallId}", callId);
        }
    }

    private void ApplyPendingApprovalPromptTracked(PendingApprovalPromptTracked tracked)
        => PendingApprovalRecovery.ApplyTracked<PendingApprovalRequest<int>, int>(
            _pendingApprovalRequests,
            tracked,
            wrapPromptId: value => int.Parse(value, CultureInfo.InvariantCulture),
            createRequest: (callId, requesterSenderId, requesterPrincipal, optionKeys, promptId, toolName, displayText)
                => new PendingApprovalRequest<int>(
                    callId,
                    requesterSenderId,
                    requesterPrincipal,
                    optionKeys,
                    promptId,
                    toolName,
                    displayText));

    private void ApplyPendingApprovalPromptCleared(PendingApprovalPromptCleared cleared)
        => PendingApprovalRecovery.ApplyCleared<PendingApprovalRequest<int>, int>(_pendingApprovalRequests, cleared);

    private async Task<bool> SendFileOutputAsync(FileOutput output)
    {
        if (!File.Exists(output.FilePath))
        {
            _log.Warning("File not found for Telegram upload: {Path}", output.FilePath);
            return false;
        }

        try
        {
            using var cts = new CancellationTokenSource(OperationTimeout);
            await _dependencies.Transport.SendDocumentAsync(
                _chatId.Value,
                output.FilePath,
                output.FileName,
                messageThreadId: _messageThreadId,
                cancellationToken: cts.Token);
            _log.Info("Uploaded file to Telegram chat: {FileName}", output.FileName);
            return true;
        }
        catch (OperationCanceledException ex)
        {
            _log.Error(ex, "Timed out uploading file {FileName} to Telegram chat", output.FileName);
            return false;
        }
        catch (Exception ex)
        {
            _log.Error(ex, "Failed to upload file {FileName} to Telegram chat", output.FileName);
            return false;
        }
    }

    private async Task RenderProcessingStateAsync(ProcessingStateOutput output)
    {
        _processingIndicatorActive = true;

        if (_dependencies.ChannelRegistry is not { } registry)
            return;

        var request = new ChannelOutputRenderRequest(
            BuildOutputRenderTarget(),
            output,
            ChannelOutputEffectKind.ProcessingIndicator,
            output.IsRequired ? ChannelOutputRequirement.Required : ChannelOutputRequirement.Optional);

        try
        {
            await registry.RenderOutputAsync(request);
        }
        catch (Exception ex) when (!output.IsRequired)
        {
            _log.Warning(ex, "Failed rendering optional Telegram processing indicator");
        }

        Timers.StartPeriodicTimer(TypingTimerKey, RefreshTyping.Instance, TypingRefreshInterval);
    }

    private async Task HandleRefreshTypingAsync(RefreshTyping _)
    {
        if (!_processingIndicatorActive)
        {
            Timers.Cancel(TypingTimerKey);
            return;
        }

        try
        {
            if (_dependencies.ChannelRegistry is { } registry)
            {
                var request = new ChannelOutputRenderRequest(
                    BuildOutputRenderTarget(),
                    new ProcessingStateOutput(true)
                    {
                        SessionId = _sessionId
                    },
                    ChannelOutputEffectKind.ProcessingIndicator,
                    ChannelOutputRequirement.Optional);
                await registry.RenderOutputAsync(request);
            }
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "Telegram typing refresh failed");
        }
    }

    private ChannelDeliveryTarget BuildOutputRenderTarget()
    {
        var channelKey = ChannelDescriptorKey.FromChannelType(ChannelType.Telegram);
        return new ChannelDeliveryTarget(
            channelKey,
            new ResolvedChannelAddress(
                channelKey,
                ChannelAddressKind.Destination,
                _chatId.Value.ToString(),
                _chatId.Value.ToString()),
            null);
    }

    private ChannelDeliveryTargetInfo BuildDefaultDeliveryTarget() => new(
        "telegram",
        _chatId.Value > 0 ? "direct_message" : "destination",
        _chatId.Value.ToString(),
        _chatId.Value.ToString());

    private async Task NotifyDeliveryFailedAsync(DeliveryFailureKind failureKind, string errorMessage)
    {
        try
        {
            await _dependencies.Pipeline.SendFeedbackAsync(new DeliveryFailed
            {
                SessionId = _sessionId,
                TurnNumber = _outputEngine.LastCompletedTurnNumber,
                ChannelType = ChannelType.Telegram,
                FailureKind = failureKind,
                ErrorMessage = errorMessage
            });
        }
        catch (Exception ex)
        {
            // A dead feedback pipe means the session never learns the turn
            // failed. Rethrow so supervision tears this actor down; the next
            // inbound re-creates it with a fresh pipeline instead of leaving
            // a zombie.
            _log.Error(ex, "Failed to send delivery feedback to session; propagating to trigger pipeline reinit");
            throw;
        }
    }

    protected override void PostStop()
    {
        Timers.Cancel(TypingTimerKey);
        _handle.Dispose();
        base.PostStop();
    }

    private sealed record OutputReceived(SessionOutput Output);

    private sealed record OutputTerminated(int Generation, Exception? Cause);

    private sealed record RefreshTyping
    {
        public static RefreshTyping Instance { get; } = new();
    }
}
