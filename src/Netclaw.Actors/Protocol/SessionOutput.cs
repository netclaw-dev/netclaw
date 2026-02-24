namespace Netclaw.Actors.Protocol;

/// <summary>
/// Base type for all session output events delivered to subscribers.
/// Forms a discriminated union — each concrete type represents one kind
/// of output from the LLM session.
///
/// Filtering is controlled by <see cref="OutputFilter"/> flags on subscription:
/// - Lifecycle messages (<see cref="TurnCompleted"/>, <see cref="ErrorOutput"/>,
///   <see cref="SessionTitleOutput"/>) are always delivered.
/// - Content messages require the matching flag in the subscriber's filter.
/// </summary>
public abstract record SessionOutput
{
    public required SessionId SessionId { get; init; }

    public long TimestampMs { get; init; } = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    public DateTimeOffset Timestamp => DateTimeOffset.FromUnixTimeMilliseconds(TimestampMs);
}

/// <summary>
/// User-facing text reply from the assistant.
/// Requires <see cref="OutputFilter.Text"/>.
/// </summary>
public sealed record TextOutput : SessionOutput
{
    public required string Text { get; init; }
}

/// <summary>
/// Incremental text delta from the assistant while a turn is streaming.
/// Requires <see cref="OutputFilter.Text"/>.
/// </summary>
public sealed record TextDeltaOutput : SessionOutput
{
    public required string Delta { get; init; }
}

/// <summary>
/// Thinking/reasoning tokens from the model (e.g., Claude extended thinking).
/// Requires <see cref="OutputFilter.Thinking"/>.
/// </summary>
public sealed record ThinkingOutput : SessionOutput
{
    public required string Text { get; init; }
}

/// <summary>
/// Incremental thinking/reasoning delta while a turn is streaming.
/// Requires <see cref="OutputFilter.Thinking"/>.
/// </summary>
public sealed record ThinkingDeltaOutput : SessionOutput
{
    public required string Delta { get; init; }
}

/// <summary>
/// The model has requested a tool/function call.
/// Requires <see cref="OutputFilter.ToolCalls"/>.
/// </summary>
public sealed record ToolCallOutput : SessionOutput
{
    public required string CallId { get; init; }

    public required string ToolName { get; init; }

    /// <summary>
    /// Tool arguments as a JSON string. Kept opaque at the protocol level —
    /// tool executors parse based on their schema.
    /// </summary>
    public string? ArgumentsJson { get; init; }
}

/// <summary>
/// Result of a tool execution, fed back into the conversation.
/// Requires <see cref="OutputFilter.ToolCalls"/>.
/// </summary>
public sealed record ToolResultOutput : SessionOutput
{
    public required string CallId { get; init; }

    public required string ToolName { get; init; }

    public required string Result { get; init; }
}

/// <summary>
/// Token usage report for the completed turn.
/// Requires <see cref="OutputFilter.Usage"/>.
/// Includes context window metadata so subscribers can display usage
/// percentage without duplicating session config.
/// </summary>
public sealed record UsageOutput : SessionOutput
{
    public long? InputTokens { get; init; }

    public long? OutputTokens { get; init; }

    public long? TotalTokens { get; init; }

    public long? CachedInputTokens { get; init; }

    public long? ReasoningTokens { get; init; }

    /// <summary>
    /// Total context window capacity from <see cref="Sessions.SessionConfig.ContextWindowTokens"/>.
    /// </summary>
    public int ContextWindowTokens { get; init; }

    /// <summary>
    /// Percentage of context window consumed (0.0–1.0), computed as
    /// <c>InputTokens / ContextWindowTokens</c>. Null when input tokens
    /// are unavailable.
    /// </summary>
    public double? UsagePercent { get; init; }
}

/// <summary>
/// Signals that a turn has completed (all content delivered).
/// Lifecycle — always delivered regardless of <see cref="OutputFilter"/>.
/// </summary>
public sealed record TurnCompleted : SessionOutput
{
    public required int TurnNumber { get; init; }
}

/// <summary>
/// Session title was generated or updated by the LLM.
/// Lifecycle — always delivered regardless of <see cref="OutputFilter"/>.
/// </summary>
public sealed record SessionTitleOutput : SessionOutput
{
    public required string Title { get; init; }
}

/// <summary>
/// An error occurred during LLM processing.
/// Lifecycle — always delivered regardless of <see cref="OutputFilter"/>.
/// </summary>
public sealed record ErrorOutput : SessionOutput
{
    public required string Message { get; init; }

    /// <summary>
    /// The underlying exception, if available. Not user-facing — intended
    /// for diagnostic logging by subscribers and adapters.
    /// </summary>
    public Exception? Cause { get; init; }
}

/// <summary>
/// Session context was compacted to stay within the context window.
/// Lifecycle — always delivered regardless of <see cref="OutputFilter"/>.
/// </summary>
public sealed record CompactionOutput : SessionOutput
{
    /// <summary>Number of messages before compaction.</summary>
    public required int MessagesBefore { get; init; }

    /// <summary>Number of messages after compaction.</summary>
    public required int MessagesAfter { get; init; }

    /// <summary>Whether tool results were cleared (Phase 1).</summary>
    public bool ToolResultsCleared { get; init; }

    /// <summary>Whether summarization was applied (Phase 2).</summary>
    public bool Summarized { get; init; }
}
