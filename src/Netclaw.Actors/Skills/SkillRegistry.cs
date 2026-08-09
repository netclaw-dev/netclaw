// -----------------------------------------------------------------------
// <copyright file="SkillRegistry.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Text;
using Netclaw.Configuration;

namespace Netclaw.Actors.Skills;

/// <summary>
/// Registry holding discovered <see cref="SkillEntry"/> items. Inventory replacements
/// are published atomically so readers observe either the old or new complete snapshot.
/// </summary>
public sealed class SkillRegistry
{
    private readonly object _writeLock = new();
    private IReadOnlyList<SkillEntry> _fileSkills = [];
    private readonly Dictionary<string, IReadOnlyList<SkillEntry>> _mcpPromptSkills =
        new(StringComparer.OrdinalIgnoreCase);
    private IReadOnlyList<SkillScanIssue> _scanIssues = [];
    private volatile Snapshot _snapshot = Snapshot.Empty;

    public void Register(SkillEntry skill)
    {
        lock (_writeLock)
        {
            if (skill.Source is McpPromptSkillSource promptSource)
            {
                var current = _mcpPromptSkills.GetValueOrDefault(promptSource.ServerName) ?? [];
                _mcpPromptSkills[promptSource.ServerName] = current.Append(skill).ToArray();
            }
            else
            {
                _fileSkills = _fileSkills.Append(skill).ToArray();
            }

            PublishCombinedSnapshot();
        }
    }

    /// <summary>
    /// Remove all registered skills so the registry can be re-populated
    /// (e.g. after a feed sync updates on-disk skill files).
    /// </summary>
    public void Clear()
    {
        lock (_writeLock)
        {
            _fileSkills = [];
            _mcpPromptSkills.Clear();
            _scanIssues = [];
            _snapshot = Snapshot.Empty;
        }
    }

    public void ReplaceAll(IEnumerable<SkillEntry> skills, IReadOnlyList<SkillScanIssue>? issues = null)
    {
        var fileSkills = skills.ToArray();
        if (fileSkills.Any(static skill => skill.Source is not FileSkillSource))
            throw new ArgumentException("The file skill inventory can contain only file skills.", nameof(skills));

        lock (_writeLock)
        {
            _fileSkills = fileSkills;
            _scanIssues = issues ?? [];
            PublishCombinedSnapshot();
        }
    }

