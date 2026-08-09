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
using Netclaw.Actors.Sessions.Pipelines;
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

    public DispatchingToolExecutor(ToolRegistry registry, ToolAccessPolicy policy,
        IToolApprovalService? approvalService = null, ILogger<DispatchingToolExecutor>? logger = null)
    {
        _registry = registry;
        _policy = policy;
        _approvalService = approvalService;
        _logger = logger ?? (ILogger)NullLogger.Instance;
    }

    /// <inheritdoc />
    public ToolArgumentRejection? ValidateToolCall(FunctionCallContent toolCall)
        => _registry.GetByName(toolCall.Name) is { } registered
            ? ValidateCore(toolCall, registered, MetaResolverFor(registered))
            : null; // unknown-tool is handled separately by the execute paths

    /// <inheritdoc />
    public ToolCallInterpretation InterpretToolCall(FunctionCallContent toolCall)
    {
        // The single execution-preflight seam: resolve the tool + build the resolver
        // ONCE, then validate and (only on success) extract — so validation and
        // extraction can never disagree, and a caller cannot extract without first
        // validating (the silent-drop footgun). Both the main pipeline and the
        // sub-agent loop route through this.
        if (_registry.GetByName(toolCall.Name) is not { } registered)
            return new ToolCallInterpretation(null, null, toolCall); // unknown tool: execute path reports it

        var resolveMeta = MetaResolverFor(registered);
        if (ValidateCore(toolCall, registered, resolveMeta) is { } rejection)
            return new ToolCallInterpretation(rejection, null, toolCall);

        var (meta, cleaned) = ToolCallMetaExtractor.Extract(toolCall, resolveMeta);
        return new ToolCallInterpretation(null, meta, cleaned);
    }

    /// <inheritdoc />
    public (ToolCallMeta? Meta, FunctionCallContent Cleaned) PrepareToolCall(FunctionCallContent toolCall)
    {
        // Extraction only (no validation) — used by the persistence path, which must
        // record the model's message regardless of whether it would be rejected.
        // Schema-aware; unknown tool → exact-match default (no schema to consult).
        return _registry.GetByName(toolCall.Name) is { } registered
            ? ToolCallMetaExtractor.Extract(toolCall, MetaResolverFor(registered))
            : ToolCallMetaExtractor.Extract(toolCall);
    }

    // Validate against a tool already resolved from the registry, using a resolver
    // built once by the caller — so InterpretToolCall and ValidateToolCall share one
    // definition and never drift. Schema-aware meta resolution (see MetaResolverFor):
    // a key that binds to the tool's OWN declared parameter is forwarded, never
    // hijacked as meta. Meta-value validity and ambiguous double-spellings are checked
    // in ValidateArguments (every tool); unrecognized keys are native-only (MCP
    // servers validate their own schema and reject observably).
    private static ToolArgumentRejection? ValidateCore(
        FunctionCallContent toolCall, INetclawTool registered, Func<string, string?> resolveMeta)
    {
        if (ValidateArguments(toolCall.Arguments, resolveMeta) is { } rejection)
            return rejection;

        if (registered is not McpToolAdapter
            && ToolArgumentValidator.ValidateArgumentKeys(registered, toolCall.Arguments) is { } keyError)
            return new ToolArgumentRejection(keyError, "unrecognized_argument");

        return null;
    }

    private static Func<string, string?> MetaResolverFor(INetclawTool tool)
        => key => ToolArgumentValidator.ResolveMetaField(tool, key);

    /// <inheritdoc />
    public ToolLivenessMode GetLivenessMode(FunctionCallContent toolCall)
        => _registry.GetByName(toolCall.Name)?.LivenessMode ?? ToolLivenessMode.Opaque;

    /// <summary>
    /// The schema-independent half of <see cref="ValidateToolCall"/>: provider
    /// args-parse sentinel + present-but-invalid meta values. Static so it is
    /// the single definition of these rules across the executor and any other
    /// pre-dispatch caller, with no registry needed.
    /// </summary>
    public static ToolArgumentRejection? ValidateArguments(
        IDictionary<string, object?>? args, Func<string, string?>? resolveMeta = null)
    {
        if (args is null || args.Count == 0)
            return null;

        // Provider args-parse failure rides as a sentinel key (set by the
        // OpenAI-compatible client when the model's arguments JSON did not
        // deserialize). Checked first so the sentinel key is not then reported
        // as an "unrecognized argument", and the value is bounded so a
        // forged/oversized value cannot flood the result.
        if (args.TryGetValue(ToolCallArgumentErrors.ArgsParseErrorKey, out var parseFailure))
        {
            return new ToolArgumentRejection(
                $"Error: Tool call arguments were not valid JSON: {ToolArgumentHelper.RenderValue(parseFailure, maxLength: 200)} The tool was NOT executed.",
                "args_parse_error");
        }

        // Present-but-invalid meta values (malformed _timeout_seconds /
        // _background) — the agent expressed execution semantics we cannot
        // honor, so reject rather than run on defaults.
        if (ToolCallMetaExtractor.ValidateMetaValues(args, resolveMeta) is { } metaError)
            return new ToolArgumentRejection(metaError, "invalid_meta_value");

        return null;
    }

    public async Task<string> ExecuteAsync(FunctionCallContent toolCall, ToolExecutionContext context, CancellationToken ct = default)
    {
        if (_registry.GetByName(toolCall.Name) is null)
        {
            _logger.LogWarning("Unknown tool requested: {ToolName}", toolCall.Name);
            return $"Unknown tool: {toolCall.Name}";
        }

        // Pre-dispatch validation runs before authorization so a doomed call
        // never raises an approval prompt. This is the shared seam: callers that
        // bypass the session pipeline (sub-agents, direct callers) get the same
        // protection here. The pipeline preflights via ValidateToolCall too, so
        // for that path this is a cheap idempotent re-check.
        if (ValidateToolCall(toolCall) is { } rejection)
        {
            _logger.LogWarning(
                "Rejected tool call ({Reason}): {ToolName} — {Error}",
                rejection.DenyReason, toolCall.Name, rejection.Message);
            return rejection.Message;
        }

        var tool = await GetAuthorizedToolAsync(toolCall, context, ct);

        var sw = Stopwatch.StartNew();
        try
        {
            var result = await tool.ExecuteAsync(toolCall.Arguments, context.Invocation, ct);

            var redacted = SecretOutputRedactor.Redact(result);

            // Tools that suppress output redaction (e.g. file_read) return the
            // raw result to the model so read-modify-write cycles don't corrupt
            // secret values with ***REDACTED*** placeholders. The spill file
            // (persisted to disk) always uses the redacted version.
            var modelFacing = tool.SuppressOutputRedaction ? result : redacted;

            // Single, uniform bounding+spill point for every tool (main session and
            // sub-agents both funnel through here): cap the inline result to the
            // tool's budget and, when it overflows, spill the full redacted result
            // to a session file and steer the model to read a slice. Tools only
            // bound their own capture for memory safety; they do not window or spill.
            result = await ToolOutputSpill.BoundAndSpillAsync(
                modelFacing, redacted, toolCall.CallId, ResolveInlineBudget(tool, context), context.Invocation, ct);

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

    public async Task AuthorizeAsync(FunctionCallContent toolCall, ToolExecutionContext context, CancellationToken ct = default)
    {
        _ = await GetAuthorizedToolAsync(toolCall, context, ct);
    }

    // The tool's own override (verbose tools like shell opt down) wins; otherwise
    // the session content budget; otherwise the built-in content default.
    private static int ResolveInlineBudget(INetclawTool tool, ToolExecutionContext context)
        => tool.InlineOutputBudgetChars is > 0 and var toolBudget
            ? toolBudget
            : context.MaxInlineToolResultChars is > 0 and var contentBudget
                ? contentBudget
                : ToolOutputSpill.DefaultContentBudget;

    public async IAsyncEnumerable<ToolCallUpdate> ExecuteStreamAsync(
        FunctionCallContent toolCall,
        ToolExecutionContext context,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        if (_registry.GetByName(toolCall.Name) is null)
        {
            _logger.LogWarning("Unknown tool requested: {ToolName}", toolCall.Name);
            yield return new ToolCompletedUpdate($"Unknown tool: {toolCall.Name}");
            yield break;
        }

        // Same pre-authorization validation as the non-streaming path.
        if (ValidateToolCall(toolCall) is { } rejection)
        {
            _logger.LogWarning(
                "Rejected tool call ({Reason}): {ToolName} — {Error}",
                rejection.DenyReason, toolCall.Name, rejection.Message);
            yield return new ToolCompletedUpdate(rejection.Message);
            yield break;
        }

        // Authorization throws (ToolApprovalRequiredException / ToolAccessDeniedException)
        // before the first item is produced; the tool-execution pipeline handles
        // those exactly as it does for the non-streaming path.
        var tool = await GetAuthorizedToolAsync(toolCall, context, ct);
        var sw = Stopwatch.StartNew();
        await foreach (var update in tool.ExecuteStreamAsync(toolCall.Arguments, context.Invocation, ct))
        {
            switch (update)
            {
                case ToolCompletedUpdate completed:
                    sw.Stop();
                    var redacted = SecretOutputRedactor.Redact(completed.Result);
                    var modelResult = tool.SuppressOutputRedaction ? completed.Result : redacted;
                    modelResult = await ToolOutputSpill.BoundAndSpillAsync(
                        modelResult, redacted, toolCall.CallId, ResolveInlineBudget(tool, context), context.Invocation, ct);
                    _logger.LogInformation(
                        "Tool executed: {ToolName} ({Duration}ms, {ResultLength} chars)",
                        toolCall.Name, sw.ElapsedMilliseconds, modelResult.Length);
                    yield return new ToolCompletedUpdate(modelResult);
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

    /// <summary>
    /// Evaluates the complete authorization gate before a tool runs or a user receives a prompt.
    /// </summary>
    /// <remarks>
    /// This method returns expected authorization outcomes instead of exceptions.
    /// Execution adapters translate the result into the existing pipeline exceptions.
    /// </remarks>
    internal async Task<ToolAuthorizationDecision> EvaluateAuthorizationAsync(
        FunctionCallContent toolCall,
        ToolExecutionContext context,
        CancellationToken ct)
    {
        context.Approval.ClearAppliedDecision();

        var tool = _registry.GetByName(toolCall.Name);
        if (tool is null)
        {
            var missingToolDecision = ToolAuthorizationDecision.Deny("tool_not_found");
            LogAuthorizationDecision(toolCall.Name, missingToolDecision);
            return missingToolDecision;
        }

        var accessDecision = _policy.AuthorizeInvocation(tool, context, toolCall.Arguments);
        IReadOnlyList<ToolApprovalMatch> approvalMatches = [];

        if (accessDecision.NeedsApproval && _approvalService is not null)
        {
            var approvalContext = accessDecision.ApprovalContext
                ?? throw new InvalidOperationException("Approval decision missing approval context.");

            // Cwd resolution happens upstream in ToolAccessPolicy.CheckApprovalGate
            // for shell tools, so the attempt Cwd is already populated when the
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
                var audience = context.Audience;

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

                if (approvalContext.Candidates is { Count: > 0 }
                    && candidatesForCheck.Count == 0)
                {
                    // Every candidate is side-effect-only — auto-allow.
                    accessDecision = ToolAccessDecision.Allow(ToolAllowReason.ApprovalExemptShellCandidates);
                }
                else if (candidatesForCheck.Count == 0)
                {
                    // A zero-candidate result does not prove that the command is exempt.
                    // Malformed input or a parser rejection can also produce this result.
                    accessDecision = ToolAccessDecision.RequiresApproval(approvalContext);
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
                        ToApprovalSessionId(context.SessionId),
                        audience,
                        new ToolName(tool.Name),
                        candidatesForCheck,
                        context.Approval.Cwd,
                        ct);
                    approvalMatches = approvalCheck.ApprovedMatches;
                    var hasExactCandidateChecks = TryGetExactUnapprovedCandidates(
                        approvalCheck,
                        candidatesForCheck,
                        out var unapprovedCandidates);
                    var hasInconsistentCandidateChecks = approvalCheck.CandidateChecks is not null
                                                         && !hasExactCandidateChecks;

                    if (approvalCheck.UnapprovedPatterns.Count == 0
                        && !hasInconsistentCandidateChecks)
                    {
                        context.Approval.ApplyDecision(
                            "PreviouslyApproved",
                            FormatApprovalMatches(approvalCheck.ApprovedMatches));
                    }

                    if (approvalCheck.UnapprovedPatterns.Count == 0
                        && !hasInconsistentCandidateChecks)
                    {
                        accessDecision = ToolAccessDecision.Allow(ToolAllowReason.StoredApproval);
                    }
                    else
                    {
                        // New approval services return exact candidate occurrences.
                        // An older implementation can only return verb strings, so
                        // keep the broader context instead of guessing which scoped
                        // candidate lacks approval.
                        var promptContext = hasExactCandidateChecks
                            && unapprovedCandidates.Count > 0
                            && string.Equals(tool.Name, ShellTool.ToolName, StringComparison.Ordinal)
                                ? ToolAccessPolicy.NarrowShellApprovalContext(
                                    approvalContext,
                                    unapprovedCandidates,
                                    context.SessionDirectory)
                                : approvalContext;
                        accessDecision = ToolAccessDecision.RequiresApproval(promptContext);
                    }
                }
            }
        }

        if (accessDecision.NeedsApproval
            && IsOneTimeApprovalSatisfied(context, toolCall, accessDecision.ApprovalContext))
        {
            accessDecision = ToolAccessDecision.Allow(ToolAllowReason.OneTimeApproval);
        }

        var authorizationDecision = CompleteAuthorizationDecision(accessDecision, approvalMatches);
        LogAuthorizationDecision(toolCall.Name, authorizationDecision);
        return authorizationDecision;
    }

    private async Task<INetclawTool> GetAuthorizedToolAsync(
        FunctionCallContent toolCall,
        ToolExecutionContext context,
        CancellationToken ct)
    {
        var decision = await EvaluateAuthorizationAsync(toolCall, context, ct);

        if (decision.Outcome is ToolAuthorizationOutcome.RequiresApproval)
        {
            throw new ToolApprovalRequiredException(
                decision.ApprovalContext
                ?? throw new InvalidOperationException("Approval decision missing approval context."));
        }

        if (decision.Outcome is ToolAuthorizationOutcome.Denied)
        {
            throw new ToolAccessDeniedException(
                decision.DenyReason
                ?? throw new InvalidOperationException("Denied decision missing a deny reason."));
        }

        return _registry.GetByName(toolCall.Name)
            ?? throw new InvalidOperationException("Allowed decision missing its registered tool.");
    }

    private static ToolAuthorizationDecision CompleteAuthorizationDecision(
        ToolAccessDecision accessDecision,
        IReadOnlyList<ToolApprovalMatch> approvalMatches)
    {
        if (accessDecision.NeedsApproval)
        {
            return ToolAuthorizationDecision.RequiresApproval(
                accessDecision.ApprovalContext
                ?? throw new InvalidOperationException("Approval decision missing approval context."),
                approvalMatches);
        }

        if (!accessDecision.Allowed)
        {
            return ToolAuthorizationDecision.Deny(
                accessDecision.DenyReason
                ?? throw new InvalidOperationException("Denied decision missing a deny reason."));
        }

        return ToolAuthorizationDecision.Allow(
            accessDecision.AllowReason
            ?? throw new InvalidOperationException("Allowed decision missing an allow reason."),
            approvalMatches);
    }

    private static bool TryGetExactUnapprovedCandidates(
        ToolApprovalCheckResult result,
        IReadOnlyList<ApprovalCandidate> checkedCandidates,
        out IReadOnlyList<ApprovalCandidate> unapprovedCandidates)
    {
        unapprovedCandidates = [];
        if (result.CandidateChecks is not { } candidateChecks
            || candidateChecks.Count != checkedCandidates.Count)
        {
            return false;
        }

        var exactUnapprovedCandidates = new List<ApprovalCandidate>();
        var exactUnapprovedPatterns = new List<string>();
        var exactApprovedMatches = new List<ToolApprovalMatch>();
        for (var index = 0; index < candidateChecks.Count; index++)
        {
            var check = candidateChecks[index];
            if (check.Candidate != checkedCandidates[index])
                return false;

            if (check.ApprovedMatch is { } approvedMatch)
                exactApprovedMatches.Add(approvedMatch);
            else
            {
                exactUnapprovedCandidates.Add(check.Candidate);
                exactUnapprovedPatterns.Add(check.Candidate.Verb);
            }
        }

        if (!exactUnapprovedPatterns.SequenceEqual(
                result.UnapprovedPatterns,
                StringComparer.OrdinalIgnoreCase)
            || !exactApprovedMatches.SequenceEqual(result.ApprovedMatches))
        {
            return false;
        }

        unapprovedCandidates = exactUnapprovedCandidates;
        return true;
    }

    private void LogAuthorizationDecision(string toolName, ToolAuthorizationDecision decision)
    {
        switch (decision.Outcome)
        {
            case ToolAuthorizationOutcome.Allowed:
                var allowReason = decision.AllowReason
                    ?? throw new InvalidOperationException("Allowed decision missing an allow reason.");
                _logger.LogDebug(
                    "Tool authorization evaluated: {ToolName} outcome={AuthorizationOutcome} " +
                    "reason={AuthorizationReason} explanation={AuthorizationExplanation}",
                    toolName,
                    decision.Outcome.ToString(),
                    allowReason.ToString(),
                    allowReason.GetDescription());
                break;
            case ToolAuthorizationOutcome.RequiresApproval:
                _logger.LogInformation(
                    "Tool authorization evaluated: {ToolName} outcome={AuthorizationOutcome}",
                    toolName,
                    decision.Outcome.ToString());
                break;
            case ToolAuthorizationOutcome.Denied:
                _logger.LogWarning(
                    "Tool authorization evaluated: {ToolName} outcome={AuthorizationOutcome} reason={AuthorizationReason}",
                    toolName,
                    decision.Outcome.ToString(),
                    decision.DenyReason);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(decision), decision.Outcome, "Unknown authorization outcome.");
        }
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

        if (string.IsNullOrEmpty(context.Approval.OneTimeApprovedToolName)
            || !string.Equals(context.Approval.OneTimeApprovedToolName, toolCall.Name, StringComparison.Ordinal))
            return false;

        // Patterns bind the authored approval units. Candidate keys bind the
        // filtered verb and effective-directory set that the user approved.
        // Exact equality forces a new prompt when a formerly safe candidate
        // becomes unsafe before the retry, for example after a symlink swap.
        // An unchanged messy command has an empty key set on both attempts,
        // while a clean-to-messy transition cannot match its original keys.
        return context.Approval.OneTimeApprovedPatterns.SetEquals(
            OneTimeApprovalKeys.Create(approvalContext));
    }
}
