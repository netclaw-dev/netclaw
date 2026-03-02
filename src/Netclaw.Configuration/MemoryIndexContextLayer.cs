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
              """
            : """
              [memories — NOT AVAILABLE: Memorizer MCP server not connected]
              Cross-session memory is unavailable. Save important knowledge to identity
              files (SOUL.md, AGENTS.md, TOOLING.md) or skill files (~/.netclaw/skills/)
              instead. See identity-management skill for triage guidance.
              """;
    }

    public string GetContextLayer() => _status;
}
