// -----------------------------------------------------------------------
// <copyright file="McpToolAdapter.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
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
    private readonly IMcpToolInvoker? _invoker;

    /// <summary>
    /// The MCP server's declared input schema, verbatim — the authority for
    /// argument coercion. Distinct from <see cref="ParameterSchema"/>, which is
    /// sanitized for LLM grammar compatibility and shown to the model.
    /// </summary>
    private readonly JsonElement _rawSchema;

    public McpToolAdapter(
        AITool mcpTool,
        string serverName,
        string toolName,
        string? grantCategory = null,
        IMcpToolInvoker? invoker = null,
        int maxDescriptionChars = 0,
        ILogger? logger = null)
    {
        _mcpTool = mcpTool;
        _toolName = toolName;
        _invoker = invoker;
        ServerName = serverName;
        Name = $"{serverName}/{toolName}";
        GrantCategory = grantCategory ?? $"mcp:{serverName}";

        if (mcpTool is AIFunction func)
        {
            Description = ClampDescription(func.Description ?? "", maxDescriptionChars);
            // The raw server schema is the authority for argument coercion.
            // The sanitized schema below is a derivative for LLM grammar
            // compatibility — shown to the model, never coerced against.
            _rawSchema = func.JsonSchema;
            var sanitized = McpSchemaSanitizer.SanitizeSchema(func.JsonSchema);
            ParameterSchema = McpSchemaSanitizer.InjectMetaProperties(sanitized, Name, logger);
            _sanitizedTool = new SanitizedAIFunction(func, Name, Description, ParameterSchema);
        }
        else
        {
            Description = "";
            _rawSchema = default;
            ParameterSchema = default;
            _sanitizedTool = mcpTool;
        }

        // The MCP protocol mandates inputSchema; a tool reaching us without a
        // usable one is anomalous. Coercion degrades to faithful pass-through
        // (the server still validates the call), but surface the anomaly.
        if (_rawSchema.ValueKind != JsonValueKind.Object)
            logger?.LogWarning(
                "MCP tool '{ToolName}' exposes no usable input schema; tool-call arguments will be forwarded without schema-directed coercion",
                Name);
    }

    /// <summary>
    /// Truncates a tool description to fit within the configured character limit.
    /// Default 2KB matches Claude Code's cap: https://code.claude.com/docs/en/mcp
    /// </summary>
    internal static string ClampDescription(string description, int maxChars)
    {
        if (maxChars <= 0 || description.Length <= maxChars)
            return description;

        return description[..maxChars] + " [truncated]";
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

    public async Task<string> ExecuteAsync(
        IDictionary<string, object?>? arguments,
        ToolExecutionContext context,
        CancellationToken ct = default)
    {
        if (_invoker is null)
            return await ExecuteViaBoundToolAsync(arguments, ct);

        try
        {
            var stripped = McpSchemaSanitizer.StripMetaFields(arguments);
            var normalized = McpSchemaSanitizer.NormalizeArgumentKeys(stripped, ParameterSchema);
            var coerced = McpSchemaSanitizer.CoerceArguments(normalized, _rawSchema);
            return await _invoker.InvokeAsync(ServerName, _toolName, coerced, context, ct);
        }
        catch (Exception ex)
        {
            return $"Error: MCP tool '{Name}' failed: {ex.Message}";
        }
    }

    public async Task<string> ExecuteAsync(IDictionary<string, object?>? arguments, CancellationToken ct = default)
        => await ExecuteViaBoundToolAsync(arguments, ct);

    private async Task<string> ExecuteViaBoundToolAsync(IDictionary<string, object?>? arguments, CancellationToken ct)
    {
        if (_mcpTool is not AIFunction func)
            return "Error: MCP tool is not invocable.";

        try
        {
            var stripped = McpSchemaSanitizer.StripMetaFields(arguments);
            var normalized = McpSchemaSanitizer.NormalizeArgumentKeys(stripped, ParameterSchema);
            var coerced = McpSchemaSanitizer.CoerceArguments(normalized, _rawSchema);
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
        private readonly string _description;
        private readonly JsonElement _sanitizedSchema;

        public SanitizedAIFunction(AIFunction inner, string namespacedName, string description, JsonElement sanitizedSchema)
        {
            _inner = inner;
            _namespacedName = namespacedName;
            _description = description;
            _sanitizedSchema = sanitizedSchema;
        }

        // Use the namespaced name (e.g., "memorizer/search_memories") so the LLM
        // calls the tool by the same name it's registered under in ToolRegistry.
        public override string Name => _namespacedName;
        public override string Description => _description;
        public override JsonElement JsonSchema => _sanitizedSchema;

        protected override ValueTask<object?> InvokeCoreAsync(
            AIFunctionArguments arguments, CancellationToken cancellationToken)
        {
            var stripped = McpSchemaSanitizer.StripMetaFields(arguments);
            var normalized = McpSchemaSanitizer.NormalizeArgumentKeys(stripped, _sanitizedSchema);
            var coerced = McpSchemaSanitizer.CoerceArguments(normalized, _inner.JsonSchema);
            var coercedArgs = coerced is not null
                ? new AIFunctionArguments(coerced)
                : arguments;
            return _inner.InvokeAsync(coercedArgs, cancellationToken);
        }
    }
}
