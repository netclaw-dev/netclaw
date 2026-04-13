using System.ComponentModel;
using System.Text;
using Netclaw.Actors.Skills;
using Netclaw.Actors.Telemetry;
using Netclaw.Configuration;
using Netclaw.Security.Skills;
using Netclaw.Tools;

namespace Netclaw.Actors.Tools;

/// <summary>
/// Loads a skill by name, returning the body (frontmatter stripped) and
/// a manifest of available resource files. Always available via builtin grant.
/// </summary>
[NetclawTool("skill_load",
    "Load a skill by name. Returns the skill instructions and a list of available reference files.",
    Grant = "builtin")]
public sealed partial class SkillLoadTool : NetclawTool<SkillLoadTool.Params>
{
    private readonly SkillRegistry _skillRegistry;
    private readonly ISkillContentScanner _scanner;
    private readonly ISessionMetrics? _sessionMetrics;

    public record Params(
        [property: Description("Name of the skill to load (e.g., 'search-citation', 'netclaw-memory')")]
        string Name);

    public SkillLoadTool(SkillRegistry skillRegistry, ISkillContentScanner scanner, ISessionMetrics? sessionMetrics = null)
    {
        _skillRegistry = skillRegistry;
        _scanner = scanner;
        _sessionMetrics = sessionMetrics;
    }

    protected override async Task<string> ExecuteAsync(Params args, CancellationToken ct)
    {
        var name = args.Name.Trim().ToLowerInvariant();
        var skill = _skillRegistry.GetByName(name);

        if (skill is null)
        {
            var available = _skillRegistry.GetAll().Select(s => s.Name).ToList();
            return available.Count > 0
                ? $"Skill '{name}' not found. Available skills: {string.Join(", ", available)}"
                : $"Skill '{name}' not found. No skills are currently registered.";
        }

        string body;
        string content;
        try
        {
            content = File.ReadAllText(skill.FilePath);
            body = SkillScanner.ExtractBody(content);
        }
        catch (IOException ex)
        {
            return $"Failed to read skill file: {ex.Message}";
        }

        var scanResult = await _scanner.ScanAsync(name, content, ct);
        if (!scanResult.IsAllowed)
            return $"Skill '{name}' blocked by content scan: {scanResult.Reason}";

        _sessionMetrics?.RecordSkillLoaded(skill.Name, SkillLoadMethod.SkillLoadTool);

        var sb = new StringBuilder();
        if (scanResult.Verdict == ScanVerdict.Warning)
        {
            sb.AppendLine($":warning: Skill '{name}' triggered a content scan warning: {scanResult.Reason}");
            sb.AppendLine();
        }

        sb.AppendLine($"## {skill.DisplayName}");
        if (skill.Version is not null)
            sb.AppendLine($"Version: {skill.Version}");
        sb.AppendLine();
        sb.AppendLine(body);

        if (skill.ResourcePaths is { Count: > 0 })
        {
            sb.AppendLine();
            sb.AppendLine("### Available Resources");
            sb.AppendLine("Load via skill_read_resource(skillName, resourcePath):");
            foreach (var path in skill.ResourcePaths)
                sb.AppendLine($"- {path}");
        }

        return sb.ToString();
    }

}
