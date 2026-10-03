// -----------------------------------------------------------------------
// <copyright file="SubAgentNotification.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
namespace Netclaw.Actors.SubAgents;

/// <summary>
/// Phase of a subagent's lifecycle within a tool call.
/// </summary>
public enum SubAgentPhase
{
    Started = 0,
    Completed = 1,
    Activity = 2
}

/// <summary>
/// Notification emitted by tools that spawn subagents, relayed by the session
/// as <see cref="SessionProtocol.SubAgentOutput"/> events to subscribers.
/// </summary>
public sealed record SubAgentNotification
{
    public required AgentName AgentName { get; init; }
    public required SubAgentPhase Phase { get; init; }

    /// <summary>Number of tools available to the subagent. Set on <see cref="SubAgentPhase.Started"/>.</summary>
    public int ToolCount { get; init; }

    /// <summary>Whether the subagent completed successfully. Set on <see cref="SubAgentPhase.Completed"/>.</summary>
    public bool Success { get; init; }

    /// <summary>Wall-clock duration of the subagent execution. Set on <see cref="SubAgentPhase.Completed"/>.</summary>
    public TimeSpan Duration { get; init; }
}
