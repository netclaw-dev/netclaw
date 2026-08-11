// -----------------------------------------------------------------------
// <copyright file="SessionOutputDtoMapper.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Actors.Reminders;
using Netclaw.Media;
using Netclaw.Tools;
using static Netclaw.Actors.Sessions.SessionProtocol;

namespace Netclaw.Actors.Protocol;

/// <summary>
/// Converts between <see cref="SessionOutput"/> and wire-safe
/// <see cref="SessionOutputDto"/> values used by SignalR.
/// </summary>
public static class SessionOutputDtoMapper
{
    public static SessionOutputDto ToDto(SessionOutput output) => output switch
    {
        TextOutput msg => new SessionOutputDto
        {
            Type = SessionOutputTypes.Text,
            SessionId = msg.SessionId.Value,
            TimestampMs = msg.TimestampMs,
            Text = msg.Text
        },

        TextDeltaOutput msg => new SessionOutputDto
        {
            Type = SessionOutputTypes.TextDelta,
            SessionId = msg.SessionId.Value,
            TimestampMs = msg.TimestampMs,
            Text = msg.Delta
        },

        ThinkingOutput msg => new SessionOutputDto
        {
            Type = SessionOutputTypes.Thinking,
            SessionId = msg.SessionId.Value,
            TimestampMs = msg.TimestampMs,
            Text = msg.Text
        },

        ThinkingDeltaOutput msg => new SessionOutputDto
        {
            Type = SessionOutputTypes.ThinkingDelta,
            SessionId = msg.SessionId.Value,
            TimestampMs = msg.TimestampMs,
            Text = msg.Delta
        },

        ToolCallOutput msg => new SessionOutputDto
        {
            Type = SessionOutputTypes.ToolCall,
            SessionId = msg.SessionId.Value,
            TimestampMs = msg.TimestampMs,
            CallId = msg.CallId.Value,
            ToolName = msg.ToolName.Value,
            ArgumentsJson = msg.ArgumentsJson
        },

        ToolResultOutput msg => new SessionOutputDto
        {
            Type = SessionOutputTypes.ToolResult,
            SessionId = msg.SessionId.Value,
            TimestampMs = msg.TimestampMs,
            CallId = msg.CallId.Value,
            ToolName = msg.ToolName.Value,
            Result = msg.Result
        },

        UsageOutput msg => new SessionOutputDto
        {
            Type = SessionOutputTypes.Usage,
            SessionId = msg.SessionId.Value,
            TimestampMs = msg.TimestampMs,
            InputTokens = msg.InputTokens,
            OutputTokens = msg.OutputTokens,
            TotalTokens = msg.TotalTokens,
            CachedInputTokens = msg.CachedInputTokens,
            ReasoningTokens = msg.ReasoningTokens,
            ContextWindowTokens = msg.ContextWindowTokens,
            UsagePercent = msg.UsagePercent,
            PromptMs = msg.PromptMs,
            PredictedPerSecond = msg.PredictedPerSecond,
        },

        TurnCompleted msg => new SessionOutputDto
        {
            Type = SessionOutputTypes.TurnCompleted,
            SessionId = msg.SessionId.Value,
            TimestampMs = msg.TimestampMs,
            TurnNumber = msg.TurnNumber,
            TurnOutcome = msg.Outcome.ToString().ToLowerInvariant(),
            SourceReminderId = msg.SourceReminderId?.Value
        },

        SessionTitleOutput msg => new SessionOutputDto
        {
            Type = SessionOutputTypes.SessionTitle,
            SessionId = msg.SessionId.Value,
            TimestampMs = msg.TimestampMs,
            Title = msg.Title
        },

        ErrorOutput msg => new SessionOutputDto
        {
            Type = SessionOutputTypes.Error,
            SessionId = msg.SessionId.Value,
            TimestampMs = msg.TimestampMs,
            ErrorMessage = msg.Message,
            ErrorDetail = msg.Cause?.ToString(),
            ErrorCorrelationId = msg.CorrelationId.ToString("N"),
            ErrorCategory = msg.Category.ToString()
        },

        FileOutput msg => new SessionOutputDto
        {
            Type = SessionOutputTypes.File,
            SessionId = msg.SessionId.Value,
            TimestampMs = msg.TimestampMs,
            FilePath = msg.FilePath,
            FileName = msg.FileName,
            MimeType = msg.MimeType.Value
        },

        SubAgentOutput msg => new SessionOutputDto
        {
            Type = SessionOutputTypes.SubAgent,
            SessionId = msg.SessionId.Value,
            TimestampMs = msg.TimestampMs,
            AgentName = msg.AgentName.Value,
            Phase = msg.Phase.ToString().ToLowerInvariant(),
            ToolCountSub = msg.ToolCount,
            SubAgentSuccess = msg.Success,
            SubAgentOutcome = msg.Phase == SubAgents.SubAgentPhase.Completed
                ? msg.Outcome.ToString().ToLowerInvariant()
                : null,
            SubAgentOutcomeReason = msg.Phase == SubAgents.SubAgentPhase.Completed
                ? msg.OutcomeReason?.Value
                : null,
            DurationMs = msg.Duration.TotalMilliseconds,
            MemoryDecision = msg.MemoryDecision,
            MemoryDecisionReason = msg.MemoryDecisionReason,
            FindingsCount = msg.FindingsCount
        },

        BufferFlush msg => new SessionOutputDto
        {
            Type = SessionOutputTypes.BufferFlush,
            SessionId = msg.SessionId.Value,
            TimestampMs = msg.TimestampMs
        },

        ProcessingStateOutput msg => new SessionOutputDto
        {
            Type = SessionOutputTypes.ProcessingState,
            SessionId = msg.SessionId.Value,
            TimestampMs = msg.TimestampMs,
            IsProcessing = msg.IsProcessing,
            ProcessingStateRequired = msg.IsRequired
        },

        CompactionOutput msg => new SessionOutputDto
        {
            Type = SessionOutputTypes.Compaction,
            SessionId = msg.SessionId.Value,
            TimestampMs = msg.TimestampMs,
            MessagesBefore = msg.MessagesBefore,
            MessagesAfter = msg.MessagesAfter,
            ContextWindowTokens = msg.ContextWindowTokens,
            PreCompactionInputTokens = msg.PreCompactionInputTokens,
            KeepCountUsed = msg.KeepCountUsed
        },

        SessionJoined msg => new SessionOutputDto
        {
            Type = SessionOutputTypes.SessionJoined,
            SessionId = msg.SessionId.Value,
            TimestampMs = msg.TimestampMs,
            Title = msg.Title,
            TurnCount = msg.TurnCount,
            RecentMessages = msg.RecentMessages?.Select(m => new ChatMessageDto(m.Role, m.Content)).ToList()
        },

        ToolInteractionRequest msg => new SessionOutputDto
        {
            Type = SessionOutputTypes.ToolInteraction,
            SessionId = msg.SessionId.Value,
            TimestampMs = msg.TimestampMs,
            InteractionKind = msg.Kind,
            CallId = msg.CallId.Value,
            ToolName = msg.ToolName.Value,
            InteractionDisplayText = msg.DisplayText,
            RequesterSenderId = msg.RequesterSenderId?.Value,
            InteractionPatterns = [.. msg.Patterns],
            InteractionCandidateVerbs = [.. msg.CandidateVerbs],
            InteractionCwd = msg.Cwd,
            InteractionIsMessy = msg.IsMessy,
            InteractionOptions = [.. msg.Options],
            InteractionHasAdoptedContext = msg.HasAdoptedContext,
            InteractionHasThirdPartyAdoptedContext = msg.HasThirdPartyAdoptedContext,
            InteractionAdoptedSpeakerIds = [.. msg.AdoptedSpeakerIds]
        },

        _ => new SessionOutputDto
        {
            Type = SessionOutputTypes.Unknown,
            SessionId = output.SessionId.Value,
            TimestampMs = output.TimestampMs
        }
    };

