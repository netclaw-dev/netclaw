namespace Netclaw.Configuration;

/// <summary>
/// Configuration for an LLM session. Carries model identity and context
/// window size so sessions can make compaction decisions.
/// </summary>
public sealed record SessionConfig
{
    /// <summary>
    /// The model identifier (e.g., "qwen3:30b", "claude-sonnet-4-20250514").
    /// Populated from <see cref="ModelSelection"/> at startup, not from the Session config section.
    /// </summary>
    public string ModelId { get; init; } = string.Empty;

    /// <summary>
    /// Maximum context window size in tokens for the configured model.
    /// Used to determine when compaction should trigger.
    /// Populated from <see cref="ModelSelection"/> at startup, not from the Session config section.
    /// </summary>
    public int ContextWindowTokens { get; init; } = 32_768;

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
    /// Number of recent non-system messages to preserve verbatim after compaction
    /// summarization. These are appended after the summary message so the assistant
    /// has immediate conversational context. Counts raw messages (not turn pairs)
    /// to handle tool-call-heavy turns correctly.
    /// Default 6 — roughly covers 2 turns with tool calls.
    /// </summary>
    public int KeepRecentMessages { get; init; } = 6;

    /// <summary>
    /// Turn interval for sidecar title generation. Title is always generated
    /// on turn 1, then refreshed every N turns. Set to 0 to disable.
    /// </summary>
    public int TitleGenerationInterval { get; init; } = 10;

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
