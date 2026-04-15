namespace Netclaw.Actors.Hosting;

/// <summary>
/// Marker type for ActorRegistry lookup of the session manager
/// (GenericChildPerEntityParent routing to LlmSessionActors).
/// </summary>
public sealed class SessionManagerActorKey;

/// <summary>
/// Marker type for ActorRegistry lookup of the model capability cache actor.
/// </summary>
public sealed class ModelCapabilityActorKey;

/// <summary>
/// Marker type for ActorRegistry lookup of the reminder manager actor.
/// </summary>
public sealed class ReminderManagerActorKey;

/// <summary>
/// Marker type for ActorRegistry lookup of the daily stats buffering actor.
/// </summary>
public sealed class DailyStatsActorKey;

/// <summary>
/// Marker type for ActorRegistry lookup of the tool approval actor.
/// </summary>
public sealed class ToolApprovalActorKey;

/// <summary>
/// Marker type for <see cref="Akka.Hosting.IActorRegistry"/> lookup of the
/// SignalR gateway parent actor (GenericChildPerEntityParent routing to
/// SignalRSessionActors). Resolved by the reminder dispatcher to deliver
/// Mode B reminder turns back through the SignalR channel's existing
/// routing hierarchy.
/// </summary>
public sealed class SignalRGatewayActorKey;

/// <summary>
/// Marker type for <see cref="Akka.Hosting.IActorRegistry"/> lookup of the
/// Slack gateway parent actor (SlackGatewayActor → SlackConversationActor →
/// SlackThreadBindingActor). Resolved by the reminder dispatcher to deliver
/// Mode B reminder turns back through the Slack channel's existing
/// routing hierarchy.
/// </summary>
public sealed class SlackGatewayActorKey;