    public IReadOnlyList<string> PublishMcpPromptSkills(string serverName, IEnumerable<SkillEntry> skills)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serverName);
        var promptSkills = ValidateMcpPromptSkills(serverName, skills);

        lock (_writeLock)
        {
            var remoteConflicts = FindMcpPromptNameConflicts(serverName, promptSkills);
            if (remoteConflicts.Count > 0)
            {
                throw new InvalidOperationException(
                    $"MCP server '{serverName}' cannot publish ambiguous logical skill name(s): "
                    + string.Join(", ", remoteConflicts));
            }

            if (promptSkills.Length == 0)
                _mcpPromptSkills.Remove(serverName);
            else
                _mcpPromptSkills[serverName] = promptSkills;

            var fileNames = new HashSet<string>(_fileSkills.Select(static skill => skill.Name),
                StringComparer.OrdinalIgnoreCase);
            var conflicts = promptSkills
                .Where(skill => fileNames.Contains(skill.Name))
                .Select(static skill => skill.Name)
                .Order(StringComparer.Ordinal)
                .ToArray();
            PublishCombinedSnapshot();
            return conflicts;
        }
    }

    public IReadOnlyList<string> GetMcpPromptNameConflicts(
        string serverName,
        IEnumerable<SkillEntry> skills)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serverName);
        var promptSkills = ValidateMcpPromptSkills(serverName, skills);

        lock (_writeLock)
            return FindMcpPromptNameConflicts(serverName, promptSkills);
    }

    public IReadOnlyList<SkillEntry> GetAll() => _snapshot.Skills;

    public SkillEntry? GetByName(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return null;

        return _snapshot.SkillsByName.TryGetValue(name.Trim(), out var skill)
            ? skill
            : null;
    }

    public SkillEntry? GetByFilePath(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath))
            return null;

        return _snapshot.SkillFiles.TryGetValue(Path.GetFullPath(filePath), out var skill)
            ? skill
            : null;
    }

    public IReadOnlyList<SkillScanIssue> GetScanIssues() => _snapshot.ScanIssues;

    /// <summary>
    /// Case-insensitive substring search against name, display name, and description.
    /// </summary>
    public IReadOnlyList<SkillEntry> Search(string query, int maxResults = 5)
    {
        if (string.IsNullOrWhiteSpace(query))
            return [];

        var queryLower = query.Trim().ToLowerInvariant();

        return _snapshot.Skills
            .Where(s =>
                s.Name.Contains(queryLower, StringComparison.OrdinalIgnoreCase)
                || s.DisplayName.Contains(queryLower, StringComparison.OrdinalIgnoreCase)
                || s.Description.Contains(queryLower, StringComparison.OrdinalIgnoreCase))
            .Take(maxResults)
            .ToList();
    }

    /// <summary>
    /// Generates a skill index for injection into the LLM context layer.
    /// Includes skill descriptions so the model knows WHEN to load each skill.
    /// Skills with <c>DisableModelInvocation</c> are excluded from the index
    /// (they remain invokable via slash commands).
    /// </summary>
    public string GenerateIndex()
        => GenerateIndex(static _ => true);

    public string GenerateIndex(Func<SkillEntry, bool> isVisible)
    {
        var skills = _snapshot.Skills;
        if (skills.Count == 0)
            return string.Empty;

        var visible = skills.Where(s => !s.DisableModelInvocation && isVisible(s)).ToList();
        if (visible.Count == 0)
            return string.Empty;

        var sb = new StringBuilder();

        sb.AppendLine("[skills]|invoke via /name");
        sb.AppendLine("|Load skill guidance with skill_load(name) BEFORE using related features.");
        sb.AppendLine("|Read bundled resources with skill_read_resource(skillName, resourcePath).");
        sb.AppendLine("|Skills routed to a subagent require a concrete task when loaded.");

        // Group by category, null category = root-level user skills
        var groups = visible
            .GroupBy(static s => s.Category ?? "user")
            .OrderBy(static g => g.Key, StringComparer.Ordinal);

        foreach (var group in groups)
        {
            sb.AppendLine($"|{group.Key}:");
            foreach (var skill in group.OrderBy(static s => s.Name, StringComparer.Ordinal))
            {
                var desc = TruncateDescription(skill.Description, maxLength: 120);
                var signature = string.IsNullOrWhiteSpace(skill.ArgumentHint)
                    ? skill.Name
                    : $"{skill.Name} {skill.ArgumentHint}";
                sb.AppendLine($"|  {signature}: {desc}");
            }
        }

        return sb.ToString();
    }

    private static string TruncateDescription(string description, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(description))
            return "(no description)";

        description = description.Trim();
        if (description.Length <= maxLength)
            return description;

        return description[..(maxLength - 3)] + "...";
    }

    private void PublishCombinedSnapshot()
    {
        var fileNames = new HashSet<string>(_fileSkills.Select(static skill => skill.Name),
            StringComparer.OrdinalIgnoreCase);
        var remoteSkills = _mcpPromptSkills
            .OrderBy(static pair => pair.Key, StringComparer.Ordinal)
            .SelectMany(static pair => pair.Value)
            .Where(skill => !fileNames.Contains(skill.Name));
        _snapshot = Snapshot.Create(_fileSkills.Concat(remoteSkills), _scanIssues);
    }

    private static SkillEntry[] ValidateMcpPromptSkills(
        string serverName,
        IEnumerable<SkillEntry> skills)
    {
        var promptSkills = skills.ToArray();
        foreach (var skill in promptSkills)
        {
            if (skill.Source is not McpPromptSkillSource source
                || !string.Equals(source.ServerName, serverName, StringComparison.OrdinalIgnoreCase))
            {
                throw new ArgumentException(
                    $"Skill '{skill.Name}' is not an MCP prompt from server '{serverName}'.",
                    nameof(skills));
            }
        }

        return promptSkills;
    }

    private IReadOnlyList<string> FindMcpPromptNameConflicts(
        string serverName,
        IReadOnlyList<SkillEntry> promptSkills)
    {
        var otherServerNames = _mcpPromptSkills
            .Where(pair => !string.Equals(pair.Key, serverName, StringComparison.OrdinalIgnoreCase))
            .SelectMany(static pair => pair.Value)
            .Select(static skill => skill.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        return promptSkills
            .Select(static skill => skill.Name)
            .GroupBy(static name => name, StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Count() > 1 || otherServerNames.Contains(group.Key))
            .Select(static group => group.Key)
            .Order(StringComparer.Ordinal)
            .ToArray();
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

        return _snapshot.SlashCommands.TryGetValue(commandName, out skill);
    }

    /// <summary>
    /// Returns all registered slash commands for error message generation.
    /// </summary>
    public IReadOnlyList<(string Command, string? ArgumentHint)> GetAvailableSlashCommands()
    {
        return _snapshot.SlashCommands.Values
            .OrderBy(s => s.Name)
            .Select(s => ($"/{s.Name}", s.ArgumentHint))
            .ToList();
    }

    private sealed record Snapshot(
        IReadOnlyList<SkillEntry> Skills,
        IReadOnlyDictionary<string, SkillEntry> SkillsByName,
        IReadOnlyDictionary<string, SkillEntry> SkillFiles,
        IReadOnlyDictionary<string, SkillEntry> SlashCommands,
        IReadOnlyList<SkillScanIssue> ScanIssues)
    {
        public static Snapshot Empty { get; } = Create([], []);

        public static Snapshot Create(
            IEnumerable<SkillEntry> skills,
            IReadOnlyList<SkillScanIssue> issues)
        {
            var skillList = skills.ToArray();
            var byName = new Dictionary<string, SkillEntry>(StringComparer.OrdinalIgnoreCase);
            var byFile = new Dictionary<string, SkillEntry>(StringComparer.OrdinalIgnoreCase);
            var slashCommands = new Dictionary<string, SkillEntry>(StringComparer.OrdinalIgnoreCase);

            foreach (var skill in skillList)
            {
                byName[skill.Name] = skill;
                if (skill.Source is FileSkillSource fileSource)
                    byFile[Path.GetFullPath(fileSource.FilePath)] = skill;
                if (skill.UserInvocable)
                    slashCommands[skill.Name] = skill;
            }

            return new Snapshot(skillList, byName, byFile, slashCommands, issues.ToArray());
        }
    }

}
