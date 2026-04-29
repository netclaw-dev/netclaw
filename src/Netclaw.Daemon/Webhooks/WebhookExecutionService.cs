// -----------------------------------------------------------------------
// <copyright file="WebhookExecutionService.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Akka.Actor;
using Microsoft.Extensions.Logging;
using Netclaw.Actors.Channels;
using Netclaw.Configuration;

namespace Netclaw.Daemon.Webhooks;

public interface IWebhookExecutionService
{
    void StartInvocation(WebhookInvocation invocation);
}

public sealed class WebhookExecutionService : IWebhookExecutionService
{
    private readonly ActorSystem _actorSystem;
    private readonly ISessionPipeline _pipeline;
    private readonly WebhooksConfig _config;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<WebhookExecutionService> _logger;

    public WebhookExecutionService(
        ActorSystem actorSystem,
        ISessionPipeline pipeline,
        WebhooksConfig config,
        TimeProvider timeProvider,
        ILogger<WebhookExecutionService> logger)
    {
        _actorSystem = actorSystem;
        _pipeline = pipeline;
        _config = config;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    public void StartInvocation(WebhookInvocation invocation)
    {
        var actorName = $"webhook-{Sanitize(invocation.Route.Name)}-{_timeProvider.GetUtcNow().ToUnixTimeMilliseconds()}-{Guid.NewGuid():N}";
        _actorSystem.ActorOf(
            WebhookExecutionActor.CreateProps(invocation, _pipeline, _config, _timeProvider),
            actorName);

        _logger.LogInformation(
            "Started webhook execution route={Route} sessionId={SessionId} actor={Actor}",
            invocation.Route.Name,
            invocation.SessionId.Value,
            actorName);
    }

    private static string Sanitize(string value)
        => new string([.. value.Select(ch => char.IsLetterOrDigit(ch) ? ch : '-')]).Trim('-');
}
