namespace Netclaw.Actors.Protocol;

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

    // Usage
    public long? InputTokens { get; init; }
    public long? OutputTokens { get; init; }
    public long? TotalTokens { get; init; }
    public int? ContextWindowTokens { get; init; }
    public double? UsagePercent { get; init; }

    // Turn Completed
    public int? TurnNumber { get; init; }

    // Error
    public string? ErrorMessage { get; init; }
    public string? ErrorDetail { get; init; }

    // Compaction
    public int? MessagesBefore { get; init; }
    public int? MessagesAfter { get; init; }

    // Session Joined
    public string? Title { get; init; }
    public int? TurnCount { get; init; }

    // File Output
    public string? FilePath { get; init; }
    public string? FileName { get; init; }
    public string? MimeType { get; init; }
}
