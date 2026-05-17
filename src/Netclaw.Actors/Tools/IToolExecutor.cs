// -----------------------------------------------------------------------
// <copyright file="IToolExecutor.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;
using Netclaw.Actors.Protocol;
using Netclaw.Tools;

namespace Netclaw.Actors.Tools;

/// <summary>
/// Executes a tool call and returns the result string.
/// Implementations handle the actual tool invocation (shell, web, MCP, etc.).
/// </summary>
public interface IToolExecutor
{
    Task<string> ExecuteAsync(FunctionCallContent toolCall, ToolExecutionContext? context = null, CancellationToken ct = default);

    Task AuthorizeAsync(FunctionCallContent toolCall, ToolExecutionContext? context = null, CancellationToken ct = default);

    /// <summary>
    /// Execute a tool call as a stream of <see cref="ToolCallUpdate"/> items. The
    /// default implementation runs <see cref="ExecuteAsync"/> and yields its
    /// result as a single terminal completion item; <see cref="DispatchingToolExecutor"/>
    /// overrides this to surface the resolved tool's own stream.
    /// </summary>
    async IAsyncEnumerable<ToolCallUpdate> ExecuteStreamAsync(
        FunctionCallContent toolCall,
        ToolExecutionContext? context = null,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        yield return new ToolCompletedUpdate(await ExecuteAsync(toolCall, context, ct));
    }
}

/// <summary>
/// Audit entry for tool invocations. Logged regardless of allow/deny.
/// </summary>
public sealed record ToolAuditEntry
{
    public required SessionId SessionId { get; init; }
    public required ToolName ToolName { get; init; }
    public required ToolCallId CallId { get; init; }
    public required DateTimeOffset Timestamp { get; init; }
    public required bool Allowed { get; init; }
    public string? DenyReason { get; init; }
    public TimeSpan? Duration { get; init; }

    /// <summary>
    /// Approval decision when the tool was gated by the approval system.
    /// Null for tools not in approval mode.
    /// </summary>
    public string? ApprovalDecision { get; init; }

    /// <summary>
    /// The command pattern that was approved/denied (for shell commands).
    /// Null for non-shell tools or tools not in approval mode.
    /// </summary>
    public string? ApprovalPattern { get; init; }

    /// <summary>
    /// LLM-provided rationale for this tool call. Extracted from <c>_rationale</c> meta field.
    /// </summary>
    public string? Rationale { get; init; }

    /// <summary>
    /// LLM-requested timeout in seconds (pre-clamp). Extracted from <c>_timeout_seconds</c> meta field.
    /// </summary>
    public int? TimeoutHintSeconds { get; init; }
}

/// <summary>
/// Receives tool invocation audit entries. Default implementation logs them.
/// </summary>
public interface IToolAuditLogger
{
    void Log(ToolAuditEntry entry);
}
