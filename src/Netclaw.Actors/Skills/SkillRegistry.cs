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
    /// </summary>
    public string GenerateCompressedIndex()
    {
        if (_skills.Count == 0)
            return string.Empty;

        var sb = new StringBuilder();
        sb.AppendLine("[skills — use search_skills to load full text]");
        sb.Append("available: ");
        sb.AppendLine(string.Join(", ", _skills.Select(s => s.Name)));
        return sb.ToString();
    }
}
