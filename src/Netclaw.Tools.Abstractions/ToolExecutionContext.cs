namespace Netclaw.Tools;

/// <summary>
/// Describes a file attachment registered by a tool during execution.
/// </summary>
public sealed record FileAttachmentInfo(string FilePath, string FileName, string MimeType);

/// <summary>
/// Lightweight subagent activity notification for the tools abstraction layer.
/// Tools emit these via <see cref="ToolExecutionContext.OnSubAgentActivity"/>;
/// the session actor converts them to output events.
/// </summary>
public sealed record SubAgentNotificationInfo
{
    public required string AgentName { get; init; }
    public required bool IsStarted { get; init; }
    public int ToolCount { get; init; }
    public bool Success { get; init; }
    public TimeSpan Duration { get; init; }
    public IReadOnlyList<SubAgentFindingCandidate> Findings { get; init; } = [];
}

/// <summary>
/// Structured candidate surfaced by a subagent for parent-session durable-memory review.
/// </summary>
public sealed record SubAgentFindingCandidate
{
    public required string Title { get; init; }
    public required string Content { get; init; }
    public string Kind { get; init; } = "record";
    public string Domain { get; init; } = "project:default";
    public string Sensitivity { get; init; } = "normal";
    public string RecallMode { get; init; } = "auto";
    public string UpdateSemantics { get; init; } = "append-document";
    public double Confidence { get; init; } = 0.7;
    public long? FreshnessAtMs { get; init; }
}

/// <summary>
/// Per-call execution context passed from the session actor to tools.
/// Provides session-scoped state like working directories for file output.
/// </summary>
public sealed class ToolExecutionContext
{
    public static readonly ToolExecutionContext Empty = new(null, null);

    private List<FileAttachmentInfo>? _fileAttachments;

    public ToolExecutionContext(string? sessionId, string? sessionDirectory)
    {
        SessionId = sessionId;
        SessionDirectory = sessionDirectory;
    }

    /// <summary>
    /// Optional callback for tools that spawn subagents.
    /// The session wires this to relay notifications as <c>SubAgentOutput</c> events.
    /// </summary>
    public Action<SubAgentNotificationInfo>? OnSubAgentActivity { get; set; }

    /// <summary>The session that initiated this tool call.</summary>
    public string? SessionId { get; }

    /// <summary>
    /// Session-scoped temp directory for tools that write files to disk.
    /// Created lazily on first access.
    /// </summary>
    public string? SessionDirectory { get; }

    /// <summary>
    /// File attachments registered by tools during execution.
    /// </summary>
    public IReadOnlyList<FileAttachmentInfo> FileAttachments
        => _fileAttachments ?? (IReadOnlyList<FileAttachmentInfo>)[];

    /// <summary>
    /// Register a file attachment to be emitted as <c>FileOutput</c> after tool execution.
    /// </summary>
    public void AddFileAttachment(string filePath, string fileName, string mimeType)
    {
        _fileAttachments ??= [];
        _fileAttachments.Add(new FileAttachmentInfo(filePath, fileName, mimeType));
    }
}
