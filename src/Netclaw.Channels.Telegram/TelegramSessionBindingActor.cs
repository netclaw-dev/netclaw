// -----------------------------------------------------------------------
// <copyright file="TelegramSessionBindingActor.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Threading.Channels;
using Akka.Actor;
using Akka.Event;
using Microsoft.Extensions.AI;
using Netclaw.Actors.Channels;
using Netclaw.Actors.Protocol;
using Netclaw.Channels;
using Netclaw.Configuration;
using Netclaw.Tools;
using static Netclaw.Actors.Sessions.SessionProtocol;

namespace Netclaw.Channels.Telegram;

internal sealed class TelegramSessionBindingActor : ReceiveActor, IWithTimers
{
    private static readonly TimeSpan OperationTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan TypingRefreshInterval = TimeSpan.FromSeconds(4);
    private static readonly object TypingTimerKey = new();

    private readonly SessionId _sessionId;
    private readonly TelegramChatId _chatId;
    private readonly TelegramGatewayDependencies _dependencies;
    private readonly SessionPipelineHandle _handle;
    private readonly ILoggingAdapter _log;
    private readonly Dictionary<string, PendingApproval> _pendingApprovals = new(StringComparer.Ordinal);
    private volatile bool _processingIndicatorActive;

    public ITimerScheduler Timers { get; set; } = null!;

    public TelegramSessionBindingActor(
        SessionId sessionId,
        TelegramChatId chatId,
        TelegramGatewayDependencies dependencies)
    {
        _sessionId = sessionId;
        _chatId = chatId;
        _dependencies = dependencies;
        _log = Context.GetLogger().WithContext("Adapter", "telegram");
        _handle = new SessionPipelineHandle(dependencies.Pipeline, _log, "telegram");

        ReceiveAsync<TelegramSessionInbound>(HandleInboundAsync);
        ReceiveAsync<OutputReceived>(HandleOutputAsync);
        ReceiveAsync<TelegramCallbackQuery>(HandleCallbackAsync);
        ReceiveAsync<StartTelegramProactiveChat>(HandleProactiveChatAsync);
        Receive<OutputTerminated>(message =>
        {
            if (message.Generation == _handle.Generation)
                Context.Stop(Self);
        });
        ReceiveAsync<RefreshTyping>(HandleRefreshTypingAsync);
    }

