// -----------------------------------------------------------------------
// <copyright file="SystemPromptAssembler.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
namespace Netclaw.Configuration;

/// <summary>
/// Pure function that assembles a system prompt from layered content sources.
/// Layers are concatenated in order, with null/empty layers silently skipped.
/// No file I/O — receives pre-loaded content strings.
/// </summary>
public static class SystemPromptAssembler
{
    /// <summary>
    /// Assemble a system prompt from identity file layers.
    /// Each non-null, non-whitespace layer is included as a section.
    /// </summary>
    /// <param name="soul">Agent personality, tone, user's name, communication style (SOUL.md).</param>
    /// <param name="agents">Agent purpose/mission, operating rules, meta-guidance (AGENTS.md).</param>
    /// <param name="tooling">Host environment execution capabilities (TOOLING.md).</param>
    /// <param name="projectInstructions">Project-scoped identity content from the project directory.</param>
    /// <returns>Assembled prompt or empty string if all layers are missing.</returns>
    public static string Assemble(
        string? soul = null,
        string? agents = null,
        string? tooling = null,
        string? projectInstructions = null)
    {
        var sections = new List<string>(4);

        AddSection(sections, soul);
        AddSection(sections, agents);
        AddSection(sections, tooling);
        AddSection(sections, projectInstructions);

        return sections.Count > 0
            ? string.Join("\n\n", sections)
            : string.Empty;
    }

    /// <summary>
    /// Legacy overload for migration compatibility. Assembles from old-style parameter names.
    /// </summary>
    public static string AssembleLegacy(
        string? personality = null,
        string? instructions = null,
        string? userPreferences = null)
    {
        return Assemble(soul: personality, agents: instructions, tooling: userPreferences);
    }

    private static void AddSection(List<string> sections, string? content)
    {
        if (!string.IsNullOrWhiteSpace(content))
            sections.Add(content.Trim());
    }
}
