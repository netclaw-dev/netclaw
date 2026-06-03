// -----------------------------------------------------------------------
// <copyright file="DispatchingToolExecutor.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Diagnostics;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Netclaw.Configuration;
using Netclaw.Security;
using Netclaw.Tools;

namespace Netclaw.Actors.Tools;

/// <summary>
/// Routes <see cref="FunctionCallContent"/> to the correct tool by name via the <see cref="ToolRegistry"/>.
/// Logs every tool execution with name, duration, and result preview.
/// </summary>
public sealed class DispatchingToolExecutor : IToolExecutor
{
    private readonly ToolRegistry _registry;
    private readonly ToolAccessPolicy _policy;
    private readonly IToolApprovalService? _approvalService;
    private readonly ILogger _logger;

    public DispatchingToolExecutor(ToolRegistry registry, ILogger<DispatchingToolExecutor>? logger = null)
        : this(
            registry,
            new ToolAccessPolicy(
                new ToolConfig { ShellMode = ShellExecutionMode.HostAllowed },
                new EffectivePolicyDefaults(
                DeploymentPosture.Personal,
                TrustAudience.Personal,
                ShellExecutionMode.HostAllowed,
                UsedStrictFallback: false),
                new ShellCommandPolicy()),
            approvalService: null,
            logger)
    {
    }

    public DispatchingToolExecutor(ToolRegistry registry, ToolAccessPolicy policy,
        IToolApprovalService? approvalService = null, ILogger<DispatchingToolExecutor>? logger = null)
    {
        _registry = registry;
        _policy = policy;
        _approvalService = approvalService;
        _logger = logger ?? (ILogger)NullLogger.Instance;
    }

    public async Task<string> ExecuteAsync(FunctionCallContent toolCall, ToolExecutionContext? context = null, CancellationToken ct = default)
    {
        if (_registry.GetByName(toolCall.Name) is null)
        {
            _logger.LogWarning("Unknown tool requested: {ToolName}", toolCall.Name);
            return $"Unknown tool: {toolCall.Name}";
        }

        var tool = await AuthorizeCoreAsync(toolCall, context, ct);

        var sw = Stopwatch.StartNew();
        try
        {
            var result = context is not null
                ? await tool.ExecuteAsync(toolCall.Arguments, context, ct)
                : await tool.ExecuteAsync(toolCall.Arguments, ct);

            result = SecretOutputRedactor.Redact(result);
            // Single, uniform bounding+spill point for every tool (main session and
            // sub-agents both funnel through here): cap the inline result to the
            // tool's budget and, when it overflows, spill the full redacted result
            // to a session file and steer the model to read a slice. Tools only
            // bound their own capture for memory safety; they do not window or spill.
            result = await ToolOutputSpill.BoundAndSpillAsync(
                result, toolCall.CallId, ResolveInlineBudget(tool, context), context, ct);

            sw.Stop();
            _logger.LogInformation(
                "Tool executed: {ToolName} ({Duration}ms, {ResultLength} chars)",
                toolCall.Name, sw.ElapsedMilliseconds, result.Length);

            return result;
        }
        catch (Exception ex)
        {
            sw.Stop();
            _logger.LogError(ex,
                "Tool execution failed: {ToolName} ({Duration}ms)",
                toolCall.Name, sw.ElapsedMilliseconds);
            throw;
        }
    }

    public async Task AuthorizeAsync(FunctionCallContent toolCall, ToolExecutionContext? context = null, CancellationToken ct = default)
    {
        _ = await AuthorizeCoreAsync(toolCall, context, ct);
    }

    // The tool's own override (verbose tools like shell opt down) wins; otherwise
    // the session content budget; otherwise the built-in content default.
    private static int ResolveInlineBudget(INetclawTool tool, ToolExecutionContext? context)
        => tool.InlineOutputBudgetChars is > 0 and var toolBudget
            ? toolBudget
            : context?.MaxInlineToolResultChars is > 0 and var contentBudget
                ? contentBudget
                : ToolOutputSpill.DefaultContentBudget;

