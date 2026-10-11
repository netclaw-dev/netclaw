// -----------------------------------------------------------------------
// <copyright file="SkillReadResourceTool.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.ComponentModel;
using Netclaw.Actors.Skills;
using Netclaw.Configuration;
using Netclaw.Security;
using Netclaw.Security.Authorization.Filesystem;
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

        if (skill.Source is not FileSkillSource fileSource)
            return $"Skill '{skillName}' does not expose file resources.";

        if (!SkillResourcePath.TryNormalize(args.ResourcePath, out var resourcePath, out var pathError))
            return SkillResourcePath.FormatReadError(pathError);

        // The skill root and its ancestors are trusted: the registry placed the
        // skill there, and on macOS those ancestors include OS links such as
        // /var -> /private/var. A link below the root is traversal.
        var fullPath = Path.GetFullPath(Path.Combine(fileSource.SkillDirectory, resourcePath));
        if (!CanonicalPath.TryCreateHost(fullPath, relativeBase: null, out var resource)
            || !CanonicalPath.TryCreateHost(fileSource.SkillDirectory, relativeBase: null, out var skillRoot))
        {
            return "Resolved path is outside the skill directory.";
        }

        switch (FileSystemAuthority.EvaluateMembership(
                    resource,
                    [new PathBoundary.Folder(skillRoot, LinkRule.BelowRoot)]))
        {
            case PathDecision.Outside:
                return "Resolved path is outside the skill directory.";
            case PathDecision.CrossesLink or PathDecision.Unverifiable:
                return "Symlink traversal is not allowed in resource paths.";
        }

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

            // The first line gives the resolved absolute path, so the agent can
            // run a bundled script by its real path and not guess a relative
            // one. The path appears only in this result, after the scan passes.
            // Prompt indexes and skill listings never expose skill roots.
            var pathLine = $"path: {fullPath}";
            if (scanResult.Verdict == ScanVerdict.Warning)
                return $"{pathLine}\n:warning: Resource '{resourcePath}' triggered a content scan warning: {scanResult.Reason}\n\n{content}";

            return $"{pathLine}\n{content}";
        }
        catch (IOException ex)
        {
            return $"Failed to read resource: {ex.Message}";
        }
    }
}
