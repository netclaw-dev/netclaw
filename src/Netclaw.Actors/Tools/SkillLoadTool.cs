// -----------------------------------------------------------------------
// <copyright file="SkillLoadTool.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.ComponentModel;
using Netclaw.Actors.SubAgents;
using System.Text;
using Microsoft.Extensions.Logging;
using Netclaw.Actors.Skills;
using Netclaw.Actors.Telemetry;
using Netclaw.Configuration;
using Netclaw.Security.Skills;
using Netclaw.Tools;
using Netclaw.Security;

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
    private readonly IMcpPromptSkillLoader _mcpPromptLoader;
    private readonly ISessionMetrics? _sessionMetrics;
    private readonly SubAgentDefinitionRegistry? _subAgentRegistry;
    private readonly SubAgentSpawner? _subAgentSpawner;
    private readonly SkillSyncConfig _skillSyncConfig;
    private readonly FileSubAgentDefinitionLoader? _subAgentLoader;
    private readonly ILogger? _logger;

    public record Params(
        [property: Description("Name of the skill to load (e.g., 'search-citation', 'netclaw-memory')")]
        string Name,
        [property: Description("Optional task used when the skill routes via metadata.subagent. Required for routed skill execution.")]
        string? Task = null,
        [property: Description("Optional runtime context passed to the routed subagent for this invocation.")]
        string? Context = null,
        [property: Description("Optional argument values for an MCP prompt skill. Use the names from the skill index.")]
        IReadOnlyDictionary<string, string>? Arguments = null);

    public SkillLoadTool(
        SkillRegistry skillRegistry,
        ISkillContentScanner scanner,
        IMcpPromptSkillLoader mcpPromptLoader,
        ISessionMetrics? sessionMetrics = null,
        SubAgentDefinitionRegistry? subAgentRegistry = null,
        SubAgentSpawner? subAgentSpawner = null,
        SkillSyncConfig? skillSyncConfig = null,
        ILogger<SkillLoadTool>? logger = null,
        FileSubAgentDefinitionLoader? subAgentLoader = null)
    {
        _skillRegistry = skillRegistry;
        _scanner = scanner;
        _mcpPromptLoader = mcpPromptLoader;
        _sessionMetrics = sessionMetrics;
        _subAgentRegistry = subAgentRegistry;
        _subAgentSpawner = subAgentSpawner;
        _skillSyncConfig = skillSyncConfig ?? new SkillSyncConfig();
        _subAgentLoader = subAgentLoader;
        _logger = logger;
    }

    protected override async Task<string> ExecuteAsync(Params args, ToolInvocationContext context, CancellationToken ct)
    {
        // Defense-in-depth: block skill loading for Public audience or when skills subsystem is disabled
        var audience = context.Audience;
        if (audience == TrustAudience.Public || !_skillSyncConfig.Enabled)
            return "Error: This tool is not available.";

        var name = args.Name.Trim().ToLowerInvariant();
        var skill = _skillRegistry.GetByName(name);

        if (skill is null)
        {
            // The fallback still lists only FILE skills by name: MCP prompt visibility
            // is audience-filtered, and enumerating names here could reveal an MCP server
            // or prompt the session is denied. But it must NOT imply MCP prompts are
            // unavailable — a lookup miss on a file skill is not "no such capability".
            // The pointer at the [skills] index is UNCONDITIONAL on purpose: gating it on
            // the registry would leak whether any MCP prompts exist to a session whose
            // audience is denied all of them, and the session's own index is already the
            // audience-correct source of truth.
            var fileSkills = _skillRegistry.GetAll()
                .Where(static candidate => candidate.Source is FileSkillSource)
                .Select(static candidate => candidate.Name)
                .ToList();

            var message = fileSkills.Count > 0
                ? $"Skill '{name}' not found. Available file skills: {string.Join(", ", fileSkills)}."
                : $"Skill '{name}' not found. No file skills are currently registered.";
            return message
                + " If your [skills] index lists MCP prompt skills (mcp__<server>__<prompt>), "
                + "load them by that exact name.";
        }

        if (skill.Source is McpPromptSkillSource promptSource)
            return await LoadMcpPromptAsync(skill, promptSource, args.Arguments, context, ct);

        if (args.Arguments is { Count: > 0 })
            return $"Skill '{name}' is file-backed and does not accept MCP prompt arguments.";

        if (skill.Source is not FileSkillSource fileSource)
            return $"Skill '{name}' has an unsupported content source.";

        var decision = SkillActivationRouter.Resolve(skill);
        if (decision.IsError)
            return decision.ErrorMessage!;

        if (decision.Path == SkillActivationPath.Routed)
        {
            if (_subAgentRegistry is null || _subAgentSpawner is null)
            {
                return $"Skill '{name}' routes to subagent '{decision.RoutedSubagent}', but routed skill execution is unavailable in this runtime.";
            }

            _subAgentLoader?.SyncInto(_subAgentRegistry);

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
                routedContent = File.ReadAllText(fileSource.FilePath);
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
            _logger?.LogInformation("turn_skill_loaded skill={SkillName} method=skill_load", skill.Name);

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
            content = File.ReadAllText(fileSource.FilePath);
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
        _logger?.LogInformation("turn_skill_loaded skill={SkillName} method=skill_load", skill.Name);

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

    private async Task<string> LoadMcpPromptAsync(
        SkillEntry skill,
        McpPromptSkillSource source,
        IReadOnlyDictionary<string, string>? arguments,
        ToolInvocationContext context,
        CancellationToken cancellationToken)
    {
        var result = await _mcpPromptLoader.LoadAsync(source, arguments, context, cancellationToken);
        if (!result.Success)
            return result.Error ?? $"MCP prompt skill '{skill.Name}' failed without an error message.";

        _sessionMetrics?.RecordSkillLoaded(skill.Name, SkillLoadMethod.SkillLoadTool);
        _logger?.LogInformation("turn_skill_loaded skill={SkillName} method=skill_load", skill.Name);

        var output = new StringBuilder();
        output.AppendLine($"## {skill.DisplayName}");
        output.AppendLine($"Source: MCP server '{source.ServerName}', prompt '{source.PromptName}', generation {source.Generation}");
        if (!string.IsNullOrWhiteSpace(result.Description))
            output.AppendLine($"Description: {result.Description}");

        foreach (var message in result.Messages)
        {
            output.AppendLine();
            output.AppendLine($"### {message.Role}");
            output.AppendLine(message.Text);
        }

        return output.ToString();
    }
}
