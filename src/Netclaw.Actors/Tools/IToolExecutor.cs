// -----------------------------------------------------------------------
// <copyright file="IToolExecutor.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;
using Netclaw.Actors.Protocol;
using Netclaw.Actors.Sessions.Pipelines;
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
    /// Pre-dispatch argument validation shared by every caller (main session
    /// pipeline AND sub-agent loop AND any direct caller): provider args-parse
    /// failure, present-but-invalid meta values, and unrecognized argument keys.
    /// Returns null when the call may proceed, otherwise a model-facing
    /// rejection. Centralizing here is what keeps the no-silent-discard
    /// invariant from holding only on the pipeline path. The default returns
    /// null so test fakes need not implement it; <see cref="DispatchingToolExecutor"/>
    /// provides the real check.
    /// </summary>
    ToolArgumentRejection? ValidateToolCall(FunctionCallContent toolCall) => null;

    /// <summary>
    /// Validate + extract a tool call in one step — the single execution-preflight
    /// seam BOTH the main session pipeline and the sub-agent loop route through, so
    /// neither can skip validation or drop the meta hints (the sub-agent previously
    /// skipped extraction entirely and silently dropped timeout hints). Returns a
    /// rejection (leaving the call uncleaned) when validation fails; otherwise the
    /// extracted meta and a tool call with meta keys stripped.
    /// <see cref="DispatchingToolExecutor"/> resolves the tool once and runs the real
    /// schema-aware checks; the default here validates nothing and extracts exact-match,
    /// for test fakes.
    /// </summary>
    ToolCallInterpretation InterpretToolCall(FunctionCallContent toolCall)
    {
        var (meta, cleaned) = PrepareToolCall(toolCall);
        return new ToolCallInterpretation(ValidateToolCall(toolCall), meta, cleaned);
    }

    /// <summary>
    /// Extracts per-call meta (<c>_rationale</c>/<c>_timeout_seconds</c>/<c>_background</c>)
    /// and returns it alongside a tool call with the meta keys stripped — extraction
    /// ONLY, no validation (used by the persistence path, which records the model's
    /// message regardless). <see cref="DispatchingToolExecutor"/> resolves meta names
    /// schema-aware: a key that binds to the tool's own declared parameter is forwarded,
    /// never hijacked as meta. The default here is exact-match so test fakes need not
    /// implement it.
    /// </summary>
    (ToolCallMeta? Meta, FunctionCallContent Cleaned) PrepareToolCall(FunctionCallContent toolCall)
        => ToolCallMetaExtractor.Extract(toolCall);

    /// <summary>
    /// Return the liveness mode for the resolved tool. Unknown tools and test
    /// fakes default to opaque so callers keep the conservative wall-clock bound.
    /// </summary>
    ToolLivenessMode GetLivenessMode(FunctionCallContent toolCall) => ToolLivenessMode.Opaque;

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
/// A pre-dispatch tool-argument rejection: the model-facing message and a
/// stable audit reason. Carries the reason so callers audit the denial
/// accurately instead of misreporting a rejected call as executed.
/// </summary>
public sealed record ToolArgumentRejection(string Message, string DenyReason);

/// <summary>
/// Result of <see cref="IToolExecutor.InterpretToolCall"/>: either a
/// <paramref name="Rejection"/> (the call must not run) or the extracted
/// <paramref name="Meta"/> plus the <paramref name="Cleaned"/> tool call with meta
/// keys stripped. On rejection, <paramref name="Cleaned"/> is the original call.
/// </summary>
public sealed record ToolCallInterpretation(
    ToolArgumentRejection? Rejection, ToolCallMeta? Meta, FunctionCallContent Cleaned);

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
