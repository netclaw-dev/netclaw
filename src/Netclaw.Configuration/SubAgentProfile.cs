// -----------------------------------------------------------------------
// <copyright file="SubAgentProfile.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
namespace Netclaw.Configuration;

/// <summary>
/// Controls whether a subagent is exposed to the frontline LLM via
/// <c>spawn_agent</c> and the discovery context layer.
/// </summary>
public enum SubAgentVisibility
{
    /// <summary>Appears in context layer catalog and <c>spawn_agent</c> tool.</summary>
    UserFacing,

    /// <summary>Platform-owned, hidden from discovery and <c>spawn_agent</c>.</summary>
    Internal
}

/// <summary>
/// Declarative subagent identity — name, system prompt, advisory tool metadata,
/// model role, timeout, and visibility. Runtime tool access is resolved from the
/// parent session's audience policy at spawn time.
/// </summary>
public sealed record SubAgentProfile
{
    /// <summary>Unique name used for lookup and logging.</summary>
    public required string Name { get; init; }

    /// <summary>One-line description shown in the discovery context layer.</summary>
    public required string Description { get; init; }

    /// <summary>System prompt for the subagent's LLM context.</summary>
    public required string SystemPrompt { get; init; }

    /// <summary>
    /// Advisory tool names parsed from frontmatter for compatibility with agent
    /// file formats. These names do not constrain runtime tool access.
    /// </summary>
    public required IReadOnlyList<string> ToolNames { get; init; }

    /// <summary>
    /// Model role resolved via <see cref="IChatClientProvider"/>.
    /// Defaults to <see cref="ModelRole.Compaction"/> (cheaper/faster model).
    /// </summary>
    public ModelRole ModelRole { get; init; } = ModelRole.Compaction;

    /// <summary>Wall-clock timeout in seconds for subagent execution.</summary>
    public int TimeoutSeconds { get; init; } = 60;

    /// <summary>
    /// Whether successful free-form output should be converted into structured
    /// findings for parent-session memory review.
    /// </summary>
    public bool EmitStructuredFindings { get; init; }

    /// <summary>Controls whether this agent is visible to the frontline LLM.</summary>
    public SubAgentVisibility Visibility { get; init; } = SubAgentVisibility.UserFacing;
}
