// -----------------------------------------------------------------------
// <copyright file="SubAgentDiscoveryContextLayer.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
namespace Netclaw.Configuration;

/// <summary>
/// Dynamic context layer that advertises available subagents to the frontline LLM.
/// Content is updated after startup and MCP discovery by the ToolIndexUpdater.
/// Returns empty for Public audience or when subagents are disabled.
/// </summary>
public sealed class SubAgentDiscoveryContextLayer : IContextLayerProvider
{
    private readonly SubAgentConfig _config;
    private readonly SubAgentDefinitionRegistry? _registry;
    private readonly FileSubAgentDefinitionLoader? _loader;
    private readonly string? _agentsDirectory;
    private volatile string _index = string.Empty;

    public SubAgentDiscoveryContextLayer() : this(new SubAgentConfig()) { }

    public SubAgentDiscoveryContextLayer(
        SubAgentConfig config,
        SubAgentDefinitionRegistry? registry = null,
        FileSubAgentDefinitionLoader? loader = null,
        NetclawPaths? paths = null)
    {
        _config = config;
        _registry = registry;
        _loader = loader;
        _agentsDirectory = paths?.AgentsDirectory;
    }

    public ContextLayerTiming Timing => ContextLayerTiming.EveryTurn;

    /// <summary>
    /// Replace the subagent discovery content. Thread-safe via volatile write.
    /// </summary>
    public void Update(string index) => _index = index;

    public string GetContextLayer(TrustAudience audience)
    {
        if (audience == TrustAudience.Public)
            return string.Empty;
        if (!_config.Enabled)
            return string.Empty;

        RefreshIfNeeded();
        return _index;
    }

    internal static string BuildIndex(IReadOnlyList<SubAgentProfile> agents, string agentsDirectory)
    {
        if (agents.Count == 0)
        {
            var deniedTools = string.Join(", ", SubAgentToolPolicy.GetDeniedSubAgentTools());
            return string.Join('\n',
            [
                "[available-subagents — use spawn_agent to delegate]",
                string.Empty,
                "No user-facing subagents are currently registered.",
                $"Agents directory: {agentsDirectory}",
                "Sub-agents inherit the parent session's tool policy.",
                $"Denied tools for sub-agents: {deniedTools}",
                string.Empty,
                "To add one: create an agent definition at <agents-directory>/<name>.md. The next turn or subagent lookup reloads it automatically.",
                "Then call `spawn_agent(agent: \"<name>\", task: \"<specific task>\", context: \"<optional background>\")`."
            ]);
        }

        var lines = new List<string>
        {
            "[available-subagents — use spawn_agent to delegate]",
            string.Empty
        };

        foreach (var agent in agents)
        {
            lines.Add($"## {agent.Name}");
            lines.Add(agent.Description);
            lines.Add(agent.ToolNames.Count == 0
                ? "Tools: inherited from parent session policy, except denied sub-agent tools"
                : $"Tools: {string.Join(", ", agent.ToolNames)} (except denied sub-agent tools)");
            lines.Add($"Timeout: {agent.TimeoutSeconds}s");
            lines.Add(string.Empty);
        }

        lines.Add("## How to delegate");
        lines.Add("Call `spawn_agent(agent: \"<name>\", task: \"<specific task>\", context: \"<optional background>\")`.");
        lines.Add(string.Empty);
        lines.Add("- `task` is what the subagent should do — be concrete and bounded.");
        lines.Add("- `context` is optional per-invocation background (workspace details, the user's broader goal,");
        lines.Add("  facts the subagent would otherwise have to rediscover). Do NOT duplicate the agent's built-in");
        lines.Add("  instructions — use this for THIS invocation's situation.");
        lines.Add("- Subagents run autonomously with their own tools and return a synthesized result, not a transcript.");

        return string.Join('\n', lines);
    }

    private void RefreshIfNeeded()
    {
        if (_loader is null || _registry is null || string.IsNullOrWhiteSpace(_agentsDirectory))
            return;

        var changed = _loader.SyncInto(_registry);
        if (changed || string.IsNullOrWhiteSpace(_index))
            _index = BuildIndex(_registry.GetUserFacing(), _agentsDirectory);
    }
}
