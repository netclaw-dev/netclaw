using System.ComponentModel;
using Netclaw.Configuration;
using Netclaw.Tools;

namespace Netclaw.Actors.SubAgents;

/// <summary>
/// User-facing tool that delegates a task to a named subagent.
/// The subagent runs autonomously with its own tools and returns a result.
/// Only user-facing agents (not internal platform agents) are invocable.
/// </summary>
[NetclawTool("spawn_agent",
    "Delegate a task to a specialist subagent. "
    + "The subagent runs autonomously with its own tools and returns a result. "
    + "Use the discovery context layer to see available subagents.",
    Grant = "builtin")]
public sealed partial class SpawnAgentTool : NetclawTool<SpawnAgentTool.Params>
{
    private readonly SubAgentDefinitionRegistry _registry;
    private readonly SubAgentSpawner _spawner;
    private readonly NetclawPaths _paths;
    private readonly SubAgentConfig _subAgentConfig;

    public record Params(
        [property: Description("Name of the subagent to invoke (see available-subagents in context)")]
        string Agent,
        [property: Description("Task description for the subagent — be specific about what you need")]
        string Task,
        [property: Description(
            "Optional background context the subagent should consider while working on the task. "
            + "Use this to pass along workspace details, the user's broader goal, or facts the "
            + "subagent would otherwise have to rediscover. Do NOT duplicate the agent's built-in "
            + "instructions; use this for THIS invocation's situation.")]
        string? Context = null);

    public SpawnAgentTool(SubAgentDefinitionRegistry registry, SubAgentSpawner spawner, NetclawPaths paths,
        SubAgentConfig? subAgentConfig = null)
    {
        _registry = registry;
        _spawner = spawner;
        _paths = paths;
        _subAgentConfig = subAgentConfig ?? new SubAgentConfig();
    }

    protected override Task<string> ExecuteAsync(Params args, CancellationToken ct)
        => ExecuteAsync(args, ToolExecutionContext.Empty, ct);

    protected override async Task<string> ExecuteAsync(Params args, ToolExecutionContext context, CancellationToken ct)
    {
        // Defense-in-depth: block subagent spawning for Public audience or when subagent subsystem is disabled
        var audience = SecurityPolicyDefaults.ParseAudienceOrPublic(context.Audience);
        if (audience == TrustAudience.Public || !_subAgentConfig.Enabled)
            return "Error: This tool is not available.";

        if (string.IsNullOrWhiteSpace(args.Agent))
            return "Error: 'agent' parameter is required.";

        if (string.IsNullOrWhiteSpace(args.Task))
            return "Error: 'task' parameter is required.";

        var profile = _registry.TryGetByName(args.Agent);
        if (profile is null || profile.Visibility != SubAgentVisibility.UserFacing)
        {
            var available = _registry.GetUserFacing();
            if (available.Count == 0)
                return $"Error: No subagents are available. Agent '{args.Agent}' not found. Author one at {_paths.AgentsDirectory}/*.md or define a skill with metadata.subagent once #661 lands.";

            var names = string.Join(", ", available.Select(a => a.Name));
            return $"Error: Unknown agent '{args.Agent}'. Available agents: {names}";
        }

        var result = await _spawner.SpawnAsync(profile, args.Task, args.Context, context!, ct);

        return result.Success
            ? result.Output
            : $"Subagent '{args.Agent}' failed: {result.Output}";
    }
}
