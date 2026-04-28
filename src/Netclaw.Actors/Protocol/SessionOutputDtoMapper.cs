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
            CallId = msg.CallId,
            ToolName = msg.ToolName,
            ArgumentsJson = msg.ArgumentsJson
        },

        ToolResultOutput msg => new SessionOutputDto
        {
            Type = SessionOutputTypes.ToolResult,
            SessionId = msg.SessionId.Value,
            TimestampMs = msg.TimestampMs,
            CallId = msg.CallId,
            ToolName = msg.ToolName,
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
            SourceReminderId = msg.SourceReminderId
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
            MimeType = msg.MimeType
        },

        SubAgentOutput msg => new SessionOutputDto
        {
            Type = SessionOutputTypes.SubAgent,
            SessionId = msg.SessionId.Value,
            TimestampMs = msg.TimestampMs,
            AgentName = msg.AgentName,
            Phase = msg.Phase.ToString().ToLowerInvariant(),
            ToolCountSub = msg.ToolCount,
            SubAgentSuccess = msg.Success,
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
            RecentMessages = msg.RecentMessages?.Select(m => new ChatMessageDto
            {
                Role = m.Role,
                Content = m.Content
            }).ToList()
        },

        ToolInteractionRequest msg => new SessionOutputDto
        {
            Type = SessionOutputTypes.ToolInteraction,
            SessionId = msg.SessionId.Value,
            TimestampMs = msg.TimestampMs,
            InteractionKind = msg.Kind,
            CallId = msg.CallId,
            ToolName = msg.ToolName,
            InteractionDisplayText = msg.DisplayText,
            RequesterSenderId = msg.RequesterSenderId,
            InteractionPatterns = msg.Patterns.ToList(),
            InteractionOptions = msg.Options.ToList(),
            InteractionHasAdoptedContext = msg.HasAdoptedContext,
            InteractionAdoptedSpeakerIds = msg.AdoptedSpeakerIds.ToList()
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
            SessionOutputTypes.Text => new TextOutput
            {
                SessionId = sessionId,
                TimestampMs = dto.TimestampMs,
                Text = dto.Text ?? string.Empty
            },
            SessionOutputTypes.TextDelta => new TextDeltaOutput
            {
                SessionId = sessionId,
                TimestampMs = dto.TimestampMs,
                Delta = dto.Text ?? string.Empty
            },
            SessionOutputTypes.Thinking => new ThinkingOutput
            {
                SessionId = sessionId,
                TimestampMs = dto.TimestampMs,
                Text = dto.Text ?? string.Empty
            },
            SessionOutputTypes.ThinkingDelta => new ThinkingDeltaOutput
            {
                SessionId = sessionId,
                TimestampMs = dto.TimestampMs,
                Delta = dto.Text ?? string.Empty
            },
            SessionOutputTypes.ToolCall => new ToolCallOutput
            {
                SessionId = sessionId,
                TimestampMs = dto.TimestampMs,
                CallId = dto.CallId ?? string.Empty,
                ToolName = dto.ToolName ?? "unknown",
                ArgumentsJson = dto.ArgumentsJson
            },
            SessionOutputTypes.ToolResult => new ToolResultOutput
            {
                SessionId = sessionId,
                TimestampMs = dto.TimestampMs,
                CallId = dto.CallId ?? string.Empty,
                ToolName = dto.ToolName ?? "unknown",
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
                TurnNumber = dto.TurnNumber ?? 0,
                Outcome = Enum.TryParse<TurnOutcome>(dto.TurnOutcome, ignoreCase: true, out var outcome)
                    ? outcome
                    : TurnOutcome.Completed,
                SourceReminderId = dto.SourceReminderId
            },
            SessionOutputTypes.SessionTitle => new SessionTitleOutput
            {
                SessionId = sessionId,
                TimestampMs = dto.TimestampMs,
                Title = dto.Title ?? string.Empty
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
                MimeType = dto.MimeType ?? "application/octet-stream"
            },
            SessionOutputTypes.SubAgent => new SubAgentOutput
            {
                SessionId = sessionId,
                TimestampMs = dto.TimestampMs,
                AgentName = dto.AgentName ?? "unknown",
                Phase = dto.Phase?.Equals("completed", StringComparison.OrdinalIgnoreCase) == true
                    ? SubAgents.SubAgentPhase.Completed
                    : SubAgents.SubAgentPhase.Started,
                ToolCount = dto.ToolCountSub ?? 0,
                Success = dto.SubAgentSuccess ?? false,
                Duration = TimeSpan.FromMilliseconds(dto.DurationMs ?? 0),
                MemoryDecision = dto.MemoryDecision,
                MemoryDecisionReason = dto.MemoryDecisionReason,
                FindingsCount = dto.FindingsCount ?? 0
            },
            SessionOutputTypes.BufferFlush => new BufferFlush
            {
                SessionId = sessionId,
                TimestampMs = dto.TimestampMs
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
                CallId = dto.CallId ?? string.Empty,
                ToolName = dto.ToolName ?? "unknown",
                DisplayText = dto.InteractionDisplayText ?? string.Empty,
                RequesterSenderId = dto.RequesterSenderId,
                HasAdoptedContext = dto.InteractionHasAdoptedContext ?? false,
                AdoptedSpeakerIds = dto.InteractionAdoptedSpeakerIds ?? [],
                Patterns = dto.InteractionPatterns ?? [],
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
}
