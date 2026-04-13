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
    private volatile IReadOnlyList<SkillScanIssue> _scanIssues = [];

    /// <summary>
    /// Slash-command dispatch map: skill name → entry, for skills where
    /// <see cref="SkillEntry.UserInvocable"/> is true.
    /// </summary>
    private readonly Dictionary<string, SkillEntry> _slashCommands = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, SkillEntry> _skillFiles = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, SkillEntry> _skillsByName = new(StringComparer.OrdinalIgnoreCase);

    public void Register(SkillEntry skill)
    {
        _skills.Add(skill);
        _skillsByName[skill.Name] = skill;
        _skillFiles[Path.GetFullPath(skill.FilePath)] = skill;
        if (skill.UserInvocable)
            _slashCommands[skill.Name] = skill;
    }

    /// <summary>
    /// Remove all registered skills so the registry can be re-populated
    /// (e.g. after a feed sync updates on-disk skill files).
    /// </summary>
    public void Clear()
    {
        _skills.Clear();
        _slashCommands.Clear();
        _skillFiles.Clear();
        _skillsByName.Clear();
        _scanIssues = [];
    }

    public void ReplaceAll(IEnumerable<SkillEntry> skills, IReadOnlyList<SkillScanIssue>? issues = null)
    {
        Clear();
        foreach (var skill in skills)
            Register(skill);

        _scanIssues = issues ?? [];
    }

    public IReadOnlyList<SkillEntry> GetAll() => _skills;

    public SkillEntry? GetByName(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return null;

        return _skillsByName.TryGetValue(name.Trim(), out var skill)
            ? skill
            : null;
    }

    public SkillEntry? GetByFilePath(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath))
            return null;

        return _skillFiles.TryGetValue(Path.GetFullPath(filePath), out var skill)
            ? skill
            : null;
    }

    public IReadOnlyList<SkillScanIssue> GetScanIssues() => _scanIssues;

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
    /// Generates a compressed pipe-delimited skill index for injection into the
    /// LLM context layer. Points the agent directly at files on disk so it can
    /// read them with <c>file_read</c> — no tool invocation decision required.
    /// Skills with <c>DisableModelInvocation</c> are excluded from the index
    /// (they remain invokable via slash commands).
    /// </summary>
    public string GenerateIndex(string skillsRoot, IReadOnlyList<ResolvedExternalSource>? externalSources = null)
    {
        if (_skills.Count == 0)
            return string.Empty;

        var visible = _skills.Where(static s => !s.DisableModelInvocation).ToList();
        if (visible.Count == 0)
            return string.Empty;

        var sb = new StringBuilder();

        // Build roots header — single root for backward compat, multi-root when external sources exist.
        // An external source may cover multiple paths; join them with ';' so the agent sees every root.
        var hasExternal = externalSources is { Count: > 0 };
        if (hasExternal)
        {
            sb.Append($"[skills]|roots: native={skillsRoot}");
            foreach (var src in externalSources!)
                sb.Append($",{src.Name}={string.Join(';', src.Paths)}");
            sb.AppendLine("|invoke via /name");
        }
        else
        {
            sb.AppendLine($"[skills]|root: {skillsRoot}|invoke via /name");
        }

        sb.AppendLine("|IMPORTANT: Prefer retrieval-led reasoning over pre-training-led reasoning");

        // Group by category, null category = root-level user skills
        var groups = visible
            .GroupBy(static s => s.Category ?? "user")
            .OrderBy(static g => g.Key, StringComparer.Ordinal);

        foreach (var group in groups)
        {
            var files = string.Join(",", group
                .OrderBy(static s => s.Name, StringComparer.Ordinal)
                .Select(static s => $"{s.Name}/SKILL.md"));
            sb.AppendLine($"|{group.Key}:{{{files}}}");
        }

        return sb.ToString();
    }


    // --- Slash-command dispatch ---

    /// <summary>
    /// Attempts to resolve a slash command from user input.
    /// Returns true if the input starts with / and matches a registered skill name.
    /// </summary>
    public bool TryResolveSlashCommand(string input, out SkillEntry? skill, out string remainder)
    {
        skill = null;
        remainder = string.Empty;

        if (string.IsNullOrWhiteSpace(input) || input[0] != '/')
            return false;

        var trimmed = input[1..]; // strip leading /
        var spaceIndex = trimmed.IndexOf(' ', StringComparison.Ordinal);
        var commandName = spaceIndex >= 0 ? trimmed[..spaceIndex] : trimmed;
        remainder = spaceIndex >= 0 ? trimmed[(spaceIndex + 1)..].Trim() : string.Empty;

        return _slashCommands.TryGetValue(commandName, out skill);
    }

    /// <summary>
    /// Returns all registered slash commands for error message generation.
    /// </summary>
    public IReadOnlyList<(string Command, string? ArgumentHint)> GetAvailableSlashCommands()
    {
        return _slashCommands.Values
            .OrderBy(s => s.Name)
            .Select(s => ($"/{s.Name}", s.ArgumentHint))
            .ToList();
    }
}
