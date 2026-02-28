namespace Netclaw.Configuration;

/// <summary>
/// Configuration for an LLM session. Carries model identity and context
/// window size so sessions can make compaction decisions.
/// </summary>
public sealed record SessionConfig
{
    /// <summary>
    /// The model identifier (e.g., "qwen3:30b", "claude-sonnet-4-20250514").
    /// </summary>
    public required string ModelId { get; init; }

    /// <summary>
    /// Maximum context window size in tokens for the configured model.
    /// Used to determine when compaction should trigger.
    /// </summary>
    public required int ContextWindowTokens { get; init; }

    /// <summary>
    /// Percentage of context window usage (0.0–1.0) at which compaction triggers.
    /// Default 0.75 — compact when 75% of the context window is consumed.
    /// </summary>
    public double CompactionThreshold { get; init; } = 0.75;

    /// <summary>
    /// Number of turns between persistence snapshots.
    /// </summary>
    public int SnapshotInterval { get; init; } = 20;

    /// <summary>
    /// Optional model ID for compaction summarization.
    /// When set, compaction LLM calls use this model (typically cheaper/faster)
    /// instead of the primary session model. Requires a provider that
    /// can resolve an <see cref="Microsoft.Extensions.AI.IChatClient"/> for this model.
    /// </summary>
    public string? CompactionModelId { get; init; }

    /// <summary>
    /// Number of recent tool call/result pairs to keep in full detail
    /// during tool result clearing (Phase 1 of compaction).
    /// Older tool results are replaced with placeholders.
    /// </summary>
    public int KeepRecentToolResults { get; init; } = 3;

    /// <summary>
    /// Maximum number of tool execution iterations allowed per turn.
    /// When the limit is reached, the next LLM call omits tools to force a text response.
    /// Prevents unbounded agentic loops from runaway tool chains.
    /// </summary>
    public int MaxToolIterationsPerTurn { get; init; } = 10;

    /// <summary>
    /// How long a session can be idle before passivating.
    /// The actor saves a snapshot and stops itself; re-creation by
    /// <c>GenericChildPerEntityParent</c> on next message recovers state from journal.
    /// Default 30 minutes. Set to <see cref="TimeSpan.Zero"/> to disable.
    /// </summary>
    public TimeSpan IdleTimeout { get; init; } = TimeSpan.FromMinutes(30);

    /// <summary>
    /// Effective token limit at which compaction fires.
    /// </summary>
    public int CompactionTokenLimit => (int)(ContextWindowTokens * CompactionThreshold);

    /// <summary>
    /// Content types the configured model accepts as input.
    /// Defaults to <see cref="ModelModality.Text"/> when capabilities
    /// have not been resolved.
    /// </summary>
    public ModelModality InputModalities { get; init; } = ModelModality.Text;

    /// <summary>
    /// Content types the configured model can produce as output.
    /// Defaults to <see cref="ModelModality.Text"/> when capabilities
    /// have not been resolved.
    /// </summary>
    public ModelModality OutputModalities { get; init; } = ModelModality.Text;
}
