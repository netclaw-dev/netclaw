using System.Text;
using System.Threading.Channels;
using System.Threading.Tasks;
using Akka.Actor;
using Akka.Event;
using Akka.Streams;
using Akka.Streams.Dsl;
using Microsoft.Extensions.AI;
using Netclaw.Actors.Channels;
using Netclaw.Actors.Protocol;
using Netclaw.Channels.Telemetry;
using Netclaw.Configuration;
using Netclaw.Security;

namespace Netclaw.Channels.Slack;

internal sealed class SlackThreadBindingActor : ReceiveActor, IWithUnboundedStash, IWithTimers
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
    private static readonly object ReinitializeTimerKey = new();
    private static readonly TimeSpan InboundProcessingTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan QueueWriteTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan FileDownloadTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan ContentScanTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan PipelineInitTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan ReplyOperationTimeout = TimeSpan.FromSeconds(5);

    public IStash Stash { get; set; } = null!;
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

        Initializing();

        Context.SetReceiveTimeout(TimeSpan.FromHours(1));
    }

    public static Props CreateProps(
        SessionId sessionId,
        SlackChannelId channelId,
        SlackThreadTs threadTs,
        SlackGatewayDependencies dependencies) =>
        Props.Create(() => new SlackThreadBindingActor(sessionId, channelId, threadTs, dependencies));

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
                _log.Error(ex, "Failed to initialize Slack thread pipeline; stopping actor");
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
        ReceiveAsync<SlackThreadInbound>(HandleInboundAsync);
        ReceiveAsync<StartProactiveThread>(HandleProactiveThreadAsync);
        ReceiveAsync<ThreadOutput>(HandleOutputAsync);
        Receive<OutputStreamTerminated>(msg =>
        {
            if (msg.Generation != _pipelineGeneration)
                return;

            var reason = msg.Cause is null
                ? "completed"
                : $"faulted: {msg.Cause.Message}";

            _log.Warning("Slack output stream terminated ({Reason}); reinitializing pipeline", reason);
            Self.Tell(new ReinitializePipeline(reason));
        });
        ReceiveAsync<ReinitializePipeline>(async msg => await ReinitializePipelineAsync(msg.Reason));
        Receive<ReceiveTimeout>(_ =>
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

            if (!string.IsNullOrWhiteSpace(message.Text))
            {
                PromptInjectionResult detection;
                try
                {
                    detection = await _promptInjectionDetector.DetectAsync(message.Text, "slack", inboundCts.Token);
                }
                catch (Exception ex)
                {
                    _log.Warning(ex, "Prompt injection detector failed; allowing message through");
                    detection = PromptInjectionResult.Safe();
                }

                if (detection.Risk == PromptInjectionRisk.High)
                {
                    var reason = string.IsNullOrWhiteSpace(detection.Message)
                        ? "High-risk prompt injection pattern detected"
                        : detection.Message;

                    _log.Warning("Blocked Slack message due to prompt injection risk: {Reason}", reason);
                    ChannelTelemetry.RecordSlackEventDropped("prompt_injection_high");
                    await SafePostAsync(":warning: Message blocked by prompt-injection policy.");
                    return;
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
                        downloadCts.CancelAfter(FileDownloadTimeout);
                        var bytes = await DownloadSlackFileAsync(file, downloadCts.Token);
                        if (bytes.Length == 0)
                            continue;

                        using var scanCts = CancellationTokenSource.CreateLinkedTokenSource(inboundCts.Token);
                        scanCts.CancelAfter(ContentScanTimeout);
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

            var writer = _inputQueue;
            if (writer is null)
            {
                _log.Warning("Slack thread input queue is not initialized; dropping inbound message");
                return;
            }

            try
            {
                using var queueWriteCts = CancellationTokenSource.CreateLinkedTokenSource(inboundCts.Token);
                queueWriteCts.CancelAfter(QueueWriteTimeout);

                await writer.WriteAsync(new ChannelInput
                {
                    SenderId = message.SenderId,
                    ChannelId = _channelId.Value,
                    MessageId = message.EventId.Value,
                    Audience = message.Audience,
                    Principal = message.Principal,
                    Provenance = message.Provenance,
                    Contents = contents,
                    ReceivedAt = message.ReceivedAt
                }, queueWriteCts.Token);
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

            inboundLog.Info("slack_turn_enqueued contentItems={ContentCount}", contents.Count);
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

    private async Task<ReadOnlyMemory<byte>> DownloadSlackFileAsync(SlackFileReference file, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, file.UrlPrivateDownload);
        if (_dependencies.Options.BotToken is { Value: { Length: > 0 } token })
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);

        using var response = await _dependencies.HttpClient!.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();

        var bytes = await response.Content.ReadAsByteArrayAsync(ct);
        return bytes;
    }

    private async Task EnsureInitializedAsync()
    {
        if (_session is not null)
            return;

        _log.Info("Initializing Slack thread binding pipeline");
        var self = Self;

        // Create a materializer scoped to this actor — all stream actors become
        // children and are stopped automatically when this actor passivates.
        _materializer = Context.Materializer(namePrefix: "slack-thread");

        using var initCts = new CancellationTokenSource(PipelineInitTimeout);
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
            using var cts = new CancellationTokenSource(ReplyOperationTimeout);
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

            using var cts = new CancellationTokenSource(ReplyOperationTimeout);
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
