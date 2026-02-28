using Microsoft.Extensions.AI;
using Netclaw.Tools;

namespace Netclaw.Actors.Tools;

/// <summary>
/// Routes <see cref="FunctionCallContent"/> to the correct tool by name via the <see cref="ToolRegistry"/>.
/// </summary>
public sealed class DispatchingToolExecutor : IToolExecutor
{
    private readonly ToolRegistry _registry;

    public DispatchingToolExecutor(ToolRegistry registry)
    {
        _registry = registry;
    }

    public Task<string> ExecuteAsync(FunctionCallContent toolCall, ToolExecutionContext? context = null, CancellationToken ct = default)
    {
        var tool = _registry.GetByName(toolCall.Name);
        if (tool is null)
            return Task.FromResult($"Unknown tool: {toolCall.Name}");

        return context is not null
            ? tool.ExecuteAsync(toolCall.Arguments, context, ct)
            : tool.ExecuteAsync(toolCall.Arguments, ct);
    }
}
