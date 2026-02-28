using Netclaw.Actors.Protocol;

namespace Netclaw.Daemon.Gateway;

/// <summary>
/// Maps <see cref="SessionOutput"/> discriminated union types to
/// <see cref="SessionOutputDto"/> for wire transport.
/// </summary>
public static class SessionOutputMapper
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

        CompactionOutput msg => new SessionOutputDto
        {
            Type = "compaction",
            SessionId = msg.SessionId.Value,
            TimestampMs = msg.TimestampMs,
            MessagesBefore = msg.MessagesBefore,
            MessagesAfter = msg.MessagesAfter
        },

        SessionJoined msg => new SessionOutputDto
        {
            Type = "session_joined",
            SessionId = msg.SessionId.Value,
            TimestampMs = msg.TimestampMs,
            Title = msg.Title,
            TurnCount = msg.TurnCount
        },

        _ => new SessionOutputDto
        {
            Type = "unknown",
            SessionId = output.SessionId.Value,
            TimestampMs = output.TimestampMs
        }
    };
}
