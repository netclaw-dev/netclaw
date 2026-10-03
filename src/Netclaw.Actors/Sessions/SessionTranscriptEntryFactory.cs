// -----------------------------------------------------------------------
// <copyright file="SessionTranscriptEntryFactory.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Actors.Protocol;
using static Netclaw.Actors.Sessions.SessionProtocol;

namespace Netclaw.Actors.Sessions;

internal static class SessionTranscriptEntryFactory
{
    public static SessionTranscriptEntry Tool(
        ToolCallOutput? call,
        ToolResultOutput result,
        string? turnId) => new()
        {
            Type = SessionTranscriptEntryTypes.Tool,
            TurnId = turnId,
            TimestampMs = result.TimestampMs,
            CallId = result.CallId.Value,
            ToolName = result.ToolName.Value,
            ArgumentsJson = call?.ArgumentsJson,
            Rationale = call?.Rationale,
            BatchId = call?.BatchId,
            BatchSize = call?.BatchSize,
            Result = result.Result
        };

    public static SessionTranscriptEntry Approval(ApprovalOutcomeOutput output, string? turnId) => new()
    {
        Type = SessionTranscriptEntryTypes.Approval,
        TurnId = turnId,
        TimestampMs = output.TimestampMs,
        CallId = output.CallId.Value,
        ParentCallId = output.ParentCallId,
        ToolName = output.ToolName.Value,
        ApprovalSelectedKey = output.SelectedKey.Value
    };

    public static SessionTranscriptEntry SubAgent(SubAgentOutput output, string? turnId) => new()
    {
        Type = SessionTranscriptEntryTypes.SubAgent,
        TurnId = turnId,
        TimestampMs = output.TimestampMs,
        RunId = output.RunId?.Value,
        ParentCallId = output.ParentCallId?.Value,
        AgentName = output.AgentName.Value,
        Outcome = output.Outcome.ToString().ToLowerInvariant(),
        OutcomeReason = output.OutcomeReason?.Value,
        DurationMs = output.Duration.TotalMilliseconds,
        FindingsCount = output.FindingsCount,
        MemoryDecision = output.MemoryDecision,
        MemoryDecisionReason = output.MemoryDecisionReason
    };

    public static SessionTranscriptEntry File(FileOutput output, string? turnId) => new()
    {
        Type = SessionTranscriptEntryTypes.File,
        TurnId = turnId,
        TimestampMs = output.TimestampMs,
        FilePath = output.FilePath,
        FileName = output.FileName,
        MimeType = output.MimeType.Value
    };

    public static SessionTranscriptEntry Error(ErrorOutput output, string? turnId) => new()
    {
        Type = SessionTranscriptEntryTypes.Error,
        TurnId = turnId,
        TimestampMs = output.TimestampMs,
        ErrorMessage = output.Message,
        ErrorDetail = output.Cause?.ToString(),
        ErrorCorrelationId = output.CorrelationId.ToString("D"),
        ErrorCategory = output.Category.ToString()
    };

    public static SessionTranscriptEntry Usage(UsageOutput output, string? turnId) => new()
    {
        Type = SessionTranscriptEntryTypes.Usage,
        TurnId = turnId,
        TimestampMs = output.TimestampMs,
        InputTokens = output.InputTokens,
        OutputTokens = output.OutputTokens,
        TotalTokens = output.TotalTokens,
        CachedInputTokens = output.CachedInputTokens,
        ReasoningTokens = output.ReasoningTokens,
        ContextWindowTokens = output.ContextWindowTokens,
        UsagePercent = output.UsagePercent,
        PromptMs = output.PromptMs,
        PredictedPerSecond = output.PredictedPerSecond
    };

    public static SessionTranscriptEntry Compaction(CompactionOutput output, string? turnId) => new()
    {
        Type = SessionTranscriptEntryTypes.Compaction,
        TurnId = turnId,
        TimestampMs = output.TimestampMs,
        MessagesBefore = output.MessagesBefore,
        MessagesAfter = output.MessagesAfter,
        ToolResultsCleared = output.ToolResultsCleared,
        Summarized = output.Summarized,
        ContextWindowTokens = output.ContextWindowTokens,
        PreCompactionInputTokens = output.PreCompactionInputTokens,
        KeepCountUsed = output.KeepCountUsed
    };
}
