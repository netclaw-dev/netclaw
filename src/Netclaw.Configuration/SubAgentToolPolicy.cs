// -----------------------------------------------------------------------
// <copyright file="SubAgentToolPolicy.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
namespace Netclaw.Configuration;

/// <summary>
/// Central policy for tools exposed to sub-agents. Sub-agents inherit the parent
/// session's audience, boundary, approval, and shell policies. This static deny
/// list prevents recursive delegation and tools whose results cannot cross the
/// child-to-parent completion boundary safely.
/// </summary>
public static class SubAgentToolPolicy
{
    private static readonly HashSet<string> DeniedSubAgentToolNames = new(StringComparer.Ordinal)
    {
        "spawn_agent",
        ToolAudienceProfileToolCatalog.AttachFile
    };

    /// <summary>True unless the tool is statically denied to sub-agents.</summary>
    public static bool IsAllowedForSubAgent(string toolName)
        => !DeniedSubAgentToolNames.Contains(toolName);

    /// <summary>The tools statically denied to sub-agents, sorted.</summary>
    public static IReadOnlyList<string> GetDeniedSubAgentTools()
        => DeniedSubAgentToolNames.OrderBy(x => x, StringComparer.Ordinal).ToArray();
}
