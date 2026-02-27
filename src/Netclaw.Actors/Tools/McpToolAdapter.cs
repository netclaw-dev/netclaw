using System.Text.Json;
using Microsoft.Extensions.AI;
using Netclaw.Tools;

namespace Netclaw.Actors.Tools;

/// <summary>
/// Wraps an <see cref="AITool"/> from an MCP server as <see cref="INetclawTool"/>.
/// Tool names are namespaced as <c>{serverName}/{toolName}</c> to avoid collisions.
/// </summary>
public sealed class McpToolAdapter : INetclawTool
{
    private readonly AITool _mcpTool;
    private readonly string _toolName;

    public McpToolAdapter(AITool mcpTool, string serverName, string toolName, string? grantCategory = null)
    {
        _mcpTool = mcpTool;
        _toolName = toolName;
        ServerName = serverName;
        Name = $"{serverName}/{toolName}";
        GrantCategory = grantCategory ?? $"mcp:{serverName}";

        // Extract description and schema from the underlying AITool
        if (mcpTool is AIFunction func)
        {
            Description = func.Description ?? "";
            ParameterSchema = func.JsonSchema;
        }
        else
        {
            Description = "";
            ParameterSchema = default;
        }
    }

    public string Name { get; }
    public string Description { get; }
    public string GrantCategory { get; }
    public string ServerName { get; }
    public JsonElement ParameterSchema { get; }

    /// <summary>The bare tool name without the server prefix.</summary>
    public string BareToolName => _toolName;

    public AITool ToAITool() => _mcpTool;

    public async Task<string> ExecuteAsync(IDictionary<string, object?>? arguments, CancellationToken ct = default)
    {
        if (_mcpTool is not AIFunction func)
            return "Error: MCP tool is not invocable.";

        try
        {
            var aiArgs = arguments is not null
                ? new AIFunctionArguments(arguments)
                : null;
            var result = await func.InvokeAsync(aiArgs, ct);
            return result?.ToString() ?? "";
        }
        catch (Exception ex)
        {
            return $"Error: MCP tool '{Name}' failed: {ex.Message}";
        }
    }
}
