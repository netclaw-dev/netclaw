using System.Text.RegularExpressions;
using Netclaw.Configuration;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace Netclaw.Actors.Skills;

/// <summary>
/// Discovers skill files recursively under the skills directory.
/// Supports two layouts:
/// <list type="bullet">
///   <item>Directory-based: <c>skill-name/SKILL.md</c> (AgentSkills.io standard, preferred)</item>
///   <item>Flat file: <c>skill-name.md</c> with YAML frontmatter</item>
/// </list>
/// Skills must have YAML frontmatter with at least <c>name</c> and <c>description</c>.
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

        // Pass 1: directory-based skills (skill-name/SKILL.md) — preferred
        foreach (var dir in Directory.GetDirectories(rootFull))
        {
            var skillMdPath = Path.Combine(dir, SkillFileName);
            if (!File.Exists(skillMdPath))
                continue;

            var entry = ParseSkillFile(skillMdPath, rootFull, isDirectoryBased: true);
            if (entry is not null)
            {
                entries.Add(entry);
                seenNames.Add(entry.Name);
            }
        }

        // Pass 2: flat .md files — skip if a directory-based skill with the same name exists
        foreach (var file in Directory.GetFiles(rootFull, "*.md", SearchOption.TopDirectoryOnly))
        {
            var name = Path.GetFileNameWithoutExtension(file).ToLowerInvariant();
            if (seenNames.Contains(name))
                continue; // directory-based skill takes precedence

            var entry = ParseSkillFile(file, rootFull, isDirectoryBased: false);
            if (entry is not null)
            {
                entries.Add(entry);
                seenNames.Add(entry.Name);
            }
        }

        // Pass 3: subdirectory flat files (category/skill.md) — for user-organized skills
        foreach (var subDir in Directory.GetDirectories(rootFull))
        {
            var subDirName = Path.GetFileName(subDir);

            // Skip directories that are skills themselves (have SKILL.md)
            if (File.Exists(Path.Combine(subDir, SkillFileName)))
                continue;

            // Skip hidden directories (except .system/ which contains system skills)
            if (subDirName.StartsWith('.') && !subDirName.Equals(".system", StringComparison.Ordinal))
                continue;

            foreach (var file in Directory.GetFiles(subDir, "*.md", SearchOption.TopDirectoryOnly))
            {
                var name = Path.GetFileNameWithoutExtension(file).ToLowerInvariant();
                if (seenNames.Contains(name))
                    continue;

                var entry = ParseSkillFile(file, rootFull, isDirectoryBased: false);
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

    private static SkillEntry? ParseSkillFile(string filePath, string rootDirectory, bool isDirectoryBased)
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

        // Try YAML frontmatter first
        var frontmatter = ExtractFrontmatter(content);
        if (frontmatter is not null)
            return BuildEntryFromFrontmatter(frontmatter, content, filePath, rootDirectory, isDirectoryBased);

        // No frontmatter — skip the file (YAML frontmatter is required)
        return null;
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
        string rootDirectory,
        bool isDirectoryBased)
    {
        // Description is required per AgentSkills.io spec
        if (string.IsNullOrWhiteSpace(fm.Description))
            return null;

        var name = !string.IsNullOrWhiteSpace(fm.Name)
            ? fm.Name.Trim().ToLowerInvariant()
            : Path.GetFileNameWithoutExtension(filePath).ToLowerInvariant();

        // Extract display name from first # heading in the body
        var body = ExtractBody(content);
        var headingMatch = HeadingRegex().Match(body);
        var displayName = headingMatch.Success
            ? headingMatch.Groups[1].Value.Trim()
            : TitleCase(name);

        // Resolve category from directory structure
        var relativePath = Path.GetRelativePath(rootDirectory, filePath);
        string? category = null;
        if (isDirectoryBased)
        {
            // For directory-based skills, the parent of the skill dir could be a category
            var skillDir = Path.GetDirectoryName(Path.GetDirectoryName(relativePath));
            if (!string.IsNullOrEmpty(skillDir) && skillDir != ".")
                category = skillDir.Replace(Path.DirectorySeparatorChar, '/');
        }
        else
        {
            var directoryPart = Path.GetDirectoryName(relativePath);
            if (!string.IsNullOrEmpty(directoryPart) && directoryPart != ".")
                category = directoryPart.Replace(Path.DirectorySeparatorChar, '/');
        }

        // Extract triggers from metadata
        string? triggers = null;
        if (fm.Metadata is not null && fm.Metadata.TryGetValue("triggers", out var triggersValue))
            triggers = Truncate(triggersValue, MaxTriggersLength);

        // Extract version from metadata
        string? version = null;
        if (fm.Metadata is not null && fm.Metadata.TryGetValue("version", out var versionValue))
            version = versionValue;

        // Build resource paths for directory-based skills
        string? skillDirectory = null;
        IReadOnlyList<string>? resourcePaths = null;
        if (isDirectoryBased)
        {
            skillDirectory = Path.GetDirectoryName(filePath)!;
            resourcePaths = EnumerateResources(skillDirectory);
        }

        return new SkillEntry(
            Name: name,
            DisplayName: displayName,
            Description: Truncate(fm.Description.Trim(), MaxDescriptionLength),
            FilePath: filePath,
            Category: category)
        {
            Triggers = triggers,
            Format = SkillFormat.Standard,
            Version = version,
            License = fm.License,
            Compatibility = fm.Compatibility,
            AllowedTools = fm.AllowedTools,
            SkillDirectory = skillDirectory,
            ResourcePaths = resourcePaths
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
