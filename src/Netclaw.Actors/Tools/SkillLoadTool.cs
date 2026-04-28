// -----------------------------------------------------------------------
// <copyright file="SkillLoadTool.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.ComponentModel;
using Netclaw.Actors.SubAgents;
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
    private readonly SubAgentDefinitionRegistry? _subAgentRegistry;
    private readonly SubAgentSpawner? _subAgentSpawner;
    private readonly SkillSyncConfig _skillSyncConfig;

    public record Params(
        [property: Description("Name of the skill to load (e.g., 'search-citation', 'netclaw-memory')")]
        string Name,
        [property: Description("Optional task used when the skill routes via metadata.subagent. Required for routed skill execution.")]
        string? Task = null,
        [property: Description("Optional runtime context passed to the routed subagent for this invocation.")]
        string? Context = null);

    public SkillLoadTool(
        SkillRegistry skillRegistry,
        ISkillContentScanner scanner,
        ISessionMetrics? sessionMetrics = null,
        SubAgentDefinitionRegistry? subAgentRegistry = null,
        SubAgentSpawner? subAgentSpawner = null,
        SkillSyncConfig? skillSyncConfig = null)
    {
        _skillRegistry = skillRegistry;
        _scanner = scanner;
        _sessionMetrics = sessionMetrics;
        _subAgentRegistry = subAgentRegistry;
        _subAgentSpawner = subAgentSpawner;
        _skillSyncConfig = skillSyncConfig ?? new SkillSyncConfig();
    }

    protected override async Task<string> ExecuteAsync(Params args, CancellationToken ct)
        => await ExecuteAsync(args, ToolExecutionContext.Empty, ct);

    protected override async Task<string> ExecuteAsync(Params args, ToolExecutionContext context, CancellationToken ct)
    {
        // Defense-in-depth: block skill loading for Public audience or when skills subsystem is disabled
        var audience = SecurityPolicyDefaults.ParseAudienceOrPublic(context.Audience);
        if (audience == TrustAudience.Public || !_skillSyncConfig.Enabled)
            return "Error: This tool is not available.";

        var name = args.Name.Trim().ToLowerInvariant();
        var skill = _skillRegistry.GetByName(name);

        if (skill is null)
        {
            var available = _skillRegistry.GetAll().Select(s => s.Name).ToList();
            return available.Count > 0
                ? $"Skill '{name}' not found. Available skills: {string.Join(", ", available)}"
                : $"Skill '{name}' not found. No skills are currently registered.";
        }

        var decision = SkillActivationRouter.Resolve(skill);
        if (decision.IsError)
            return decision.ErrorMessage!;

        if (decision.Path == SkillActivationPath.Routed)
        {
            if (_subAgentRegistry is null || _subAgentSpawner is null)
            {
                return $"Skill '{name}' routes to subagent '{decision.RoutedSubagent}', but routed skill execution is unavailable in this runtime.";
            }

            if (string.IsNullOrWhiteSpace(args.Task))
            {
                return $"Skill '{name}' routes to subagent '{decision.RoutedSubagent}'. Provide a non-empty task when invoking skill_load for this skill.";
            }

            var profile = _subAgentRegistry.TryGetByName(decision.RoutedSubagent!);
            if (profile is null)
                return SkillActivationRouter.UnknownTargetError(skill.Name, decision.RoutedSubagent!);

            if (profile.Visibility != SubAgentVisibility.UserFacing)
                return SkillActivationRouter.InternalTargetError(skill.Name, decision.RoutedSubagent!);

            string routedContent;
            string routedBody;
            try
            {
                routedContent = File.ReadAllText(skill.FilePath);
                routedBody = SkillScanner.ExtractBody(routedContent);
            }
            catch (IOException ex)
            {
                return $"Failed to read skill file: {ex.Message}";
            }

            var routedScanResult = await _scanner.ScanAsync(name, routedContent, ct);
            if (!routedScanResult.IsAllowed)
                return $"Skill '{name}' blocked by content scan: {routedScanResult.Reason}";

            _sessionMetrics?.RecordSkillLoaded(skill.Name, SkillLoadMethod.SkillLoadTool);

            var routedResult = await _subAgentSpawner.SpawnAsync(
                profile,
                args.Task,
                args.Context,
                context!,
                ct,
                systemPromptOverlay: routedBody);

            return routedResult.Success
                ? routedResult.Output
                : $"Subagent '{profile.Name}' failed: {routedResult.Output}";
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
