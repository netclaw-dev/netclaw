// -----------------------------------------------------------------------
// <copyright file="LlmMessages.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Akka.Actor;
using Microsoft.Extensions.AI;
using Netclaw.Actors.Protocol;
using Netclaw.Actors.Sessions.Pipelines;
using Netclaw.Actors.SubAgents;
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

    public int StreamUpdateCount { get; init; }

    public int EmptyStreamUpdateCount { get; init; }

    public int StreamTextDeltaCount { get; init; }

    public int StreamTextChars { get; init; }

    public int StreamThinkingDeltaCount { get; init; }

    public int StreamThinkingChars { get; init; }

    public int StreamToolCallDeltaCount { get; init; }
}

/// <summary>
/// Incremental streaming delta emitted while an LLM response is in-flight.
/// <see cref="Substantive"/> is false for content-free keepalives (e.g.
/// <c>prompt_progress</c> heartbeats) so the watchdog refreshes the prefill budget
/// on them but only promotes to the tighter inter-delta budget on real output.
/// </summary>
internal sealed record LlmResponseDeltaReceived(AIContent Content) : INoSerializationVerificationNeeded
{
    public long CallId { get; init; }

    public bool Substantive { get; init; }
}

/// <summary>
/// Internal message sent back to the session actor when the async LLM call fails.
/// </summary>
internal sealed record LlmCallFailed(Exception Cause) : INoSerializationVerificationNeeded
{
    public long CallId { get; init; }
}

/// <summary>
/// Internal message sent back to the session actor when tool execution completes.
/// Contains the tool results to feed back into the next LLM call.
/// </summary>
internal sealed record ToolExecutionCompleted : INoSerializationVerificationNeeded
{
    public required List<Protocol.SerializableChatMessage> ToolResults { get; init; }
    public List<SerializableMediaReference> ModelInputMediaReferences { get; init; } = [];
    public List<FileAttachmentInfo> FileAttachments { get; init; } = [];
    public List<CompletedSubAgentRun> CompletedSubAgentRuns { get; init; } = [];
    public List<AcceptedSubAgentFinding> AcceptedSubAgentFindings { get; init; } = [];
    public List<Jobs.ActiveJobInfo> StartedBackgroundJobs { get; init; } = [];
    public List<SessionScratchCorrectionChange> ScratchCorrectionChanges { get; init; } = [];
    public Dictionary<string, string> ToolFailureCodes { get; init; } = new(StringComparer.Ordinal);
    public Dictionary<string, ToolInvocationReceipt> ToolReceipts { get; init; } = new(StringComparer.Ordinal);
    public Dictionary<string, ToolExposureRequest> ToolExposureRequests { get; init; } = new(StringComparer.Ordinal);
}

internal sealed record ToolExecutionSingleCompleted(ToolCallResult Result) : INoSerializationVerificationNeeded;

internal sealed record ToolExposureRequest(ToolName ToolName);

/// <summary>
/// Piped back to the session as the resolution of a background-job reap Ask
/// (issued when a session with active jobs passivates). <see cref="Error"/> is
/// non-null when the Ask failed (timeout or manager error) — passivation then
/// proceeds anyway, because the manager's kill is idempotent and no job process
/// outlives the daemon. <see cref="Epoch"/> correlates the reply to the exact
/// reap request that produced it: a session can abort passivation and re-issue
/// a fresh reap, and a late reply from the superseded request must not resolve
/// the newer handshake.
/// </summary>
internal sealed record JobReapResolved(long Epoch, int ReapedCount, Exception? Error)
    : INoSerializationVerificationNeeded;

internal sealed record ToolExecutionBatchCompleted : INoSerializationVerificationNeeded;

internal sealed record WorkingContextSnapshotReady(
    long Generation,
    bool ForceNoTools,
    string? TurnRestartNotice,
    WorkingContextSnapshot Snapshot) : INoSerializationVerificationNeeded;

internal sealed record WorkingContextSnapshotCancelled(long Generation)
    : INoSerializationVerificationNeeded;

internal sealed record WorkingContextSnapshotFailed(
    long Generation,
    bool ForceNoTools,
    string? TurnRestartNotice,
    WorkingContext WorkingContext,
    Exception Cause) : INoSerializationVerificationNeeded;

internal sealed record WorkingContextSnapshotFatal(Exception Cause)
    : INoSerializationVerificationNeeded;

internal sealed record CompletedSubAgentRun : INoSerializationVerificationNeeded
{
    public required SubAgentRunId RunId { get; init; }
    public required SubAgents.AgentName AgentName { get; init; }
    public required ChildRunCompletion Completion { get; init; }
    public required TimeSpan Duration { get; init; }
    public int FindingsCount { get; init; }
    public string? MemoryDecision { get; init; }
    public string? MemoryDecisionReason { get; init; }
    public bool Success => Completion.Success;
    public SubAgentRunOutcome Outcome => Completion.Outcome;
    public SubAgentOutcomeReason? OutcomeReason => Completion.Reason;
    public WorkingContextDelta? WorkingContext => Completion.Delta;
}

internal sealed record AcceptedSubAgentFinding : INoSerializationVerificationNeeded
{
    public required SubAgentRunId RunId { get; init; }
    public required SubAgents.AgentName AgentName { get; init; }
    public required TimeSpan Duration { get; init; }
    public required SubAgentFindingShape Shape { get; init; }
    public required string Title { get; init; }
    public required string Content { get; init; }

    // Kind / UpdateSemantics stay string: the source SubAgentFinding carries
    // them as free-form wire strings with no matching enum.
    public required string Kind { get; init; }
    public required SubAgentFindingSensitivity Sensitivity { get; init; }
    public required SubAgentFindingRecallMode RecallMode { get; init; }
    public required string UpdateSemantics { get; init; }
    public required double Confidence { get; init; }
    public required SubAgentFindingDurability Durability { get; init; }
    public required SubAgentFindingReusability Reusability { get; init; }
    public IReadOnlyList<string> Evidence { get; init; } = [];
    public long? FreshnessAtMs { get; init; }
    public required SubAgentFindingReviewDecision Decision { get; init; }
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
/// and return the session actor to Ready state. <see cref="NoProgress"/> is true
/// when the keepalive-immune no-progress deadline fired (the call produced no
/// substantive output for the whole budget) rather than the liveness timer —
/// the handler treats that as a hard kill with no grace, since keepalives never
/// refresh it.
/// </summary>
internal sealed record ProcessingWatchdogExpired(long OperationId, string OperationName, bool NoProgress = false)
    : INoSerializationVerificationNeeded;

/// <summary>
/// Marshal a child actor creation request back onto the session actor thread.
/// This keeps <c>Context.ActorOf</c> usage on the actor mailbox thread.
/// </summary>
internal sealed record SpawnChildActorRequest(Props Props, string ActorName)
    : INoSerializationVerificationNeeded;

/// <summary>
/// Internal trigger to begin the compaction sequence.
/// Sent to self after a turn completes when usage exceeds the threshold.
/// </summary>
internal sealed record CompactionTriggered(long InputTokenCount) : INoSerializationVerificationNeeded;

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
/// Fired by a short timer after the session has finished its passivation
/// distillation/snapshot work. The Passivating receive ignores in-flight
/// snapshot acks but treats any user-initiated message as a signal to abort
/// the stop, so this timer is the explicit "no one woke us up; commit now"
/// signal. See <c>LlmSessionActor.CompletePassivation</c>.
/// </summary>
internal sealed record PassivationFinalStop : INoSerializationVerificationNeeded;
