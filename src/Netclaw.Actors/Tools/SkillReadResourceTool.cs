using System.ComponentModel;
using Netclaw.Actors.Skills;
using Netclaw.Configuration;
using Netclaw.Security.Skills;
using Netclaw.Tools;

namespace Netclaw.Actors.Tools;

/// <summary>
/// Reads a resource file from a skill's references/, scripts/, or assets/ directory.
/// Scoped to the skill's directory with path traversal prevention.
/// </summary>
[NetclawTool("skill_read_resource",
    "Read a resource file from a skill's references, scripts, or assets directory.",
    Grant = "builtin")]
public sealed partial class SkillReadResourceTool : NetclawTool<SkillReadResourceTool.Params>
{
    private static readonly HashSet<string> AllowedPrefixes = new(StringComparer.OrdinalIgnoreCase)
    {
        "references",
        "scripts",
        "assets"
    };

    private readonly SkillRegistry _skillRegistry;
    private readonly ISkillContentScanner _scanner;

    public record Params(
        [property: Description("Name of the skill containing the resource")]
        string SkillName,
        [property: Description("Relative path within the skill directory (e.g., 'references/checklist.md')")]
        string ResourcePath);

    public SkillReadResourceTool(SkillRegistry skillRegistry, ISkillContentScanner scanner)
    {
        _skillRegistry = skillRegistry;
        _scanner = scanner;
    }

    protected override async Task<string> ExecuteAsync(Params args, CancellationToken ct)
    {
        var skillName = args.SkillName.Trim().ToLowerInvariant();
        var skill = _skillRegistry.GetAll()
            .FirstOrDefault(s => s.Name.Equals(skillName, StringComparison.OrdinalIgnoreCase));

        if (skill is null)
            return $"Skill '{skillName}' not found.";

        var resourcePath = args.ResourcePath.Trim();

        // Reject absolute paths
        if (Path.IsPathRooted(resourcePath))
            return "Absolute paths are not allowed. Use a relative path like 'references/doc.md'.";

        // Reject path traversal
        if (resourcePath.Contains("..", StringComparison.Ordinal))
            return "Path traversal ('..') is not allowed.";

        // Normalize separators
        resourcePath = resourcePath.Replace('\\', '/');

        // Must start with an allowed prefix
        var firstSegment = resourcePath.Split('/')[0];
        if (!AllowedPrefixes.Contains(firstSegment))
            return $"Resource path must start with one of: {string.Join(", ", AllowedPrefixes)}. Got '{firstSegment}'.";

        // Resolve the full path and verify it's within the skill directory
        var fullPath = Path.GetFullPath(Path.Combine(skill.SkillDirectory, resourcePath));
        var skillDirFull = Path.GetFullPath(skill.SkillDirectory);

        if (!fullPath.StartsWith(skillDirFull, StringComparison.Ordinal))
            return "Resolved path is outside the skill directory.";

        // Check for symlinks in the path
        if (ContainsSymlink(fullPath))
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

    private static bool ContainsSymlink(string path)
    {
        var current = path;
        while (!string.IsNullOrEmpty(current))
        {
            if (File.Exists(current) || Directory.Exists(current))
            {
                var info = new FileInfo(current);
                if (info.LinkTarget is not null)
                    return true;

                var dirInfo = new DirectoryInfo(current);
                if (dirInfo.LinkTarget is not null)
                    return true;
            }

            current = Path.GetDirectoryName(current);
        }
        return false;
    }
}
