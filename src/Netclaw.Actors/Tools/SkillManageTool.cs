// -----------------------------------------------------------------------
// <copyright file="SkillManageTool.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.ComponentModel;
using System.Text;
using System.Text.RegularExpressions;
using Netclaw.Actors.Skills;
using Netclaw.Configuration;
using Netclaw.Security;
using Netclaw.Security.Skills;
using Netclaw.Tools;

namespace Netclaw.Actors.Tools;

/// <summary>
/// CRUD tool for skill creation and management. Validates AgentSkills.io format,
/// writes atomically, and triggers registry re-scan after mutations.
/// </summary>
[NetclawTool("skill_manage",
    "Create, edit, patch, or delete skills and their resource files. "
    + "Actions: create, edit, patch, delete, write_file, remove_file.",
    Grant = "builtin")]
public sealed partial class SkillManageTool : NetclawTool<SkillManageTool.Params>
{
    private static readonly HashSet<string> AllowedResourcePrefixes =
        new(StringComparer.OrdinalIgnoreCase) { "references", "scripts", "assets" };

    [GeneratedRegex(@"^[a-z0-9]([a-z0-9-]*[a-z0-9])?$")]
    private static partial Regex ValidNameRegex();

    private const int MaxNameLength = 64;
    private const int MaxDescriptionLength = 1024;

    private readonly SkillRegistry _skillRegistry;
    private readonly SkillIndexContextLayer _skillIndexLayer;
    private readonly NetclawPaths _paths;
    private readonly ISkillContentScanner _scanner;
    private readonly IReadOnlyList<ResolvedExternalSource> _externalSources;

    public record Params(
        [property: Description("Action to perform: create, edit, patch, delete, write_file, remove_file")]
        string Action,
        [property: Description("Skill name (lowercase letters, numbers, hyphens)")]
        string Name,
        [property: Description("Full SKILL.md content for create/edit actions")]
        string? Content = null,
        [property: Description("Relative file path within the skill for write_file/remove_file/patch")]
        string? FilePath = null,
        [property: Description("File content for write_file action")]
        string? FileContent = null,
        [property: Description("String to find for patch action")]
        string? OldString = null,
        [property: Description("Replacement string for patch action")]
        string? NewString = null,
        [property: Description("Replace all occurrences for patch action (default: false)")]
        bool ReplaceAll = false);

    public SkillManageTool(
        SkillRegistry skillRegistry,
        SkillIndexContextLayer skillIndexLayer,
        NetclawPaths paths,
        ISkillContentScanner scanner,
        IReadOnlyList<ResolvedExternalSource> externalSources)
    {
        _skillRegistry = skillRegistry;
        _skillIndexLayer = skillIndexLayer;
        _paths = paths;
        _scanner = scanner;
        _externalSources = externalSources;
    }

    protected override async Task<string> ExecuteAsync(Params args, CancellationToken ct)
    {
        var action = args.Action.Trim().ToLowerInvariant();
        return action switch
        {
            "create" => await CreateAsync(args, ct),
            "edit" => await EditAsync(args, ct),
            "patch" => await PatchAsync(args, ct),
            "delete" => Delete(args),
            "write_file" => await WriteFileAsync(args, ct),
            "remove_file" => RemoveFile(args),
            _ => $"Unknown action '{action}'. Valid actions: create, edit, patch, delete, write_file, remove_file."
        };
    }

    private async Task<string> CreateAsync(Params args, CancellationToken ct)
    {
        var nameError = ValidateName(args.Name);
        if (nameError is not null) return nameError;

        if (string.IsNullOrWhiteSpace(args.Content))
            return "Content is required for create action.";

        var name = args.Name.Trim().ToLowerInvariant();

        // Reject writes to system directory
        if (IsSystemSkill(name))
            return "Cannot create skills in the .system directory. System skills are read-only.";

        var contentError = ValidateFrontmatter(args.Content);
        if (contentError is not null) return contentError;

        var identityError = ValidateManagedIdentity(name, args.Content);
        if (identityError is not null) return identityError;

        var scanResult = await _scanner.ScanAsync(name, args.Content, ct);
        if (!scanResult.IsAllowed)
            return $"Content scan rejected: {scanResult.Reason}";

        var skillDir = Path.Combine(_paths.SkillsDirectory, name);
        var skillPath = Path.Combine(skillDir, "SKILL.md");

        if (File.Exists(skillPath))
        {
            // If the file exists on disk but isn't in the registry (orphaned from file_write),
            // allow create to overwrite it. Only block if the skill is properly registered.
            var existing = FindSkill(name);
            if (existing is not null)
                return $"Skill '{name}' already exists. Use 'edit' to modify it.";

            // Orphaned file — overwrite and register it
            AtomicWrite(skillPath, args.Content);
            RescanAndUpdateIndex();
            return $"Skill '{name}' created at {skillDir} (replaced orphaned file)";
        }

        Directory.CreateDirectory(skillDir);
        AtomicWrite(skillPath, args.Content);
        var rescan = RescanAndUpdateIndex();

        var message = $"Skill '{name}' created at {skillDir}";
        if (scanResult.Verdict == ScanVerdict.Warning)
            message += $" (warning: {scanResult.Reason})";

        return AppendScanWarnings(message, rescan);
    }

