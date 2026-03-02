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
            Type = "text",
            SessionId = msg.SessionId.Value,
            TimestampMs = msg.TimestampMs,
            Text = msg.Text
        },

        TextDeltaOutput msg => new SessionOutputDto
        {
            Type = "text_delta",
            SessionId = msg.SessionId.Value,
            TimestampMs = msg.TimestampMs,
            Text = msg.Delta
        },

        ThinkingOutput msg => new SessionOutputDto
        {
            Type = "thinking",
            SessionId = msg.SessionId.Value,
            TimestampMs = msg.TimestampMs,
            Text = msg.Text
        },

        ThinkingDeltaOutput msg => new SessionOutputDto
        {
            Type = "thinking_delta",
            SessionId = msg.SessionId.Value,
            TimestampMs = msg.TimestampMs,
            Text = msg.Delta
        },

        ToolCallOutput msg => new SessionOutputDto
        {
            Type = "tool_call",
            SessionId = msg.SessionId.Value,
            TimestampMs = msg.TimestampMs,
            CallId = msg.CallId,
            ToolName = msg.ToolName,
            ArgumentsJson = msg.ArgumentsJson
        },

        ToolResultOutput msg => new SessionOutputDto
        {
            Type = "tool_result",
            SessionId = msg.SessionId.Value,
            TimestampMs = msg.TimestampMs,
            CallId = msg.CallId,
            ToolName = msg.ToolName,
            Result = msg.Result
        },

        UsageOutput msg => new SessionOutputDto
        {
            Type = "usage",
            SessionId = msg.SessionId.Value,
            TimestampMs = msg.TimestampMs,
            InputTokens = msg.InputTokens,
            OutputTokens = msg.OutputTokens,
            TotalTokens = msg.TotalTokens,
            ContextWindowTokens = msg.ContextWindowTokens,
            UsagePercent = msg.UsagePercent
        },

        TurnCompleted msg => new SessionOutputDto
        {
            Type = "turn_completed",
            SessionId = msg.SessionId.Value,
            TimestampMs = msg.TimestampMs,
            TurnNumber = msg.TurnNumber
        },

        SessionTitleOutput msg => new SessionOutputDto
        {
            Type = "session_title",
            SessionId = msg.SessionId.Value,
            TimestampMs = msg.TimestampMs,
            Title = msg.Title
        },

        ErrorOutput msg => new SessionOutputDto
        {
            Type = "error",
            SessionId = msg.SessionId.Value,
            TimestampMs = msg.TimestampMs,
            ErrorMessage = msg.Message,
            ErrorDetail = msg.Cause?.ToString()
        },

        FileOutput msg => new SessionOutputDto
        {
            Type = "file",
            SessionId = msg.SessionId.Value,
            TimestampMs = msg.TimestampMs,
            FilePath = msg.FilePath,
            FileName = msg.FileName,
            MimeType = msg.MimeType
        },

        SubAgentOutput msg => new SessionOutputDto
        {
            Type = "subagent",
            SessionId = msg.SessionId.Value,
            TimestampMs = msg.TimestampMs,
            AgentName = msg.AgentName,
            Phase = msg.Phase.ToString().ToLowerInvariant(),
            ToolCountSub = msg.ToolCount,
            SubAgentSuccess = msg.Success,
            DurationMs = msg.Duration.TotalMilliseconds
        },

        CompactionOutput msg => new SessionOutputDto
        {
            Type = "compaction",
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
            Type = "session_joined",
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

        _ => new SessionOutputDto
        {
            Type = "unknown",
            SessionId = output.SessionId.Value,
            TimestampMs = output.TimestampMs
        }
    };

    public static SessionOutput FromDto(SessionOutputDto dto)
    {
        var sessionId = new SessionId(dto.SessionId);

        return dto.Type switch
        {
            "text" => new TextOutput
            {
                SessionId = sessionId,
                TimestampMs = dto.TimestampMs,
                Text = dto.Text ?? string.Empty
            },
            "text_delta" => new TextDeltaOutput
            {
                SessionId = sessionId,
                TimestampMs = dto.TimestampMs,
                Delta = dto.Text ?? string.Empty
            },
            "thinking" => new ThinkingOutput
            {
                SessionId = sessionId,
                TimestampMs = dto.TimestampMs,
                Text = dto.Text ?? string.Empty
            },
            "thinking_delta" => new ThinkingDeltaOutput
            {
                SessionId = sessionId,
                TimestampMs = dto.TimestampMs,
                Delta = dto.Text ?? string.Empty
            },
            "tool_call" => new ToolCallOutput
            {
                SessionId = sessionId,
                TimestampMs = dto.TimestampMs,
                CallId = dto.CallId ?? string.Empty,
                ToolName = dto.ToolName ?? "unknown",
                ArgumentsJson = dto.ArgumentsJson
            },
            "tool_result" => new ToolResultOutput
            {
                SessionId = sessionId,
                TimestampMs = dto.TimestampMs,
                CallId = dto.CallId ?? string.Empty,
                ToolName = dto.ToolName ?? "unknown",
                Result = dto.Result ?? string.Empty
            },
            "usage" => new UsageOutput
            {
                SessionId = sessionId,
                TimestampMs = dto.TimestampMs,
                InputTokens = dto.InputTokens,
                OutputTokens = dto.OutputTokens,
                TotalTokens = dto.TotalTokens,
                ContextWindowTokens = dto.ContextWindowTokens ?? 0,
                UsagePercent = dto.UsagePercent
            },
            "turn_completed" => new TurnCompleted
            {
                SessionId = sessionId,
                TimestampMs = dto.TimestampMs,
                TurnNumber = dto.TurnNumber ?? 0
            },
            "session_title" => new SessionTitleOutput
            {
                SessionId = sessionId,
                TimestampMs = dto.TimestampMs,
                Title = dto.Title ?? string.Empty
            },
            "error" => new ErrorOutput
            {
                SessionId = sessionId,
                TimestampMs = dto.TimestampMs,
                Message = dto.ErrorMessage ?? "Unknown daemon error",
                Cause = dto.ErrorDetail is not null
                    ? new Exception(dto.ErrorDetail) : null
            },
            "file" => new FileOutput
            {
                SessionId = sessionId,
                TimestampMs = dto.TimestampMs,
                FilePath = dto.FilePath ?? string.Empty,
                FileName = dto.FileName ?? "file",
                MimeType = dto.MimeType ?? "application/octet-stream"
            },
            "subagent" => new SubAgentOutput
            {
                SessionId = sessionId,
                TimestampMs = dto.TimestampMs,
                AgentName = dto.AgentName ?? "unknown",
                Phase = dto.Phase?.Equals("completed", StringComparison.OrdinalIgnoreCase) == true
                    ? SubAgents.SubAgentPhase.Completed
                    : SubAgents.SubAgentPhase.Started,
                ToolCount = dto.ToolCountSub ?? 0,
                Success = dto.SubAgentSuccess ?? false,
                Duration = TimeSpan.FromMilliseconds(dto.DurationMs ?? 0)
            },
            "compaction" => new CompactionOutput
            {
                SessionId = sessionId,
                TimestampMs = dto.TimestampMs,
                MessagesBefore = dto.MessagesBefore ?? 0,
                MessagesAfter = dto.MessagesAfter ?? 0,
                ContextWindowTokens = dto.ContextWindowTokens ?? 0,
                PreCompactionInputTokens = dto.PreCompactionInputTokens ?? 0,
                KeepCountUsed = dto.KeepCountUsed ?? 0
            },
            "session_joined" => new SessionJoined
            {
                SessionId = sessionId,
                TimestampMs = dto.TimestampMs,
                Title = dto.Title,
                TurnCount = dto.TurnCount ?? 0,
                RecentMessages = dto.RecentMessages
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
