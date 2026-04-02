using System.Text;
using Akka.Actor;
using Akka.Event;
using Akka.Streams;
using Akka.Streams.Dsl;
using Microsoft.Extensions.AI;
using Netclaw.Actors.Channels;
using Netclaw.Actors.Protocol;
using Netclaw.Configuration;

namespace Netclaw.Daemon.Webhooks;

internal sealed class WebhookExecutionActor : ReceiveActor
{
    private readonly WebhookInvocation _invocation;
    private readonly ISessionPipeline _pipeline;
    private readonly WebhooksConfig _config;
    private readonly TimeProvider _timeProvider;
    private readonly ILoggingAdapter _log;
    private readonly DateTimeOffset _dispatchedAt;

    private readonly StringBuilder _buffer = new();
    private bool _sawTextDelta;
    private bool _completed;
    private bool _notifyAttempted;
    private bool _notifyFailed;
    private string? _notifyFailureDetail;

    private ActorMaterializer? _materializer;
    private MaterializedSession? _session;

    public static Props CreateProps(
        WebhookInvocation invocation,
        ISessionPipeline pipeline,
        WebhooksConfig config,
        TimeProvider timeProvider) =>
        Props.Create(() => new WebhookExecutionActor(invocation, pipeline, config, timeProvider));

    public WebhookExecutionActor(
        WebhookInvocation invocation,
        ISessionPipeline pipeline,
        WebhooksConfig config,
        TimeProvider timeProvider)
    {
        _invocation = invocation;
        _pipeline = pipeline;
        _config = config;
        _timeProvider = timeProvider;
        _dispatchedAt = timeProvider.GetUtcNow();
        _log = Context.GetLogger();

        Context.SetReceiveTimeout(TimeSpan.FromSeconds(_config.ExecutionTimeoutSeconds));

        Receive<ExecutionStarted>(_ => { });
        Receive<ExecutionOutput>(HandleOutput);
        Receive<ReceiveTimeout>(_ =>
        {
            _log.Warning(
                "Webhook execution timed out route={Route} sessionId={SessionId} elapsed_s={Elapsed}",
                _invocation.Route.Name,
                _invocation.SessionId.Value,
                (int)(_timeProvider.GetUtcNow() - _dispatchedAt).TotalSeconds);
            ReportAndStop(false, "Execution timed out");
        });
    }

    protected override void PreStart()
    {
        Self.Tell(new ExecutionStarted());
        RunTask(InitializeAsync);
    }

    private async Task InitializeAsync()
    {
        try
        {
            _materializer = Context.Materializer(namePrefix: "webhook-exec");

            var materialized = await _pipeline.CreateAsync(_invocation.SessionId, new SessionPipelineOptions
            {
                ChannelType = ChannelType.Webhook,
                DefaultAudience = _invocation.Route.Config.Audience,
                DefaultBoundary = SecurityPolicyDefaults.ResolveBoundaryFromAudience(_invocation.Route.Config.Audience),
                DefaultPrincipal = PrincipalClassification.VerifiedAutomation,
                DefaultProvenance = new SourceProvenance
                {
                    TransportAuthenticity = TransportAuthenticity.Verified,
                    PayloadTaint = ToPayloadTaint(_invocation.Route.Config.Audience),
                    SourceKind = _invocation.EventType ?? _invocation.Route.Name,
                    SourceScope = _invocation.Route.Name
                },
                Filter = OutputFilter.TextStreaming | OutputFilter.ToolCalls,
                PromptOverlay = _invocation.Route.BuildPromptOverlay()
            }, materializer: _materializer);

            var self = Self;
            var inputQueue = Source.Queue<ChannelInput>(8, OverflowStrategy.Backpressure)
                .ToMaterialized(materialized.Input, Keep.Left)
                .Run(_materializer);

            materialized.Output
                .To(Sink.ForEach<SessionOutput>(output => self.Tell(new ExecutionOutput(output))))
                .Run(_materializer);

            _session = materialized;

            await inputQueue.OfferAsync(new ChannelInput
            {
                SenderId = $"webhook:{_invocation.Route.Name}",
                ChannelId = _invocation.Route.Name,
                Contents = [new TextContent(WebhookPayloadFormatter.Format(_invocation))],
                ReceivedAt = _invocation.ReceivedAt
            });

            inputQueue.Complete();
        }
        catch (Exception ex)
        {
            _log.Error(ex,
                "Webhook execution initialization failed route={Route} sessionId={SessionId}",
                _invocation.Route.Name,
                _invocation.SessionId.Value);
            ReportAndStop(false, ex.Message);
        }
    }

    private void HandleOutput(ExecutionOutput wrapper)
    {
        switch (wrapper.Output)
        {
            case TextDeltaOutput delta:
                _buffer.Append(delta.Delta);
                _sawTextDelta = true;
                break;

            case TextOutput text:
                if (!_sawTextDelta)
                    _buffer.Append(text.Text);
                break;

            case ToolResultOutput toolResult:
                TrackNotificationResult(toolResult);
                break;

            case BufferFlush:
                break;

            case TurnCompleted:
                ReportAndStop(BuildNotifyFailureMessage() is null, BuildNotifyFailureMessage());
                break;

            case ErrorOutput err:
                ReportAndStop(false, err.Message);
                break;
        }
    }

    private void ReportAndStop(bool success, string? errorMessage)
    {
        if (_completed)
            return;

        _completed = true;
        if (!success)
        {
            _log.Warning(
                "Webhook execution failed route={Route} sessionId={SessionId} error={Error}",
                _invocation.Route.Name,
                _invocation.SessionId.Value,
                errorMessage);
        }

        Context.Stop(Self);
    }

    private void TrackNotificationResult(ToolResultOutput toolResult)
    {
        if (!string.Equals(toolResult.ToolName, "send_slack_message", StringComparison.Ordinal))
            return;

        _notifyAttempted = true;
        var result = toolResult.Result?.Trim() ?? string.Empty;
        if (result.StartsWith("Error", StringComparison.OrdinalIgnoreCase))
        {
            _notifyFailed = true;
            _notifyFailureDetail = result;
            return;
        }

        _notifyFailed = false;
        _notifyFailureDetail = null;
    }

    private string? BuildNotifyFailureMessage()
    {
        if (string.IsNullOrWhiteSpace(_invocation.Route.BuildDefaultNotifyInstructions())
            && string.IsNullOrWhiteSpace(_invocation.Route.Config.NotifyInstructions))
        {
            return null;
        }

        if (!_notifyAttempted)
        {
            if (_invocation.Route.Config.NotifyPolicy == NotificationPolicy.Conditional)
                return null;

            return "Notification instructions were provided but no notification tool was invoked.";
        }

        if (_notifyFailed)
            return _notifyFailureDetail ?? "Notification tool returned an unspecified error.";

        return null;
    }

    protected override void PostStop()
    {
        try
        {
            _session?.DisposeAsync().AsTask().GetAwaiter().GetResult();
            _materializer?.Dispose();
        }
        catch (Exception ex)
        {
            _log.Debug(ex, "Failed to dispose webhook execution resources for route {Route}", _invocation.Route.Name);
        }
    }

    private static PayloadTaint ToPayloadTaint(TrustAudience audience)
        => audience switch
        {
            TrustAudience.Public => PayloadTaint.Public,
            TrustAudience.Team => PayloadTaint.Community,
            TrustAudience.Personal => PayloadTaint.Trusted,
            _ => PayloadTaint.Public
        };

    private sealed record ExecutionStarted;
    private sealed record ExecutionOutput(SessionOutput Output);
}
