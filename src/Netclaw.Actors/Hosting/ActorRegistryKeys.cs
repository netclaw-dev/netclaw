// -----------------------------------------------------------------------
// <copyright file="ActorRegistryKeys.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
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

/// <summary>
/// Marker type for <see cref="Akka.Hosting.IActorRegistry"/> lookup of the
/// background job manager singleton actor.
/// </summary>
public sealed class BackgroundJobManagerActorKey;

/// <summary>
/// Marker type for <see cref="Akka.Hosting.IActorRegistry"/> lookup of the
/// session log dispatcher. The dispatcher owns one <c>SessionLogActor</c>
/// child per sanitized session id and is the single writer per session log
/// file. MEL diagnostic providers and <c>LlmSessionActor</c> route audit
/// and diagnostic lines through it.
/// </summary>
public sealed class SessionLogDispatcherActorKey;

/// <summary>
/// Marker type for <see cref="Akka.Hosting.IActorRegistry"/> lookup of the
/// Discord gateway parent actor (DiscordGatewayActor -> DiscordSessionBindingActor).
/// Resolved by the reminder dispatcher to deliver Mode B reminder turns through
/// the Discord channel's existing routing hierarchy.
/// </summary>
public sealed class DiscordGatewayActorKey;

/// <summary>
/// Marker type for <see cref="Akka.Hosting.IActorRegistry"/> lookup of the
/// Mattermost gateway parent actor (MattermostGatewayActor → MattermostConversationActor →
/// MattermostSessionBindingActor). Resolved by the reminder dispatcher to deliver
/// Mode B reminder turns through the Mattermost channel's existing routing hierarchy.
/// </summary>
public sealed class MattermostGatewayActorKey;
