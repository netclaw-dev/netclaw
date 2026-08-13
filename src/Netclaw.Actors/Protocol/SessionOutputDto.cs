// -----------------------------------------------------------------------
// <copyright file="SessionOutputDto.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Security;
using static Netclaw.Actors.Sessions.SessionProtocol;

namespace Netclaw.Actors.Protocol;

/// <summary>
/// Wire discriminator strings for <see cref="SessionOutputDto.Type"/>.
/// Used by <see cref="SessionOutputDtoMapper"/> and any consumers
/// that pattern-match on the Type field.
/// </summary>
public static class SessionOutputTypes
{
    public const string Text = "text";
    public const string TextDelta = "text_delta";
    public const string Thinking = "thinking";
    public const string ThinkingDelta = "thinking_delta";
    public const string ToolCall = "tool_call";
    public const string ToolActivity = "tool_activity";
    public const string ToolResult = "tool_result";
    public const string Usage = "usage";
    public const string TurnCompleted = "turn_completed";
    public const string SessionTitle = "session_title";
    public const string Error = "error";
    public const string File = "file";
    public const string SubAgent = "subagent";
    public const string BufferFlush = "buffer_flush";
    public const string ProcessingState = "processing_state";
    public const string UserMessageQueued = "user_message_queued";
    public const string UserMessagesPulled = "user_messages_pulled";
    public const string Compaction = "compaction";
    public const string SessionJoined = "session_joined";
    public const string ToolInteraction = "tool_interaction";
    public const string ApprovalOutcome = "approval_outcome";
    public const string Unknown = "unknown";
}

/// <summary>
/// Lightweight DTO for a chat message carried on the wire (role + content only).
/// Used to replay recent history when resuming a session.
/// </summary>
public sealed record ChatMessageDto(string Role, string Content);

/// <summary>
/// One user message in an agent-pull receipt.
/// </summary>
public sealed record PulledUserMessageDto(string MessageId, string Content);

/// <summary>
/// Wire-safe DTO for session output. Flattens the discriminated union
/// (<see cref="SessionOutput"/>) into a single serializable type for
/// SignalR transport.
/// </summary>
public sealed record SessionOutputDto
{
    /// <summary>
    /// Output type discriminator (e.g. "text", "text_delta", "thinking", "thinking_delta", "tool_call",
    /// "tool_result", "usage", "turn_completed", "error", "compaction",
    /// "session_joined", "session_title").
    /// </summary>
    public required string Type { get; init; }

    public required string SessionId { get; init; }

    public long TimestampMs { get; init; }

    // Text / Thinking
    public string? Text { get; init; }

    // Tool Call / Tool Result
    public string? CallId { get; init; }
    public string? ToolName { get; init; }
    public string? ArgumentsJson { get; init; }
    public string? Result { get; init; }
    public string? ToolFailureCode { get; init; }
    public string? TurnId { get; init; }
    public string? ActivityPhase { get; init; }
    public string? ActivitySummary { get; init; }
    public string? ToolBatchId { get; init; }
    public int? ToolBatchSize { get; init; }
    public string? ToolRationale { get; init; }

    // Usage
    public long? InputTokens { get; init; }
    public long? OutputTokens { get; init; }
    public long? TotalTokens { get; init; }
    public long? CachedInputTokens { get; init; }
    public long? ReasoningTokens { get; init; }
    public int? ContextWindowTokens { get; init; }
    public double? UsagePercent { get; init; }
    public double? PromptMs { get; init; }
    public double? PredictedPerSecond { get; init; }

    // Turn Completed
    [System.Text.Json.Serialization.JsonConverter(typeof(NullableTurnNumberJsonConverter))]
    public TurnNumber? TurnNumber { get; init; }
    public string? TurnOutcome { get; init; }
    public string? SourceReminderId { get; init; }

    // Error
    public string? ErrorMessage { get; init; }
    public string? ErrorDetail { get; init; }
    public string? ErrorCorrelationId { get; init; }
    public string? ErrorCategory { get; init; }

    // Processing State
    public bool? IsProcessing { get; init; }
    public bool? ProcessingStateRequired { get; init; }

    // User message lifecycle
    public string? MessageId { get; init; }
    public int? QueueDepth { get; init; }
    public string? MessageBatchId { get; init; }
    public List<PulledUserMessageDto>? PulledUserMessages { get; init; }

    // Compaction
    public int? MessagesBefore { get; init; }
    public int? MessagesAfter { get; init; }
    public bool? ToolResultsCleared { get; init; }
    public bool? Summarized { get; init; }
    public long? PreCompactionInputTokens { get; init; }
    public int? KeepCountUsed { get; init; }

    // Session Joined
    public string? Title { get; init; }
    public int? TurnCount { get; init; }
    public List<ChatMessageDto>? RecentMessages { get; init; }
    public List<SessionTranscriptEntry>? RecentTranscript { get; init; }

    // Tool Interaction
    public string? InteractionKind { get; init; }
    public string? InteractionDisplayText { get; init; }
    public string? RequesterSenderId { get; init; }
    public string? InteractionRequesterPrincipal { get; init; }
    public List<string>? InteractionPatterns { get; init; }
    public List<string>? InteractionCandidateVerbs { get; init; }
    public List<ApprovalCandidate>? InteractionCandidates { get; init; }
    public string? InteractionCwd { get; init; }
    public bool? InteractionIsMessy { get; init; }
    public List<ToolInteractionOption>? InteractionOptions { get; init; }
    public bool? InteractionHasAdoptedContext { get; init; }
    public bool? InteractionHasThirdPartyAdoptedContext { get; init; }
    public List<string>? InteractionAdoptedSpeakerIds { get; init; }
    public bool? InteractionPersistedAdoptedContext { get; init; }

    // Approval Outcome
    public string? ApprovalSelectedKey { get; init; }
    public string? ApprovalParentCallId { get; init; }

    // SubAgent
    public string? AgentName { get; init; }
    public string? Phase { get; init; }
    public string? RunId { get; init; }
    public string? ParentCallId { get; init; }
    public int? ToolCountSub { get; init; }
    public bool? SubAgentSuccess { get; init; }
    public string? SubAgentOutcome { get; init; }
    public string? SubAgentOutcomeReason { get; init; }
    public double? DurationMs { get; init; }
    public string? MemoryDecision { get; init; }
    public string? MemoryDecisionReason { get; init; }
    public int? FindingsCount { get; init; }

    // File Output
    public string? FilePath { get; init; }
    public string? FileName { get; init; }
    public string? MimeType { get; init; }
}
