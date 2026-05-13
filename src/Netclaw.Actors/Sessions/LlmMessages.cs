// -----------------------------------------------------------------------
// <copyright file="LlmMessages.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Akka.Actor;
using Microsoft.Extensions.AI;
using Netclaw.Actors.Protocol;
using Netclaw.Tools;

namespace Netclaw.Actors.Sessions;

/// <summary>
/// Internal message sent back to the session actor when the async LLM call completes.
/// </summary>
internal sealed record LlmResponseReceived : INoSerializationVerificationNeeded
{
    public required ChatResponse Response { get; init; }

    public bool StreamedText { get; init; }

    public bool StreamedThinking { get; init; }

    public AutomaticRecallResult? RecallResult { get; init; }

    /// <summary>
    /// Correlation ID matching <see cref="LlmSessionActor._activeCallId"/>.
    /// Stale responses from cancelled calls are ignored.
    /// </summary>
    public long CallId { get; init; }
}

/// <summary>
/// Incremental streaming delta emitted while an LLM response is in-flight.
/// </summary>
internal sealed record LlmResponseDeltaReceived : INoSerializationVerificationNeeded
{
    public required AIContent Content { get; init; }

    public long CallId { get; init; }
}

/// <summary>
/// Internal message sent back to the session actor when the async LLM call fails.
/// </summary>
internal sealed record LlmCallFailed : INoSerializationVerificationNeeded
{
    public required Exception Cause { get; init; }

    public long CallId { get; init; }
}

/// <summary>
/// Internal message sent back to the session actor when tool execution completes.
/// Contains the tool results to feed back into the next LLM call.
/// </summary>
internal sealed record ToolExecutionCompleted : INoSerializationVerificationNeeded
{
    public required List<Protocol.SerializableChatMessage> ToolResults { get; init; }
    public List<FileAttachmentInfo> FileAttachments { get; init; } = [];
    public List<CompletedSubAgentRun> CompletedSubAgentRuns { get; init; } = [];
    public List<AcceptedSubAgentFinding> AcceptedSubAgentFindings { get; init; } = [];
}

internal sealed record CompletedSubAgentRun : INoSerializationVerificationNeeded
{
    public required string RunId { get; init; }
    public required string AgentName { get; init; }
    public required bool Success { get; init; }
    public required TimeSpan Duration { get; init; }
    public int FindingsCount { get; init; }
    public string? MemoryDecision { get; init; }
    public string? MemoryDecisionReason { get; init; }
}

internal sealed record AcceptedSubAgentFinding : INoSerializationVerificationNeeded
{
    public required string RunId { get; init; }
    public required string AgentName { get; init; }
    public required TimeSpan Duration { get; init; }
    public required string Shape { get; init; }
    public required string Title { get; init; }
    public required string Content { get; init; }
    public required string Kind { get; init; }
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
internal sealed record ToolExecutionFailed : INoSerializationVerificationNeeded
{
    public required Exception Cause { get; init; }
}

/// <summary>
/// Internal watchdog timeout used to force stuck Processing operations to fail
/// and return the session actor to Ready state.
/// </summary>
internal sealed record ProcessingWatchdogExpired : INoSerializationVerificationNeeded
{
    public required long OperationId { get; init; }

    public required string OperationName { get; init; }
}

/// <summary>
/// Marshal a child actor creation request back onto the session actor thread.
/// This keeps <c>Context.ActorOf</c> usage on the actor mailbox thread.
/// </summary>
internal sealed record SpawnChildActorRequest : INoSerializationVerificationNeeded
{
    public required Props Props { get; init; }
    public required string ActorName { get; init; }
}

/// <summary>
/// Internal trigger to begin the compaction sequence.
/// Sent to self after a turn completes when usage exceeds the threshold.
/// </summary>
internal sealed record CompactionTriggered : INoSerializationVerificationNeeded
{
    public required long InputTokenCount { get; init; }
}

internal sealed record CompactionWorkCompleted : INoSerializationVerificationNeeded
{
    public required long OperationId { get; init; }
    public required string Summary { get; init; }
    public required List<SerializableChatMessage> CompactedMessages { get; init; }
    public required int MessagesBefore { get; init; }
    public required int ClearedCount { get; init; }
    public required long PreCompactionInputTokens { get; init; }
    public required int KeepCountUsed { get; init; }
}

internal sealed record CompactionWorkFailed : INoSerializationVerificationNeeded
{
    public required long OperationId { get; init; }
    public required Exception Cause { get; init; }
}

/// <summary>
/// Internal message completing a memory extraction LLM call.
/// Contains extracted memories that should be persisted externally.
/// </summary>
internal sealed record MemoryExtractionCompleted : INoSerializationVerificationNeeded
{
    public required string ExtractedMemories { get; init; }
}

/// <summary>
/// Internal message when a compaction step (extraction or summarization) fails.
/// </summary>
internal sealed record CompactionFailed : INoSerializationVerificationNeeded
{
    public required Exception Cause { get; init; }
}

/// <summary>
/// Internal message completing a sidecar title generation LLM call.
/// </summary>
internal sealed record TitleGenerationCompleted : INoSerializationVerificationNeeded
{
    public required string Title { get; init; }
}

/// <summary>
/// Sent to child actors (e.g., observer) when the session changes phase.
/// Enables child actors to react to lifecycle events (e.g., trigger
/// final distillation when entering <see cref="SessionPhase.Passivating"/>).
/// </summary>
internal sealed record SessionPhaseChanged(SessionPhase Phase) : INotInfluenceReceiveTimeout, INoSerializationVerificationNeeded;

/// <summary>
/// Sent to the observer actor to request immediate memory distillation
/// before the session stops during passivation.
/// </summary>
internal sealed record RequestFinalDistillation : INoSerializationVerificationNeeded;

/// <summary>
/// Sent back from the passivation timeout timer when the observer
/// does not complete distillation within the grace period.
/// </summary>
internal sealed record PassivationTimeout : INoSerializationVerificationNeeded;

/// <summary>
/// Timer-fired message that triggers an LLM call retry after exponential backoff.
/// Carries the attempt number for observability logging.
/// </summary>
internal sealed record RetryLlmCallAfterBackoff(int Attempt) : INoSerializationVerificationNeeded;
