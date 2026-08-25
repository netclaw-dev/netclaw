// -----------------------------------------------------------------------
// <copyright file="SessionTuning.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
namespace Netclaw.Configuration;

/// <summary>
/// Internal tuning constants for session behavior. These are bindable from the
/// <c>Session.Tuning</c> config section for development and testing, but are not
/// part of the documented operator-facing configuration surface. Production
/// defaults are chosen to work well with most models and workloads.
/// </summary>
public sealed record SessionTuning
{
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
    /// Number of recent tool call/result pairs to keep in full detail
    /// during tool result clearing (Phase 1 of compaction).
    /// Older tool results are replaced with placeholders.
    /// </summary>
    public int KeepRecentToolResults { get; init; } = 3;

    /// <summary>
    /// The session <i>content</i> inline budget: the default maximum characters of
    /// a tool result inlined into conversation history, for tools whose output the
    /// model needs to read in full (file_read, web_fetch, memory recall, MCP).
    /// Oversized results are truncated to a head+tail window by the dispatcher and
    /// the full (redacted) output is spilled to a session file with a steer to read
    /// a slice / grep. Verbose tools (shell) override this with a much smaller
    /// per-tool budget (<see cref="Netclaw.Tools.INetclawTool.InlineOutputBudgetChars"/>).
    /// </summary>
    public int MaxInlineToolResultChars { get; init; } = 12_000;

    /// <summary>
    /// Number of future user turns that dynamically loaded deferred first-party
    /// or MCP tools remain available without another load. Set to 0 to require
    /// activation again on every user turn.
    /// </summary>
    public int DiscoveredToolRetentionTurns { get; init; } = 3;

    /// <summary>
    /// Maximum number of loaded deferred first-party or MCP tools retained
    /// across turns. Oldest loaded tools are evicted first when the cap is
    /// exceeded.
    /// </summary>
    public int DiscoveredToolMaxCount { get; init; } = 12;

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
    /// Enables deterministic retrieval request planning for automatic
    /// memory recall on each turn. Scheduled for removal — always true in production.
    /// </summary>
    public bool DeterministicRetrievalEnabled { get; init; } = true;

    /// <summary>
    /// Optional override for the minimum composite score a memory must reach
    /// before it is injected into a turn via automatic recall. Candidates
    /// below this floor are dropped silently, which lets automatic recall
    /// return zero items when nothing in the memory store is a strong enough
    /// match for the current query.
    ///
    /// When null (the default), the coordinator uses its own baked-in floor.
    /// This property is a power-user knob: lower values let weaker matches
    /// through (increasing the risk of unrelated-memory pollution), higher
    /// values are stricter (at the cost of erasing legitimate marginal
    /// matches). Set to 0 to effectively disable the floor.
    /// </summary>
    public double? MinimumRecallCompositeScore { get; init; }

    /// <summary>
    /// Character budget for memory content injected by automatic recall in a
    /// single turn. Items are admitted in rank order until the next item's
    /// content would exceed the budget; whole items are dropped, never
    /// truncated (a truncated memory is worse than an absent one — it reads
    /// as complete while missing its distinguishing detail). The July 2026
    /// audit measured a mean 3-item recall at ~1,400 chars (~349 tokens), so
    /// the default only clips outlier turns dominated by oversized memories.
    /// Set to 0 to disable the budget.
    /// </summary>
    public int MaxRecallInjectedChars { get; init; } = 2_000;

    /// <summary>
    /// Number of completed turns between memory distillation triggers.
    /// When set, the observer distills every N turns regardless of idle state,
    /// ensuring memories form during long active sessions (e.g., 25-turn tool loops).
    /// The idle timeout (<see cref="SessionConfig.MemoryObserverIdleSeconds"/>) remains
    /// active as a fallback for partial-turn content. Set to 0 to disable.
    /// </summary>
    public int MemoryDistillationTurnInterval { get; init; } = 5;

    /// <summary>
    /// Maximum characters allowed in an MCP tool description before truncation.
    /// Oversized descriptions (e.g., Notion tools at ~10K chars each) are truncated
    /// at registration time to protect the context window. Default matches Claude Code's
    /// documented 2KB cap (see https://code.claude.com/docs/en/mcp — "Scale with MCP
    /// Tool Search" section). Set to 0 to disable truncation.
    /// </summary>
    public int MaxToolDescriptionChars { get; init; } = 2048;

    /// <summary>
    /// Character threshold at which an MCP tool's JSON schema triggers a warning log.
    /// Schemas cannot be safely truncated (would break invocation), so this is
    /// observability only — alerts operators that an MCP server is shipping bloated
    /// tool definitions. Set to 0 to disable warnings.
    /// </summary>
    public int MaxToolSchemaWarnChars { get; init; } = 8000;

    /// <summary>
    /// Retry policy for transient streaming LLM failures (5xx, 429).
    /// Only applies when no data has been streamed yet — mid-stream failures
    /// are not retried because the partial response cannot be reconstructed.
    /// </summary>
    public RetryPolicy StreamingRetryPolicy { get; init; } = new();
}
