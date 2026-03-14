using System.Text;
using Netclaw.Actors.Text;
using Netclaw.Configuration;

namespace Netclaw.Actors.Skills;

/// <summary>
/// Mutable registry holding discovered <see cref="SkillEntry"/> items.
/// Follows the same pattern as <see cref="Tools.ToolRegistry"/>.
/// </summary>
public sealed class SkillRegistry
{
    private readonly List<SkillEntry> _skills = new();
    private readonly Dictionary<string, HashSet<string>> _enrichedKeywords = new(StringComparer.OrdinalIgnoreCase);

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
        _enrichedKeywords.Clear();
    }

    public IReadOnlyList<SkillEntry> GetAll() => _skills;

    /// <summary>
    /// Store enriched keywords for a skill. Called by the enrichment service
    /// after sidecar LLM generates keywords from the skill's content.
    /// </summary>
    public void SetEnrichedKeywords(string skillName, HashSet<string> keywords)
    {
        _enrichedKeywords[skillName] = keywords;
    }

    /// <summary>
    /// Get all enriched keyword sets indexed by skill name.
    /// </summary>
    public IReadOnlyDictionary<string, HashSet<string>> GetEnrichedKeywords()
        => _enrichedKeywords;

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
                || s.Description.Contains(queryLower, StringComparison.OrdinalIgnoreCase)
                || (s.Triggers is not null && s.Triggers.Contains(queryLower, StringComparison.OrdinalIgnoreCase)))
            .Take(maxResults)
            .ToList();
    }

    /// <summary>
    /// Score each skill's enriched keywords against user message tokens using
    /// set intersection. Returns skills with overlap count >= threshold,
    /// sorted by score descending. Skills without enriched keywords are skipped.
    /// </summary>
    public IReadOnlyList<(SkillEntry Skill, int Score)> MatchByKeywords(
        string userMessage,
        IReadOnlySet<string>? excludeNames = null,
        int threshold = 2,
        int maxResults = 3)
    {
        if (string.IsNullOrWhiteSpace(userMessage) || _enrichedKeywords.Count == 0)
            return [];

        var userTokens = TextTokenizer.Tokenize(userMessage).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (userTokens.Count == 0)
            return [];

        var candidates = new List<(SkillEntry Skill, int Score)>();

        foreach (var skill in _skills)
        {
            if (excludeNames is not null && excludeNames.Contains(skill.Name))
                continue;

            if (!_enrichedKeywords.TryGetValue(skill.Name, out var keywords))
                continue;

            var overlap = userTokens.Count(t => keywords.Contains(t));
            if (overlap >= threshold)
                candidates.Add((skill, overlap));
        }

        return candidates
            .OrderByDescending(c => c.Score)
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
        sb.AppendLine("[skills — LOAD these with file_read when your current situation matches a trigger]");
        foreach (var skill in _skills)
        {
            sb.AppendLine($"{skill.Name} ({skill.FilePath})");
            sb.AppendLine($"  {skill.Description}");
            if (skill.Triggers is not null)
                sb.AppendLine($"  LOAD WHEN: {skill.Triggers}");
            if (skill.ResourcePaths is { Count: > 0 })
                sb.AppendLine($"  [{skill.ResourcePaths.Count} resources in {skill.SkillDirectory}]");
        }

        return sb.ToString();
    }
}
