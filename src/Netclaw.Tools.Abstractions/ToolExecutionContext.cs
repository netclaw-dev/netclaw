namespace Netclaw.Tools;

/// <summary>
/// Per-call execution context passed from the session actor to tools.
/// Provides session-scoped state like working directories for file output.
/// </summary>
public sealed class ToolExecutionContext
{
    public static readonly ToolExecutionContext Empty = new(null, null);

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
}
