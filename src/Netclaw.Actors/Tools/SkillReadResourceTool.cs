// -----------------------------------------------------------------------
// <copyright file="SkillReadResourceTool.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.ComponentModel;
using Netclaw.Actors.Skills;
using Netclaw.Configuration;
using Netclaw.Security;
using Netclaw.Security.Skills;
using Netclaw.Tools;

namespace Netclaw.Actors.Tools;

/// <summary>
/// Reads a resource file from a skill directory.
/// Scoped to the skill's directory with path traversal prevention.
/// </summary>
[NetclawTool("skill_read_resource",
    "Read a resource file from a skill directory.",
    Grant = "builtin")]
public sealed partial class SkillReadResourceTool : NetclawTool<SkillReadResourceTool.Params>
{
    private readonly SkillRegistry _skillRegistry;
    private readonly ISkillContentScanner _scanner;
    private readonly SkillSyncConfig _skillSyncConfig;

    public record Params(
        [property: Description("Name of the skill containing the resource")]
        string SkillName,
        [property: Description("Relative path within the skill directory (e.g., 'references/checklist.md' or 'tools/check')")]
        string ResourcePath);

    public SkillReadResourceTool(SkillRegistry skillRegistry, ISkillContentScanner scanner,
        SkillSyncConfig? skillSyncConfig = null)
    {
        _skillRegistry = skillRegistry;
        _scanner = scanner;
        _skillSyncConfig = skillSyncConfig ?? new SkillSyncConfig();
    }

    protected override async Task<string> ExecuteAsync(Params args, ToolInvocationContext context, CancellationToken ct)
    {
        // Defense-in-depth: block skill resource reading for Public audience or when skills subsystem is disabled
        var audience = context.Audience;
        if (audience == TrustAudience.Public || !_skillSyncConfig.Enabled)
            return "Error: This tool is not available.";

        var skillName = args.SkillName.Trim().ToLowerInvariant();
        var skill = _skillRegistry.GetAll()
            .FirstOrDefault(s => s.Name.Equals(skillName, StringComparison.OrdinalIgnoreCase));

        if (skill is null)
            return $"Skill '{skillName}' not found.";

        if (!SkillResourcePath.TryNormalize(args.ResourcePath, out var resourcePath, out var pathError))
            return SkillResourcePath.FormatReadError(pathError);

        // Resolve the full path and verify it's within the skill directory
        var fullPath = Path.GetFullPath(Path.Combine(skill.SkillDirectory, resourcePath));
        var skillDirFull = Path.GetFullPath(skill.SkillDirectory);

        if (!PathUtility.IsWithinRoot(fullPath, skillDirFull))
            return "Resolved path is outside the skill directory.";

        // Check for symlinks introduced *within* the skill directory. The skill
        // root and its ancestors are trusted (the registry placed the skill
        // there) — and on macOS those ancestors include OS-level symlinks such
        // as /var -> /private/var, so walking past the root would false-positive.
        if (ContainsSymlink(fullPath, skillDirFull))
            return "Symlink traversal is not allowed in resource paths.";

        if (!File.Exists(fullPath))
        {
            if (skill.ResourcePaths is { Count: > 0 })
            {
                return $"Resource '{resourcePath}' not found in skill '{skillName}'. " +
                    $"Available: {string.Join(", ", skill.ResourcePaths)}";
            }
            return $"Resource '{resourcePath}' not found in skill '{skillName}'.";
        }

        try
        {
            var content = File.ReadAllText(fullPath);
            var scanResult = await _scanner.ScanAsync($"{skillName}:{resourcePath}", content, ct);
            if (!scanResult.IsAllowed)
                return $"Resource '{resourcePath}' blocked by content scan: {scanResult.Reason}";

            if (scanResult.Verdict == ScanVerdict.Warning)
                return $":warning: Resource '{resourcePath}' triggered a content scan warning: {scanResult.Reason}\n\n{content}";

            return content;
        }
        catch (IOException ex)
        {
            return $"Failed to read resource: {ex.Message}";
        }
    }

    /// <summary>
    /// Returns true if any path component strictly between <paramref name="root"/>
    /// (exclusive) and <paramref name="path"/> (inclusive) is a symlink. The walk
    /// stops at the skill root so OS-level symlinks above it (e.g. macOS
    /// <c>/var</c> -&gt; <c>/private/var</c>) do not register as traversal.
    /// </summary>
    private static bool ContainsSymlink(string path, string root)
    {
        var rootFull = Path.TrimEndingDirectorySeparator(root);
        var current = path;
        while (!string.IsNullOrEmpty(current)
            && !string.Equals(
                Path.TrimEndingDirectorySeparator(current), rootFull, StringComparison.Ordinal))
        {
            if (File.Exists(current) || Directory.Exists(current))
            {
                if (new FileInfo(current).LinkTarget is not null)
                    return true;

                if (new DirectoryInfo(current).LinkTarget is not null)
                    return true;
            }

            current = Path.GetDirectoryName(current);
        }
        return false;
    }
}
