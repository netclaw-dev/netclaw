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
              [memories — use search_memories to recall prior knowledge]
              Memory store available via Memorizer. Use search_memories when you need to
              recall information from prior sessions, saved knowledge, or project context.
              """
            : "[memories — NOT AVAILABLE: Memorizer MCP server not connected]";
    }

    public string GetContextLayer() => _status;
}
