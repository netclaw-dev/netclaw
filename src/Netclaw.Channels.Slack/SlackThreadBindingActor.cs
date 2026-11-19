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
using Netclaw.Actors.Sessions;
using Netclaw.Channels;
using Netclaw.Channels.Telemetry;
using Netclaw.Configuration;
using Netclaw.Media;
using Netclaw.Security;
using Netclaw.Tools;
using SlackNet.Blocks;
using PendingApprovalRequest = Netclaw.Channels.PendingApprovalRequest<Netclaw.Channels.Slack.SlackEventTs>;
using static Netclaw.Actors.Sessions.SessionProtocol;
using static Netclaw.Actors.Reminders.ReminderProtocol;

namespace Netclaw.Channels.Slack;

internal sealed class SlackThreadBindingActor : ChannelSessionBindingActor<SlackEventTs>
{
    private readonly SlackChannelId _channelId;
    private readonly SlackThreadTs _threadTs;
    private readonly SlackGatewayDependencies _dependencies;

    // A Slack ts is decimal seconds, so two ts values order by numeric value,
    // not by ordinal text. SlackEventTs.CompareTo owns that rule; the hydration
    // engine and the output engine compare cursor keys through this wrapper.
    private static readonly IComparer<string> CursorComparer =
        Comparer<string>.Create((x, y) => new SlackEventTs(x).CompareTo(new SlackEventTs(y)));

    private volatile bool _processingIndicatorActive;
    private readonly object _processingIndicatorRenderLock = new();
    private Task _processingIndicatorRenderTail = Task.CompletedTask;

    private static readonly TimeSpan ProcessingIndicatorTimeout = TimeSpan.FromSeconds(1);

    public SlackThreadBindingActor(
        SessionId sessionId,
        SlackChannelId channelId,
        SlackThreadTs threadTs,
        SlackGatewayDependencies dependencies) : base(
            sessionId,
            dependencies.Pipeline,
            dependencies.PromptInjectionDetector ?? throw new InvalidOperationException(
                "SlackGatewayDependencies.PromptInjectionDetector is not wired; "
                + "prompt-injection scanning cannot be silently disabled."),
            dependencies.IngressGate,
            dependencies.TimeProvider,
            dependencies.ThreadHistoryFetcher ?? throw new InvalidOperationException(
                "SlackGatewayDependencies.ThreadHistoryFetcher is not wired; "
                + "Slack thread history hydration cannot be silently disabled."),
            ChannelType.Slack,
            channelId.Value,
            CursorComparer,
            Context.GetLogger()
                .WithContext("Adapter", "slack")
                .WithContext(NetclawLogProperties.SessionId, sessionId.Value)
                .WithContext("SlackChannelId", channelId)
                .WithContext("SlackThreadTs", threadTs))
    {
        _channelId = channelId;
        _threadTs = threadTs;
        _dependencies = dependencies;
    }

    public static Props CreateProps(
        SessionId sessionId,
        SlackChannelId channelId,
        SlackThreadTs threadTs,
        SlackGatewayDependencies dependencies) =>
        Props.Create(() => new SlackThreadBindingActor(sessionId, channelId, threadTs, dependencies));

    public override string PersistenceId => $"slack-thread-cursor-{Uri.EscapeDataString(_sessionId.Value)}";

    protected override void RegisterProviderCommands()
    {
        CommandAsync<SlackThreadInbound>(HandleInboundAsync);
        CommandAsync<SlackApprovalResponse>(HandleApprovalResponseAsync);
        CommandAsync<StartProactiveThread>(HandleProactiveThreadAsync);
    }

