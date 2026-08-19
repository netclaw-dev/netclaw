// -----------------------------------------------------------------------
// <copyright file="LoadToolTool.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.ComponentModel;
using Netclaw.Tools;

namespace Netclaw.Actors.Tools;

/// <summary>
/// Activates a deferred first-party or MCP tool by name so the LLM can call it.
/// Tools must first be found via <see cref="SearchToolsTool"/> before loading.
/// The owning actor intercepts successful results and activates the requested
/// tool in its private exposure set.
/// </summary>
[NetclawTool("load_tool",
    "Activate a tool by name so you can call it. Use search_tools first to find tool names, then load_tool to activate one.",
    Grant = "builtin")]
public sealed partial class LoadToolTool : NetclawTool<LoadToolTool.Params>
{
    private readonly ToolRegistry _registry;
    private readonly ToolAccessPolicy _policy;

    public record Params(
        [property: Description("Full tool name to activate (e.g., 'notion/notion-search'). Use search_tools to find available tool names.")]
        string Name);

    public LoadToolTool(ToolRegistry registry, ToolAccessPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(policy);

        _registry = registry;
        _policy = policy;
    }

    protected override Task<string> ExecuteAsync(Params args, ToolInvocationContext context, CancellationToken ct)
    {
        var name = args.Name.Trim();

        if (string.IsNullOrEmpty(name))
            return Task.FromResult("Error: tool name is required.");

        var registration = _registry.GetRegistrationByToolName(name);
        if (registration is null || !IsVisible(registration.Tool, context))
            return Task.FromResult(BuildNotFound(name, context));

        // Return the LLM-facing alias so the model sees the same form
        // it must emit in a subsequent tool_use call. The session actor
        // intercepts load_tool results and runs the content back through
        // the registry's two-form lookup to activate the tool. Error
        // messages above will not match any registry entry, so only
        // successful loads trigger activation — no string parsing required.
        return Task.FromResult(registration.Tool.LlmFacingName.Value);
    }

    private bool IsVisible(INetclawTool tool, ToolInvocationContext context)
        => _policy.IsToolExposed(tool, context);

    private string BuildNotFound(string authoredName, ToolInvocationContext context)
    {
        var suggestions = _registry.SearchTools(authoredName, null, int.MaxValue)
            .Where(tool => IsVisible(tool, context))
            .Take(3)
            .Select(static tool => tool.LlmFacingName.Value)
            .ToList();

        return suggestions.Count > 0
            ? $"Tool '{authoredName}' not found. Did you mean: {string.Join(", ", suggestions)}?"
            : $"Tool '{authoredName}' not found. Use search_tools to find available tools.";
    }
}