    private async Task<string> EditAsync(Params args, CancellationToken ct)
    {
        var nameError = ValidateName(args.Name);
        if (nameError is not null) return nameError;

        if (string.IsNullOrWhiteSpace(args.Content))
            return "Content is required for edit action.";

        var name = args.Name.Trim().ToLowerInvariant();

        if (IsSystemSkill(name))
            return "Cannot edit skills in the .system directory. System skills are read-only.";

        var skill = FindSkill(name);
        if (skill is null)
        {
            // The skill might exist on disk but not be in the registry (orphaned from file_write).
            // Rescan and retry before giving up.
            var candidatePath = Path.Combine(_paths.SkillsDirectory, name, "SKILL.md");
            if (File.Exists(candidatePath))
            {
                RescanAndUpdateIndex();
                skill = FindSkill(name);
            }

            if (skill is null)
                return $"Skill '{name}' not found.";
        }

        if (IsSystemCategory(skill))
            return "Cannot edit system skills. System skills are read-only.";

        if (IsExternalSkill(skill))
            return "Cannot edit external skills. External skill directories are read-only.";

        var contentError = ValidateFrontmatter(args.Content);
        if (contentError is not null) return contentError;

        var identityError = ValidateManagedIdentity(name, args.Content);
        if (identityError is not null) return identityError;

        var scanResult = await _scanner.ScanAsync(name, args.Content, ct);
        if (!scanResult.IsAllowed)
            return $"Content scan rejected: {scanResult.Reason}";

        AtomicWrite(skill.FilePath, args.Content);
        var rescan = RescanAndUpdateIndex();

        var message = $"Skill '{name}' updated.";
        if (scanResult.Verdict == ScanVerdict.Warning)
            message += $" (warning: {scanResult.Reason})";

        return AppendScanWarnings(message, rescan);
    }

    private async Task<string> PatchAsync(Params args, CancellationToken ct)
    {
        var nameError = ValidateName(args.Name);
        if (nameError is not null) return nameError;

        if (string.IsNullOrWhiteSpace(args.OldString))
            return "OldString is required for patch action.";
        if (args.NewString is null)
            return "NewString is required for patch action.";

        var name = args.Name.Trim().ToLowerInvariant();
        var skill = FindSkill(name);
        if (skill is null)
            return $"Skill '{name}' not found.";

        if (IsSystemCategory(skill))
            return "Cannot patch system skills. System skills are read-only.";

        if (IsExternalSkill(skill))
            return "Cannot patch external skills. External skill directories are read-only.";

        // Determine target file
        var targetPath = skill.FilePath;
        if (!string.IsNullOrWhiteSpace(args.FilePath))
        {
            var fileError = ValidateResourcePath(args.FilePath);
            if (fileError is not null) return fileError;
            targetPath = Path.Combine(skill.SkillDirectory, args.FilePath);
        }

        if (!File.Exists(targetPath))
            return $"File not found: {args.FilePath ?? "SKILL.md"}";

        var content = File.ReadAllText(targetPath);
        var occurrences = CountOccurrences(content, args.OldString);

        if (occurrences == 0)
            return "OldString not found in the file.";

        if (occurrences > 1 && !args.ReplaceAll)
            return $"OldString found {occurrences} times. Set ReplaceAll=true to replace all, or provide a more specific string.";

        var newContent = args.ReplaceAll
            ? content.Replace(args.OldString, args.NewString, StringComparison.Ordinal)
            : ReplaceFirst(content, args.OldString, args.NewString);

        if (targetPath == skill.FilePath)
        {
            var contentError = ValidateFrontmatter(newContent);
            if (contentError is not null) return contentError;

            var identityError = ValidateManagedIdentity(name, newContent);
            if (identityError is not null) return identityError;
        }

        var scanSubject = targetPath == skill.FilePath
            ? name
            : $"{name}:{args.FilePath}";
        var scanResult = await _scanner.ScanAsync(scanSubject, newContent, ct);
        if (!scanResult.IsAllowed)
            return $"Content scan rejected: {scanResult.Reason}";

        if (targetPath == skill.FilePath)
        {
            AtomicWrite(targetPath, newContent);

            var message = "Patch applied.";
            if (scanResult.Verdict == ScanVerdict.Warning)
                message += $" (warning: {scanResult.Reason})";

            return AppendScanWarnings(message, RescanAndUpdateIndex());
        }

        AtomicWrite(targetPath, newContent);
        var warning = scanResult.Verdict == ScanVerdict.Warning
            ? $" (warning: {scanResult.Reason})"
            : string.Empty;
        return $"Patch applied.{warning}";
    }

