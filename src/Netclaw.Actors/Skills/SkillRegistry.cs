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
    /// Produces a compressed index for the system prompt context layer.
    /// Lists each skill with its file path and description so the agent
    /// can use <c>file_read</c> directly — no search tool required.
    /// </summary>
    public string GenerateCompressedIndex()
    {
        if (_skills.Count == 0)
            return string.Empty;

        var sb = new StringBuilder();
        sb.AppendLine("[skills — read with file_read for full instructions]");
        foreach (var skill in _skills)
        {
            sb.AppendLine($"{skill.Name} ({skill.FilePath})");
            sb.AppendLine($"  {skill.Description}");
        }

        return sb.ToString();
    }
}
