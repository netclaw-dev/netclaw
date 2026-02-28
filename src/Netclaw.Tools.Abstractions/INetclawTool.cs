using System.Text.Json;
using Microsoft.Extensions.AI;

namespace Netclaw.Tools;

/// <summary>
/// A self-contained tool definition: schema, metadata, and execution in one type.
/// </summary>
public interface INetclawTool
{
    /// <summary>Tool name as seen by the LLM.</summary>
    string Name { get; }

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
}
