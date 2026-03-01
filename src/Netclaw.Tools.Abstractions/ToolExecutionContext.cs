namespace Netclaw.Tools;

/// <summary>
/// Describes a file attachment registered by a tool during execution.
/// </summary>
public sealed record FileAttachmentInfo(string FilePath, string FileName, string MimeType);

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
