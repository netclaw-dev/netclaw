namespace Netclaw.Configuration;

/// <summary>
/// Dynamic context layer that indicates Memorizer availability status.
/// Updated after MCP discovery completes.
/// </summary>
public sealed class MemoryIndexContextLayer : IContextLayerProvider
{
    private volatile string _status = string.Empty;

    /// <summary>
    /// Update the Memorizer status. Call with connected=true after
    /// Memorizer MCP tools are registered, or connected=false otherwise.
    /// </summary>
    public void Update(bool connected)
    {
        _status = connected
            ? """
              [memories — cross-session knowledge via Memorizer]
              Use search_memories to recall prior knowledge. Memorizer organizes knowledge in
              workspaces (domains) → projects (goals) → memories (documents or records).
              Assign memories to the right workspace/project when saving.
              For full guidance: file_read ~/.netclaw/skills/memorizer-usage.md
              """
            : """
              [memories — NOT AVAILABLE: Memorizer MCP server not connected]
              Cross-session memory is unavailable. Save important knowledge to identity
              files or skill files instead.
              """;
    }

    public string GetContextLayer() => _status;
}
