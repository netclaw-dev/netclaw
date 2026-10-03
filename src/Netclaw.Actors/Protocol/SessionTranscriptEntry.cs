// -----------------------------------------------------------------------
// <copyright file="SessionTranscriptEntry.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
namespace Netclaw.Actors.Protocol;

/// <summary>
/// Stable discriminators for settled session transcript entries.
/// </summary>
public static class SessionTranscriptEntryTypes
{
    public const string User = "user";
    public const string Assistant = "assistant";
    public const string Tool = "tool";
    public const string SubAgent = "subagent";
    public const string File = "file";
    public const string Error = "error";
    public const string Usage = "usage";
    public const string Compaction = "compaction";
    public const string Approval = "approval";
    public const string Legacy = "legacy";
    public const string Diagnostic = "diagnostic";
}

/// <summary>
/// A framework-owned settled transcript entry for resume and transport.
/// The <see cref="Type"/> value selects the valid optional fields.
/// </summary>
public sealed record SessionTranscriptEntry
{
    public required string Type { get; init; }

    public string? TurnId { get; init; }

    public long TimestampMs { get; init; }

    public string? Role { get; init; }

    public string? Text { get; init; }

    public string? CallId { get; init; }

    public string? ToolName { get; init; }

    public string? ArgumentsJson { get; init; }

    public string? Rationale { get; init; }

    public string? BatchId { get; init; }

    public int? BatchSize { get; init; }

    public string? Result { get; init; }

    public string? RunId { get; init; }

    public string? ParentCallId { get; init; }

    public string? AgentName { get; init; }

    public string? Outcome { get; init; }

    public string? OutcomeReason { get; init; }

    public string? ApprovalSelectedKey { get; init; }

    public double? DurationMs { get; init; }

    public int? FindingsCount { get; init; }

    public string? MemoryDecision { get; init; }

    public string? MemoryDecisionReason { get; init; }

    public string? FilePath { get; init; }

    public string? FileName { get; init; }

    public string? MimeType { get; init; }

    public string? ErrorMessage { get; init; }

    public string? ErrorDetail { get; init; }

    public string? ErrorCorrelationId { get; init; }

    public string? ErrorCategory { get; init; }

    public long? InputTokens { get; init; }

    public long? OutputTokens { get; init; }

    public long? TotalTokens { get; init; }

    public long? CachedInputTokens { get; init; }

    public long? ReasoningTokens { get; init; }

    public int? ContextWindowTokens { get; init; }

    public double? UsagePercent { get; init; }

    public double? PromptMs { get; init; }

    public double? PredictedPerSecond { get; init; }

    public int? MessagesBefore { get; init; }

    public int? MessagesAfter { get; init; }

    public bool? ToolResultsCleared { get; init; }

    public bool? Summarized { get; init; }

    public long? PreCompactionInputTokens { get; init; }

    public int? KeepCountUsed { get; init; }
}