    protected override void BeforePipelineReinitialize() => QueueProcessingIndicatorClearIfActive();
    protected override void PostStop()
    {
        QueueProcessingIndicatorClearIfActive();
        base.PostStop();
    }
    protected override TimeSpan InitializationTimeout => OperationTimeout;
    protected override void PersistCursor(CursorAdvanced value, Action<CursorAdvanced> apply) => PersistAsync(value, apply);
    protected override bool IsApprovalRequest(ToolInteractionRequest request) => true;
    protected override string? RenderTextOutput(TextOutput output)
    {
        var text = output.Text?.Trim();
        return string.IsNullOrWhiteSpace(text) ? null : text;
    }
    protected override string RenderErrorOutput(ErrorOutput error) =>
        $":warning: {error.Message} (ref: {error.CorrelationId.ToString("N")[..8]})";
    protected override SlackEventTs ParsePromptId(string value) => new(value);
    protected override string FormatPromptId(SlackEventTs value) => value.Value;
    protected override string? NormalizeCursor(string? value) =>
        new SlackEventId(value ?? string.Empty).TryGetEventTs()?.Value;
    protected override bool IsAuthorizedSender(string senderId) =>
        SlackAclPolicy.IsAllowedUser(new SlackUserId(senderId), _dependencies.Options);
    protected override Task PostPromptUnavailableNoticeAsync(ToolInteractionRequest request) =>
        PostTextAsync($":warning: Netclaw could not show the approval prompt for `{request.ToolName}` in this channel. The call did not run.");

    private async Task HandleProactiveThreadAsync(StartProactiveThread message)
    {
        var replyTo = Sender;
        _log.Info("Initializing proactive thread pipeline for session {0}", message.SessionId.Value);
        await EnsureInitializedAsync();
        replyTo.Tell(new ProactiveThreadAck(message.SessionId));
    }

