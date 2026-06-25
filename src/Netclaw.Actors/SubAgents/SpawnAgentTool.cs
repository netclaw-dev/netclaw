// -----------------------------------------------------------------------
// <copyright file="SpawnAgentTool.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Netclaw.Configuration;
using Netclaw.Tools;
using static Netclaw.Actors.SubAgents.SubAgentProtocol;

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
    Grant = "builtin",
    Liveness = ToolLivenessMode.SelfMonitoring)]
public sealed partial class SpawnAgentTool : NetclawTool<SpawnAgentTool.Params>
{
    private readonly SubAgentDefinitionRegistry _registry;
    private readonly SubAgentSpawner _spawner;
    private readonly NetclawPaths _paths;
    private readonly SubAgentConfig _subAgentConfig;
    private readonly FileSubAgentDefinitionLoader? _loader;
    private readonly ILogger<SpawnAgentTool>? _logger;

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
        SubAgentConfig? subAgentConfig = null,
        FileSubAgentDefinitionLoader? loader = null,
        ILogger<SpawnAgentTool>? logger = null)
    {
        _registry = registry;
        _spawner = spawner;
        _paths = paths;
        _subAgentConfig = subAgentConfig ?? new SubAgentConfig();
        _loader = loader;
        _logger = logger;
    }

    protected override Task<string> ExecuteAsync(Params args, CancellationToken ct)
        => ExecuteAsync(args, ToolExecutionContext.Empty, ct);

    protected override async Task<string> ExecuteAsync(Params args, ToolExecutionContext context, CancellationToken ct)
    {
        var (error, profile) = Resolve(args, context);
        if (error is not null)
            return error;

        var result = await _spawner.SpawnAsync(profile!, args.Task, args.Context, context, ct);
        return FormatResult(args.Agent, result);
    }

    /// <summary>
    /// Streaming entry point: the sub-agent's liveness/progress activity is surfaced
    /// as the tool call's stream for visibility. The parent applies NO watchdog to
    /// this call — spawn_agent is self-monitoring, so the sub-agent owns its own
    /// liveness and the parent simply drains the stream to its terminal item (see
    /// <c>SessionToolExecutionPipeline</c>).
    /// </summary>
    public override async IAsyncEnumerable<ToolCallUpdate> ExecuteStreamAsync(
        IDictionary<string, object?>? arguments,
        ToolExecutionContext context,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        SubAgentProfile? profile = null;
        if (TryParse(arguments, out var failure, out var args))
            (failure, profile) = Resolve(args, context);

        if (failure is not null)
        {
            yield return new ToolCompletedUpdate(failure);
            yield break;
        }

        // The spawner writes the sub-agent's activity into this channel and
        // completes it (even on failure) when the run ends.
        var channel = Channel.CreateUnbounded<ToolActivityUpdate>();
        var spawnTask = _spawner.SpawnAsync(
            profile!, args!.Task, args.Context, context, ct, activitySink: channel.Writer);
        try
        {
            await foreach (var activity in channel.Reader.ReadAllAsync(ct))
                yield return activity;

            yield return new ToolCompletedUpdate(FormatResult(args.Agent, await spawnTask));
        }
        finally
        {
            // Observe the spawn even when enumeration is abandoned (cancellation),
            // so the sub-agent is stopped before this tool call returns.
            await spawnTask;
        }
    }

    private static string FormatResult(string agent, SubAgentResult result)
        => result.Success ? result.Output : $"Subagent '{agent}' failed: {result.Output}";

    /// <summary>
    /// Validate the invocation and resolve the requested agent. Returns an error
    /// string (and a null profile) when the spawn must be refused, otherwise a
    /// null error and the resolved profile.
    /// </summary>
    private (string? Error, SubAgentProfile? Profile) Resolve(Params args, ToolExecutionContext context)
    {
        // Rejections here return a (sometimes deliberately opaque) error string to
        // the model. Mirror the real reason to the session transcript under the
        // parent session scope so an operator can tell *why* a spawn was refused —
        // the "This tool is not available." string hides the audience-vs-disabled
        // distinction from the model on purpose.
        using var diagnosticsScope = SessionDiagnosticsContext.Push(context.SessionId);

        // Defense-in-depth: block subagent spawning for Public audience or when
        // the subagent subsystem is disabled.
        if (context.Audience == TrustAudience.Public || !_subAgentConfig.Enabled)
        {
            _logger?.LogWarning(
                "spawn_agent refused (agent={Agent}, audience={Audience}, subsystemEnabled={Enabled})",
                args.Agent, context.Audience, _subAgentConfig.Enabled);
            return ("Error: This tool is not available.", null);
        }

        if (string.IsNullOrWhiteSpace(args.Agent))
            return ("Error: 'agent' parameter is required.", null);

        if (string.IsNullOrWhiteSpace(args.Task))
            return ("Error: 'task' parameter is required.", null);

        _loader?.SyncInto(_registry);

        var profile = _registry.TryGetByName(args.Agent);
        if (profile is null || profile.Visibility != SubAgentVisibility.UserFacing)
        {
            var available = _registry.GetUserFacing();
            _logger?.LogWarning(
                "spawn_agent refused: agent '{Agent}' not found or not user-facing (availableCount={Count})",
                args.Agent, available.Count);
            if (available.Count == 0)
                return ($"Error: No subagents are available. Agent '{args.Agent}' not found. Author one at {_paths.AgentsDirectory}/*.md or define a skill with metadata.subagent once #661 lands.", null);

            var names = string.Join(", ", available.Select(a => a.Name));
            return ($"Error: Unknown agent '{args.Agent}'. Available agents: {names}", null);
        }

        return (null, profile);
    }
}
