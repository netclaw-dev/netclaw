using Akka.Actor;
using Akka.Event;
using Microsoft.Extensions.AI;
using Netclaw.Actors.Channels;
using Netclaw.Actors.Protocol;
using Netclaw.Configuration;
using Netclaw.Tools;

namespace Netclaw.Daemon.Webhooks;

internal sealed class WebhookExecutionActor : ReceiveActor
{
    private readonly WebhookInvocation _invocation;
    private readonly WebhooksConfig _config;
    private readonly TimeProvider _timeProvider;
    private readonly ILoggingAdapter _log;
    private readonly DateTimeOffset _dispatchedAt;

    private readonly SessionPipelineHandle _handle;
    private static readonly ToolName NotificationTool = new("send_slack_message");
    private readonly ExecutionOutputAccumulator _accumulator = new(NotificationTool);
    private bool _completed;

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
        _config = config;
        _timeProvider = timeProvider;
        _dispatchedAt = timeProvider.GetUtcNow();
        _log = Context.GetLogger();
        _handle = new SessionPipelineHandle(pipeline, _log, "webhook-exec");

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
            var self = Self;
            var inputQueue = await _handle.InitializeWithQueueAsync(
                Context,
                _invocation.SessionId,
                new SessionPipelineOptions
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
                },
                output => self.Tell(new ExecutionOutput(output)));

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
        var action = _accumulator.ProcessOutput(wrapper.Output);
        switch (action)
        {
            case OutputAction.TurnCompleted:
            {
                var hasNotify = !string.IsNullOrWhiteSpace(_invocation.Route.BuildDefaultNotifyInstructions())
                    || !string.IsNullOrWhiteSpace(_invocation.Route.Config.NotifyInstructions);
                var failureMsg = _accumulator.BuildNotifyFailureMessage(hasNotify, _invocation.Route.Config.NotifyPolicy);
                ReportAndStop(failureMsg is null, failureMsg);
                break;
            }
            case OutputAction.Error:
                ReportAndStop(false, _accumulator.LastErrorMessage);
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

    protected override void PostStop()
    {
        try
        {
            _handle.Dispose();
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
