namespace Netclaw.Configuration;

/// <summary>
/// Configuration for the cross-session memory subsystem.
/// <c>Provider</c> selects the backend: "files" for local markdown files,
/// "memorizer" for the Memorizer MCP server.
/// </summary>
public sealed class MemoryConfig
{
    /// <summary>
    /// Memory backend provider. "files" (default) uses local markdown files;
    /// "memorizer" delegates to the Memorizer MCP server.
    /// </summary>
    public string Provider { get; set; } = "files";
}
