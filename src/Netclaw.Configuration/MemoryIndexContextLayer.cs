namespace Netclaw.Configuration;

/// <summary>
/// The three possible states for the memory context layer.
/// </summary>
public enum MemoryContextState
{
    /// <summary>
    /// File-backed memory using local markdown files.
    /// <c>search_memories</c> and <c>store_memory</c> are always-loaded builtins.
    /// </summary>
    FileBacked,

    /// <summary>
    /// Memorizer MCP server is configured and connected.
    /// Full graph: workspaces, projects, relationships, similarity search.
    /// </summary>
    MemorizerConnected,

    /// <summary>
    /// Memorizer MCP server is configured but not connected.
    /// </summary>
    MemorizerDisconnected
}

/// <summary>
/// Dynamic context layer that provides memory subsystem guidance to the LLM.
/// Updated after MCP discovery completes based on the configured provider
/// and Memorizer connectivity status.
/// </summary>
public sealed class MemoryIndexContextLayer : IContextLayerProvider
{
    private volatile string _status = string.Empty;

    /// <summary>
    /// Update the memory context layer based on the resolved state.
    /// </summary>
    public void Update(MemoryContextState state)
    {
        _status = state switch
        {
            MemoryContextState.FileBacked => """
                [memories — file-backed, search_memories and store_memory always available]
                RETRIEVE at conversation start. SAVE immediately when learning something durable.
                For full guidance: file_read on the memory-usage skill.
                """,

            MemoryContextState.MemorizerConnected => """
                [memories — Memorizer connected, search_memories and store_memory available]
                RETRIEVE at conversation start. SAVE immediately when learning something durable.
                For full guidance: file_read on the memory-usage and memorizer-usage skills.
                """,

            MemoryContextState.MemorizerDisconnected => """
                [memories — NOT AVAILABLE: Memorizer MCP server not connected]
                Troubleshoot: check McpServers in netclaw.json, verify server running, check daemon logs.
                Save important knowledge to identity files instead. See identity-management skill.
                """,

            _ => string.Empty
        };
    }

    public string GetContextLayer() => _status;
}
