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

    /// <summary>
    /// Indicates who owns stall detection for this tool. Most tools are opaque:
    /// the parent tool pipeline applies one wall-clock budget. Self-monitoring
    /// tools, such as <c>spawn_agent</c>, report their own terminal failure when
    /// their internal watchdogs detect a stall.
    /// </summary>
    ToolLivenessMode LivenessMode => ToolLivenessMode.Opaque;

    /// <summary>
    /// Inline character budget for this tool's result before the dispatcher
    /// windows it (head+tail) and spills the full output to a session file with a
    /// steer. <c>0</c> means "use the session content budget"
    /// (<c>SessionTuning.MaxInlineToolResultChars</c>) — the default for content
    /// tools whose output the model needs to read. Verbose tools (e.g. shell)
    /// override this to a small value so their noisy output is bounded aggressively.
    /// </summary>
    int InlineOutputBudgetChars => 0;

    /// <summary>
    /// When <c>true</c>, the dispatcher skips <see cref="Netclaw.Security.SecretOutputRedactor"/>
    /// on the result returned to the model. The spill file (if any) is still redacted.
    /// Set this on tools whose output the model may need to write back verbatim
    /// (e.g. file_read) — redacting their output corrupts the content on a
    /// read-modify-write cycle.
    /// </summary>
    bool SuppressOutputRedaction => false;

    /// <summary>JSON Schema describing the tool's parameters.</summary>
    JsonElement ParameterSchema { get; }

    /// <summary>
    /// Produce an <see cref="AITool"/> for the <c>ChatOptions.Tools</c> boundary.
    /// </summary>
    AITool ToAITool();

    /// <summary>
    /// Execute the tool with raw arguments and required execution context.
    /// </summary>
    Task<string> ExecuteAsync(IDictionary<string, object?>? arguments, ToolInvocationContext context, CancellationToken ct = default);

    /// <summary>
    /// Execute the tool as a stream of <see cref="ToolCallUpdate"/> items: zero
    /// or more non-terminal <see cref="ToolActivityUpdate"/> items, then exactly
    /// one terminal <see cref="ToolCompletedUpdate"/>. The default implementation
    /// runs the non-streaming context overload and yields its result as a single
    /// completion item, so tools that do not stream behave identically. Long-
    /// running tools override this to emit live activity while they work.
    /// </summary>
    async IAsyncEnumerable<ToolCallUpdate> ExecuteStreamAsync(
        IDictionary<string, object?>? arguments,
        ToolInvocationContext context,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        yield return new ToolCompletedUpdate(await ExecuteAsync(arguments, context, ct));
    }
}
