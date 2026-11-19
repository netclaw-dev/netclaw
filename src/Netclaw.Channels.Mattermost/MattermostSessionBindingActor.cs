// -----------------------------------------------------------------------
// <copyright file="MattermostSessionBindingActor.cs" company="Petabridge, LLC">
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
using PendingApprovalRequest = Netclaw.Channels.PendingApprovalRequest<Netclaw.Channels.Mattermost.MattermostPostId>;
using static Netclaw.Actors.Sessions.SessionProtocol;
using static Netclaw.Actors.Reminders.ReminderProtocol;

namespace Netclaw.Channels.Mattermost;

internal sealed class MattermostSessionBindingActor : ChannelSessionBindingActor<MattermostPostId>
{
    private readonly MattermostChannelId _channelId;
    private readonly MattermostRootPostId _rootPostId;


    private const int MaxMattermostPostLength = 16_000;

    private readonly MattermostGatewayDependencies _dependencies;

    // A Mattermost client clears a typing pulse after about five seconds (the
    // server default for TimeBetweenUserTypingUpdatesMilliseconds). The repeat
    // interval stays below that window so a long turn stays visible. See
    // issue #2347.
    private static readonly TimeSpan TypingPulseInterval = TimeSpan.FromSeconds(3);
    // The actor awaits each pulse on its message path. The limit stays below
    // the repeat interval so a stalled transport cannot delay session output
    // for longer than one interval.
    private static readonly TimeSpan TypingPulseTimeout = TimeSpan.FromSeconds(2);
    // Upper bound for one processing phase. The idle signal is lost when the
    // session actor fails during a turn: the new incarnation emits no
    // ProcessingStateOutput(false), and the output stream stays open. Without
    // this bound the binding sends pulses until the daemon stops, and each
    // timer message also resets the idle passivation timeout.
    private static readonly TimeSpan TypingPulseMaxDuration = TimeSpan.FromMinutes(10);
    private static readonly object TypingPulseTimerKey = new();
    private bool _processingIndicatorActive;
    private DateTimeOffset _typingPulseDeadline;

    public MattermostSessionBindingActor(
        SessionId sessionId,
        MattermostChannelId channelId,
        MattermostRootPostId rootPostId,
        MattermostGatewayDependencies dependencies) : base(
            sessionId,
            dependencies.Pipeline,
            dependencies.PromptInjectionDetector ?? throw new InvalidOperationException(
                "MattermostGatewayDependencies.PromptInjectionDetector is not wired; "
                + "prompt-injection scanning cannot be silently disabled."),
            dependencies.IngressGate,
            dependencies.TimeProvider,
            dependencies.ThreadHistoryFetcher,
            ChannelType.Mattermost,
            channelId.Value,
            StringComparer.Ordinal,
            Context.GetLogger()
                .WithContext("Adapter", "mattermost")
                .WithContext(NetclawLogProperties.SessionId, sessionId.Value)
                .WithContext("MattermostChannelId", channelId.Value)
                .WithContext("MattermostRootPostId", rootPostId.Value))
    {
        _channelId = channelId;
        _rootPostId = rootPostId;
        _dependencies = dependencies;
    }

    public override string PersistenceId => $"mattermost-session-cursor-{Uri.EscapeDataString(_sessionId.Value)}";

    public static Props CreateProps(
        SessionId sessionId,
        MattermostChannelId channelId,
        MattermostRootPostId rootPostId,
        MattermostGatewayDependencies dependencies)
        => Props.Create(() => new MattermostSessionBindingActor(
            sessionId,
            channelId,
            rootPostId,
            dependencies));

    protected override void RegisterProviderCommands()
    {
        CommandAsync<MattermostThreadInbound>(HandleInboundAsync);
        CommandAsync<MattermostApprovalResponse>(HandleApprovalResponseAsync);
        CommandAsync<SendTypingPulse>(async _ =>
        {
            if (!_processingIndicatorActive)
                return;

            if (_dependencies.TimeProvider.GetUtcNow() >= _typingPulseDeadline)
            {
                _log.Info(
                    "Session reported processing for {0}; stopping Mattermost typing pulses",
                    TypingPulseMaxDuration);
                StopTypingPulses();
                return;
            }

            ScheduleNextTypingPulse();
            await RenderTypingPulseAsync(new ProcessingStateOutput(true)
            {
                IsRequired = false,
                SessionId = _sessionId
            });
        });
    }

    protected override void BeforeDrain() => StopTypingPulses();
    protected override void BeforePipelineReinitialize() => StopTypingPulses();
    protected override TimeSpan InitializationTimeout => TimeSpan.FromSeconds(15);
    protected override void PersistCursor(CursorAdvanced value, Action<CursorAdvanced> apply) => Persist(value, apply);