    public static SessionOutput FromDto(SessionOutputDto dto)
    {
        var sessionId = new SessionId(dto.SessionId);

        return dto.Type switch
        {
            SessionOutputTypes.Text => new TextOutput(dto.Text ?? string.Empty)
            {
                SessionId = sessionId,
                TimestampMs = dto.TimestampMs
            },
            SessionOutputTypes.TextDelta => new TextDeltaOutput(dto.Text ?? string.Empty)
            {
                SessionId = sessionId,
                TimestampMs = dto.TimestampMs
            },
            SessionOutputTypes.Thinking => new ThinkingOutput(dto.Text ?? string.Empty)
            {
                SessionId = sessionId,
                TimestampMs = dto.TimestampMs
            },
            SessionOutputTypes.ThinkingDelta => new ThinkingDeltaOutput(dto.Text ?? string.Empty)
            {
                SessionId = sessionId,
                TimestampMs = dto.TimestampMs
            },
            SessionOutputTypes.ToolCall => new ToolCallOutput
            {
                SessionId = sessionId,
                TimestampMs = dto.TimestampMs,
                CallId = new Netclaw.Tools.ToolCallId(dto.CallId ?? string.Empty),
                ToolName = new Netclaw.Tools.ToolName(dto.ToolName ?? "unknown"),
                ArgumentsJson = dto.ArgumentsJson
            },
            SessionOutputTypes.ToolResult => new ToolResultOutput
            {
                SessionId = sessionId,
                TimestampMs = dto.TimestampMs,
                CallId = new Netclaw.Tools.ToolCallId(dto.CallId ?? string.Empty),
                ToolName = new Netclaw.Tools.ToolName(dto.ToolName ?? "unknown"),
                Result = dto.Result ?? string.Empty
            },
            SessionOutputTypes.Usage => new UsageOutput
            {
                SessionId = sessionId,
                TimestampMs = dto.TimestampMs,
                InputTokens = dto.InputTokens,
                OutputTokens = dto.OutputTokens,
                TotalTokens = dto.TotalTokens,
                CachedInputTokens = dto.CachedInputTokens,
                ReasoningTokens = dto.ReasoningTokens,
                ContextWindowTokens = dto.ContextWindowTokens ?? 0,
                UsagePercent = dto.UsagePercent,
                PromptMs = dto.PromptMs,
                PredictedPerSecond = dto.PredictedPerSecond,
            },
            SessionOutputTypes.TurnCompleted => new TurnCompleted
            {
                SessionId = sessionId,
                TimestampMs = dto.TimestampMs,
                TurnNumber = dto.TurnNumber ?? new TurnNumber(0),
                Outcome = Enum.TryParse<TurnOutcome>(dto.TurnOutcome, ignoreCase: true, out var outcome)
                    ? outcome
                    : TurnOutcome.Completed,
                SourceReminderId = dto.SourceReminderId is null ? null : new ReminderId(dto.SourceReminderId)
            },
            SessionOutputTypes.SessionTitle => new SessionTitleOutput(dto.Title ?? string.Empty)
            {
                SessionId = sessionId,
                TimestampMs = dto.TimestampMs
            },
            SessionOutputTypes.Error => new ErrorOutput
            {
                SessionId = sessionId,
                TimestampMs = dto.TimestampMs,
                Message = dto.ErrorMessage ?? "Unknown daemon error",
                CorrelationId = Guid.TryParse(dto.ErrorCorrelationId, out var cid) ? cid : Guid.NewGuid(),
                Category = Enum.TryParse<ErrorCategory>(dto.ErrorCategory, out var cat) ? cat : ErrorCategory.Unknown,
                Cause = dto.ErrorDetail is not null
                    ? new Exception(dto.ErrorDetail) : null
            },
            SessionOutputTypes.File => new FileOutput
            {
                SessionId = sessionId,
                TimestampMs = dto.TimestampMs,
                FilePath = dto.FilePath ?? string.Empty,
                FileName = dto.FileName ?? "file",
                MimeType = new MimeType(dto.MimeType)
            },
            SessionOutputTypes.SubAgent => MapSubAgentOutput(dto, sessionId),
            SessionOutputTypes.BufferFlush => new BufferFlush
            {
                SessionId = sessionId,
                TimestampMs = dto.TimestampMs
            },
            SessionOutputTypes.ProcessingState => new ProcessingStateOutput(dto.IsProcessing ?? false)
            {
                SessionId = sessionId,
                TimestampMs = dto.TimestampMs,
                IsRequired = dto.ProcessingStateRequired ?? false
            },
            SessionOutputTypes.Compaction => new CompactionOutput
            {
                SessionId = sessionId,
                TimestampMs = dto.TimestampMs,
                MessagesBefore = dto.MessagesBefore ?? 0,
                MessagesAfter = dto.MessagesAfter ?? 0,
                ContextWindowTokens = dto.ContextWindowTokens ?? 0,
                PreCompactionInputTokens = dto.PreCompactionInputTokens ?? 0,
                KeepCountUsed = dto.KeepCountUsed ?? 0
            },
            SessionOutputTypes.SessionJoined => new SessionJoined
            {
                SessionId = sessionId,
                TimestampMs = dto.TimestampMs,
                Title = dto.Title,
                TurnCount = dto.TurnCount ?? 0,
                RecentMessages = dto.RecentMessages
            },
            SessionOutputTypes.ToolInteraction => new ToolInteractionRequest
            {
                SessionId = sessionId,
                TimestampMs = dto.TimestampMs,
                Kind = dto.InteractionKind ?? "approval",
                CallId = new Netclaw.Tools.ToolCallId(dto.CallId ?? string.Empty),
                ToolName = new Netclaw.Tools.ToolName(dto.ToolName ?? "unknown"),
                DisplayText = dto.InteractionDisplayText ?? string.Empty,
                RequesterSenderId = dto.RequesterSenderId is { } rsid ? new SenderId(rsid) : null,
                HasAdoptedContext = dto.InteractionHasAdoptedContext ?? false,
                HasThirdPartyAdoptedContext = dto.InteractionHasThirdPartyAdoptedContext ?? false,
                AdoptedSpeakerIds = dto.InteractionAdoptedSpeakerIds ?? [],
                Patterns = dto.InteractionPatterns ?? [],
                CandidateVerbs = dto.InteractionCandidateVerbs ?? [],
                Cwd = dto.InteractionCwd,
                IsMessy = dto.InteractionIsMessy ?? false,
                Options = dto.InteractionOptions ?? []
            },
            _ => new ErrorOutput
            {
                SessionId = sessionId,
                TimestampMs = dto.TimestampMs,
                Message = $"Unknown output type from daemon: {dto.Type}"
            }
            };
    }

