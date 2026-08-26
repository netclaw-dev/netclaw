// -----------------------------------------------------------------------
// <copyright file="McpToolAdapter.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Net;
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
        // LLM-facing alias: replaces `/` with `__` to satisfy the
        // Anthropic tool-name regex (^[a-zA-Z0-9_-]{1,128}$). Surfaced
        // to the model in tool definitions and echoed back on tool
        // result messages; everything else (audit log, approvals,
        // registry keys, CLI) stays canonical via Name.
        LlmFacingName = LlmFacingToolName.FromCanonical(Name);
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
            _sanitizedTool = new SanitizedAIFunction(func, LlmFacingName.Value, Description, ParameterSchema);
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
    public LlmFacingToolName LlmFacingName { get; }
    public string Description { get; }
    public string GrantCategory { get; }
    public string ServerName { get; }
    public JsonElement ParameterSchema { get; }

    /// <summary>The bare tool name without the server prefix.</summary>
    public string BareToolName => _toolName;

    /// <summary>
    /// MCP servers are trusted, user-configured integrations — the model can only
    /// reach them because the operator added and granted them — so their output is
    /// passed to the model verbatim, matching Claude Code, Cursor, and other MCP
    /// harnesses. <see cref="Netclaw.Security.SecretOutputRedactor"/> remains on for
    /// genuinely-untrusted sources (shell, file reads, web fetch, background jobs);
    /// applying it to MCP results corrupts legitimate payloads such as presigned
    /// upload URLs, whose signed query parameters look like live credentials.
    /// </summary>
    public bool SuppressOutputRedaction => true;

    /// <summary>
    /// Returns the AITool with a sanitized JSON schema for LLM compatibility.
    /// </summary>
    public AITool ToAITool() => _sanitizedTool;

    public async Task<string> ExecuteAsync(
        IDictionary<string, object?>? arguments,
        ToolInvocationContext context,
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
        // Cancellation must not become a tool result. OperationCanceledException is an
        // Exception, so without this clause the catch-all below would swallow a caller's
        // abort and hand the agent "Error: ... The operation was canceled." as if the tool
        // had merely misbehaved, and the agent would carry on.
        //
        // The filter matters as much as the rethrow: HttpClient timeouts surface as
        // TaskCanceledException, which derives from OperationCanceledException. Those fire
        // while ct is NOT cancelled, and they are faults rather than caller intent, so they
        // fall through to the catch-all and come back as an actionable error instead of
        // tearing down the caller's operation.
        // Same guard as ExecuteAsync: without this the catch-all below turns a caller's
        // cancellation into a tool result, and the filter keeps HttpClient timeouts
        // (TaskCanceledException) classified as faults rather than caller intent.
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        // The adapter completes the receipt here because the exception stops here: the
        // dispatcher never sees it and completes the receipt as Success. The receipt is
        // first-writer-wins, so this category is the one the actor reads.
        catch (Exception ex)
        {
            var text = $"Error: MCP tool '{Name}' failed: {ex.Message}";
            return ex switch
            {
                HttpRequestException
                    {
                        StatusCode: HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden
                    } => context.AccessDenied(text),
                HttpRequestException { StatusCode: HttpStatusCode.NotFound } => context.NotFound(text),
                _ => context.TransientFailure(text)
            };
        }
    }

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
            return McpToolResultFormatter.Format(result, Name);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
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