    public async IAsyncEnumerable<ToolCallUpdate> ExecuteStreamAsync(
        FunctionCallContent toolCall,
        ToolExecutionContext? context = null,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        if (_registry.GetByName(toolCall.Name) is null)
        {
            _logger.LogWarning("Unknown tool requested: {ToolName}", toolCall.Name);
            yield return new ToolCompletedUpdate($"Unknown tool: {toolCall.Name}");
            yield break;
        }

        // Authorization throws (ToolApprovalRequiredException / ToolAccessDeniedException)
        // before the first item is produced; the tool-execution pipeline handles
        // those exactly as it does for the non-streaming path.
        var tool = await AuthorizeCoreAsync(toolCall, context, ct);
        var execContext = context ?? ToolExecutionContext.Empty;

        var sw = Stopwatch.StartNew();
        await foreach (var update in tool.ExecuteStreamAsync(toolCall.Arguments, execContext, ct))
        {
            switch (update)
            {
                case ToolCompletedUpdate completed:
                    sw.Stop();
                    var redacted = SecretOutputRedactor.Redact(completed.Result);
                    redacted = await ToolOutputSpill.BoundAndSpillAsync(
                        redacted, toolCall.CallId, ResolveInlineBudget(tool, context), context, ct);
                    _logger.LogInformation(
                        "Tool executed: {ToolName} ({Duration}ms, {ResultLength} chars)",
                        toolCall.Name, sw.ElapsedMilliseconds, redacted.Length);
                    yield return new ToolCompletedUpdate(redacted);
                    break;
                case ToolActivityUpdate { OutputChunk: not null } activity:
                    yield return activity with { OutputChunk = SecretOutputRedactor.Redact(activity.OutputChunk) };
                    break;
                default:
                    yield return update;
                    break;
            }
        }
    }

    private async Task<INetclawTool> AuthorizeCoreAsync(FunctionCallContent toolCall, ToolExecutionContext? context, CancellationToken ct)
    {
        if (context is not null)
        {
            context.AppliedApprovalDecision = null;
            context.AppliedApprovalPattern = null;
        }

        var tool = _registry.GetByName(toolCall.Name);
        if (tool is null)
        {
            _logger.LogWarning("Unknown tool requested: {ToolName}", toolCall.Name);
            throw new ToolAccessDeniedException("tool_not_found");
        }

        var accessDecision = _policy.AuthorizeInvocation(tool, context, toolCall.Arguments);

        if (accessDecision.NeedsApproval && _approvalService is not null)
        {
            var approvalContext = accessDecision.ApprovalContext
                ?? throw new InvalidOperationException("Approval decision missing approval context.");

            // Cwd resolution happens upstream in ToolAccessPolicy.CheckApprovalGate
            // for shell tools, so context.Cwd is already populated when the
            // gate produced an approval context. Other tools have no
            // directory anchor; cwd stays null.

            // Messy commands cannot be persistently approved — the matcher
            // refuses to extract verb chains we could match a future
            // invocation against. Always round-trip through the user, even if
            // the candidate-verbs list happens to be empty for unrelated
            // reasons (which would otherwise short-circuit to allow).
            if (approvalContext.IsMessy)
            {
                accessDecision = ToolAccessDecision.RequiresApproval(approvalContext);
            }
            else
            {
                var audience = SecurityPolicyDefaults.ResolveAudienceWithFallback(
                    context?.Audience, context?.SessionId);

                // Pure side-effect candidates (echo "X" with no path/redirect,
                // bash :, true/false) are not persisted on Always-here clicks
                // and must also be treated as authorized at match time —
                // otherwise the matcher would see them as unapproved on retry
                // after the click, throw ToolApprovalRequiredException again,
                // and fail the turn (the outer try/catch is already inside
                // the conditional catch so a re-throw escapes).
                var candidatesForCheck = approvalContext.Candidates is { Count: > 0 } candidates
                    ? candidates
                        .Where(c => !ApprovalPatternMatching.IsPureSideEffect(c))
                        .ToList()
                    : approvalContext.CandidateVerbs
                        .Select(verb => new ApprovalCandidate(verb, Directory: null))
                        .ToList();

                if (candidatesForCheck.Count == 0)
                {
                    // Every candidate is side-effect-only — auto-allow.
                    accessDecision = ToolAccessDecision.Allow();
                }
                else
                {
                    // Use tool.Name (canonical) — not toolCall.Name — so the
                    // lookup key matches what PersistApprovalCandidatesAsync
                    // stored. For MCP tools the LLM-facing name is the
                    // sanitized alias (`server__tool`), while the policy
                    // builds the approval context — and the session actor
                    // records the grant — under the canonical `server/tool`.
                    // Looking up by the sanitized alias here would miss every
                    // grant and re-throw ToolApprovalRequiredException on
                    // approved retries.
                    var approvalCheck = await _approvalService.CheckApprovalAsync(
                        ToApprovalSessionId(context?.SessionId),
                        audience,
                        new ToolName(tool.Name),
                        candidatesForCheck,
                        context?.Cwd,
                        ct);

                    if (approvalCheck.UnapprovedPatterns.Count == 0 && context is not null)
                    {
                        context.AppliedApprovalDecision = "PreviouslyApproved";
                        context.AppliedApprovalPattern = FormatApprovalMatches(approvalCheck.ApprovedMatches);
                    }

                    accessDecision = approvalCheck.UnapprovedPatterns.Count == 0
                        ? ToolAccessDecision.Allow()
                        : ToolAccessDecision.RequiresApproval(approvalContext);
                }
            }
        }

        if (accessDecision.NeedsApproval
            && context is not null
            && IsOneTimeApprovalSatisfied(context, toolCall, accessDecision.ApprovalContext))
        {
            _logger.LogInformation(
                "Applying one-time approval bypass for tool {ToolName} in session {SessionId}",
                toolCall.Name,
                context.SessionId ?? "unknown");
            accessDecision = ToolAccessDecision.Allow();
        }

        if (accessDecision.NeedsApproval)
        {
            _logger.LogInformation("Tool requires approval: {ToolName}", toolCall.Name);
            throw new ToolApprovalRequiredException(accessDecision.ApprovalContext!);
        }

        if (!accessDecision.Allowed)
        {
            _logger.LogWarning("Tool denied by policy: {ToolName} reason={Reason}", toolCall.Name, accessDecision.DenyReason);
            throw new ToolAccessDeniedException(accessDecision.DenyReason ?? "tool_denied");
        }

        return tool;
    }