    private static SubAgentRunOutcome ParseSubAgentOutcome(string? value, bool? success)
    {
        if (!string.IsNullOrWhiteSpace(value)
            && Enum.TryParse<SubAgentRunOutcome>(value, ignoreCase: true, out var parsed))
            return parsed;

        return success == false ? SubAgentRunOutcome.Failed : SubAgentRunOutcome.Completed;
    }

    private static SubAgentOutput MapSubAgentOutput(SessionOutputDto dto, SessionId sessionId)
    {
        var phase = dto.Phase?.Equals("completed", StringComparison.OrdinalIgnoreCase) == true
            ? SubAgents.SubAgentPhase.Completed
            : SubAgents.SubAgentPhase.Started;

        return new SubAgentOutput
        {
            SessionId = sessionId,
            TimestampMs = dto.TimestampMs,
            AgentName = new SubAgents.AgentName(dto.AgentName ?? "unknown"),
            Phase = phase,
            ToolCount = dto.ToolCountSub ?? 0,
            Success = dto.SubAgentSuccess ?? false,
            Outcome = phase == SubAgents.SubAgentPhase.Completed
                ? ParseSubAgentOutcome(dto.SubAgentOutcome, dto.SubAgentSuccess)
                : SubAgentRunOutcome.Completed,
            OutcomeReason = phase == SubAgents.SubAgentPhase.Completed && !string.IsNullOrWhiteSpace(dto.SubAgentOutcomeReason)
                ? new SubAgentOutcomeReason(dto.SubAgentOutcomeReason)
                : null,
            Duration = TimeSpan.FromMilliseconds(dto.DurationMs ?? 0),
            MemoryDecision = dto.MemoryDecision,
            MemoryDecisionReason = dto.MemoryDecisionReason,
            FindingsCount = dto.FindingsCount ?? 0
        };
    }
}
