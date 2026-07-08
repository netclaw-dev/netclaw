// -----------------------------------------------------------------------
// <copyright file="SkillScanner.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Text.RegularExpressions;
using Netclaw.Configuration;
using Netclaw.Security;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace Netclaw.Actors.Skills;

/// <summary>
/// Discovers skills under the skills directory using the AgentSkills.io
/// directory layout: each skill is a directory containing <c>SKILL.md</c>
/// with YAML frontmatter. Additional files under the skill directory are
/// progressive-disclosure resources.
/// </summary>
public static partial class SkillScanner
{
    private const int MaxDescriptionLength = 1024; // AgentSkills.io spec limit

    private const string SkillFileName = "SKILL.md";

    /// <summary>
    /// The directory name for system skills (synced from CDN, read-only).
    /// </summary>
    public const string SystemCategory = ".system";

    [GeneratedRegex(@"^#\s+(.+)$", RegexOptions.Multiline)]
    private static partial Regex HeadingRegex();

    private static readonly IDeserializer YamlDeserializer = new DeserializerBuilder()
        .WithNamingConvention(HyphenatedNamingConvention.Instance)
        .IgnoreUnmatchedProperties()
        .Build();

    /// <summary>
    /// Scan the skills directory and return metadata for each skill found.
    /// Returns accepted skills plus explicit issues for rejected items.
    /// </summary>
    /// <param name="skillsDirectory">Root directory to scan for skills.</param>
    /// <param name="allowSymlinks">
    /// When <c>true</c>, symlinks within the directory are followed instead of rejected.
    /// Resolved paths are still validated to stay within the root. Used for external
    /// skill directories (e.g., Claude Code) that rely on symlinks.
    /// </param>
    /// <param name="strictNameMatch">
    /// When <c>true</c> (default), the frontmatter <c>name</c> field must match the
    /// directory or file name; mismatches are emitted as <see cref="SkillScanIssueKind.FrontmatterNameMismatch"/>
    /// and reject the skill. When <c>false</c>, the frontmatter name is treated as
    /// canonical — used for external sources like Claude Code where skill directory
    /// names don't have to align with the declared name.
    /// </param>
    /// <param name="allowFrontmatterlessFlatFiles">
    /// When <c>true</c>, flat <c>.md</c> files without YAML frontmatter are treated
    /// as valid skills with name derived from filename and description inferred from
    /// first non-empty line. This compatibility mode is used for Claude Code
    /// <c>~/.claude/commands</c> files.
    /// </param>
    public static SkillScanResult Scan(
        string skillsDirectory,
        bool allowSymlinks = false,
        bool strictNameMatch = true,
        bool allowFrontmatterlessFlatFiles = false)
    {
        if (!Directory.Exists(skillsDirectory))
            return SkillScanResult.Empty;

        var rootFull = Path.GetFullPath(skillsDirectory);
        var acceptedCandidates = new List<SkillEntry>();
        var issues = new List<SkillScanIssue>();

        // Pass 0: flat-file skills (root-level .md files with valid frontmatter)
        // Claude Code and other platforms allow skill-name.md as a skill without a directory wrapper.
        foreach (var mdFile in Directory.GetFiles(rootFull, "*.md"))
        {
            var fileName = Path.GetFileName(mdFile);

            // Skip README and hidden files
            if (fileName.Equals("README.md", StringComparison.OrdinalIgnoreCase)
                || fileName.StartsWith('.'))
                continue;

            var entry = ParseFlatSkillFile(mdFile, rootFull, issues, allowSymlinks, strictNameMatch, allowFrontmatterlessFlatFiles);
            if (entry is not null)
                acceptedCandidates.Add(entry);
        }

        // Pass 1 & 2 iterate the same set of root children — enumerate once.
        var rootDirs = Directory.GetDirectories(rootFull);

        // Pass 1: root-level directory skills (skill-name/SKILL.md).
        // A directory that owns a SKILL.md is a claimed root — any SKILL.md
        // nested beneath it is an internal resource, not a separate skill.
        var claimedRoots = new HashSet<string>(StringComparer.Ordinal);
        foreach (var dir in rootDirs)
        {
            var skillMdPath = Path.Combine(dir, SkillFileName);
            if (!File.Exists(skillMdPath))
                continue;

            claimedRoots.Add(dir);

            var entry = ParseSkillFile(skillMdPath, rootFull, issues, allowSymlinks, strictNameMatch);
            if (entry is not null)
                acceptedCandidates.Add(entry);
        }

        // Pass 2: nested directory skills (.system/skill-name/SKILL.md, category/skill-name/SKILL.md)
        foreach (var subDir in rootDirs)
        {
            if (claimedRoots.Contains(subDir))
                continue;

            var subDirName = Path.GetFileName(subDir);

            // Skip hidden directories except known skill tier directories
            if (subDirName.StartsWith('.') && !IsAllowedHiddenDirectory(subDirName))
                continue;

            foreach (var nestedDir in Directory.GetDirectories(subDir))
            {
                var skillMdPath = Path.Combine(nestedDir, SkillFileName);
                if (!File.Exists(skillMdPath))
                    continue;

                var entry = ParseSkillFile(skillMdPath, rootFull, issues, allowSymlinks, strictNameMatch);
                if (entry is not null)
                    acceptedCandidates.Add(entry);
            }
        }

        // Resolve duplicates: directory skills take precedence over flat-file skills.
        // If both are the same type (both directories or both flat files), reject all duplicates.
        var groups = acceptedCandidates
            .GroupBy(static s => s.Name, StringComparer.OrdinalIgnoreCase);

        var resolved = new List<SkillEntry>();
        foreach (var group in groups)
        {
            var entries = group.ToList();
            if (entries.Count == 1)
            {
                resolved.Add(entries[0]);
                continue;
            }

            var directoryEntries = entries.Where(static e => !e.IsFlatFile).ToList();
            var flatEntries = entries.Where(static e => e.IsFlatFile).ToList();

            if (directoryEntries.Count == 1 && flatEntries.Count >= 1)
            {
                // Directory skill wins; flat file(s) are shadowed
                resolved.Add(directoryEntries[0]);
                foreach (var flat in flatEntries)
                {
                    issues.Add(new SkillScanIssue(
                        Path: flat.FilePath,
                        Kind: SkillScanIssueKind.DuplicateName,
                        Message: $"Flat file '{flat.FilePath}' is shadowed by directory skill '{directoryEntries[0].FilePath}'.",
                        SkillName: flat.Name));
                }
            }
            else
            {
                // Multiple directory skills or multiple flat files with same name — reject all
                var conflictingPaths = string.Join(", ", entries.Select(static s => s.FilePath));
                foreach (var skill in entries)
                {
                    issues.Add(new SkillScanIssue(
                        Path: skill.FilePath,
                        Kind: SkillScanIssueKind.DuplicateName,
                        Message: $"Skill name '{skill.Name}' is duplicated across: {conflictingPaths}",
                        SkillName: skill.Name));
                }
            }
        }

        var accepted = resolved
            .OrderBy(static s => s.Name, StringComparer.Ordinal)
            .ToList();

        return new SkillScanResult(accepted, issues);
    }

