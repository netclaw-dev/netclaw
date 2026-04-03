using Microsoft.Extensions.AI;
using Netclaw.Tools;

namespace Netclaw.Actors.Tools;

/// <summary>
/// Executes a tool call and returns the result string.
/// Implementations handle the actual tool invocation (shell, web, MCP, etc.).
/// </summary>
public interface IToolExecutor
{
    Task<string> ExecuteAsync(FunctionCallContent toolCall, ToolExecutionContext? context = null, CancellationToken ct = default);
}

/// <summary>
/// Audit entry for tool invocations. Logged regardless of allow/deny.
/// </summary>
public sealed record ToolAuditEntry
{
    public required string SessionId { get; init; }
    public required string ToolName { get; init; }
    public required string CallId { get; init; }
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
}

/// <summary>
/// Receives tool invocation audit entries. Default implementation logs them.
/// </summary>
public interface IToolAuditLogger
{
    void Log(ToolAuditEntry entry);
}
