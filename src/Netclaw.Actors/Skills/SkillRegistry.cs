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
    private readonly Dictionary<string, SkillKeywordIndex> _enrichedKeywords = new(StringComparer.OrdinalIgnoreCase);

    private static readonly IReadOnlyDictionary<string, double> ThresholdOverrides =
        new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase)
        {
            ["netclaw-diagnostics"] = 3.0,
            ["netclaw-manual"] = 2.5,
            ["netclaw-memory"] = 3.0,
            ["netclaw-identity"] = 3.0,
            ["skill-authoring"] = 3.0
        };

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
    public void SetEnrichedKeywords(
        string skillName,
        HashSet<string> keywords,
        HashSet<string>? phrases = null,
        double? threshold = null)
    {
        _enrichedKeywords[skillName] = new SkillKeywordIndex(
            keywords,
            phrases ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            threshold ?? ResolveThreshold(skillName));
    }

    /// <summary>
    /// Get all enriched keyword sets indexed by skill name.
    /// </summary>
    public IReadOnlyDictionary<string, SkillKeywordIndex> GetEnrichedKeywords()
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
    public IReadOnlyList<SkillMatchResult> MatchByKeywords(
        string userMessage,
        IReadOnlySet<string>? excludeNames = null,
        int maxResults = 3)
    {
        if (string.IsNullOrWhiteSpace(userMessage) || _enrichedKeywords.Count == 0)
            return [];

        var userTokenList = TextTokenizer.Tokenize(userMessage);
        if (userTokenList.Count == 0)
            return [];

        var userTokens = userTokenList.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var userBigrams = TextTokenizer.MakeBigrams(userTokenList).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var documentFrequency = BuildTokenDocumentFrequency();
        var candidates = new List<SkillMatchResult>();

        foreach (var skill in _skills)
        {
            if (excludeNames is not null && excludeNames.Contains(skill.Name))
                continue;

            if (!_enrichedKeywords.TryGetValue(skill.Name, out var keywordIndex))
                continue;

            var matchedTokens = userTokens
                .Where(t => keywordIndex.Keywords.Contains(t))
                .OrderBy(t => t, StringComparer.OrdinalIgnoreCase)
                .ToArray();

            var matchedPhrases = userBigrams
                .Where(p => keywordIndex.Phrases.Contains(p))
                .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
                .ToArray();

            var tokenScore = matchedTokens.Sum(t => GetTokenWeight(t, documentFrequency));
            var phraseScore = matchedPhrases.Length * 1.5;
            var totalScore = tokenScore + phraseScore;

            if (totalScore >= keywordIndex.Threshold)
            {
                candidates.Add(new SkillMatchResult(
                    skill,
                    totalScore,
                    matchedTokens.Length,
                    matchedPhrases.Length,
                    keywordIndex.Threshold,
                    matchedTokens,
                    matchedPhrases));
            }
        }

        return candidates
            .OrderByDescending(c => c.Score)
            .ThenByDescending(c => c.PhraseHits)
            .ThenByDescending(c => c.TokenHits)
            .Take(maxResults)
            .ToList();
    }

    private Dictionary<string, int> BuildTokenDocumentFrequency()
    {
        var frequency = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        foreach (var keywordIndex in _enrichedKeywords.Values)
        {
            foreach (var token in keywordIndex.Keywords)
            {
                frequency[token] = frequency.TryGetValue(token, out var count)
                    ? count + 1
                    : 1;
            }
        }

        return frequency;
    }

    private static double GetTokenWeight(string token, IReadOnlyDictionary<string, int> documentFrequency)
    {
        if (!documentFrequency.TryGetValue(token, out var frequency) || frequency <= 1)
            return 1.0;

        if (frequency == 2)
            return 0.75;

        return 0.5;
    }

    private static double ResolveThreshold(string skillName)
        => ThresholdOverrides.TryGetValue(skillName, out var threshold) ? threshold : 2.0;

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

public sealed record SkillKeywordIndex(
    IReadOnlySet<string> Keywords,
    IReadOnlySet<string> Phrases,
    double Threshold);

public sealed record SkillMatchResult(
    SkillEntry Skill,
    double Score,
    int TokenHits,
    int PhraseHits,
    double Threshold,
    IReadOnlyList<string> MatchedTokens,
    IReadOnlyList<string> MatchedPhrases);
