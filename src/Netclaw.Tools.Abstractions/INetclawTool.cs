// -----------------------------------------------------------------------
// <copyright file="INetclawTool.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Extensions.AI;

namespace Netclaw.Tools;

/// <summary>
/// Marker interface for tools provided by a channel adapter (e.g. Slack, Teams).
/// Channel tools are discovered and registered dynamically at startup.
/// </summary>
public interface IChannelTool : INetclawTool;

/// <summary>
/// A self-contained tool definition: schema, metadata, and execution in one type.
/// </summary>
public interface INetclawTool
{
    /// <summary>
    /// Canonical, operator-facing tool identity. Used by the registry,
    /// approval store, audit log, configuration, and CLI. For MCP tools
    /// this is <c>{server}/{tool}</c>. For first-party tools it equals
    /// the <see cref="LlmFacingName"/> value because their names already
    /// satisfy the Anthropic tool-name regex.
    /// </summary>
    string Name { get; }

    /// <summary>
    /// LLM-facing alias for <see cref="Name"/>. Surfaced in tool
    /// definitions sent to the model and echoed back on tool result
    /// messages. Equal to <see cref="Name"/> for first-party tools;
    /// for MCP tools the canonical <c>/</c> separator is replaced with
    /// <c>__</c> so the name satisfies the Anthropic regex.
    /// </summary>
    LlmFacingToolName LlmFacingName { get; }

    /// <summary>Human-readable description included in the tool schema.</summary>
    string Description { get; }

    /// <summary>ACL grant category for policy filtering.</summary>
    string GrantCategory { get; }

    /// <summary>JSON Schema describing the tool's parameters.</summary>
    JsonElement ParameterSchema { get; }

    /// <summary>
    /// Produce an <see cref="AITool"/> for the <c>ChatOptions.Tools</c> boundary.
    /// </summary>
    AITool ToAITool();

    /// <summary>
    /// Execute the tool with raw arguments from the LLM provider.
    /// </summary>
    Task<string> ExecuteAsync(IDictionary<string, object?>? arguments, CancellationToken ct = default);

    /// <summary>
    /// Execute the tool with raw arguments and session execution context.
    /// Default implementation ignores context and delegates to the simple overload.
    /// Tools that need session-scoped state (e.g. temp directories) override this.
    /// </summary>
    Task<string> ExecuteAsync(IDictionary<string, object?>? arguments, ToolExecutionContext context, CancellationToken ct = default)
        => ExecuteAsync(arguments, ct);

    /// <summary>
    /// Execute the tool as a stream of <see cref="ToolCallUpdate"/> items: zero
    /// or more non-terminal <see cref="ToolActivityUpdate"/> items, then exactly
    /// one terminal <see cref="ToolCompletedUpdate"/>. The default implementation
    /// runs the non-streaming context overload and yields its result as a single
    /// completion item, so tools that do not stream behave identically. Long-
    /// running tools override this to emit liveness/progress while they work.
    /// </summary>
    async IAsyncEnumerable<ToolCallUpdate> ExecuteStreamAsync(
        IDictionary<string, object?>? arguments,
        ToolExecutionContext context,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        yield return new ToolCompletedUpdate(await ExecuteAsync(arguments, context, ct));
    }
}
