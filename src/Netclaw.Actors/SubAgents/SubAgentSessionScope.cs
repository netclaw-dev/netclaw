// -----------------------------------------------------------------------
// <copyright file="SubAgentSessionScope.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
namespace Netclaw.Actors.SubAgents;

/// <summary>
/// Derives the owning (parent) session id from a sub-agent's composite scope id
/// (<c>{parentId}/subagent/{name}/{runId}</c>) by stripping the <c>/subagent/…</c> suffix.
/// Sub-agent log lines are tagged with this parent id as their <c>SessionId</c> so OTEL/Seq
/// groups a sub-agent's activity under the session that spawned it; the full composite id is
/// carried separately as <c>SubSessionId</c> and is what partitions the local
/// <c>session.log</c> into the sub-agent's OWN file. Keep this in sync with how sub-agent ids
/// are constructed in the spawner. <c>ToolApprovalActor</c> also calls this to walk a
/// sub-agent's approvals up to its parent, so this is the single owner of the <c>/subagent/</c>
/// split.
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