    /// <summary>
    /// Scans the native skills directory and all external sources, merging results.
    /// Native skills take highest precedence (strict frontmatter-name matching);
    /// external sources follow config order and use relaxed name matching (trust
    /// the frontmatter <c>name</c>, since platforms like Claude Code treat it as
    /// canonical). Duplicate names from lower-precedence sources are logged as
    /// issues and skipped.
    /// </summary>
    public static MergedSkillScanResult ScanAndMerge(
        string nativeSkillsDirectory,
        IReadOnlyList<ResolvedExternalSource> externalSources)
        => ScanAndMerge(nativeSkillsDirectory, Array.Empty<ResolvedExternalSource>(), externalSources);

    /// <summary>
    /// Scans the native skills directory, server feed sources, and external sources,
    /// merging results with three-tier precedence: native &gt; server feeds &gt; external.
    /// Server feed sources are org-managed private skill servers that take precedence
    /// over local external directories but yield to native user skills.
    /// </summary>
    public static MergedSkillScanResult ScanAndMerge(
        string nativeSkillsDirectory,
        IReadOnlyList<ResolvedExternalSource> serverFeedSources,
        IReadOnlyList<ResolvedExternalSource> externalSources)
    {
        var nativeScan = Scan(nativeSkillsDirectory, allowSymlinks: false, strictNameMatch: true);
        var allAccepted = new List<SkillEntry>(nativeScan.AcceptedSkills);
        var allIssues = new List<SkillScanIssue>(nativeScan.Issues);

        var knownNames = new HashSet<string>(
            nativeScan.AcceptedSkills.Select(s => s.Name), StringComparer.OrdinalIgnoreCase);

        // Server feed sources (second tier — org-managed private skill servers)
        MergeSources(serverFeedSources, allAccepted, allIssues, knownNames, allowFrontmatterlessFlatFiles: false);

        // External filesystem sources (third tier — Claude Code, Open Code, custom paths)
        MergeSources(externalSources, allAccepted, allIssues, knownNames, allowFrontmatterlessFlatFiles: true);

        return new MergedSkillScanResult(allAccepted, allIssues);
    }

