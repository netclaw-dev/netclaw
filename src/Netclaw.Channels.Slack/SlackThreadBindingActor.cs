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
using Netclaw.Security;

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
    private readonly List<PendingApprovalRequest> _pendingApprovalRequests = [];

    private readonly SessionPipelineHandle _handle;
    private SlackEventTs? _cursorTs;
    private SlackEventTs? _pendingCursorTs;
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
        _promptInjectionDetector = dependencies.PromptInjectionDetector ?? new NullPromptInjectionDetector();
        _handle = new SessionPipelineHandle(dependencies.Pipeline, Context.GetLogger(), "slack-thread");
        _log = Context.GetLogger()
            .WithContext("Adapter", "slack")
            .WithContext("SessionId", _sessionId.Value)
            .WithContext("SlackChannelId", _channelId)
            .WithContext("SlackThreadTs", _threadTs);

        Recover<CursorAdvanced>(ApplyCursorAdvanced);

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
                Become(Active);
                Stash.UnstashAll();
            }
            catch (Exception ex)
            {
                _log.Error(ex, "Failed to initialize Slack thread pipeline; stopping actor");
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

            _log.Warning("Slack output stream terminated ({Reason}); reinitializing pipeline", reason);
            Self.Tell(new ReinitializePipeline(reason));
        });
        CommandAsync<ReinitializePipeline>(async msg => await ReinitializePipelineAsync(msg.Reason));
        Command<ReceiveTimeout>(_ =>
        {
            if (_pendingApprovalRequests.Count > 0)
            {
                _log.Info("Slack thread idle but {0} approval(s) are pending; deferring passivation", _pendingApprovalRequests.Count);
                return;
            }

            _log.Info("Slack thread idle for 1 hour, passivating");
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
            _log.Warning("Slack thread input queue is not initialized; rejecting Mode B reminder");
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
            ReminderId = message.Source.ReminderId,
            AckTarget = ackTarget
        };

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
            _log.Warning("Slack thread input queue closed; rejecting Mode B reminder for session {0}", _sessionId.Value);
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
            inboundLog.Info("slack_turn_received textChars={TextLength} fileCount={FileCount}",
                message.Text?.Length ?? 0,
                message.Files?.Count ?? 0);

            var currentTs = new SlackEventId(message.EventId.Value).TryGetEventTs();

            if (!string.IsNullOrWhiteSpace(message.Text)
                && ToolInteractionResponseParser.TryParseApprovalResponse(message.Text, out var selectedKey)
                && selectedKey is not null
                && await TryHandleApprovalResponseAsync(message, selectedKey))
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
                _log.Warning("Slack thread input queue is not initialized; dropping inbound message");
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

            var buildResult = await BuildInputForInboundAsync(message, contents, currentTs, inboundCts.Token);
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
                _log.Warning(ex, "Timed out enqueueing Slack message for session {0}", _sessionId.Value);
                Self.Tell(new ReinitializePipeline("input queue write timeout"));
                return;
            }
            catch (ChannelClosedException ex)
            {
                _log.Warning(ex, "Slack thread input queue closed for session {0}", _sessionId.Value);
                Self.Tell(new ReinitializePipeline("input queue write failed"));
                return;
            }

            inboundLog.Info("slack_turn_enqueued contentItems={ContentCount}", input.Contents.Count);
            ChannelTelemetry.For(ChannelType.Slack).RecordMessageEnqueued();
        }
        catch (OperationCanceledException ex)
        {
            inboundLog.Warning(ex, "slack_turn_enqueue_timeout");
        }
        catch (Exception ex)
        {
            inboundLog.Error(ex, "slack_turn_enqueue_failed");
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
                "slack_attachments_rejected count={Count} limit={Limit} audience={Audience} reason=too-many-files",
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
            await SafePostAsync(joined);
        }
    }

    private async Task<AttachmentIngestResult> TryIngestSingleAttachmentAsync(
        SlackFileReference file,
        TrustAudience audience,
        ChannelAttachmentPolicy policy,
        bool inlineImages,
        string inboxDir,
        string stagingDir,
        CancellationToken cancellationToken)
    {
        var category = AttachmentCategories.FromMime(file.MimeType);

        // Pre-download policy gates — these all operate on Slack-reported
        // metadata and avoid burning bandwidth on files that can't be accepted.
        if (!policy.Allows(category))
        {
            _log.Warning(
                "slack_attachment_rejected name={Name} mime={Mime} audience={Audience} category={Category} reason=category-not-allowed",
                file.Name, file.MimeType, audience, category);
            return new AttachmentIngestResult.Rejected(
                $"`{file.Name}` ({category}) isn't allowed in {audience} channels. " +
                "Please DM me if you want to share this class of file.");
        }

        if (file.Size > policy.MaxFileBytes)
        {
            _log.Warning(
                "slack_attachment_rejected name={Name} mime={Mime} audience={Audience} size={Size} limit={Limit} reason=too-large",
                file.Name, file.MimeType, audience, file.Size, policy.MaxFileBytes);
            return new AttachmentIngestResult.Rejected(
                $"`{file.Name}` ({FormatBytes(file.Size)}) exceeds the {FormatBytes(policy.MaxFileBytes)} per-file limit.");
        }

        AttachmentDownloadResult downloadResult;
        try
        {
            using var downloadCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            downloadCts.CancelAfter(OperationTimeout);
            downloadResult = await DownloadSlackFileToDirectoryAsync(
                file, stagingDir, policy.MaxFileBytes, downloadCts.Token);
        }
        catch (AttachmentTooLargeException ex)
        {
            _log.Warning(
                "slack_attachment_rejected name={Name} mime={Mime} audience={Audience} size={Size} limit={Limit} reason=too-large-during-download",
                file.Name, file.MimeType, audience, ex.BytesReceived, ex.MaxBytes);
            return new AttachmentIngestResult.Rejected(
                $"`{file.Name}` ({FormatBytes(ex.BytesReceived)}) exceeds the {FormatBytes(ex.MaxBytes)} per-file limit.");
        }
        catch (OperationCanceledException ex)
        {
            _log.Warning(ex,
                "slack_attachment_rejected name={Name} mime={Mime} reason=download-timeout",
                file.Name, file.MimeType);
            return new AttachmentIngestResult.Rejected(
                $"Timed out downloading `{file.Name}`. Please try again.");
        }
        catch (Exception ex)
        {
            _log.Warning(ex,
                "slack_attachment_rejected name={Name} mime={Mime} reason=download-failed",
                file.Name, file.MimeType);
            return new AttachmentIngestResult.Rejected(
                $"Couldn't download `{file.Name}` — please try again later.");
        }

        if (downloadResult.BytesWritten == 0)
        {
            _log.Warning(
                "slack_attachment_rejected name={Name} mime={Mime} reason=empty-download",
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
                "slack_attachment_rejected name={Name} mime={Mime} reason=scan-exception",
                file.Name, file.MimeType);
            TryDeleteTemp(downloadResult.FilePath);
            return new AttachmentIngestResult.Rejected(
                $"Couldn't scan `{file.Name}` — please try again later.");
        }

        if (!scanResult.IsAllowed)
        {
            _log.Warning(
                "slack_attachment_rejected name={Name} mime={Mime} reason=scan-blocked error={ScanError} message={ScanMessage}",
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

        // Move temp file to final inbox path with collision suffixing.
        string inboxPath;
        try
        {
            inboxPath = InboxWriter.SanitizeReserveAndMove(
                inboxDir, file.Name, downloadResult.FilePath);
        }
        catch (InboxWriter.CollisionExhaustedException ex)
        {
            _log.Warning(ex,
                "slack_attachment_rejected name={Name} reason=collision-exhausted",
                file.Name);
            TryDeleteTemp(downloadResult.FilePath);
            return new AttachmentIngestResult.Rejected(
                $"Too many attachments named `{file.Name}` in this session — please rename and try again.");
        }
        catch (Exception ex)
        {
            _log.Error(ex,
                "slack_attachment_rejected name={Name} reason=inbox-write-failed",
                file.Name);
            TryDeleteTemp(downloadResult.FilePath);
            return new AttachmentIngestResult.Rejected(
                $"Couldn't save `{file.Name}` — please try again later.");
        }

        // Decide inlining based on model modalities and category.
        var (inlined, note) = AttachmentIngressFormatting.ResolveInlineDecision(category, inlineImages);

        var relativePath = $"{SessionDirectoryHelper.InboxSubdirectory}/{Path.GetFileName(inboxPath)}";
        var line = AttachmentIngressFormatting.BuildAttachmentLine(
            file.Name, file.MimeType, downloadResult.BytesWritten, relativePath, inlined, note);

        DataContent? inlineContent = null;
        if (inlined)
        {
            var inlineBytes = await File.ReadAllBytesAsync(inboxPath, cancellationToken);
            inlineContent = new DataContent(inlineBytes, file.MimeType);
        }

        _log.Info(
            "slack_attachment_accepted name={Name} mime={Mime} size={Size} audience={Audience} category={Category} inlined={Inlined}",
            file.Name, file.MimeType, downloadResult.BytesWritten, audience, category, inlined);

        return new AttachmentIngestResult.Accepted(line, inlineContent);
    }

    /// <summary>
    /// Formats a single <c>[attachment]</c> announcement line in the
    /// canonical cross-channel shape defined in netclaw-input-adapters.
    /// </summary>
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

    private SessionPipelineOptions BuildOptions() => new()
    {
        ChannelType = Actors.Channels.ChannelType.Slack,
        DefaultAudience = TrustAudience.Public,
        DefaultBoundary = SecurityPolicyDefaults.SlackWorkspaceBoundary,
        DefaultPrincipal = PrincipalClassification.UntrustedExternal,
        DefaultProvenance = new SourceProvenance
        {
            TransportAuthenticity = TransportAuthenticity.Verified,
            PayloadTaint = PayloadTaint.Public,
            SourceKind = "slack"
        },
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

    private async Task<InboundBuildResult> BuildInputForInboundAsync(
        SlackThreadInbound triggeringMessage,
        IReadOnlyList<AIContent> liveContents,
        SlackEventTs? currentTs,
        CancellationToken cancellationToken)
    {
        var baseInput = new ChannelInput
        {
            SenderId = triggeringMessage.SenderId,
            ChannelId = _channelId.Value,
            MessageId = triggeringMessage.EventId.Value,
            Audience = triggeringMessage.Audience,
            Principal = triggeringMessage.Principal,
            Provenance = triggeringMessage.Provenance,
            Contents = liveContents,
            ReceivedAt = triggeringMessage.ReceivedAt,
            ExecutableText = triggeringMessage.Text
        };

        if (currentTs is not { } triggerTs)
            return new InboundBuildResult(baseInput, false);

        var history = await _dependencies.ThreadHistoryFetcher.FetchThreadHistoryAsync(_sessionId, cancellationToken);
        if (history.Count == 0)
            return new InboundBuildResult(baseInput, false);

        var cursor = _cursorTs;

        // Phase 1: filter gap candidates by ts bounds (cheap, sync).
        var candidates = new List<ChannelInput>();
        foreach (var item in history)
        {
            if (new SlackEventId(item.MessageId ?? string.Empty).TryGetEventTs() is not { } itemTs)
                continue;

            if (itemTs.CompareTo(triggerTs) >= 0)
                continue;

            // Keep the cursor event itself during fresh-runtime hydration.
            // If the daemon restarts while a turn is still in-flight, the
            // session actor may recover with no completed turns even though the
            // cursor already advanced to the last inbound user message.
            // Including ts == cursor prevents that newest pre-restart user turn
            // from being dropped from rebuilt context.
            if (cursor is { } c && itemTs.CompareTo(c) < 0)
                continue;

            candidates.Add(item);
        }

        if (candidates.Count == 0)
        {
            _log.Info(
                "Thread history hydrated fetched={FetchedCount} gapCount=0 cursor={Cursor}",
                history.Count,
                cursor?.Value ?? "none");
            return new InboundBuildResult(baseInput, false);
        }

        // Phase 2: classify candidates in parallel — detector calls are the
        // latency bottleneck on large gaps.
        var classifications = await Task.WhenAll(candidates.Select(c => ClassifyGapMessageAsync(c, cancellationToken)));

        // Phase 3: assemble gap preserving chronological order.
        var gap = new List<AdoptedContextMessage>(candidates.Count);
        var blockedForRisk = 0;
        var detectorUnavailable = false;

        for (var i = 0; i < candidates.Count; i++)
        {
            var item = candidates[i];
            switch (classifications[i].Outcome)
            {
                case ClassificationOutcome.Allow:
                    var authority = SlackAclPolicy.IsAllowedUser(new SlackUserId(item.SenderId), _dependencies.Options)
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

        _log.Info(
            "Thread history hydrated fetched={FetchedCount} gapCount={GapCount} blockedHighRisk={BlockedHighRiskCount} cursor={Cursor}",
            history.Count,
            gap.Count,
            blockedForRisk,
            cursor?.Value ?? "none");

        if (gap.Count == 0)
            return new InboundBuildResult(baseInput, detectorUnavailable);

        var merged = MergeGapWithLiveContents(gap, liveContents, triggeringMessage);
        return new InboundBuildResult(baseInput with
        {
            Contents = merged.Contents,
            HasAdoptedContext = true,
            AdoptedSpeakerIds = merged.SpeakerIds,
            AdoptedContextProjection = merged.Projection,
            AdoptedContextLowerBound = cursor?.Value,
            AdoptedContextUpperBound = triggeringMessage.EventId.Value,
            AdoptedContextEntries = merged.Entries
        }, detectorUnavailable);
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

    private void AdvanceCursor(SlackEventTs candidateTs)
    {
        if (_cursorTs is { } c && candidateTs.CompareTo(c) <= 0)
        {
            _log.Debug("Slack thread cursor did not advance stream={StreamKey} ts={Ts}", _sessionId.Value, candidateTs.Value);
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

    private readonly record struct InboundBuildResult(ChannelInput Input, bool BackfillDetectorUnavailable);

    private static AdoptedContextMergeResult MergeGapWithLiveContents(
        IReadOnlyList<AdoptedContextMessage> gap,
        IReadOnlyList<AIContent> liveContents,
        SlackThreadInbound triggeringMessage)
        => AdoptedContextContentBuilder.MergeWithCurrentMessage(
            gap,
            liveContents,
            triggeringMessage.SenderId,
            triggeringMessage.ReceivedAt);

    private async Task ReinitializePipelineAsync(string reason)
    {
        _pendingCursorTs = null;
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
                var pendingApproval = new PendingApprovalRequest(interaction);
                _pendingApprovalRequests.Add(pendingApproval);
                var promptMessageTs = await HandleApprovalRequestAsync(interaction);
                if (promptMessageTs is not null)
                {
                    pendingApproval.PromptMessageTs = promptMessageTs;
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

                if (!string.IsNullOrWhiteSpace(completed.SourceReminderId) && (_postedThisTurn || _uploadedFileThisTurn))
                {
                    Context.System.EventStream.Publish(new ReminderDeliveryObserved(
                        completed.SourceReminderId,
                        ChannelType.Slack,
                        _dependencies.TimeProvider.GetUtcNow().ToUnixTimeMilliseconds()));
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
                _pendingApprovalRequests.Clear();

                break;
        }
    }

    private async Task<bool> TryHandleApprovalResponseAsync(SlackThreadInbound message, string selectedKey)
    {
        if (_pendingApprovalRequests.Count == 0)
            return false;

        var pendingIndex = _pendingApprovalRequests.FindIndex(request =>
            ApprovalButtonValueCodec.CanApprove(request.Request.RequesterPrincipal, request.Request.RequesterSenderId, message.SenderId));

        if (pendingIndex < 0)
        {
            await SafePostAsync(":warning: Only the requesting user can approve this tool action.");
            return true;
        }

        var pending = _pendingApprovalRequests[pendingIndex];

        try
        {
            await _dependencies.Pipeline.SendFeedbackAsync(new ToolInteractionResponse
            {
                SessionId = _sessionId,
                CallId = pending.Request.CallId,
                SelectedKey = selectedKey,
                SenderId = message.SenderId
            });

            _pendingApprovalRequests.RemoveAt(pendingIndex);

            await TryResolveApprovalPromptAsync(pending, selectedKey, message.SenderId);

            _log.Info(
                "Recorded Slack approval response for call {CallId} sender={SenderId} selection={SelectedKey}",
                pending.Request.CallId,
                message.SenderId,
                selectedKey);
        }
        catch (Exception ex)
        {
            _log.Error(ex, "Failed to route Slack approval response for call {CallId}", pending.Request.CallId);
        }

        return true;
    }

    private async Task HandleApprovalResponseAsync(SlackApprovalResponse message)
    {
        if (_pendingApprovalRequests.Count == 0)
            return;

        var pendingIndex = _pendingApprovalRequests.FindIndex(request =>
            string.Equals(request.Request.CallId, message.CallId, StringComparison.Ordinal));

        if (pendingIndex < 0)
            return;

        var pending = _pendingApprovalRequests[pendingIndex];
        if (!ApprovalButtonValueCodec.CanApprove(pending.Request.RequesterPrincipal, pending.Request.RequesterSenderId, message.SenderId))
        {
            await SafePostAsync(":warning: Only the requesting user can approve this tool action.");
            return;
        }

        try
        {
            await _dependencies.Pipeline.SendFeedbackAsync(new ToolInteractionResponse
            {
                SessionId = _sessionId,
                CallId = pending.Request.CallId,
                SelectedKey = message.SelectedKey,
                SenderId = message.SenderId
            });

            _pendingApprovalRequests.RemoveAt(pendingIndex);

            await TryResolveApprovalPromptAsync(pending, message.SelectedKey, message.SenderId);

            _log.Info(
                "Recorded Slack button approval response for call {CallId} sender={SenderId} selection={SelectedKey}",
                pending.Request.CallId,
                message.SenderId,
                message.SelectedKey);
        }
        catch (Exception ex)
        {
            _log.Error(ex, "Failed to route Slack button approval response for call {CallId}", pending.Request.CallId);
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
            _log.Warning("Slack delivery rejected for session {SessionId} error={ErrorCode} kind={FailureKind}",
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

    private async Task NotifyDeliveryFailedAsync(int turnNumber, DeliveryFailureKind failureKind, string errorMessage)
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
                SelectedKey = ApprovalOptionKeys.Deny,
                SenderId = request.RequesterSenderId ?? string.Empty
            });
        }
        catch (Exception ex)
        {
            _log.Error(ex, "Failed to send auto-deny feedback for {CallId}", request.CallId);
        }

        await SafePostAsync(
            $":warning: I couldn't post the approval prompt for `{request.ToolName}`. The action was automatically denied — please ask me to try again.");
    }

    private async Task TryResolveApprovalPromptAsync(PendingApprovalRequest pending, string selectedKey, string senderId)
    {
        if (pending.PromptMessageTs is not { } promptTs)
            return;

        try
        {
            var text = SlackApprovalBlockBuilder.BuildResolvedApprovalText(
                pending.Request,
                selectedKey,
                senderId);
            var blocks = SlackApprovalBlockBuilder.BuildResolvedApprovalBlocks(
                pending.Request,
                selectedKey,
                senderId);

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
                pending.Request.CallId,
                pending.PromptMessageTs);
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
            _log.Warning("Slack delivery rejected for file upload {FileName} session={SessionId} error={ErrorCode} kind={FailureKind}",
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

    private sealed record ThreadOutput(SessionOutput Output);
    private sealed record OutputStreamTerminated(int Generation, Exception? Cause);
    private sealed record ReinitializePipeline(string Reason);
    private sealed class PendingApprovalRequest(ToolInteractionRequest request)
    {
        public ToolInteractionRequest Request { get; } = request;

        public SlackEventTs? PromptMessageTs { get; set; }
    }

    private sealed record InitializePipeline
    {
        public static InitializePipeline Instance { get; } = new();
    }
}
