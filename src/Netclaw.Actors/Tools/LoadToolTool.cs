// -----------------------------------------------------------------------
// <copyright file="LoadToolTool.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.ComponentModel;
using Netclaw.Tools;

namespace Netclaw.Actors.Tools;

/// <summary>
/// Activates a discovered MCP tool by name so the LLM can call it.
/// Tools must first be found via <see cref="SearchToolsTool"/> before loading.
/// The session actor intercepts this tool by name and reads the tool call
/// arguments to activate the requested tool — the output is LLM-facing only.
/// </summary>
[NetclawTool("load_tool",
    "Activate a tool by name so you can call it. Use search_tools first to find tool names, then load_tool to activate one.",
    Grant = "builtin")]
public sealed partial class LoadToolTool : NetclawTool<LoadToolTool.Params>
{
    private readonly ToolRegistry _registry;
    private readonly ToolAccessPolicy? _policy;

    public record Params(
        [property: Description("Full tool name to activate (e.g., 'notion/notion-search'). Use search_tools to find available tool names.")]
        string Name);

    public LoadToolTool(ToolRegistry registry, ToolAccessPolicy? policy = null)
    {
        _registry = registry;
        _policy = policy;
    }

    protected override Task<string> ExecuteAsync(Params args, ToolExecutionContext context, CancellationToken ct)
    {
        var name = args.Name.Trim();

        if (string.IsNullOrEmpty(name))
            return Task.FromResult("Error: tool name is required.");

        var registration = _registry.GetRegistrationByToolName(name);
        if (registration is null)
        {
            var suggestions = _registry.SearchTools(name, null, 3);
            if (suggestions.Count > 0)
            {
                var names = string.Join(", ", suggestions.Select(s => s.Name));
                return Task.FromResult($"Tool '{name}' not found. Did you mean: {names}?");
            }

            return Task.FromResult($"Tool '{name}' not found. Use search_tools to find available tools.");
        }

        if (_policy is not null && !_policy.IsToolExposed(registration.Tool, context))
            return Task.FromResult($"Tool '{name}' is not available in the current trust context.");

        // Return the canonical tool name. The session actor intercepts load_tool
        // results and attempts a registry lookup on the content to activate the tool.
        // Error messages above will not match any registry entry, so only successful
        // loads trigger activation — no string parsing required.
        return Task.FromResult(registration.Tool.Name);
    }

    protected override Task<string> ExecuteAsync(Params args, CancellationToken ct)
        => ExecuteAsync(args, ToolExecutionContext.Empty, ct);
}
