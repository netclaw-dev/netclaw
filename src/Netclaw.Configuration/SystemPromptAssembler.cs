namespace Netclaw.Configuration;

/// <summary>
/// Pure function that assembles a system prompt from layered content sources.
/// Layers are concatenated in order, with null/empty layers silently skipped.
/// No file I/O — receives pre-loaded content strings.
/// </summary>
public static class SystemPromptAssembler
{
    /// <summary>
    /// Assemble a system prompt from layered content sources.
    /// Each non-null, non-whitespace layer is included as a section.
    /// </summary>
    /// <param name="personality">Agent character, tone, values, and boundaries (PERSONALITY.md).</param>
    /// <param name="instructions">Operating rules and behavioral guidelines (INSTRUCTIONS.md).</param>
    /// <param name="userPreferences">Owner preferences, timezone, communication style (USER.md).</param>
    /// <param name="projectAgents">Project-specific instructions from AGENTS.md overlay.</param>
    /// <returns>Assembled prompt or empty string if all layers are missing.</returns>
    public static string Assemble(
        string? personality = null,
        string? instructions = null,
        string? userPreferences = null,
        string? projectAgents = null)
    {
        var sections = new List<string>(4);

        AddSection(sections, personality);
        AddSection(sections, instructions);
        AddSection(sections, userPreferences);
        AddSection(sections, projectAgents);

        return sections.Count > 0
            ? string.Join("\n\n", sections)
            : string.Empty;
    }

    private static void AddSection(List<string> sections, string? content)
    {
        if (!string.IsNullOrWhiteSpace(content))
            sections.Add(content.Trim());
    }
}
