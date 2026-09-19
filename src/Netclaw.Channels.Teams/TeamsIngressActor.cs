// -----------------------------------------------------------------------
// <copyright file="TeamsIngressActor.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Akka.Actor;
using Netclaw.Actors.Protocol;
using static Netclaw.Actors.Sessions.SessionProtocol;
using Netclaw.Actors.Channels;
using Netclaw.Channels.Telemetry;

namespace Netclaw.Channels.Teams;

/// <summary>
/// The boundary for canonical Teams conversation-child routing. It deliberately
/// exposes no session-pipeline dependency to the ingress fast path.
/// </summary>
public interface ITeamsConversationIngressSink
{
    ValueTask<TeamsIngressSinkResult> RouteAsync(TeamsInboundActivity activity, CancellationToken cancellationToken);

    ValueTask<TeamsApprovalActionResult> RouteApprovalAsync(TeamsApprovalAction action, CancellationToken cancellationToken)
        => ValueTask.FromResult(new TeamsApprovalActionResult(TeamsApprovalActionDisposition.Unavailable));

    /// <summary>
    /// Resolves the actor that owns a previously established Teams session for
    /// generic reminder re-entry. It does not create a destination or bypass
    /// ingress ACL checks; the binding remains the durable destination owner.
    /// </summary>
    bool TryGetReminderConversation(SessionId sessionId, out IActorRef conversation)
    {
        conversation = ActorRefs.Nobody;
        return false;
    }
}

/// <summary>
/// SDK-free reminder gateway. It preserves the generic gateway forwarding
/// behavior: the session pipeline, not this gateway, sends the final command
/// acknowledgement to the reminder execution actor.
/// </summary>
public sealed class TeamsReminderGatewayActor : ReceiveActor
{
    private readonly ITeamsConversationIngressSink _conversationSink;

    public TeamsReminderGatewayActor(ITeamsConversationIngressSink conversationSink)
    {
        _conversationSink = conversationSink ?? throw new ArgumentNullException(nameof(conversationSink));
        Receive<DeliverTrustedSessionTurn>(message =>
        {
            if (!_conversationSink.TryGetReminderConversation(message.SessionId, out var conversation))
            {
                Sender.Tell(CommandNack.For(message.SessionId, "Teams session is unavailable."));
                return;
            }

            conversation.Forward(new TeamsConversationReminder(message));
        });
    }
}

public enum TeamsApprovalActionDisposition
{
    Accepted,
    Rejected,
    Expired,
    AlreadyProcessed,
    Unavailable,
    Failed,
    Cancelled
}

public sealed record TeamsApprovalActionResult(
    TeamsApprovalActionDisposition Disposition,
    TeamsApprovalCard? TerminalCard = null);

public enum TeamsIngressSinkResult
{
    Accepted,
    Duplicate,
    Ignored,
    Denied,
    Cancelled,
    Failed,
    Unavailable
}

public sealed record TeamsIngressReceived(TeamsInboundActivity Activity, CancellationToken CancellationToken);

public enum TeamsIngressRouteDisposition
{
    Routed,
    Duplicate,
    Ignored,
    Denied,
    Cancelled,
    Unavailable,
    RouteFailed
}

public sealed record TeamsIngressRouteResult(TeamsIngressRouteDisposition Disposition);

/// <summary>
/// Process-local routing and duplicate fast path only. Durable deduplication
/// belongs to the session-binding actor, and conversation lookup belongs to
/// the conversation actor.
/// </summary>
public sealed class TeamsIngressActor : ReceiveActor
{
    internal const int DuplicateCapacity = 1_024;
    internal static readonly TimeSpan DuplicateRetention = TimeSpan.FromMinutes(5);

    private readonly ITeamsConversationIngressSink _conversationSink;
    private readonly TimeProvider _timeProvider;
    private readonly Dictionary<DuplicateKey, DateTimeOffset> _recent = new();
    private readonly Queue<DuplicateKey> _order = new();

