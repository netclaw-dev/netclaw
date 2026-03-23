using Microsoft.Extensions.AI;
using Netclaw.Tools;

namespace Netclaw.Actors.SubAgents;

/// <summary>
/// Defines a subagent's identity: system prompt, tools, and model.
/// Can be constructed from built-in definitions or authored dynamically.
/// </summary>
public sealed record SubAgentDefinition
{
    /// <summary>Human-readable name for logging and observability.</summary>
    public required string Name { get; init; }

    /// <summary>System prompt for the subagent's LLM context.</summary>
    public required string SystemPrompt { get; init; }

    /// <summary>Tools available to the subagent during execution.</summary>
    public required IReadOnlyList<INetclawTool> Tools { get; init; }

    /// <summary>
    /// Model role to resolve via <see cref="Configuration.IChatClientProvider"/>.
    /// Defaults to <see cref="Configuration.ModelRole.Compaction"/> (cheaper/faster model).
    /// </summary>
    public Configuration.ModelRole ModelRole { get; init; } = Configuration.ModelRole.Compaction;

    /// <summary>
    /// Whether successful free-form output should be converted into structured findings.
    /// </summary>
    public bool EmitStructuredFindings { get; init; }
}

/// <summary>
/// Message sent to a <see cref="SubAgentActor"/> to begin execution.
/// </summary>
public sealed record RunSubAgent
{
    /// <summary>The task for the subagent to perform (becomes the user message).</summary>
    public required string Task { get; init; }

    /// <summary>Wall-clock timeout set by the caller.</summary>
    public required TimeSpan Timeout { get; init; }

    /// <summary>
    /// Cancellation token from the calling tool execution. Used to stop the
    /// subagent promptly when the parent turn is cancelled or times out.
    /// </summary>
    public CancellationToken Cancellation { get; init; }

    /// <summary>
    /// Optional execution scope ID used for session-scoped tool routing.
    /// When omitted, the subagent runtime assigns a unique transient scope.
    /// </summary>
    public string? SessionScopeId { get; init; }

    public string? Audience { get; init; }

    public string? Boundary { get; init; }

    public string? ChannelType { get; init; }
}

/// <summary>
/// Result returned by a <see cref="SubAgentActor"/> when execution completes.
/// </summary>
public sealed record SubAgentResult
{
    /// <summary>Whether the subagent completed successfully.</summary>
    public required bool Success { get; init; }

    /// <summary>The subagent's final text output (or error message on failure).</summary>
    public required string Output { get; init; }

    /// <summary>Name of the subagent that produced this result.</summary>
    public required string AgentName { get; init; }

    /// <summary>
    /// Structured findings returned to the owning session for policy and checkpoint review.
    /// </summary>
    public List<SubAgentFinding> Findings { get; init; } = [];

    /// <summary>
    /// Total number of structured findings returned before parent-session review.
    /// </summary>
    public int FindingsCount { get; init; }
}

