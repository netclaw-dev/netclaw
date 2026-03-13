using System.Text.RegularExpressions;
using Netclaw.Configuration;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace Netclaw.Actors.Skills;

/// <summary>
/// Discovers skills under the skills directory using the AgentSkills.io
/// directory layout: each skill is a directory containing <c>SKILL.md</c>
/// with YAML frontmatter. Optional subdirectories (<c>scripts/</c>,
/// <c>references/</c>, <c>assets/</c>) hold progressive-disclosure resources.
/// </summary>
public static partial class SkillScanner
{
    private const int MaxDescriptionLength = 1024; // AgentSkills.io spec limit
    private const int MaxTriggersLength = 300;
    private const string SkillFileName = "SKILL.md";

    /// <summary>
    /// Standard subdirectories within a skill directory that contain resources.
    /// </summary>
    private static readonly string[] ResourceSubdirectories = ["scripts", "references", "assets"];

    [GeneratedRegex(@"^#\s+(.+)$", RegexOptions.Multiline)]
    private static partial Regex HeadingRegex();

    private static readonly IDeserializer YamlDeserializer = new DeserializerBuilder()
        .WithNamingConvention(HyphenatedNamingConvention.Instance)
        .IgnoreUnmatchedProperties()
        .Build();

    /// <summary>
    /// Scan the skills directory and return metadata for each skill found.
    /// Returns an empty list if the directory does not exist.
    /// </summary>
    public static IReadOnlyList<SkillEntry> Scan(string skillsDirectory)
    {
        if (!Directory.Exists(skillsDirectory))
            return [];

        var rootFull = Path.GetFullPath(skillsDirectory);
        var entries = new List<SkillEntry>();
        var seenNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Pass 1: root-level directory skills (skill-name/SKILL.md)
        foreach (var dir in Directory.GetDirectories(rootFull))
        {
            var skillMdPath = Path.Combine(dir, SkillFileName);
            if (!File.Exists(skillMdPath))
                continue;

            var entry = ParseSkillFile(skillMdPath, rootFull);
            if (entry is not null)
            {
                entries.Add(entry);
                seenNames.Add(entry.Name);
            }
        }

        // Pass 2: nested directory skills (.system/skill-name/SKILL.md, category/skill-name/SKILL.md)
        foreach (var subDir in Directory.GetDirectories(rootFull))
        {
            var subDirName = Path.GetFileName(subDir);

            // Skip directories that are skills themselves (have SKILL.md — handled in Pass 1)
            if (File.Exists(Path.Combine(subDir, SkillFileName)))
                continue;

            // Skip hidden directories (except .system/ which contains system skills)
            if (subDirName.StartsWith('.') && !subDirName.Equals(".system", StringComparison.Ordinal))
                continue;

            foreach (var nestedDir in Directory.GetDirectories(subDir))
            {
                var skillMdPath = Path.Combine(nestedDir, SkillFileName);
                if (!File.Exists(skillMdPath))
                    continue;

                var name = Path.GetFileName(nestedDir).ToLowerInvariant();
                if (seenNames.Contains(name))
                    continue;

                var entry = ParseSkillFile(skillMdPath, rootFull);
                if (entry is not null)
                {
                    entries.Add(entry);
                    seenNames.Add(entry.Name);
                }
            }
        }

        entries.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.Ordinal));
        return entries;
    }

    private static SkillEntry? ParseSkillFile(string filePath, string rootDirectory)
    {
        string content;
        try
        {
            content = File.ReadAllText(filePath);
        }
        catch (IOException)
        {
            return null;
        }

        var frontmatter = ExtractFrontmatter(content);
        if (frontmatter is null)
            return null;

        return BuildEntryFromFrontmatter(frontmatter, content, filePath, rootDirectory);
    }

    /// <summary>
    /// Extracts and deserializes YAML frontmatter from a skill file.
    /// Returns null if the file does not start with <c>---</c> or the YAML is unparseable.
    /// </summary>
    internal static SkillFrontmatter? ExtractFrontmatter(string content)
    {
        if (!content.StartsWith("---", StringComparison.Ordinal))
            return null;

        // Find the closing --- delimiter
        var closingIndex = content.IndexOf("\n---", 3, StringComparison.Ordinal);
        if (closingIndex < 0)
            return null;

        var yamlBlock = content[(content.IndexOf('\n', 0) + 1)..closingIndex];

        try
        {
            return YamlDeserializer.Deserialize<SkillFrontmatter>(yamlBlock);
        }
        catch (YamlDotNet.Core.YamlException)
        {
            return null;
        }
    }

    /// <summary>
    /// Extracts the markdown body after the YAML frontmatter closing delimiter.
    /// </summary>
    internal static string ExtractBody(string content)
    {
        var closingIndex = content.IndexOf("\n---", 3, StringComparison.Ordinal);
        if (closingIndex < 0)
            return content;

        var bodyStart = content.IndexOf('\n', closingIndex + 4);
        return bodyStart < 0 ? string.Empty : content[(bodyStart + 1)..].TrimStart();
    }

    private static SkillEntry? BuildEntryFromFrontmatter(
        SkillFrontmatter fm,
        string content,
        string filePath,
        string rootDirectory)
    {
        // Description is required per AgentSkills.io spec
        if (string.IsNullOrWhiteSpace(fm.Description))
            return null;

        var skillDirectory = Path.GetDirectoryName(filePath)!;

        var name = !string.IsNullOrWhiteSpace(fm.Name)
            ? fm.Name.Trim().ToLowerInvariant()
            : Path.GetFileName(skillDirectory).ToLowerInvariant();

        // Extract display name from first # heading in the body
        var body = ExtractBody(content);
        var headingMatch = HeadingRegex().Match(body);
        var displayName = headingMatch.Success
            ? headingMatch.Groups[1].Value.Trim()
            : TitleCase(name);

        // Resolve category from directory structure
        // For skill at root/skill-name/SKILL.md → category is null
        // For skill at root/category/skill-name/SKILL.md → category is "category"
        var relativePath = Path.GetRelativePath(rootDirectory, filePath);
        string? category = null;
        var parentOfSkillDir = Path.GetDirectoryName(Path.GetDirectoryName(relativePath));
        if (!string.IsNullOrEmpty(parentOfSkillDir) && parentOfSkillDir != ".")
            category = parentOfSkillDir.Replace(Path.DirectorySeparatorChar, '/');

        // Extract triggers from metadata
        string? triggers = null;
        if (fm.Metadata is not null && fm.Metadata.TryGetValue("triggers", out var triggersValue))
            triggers = Truncate(triggersValue, MaxTriggersLength);

        // Extract version from metadata
        string? version = null;
        if (fm.Metadata is not null && fm.Metadata.TryGetValue("version", out var versionValue))
            version = versionValue;

        return new SkillEntry(
            Name: name,
            DisplayName: displayName,
            Description: Truncate(fm.Description.Trim(), MaxDescriptionLength),
            FilePath: filePath,
            SkillDirectory: skillDirectory,
            Category: category)
        {
            Triggers = triggers,
            Version = version,
            License = fm.License,
            Compatibility = fm.Compatibility,
            AllowedTools = fm.AllowedTools,
            ResourcePaths = EnumerateResources(skillDirectory)
        };
    }

    /// <summary>
    /// Enumerates resource files in standard subdirectories of a skill directory.
    /// Returns null if no resources are found.
    /// </summary>
    private static IReadOnlyList<string>? EnumerateResources(string skillDirectory)
    {
        List<string>? resources = null;

        foreach (var subDirName in ResourceSubdirectories)
        {
            var subDir = Path.Combine(skillDirectory, subDirName);
            if (!Directory.Exists(subDir))
                continue;

            foreach (var file in Directory.GetFiles(subDir, "*", SearchOption.AllDirectories))
            {
                var relativePath = Path.GetRelativePath(skillDirectory, file)
                    .Replace(Path.DirectorySeparatorChar, '/');
                resources ??= [];
                resources.Add(relativePath);
            }
        }

        return resources;
    }

    private static string TitleCase(string kebabName)
    {
        var parts = kebabName.Split('-', '_');
        for (var i = 0; i < parts.Length; i++)
        {
            if (parts[i].Length > 0)
                parts[i] = char.ToUpperInvariant(parts[i][0]) + parts[i][1..];
        }
        return string.Join(' ', parts);
    }

    private static string Truncate(string value, int maxLength)
    {
        if (value.Length <= maxLength)
            return value;
        return value[..(maxLength - 3)] + "...";
    }
}

/// <summary>
/// YAML frontmatter schema for AgentSkills.io SKILL.md files.
/// </summary>
internal sealed class SkillFrontmatter
{
    public string? Name { get; set; }
    public string? Description { get; set; }
    public string? License { get; set; }
    public string? Compatibility { get; set; }

    [YamlMember(Alias = "allowed-tools")]
    public string? AllowedTools { get; set; }

    public Dictionary<string, string>? Metadata { get; set; }
}
