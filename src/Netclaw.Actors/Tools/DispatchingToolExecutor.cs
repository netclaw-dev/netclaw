using Microsoft.Extensions.AI;

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

    public Task<string> ExecuteAsync(FunctionCallContent toolCall, CancellationToken ct = default)
    {
        var tool = _registry.GetByName(toolCall.Name);
        if (tool is null)
            return Task.FromResult($"Unknown tool: {toolCall.Name}");

        return tool.ExecuteAsync(toolCall.Arguments, ct);
    }
}
