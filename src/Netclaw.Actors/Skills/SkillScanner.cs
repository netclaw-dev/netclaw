using System.Text.RegularExpressions;
using Netclaw.Configuration;

namespace Netclaw.Actors.Skills;

/// <summary>
/// Discovers <c>.md</c> skill files recursively under the skills directory.
/// Extracts metadata from each file (name, display name, description, category).
/// </summary>
public static partial class SkillScanner
{
    private const int MaxMetadataLines = 20;
    private const int MaxDescriptionLength = 200;
    private const int MaxTriggersLength = 300;

    [GeneratedRegex(@"^#\s+(.+)$")]
    private static partial Regex HeadingRegex();

    [GeneratedRegex(@"<!--\s*description:\s*(.+?)\s*-->")]
    private static partial Regex DescriptionCommentRegex();

    [GeneratedRegex(@"<!--\s*triggers:\s*(.+?)\s*-->")]
    private static partial Regex TriggersCommentRegex();

    /// <summary>
    /// Scan the skills directory and return metadata for each <c>.md</c> file found.
    /// Returns an empty list if the directory does not exist.
    /// </summary>
    public static IReadOnlyList<SkillEntry> Scan(string skillsDirectory)
    {
        if (!Directory.Exists(skillsDirectory))
            return [];

        var rootFull = Path.GetFullPath(skillsDirectory);
        var files = Directory.GetFiles(rootFull, "*.md", SearchOption.AllDirectories);
        var entries = new List<SkillEntry>(files.Length);

        foreach (var file in files)
        {
            var entry = ParseSkillFile(file, rootFull);
            if (entry is not null)
                entries.Add(entry);
        }

        entries.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.Ordinal));
        return entries;
    }

    private static SkillEntry? ParseSkillFile(string filePath, string rootDirectory)
    {
        var fileName = Path.GetFileNameWithoutExtension(filePath).ToLowerInvariant();
        var relativePath = Path.GetRelativePath(rootDirectory, filePath);
        var directoryPart = Path.GetDirectoryName(relativePath);
        var category = string.IsNullOrEmpty(directoryPart) || directoryPart == "."
            ? null
            : directoryPart.Replace(Path.DirectorySeparatorChar, '/');

        string? displayName = null;
        string? description = null;
        string? triggers = null;

        try
        {
            using var reader = new StreamReader(filePath);
            var lineCount = 0;
            var foundHeading = false;
            var paragraphLines = new List<string>();

            while (lineCount < MaxMetadataLines && reader.ReadLine() is { } line)
            {
                lineCount++;
                var trimmed = line.Trim();

                // Check for <!-- description: ... --> comment
                if (description is null)
                {
                    var descMatch = DescriptionCommentRegex().Match(trimmed);
                    if (descMatch.Success)
                    {
                        description = Truncate(descMatch.Groups[1].Value, MaxDescriptionLength);
                        continue;
                    }
                }

                // Check for <!-- triggers: ... --> comment
                if (triggers is null)
                {
                    var trigMatch = TriggersCommentRegex().Match(trimmed);
                    if (trigMatch.Success)
                    {
                        triggers = Truncate(trigMatch.Groups[1].Value, MaxTriggersLength);
                        continue;
                    }
                }

                // Check for # heading
                if (!foundHeading)
                {
                    var headingMatch = HeadingRegex().Match(trimmed);
                    if (headingMatch.Success)
                    {
                        displayName = headingMatch.Groups[1].Value.Trim();
                        foundHeading = true;
                        continue;
                    }
                }

                // Collect first non-heading, non-empty paragraph for description fallback
                if (description is null && foundHeading)
                {
                    if (string.IsNullOrWhiteSpace(trimmed))
                    {
                        if (paragraphLines.Count > 0)
                        {
                            description = Truncate(string.Join(' ', paragraphLines), MaxDescriptionLength);
                        }
                    }
                    else
                    {
                        paragraphLines.Add(trimmed);
                    }
                }
            }

            // If we ran out of lines and have accumulated paragraph text
            if (description is null && paragraphLines.Count > 0)
            {
                description = Truncate(string.Join(' ', paragraphLines), MaxDescriptionLength);
            }
        }
        catch (IOException)
        {
            // File became unreadable — skip it
            return null;
        }

        displayName ??= TitleCase(fileName);
        description ??= $"Skill: {displayName}";

        return new SkillEntry(
            Name: fileName,
            DisplayName: displayName,
            Description: description,
            FilePath: filePath,
            Category: category)
        {
            Triggers = triggers
        };
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
