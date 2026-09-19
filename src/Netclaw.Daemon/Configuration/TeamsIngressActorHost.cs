// -----------------------------------------------------------------------
// <copyright file="TeamsIngressActorHost.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Akka.Actor;
using Akka.Hosting;
using Netclaw.Actors.Channels;
using Netclaw.Actors.Hosting;
using Netclaw.Channels.Teams;
using Netclaw.Channels.Telemetry;

namespace Netclaw.Daemon.Configuration;

/// <summary>
/// Deliberately terminates at the PR 2 actor boundary. This makes accepted
/// ingress observable without creating a hidden path to a session or LLM.
/// </summary>
internal sealed class DeferredTeamsConversationIngressSink : ITeamsConversationIngressSink
{
    public ValueTask<TeamsIngressSinkResult> RouteAsync(TeamsInboundActivity activity, CancellationToken cancellationToken)
    {
        ChannelTelemetry.For(ChannelType.Teams).RecordEventDropped("conversation_routing_not_implemented");
        return ValueTask.FromResult(TeamsIngressSinkResult.Unavailable);
    }

    public ValueTask<TeamsApprovalActionResult> RouteApprovalAsync(TeamsApprovalAction action, CancellationToken cancellationToken)
    {
        ChannelTelemetry.For(ChannelType.Teams).RecordEventDropped("approval_routing_not_implemented");
        return ValueTask.FromResult(new TeamsApprovalActionResult(TeamsApprovalActionDisposition.Unavailable));
    }
}

internal sealed class TeamsIngressActorHost(IServiceProvider serviceProvider) : IHostedService
{
    private IActorRef? _actor;
    private IActorRef? _reminderGateway;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        var actorSystem = serviceProvider.GetRequiredService<ActorSystem>();
        var conversationSink = serviceProvider.GetRequiredService<ITeamsConversationIngressSink>();
        var timeProvider = serviceProvider.GetRequiredService<TimeProvider>();
        _actor = actorSystem.ActorOf(
            Props.Create(() => new TeamsIngressActor(conversationSink, timeProvider)),
            "teams-ingress");
        var reminderGateway = actorSystem.ActorOf(
            Props.Create(() => new TeamsReminderGatewayActor(conversationSink)),
            "teams-reminder-gateway");
        _reminderGateway = reminderGateway;
        ActorRegistry.For(actorSystem).Register<TeamsGatewayActorKey>(reminderGateway);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _actor?.Tell(PoisonPill.Instance);
        _actor = null;
        _reminderGateway?.Tell(PoisonPill.Instance);
        _reminderGateway = null;
        return Task.CompletedTask;
    }

    public async ValueTask<TeamsIngressRouteResult> SubmitAsync(TeamsInboundActivity activity, CancellationToken cancellationToken)
    {
        var actor = _actor;
        if (actor is null || cancellationToken.IsCancellationRequested)
            return new TeamsIngressRouteResult(cancellationToken.IsCancellationRequested
                ? TeamsIngressRouteDisposition.Cancelled
                : TeamsIngressRouteDisposition.Unavailable);

        try
        {
            return await actor.Ask<TeamsIngressRouteResult>(
                new TeamsIngressReceived(activity, cancellationToken),
                TeamsIngressTimeouts.IngressRoute(activity),
                cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return new TeamsIngressRouteResult(TeamsIngressRouteDisposition.Cancelled);
        }
        catch (AskTimeoutException)
        {
            return new TeamsIngressRouteResult(TeamsIngressRouteDisposition.Unavailable);
        }
    }

    public async ValueTask<TeamsApprovalActionResult> SubmitApprovalAsync(
        TeamsApprovalAction action,
        CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
            return new TeamsApprovalActionResult(TeamsApprovalActionDisposition.Cancelled);

        var sink = serviceProvider.GetRequiredService<ITeamsConversationIngressSink>();
        try
        {
            return await sink.RouteApprovalAsync(action, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return new TeamsApprovalActionResult(TeamsApprovalActionDisposition.Cancelled);
        }
        catch (Exception)
        {
            return new TeamsApprovalActionResult(TeamsApprovalActionDisposition.Failed);
        }
    }
}
