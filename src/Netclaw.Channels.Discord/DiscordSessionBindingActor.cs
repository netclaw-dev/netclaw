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
using Netclaw.Actors.Sessions;
using Netclaw.Channels;
using Netclaw.Channels.Telemetry;
using Netclaw.Configuration;
using Netclaw.Media;
using Netclaw.Security;
using Netclaw.Tools;
using IOPath = System.IO.Path;
using PendingApprovalRequest = Netclaw.Channels.PendingApprovalRequest<Netclaw.Channels.Discord.DiscordMessageId>;
using static Netclaw.Actors.Sessions.SessionProtocol;
using static Netclaw.Actors.Reminders.ReminderProtocol;

namespace Netclaw.Channels.Discord;

internal sealed class DiscordSessionBindingActor : ChannelSessionBindingActor<DiscordMessageId>
{
    private readonly DiscordChannelId _channelId;
    private DiscordReplyChannelId _replyChannelId;
    private readonly DiscordThreadOrMessageId _threadOrMessageId;
    private DiscordMessageId? _rootMessageId;
    private bool _threadCreated;
    private const int MaxDiscordMessageLength = 2000;

    private readonly DiscordGatewayDependencies _dependencies;
    private string? _lastSetThreadName;
    // Snowflake cursors in canonical decimal string form, which is also the
    // persisted CursorAdvanced form. NormalizeSnowflake produces every value,
    // so CursorComparer can order them.
    private static readonly SnowflakeCursorComparer CursorComparer = SnowflakeCursorComparer.Instance;

    public DiscordSessionBindingActor(
        SessionId sessionId,
        DiscordChannelId channelId,
        DiscordReplyChannelId replyChannelId,
        DiscordThreadOrMessageId threadOrMessageId,
        DiscordMessageId? rootMessageId,
        DiscordGatewayDependencies dependencies) : base(
            sessionId,
            dependencies.Pipeline,
            dependencies.PromptInjectionDetector ?? throw new InvalidOperationException(
                "DiscordGatewayDependencies.PromptInjectionDetector is not wired; "
                + "prompt-injection scanning cannot be silently disabled."),
            dependencies.IngressGate,
            dependencies.TimeProvider,
            dependencies.ThreadHistoryFetcher,
            ChannelType.Discord,
            channelId.Value,
            CursorComparer,
            Context.GetLogger()
                .WithContext("Adapter", "discord")
                .WithContext(NetclawLogProperties.SessionId, sessionId.Value)
                .WithContext("DiscordChannelId", channelId.Value)
                .WithContext("DiscordThreadOrMessageId", threadOrMessageId.Value))
    {
        _channelId = channelId;
        _replyChannelId = replyChannelId;
        _threadOrMessageId = threadOrMessageId;
        _rootMessageId = rootMessageId;
        _dependencies = dependencies;
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

    protected override void RegisterProviderCommands()
    {
        CommandAsync<DiscordThreadInbound>(HandleInboundAsync);
        CommandAsync<DiscordApprovalResponse>(HandleApprovalResponseAsync);
        CommandAsync<StartProactiveThread>(HandleProactiveThreadAsync);
    }

    protected override TimeSpan InitializationTimeout => TimeSpan.FromSeconds(15);
    protected override void PersistCursor(CursorAdvanced value, Action<CursorAdvanced> apply) => Persist(value, apply);

    protected override DiscordMessageId ParsePromptId(string value) => new(value);
    protected override string FormatPromptId(DiscordMessageId value) => value.Value;
    protected override string? NormalizeCursor(string? value) => NormalizeSnowflake(value);
    protected override bool TryReadStoredCursor(string raw, out string? cursor)
    {
        cursor = NormalizeSnowflake(raw);
        return cursor is not null;
    }

    private async Task HandleProactiveThreadAsync(StartProactiveThread message)
    {
        var replyTo = Sender;
        _replyChannelId = message.ReplyChannelId;
        _threadCreated = message.DirectMessageUserId is not null || message.RootMessageId is null;
        _rootMessageId = message.RootMessageId;

        _log.Info("Initializing proactive thread pipeline for session {0}", message.SessionId.Value);
        await EnsureInitializedAsync();
        replyTo.Tell(new ProactiveThreadAck(message.SessionId));
    }

    private async Task HandleInboundAsync(DiscordThreadInbound message)
    {
        if (!await AcceptIngressAsync())
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(message.Text) && message.Attachments is not { Count: > 0 })
        {
            return;
        }

        if (!string.IsNullOrWhiteSpace(message.Text)
            && await _approvalFlow.TryHandleTextApprovalResponseAsync(message.Text, message.SenderId.Value))
        {
            return;
        }

        using var inbound = new CancellationTokenSource(TimeSpan.FromSeconds(30), _dependencies.TimeProvider);
        if (!await ScanLiveTextAsync(message.Text, inbound.Token))
        {
            return;
        }

        if (!InputQueueReady())
        {
            return;
        }

        var contents = new List<AIContent>();
        if (!string.IsNullOrWhiteSpace(message.Text))
        {
            contents.Add(new TextContent(message.Text));
        }

        if (message.Attachments is { Count: > 0 } attachments)
        {
            await ProcessInboundAttachmentsAsync(attachments, message.Audience, contents, inbound.Token);
        }

        if (contents.Count == 0)
        {
            return;
        }

        var input = new ChannelInput
        {
            SenderId = new SenderId(message.SenderId.Value),
            ChannelId = message.ChannelId.Value,
            MessageId = message.EventId.Value,
            Audience = message.Audience,
            Boundary = TrustBoundary.TrustedInstance,
            Principal = message.Principal,
            Provenance = message.Provenance,
            Contents = contents,
            ReceivedAt = message.ReceivedAt,
            ExecutableText = message.Text,
            DefaultDeliveryTarget = BuildDefaultDeliveryTarget()
        };
        input = await PrepareLiveInputAsync(input,
            _dependencies.Options.MentionRequiredInThreadFor(_channelId.Value), inbound.Token);

        try
        {
            using var write = new CancellationTokenSource(OperationTimeout, _dependencies.TimeProvider);
            await WriteLiveInputAsync(input, write.Token);
        }
        catch (OperationCanceledException)
        {
            _log.Warning("Timed out enqueueing message for session {0}", _sessionId.Value);
            RequestPipelineReinitialization("input queue write timeout");
        }
        catch (ChannelClosedException)
        {
            _log.Warning("Input queue closed for session {0}", _sessionId.Value);
            RequestPipelineReinitialization("input queue closed");
        }
    }

