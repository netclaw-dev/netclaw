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
                [memories — file-backed, 4 tools always available]
                Tools: find_memories, get_memories, store_memory, update_memory
                ALWAYS call find_memories at conversation start to check for relevant prior knowledge.
                Search EACH distinct topic, project, or proper noun separately — one find_memories
                call per entity. Example: "use claude-wt on the geeked-in repo" →
                find_memories("claude-wt") AND find_memories("geeked-in") in parallel.
                SAVE immediately when the user shares durable facts — their environment,
                hardware, projects, preferences, decisions, or solutions. When in doubt, save it.

                ## Two-Phase Retrieval
                1. find_memories("query") → lightweight results: IDs, titles, scores, snippets
                2. Pick the IDs you need → get_memories("id1, id2") → full content

                ## Storing Memories — Quality Bar
                BAD title: "DB fix" → GOOD: "PostgreSQL connection pooling fix for Npgsql 8.x"
                BAD content: one-liner → GOOD: markdown with ## Problem, ## Solution, code blocks, links
                Use markdown formatting. Include links to repos, PRs, docs.
                Include WHY, not just WHAT. Rich memories with context are the only useful kind.

                ## Updating Memories
                update_memory(id, old_text, new_text) — fix mistakes, update stale info.
                update_memory(id, delete="true") — remove duplicates or obsolete entries.

                For full guidance: file_read on the memory-usage skill.
                On errors, timeouts, or missing tools → file_read the self-diagnostics skill.
                """,

            MemoryContextState.MemorizerConnected => """
                [memories — Memorizer connected, 4 tools available]
                Tools: find_memories, get_memories, store_memory, update_memory
                ALWAYS call find_memories at conversation start to check for relevant prior knowledge.
                Search EACH distinct topic, project, or proper noun separately — one find_memories
                call per entity. Example: "use claude-wt on the geeked-in repo" →
                find_memories("claude-wt") AND find_memories("geeked-in") in parallel.
                SAVE immediately when the user shares durable facts — their environment,
                hardware, projects, preferences, decisions, or solutions. When in doubt, save it.

                ## Two-Phase Retrieval
                1. find_memories("query") → lightweight results: IDs, titles, similarity scores
                2. Pick the IDs you need → get_memories("id1, id2") → full content

                ## Storing Memories — Quality Bar
                BAD title: "DB fix" → GOOD: "PostgreSQL connection pooling fix for Npgsql 8.x"
                BAD content: one-liner → GOOD: markdown with ## Problem, ## Solution, code blocks, links
                Use markdown formatting. Include links to repos, PRs, docs.
                Include WHY, not just WHAT. Rich memories with context are the only useful kind.
                Note: store_memory delegates to a curation subagent (10–30s latency is normal).

                ## Updating Memories
                update_memory(id, old_text, new_text) — fix mistakes, update stale info.
                update_memory(id, delete="true") — archive duplicates or obsolete entries.

                For full guidance: file_read on the memory-usage and memorizer-usage skills.
                On errors, timeouts, or missing tools → file_read the self-diagnostics skill.
                """,

            MemoryContextState.MemorizerDisconnected => """
                [memories — NOT AVAILABLE: Memorizer MCP server not connected]
                file_read the self-diagnostics skill — run netclaw mcp list, check daemon logs, verify McpServers config.
                Save important knowledge to identity files instead. See identity-management skill.
                """,

            _ => string.Empty
        };
    }

    public string GetContextLayer() => _status;
}