    public static Props CreateProps(
        SessionId sessionId,
        TelegramChatId chatId,
        TelegramGatewayDependencies dependencies) =>
        Props.Create(() => new TelegramSessionBindingActor(sessionId, chatId, dependencies));

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
    }

    private async Task HandleProactiveChatAsync(StartTelegramProactiveChat message)
    {
        await EnsureInitializedAsync();
        Sender.Tell(new TelegramProactiveChatAck(message.SessionId));
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
                $"I can only accept up to {policy.MaxFilesPerMessage} files in one message.");
            return;
        }

        var inlineImages = _dependencies.ModelCapabilities.InputModalities.HasFlag(ModelModality.Image);
        var inbox = SessionDirectoryHelper.GetOrCreateInboxDirectory(
            _sessionId,
            _dependencies.Paths.SessionsDirectory);
        var staging = SessionDirectoryHelper.GetOrCreateAttachmentStagingDirectory(
            _sessionId,
            _dependencies.Paths.SessionsDirectory);
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
                    await _dependencies.Transport.SendTextAsync(_chatId.Value, rejected.UserFacingReason);
                    break;
            }
        }

        if (acceptedLines.Count > 0)
            contents.Add(new TextContent(string.Join('\n', acceptedLines)));
    }

    private async Task HandleOutputAsync(OutputReceived message)
    {
        switch (message.Output)
        {
            case TextOutput output:
            {
                var text = output.Text;
                if (!string.IsNullOrWhiteSpace(text))
                    await _dependencies.Transport.SendTextAsync(_chatId.Value, text);
                break;
            }

            case ErrorOutput output:
                await _dependencies.Transport.SendTextAsync(
                    _chatId.Value,
                    $"Sorry, NetClaw had a problem: {output.Message}");
                break;

            case FileOutput output:
                await SendFileOutputAsync(output);
                break;

            case ToolInteractionRequest request
                when string.Equals(request.Kind, "approval", StringComparison.OrdinalIgnoreCase):
                await SendApprovalPromptAsync(request);
                break;

            case ProcessingStateOutput { IsProcessing: true } processing:
                await RenderProcessingStateAsync(processing);
                break;

            case ProcessingStateOutput:
                _processingIndicatorActive = false;
                Timers.Cancel(TypingTimerKey);
                break;
        }
    }

    private async Task SendApprovalPromptAsync(ToolInteractionRequest request)
    {
        var pending = new PendingApproval(request);
        var buttons = request.Options.Select((option, index) =>
        {
            var token = $"nc_{Guid.NewGuid():N}_{index}";
            _pendingApprovals[token] = pending;
            pending.Selections[token] = option.Key.Value;
            return new TelegramApprovalButton(option.Label, token);
        }).ToArray();

        try
        {
            using var cts = new CancellationTokenSource(OperationTimeout);
            pending.MessageId = await _dependencies.Transport.SendApprovalPromptAsync(
                _chatId.Value,
                TelegramApprovalPromptBuilder.BuildPrompt(request),
                buttons,
                cts.Token);
            _log.Info("Posted Telegram approval prompt for call {CallId}", request.CallId);
        }
        catch (Exception ex)
        {
            RemovePendingApproval(pending);
            _log.Error(ex, "Failed to post Telegram approval prompt; denying call {CallId}", request.CallId);
            await SendApprovalDenyOnFailureAsync(request.CallId);
        }
    }

    private async Task HandleCallbackAsync(TelegramCallbackQuery callback)
    {
        if (!_pendingApprovals.TryGetValue(callback.Data, out var pending)
            || !pending.Selections.TryGetValue(callback.Data, out var selectedKey))
        {
            await SafeAnswerCallbackAsync(callback.QueryId, "This approval request expired.", showAlert: true);
            return;
        }

        var senderId = callback.UserId.ToString();
        if (!ApprovalButtonValueCodec.CanApprove(
                pending.Request.RequesterPrincipal,
                pending.Request.RequesterSenderId?.Value,
                senderId))
        {
            await SafeAnswerCallbackAsync(callback.QueryId, "Only the requester can approve this action.", showAlert: true);
            return;
        }

        ISessionResponse result;
        try
        {
            using var cts = new CancellationTokenSource(OperationTimeout);
            result = await _dependencies.Pipeline.SendFeedbackAndWaitAsync(new ToolInteractionResponse
            {
                SessionId = _sessionId,
                CallId = pending.Request.CallId,
                SelectedKey = new ApprovalOptionKey(selectedKey),
                SenderId = new SenderId(senderId)
            }, cts.Token);
        }
        catch (Exception ex)
        {
            _log.Error(ex, "Failed to route Telegram approval response for call {CallId}", pending.Request.CallId);
            await SafeAnswerCallbackAsync(callback.QueryId, "Netclaw could not record this decision.", showAlert: true);
            return;
        }

        if (result is not CommandAck)
        {
            var message = result is CommandNack { Reason: ApprovalNackReasons.WrongRequester }
                ? "Only the requester can approve this action."
                : "This approval request is no longer pending.";
            await SafeAnswerCallbackAsync(callback.QueryId, message, showAlert: true);
            return;
        }

        RemovePendingApproval(pending);
        await SafeAnswerCallbackAsync(callback.QueryId, "Decision recorded.");

        try
        {
            using var cts = new CancellationTokenSource(OperationTimeout);
            await _dependencies.Transport.UpdateApprovalPromptAsync(
                _chatId.Value,
                pending.MessageId ?? callback.MessageId,
                TelegramApprovalPromptBuilder.BuildResolvedPrompt(
                    pending.Request,
                    selectedKey,
                    callback.UserId),
                cts.Token);
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "Failed to update resolved Telegram approval prompt for call {CallId}", pending.Request.CallId);
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

    private void RemovePendingApproval(PendingApproval pending)
    {
        foreach (var token in pending.Selections.Keys)
            _pendingApprovals.Remove(token);
    }

    private async Task SendFileOutputAsync(FileOutput output)
    {
        if (!File.Exists(output.FilePath))
        {
            _log.Warning("File not found for Telegram upload: {Path}", output.FilePath);
            return;
        }

        try
        {
            using var cts = new CancellationTokenSource(OperationTimeout);
            await _dependencies.Transport.SendDocumentAsync(
                _chatId.Value,
                output.FilePath,
                output.FileName,
                cts.Token);
            _log.Info("Uploaded file to Telegram chat: {FileName}", output.FileName);
        }
        catch (OperationCanceledException ex)
        {
            _log.Error(ex, "Timed out uploading file {FileName} to Telegram chat", output.FileName);
        }
        catch (Exception ex)
        {
            _log.Error(ex, "Failed to upload file {FileName} to Telegram chat", output.FileName);
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

    private sealed class PendingApproval(ToolInteractionRequest request)
    {
        public ToolInteractionRequest Request { get; } = request;

        public Dictionary<string, string> Selections { get; } = new(StringComparer.Ordinal);

        public int? MessageId { get; set; }
    }
}
