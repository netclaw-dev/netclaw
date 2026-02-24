using Microsoft.Extensions.AI;

namespace Netclaw.Actors.Sessions;

/// <summary>
/// Internal message sent back to the session actor when the async LLM call completes.
/// </summary>
internal sealed record LlmResponseReceived
{
    public required ChatResponse Response { get; init; }

    public bool StreamedText { get; init; }

    public bool StreamedThinking { get; init; }
}

/// <summary>
/// Incremental streaming delta emitted while an LLM response is in-flight.
/// </summary>
internal sealed record LlmResponseDeltaReceived
{
    public required AIContent Content { get; init; }
}

/// <summary>
/// Internal message sent back to the session actor when the async LLM call fails.
/// </summary>
internal sealed record LlmCallFailed
{
    public required Exception Cause { get; init; }
}

/// <summary>
/// Internal message sent back to the session actor when tool execution completes.
/// Contains the tool results to feed back into the next LLM call.
/// </summary>
internal sealed record ToolExecutionCompleted
{
    public required List<Protocol.SerializableChatMessage> ToolResults { get; init; }
}

/// <summary>
/// Internal message sent back when tool execution fails.
/// </summary>
internal sealed record ToolExecutionFailed
{
    public required Exception Cause { get; init; }
}

/// <summary>
/// Internal trigger to begin the compaction sequence.
/// Sent to self after a turn completes when usage exceeds the threshold.
/// </summary>
internal sealed record CompactionTriggered
{
    public required long InputTokenCount { get; init; }
}

/// <summary>
/// Internal message completing a memory extraction LLM call.
/// Contains extracted memories that should be persisted externally.
/// </summary>
internal sealed record MemoryExtractionCompleted
{
    public required string ExtractedMemories { get; init; }
}

/// <summary>
/// Internal message completing the summarization LLM call.
/// Contains the structured summary to persist in <see cref="Protocol.SessionCompacted"/>.
/// </summary>
internal sealed record SummarizationCompleted
{
    public required string Summary { get; init; }
}

/// <summary>
/// Internal message when a compaction step (extraction or summarization) fails.
/// </summary>
internal sealed record CompactionFailed
{
    public required Exception Cause { get; init; }
}