    private static void MergeSources(
        IReadOnlyList<ResolvedExternalSource> sources,
        List<SkillEntry> allAccepted,
        List<SkillScanIssue> allIssues,
        HashSet<string> knownNames,
        bool allowFrontmatterlessFlatFiles)
    {
        foreach (var source in sources)
        {
            foreach (var path in source.Paths)
            {
                var externalScan = Scan(
                    path,
                    allowSymlinks: source.AllowSymlinks,
                    strictNameMatch: false,
                    allowFrontmatterlessFlatFiles: allowFrontmatterlessFlatFiles && IsClaudeCommandsDirectory(path));
                allIssues.AddRange(externalScan.Issues);

                foreach (var skill in externalScan.AcceptedSkills)
                {
                    if (!knownNames.Add(skill.Name))
                    {
                        allIssues.Add(new SkillScanIssue(
                            Path: skill.FilePath,
                            Kind: SkillScanIssueKind.DuplicateName,
                            Message: $"Skill '{skill.Name}' from external source '{source.Name}' is shadowed by a higher-precedence skill.",
                            SkillName: skill.Name));
                        continue;
                    }

                    allAccepted.Add(skill);
                }
            }
        }
    }

    private static SkillEntry? ParseSkillFile(string filePath, string rootDirectory, List<SkillScanIssue> issues, bool allowSymlinks = false, bool strictNameMatch = true)
    {
        var canonicalRoot = PathUtility.Normalize(rootDirectory);
        var canonicalSkillFilePath = ValidateCanonicalPath(filePath, canonicalRoot, issues, "skill file", allowSymlinks);
        if (canonicalSkillFilePath is null)
            return null;

        var skillDirectory = Path.GetDirectoryName(canonicalSkillFilePath)!;
        var canonicalSkillDirectory = ValidateCanonicalPath(skillDirectory, canonicalRoot, issues, "skill directory", allowSymlinks);
        if (canonicalSkillDirectory is null)
            return null;

        string content;
        try
        {
            content = File.ReadAllText(canonicalSkillFilePath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            var skillName = NormalizeSkillName(Path.GetFileName(Path.GetDirectoryName(canonicalSkillFilePath)!));
            issues.Add(new SkillScanIssue(
                Path: canonicalSkillFilePath,
                Kind: SkillScanIssueKind.UnreadableFile,
                Message: $"Failed to read skill file: {ex.Message}",
                SkillName: skillName));
            return null;
        }

        var frontmatter = ExtractFrontmatter(content);
        if (frontmatter is null)
        {
            var skillName = NormalizeSkillName(Path.GetFileName(Path.GetDirectoryName(canonicalSkillFilePath)!));
            // content is from File.ReadAllText, which already strips any UTF-8 BOM, so no TrimStart
            // is needed here; BOM tolerance for direct-string callers lives in ExtractFrontmatter.
            var hasFrontmatterStart = content.StartsWith("---", StringComparison.Ordinal);
            issues.Add(new SkillScanIssue(
                Path: canonicalSkillFilePath,
                Kind: hasFrontmatterStart
                    ? SkillScanIssueKind.InvalidFrontmatter
                    : SkillScanIssueKind.MissingFrontmatter,
                Message: hasFrontmatterStart
                    ? "Skill frontmatter is invalid or unparseable."
                    : "Skill file must start with YAML frontmatter.",
                SkillName: skillName));
            return null;
        }

        return BuildEntryFromFrontmatter(frontmatter, content, canonicalSkillFilePath, canonicalSkillDirectory, canonicalRoot, issues, allowSymlinks, strictNameMatch);
    }

    /// <summary>
    /// Parses a flat <c>.md</c> file as a skill. Flat files are root-level markdown
    /// files with valid YAML frontmatter — supported for compatibility with Claude Code
    /// and other platforms that allow skills without the directory wrapper.
    /// </summary>
    private static SkillEntry? ParseFlatSkillFile(
        string filePath,
        string rootDirectory,
        List<SkillScanIssue> issues,
        bool allowSymlinks = false,
        bool strictNameMatch = true,
        bool allowFrontmatterlessFlatFiles = false)
    {
        var canonicalRoot = PathUtility.Normalize(rootDirectory);
        var canonicalPath = ValidateCanonicalPath(filePath, canonicalRoot, issues, "flat skill file", allowSymlinks);
        if (canonicalPath is null)
            return null;

        string content;
        try
        {
            content = File.ReadAllText(canonicalPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            var skillName = NormalizeSkillName(Path.GetFileNameWithoutExtension(canonicalPath));
            issues.Add(new SkillScanIssue(
                Path: canonicalPath,
                Kind: SkillScanIssueKind.UnreadableFile,
                Message: $"Failed to read flat skill file: {ex.Message}",
                SkillName: skillName));
            return null;
        }

        var frontmatter = ExtractFrontmatter(content);
        if (frontmatter is null)
        {
            // content is from File.ReadAllText (BOM already stripped), so no TrimStart needed.
            if (allowFrontmatterlessFlatFiles && !content.StartsWith("---", StringComparison.Ordinal))
                return BuildFlatSkillEntryWithoutFrontmatter(canonicalPath, canonicalRoot, content, issues);

            var skillName = NormalizeSkillName(Path.GetFileNameWithoutExtension(canonicalPath));
            issues.Add(new SkillScanIssue(
                Path: canonicalPath,
                Kind: SkillScanIssueKind.FlatFileMissingFrontmatter,
                Message: "Flat .md file found but lacks valid YAML frontmatter. Add frontmatter with name and description, or move into a skill-name/SKILL.md directory.",
                SkillName: skillName));
            return null;
        }

        // Derive skill name from frontmatter or filename
        var fileNameWithoutExt = Path.GetFileNameWithoutExtension(canonicalPath);
        var name = !string.IsNullOrWhiteSpace(frontmatter.Name)
            ? NormalizeSkillName(frontmatter.Name)
            : NormalizeSkillName(fileNameWithoutExt);

        if (string.IsNullOrWhiteSpace(frontmatter.Description))
        {
            issues.Add(new SkillScanIssue(
                Path: canonicalPath,
                Kind: SkillScanIssueKind.FlatFileNoDescription,
                Message: "Flat .md file has frontmatter but missing description field.",
                SkillName: name));
            return null;
        }

        if (strictNameMatch && !string.IsNullOrWhiteSpace(frontmatter.Name))
        {
            var expectedName = NormalizeSkillName(fileNameWithoutExt);
            if (!string.Equals(name, expectedName, StringComparison.OrdinalIgnoreCase))
            {
                issues.Add(new SkillScanIssue(
                    Path: canonicalPath,
                    Kind: SkillScanIssueKind.FrontmatterNameMismatch,
                    Message: $"Frontmatter name '{name}' does not match file name '{expectedName}'.",
                    SkillName: name));
                return null;
            }
        }

        var entry = BuildSkillEntry(frontmatter, content, name, canonicalPath, canonicalRoot, category: null);
        var (hasSubagentMetadata, subagent, subagentError) = ParseSubagentMetadata(frontmatter);
        if (hasSubagentMetadata && subagentError is not null)
        {
            issues.Add(new SkillScanIssue(
                Path: canonicalPath,
                Kind: SkillScanIssueKind.InvalidSubagentMetadata,
                Message: $"Invalid metadata.subagent: {subagentError}",
                SkillName: name));
        }

        return entry with
        {
            ResourcePaths = null,
            IsFlatFile = true,
            HasSubagentRoutingMetadata = hasSubagentMetadata,
            Subagent = subagent,
            SubagentMetadataError = subagentError
        };
    }

    /// <summary>
    /// Extracts and deserializes YAML frontmatter from a skill file.
    /// Returns null if the file does not start with <c>---</c> or the YAML is unparseable.
    /// </summary>
    public static SkillFrontmatter? ExtractFrontmatter(string content)
    {
        // Strip UTF-8 BOM — some editors (e.g., Notepad on Windows) prepend \uFEFF
        content = content.TrimStart('\uFEFF');
        if (!content.StartsWith("---", StringComparison.Ordinal))
            return null;

        // Find the closing --- delimiter
        var closingIndex = content.IndexOf("\n---", 3, StringComparison.Ordinal);
        if (closingIndex < 0)
            return null;

        // Guard degenerate blocks like "---\n---" where the opening line's newline IS the
        // closing delimiter: the YAML body is empty, so there is nothing to deserialize.
        // Without this, content[(firstNewline+1)..closingIndex] slices a negative-length
        // range and throws ArgumentOutOfRangeException, which propagates out of Scan (the
        // parse calls are unguarded) and aborts the entire skill-discovery pass.
        var firstNewline = content.IndexOf('\n', StringComparison.Ordinal);
        if (firstNewline < 0 || firstNewline >= closingIndex)
            return null;

        var yamlBlock = content[(firstNewline + 1)..closingIndex];

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
        string skillDirectory,
        string rootDirectory,
        List<SkillScanIssue> issues,
        bool allowSymlinks = false,
        bool strictNameMatch = true)
    {
        // Description is required per AgentSkills.io spec
        if (string.IsNullOrWhiteSpace(fm.Description))
        {
            var skillName = !string.IsNullOrWhiteSpace(fm.Name)
                ? NormalizeSkillName(fm.Name)
                : NormalizeSkillName(Path.GetFileName(skillDirectory));
            issues.Add(new SkillScanIssue(
                Path: filePath,
                Kind: SkillScanIssueKind.MissingDescription,
                Message: "Skill frontmatter must include a non-empty description.",
                SkillName: skillName));
            return null;
        }

        var name = !string.IsNullOrWhiteSpace(fm.Name)
            ? NormalizeSkillName(fm.Name)
            : NormalizeSkillName(Path.GetFileName(skillDirectory));

        if (strictNameMatch && !string.IsNullOrWhiteSpace(fm.Name))
        {
            var expectedName = NormalizeSkillName(Path.GetFileName(skillDirectory));
            if (!string.Equals(name, expectedName, StringComparison.OrdinalIgnoreCase))
            {
                issues.Add(new SkillScanIssue(
                    Path: filePath,
                    Kind: SkillScanIssueKind.FrontmatterNameMismatch,
                    Message: $"Frontmatter name '{name}' does not match directory name '{expectedName}'.",
                    SkillName: name));
                return null;
            }
        }

        // Resolve category from directory structure
        var relativePath = Path.GetRelativePath(rootDirectory, filePath);
        string? category = null;
        var parentOfSkillDir = Path.GetDirectoryName(Path.GetDirectoryName(relativePath));
        if (!string.IsNullOrEmpty(parentOfSkillDir) && parentOfSkillDir != ".")
            category = parentOfSkillDir.Replace(Path.DirectorySeparatorChar, '/');

        var issueCountBeforeResourceScan = issues.Count;
        var resourcePaths = EnumerateResources(skillDirectory, rootDirectory, issues, allowSymlinks);
        if (issues.Count > issueCountBeforeResourceScan)
            return null;

        var entry = BuildSkillEntry(fm, content, name, filePath, skillDirectory, category);
        var (hasSubagentMetadata, subagent, subagentError) = ParseSubagentMetadata(fm);
        if (hasSubagentMetadata && subagentError is not null)
        {
            issues.Add(new SkillScanIssue(
                Path: filePath,
                Kind: SkillScanIssueKind.InvalidSubagentMetadata,
                Message: $"Invalid metadata.subagent: {subagentError}",
                SkillName: name));
        }

        return entry with
        {
            ResourcePaths = resourcePaths,
            HasSubagentRoutingMetadata = hasSubagentMetadata,
            Subagent = subagent,
            SubagentMetadataError = subagentError
        };
    }

    /// <summary>
    /// Shared SkillEntry construction from validated frontmatter. Used by both
    /// directory-based and flat-file parsing paths.
    /// </summary>
    private static SkillEntry BuildSkillEntry(
        SkillFrontmatter fm, string content, string name,
        string filePath, string skillDirectory, string? category)
    {
        var body = ExtractBody(content);
        var headingMatch = HeadingRegex().Match(body);
        var displayName = headingMatch.Success
            ? headingMatch.Groups[1].Value.Trim()
            : TitleCase(name);

        string? version = null;
        if (fm.Metadata is not null && fm.Metadata.TryGetValue("version", out var versionValue))
            version = versionValue?.ToString();

        return new SkillEntry(
            Name: name,
            DisplayName: displayName,
            Description: Truncate(fm.Description!.Trim(), MaxDescriptionLength),
            FilePath: filePath,
            SkillDirectory: skillDirectory,
            Category: category)
        {
            Version = version,
            License = fm.License,
            Compatibility = fm.Compatibility,
            AllowedTools = fm.AllowedTools,
            DisableModelInvocation = fm.DisableModelInvocation,
            UserInvocable = fm.UserInvocable,
            ArgumentHint = fm.ArgumentHint
        };
    }

    private static (bool HasSubagentMetadata, string? Subagent, string? Error) ParseSubagentMetadata(SkillFrontmatter fm)
    {
        if (fm.Metadata is null || !fm.Metadata.TryGetValue("subagent", out var rawValue))
            return (false, null, null);

        if (rawValue is not string text)
        {
            var typeName = rawValue?.GetType().Name ?? "null";
            return (true, null, $"expected string value but found {typeName}.");
        }

        var trimmed = text.Trim();
        if (trimmed.Length == 0)
            return (true, null, "value must not be empty.");

        if (!SkillActivationRouter.IsValidSubagentName(trimmed))
            return (true, null, $"'{trimmed}' is not a valid subagent name (expected lowercase kebab-case).");

        return (true, trimmed, null);
    }

    /// <summary>
    /// Enumerates non-root resource files under a skill directory.
    /// Returns null if no resources are found.
    /// </summary>
    private static IReadOnlyList<string>? EnumerateResources(string skillDirectory, string rootDirectory, List<SkillScanIssue> issues, bool allowSymlinks = false)
    {
        List<string>? resources = null;
        var canonicalRoot = PathUtility.Normalize(rootDirectory);

        string[] files;
        try
        {
            files = Directory.GetFiles(skillDirectory, "*", SearchOption.AllDirectories);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // skillDirectory is already the skill's own directory, so its leaf name IS the
            // skill name — do not climb to the parent (that yields the container dir, e.g. "files").
            var skillName = NormalizeSkillName(Path.GetFileName(skillDirectory));
            issues.Add(new SkillScanIssue(
                Path: skillDirectory,
                Kind: SkillScanIssueKind.ResourceEnumerationFailed,
                Message: $"Failed to enumerate resources: {ex.Message}",
                SkillName: skillName));
            return null;
        }

        foreach (var file in files.OrderBy(static f => f, StringComparer.Ordinal))
        {
            if (PathUtility.AreEquivalentPaths(file, Path.Combine(skillDirectory, SkillFileName)))
                continue;

            if (ValidateCanonicalPath(file, canonicalRoot, issues, "resource file", allowSymlinks) is null)
                return null;

            var relativePath = Path.GetRelativePath(skillDirectory, file)
                .Replace(Path.DirectorySeparatorChar, '/');
            resources ??= [];
            resources.Add(relativePath);
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

    private static SkillEntry? BuildFlatSkillEntryWithoutFrontmatter(
        string canonicalPath,
        string canonicalRoot,
        string content,
        List<SkillScanIssue> issues)
    {
        var description = ExtractFirstNonEmptyMarkdownLine(content);
        if (string.IsNullOrWhiteSpace(description))
        {
            var skillName = NormalizeSkillName(Path.GetFileNameWithoutExtension(canonicalPath));
            issues.Add(new SkillScanIssue(
                Path: canonicalPath,
                Kind: SkillScanIssueKind.FlatFileNoDescription,
                Message: "Flat .md file without frontmatter must contain at least one non-empty line to infer a description.",
                SkillName: skillName));
            return null;
        }

        var fileNameWithoutExt = Path.GetFileNameWithoutExtension(canonicalPath);
        var name = NormalizeSkillName(fileNameWithoutExt);

        var headingMatch = HeadingRegex().Match(content);
        var displayName = headingMatch.Success
            ? headingMatch.Groups[1].Value.Trim()
            : TitleCase(name);

        return new SkillEntry(
            Name: name,
            DisplayName: displayName,
            Description: Truncate(description, MaxDescriptionLength),
            FilePath: canonicalPath,
            SkillDirectory: canonicalRoot,
            Category: null)
        {
            ResourcePaths = null,
            IsFlatFile = true
        };
    }

    private static string? ExtractFirstNonEmptyMarkdownLine(string content)
    {
        var lines = content.Split('\n');
        foreach (var rawLine in lines)
        {
            var line = rawLine.Trim();
            if (line.Length == 0)
                continue;

            if (line.StartsWith("#", StringComparison.Ordinal))
                line = line.TrimStart('#').Trim();

            if (line.Length > 0)
                return line;
        }

        return null;
    }

    private const string ServerFeedsCategory = ".server-feeds";

    /// <summary>
    /// Hidden directories that are scanned for skills. <c>.system</c> holds CDN-synced
    /// system skills; <c>.server-feeds</c> holds skills synced from private skill servers.
    /// </summary>
    private static bool IsAllowedHiddenDirectory(string dirName)
        => string.Equals(dirName, SystemCategory, StringComparison.Ordinal)
        || string.Equals(dirName, ServerFeedsCategory, StringComparison.Ordinal);

    private static bool IsClaudeCommandsDirectory(string path)
    {
        var normalized = PathUtility.Normalize(path);
        var directoryName = Path.GetFileName(normalized);
        if (!string.Equals(directoryName, "commands", StringComparison.OrdinalIgnoreCase))
            return false;

        var parent = Path.GetDirectoryName(normalized);
        if (string.IsNullOrEmpty(parent))
            return false;

        return string.Equals(Path.GetFileName(parent), ".claude", StringComparison.OrdinalIgnoreCase);
    }

    internal static string NormalizeSkillName(string value)
        => value.Trim().ToLowerInvariant();

    private static string? ValidateCanonicalPath(
        string path,
        string canonicalRoot,
        List<SkillScanIssue> issues,
        string label,
        bool allowSymlinks = false)
    {
        var normalizedPath = Path.GetFullPath(path);

        if (!PathUtility.IsWithinRoot(normalizedPath, canonicalRoot))
        {
            issues.Add(new SkillScanIssue(
                Path: normalizedPath,
                Kind: SkillScanIssueKind.InvalidPath,
                Message: $"Resolved {label} is outside the configured skills root."));
            return null;
        }

        if (!allowSymlinks && ContainsSymlink(normalizedPath, canonicalRoot))
        {
            issues.Add(new SkillScanIssue(
                Path: normalizedPath,
                Kind: SkillScanIssueKind.SymlinkNotAllowed,
                Message: $"Symlink traversal is not allowed for {label}."));
            return null;
        }

        return normalizedPath;
    }

    private static bool ContainsSymlink(string path, string canonicalRoot)
    {
        string? current = path;
        while (!string.IsNullOrEmpty(current) && PathUtility.IsWithinRoot(current, canonicalRoot))
        {
            if (File.Exists(current) && new FileInfo(current).LinkTarget is not null)
                return true;

            if (Directory.Exists(current) && new DirectoryInfo(current).LinkTarget is not null)
                return true;

            var parent = Path.GetDirectoryName(current);
            if (string.IsNullOrEmpty(parent) || string.Equals(parent, current, StringComparison.Ordinal))
                break;

            current = parent;
        }

        return false;
    }
}

/// <summary>
/// YAML frontmatter schema for AgentSkills.io SKILL.md files.
/// </summary>
public sealed class SkillFrontmatter
{
    public string? Name { get; set; }
    public string? Description { get; set; }
    public string? License { get; set; }
    public string? Compatibility { get; set; }

    [YamlMember(Alias = "allowed-tools")]
    public string? AllowedTools { get; set; }

    [YamlMember(Alias = "disable-model-invocation")]
    public bool DisableModelInvocation { get; set; }

    [YamlMember(Alias = "invocable")]
    public bool UserInvocable { get; set; } = true;

    [YamlMember(Alias = "argument-hint")]
    public string? ArgumentHint { get; set; }

    public Dictionary<string, object?>? Metadata { get; set; }
}
