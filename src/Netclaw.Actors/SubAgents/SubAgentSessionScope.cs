// -----------------------------------------------------------------------
// <copyright file="SubAgentSessionScope.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
namespace Netclaw.Actors.SubAgents;

/// <summary>
/// Derives the owning (parent) session id from a sub-agent's composite scope id.
/// Sub-agents run as ephemeral children of a parent session and reuse its
/// <c>session.log</c>; their composite ids (<c>{parentId}/subagent/{name}/{runId}</c>)
/// must collapse back to the parent so their diagnostics route to the parent's log
/// rather than scatter into per-agent files operators do not monitor. This is the
/// only place that decision is made — keep it in sync with how sub-agent ids are
/// constructed in the spawner.
/// </summary>
internal static class SubAgentSessionScope
{
    public static string? NormalizeSessionId(string? sessionId)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
            return null;

        var value = sessionId.Trim();
        var subAgentMarker = value.IndexOf("/subagent/", StringComparison.Ordinal);
        if (subAgentMarker > 0)
            value = value[..subAgentMarker];

        return value;
    }
}