    private async Task HandleInboundAsync(SlackThreadInbound message)
    {
        var inboundLog = _log.WithContext("TurnId", message.TurnId).WithContext("SlackEventId", message.EventId.Value);
        using var inbound = new CancellationTokenSource(TimeSpan.FromSeconds(30), _dependencies.TimeProvider);
        try
        {
            inboundLog.Info("turn_received textChars={TextLength} fileCount={FileCount}", message.Text?.Length ?? 0, message.Files?.Count ?? 0);
            if (!string.IsNullOrWhiteSpace(message.Text)
                && await _approvalFlow.TryHandleTextApprovalResponseAsync(message.Text, message.SenderId.Value))
            {
                return;
            }

            if (!await ScanLiveTextAsync(message.Text, inbound.Token))
            {
                return;
            }

            var contents = new List<AIContent>();
            if (!string.IsNullOrEmpty(message.Text))
            {
                contents.Add(new TextContent(message.Text));
            }

            if (message.Files is { Count: > 0 } files)
            {
                await ProcessInboundAttachmentsAsync(files, message.Audience, contents, inbound.Token);
            }

            if (contents.Count == 0)
            {
                return;
            }

            if (!await AcceptIngressAsync() || !InputQueueReady())
            {
                return;
            }

            if (IsStaleCursor(NormalizeCursor(message.EventId.Value)))
            {
                _log.Info("Dropping stale Slack inbound event eventId={EventId}", message.EventId.Value);
                ChannelTelemetry.For(ChannelType.Slack).RecordEventDropped("stale_event");
                return;
            }

            var input = new ChannelInput
            {
                SenderId = message.SenderId,
                ChannelId = _channelId.Value,
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
                using var deadline = new CancellationTokenSource(OperationTimeout, _dependencies.TimeProvider);
                using var write = CancellationTokenSource.CreateLinkedTokenSource(inbound.Token, deadline.Token);
                await WriteLiveInputAsync(input, write.Token);
                QueueProcessingIndicatorRefreshIfActive();
                inboundLog.Info("turn_enqueued contentItems={ContentCount}", input.Contents.Count);
            }
            catch (OperationCanceledException ex)
            {
                _log.Warning(ex, "Timed out enqueueing message for session {0}", _sessionId.Value);
                RequestPipelineReinitialization("input queue write timeout");
            }
            catch (ChannelClosedException ex)
            {
                _log.Warning(ex, "Thread input queue closed for session {0}", _sessionId.Value);
                RequestPipelineReinitialization("input queue write failed");
            }
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

        var storage = _dependencies.StorageResolver.Resolve(_sessionId);
        var inboxDir = SessionDirectoryHelper.GetOrCreateInboxDirectory(storage);
        var stagingDir = SessionDirectoryHelper.GetOrCreateAttachmentStagingDirectory(storage);

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
            await PostTextAsync(joined);
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

    protected override ChannelDeliveryTargetInfo BuildDefaultDeliveryTarget()
        => new(
            ChannelType.Slack.ToWireValue(),
            "destination",
            _channelId.Value,
            _channelId.Value,
            _threadTs.Value);

    /// <summary>
    /// Handles the outputs the shared engine leaves to the channel. Slack
    /// renders a processing indicator; other output types have no Slack effect.
    /// BufferFlush and TextDeltaOutput never arrive, because Slack subscribes to
    /// OutputFilter.Text (final assembled text), not TextStreaming.
    /// </summary>
    protected override async Task HandleChannelSpecificOutputAsync(SessionOutput output)
    {
        if (output is ProcessingStateOutput processing)
            await RenderProcessingStateAsync(processing);
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

    private async Task RenderOutputWithTimeoutAsync(
        IChannelRegistry registry,
        ChannelOutputRenderRequest request)
    {
        using var renderCts = new CancellationTokenSource(ProcessingIndicatorTimeout, _dependencies.TimeProvider);
        var renderTask = registry.RenderOutputAsync(request, renderCts.Token).AsTask();
        try
        {
            await renderTask.WaitAsync(ProcessingIndicatorTimeout, _dependencies.TimeProvider);
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

    // Slack block actions reach the binding one way through the gateway, so the
    // flow gets no synchronous-reply hook here.
    private Task HandleApprovalResponseAsync(SlackApprovalResponse message)
        => _approvalFlow.HandleApprovalResponseAsync(
            message.CallId,
            message.SelectedKey,
            message.SenderId.Value,
            message.PromptMessageTs);

    protected override async Task<TransportResult> PostTextAsync(string text)
    {
        var startedAt = _dependencies.TimeProvider.GetTimestamp();
        try
        {
            using var cts = new CancellationTokenSource(OperationTimeout, _dependencies.TimeProvider);
            await _dependencies.ReplyClient.PostThreadReplyAsync(new SlackPostMessage(
                ChannelId: _channelId,
                ThreadTs: _threadTs,
                Text: text), cts.Token);

            _log.Info("Posted Slack reply message");
            ChannelTelemetry.For(ChannelType.Slack).RecordReplyPosted(_dependencies.TimeProvider.GetElapsedTime(startedAt).TotalMilliseconds);
            QueueProcessingIndicatorRefreshIfActive();
            return new TransportResult.Success();
        }
        catch (OperationCanceledException ex)
        {
            _log.Error(ex, "Timed out posting Slack reply for session {0}", _sessionId.Value);
            ChannelTelemetry.For(ChannelType.Slack).RecordReplyFailed(_dependencies.TimeProvider.GetElapsedTime(startedAt).TotalMilliseconds);
            return new TransportResult.Failure(DeliveryFailureKind.TransportFailure, $"Timed out posting reply: {ex.Message}");
        }
        catch (SlackMessageDeliveryException ex)
        {
            _log.Warning("Delivery rejected for session {SessionId} error={ErrorCode} kind={FailureKind}",
                _sessionId.Value, ex.ErrorCode ?? "unknown", ex.FailureKind);
            ChannelTelemetry.For(ChannelType.Slack).RecordReplyRejected(ex.ErrorCode);
            return new TransportResult.Failure(ex.FailureKind, ex.Message);
        }
        catch (Exception ex)
        {
            _log.Error(ex, "Failed posting Slack reply for session {0}", _sessionId.Value);
            ChannelTelemetry.For(ChannelType.Slack).RecordReplyFailed(_dependencies.TimeProvider.GetElapsedTime(startedAt).TotalMilliseconds);
            return new TransportResult.Failure(DeliveryFailureKind.Unknown, ex.Message);
        }
    }

    protected override async Task<SlackEventTs?> PostApprovalPromptAsync(ToolInteractionRequest request)
    {
        try
        {
            var text = SlackApprovalBlockBuilder.BuildApprovalText(request);
            var blocks = SlackApprovalBlockBuilder.BuildApprovalBlocks(request);

            using var cts = new CancellationTokenSource(OperationTimeout, _dependencies.TimeProvider);
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

    protected override async Task RedrawClosedApprovalPromptAsync(PendingApprovalRequest pending, string closeText)
    {
        var promptTs = pending.PromptId ?? throw new InvalidOperationException("A closed prompt redraw needs its message.");
        try
        {
            using var cts = new CancellationTokenSource(OperationTimeout, _dependencies.TimeProvider);
            await _dependencies.ReplyClient.UpdateThreadMessageAsync(
                _channelId,
                promptTs,
                closeText,
                SlackApprovalBlockBuilder.BuildClosedApprovalBlocks(closeText),
                cts.Token);
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "Failed to redraw closed approval prompt for call {CallId} messageTs={MessageTs}",
                pending.CallId, promptTs);
        }
    }

    protected override async Task TryResolveApprovalPromptAsync(
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

            using var cts = new CancellationTokenSource(OperationTimeout, _dependencies.TimeProvider);
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

    protected override async Task<TransportResult> UploadFileAsync(FileOutput file)
    {
        var startedAt = _dependencies.TimeProvider.GetTimestamp();
        try
        {
            if (!File.Exists(file.FilePath))
            {
                _log.Warning("File not found for upload: {Path}", file.FilePath);
                return new TransportResult.Failure(DeliveryFailureKind.Unknown, $"File not found for upload: {file.FilePath}");
            }

            using var cts = new CancellationTokenSource(OperationTimeout, _dependencies.TimeProvider);
            await _dependencies.ReplyClient.UploadFileToThreadAsync(
                _channelId,
                _threadTs,
                file.FilePath,
                file.FileName,
                cts.Token);

            _log.Info("Uploaded file to Slack thread: {FileName}", file.FileName);
            return new TransportResult.Success();
        }
        catch (OperationCanceledException ex)
        {
            _log.Error(ex, "Timed out uploading file {FileName} to Slack thread", file.FileName);
            ChannelTelemetry.For(ChannelType.Slack).RecordReplyFailed(_dependencies.TimeProvider.GetElapsedTime(startedAt).TotalMilliseconds);
            return new TransportResult.Failure(DeliveryFailureKind.TransportFailure, $"Timed out uploading file: {ex.Message}");
        }
        catch (SlackMessageDeliveryException ex)
        {
            _log.Warning("Delivery rejected for file upload {FileName} session={SessionId} error={ErrorCode} kind={FailureKind}",
                file.FileName, _sessionId.Value, ex.ErrorCode ?? "unknown", ex.FailureKind);
            ChannelTelemetry.For(ChannelType.Slack).RecordReplyRejected(ex.ErrorCode);
            return new TransportResult.Failure(ex.FailureKind, ex.Message);
        }
        catch (Exception ex)
        {
            _log.Error(ex, "Failed to upload file {FileName} to Slack thread", file.FileName);
            ChannelTelemetry.For(ChannelType.Slack).RecordReplyFailed(_dependencies.TimeProvider.GetElapsedTime(startedAt).TotalMilliseconds);
            return new TransportResult.Failure(DeliveryFailureKind.Unknown, ex.Message);
        }
    }

}
