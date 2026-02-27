using System.Text.Json;
using Microsoft.Extensions.AI;
using Netclaw.Tools;

namespace Netclaw.Actors.Tools;

/// <summary>
/// Wraps an <see cref="AITool"/> from an MCP server as <see cref="INetclawTool"/>.
/// Tool names are namespaced as <c>{serverName}/{toolName}</c> to avoid collisions.
/// Schemas are sanitized for broad LLM compatibility (e.g., Ollama can't handle
/// nullable type arrays) and arguments are coerced from string to native types.
/// </summary>
public sealed class McpToolAdapter : INetclawTool
{
    private readonly AITool _mcpTool;
    private readonly AITool _sanitizedTool;
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
            // Sanitize schema for LLM compatibility (strips nullable unions, etc.)
            ParameterSchema = McpSchemaSanitizer.SanitizeSchema(func.JsonSchema);
            _sanitizedTool = new SanitizedAIFunction(func, Name, ParameterSchema);
        }
        else
        {
            Description = "";
            ParameterSchema = default;
            _sanitizedTool = mcpTool;
        }
    }

    public string Name { get; }
    public string Description { get; }
    public string GrantCategory { get; }
    public string ServerName { get; }
    public JsonElement ParameterSchema { get; }

    /// <summary>The bare tool name without the server prefix.</summary>
    public string BareToolName => _toolName;

    /// <summary>
    /// Returns the AITool with a sanitized JSON schema for LLM compatibility.
    /// </summary>
    public AITool ToAITool() => _sanitizedTool;

    public async Task<string> ExecuteAsync(IDictionary<string, object?>? arguments, CancellationToken ct = default)
    {
        if (_mcpTool is not AIFunction func)
            return "Error: MCP tool is not invocable.";

        try
        {
            // Coerce arguments — some LLMs send numbers as strings
            var coerced = McpSchemaSanitizer.CoerceArguments(arguments);
            var aiArgs = coerced is not null
                ? new AIFunctionArguments(coerced)
                : null;
            var result = await func.InvokeAsync(aiArgs, ct);
            return result?.ToString() ?? "";
        }
        catch (Exception ex)
        {
            return $"Error: MCP tool '{Name}' failed: {ex.Message}";
        }
    }

    /// <summary>
    /// Wraps an <see cref="AIFunction"/> with a sanitized JSON schema while
    /// delegating invocation to the original function.
    /// </summary>
    private sealed class SanitizedAIFunction : AIFunction
    {
        private readonly AIFunction _inner;
        private readonly string _namespacedName;
        private readonly JsonElement _sanitizedSchema;

        public SanitizedAIFunction(AIFunction inner, string namespacedName, JsonElement sanitizedSchema)
        {
            _inner = inner;
            _namespacedName = namespacedName;
            _sanitizedSchema = sanitizedSchema;
        }

        // Use the namespaced name (e.g., "memorizer/search_memories") so the LLM
        // calls the tool by the same name it's registered under in ToolRegistry.
        public override string Name => _namespacedName;
        public override string Description => _inner.Description;
        public override JsonElement JsonSchema => _sanitizedSchema;

        protected override ValueTask<object?> InvokeCoreAsync(
            AIFunctionArguments arguments, CancellationToken cancellationToken)
        {
            // Coerce arguments before forwarding to the MCP client
            var coerced = McpSchemaSanitizer.CoerceArguments(arguments);
            var coercedArgs = coerced is not null
                ? new AIFunctionArguments(coerced)
                : arguments;
            return _inner.InvokeAsync(coercedArgs, cancellationToken);
        }
    }
}
