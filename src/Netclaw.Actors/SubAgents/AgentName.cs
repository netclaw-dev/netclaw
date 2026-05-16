// -----------------------------------------------------------------------
// <copyright file="AgentName.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
namespace Netclaw.Actors.SubAgents;

/// <summary>
/// Strongly-typed sub-agent name — the identity of a sub-agent definition used
/// for spawn routing, notifications, and finding attribution. Wraps the raw
/// name string so an agent name cannot be confused with any other string at a
/// call boundary.
/// </summary>
public readonly record struct AgentName(string Value)
{
    public static explicit operator AgentName(string value) => new(value);

    public override string ToString() => Value;
}