    // Discord authorization basis for adopted-context: an empty AllowedUserIds
    // list means the instance is unrestricted; otherwise the sender must be listed.
    protected override bool IsAuthorizedSender(string senderId)
        => _dependencies.Options.AllowedUserIds.Length == 0
            || _dependencies.Options.AllowedUserIds.Contains(senderId, StringComparer.Ordinal);

    // Discord gateway interactions are one-way telemetry from the websocket, so
    // the flow gets no synchronous-reply hook here.
    private Task HandleApprovalResponseAsync(DiscordApprovalResponse message)
        => _approvalFlow.HandleApprovalResponseAsync(
            message.CallId,
            message.SelectedKey,
            message.SenderId.Value,
            message.PromptMessageId);

    protected override async Task RedrawClosedApprovalPromptAsync(PendingApprovalRequest pending, string closeText)
    {
        var messageId = pending.PromptId ?? throw new InvalidOperationException("A closed prompt redraw needs its message.");
        try
        {
            using var cts = new CancellationTokenSource(OperationTimeout, _dependencies.TimeProvider);
            await _dependencies.ReplyClient.UpdateMessageAsync(
                _replyChannelId,
                messageId,
                closeText,
                removeComponents: true,
                cts.Token);
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "Failed to redraw closed approval prompt for call {CallId} messageId={MessageId}",
                pending.CallId, messageId.Value);
        }
    }

    protected override async Task TryResolveApprovalPromptAsync(
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

            using var cts = new CancellationTokenSource(OperationTimeout, _dependencies.TimeProvider);
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

    protected override ChannelDeliveryTargetInfo BuildDefaultDeliveryTarget()
        => new(
            ChannelType.Discord.ToWireValue(),
            "destination",
            _channelId.Value,
            _channelId.Value,
            _threadOrMessageId.Value);

    /// <summary>
    /// Handles the outputs the shared engine leaves to the channel. Discord
    /// renames its thread from a session title and renders a processing
    /// indicator; other output types have no Discord effect.
    /// </summary>
    protected override async Task HandleChannelSpecificOutputAsync(SessionOutput output)
    {
        switch (output)
        {
            case ProcessingStateOutput processing:
                await RenderProcessingStateAsync(processing);
                break;

            case SessionTitleOutput titleOutput:
                if (_threadCreated && titleOutput.Title != _lastSetThreadName)
                    await SafeSetThreadNameAsync(titleOutput.Title);
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

    protected override async Task<DiscordMessageId?> PostApprovalPromptAsync(ToolInteractionRequest request)
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
        catch (Exception ex) when (request.Lifetime == ToolInteractionLifetime.UntilClosed)
        {
            // A child prompt has buttons only (design D3, S7). The shared output engine refuses the call as
            // prompt_unavailable when this returns null, and the session withdraws the request.
            _log.Error(ex, "Failed posting Discord button prompt for a child consent request; refusing the call");
            return null;
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
                // The shared output engine refuses the call as prompt_unavailable when this
                // returns null, so the blocked tool call still unwinds.
                _log.Error(textEx, "Failed posting text-only approval fallback; refusing the call");
                return null;
            }
        }
    }

    protected override Task<TransportResult> PostTextAsync(string text)
        => _safeCall.PostChunkedAsync(
            text,
            MaxDiscordMessageLength,
            async chunk =>
            {
                var result = await _dependencies.ReplyClient.PostReplyAsync(BuildPostMessage(chunk));
                ApplyThreadPromotion(result);
            },
            ex => _log.Warning(ex, "Failed posting Discord reply for session {0}", _sessionId.Value));

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

    protected override async Task<TransportResult> UploadFileAsync(FileOutput file)
    {
        // A missing file never reaches the transport, so it carries no transport
        // telemetry and a different failure kind.
        if (!File.Exists(file.FilePath))
        {
            _log.Warning("File not found for upload: {Path}", file.FilePath);
            return new TransportResult.Failure(DeliveryFailureKind.Unknown, $"File not found for upload: {file.FilePath}");
        }

        var uploaded = await _safeCall.InvokeAsync(
            async () =>
            {
                using var cts = new CancellationTokenSource(OperationTimeout, _dependencies.TimeProvider);
                await _dependencies.ReplyClient.UploadFileAsync(
                    new DiscordFileUpload(
                        _replyChannelId,
                        file.FilePath,
                        file.FileName,
                        $":paperclip: {file.FileName}",
                        _threadCreated ? null : _rootMessageId),
                    cts.Token);
            },
            ex =>
            {
                if (ex is OperationCanceledException)
                    _log.Error(ex, "Timed out uploading file {FileName} to Discord session", file.FileName);
                else
                    _log.Error(ex, "Failed to upload file {FileName} to Discord session", file.FileName);
            });

        if (uploaded is TransportResult.Success)
            _log.Info("Uploaded file to Discord session: {FileName}", file.FileName);

        return uploaded;
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
            await PostTextAsync(":warning: I can't download attachments right now — HTTP client is not configured.");
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
            await PostTextAsync(
                $":warning: I can only accept up to {policy.MaxFilesPerMessage} attachments per message. " +
                $"Please split your upload and try again. Text content was delivered.");
            return;
        }

        var modelCapabilities = _dependencies.ModelCapabilities;
        var inlineImages = modelCapabilities.InputModalities.HasFlag(ModelModality.Image);

        var acceptedLines = new List<string>(files.Count);
        var dataContents = new List<DataContent>();
        var rejections = new List<string>();

        var storage = _dependencies.StorageResolver.Resolve(_sessionId);
        var inboxDir = SessionDirectoryHelper.GetOrCreateInboxDirectory(storage);
        var stagingDir = SessionDirectoryHelper.GetOrCreateAttachmentStagingDirectory(storage);

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
            await PostTextAsync(joined);
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
    /// Returns the canonical decimal form of a Discord snowflake, or null
    /// when the value is not a snowflake and therefore has no order. The
    /// <see cref="ulong"/> round-trip is a validation and normalization
    /// step, not cursor state: it rejects the same inputs the previous
    /// numeric cursor rejected, and it strips a form such as a leading zero
    /// that <see cref="SnowflakeCursorComparer"/> cannot order.
    /// </summary>
    private static string? NormalizeSnowflake(string? value)
        => ulong.TryParse(value, out var id) ? id.ToString() : null;

}
