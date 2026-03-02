namespace Netclaw.Configuration;

/// <summary>
/// Context layer providing behavioral guidance for memory triage.
/// Teaches the agent WHERE to save different kinds of information.
/// Conditional: omits Memorizer guidance when it is not connected.
/// </summary>
public sealed class MemoryTriageContextLayer : IContextLayerProvider
{
    private volatile string _content = string.Empty;

    /// <summary>
    /// Update triage guidance based on Memorizer availability.
    /// </summary>
    public void Update(bool memorizerConnected)
    {
        if (memorizerConnected)
        {
            _content = """
                [memory triage — where to save what you learn]
                When you learn something important, save it immediately to the right place:

                IDENTITY FILES (always loaded, use file_read then file_write):
                  SOUL.md  — User's name, family, key relationships, preferences, timezone.
                             This is your mental model of who you serve. Keep it small and high-signal.
                  AGENTS.md — Your behavioral rules, workflow preferences, operating guidelines.
                  TOOLING.md — Environment capabilities, installed tools, MCP server notes.

                MEMORIZER (cross-session, use search_memories / memorizer tools):
                  World knowledge, project details, solutions to problems, research findings,
                  factual context that doesn't define who the user IS but what you've learned
                  while working together.

                SKILL FILES (reusable procedures, use file_write to ~/.netclaw/skills/):
                  Workflows, procedures, and how-to instructions you develop through experience.
                  If you solve something that could be reused, write a skill file.

                RULE: Before saving, load search_skills for "identity-management" to review
                full guidance on what belongs where.
                """;
        }
        else
        {
            _content = """
                [memory triage — where to save what you learn]
                When you learn something important, save it to the right place:

                IDENTITY FILES (always loaded, use file_read then file_write):
                  SOUL.md  — User's name, family, key relationships, preferences, timezone.
                  AGENTS.md — Your behavioral rules, workflow preferences, operating guidelines.
                  TOOLING.md — Environment capabilities, installed tools, MCP server notes.

                SKILL FILES (reusable procedures, use file_write to ~/.netclaw/skills/):
                  Workflows, procedures, and how-to instructions you develop through experience.

                Note: Memorizer is not connected. Cross-session memory is unavailable.
                Save important knowledge to identity files or skill files instead.
                """;
        }
    }

    public string GetContextLayer() => _content;
}