    public TeamsIngressActor(ITeamsConversationIngressSink conversationSink, TimeProvider timeProvider)
    {
        _conversationSink = conversationSink ?? throw new ArgumentNullException(nameof(conversationSink));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));

        ReceiveAsync<TeamsIngressReceived>(HandleAsync);
    }

    private async Task HandleAsync(TeamsIngressReceived received)
    {
        if (received.CancellationToken.IsCancellationRequested)
        {
            Sender.Tell(new TeamsIngressRouteResult(TeamsIngressRouteDisposition.Cancelled));
            return;
        }

        var now = _timeProvider.GetUtcNow();
        EvictExpired(now);
        var key = new DuplicateKey(
            received.Activity.Trust.TenantId,
            received.Activity.Trust.ConversationId,
            received.Activity.Trust.ActivityId);

        if (_recent.ContainsKey(key))
        {
            ChannelTelemetry.For(ChannelType.Teams).RecordEventFiltered("fast_path_duplicate");
            Sender.Tell(new TeamsIngressRouteResult(TeamsIngressRouteDisposition.Duplicate));
            return;
        }

        try
        {
            var sinkResult = await _conversationSink.RouteAsync(received.Activity, received.CancellationToken);
            if (sinkResult == TeamsIngressSinkResult.Duplicate)
            {
                ChannelTelemetry.For(ChannelType.Teams).RecordEventFiltered("durable_activity_duplicate");
                Sender.Tell(new TeamsIngressRouteResult(TeamsIngressRouteDisposition.Duplicate));
                return;
            }

            if (sinkResult == TeamsIngressSinkResult.Denied)
            {
                ChannelTelemetry.For(ChannelType.Teams).RecordEventDropped("teams_acl_denied");
                Sender.Tell(new TeamsIngressRouteResult(TeamsIngressRouteDisposition.Denied));
                return;
            }

            if (sinkResult == TeamsIngressSinkResult.Ignored)
            {
                ChannelTelemetry.For(ChannelType.Teams).RecordEventFiltered("channel_unmentioned");
                Sender.Tell(new TeamsIngressRouteResult(TeamsIngressRouteDisposition.Ignored));
                return;
            }

            if (sinkResult == TeamsIngressSinkResult.Cancelled)
            {
                Sender.Tell(new TeamsIngressRouteResult(TeamsIngressRouteDisposition.Cancelled));
                return;
            }

            if (sinkResult == TeamsIngressSinkResult.Failed)
            {
                ChannelTelemetry.For(ChannelType.Teams).RecordEventDropped("conversation_boundary_failed");
                Sender.Tell(new TeamsIngressRouteResult(TeamsIngressRouteDisposition.RouteFailed));
                return;
            }

            if (sinkResult != TeamsIngressSinkResult.Accepted)
            {
                ChannelTelemetry.For(ChannelType.Teams).RecordEventDropped("conversation_boundary_unavailable");
                Sender.Tell(new TeamsIngressRouteResult(TeamsIngressRouteDisposition.Unavailable));
                return;
            }

            // Retain only work accepted by the next boundary. PR 3 owns the
            // durable processed-ID record; this cache must never suppress a retry.
            _recent.Add(key, now);
            _order.Enqueue(key);
            EvictExcess();
            ChannelTelemetry.For(ChannelType.Teams).RecordEventRouted("conversation_boundary");
            Sender.Tell(new TeamsIngressRouteResult(TeamsIngressRouteDisposition.Routed));
        }
        catch (OperationCanceledException) when (received.CancellationToken.IsCancellationRequested)
        {
            Sender.Tell(new TeamsIngressRouteResult(TeamsIngressRouteDisposition.Cancelled));
        }
        catch (Exception)
        {
            ChannelTelemetry.For(ChannelType.Teams).RecordEventDropped("conversation_boundary_failed");
            Sender.Tell(new TeamsIngressRouteResult(TeamsIngressRouteDisposition.RouteFailed));
        }
    }

    private void EvictExpired(DateTimeOffset now)
    {
        while (_order.TryPeek(out var key)
               && _recent.TryGetValue(key, out var seenAt)
               && now - seenAt >= DuplicateRetention)
        {
            _order.Dequeue();
            _recent.Remove(key);
        }
    }

    private void EvictExcess()
    {
        while (_recent.Count > DuplicateCapacity && _order.TryDequeue(out var key))
            _recent.Remove(key);
    }

    private sealed record DuplicateKey(string TenantId, string ConversationId, string ActivityId);
}