    protected override MattermostPostId ParsePromptId(string value) => new(value);
    protected override string FormatPromptId(MattermostPostId value) => value.Value;
    protected override string? NormalizeCursor(string? value) => string.IsNullOrEmpty(value) ? null : value;

    private async Task HandleInboundAsync(MattermostThreadInbound message)
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

    // Mattermost authorization basis for adopted-context: an empty AllowedUserIds
    // list means the instance is unrestricted; otherwise the sender must be listed.
    protected override bool IsAuthorizedSender(string senderId)
        => _dependencies.Options.AllowedUserIds.Length == 0
            || _dependencies.Options.AllowedUserIds.Contains(senderId, StringComparer.Ordinal);

    // The Mattermost interactive-message webhook asks the binding over HTTP and
    // waits for the session's verdict, so this is the one channel that registers
    // the synchronous-reply hook.
    private Task HandleApprovalResponseAsync(MattermostApprovalResponse message)
    {
        var replyTo = Sender;
        return _approvalFlow.HandleApprovalResponseAsync(
            message.CallId,
            message.SelectedKey,
            message.SenderId.Value,
            message.PromptPostId,
            respondSynchronously: response => ReplyIfExpected(replyTo, response));
    }

    protected override async Task RedrawClosedApprovalPromptAsync(PendingApprovalRequest pending, string closeText)
    {
        var postId = pending.PromptId ?? throw new InvalidOperationException("A closed prompt redraw needs its post.");
        try
        {
            var closed = MattermostApprovalPromptBuilder.BuildClosedAttachment(closeText);
            using var cts = new CancellationTokenSource(OperationTimeout, _dependencies.TimeProvider);
            await _dependencies.ReplyClient.UpdatePostAsync(postId, closed.Text ?? string.Empty, [closed], cts.Token);
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "Failed to redraw closed approval prompt for call {CallId} postId={PostId}",
                pending.CallId, postId.Value);
        }
    }

    protected override async Task TryResolveApprovalPromptAsync(
        MattermostPostId? promptPostId,
        ToolInteractionRequest? request,
        Netclaw.Tools.ToolCallId callId,
        string selectedKey,
        string senderId,
        string? persistedToolName = null,
        string? persistedDisplayText = null)
    {
        if (promptPostId is not { } postId)
            return;

        try
        {
            // Hot path uses the in-memory request. Cold-spawn path uses the persisted
            // tool name + display text when present (PendingApprovalPromptTracked
            // carried them); legacy journals without the fields fall back to the
            // generic banner.
            var resolvedAttachment = request is not null
                ? MattermostApprovalPromptBuilder.BuildResolvedAttachment(request, selectedKey, senderId)
                : MattermostApprovalPromptBuilder.BuildResolvedAttachmentWithoutRequest(
                    selectedKey,
                    senderId,
                    toolName: persistedToolName,
                    displayText: persistedDisplayText);

            using var cts = new CancellationTokenSource(OperationTimeout, _dependencies.TimeProvider);
            await _dependencies.ReplyClient.UpdatePostAsync(
                postId,
                resolvedAttachment.Text ?? string.Empty,
                [resolvedAttachment],
                cts.Token);
        }
        catch (Exception ex)
        {
            _log.Warning(
                ex,
                "Failed to update resolved approval prompt for call {CallId} postId={PostId}",
                callId,
                postId.Value);
        }
    }

    protected override ChannelDeliveryTargetInfo BuildDefaultDeliveryTarget()
        => new(
            ChannelType.Mattermost.ToWireValue(),
            "destination",
            _channelId.Value,
            _channelId.Value,
            _rootPostId.Value);

    /// <summary>
    /// Handles the outputs the shared engine leaves to the channel. Mattermost
    /// cannot rename threads, so a <c>SessionTitleOutput</c> has no effect. A
    /// <c>ProcessingStateOutput</c> drives a native typing pulse for the
    /// channel and its thread root.
    /// </summary>
    protected override async Task HandleChannelSpecificOutputAsync(SessionOutput output)
    {
        switch (output)
        {
            case ProcessingStateOutput processing:
                await RenderProcessingStateAsync(processing);
                break;
        }
    }

    private async Task RenderProcessingStateAsync(ProcessingStateOutput output)
    {
        if (!output.IsProcessing)
        {
            StopTypingPulses();
            return;
        }

        // The session emits ProcessingStateOutput(true) once for each
        // processing phase, and a Mattermost typing pulse is transient, so the
        // binding repeats it. Schedule the repeat before the first pulse: a
        // required first pulse that throws must not leave the flag set with
        // no timer.
        _processingIndicatorActive = true;
        _typingPulseDeadline = _dependencies.TimeProvider.GetUtcNow() + TypingPulseMaxDuration;
        ScheduleNextTypingPulse();
        await RenderTypingPulseAsync(output);
    }

    /// <summary>
    /// Schedules one repeat pulse. Each handled pulse schedules the next one,
    /// so at most one timer message waits in the mailbox while the actor
    /// awaits a slow post or upload. A periodic timer would queue one message
    /// for each interval, and the actor would send them all after the reply.
    /// </summary>
    private void ScheduleNextTypingPulse()
        => Timers.StartSingleTimer(
            TypingPulseTimerKey,
            SendTypingPulse.Instance,
            TypingPulseInterval);

    private void StopTypingPulses()
    {
        // The flag also stops a timer message that is already in the mailbox.
        _processingIndicatorActive = false;
        Timers.Cancel(TypingPulseTimerKey);
    }

    private async Task RenderTypingPulseAsync(ProcessingStateOutput output)
    {
        var requirement = output.IsRequired
            ? ChannelOutputRequirement.Required
            : ChannelOutputRequirement.Optional;
        var request = new ChannelOutputRenderRequest(
            BuildTypingRenderTarget(),
            output,
            ChannelOutputEffectKind.ProcessingIndicator,
            requirement);

        try
        {
            using var cts = new CancellationTokenSource(TypingPulseTimeout, _dependencies.TimeProvider);
            await _dependencies.ChannelRegistry.RenderOutputAsync(request, cts.Token);
        }
        catch (Exception ex) when (!output.IsRequired)
        {
            _log.Warning(ex, "Failed rendering optional Mattermost processing indicator");
        }
    }

    private ChannelDeliveryTarget BuildTypingRenderTarget()
    {
        var channelKey = ChannelDescriptorKey.FromChannelType(ChannelType.Mattermost);
        return new ChannelDeliveryTarget(
            channelKey,
            new ResolvedChannelAddress(
                channelKey,
                ChannelAddressKind.Destination,
                _channelId.Value,
                _channelId.Value),
            _rootPostId.Value);
    }

    protected override async Task<MattermostPostId?> PostApprovalPromptAsync(ToolInteractionRequest request)
    {
        var callbackUrl = _dependencies.CallbackUrl;

        if (!string.IsNullOrEmpty(callbackUrl))
        {
            return await TryPostButtonPromptAsync(request, callbackUrl);
        }

        // A child prompt has buttons only (design D3, S7). A binding with no callback URL cannot show buttons, so the
        // shared output engine refuses the call as prompt_unavailable, and the session withdraws the request.
        if (request.Lifetime == ToolInteractionLifetime.UntilClosed)
        {
            _log.Warning("Mattermost has no callback URL for buttons; refusing child consent request {CallId}", request.CallId);
            return null;
        }

        return await TryPostTextPromptAsync(request);
    }

    private async Task<MattermostPostId?> TryPostButtonPromptAsync(
        ToolInteractionRequest request,
        string callbackUrl)
    {
        var promptCorrelationId = Guid.NewGuid().ToString("N");
        var (promptText, attachments) = MattermostApprovalPromptBuilder.BuildButtonPrompt(
            request,
            callbackUrl,
            _channelId.Value,
            _rootPostId.Value,
            promptCorrelationId,
            _dependencies.CallbackActionStore);
        var startedAt = _dependencies.TimeProvider.GetTimestamp();
        try
        {
            var postMessage = BuildPostMessage(promptText, attachments: attachments);
            var result = await _dependencies.ReplyClient.PostReplyAsync(postMessage);
            var duration = _dependencies.TimeProvider.GetElapsedTime(startedAt).TotalMilliseconds;
            ChannelTelemetry.For(ChannelType.Mattermost).RecordReplyPosted(duration);
            ChannelTelemetry.For(ChannelType.Mattermost).RecordExtra("approvalFallbackActivated", "button_prompt");
            // Bind the actual prompt post ID to the action tokens we just minted so
            // the callback endpoint can verify payload.PostId on the way back in.
            // Closes the forgery vector tracked under #939's review. The action
            // store is null in text-only mode (no callback URL); this branch isn't
            // reachable in that case.
            if (result.PostId is { } postId && _dependencies.CallbackActionStore is { } store)
            {
                store.AssociatePromptPostId(promptCorrelationId, postId.Value);
            }
            return result.PostId;
        }
        catch (Exception ex) when (request.Lifetime == ToolInteractionLifetime.UntilClosed)
        {
            // A child prompt has buttons only (design D3, S7), so it gets no text-only fallback.
            _log.Error(ex, "Failed posting Mattermost button prompt for a child consent request; refusing the call");
            ChannelTelemetry.For(ChannelType.Mattermost).RecordExtra("approvalFallbackActivated", "auto_deny");
            return null;
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "Failed posting Mattermost button prompt; falling back to text-only");
            ChannelTelemetry.For(ChannelType.Mattermost).RecordExtra("approvalFallbackActivated", "text_prompt");
            return await TryPostTextPromptAsync(request);
        }
    }

    private async Task<MattermostPostId?> TryPostTextPromptAsync(ToolInteractionRequest request)
    {
        var promptText = MattermostApprovalPromptBuilder.BuildTextPrompt(request);
        var startedAt = _dependencies.TimeProvider.GetTimestamp();
        try
        {
            var postMessage = BuildPostMessage(promptText);
            var result = await _dependencies.ReplyClient.PostReplyAsync(postMessage);
            var duration = _dependencies.TimeProvider.GetElapsedTime(startedAt).TotalMilliseconds;
            ChannelTelemetry.For(ChannelType.Mattermost).RecordReplyPosted(duration);
            return result.PostId;
        }
        catch (Exception ex)
        {
            // The shared output engine refuses the call as prompt_unavailable when this
            // returns null, so the blocked tool call still unwinds.
            _log.Error(ex, "Failed posting Mattermost approval prompt; refusing the call");
            ChannelTelemetry.For(ChannelType.Mattermost).RecordExtra("approvalFallbackActivated", "auto_deny");
            return null;
        }
    }

    protected override Task<TransportResult> PostTextAsync(string text)
        => _safeCall.PostChunkedAsync(
            text,
            MaxMattermostPostLength,
            chunk => _dependencies.ReplyClient.PostReplyAsync(BuildPostMessage(chunk)),
            ex => _log.Warning(ex, "Failed posting Mattermost reply for session {0}", _sessionId.Value));

    private MattermostPostMessage BuildPostMessage(
        string text,
        IReadOnlyList<string>? fileIds = null,
        IReadOnlyList<MattermostAttachment>? attachments = null)
        => new(
            ChannelId: _channelId,
            Text: text,
            RootPostId: _rootPostId.IsEmpty ? null : new MattermostPostId(_rootPostId.Value),
            FileIds: fileIds,
            Attachments: attachments);

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
                using var uploadCts = new CancellationTokenSource(OperationTimeout, _dependencies.TimeProvider);
                var fileId = await _dependencies.ReplyClient.UploadFileAsync(
                    _channelId,
                    file.FilePath,
                    file.FileName,
                    uploadCts.Token);

                using var postCts = new CancellationTokenSource(OperationTimeout, _dependencies.TimeProvider);
                var postMessage = BuildPostMessage($":paperclip: {file.FileName}", fileIds: [fileId]);
                await _dependencies.ReplyClient.PostReplyAsync(postMessage, postCts.Token);
            },
            ex =>
            {
                if (ex is OperationCanceledException)
                    _log.Error(ex, "Timed out uploading file {FileName} to Mattermost thread", file.FileName);
                else
                    _log.Error(ex, "Failed to upload file {FileName} to Mattermost thread", file.FileName);
            });

        if (uploaded is TransportResult.Success)
            _log.Info("Uploaded file to Mattermost thread: {FileName}", file.FileName);

        return uploaded;
    }

    private void ReplyIfExpected(IActorRef replyTo, object response)
    {
        if (replyTo.IsNobody() || Equals(replyTo, ActorRefs.Nobody) || Equals(replyTo, Context.System.DeadLetters))
            return;

        replyTo.Tell(response);
    }


    private async Task ProcessInboundAttachmentsAsync(
        IReadOnlyList<MattermostFileReference> files,
        TrustAudience audience,
        List<AIContent> contents,
        CancellationToken cancellationToken)
    {
        if (_dependencies.HttpClient is null)
        {
            _log.Warning(
                "Mattermost HTTP client is not configured; rejecting {Count} inbound attachment(s)",
                files.Count);
            await PostTextAsync(":warning: I can't download attachments right now -- HTTP client is not configured.");
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
                "Please split your upload and try again. Text content was delivered.");
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
        MattermostFileReference file,
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
            // Mattermost attachment URLs must originate from the configured server.
            preDownloadGate: () =>
            {
                if (string.IsNullOrEmpty(_dependencies.ServerUrl))
                {
                    _log.Warning(
                        "attachment_rejected name={Name} reason=no-server-url-configured",
                        file.Name);
                    return $"`{file.Name}` was rejected because no Mattermost server URL is configured for URL trust validation.";
                }

                if (!MattermostAttachmentUrlTrust.IsAllowedAttachmentUrl(file.Url, _dependencies.ServerUrl))
                {
                    _log.Warning(
                        "attachment_rejected name={Name} url={Url} reason=untrusted-url",
                        file.Name, file.Url);
                    return $"`{file.Name}` has an untrusted URL and was skipped.";
                }

                return null;
            });

    internal static List<string> ChunkMessage(string text) =>
        MessageChunker.Chunk(text, MaxMattermostPostLength);

    private sealed record SendTypingPulse
    {
        public static readonly SendTypingPulse Instance = new();
    }
}
