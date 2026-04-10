using System.Text;
using System.Threading.Channels;
using System.Threading.Tasks;
using Akka.Actor;
using Akka.Event;
using Akka.Persistence;
using Akka.Streams;
using Akka.Streams.Dsl;
using Microsoft.Extensions.AI;
using Netclaw.Actors.Channels;
using Netclaw.Actors.Protocol;
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

    private ActorMaterializer? _materializer;
    private MaterializedSession? _session;
    private ChannelWriter<ChannelInput>? _inputQueue;
    private int _pipelineGeneration;
    private bool _isReinitializing;
    private bool _threadHistoryFetchAttempted;
    private SlackEventTs? _cursorTs;
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
        _inputQueue?.TryComplete();
        _session?.DisposeAsync().AsTask().GetAwaiter().GetResult();
        _materializer?.Dispose();
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
        CommandAsync<StartProactiveThread>(HandleProactiveThreadAsync);
        CommandAsync<ThreadOutput>(HandleOutputAsync);
        Command<OutputStreamTerminated>(msg =>
        {
            if (msg.Generation != _pipelineGeneration)
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
            _log.Info("Slack thread idle for 1 hour, passivating");
            Context.Stop(Self);
        });
    }

    private async Task HandleProactiveThreadAsync(StartProactiveThread message)
    {
        _log.Info("Initializing proactive thread pipeline for session {0}", message.SessionId.Value);
        await EnsureInitializedAsync();
        Sender.Tell(new ProactiveThreadAck(message.SessionId));
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

            if (!string.IsNullOrWhiteSpace(message.Text))
            {
                var classification = await ClassifyAsync(message.Text, "slack-live", inboundCts.Token);
                switch (classification.Outcome)
                {
                    case ClassificationOutcome.Block:
                        _log.Warning("Blocked Slack message due to prompt injection risk: {Reason}", classification.Reason);
                        ChannelTelemetry.RecordSlackEventDropped("prompt_injection_high");
                        await SafePostAsync(LiveInjectionBlockedWarning);
                        return;

                    case ClassificationOutcome.DetectorUnavailable:
                        _log.Warning("Prompt injection detector unavailable for live message — dropping");
                        ChannelTelemetry.RecordSlackEventDropped("prompt_injection_detector_unavailable");
                        await SafePostAsync(LiveDetectorUnavailableWarning);
                        return;

                    case ClassificationOutcome.Allow:
                        break;
                }
            }

            // Build content list: text + downloaded file attachments
            var contents = new List<AIContent>();
            if (!string.IsNullOrEmpty(message.Text))
                contents.Add(new TextContent(message.Text));

            // Download and scan file attachments
            if (message.Files is { Count: > 0 } && _dependencies.HttpClient is not null)
            {
                foreach (var file in message.Files)
                {
                    // Only process image MIME types for now
                    if (!file.MimeType.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
                    {
                        _log.Debug("Skipping non-image file attachment: {Name} ({MimeType})", file.Name, file.MimeType);
                        continue;
                    }

                    try
                    {
                        using var downloadCts = CancellationTokenSource.CreateLinkedTokenSource(inboundCts.Token);
                        downloadCts.CancelAfter(OperationTimeout);
                        var bytes = await DownloadSlackFileAsync(file, downloadCts.Token);
                        if (bytes.Length == 0)
                            continue;

                        using var scanCts = CancellationTokenSource.CreateLinkedTokenSource(inboundCts.Token);
                        scanCts.CancelAfter(OperationTimeout);
                        var scanResult = await _dependencies.ContentScanner.ScanAsync(
                            bytes, file.Name, file.MimeType, scanCts.Token);

                        if (!scanResult.IsAllowed)
                        {
                            if (scanResult.Error == ContentScanError.ScanFailure)
                            {
                                // Scanner itself is broken — allow the file through rather
                                // than silently dropping valid images. The LLM provider
                                // will still validate content on its end.
                                _log.Error("Content scanner failed for file {Name}: {Message} — allowing file through",
                                    file.Name, scanResult.Message);
                            }
                            else
                            {
                                _log.Warning("Content scan rejected file {Name}: {Message}",
                                    file.Name, scanResult.Message ?? scanResult.Error?.ToString());
                                continue;
                            }
                        }

                        contents.Add(new DataContent(bytes.ToArray(), file.MimeType));
                        _log.Info("Downloaded and scanned Slack file: {Name} ({Size} bytes)", file.Name, bytes.Length);
                    }
                    catch (OperationCanceledException ex)
                    {
                        _log.Warning(ex, "Timed out while processing Slack file {Name}, skipping", file.Name);
                    }
                    catch (Exception ex)
                    {
                        _log.Warning(ex, "Failed to download Slack file {Name}, skipping", file.Name);
                    }
                }
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

            var writer = _inputQueue;
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
                ChannelTelemetry.RecordSlackEventDropped("stale_event");
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

                if (currentTs is { } ts)
                    AdvanceCursor(ts);
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
            ChannelTelemetry.RecordSlackMessageEnqueued();
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

    private Task<ReadOnlyMemory<byte>> DownloadSlackFileAsync(SlackFileReference file, CancellationToken ct)
        => SlackFileDownloader.DownloadAsync(_dependencies.HttpClient!, file.UrlPrivateDownload, _dependencies.Options.BotToken, ct);

    private async Task EnsureInitializedAsync()
    {
        if (_session is not null)
            return;

        _log.Info("Initializing Slack thread binding pipeline");
        var self = Self;

        // Create a materializer scoped to this actor — all stream actors become
        // children and are stopped automatically when this actor passivates.
        _materializer = Context.Materializer(namePrefix: "slack-thread");

        using var initCts = new CancellationTokenSource(OperationTimeout);
        var materialized = await _dependencies.Pipeline.CreateAsync(
            _sessionId,
            new SessionPipelineOptions
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
            },
            materializer: _materializer,
            cancellationToken: initCts.Token);

        var inputQueue = Source.Channel<ChannelInput>(512, true)
            .ToMaterialized(materialized.Input, Keep.Left)
            .Run(_materializer);

        var generation = ++_pipelineGeneration;
        var outputCompletion = materialized.Output
            .ToMaterialized(
                Sink.ForEach<SessionOutput>(output => self.Tell(new ThreadOutput(output))),
                Keep.Right)
            .Run(_materializer);

        _ = outputCompletion.ContinueWith(t =>
            {
                var cause = t.IsFaulted ? t.Exception?.GetBaseException() : null;
                self.Tell(new OutputStreamTerminated(generation, cause));
            },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);

        _session = materialized;
        _inputQueue = inputQueue;

        _log.Info("Slack thread binding pipeline initialized");
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
            ReceivedAt = triggeringMessage.ReceivedAt
        };

        // Only attempt hydration once per actor runtime. Setting the flag up front
        // avoids re-fetching the entire thread if any downstream step throws.
        if (_threadHistoryFetchAttempted || currentTs is not { } triggerTs)
            return new InboundBuildResult(baseInput, false);

        _threadHistoryFetchAttempted = true;

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

            if (cursor is { } c && itemTs.CompareTo(c) <= 0)
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
        var gap = new List<ChannelInput>(candidates.Count);
        var blockedForRisk = 0;
        var detectorUnavailable = false;

        for (var i = 0; i < candidates.Count; i++)
        {
            var item = candidates[i];
            switch (classifications[i].Outcome)
            {
                case ClassificationOutcome.Allow:
                    gap.Add(item);
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

        var mergedContents = MergeGapWithLiveContents(gap, liveContents);
        return new InboundBuildResult(baseInput with { Contents = mergedContents }, detectorUnavailable);
    }

    private Task<Classification> ClassifyGapMessageAsync(ChannelInput input, CancellationToken cancellationToken)
    {
        var text = string.Join("\n", input.Contents
            .OfType<TextContent>()
            .Select(t => t.Text)
            .Where(t => !string.IsNullOrWhiteSpace(t)));

        return ClassifyAsync(text, "slack-backfill", cancellationToken);
    }

    private async Task<Classification> ClassifyAsync(string? text, string sourceContext, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(text))
            return new Classification(ClassificationOutcome.Allow, null);

        PromptInjectionResult detection;
        try
        {
            detection = await _promptInjectionDetector.DetectAsync(text, sourceContext, cancellationToken);
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
        if (_cursorTs is not { } c || eventTs is not { } ts)
            return false;

        return ts.CompareTo(c) <= 0;
    }

    private void ApplyCursorAdvanced(CursorAdvanced advanced)
    {
        _cursorTs = new SlackEventTs(advanced.CursorTs);

        // Skip journal truncation during recovery replay — we only need to run
        // it when new events are being persisted.
        if (!IsRecovering && LastSequenceNr > 1 && LastSequenceNr % 10 == 0)
            DeleteMessages(LastSequenceNr - 1);
    }

    private enum ClassificationOutcome
    {
        Allow,
        Block,
        DetectorUnavailable
    }

    private readonly record struct Classification(ClassificationOutcome Outcome, string? Reason);

    private readonly record struct InboundBuildResult(ChannelInput Input, bool BackfillDetectorUnavailable);

    private readonly record struct CursorAdvanced(string CursorTs);

    private static List<AIContent> MergeGapWithLiveContents(IReadOnlyList<ChannelInput> gap, IReadOnlyList<AIContent> liveContents)
    {
        var sb = new StringBuilder();
        sb.AppendLine("[thread history — messages exchanged before this inbound event]");
        sb.AppendLine();

        foreach (var item in gap)
        {
            var ts = item.ReceivedAt == default ? string.Empty : $", {item.ReceivedAt:yyyy-MM-dd HH:mm} UTC";
            sb.AppendLine($"<user: {item.SenderId}{ts}>");

            var imageCount = 0;
            foreach (var content in item.Contents)
            {
                switch (content)
                {
                    case TextContent text when !string.IsNullOrWhiteSpace(text.Text):
                        sb.AppendLine(text.Text);
                        break;
                    case DataContent:
                        imageCount++;
                        break;
                }
            }

            if (imageCount > 0)
                sb.AppendLine($"[image attachments: {imageCount}]");

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

        // Gap image bytes are intentionally NOT copied into the merged input:
        // the summary line above already records the count. Copying the raw
        // bytes duplicates multi-MB payloads on every hydrated first turn.
        foreach (var content in liveContents)
        {
            if (content is not TextContent)
                merged.Add(content);
        }

        return merged;
    }

    private async Task ReinitializePipelineAsync(string reason)
    {
        if (_isReinitializing)
            return;

        _isReinitializing = true;
        try
        {
            _log.Warning("Reinitializing Slack thread pipeline: {Reason}", reason);

            _inputQueue?.TryComplete();
            _inputQueue = null;

            if (_session is not null)
            {
                await _session.DisposeAsync();
                _session = null;
            }

            _materializer?.Dispose();
            _materializer = null;

            await EnsureInitializedAsync();
        }
        catch (Exception ex)
        {
            _log.Error(ex, "Slack thread pipeline reinitialization failed; scheduling retry");
            Timers.StartSingleTimer(
                ReinitializeTimerKey,
                new ReinitializePipeline("retry after failed reinit"),
                TimeSpan.FromSeconds(2));
        }
        finally
        {
            _isReinitializing = false;
        }
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

            case ErrorOutput err:
                var refId = err.CorrelationId.ToString("N")[..8];
                var errorResult = await SafePostAsync($":warning: {err.Message} (ref: {refId})");
                if (errorResult.Success)
                    _postedThisTurn = true;
                else
                    _lastFailedPost = errorResult;
                break;

            case TurnCompleted completed:
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

                break;
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
            ChannelTelemetry.RecordSlackReplyPosted(_dependencies.TimeProvider.GetElapsedTime(startedAt).TotalMilliseconds);
            return PostResult.Ok;
        }
        catch (OperationCanceledException ex)
        {
            _log.Error(ex, "Timed out posting Slack reply for session {0}", _sessionId.Value);
            ChannelTelemetry.RecordSlackReplyFailed(_dependencies.TimeProvider.GetElapsedTime(startedAt).TotalMilliseconds);
            return new PostResult($"Timed out posting reply: {ex.Message}", DeliveryFailureKind.TransportFailure);
        }
        catch (SlackMessageDeliveryException ex)
        {
            _log.Warning("Slack delivery rejected for session {SessionId} error={ErrorCode} kind={FailureKind}",
                _sessionId.Value, ex.ErrorCode ?? "unknown", ex.FailureKind);
            ChannelTelemetry.RecordSlackReplyRejected(ex.ErrorCode);
            return new PostResult(ex.Message, ex.FailureKind);
        }
        catch (Exception ex)
        {
            _log.Error(ex, "Failed posting Slack reply for session {0}", _sessionId.Value);
            ChannelTelemetry.RecordSlackReplyFailed(_dependencies.TimeProvider.GetElapsedTime(startedAt).TotalMilliseconds);
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
            ChannelTelemetry.RecordSlackReplyFailed(_dependencies.TimeProvider.GetElapsedTime(startedAt).TotalMilliseconds);
            return new PostResult($"Timed out uploading file: {ex.Message}", DeliveryFailureKind.TransportFailure);
        }
        catch (SlackMessageDeliveryException ex)
        {
            _log.Warning("Slack delivery rejected for file upload {FileName} session={SessionId} error={ErrorCode} kind={FailureKind}",
                file.FileName, _sessionId.Value, ex.ErrorCode ?? "unknown", ex.FailureKind);
            ChannelTelemetry.RecordSlackReplyRejected(ex.ErrorCode);
            return new PostResult(ex.Message, ex.FailureKind);
        }
        catch (Exception ex)
        {
            _log.Error(ex, "Failed to upload file {FileName} to Slack thread", file.FileName);
            ChannelTelemetry.RecordSlackReplyFailed(_dependencies.TimeProvider.GetElapsedTime(startedAt).TotalMilliseconds);
            return new PostResult(ex.Message, DeliveryFailureKind.Unknown);
        }
    }

    private sealed record ThreadOutput(SessionOutput Output);
    private sealed record OutputStreamTerminated(int Generation, Exception? Cause);
    private sealed record ReinitializePipeline(string Reason);
    private sealed record InitializePipeline
    {
        public static InitializePipeline Instance { get; } = new();
    }
}