    private static string FormatApprovalMatches(IReadOnlyList<ToolApprovalMatch> matches)
        => string.Join(", ", matches.Select(match => $"{match.Pattern} [{match.Source}: {match.Scope}]"));

    private static ToolApprovalSessionId? ToApprovalSessionId(string? sessionId)
        => sessionId is null ? null : (ToolApprovalSessionId)sessionId;

    private static bool IsOneTimeApprovalSatisfied(
        ToolExecutionContext context,
        FunctionCallContent toolCall,
        ToolApprovalContext? approvalContext)
    {
        if (approvalContext is null)
            return false;

        // Tool-name match is required for any one-time bypass — without it
        // we could never tell which tool the grant applies to.
        if (!string.IsNullOrEmpty(context.OneTimeApprovedToolName)
            && !string.Equals(context.OneTimeApprovedToolName, toolCall.Name, StringComparison.Ordinal))
            return false;

        // By this point: either OneTimeApprovedToolName is empty (no
        // bypass active), or it matched toolCall.Name above. Messy commands
        // have no extractable patterns, so an active per-tool ApprovedOnce
        // bypass is the only signal we can use — without this branch a
        // retry would hit the empty-patterns guard below and throw
        // ToolApprovalRequiredException. The pipeline clears
        // OneTimeApprovedToolName after the retry, so the bypass cannot
        // leak into a subsequent call.
        if (approvalContext.IsMessy && !string.IsNullOrEmpty(context.OneTimeApprovedToolName))
            return true;

        if (context.OneTimeApprovedPatterns.Count == 0)
            return false;

        if (approvalContext.Patterns.Count == 0)
            return false;

        if (string.IsNullOrEmpty(context.OneTimeApprovedToolName))
            return false;

        return approvalContext.Patterns.All(pattern => context.OneTimeApprovedPatterns.Contains(pattern));
    }
}