    private string Delete(Params args)
    {
        var nameError = ValidateName(args.Name);
        if (nameError is not null) return nameError;

        var name = args.Name.Trim().ToLowerInvariant();
        var skill = FindSkill(name);
        if (skill is null)
            return $"Skill '{name}' not found.";

        if (IsSystemCategory(skill))
            return "Cannot delete system skills. System skills are read-only.";

        if (IsExternalSkill(skill))
            return "Cannot delete external skills. External skill directories are read-only.";

        if (skill.IsFlatFile)
        {
            // Flat-file skill: delete the single .md file
            File.Delete(skill.FilePath);
        }
        else
        {
            Directory.Delete(skill.SkillDirectory, recursive: true);

            // Clean empty parent category directories
            var parent = Path.GetDirectoryName(skill.SkillDirectory);
            if (parent is not null && parent != _paths.SkillsDirectory
                && Directory.Exists(parent) && !Directory.EnumerateFileSystemEntries(parent).Any())
            {
                Directory.Delete(parent);
            }
        }

        return AppendScanWarnings($"Skill '{name}' deleted.", RescanAndUpdateIndex());
    }

    private async Task<string> WriteFileAsync(Params args, CancellationToken ct)
    {
        var nameError = ValidateName(args.Name);
        if (nameError is not null) return nameError;

        if (string.IsNullOrWhiteSpace(args.FilePath))
            return "FilePath is required for write_file action.";
        if (args.FileContent is null)
            return "FileContent is required for write_file action.";

        var name = args.Name.Trim().ToLowerInvariant();
        var skill = FindSkill(name);
        if (skill is null)
            return $"Skill '{name}' not found.";

        if (IsSystemCategory(skill))
            return "Cannot write files in system skills. System skills are read-only.";

        if (IsExternalSkill(skill))
            return "Cannot write files in external skills. External skill directories are read-only.";

        var fileError = ValidateResourcePath(args.FilePath);
        if (fileError is not null) return fileError;

        var fullPath = Path.GetFullPath(Path.Combine(skill.SkillDirectory, args.FilePath));
        if (!PathUtility.IsWithinRoot(fullPath, skill.SkillDirectory))
            return "Resolved path is outside the skill directory.";

        var scanResult = await _scanner.ScanAsync(
            $"{name}:{args.FilePath}",
            args.FileContent,
            ct);
        if (!scanResult.IsAllowed)
            return $"Content scan rejected: {scanResult.Reason}";

        var dir = Path.GetDirectoryName(fullPath)!;
        Directory.CreateDirectory(dir);
        AtomicWrite(fullPath, args.FileContent);
        var rescan = RescanAndUpdateIndex();

        var message = $"File written: {args.FilePath}";
        if (scanResult.Verdict == ScanVerdict.Warning)
            message += $" (warning: {scanResult.Reason})";

        return AppendScanWarnings(message, rescan);
    }

    private string RemoveFile(Params args)
    {
        var nameError = ValidateName(args.Name);
        if (nameError is not null) return nameError;

        if (string.IsNullOrWhiteSpace(args.FilePath))
            return "FilePath is required for remove_file action.";

        var name = args.Name.Trim().ToLowerInvariant();
        var skill = FindSkill(name);
        if (skill is null)
            return $"Skill '{name}' not found.";

        if (IsSystemCategory(skill))
            return "Cannot remove files from system skills. System skills are read-only.";

        if (IsExternalSkill(skill))
            return "Cannot remove files from external skills. External skill directories are read-only.";

        var fileError = ValidateResourcePath(args.FilePath);
        if (fileError is not null) return fileError;

        var fullPath = Path.GetFullPath(Path.Combine(skill.SkillDirectory, args.FilePath));
        if (!PathUtility.IsWithinRoot(fullPath, skill.SkillDirectory))
            return "Resolved path is outside the skill directory.";

        if (!File.Exists(fullPath))
            return $"File not found: {args.FilePath}";

        File.Delete(fullPath);

        // Clean empty subdirectories
        var dir = Path.GetDirectoryName(fullPath);
        if (dir is not null && dir != skill.SkillDirectory
            && Directory.Exists(dir) && !Directory.EnumerateFileSystemEntries(dir).Any())
        {
            Directory.Delete(dir);
        }

        return AppendScanWarnings($"File removed: {args.FilePath}", RescanAndUpdateIndex());
    }

