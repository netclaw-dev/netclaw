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
                [memories — file-backed cross-session knowledge]

                RETRIEVE: At the start of each conversation, search_memories for topics
                relevant to the user's first message. Also search when asked about something
                you might have encountered before. Check before answering from scratch.

                SAVE: When you learn something worth remembering across sessions, save it
                immediately using store_memory. Write rich content: use markdown, include
                code blocks, provide full context. Good memories are self-contained — a future
                agent reading them should understand without the original conversation.

                What to save: solutions to problems, user-confirmed facts, architecture
                decisions, research findings, troubleshooting steps, project context.

                What NOT to save: ephemeral task state, things already in identity files,
                unverified guesses, duplicate information (search first).

                Tags: reference, how-to, decision, troubleshooting, coding-standard.

                The memory index at ~/.netclaw/memories/memory.md lists all stored memories.
                """,

            MemoryContextState.MemorizerConnected => """
                [memories — cross-session knowledge via Memorizer]

                RETRIEVE: At the start of each conversation, search_memories for topics
                relevant to the user's first message. Also search when asked about something
                you might have encountered before. Check before answering from scratch.

                SAVE: When you learn something worth remembering across sessions, save it
                immediately using memorizer/store. Write rich content: use markdown, include
                code blocks, provide full context. Good memories are self-contained — a future
                agent reading them should understand without the original conversation.

                What to save: solutions to problems, user-confirmed facts, architecture
                decisions, research findings, troubleshooting steps, project context.

                What NOT to save: ephemeral task state, things already in identity files,
                unverified guesses, duplicate information (search first).

                Organization: workspaces (domains) → projects (goals) → memories (documents
                or records). Assign to the right workspace/project. Use tags: reference,
                how-to, decision, troubleshooting, coding-standard.

                For full guidance: file_read ~/.netclaw/skills/memorizer-usage.md
                """,

            MemoryContextState.MemorizerDisconnected => """
                [memories — NOT AVAILABLE: Memorizer MCP server not connected]

                Cross-session memory is configured to use Memorizer, but the MCP server is not
                connected. Troubleshooting:
                1. Check McpServers configuration in netclaw.json
                2. Verify the Memorizer server is running
                3. Check daemon logs for MCP connection errors

                Save important knowledge to identity files (SOUL.md, AGENTS.md, TOOLING.md)
                or skill files (~/.netclaw/skills/) instead.
                See identity-management skill for triage guidance.
                """,

            _ => string.Empty
        };
    }

    public string GetContextLayer() => _status;
}
