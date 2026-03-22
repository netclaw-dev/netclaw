using System.Text;
using Netclaw.Configuration;

namespace Netclaw.Actors.Skills;

/// <summary>
/// Mutable registry holding discovered <see cref="SkillEntry"/> items.
/// Follows the same pattern as <see cref="Tools.ToolRegistry"/>.
/// </summary>
public sealed class SkillRegistry
{
    private readonly List<SkillEntry> _skills = new();

    public void Register(SkillEntry skill)
    {
        _skills.Add(skill);
    }

    /// <summary>
    /// Remove all registered skills so the registry can be re-populated
    /// (e.g. after a feed sync updates on-disk skill files).
    /// </summary>
    public void Clear()
    {
        _skills.Clear();
    }

    public IReadOnlyList<SkillEntry> GetAll() => _skills;

    /// <summary>
    /// Case-insensitive substring search against name, display name, and description.
    /// </summary>
    public IReadOnlyList<SkillEntry> Search(string query, int maxResults = 5)
    {
        if (string.IsNullOrWhiteSpace(query))
            return [];

        var queryLower = query.Trim().ToLowerInvariant();

        return _skills
            .Where(s =>
                s.Name.Contains(queryLower, StringComparison.OrdinalIgnoreCase)
                || s.DisplayName.Contains(queryLower, StringComparison.OrdinalIgnoreCase)
                || s.Description.Contains(queryLower, StringComparison.OrdinalIgnoreCase))
            .Take(maxResults)
            .ToList();
    }

    /// <summary>
    /// Produces a description menu for the system prompt context layer.
    /// The LLM reads descriptions and loads skills via <c>file_read</c>.
    /// No keyword matching — the LLM decides which skill to load.
    /// </summary>
    public string GenerateDescriptionMenu()
    {
        if (_skills.Count == 0)
            return string.Empty;

        var sb = new StringBuilder();
        sb.AppendLine("[available-skills — MANDATORY FIRST STEP]");
        sb.AppendLine("Before using ANY tool or generating a response, you MUST check this list.");
        sb.AppendLine("If the user's request touches a skill's domain, call file_read on its path");
        sb.AppendLine("as your FIRST action. Do NOT call other tools first. Skills contain");
        sb.AppendLine("project-specific rules, required citation formats, memory policies, and");
        sb.AppendLine("operational constraints that override your defaults. Skipping a skill");
        sb.AppendLine("means you will miss required behavior and produce incorrect output.");
        sb.AppendLine("When a skill references additional files (in references/ or scripts/),");
        sb.AppendLine("load those too if they match the user's specific request.");
        sb.AppendLine();
        foreach (var skill in _skills)
        {
            sb.AppendLine($"- {skill.Name}: {skill.Description}");
            sb.AppendLine($"  path: {skill.FilePath}");
            if (skill.ResourcePaths is { Count: > 0 })
                sb.AppendLine($"  resources: [{skill.ResourcePaths.Count} files in {skill.SkillDirectory}]");
        }

        return sb.ToString();
    }
}