    // --- Helpers ---

    private SkillEntry? FindSkill(string name) =>
        _skillRegistry.GetAll()
            .FirstOrDefault(s => s.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

    private bool IsSystemSkill(string name)
    {
        var skill = FindSkill(name);
        return skill is not null && IsSystemCategory(skill);
    }

    private static string? ValidateName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return "Name is required.";

        var trimmed = name.Trim().ToLowerInvariant();
        if (trimmed.Length > MaxNameLength)
            return $"Name must be at most {MaxNameLength} characters.";

        if (!ValidNameRegex().IsMatch(trimmed))
            return "Name must contain only lowercase letters, numbers, and hyphens. Cannot start or end with a hyphen.";

        return null;
    }

    private static string? ValidateFrontmatter(string content)
    {
        var frontmatter = SkillScanner.ExtractFrontmatter(content);
        if (frontmatter is null)
            return "Content must start with YAML frontmatter (--- delimiters).";

        if (string.IsNullOrWhiteSpace(frontmatter.Description))
            return "Frontmatter must include a 'description' field.";

        if (frontmatter.Description.Length > MaxDescriptionLength)
            return $"Description must be at most {MaxDescriptionLength} characters.";

        return null;
    }

    private static string? ValidateManagedIdentity(string targetName, string content)
    {
        var frontmatter = SkillScanner.ExtractFrontmatter(content);
        if (frontmatter is null || string.IsNullOrWhiteSpace(frontmatter.Name))
            return null;

        var normalizedFrontmatterName = SkillScanner.NormalizeSkillName(frontmatter.Name);
        if (!string.Equals(normalizedFrontmatterName, targetName, StringComparison.OrdinalIgnoreCase))
        {
            return $"Frontmatter name '{normalizedFrontmatterName}' does not match target skill '{targetName}'.";
        }

        return null;
    }

    private static string? ValidateResourcePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return "FilePath is required.";

        if (Path.IsPathRooted(path))
            return "Absolute paths are not allowed.";

        if (path.Contains("..", StringComparison.Ordinal))
            return "Path traversal ('..') is not allowed.";

        var normalized = path.Replace('\\', '/');
        var firstSegment = normalized.Split('/')[0];
        if (!AllowedResourcePrefixes.Contains(firstSegment))
            return $"FilePath must start with one of: {string.Join(", ", AllowedResourcePrefixes)}. Got '{firstSegment}'.";

        return null;
    }

    private static bool IsSystemCategory(SkillEntry skill)
        => string.Equals(skill.Category, SkillScanner.SystemCategory, StringComparison.Ordinal);

    private bool IsExternalSkill(SkillEntry skill)
    {
        var nativeRoot = PathUtility.Normalize(_paths.SkillsDirectory);
        var skillPath = PathUtility.Normalize(Path.GetDirectoryName(skill.FilePath)!);
        return !PathUtility.IsWithinRoot(skillPath, nativeRoot);
    }

    private static void AtomicWrite(string path, string content)
    {
        var tempPath = path + ".tmp";
        File.WriteAllText(tempPath, content, Encoding.UTF8);
        File.Move(tempPath, path, overwrite: true);
    }

    private Netclaw.Actors.Skills.SkillScanResult RescanAndUpdateIndex()
    {
        var mergedResult = SkillScanner.ScanAndMerge(_paths.SkillsDirectory, _externalSources);
        SkillRegistryUpdater.ApplyMergedScanResult(
            _skillRegistry, _skillIndexLayer, mergedResult, _paths.SkillsDirectory, _externalSources);

        // Return as SkillScanResult for AppendScanWarnings compatibility
        return new Netclaw.Actors.Skills.SkillScanResult(mergedResult.AcceptedSkills, mergedResult.Issues);
    }

    private static string AppendScanWarnings(string message, Netclaw.Actors.Skills.SkillScanResult scanResult)
    {
        if (!scanResult.HasIssues)
            return message;

        return $"{message} Warning: skill inventory rebuild is degraded ({scanResult.Summary}). {string.Join(" ", scanResult.FormatIssueLines(3))}";
    }

    private static int CountOccurrences(string text, string search)
    {
        var count = 0;
        var index = 0;
        while ((index = text.IndexOf(search, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += search.Length;
        }
        return count;
    }

    private static string ReplaceFirst(string text, string oldValue, string newValue)
    {
        var index = text.IndexOf(oldValue, StringComparison.Ordinal);
        return index < 0
            ? text
            : string.Concat(text.AsSpan(0, index), newValue, text.AsSpan(index + oldValue.Length));
    }
}
