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
    private volatile Snapshot _snapshot = Snapshot.Empty;

    public void Register(SkillEntry skill)
    {
        lock (_writeLock)
        {
            var current = _snapshot;
            _snapshot = Snapshot.Create(current.Skills.Append(skill), current.ScanIssues);
        }
    }

    /// <summary>
    /// Remove all registered skills so the registry can be re-populated
    /// (e.g. after a feed sync updates on-disk skill files).
    /// </summary>
    public void Clear()
    {
        lock (_writeLock)
            _snapshot = Snapshot.Empty;
    }

    public void ReplaceAll(IEnumerable<SkillEntry> skills, IReadOnlyList<SkillScanIssue>? issues = null)
    {
        lock (_writeLock)
            _snapshot = Snapshot.Create(skills, issues ?? []);
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
    {
        var skills = _snapshot.Skills;
        if (skills.Count == 0)
            return string.Empty;

        var visible = skills.Where(static s => !s.DisableModelInvocation).ToList();
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
                sb.AppendLine($"|  {skill.Name}: {desc}");
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
                byFile[Path.GetFullPath(skill.FilePath)] = skill;
                if (skill.UserInvocable)
                    slashCommands[skill.Name] = skill;
            }

            return new Snapshot(skillList, byName, byFile, slashCommands, issues.ToArray());
        }
    }

}
