using Akka.Actor;
using Microsoft.Extensions.AI;
using Netclaw.Tools;

namespace Netclaw.Actors.Sessions;

/// <summary>
/// Internal message sent back to the session actor when the async LLM call completes.
/// </summary>
internal sealed record LlmResponseReceived
{
    public required ChatResponse Response { get; init; }

    public bool StreamedText { get; init; }

    public bool StreamedThinking { get; init; }

    public AutomaticRecallResult? RecallResult { get; init; }
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
    public List<FileAttachmentInfo> FileAttachments { get; init; } = [];
    public List<CompletedSubAgentRun> CompletedSubAgentRuns { get; init; } = [];
    public List<AcceptedSubAgentFinding> AcceptedSubAgentFindings { get; init; } = [];
}

internal sealed record CompletedSubAgentRun
{
    public required string RunId { get; init; }
    public required string AgentName { get; init; }
    public required bool Success { get; init; }
    public required TimeSpan Duration { get; init; }
    public int FindingsCount { get; init; }
    public string? MemoryDecision { get; init; }
    public string? MemoryDecisionReason { get; init; }
}

internal sealed record AcceptedSubAgentFinding
{
    public required string RunId { get; init; }
    public required string AgentName { get; init; }
    public required TimeSpan Duration { get; init; }
    public required string Shape { get; init; }
    public required string Title { get; init; }
    public required string Content { get; init; }
    public required string Kind { get; init; }
    public required string Domain { get; init; }
    public required string Sensitivity { get; init; }
    public required string RecallMode { get; init; }
    public required string UpdateSemantics { get; init; }
    public required double Confidence { get; init; }
    public required string Durability { get; init; }
    public required string Reusability { get; init; }
    public IReadOnlyList<string> Evidence { get; init; } = [];
    public long? FreshnessAtMs { get; init; }
    public required string Decision { get; init; }
    public string? DecisionReason { get; init; }
}

/// <summary>
/// Internal message sent back when tool execution fails.
/// </summary>
internal sealed record ToolExecutionFailed
{
    public required Exception Cause { get; init; }
}

/// <summary>
/// Internal watchdog timeout used to force stuck Processing operations to fail
/// and return the session actor to Ready state.
/// </summary>
internal sealed record ProcessingWatchdogExpired
{
    public required long OperationId { get; init; }

    public required string OperationName { get; init; }
}

/// <summary>
/// Marshal a child actor creation request back onto the session actor thread.
/// This keeps <c>Context.ActorOf</c> usage on the actor mailbox thread.
/// </summary>
internal sealed record SpawnChildActorRequest
{
    public required Props Props { get; init; }
    public required string ActorName { get; init; }
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
/// Internal message when a compaction step (extraction or summarization) fails.
/// </summary>
internal sealed record CompactionFailed
{
    public required Exception Cause { get; init; }
}

/// <summary>
/// Internal message completing a sidecar title generation LLM call.
/// </summary>
internal sealed record TitleGenerationCompleted
{
    public required string Title { get; init; }
}

internal sealed record MemoryObservationFailed
{
    public required string Reason { get; init; }
}

internal sealed record RecallPlanningFailed
{
    public required string Reason { get; init; }
}
