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
using Netclaw.Actors.Authorization;
using Netclaw.Actors.Authorization.Consent;
using Netclaw.Actors.Sessions.Pipelines;
using Netclaw.Configuration;
using Netclaw.Security;
using Netclaw.Tools;

namespace Netclaw.Actors.Tools;

/// <summary>
/// Routes <see cref="FunctionCallContent"/> to the correct tool by name via the <see cref="ToolRegistry"/>.
/// Logs every tool execution with name, duration, and result preview.
/// </summary>
public sealed class DispatchingToolExecutor : IToolExecutor, IApprovalShellProvider
{
    private readonly ToolRegistry _registry;
    private readonly ToolAccessPolicy _policy;
    private readonly ILogger _logger;

    /// <summary>The linear authorizer. Every tool call uses it.</summary>
    internal ToolAuthorizer Authorizer { get; }

    public DispatchingToolExecutor(ToolRegistry registry, ToolAccessPolicy policy,
        IToolApprovalService? approvalService = null, ILogger<DispatchingToolExecutor>? logger = null)
        : this(registry, policy, approvalService, logger is null ? NullLogger.Instance : logger)
    {
    }

    private DispatchingToolExecutor(
        ToolRegistry registry,
        ToolAccessPolicy policy,
        IToolApprovalService? approvalService,
        ILogger logger)
    {
        _registry = registry;
        _policy = policy;
        Authorizer = new ToolAuthorizer(
            registry,
            policy,
            approvalService,
            new ShellPolicyCoordinator(registry, policy, approvalService));
        _logger = logger;
    }

    internal static DispatchingToolExecutor CreateWithLogger(
        ToolRegistry registry,
        ToolAccessPolicy policy,
        IToolApprovalService? approvalService,
        ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(logger);
        return new DispatchingToolExecutor(
            registry,
            policy,
            approvalService,
            logger);
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

        if (ToolCallMetaExtractor.ValidateRequiredRationale(toolCall.Arguments, resolveMeta) is { } rationaleError)
            return new ToolArgumentRejection(rationaleError, "invalid_rationale");

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
            _logger.LogWarning(
                "Unknown tool requested: {ToolName} authorizationAttemptId={AuthorizationAttemptId} " +
                "sessionId={SessionId} callId={CallId}",
                toolCall.Name,
                context.Approval.AuthorizationAttemptId.Value,
                context.SessionId,
                toolCall.CallId);
            context.Outputs.TryComplete(new ToolInvocationReceipt.OtherOutcome(ToolInvocationOutcomeCategory.NotFound));
            return $"Unknown tool: {toolCall.Name}";
        }

        // Interpret the original call before authorization. This keeps required
        // metadata available for validation and removes it before tool dispatch.
        var interpretation = InterpretToolCall(toolCall);
        if (interpretation.Rejection is { } rejection)
        {
            _logger.LogWarning(
                "Rejected tool call ({Reason}): {ToolName} — {Error} " +
                "authorizationAttemptId={AuthorizationAttemptId} sessionId={SessionId} callId={CallId}",
                rejection.DenyReason,
                toolCall.Name,
                rejection.Message,
                context.Approval.AuthorizationAttemptId.Value,
                context.SessionId,
                toolCall.CallId);
            context.Outputs.TryComplete(new ToolInvocationReceipt.OtherOutcome(ToolInvocationOutcomeCategory.InvalidInput));
            return rejection.Message;
        }

        toolCall = interpretation.Cleaned;

        var sw = Stopwatch.StartNew();
        try
        {
            var (tool, allowed) = await GetAuthorizedToolAsync(toolCall, context, ct);
            var result = (tool, allowed.Analysis) switch
            {
                (ShellTool shellTool, { } analysis) =>
                    await shellTool.ExecuteAuthorizedAsync(
                        toolCall.Arguments,
                        context.Invocation,
                        CreateShellLaunch(shellTool, toolCall.CallId, context, analysis),
                        ct),
                (ShellTool shellTool, null) => shellTool.ValidateUnanalyzedArguments(toolCall.Arguments),
                (_, null) => await tool.ExecuteAsync(toolCall.Arguments, context.Invocation, ct),
                _ => throw new InvalidOperationException("Only a shell call can carry an analysis.")
            };

            context.Outputs.TryComplete(new ToolInvocationReceipt.Succeeded([], null));

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
                "Tool executed: {ToolName} ({Duration}ms, {ResultLength} chars) " +
                "authorizationAttemptId={AuthorizationAttemptId} sessionId={SessionId} callId={CallId}",
                toolCall.Name,
                sw.ElapsedMilliseconds,
                result.Length,
                context.Approval.AuthorizationAttemptId.Value,
                context.SessionId,
                toolCall.CallId);

            return result;
        }
        catch (Exception ex)
        {
            CompleteExceptionOutcome(context, ex, ct);
            sw.Stop();
            _logger.LogError(ex,
                "Tool execution failed: {ToolName} ({Duration}ms) " +
                "authorizationAttemptId={AuthorizationAttemptId} sessionId={SessionId} callId={CallId}",
                toolCall.Name,
                sw.ElapsedMilliseconds,
                context.Approval.AuthorizationAttemptId.Value,
                context.SessionId,
                toolCall.CallId);
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
            _logger.LogWarning(
                "Unknown tool requested: {ToolName} authorizationAttemptId={AuthorizationAttemptId} " +
                "sessionId={SessionId} callId={CallId}",
                toolCall.Name,
                context.Approval.AuthorizationAttemptId.Value,
                context.SessionId,
                toolCall.CallId);
            context.Outputs.TryComplete(new ToolInvocationReceipt.OtherOutcome(ToolInvocationOutcomeCategory.NotFound));
            yield return new ToolCompletedUpdate($"Unknown tool: {toolCall.Name}");
            yield break;
        }

        // Use the same atomic validation and extraction as the non-streaming path.
        var interpretation = InterpretToolCall(toolCall);
        if (interpretation.Rejection is { } rejection)
        {
            _logger.LogWarning(
                "Rejected tool call ({Reason}): {ToolName} — {Error} " +
                "authorizationAttemptId={AuthorizationAttemptId} sessionId={SessionId} callId={CallId}",
                rejection.DenyReason,
                toolCall.Name,
                rejection.Message,
                context.Approval.AuthorizationAttemptId.Value,
                context.SessionId,
                toolCall.CallId);
            context.Outputs.TryComplete(new ToolInvocationReceipt.OtherOutcome(ToolInvocationOutcomeCategory.InvalidInput));
            yield return new ToolCompletedUpdate(rejection.Message);
            yield break;
        }

        toolCall = interpretation.Cleaned;

        (INetclawTool Tool, AuthorizationDecision.Allowed Allowed) authorized;
        try
        {
            authorized = await GetAuthorizedToolAsync(toolCall, context, ct);
        }
        catch (Exception ex)
        {
            CompleteExceptionOutcome(context, ex, ct);
            throw;
        }

        var tool = authorized.Tool;
        var updates = (tool, authorized.Allowed.Analysis) switch
        {
            (ShellTool shellTool, { } analysis) =>
                shellTool.ExecuteAuthorizedStreamAsync(
                    toolCall.Arguments,
                    context.Invocation,
                    CreateShellLaunch(shellTool, toolCall.CallId, context, analysis),
                    ct),
            (ShellTool shellTool, null) =>
                SingleCompletion(shellTool.ValidateUnanalyzedArguments(toolCall.Arguments)),
            (_, null) => tool.ExecuteStreamAsync(toolCall.Arguments, context.Invocation, ct),
            _ => throw new InvalidOperationException("Only a shell call can carry an analysis.")
        };
        var sw = Stopwatch.StartNew();
        await foreach (var update in updates)
        {
            switch (update)
            {
                case ToolCompletedUpdate completed:
                    sw.Stop();
                    context.Outputs.TryComplete(new ToolInvocationReceipt.Succeeded([], null));
                    var redacted = SecretOutputRedactor.Redact(completed.Result);
                    var modelResult = tool.SuppressOutputRedaction ? completed.Result : redacted;
                    modelResult = await ToolOutputSpill.BoundAndSpillAsync(
                        modelResult, redacted, toolCall.CallId, ResolveInlineBudget(tool, context), context.Invocation, ct);
                    _logger.LogInformation(
                        "Tool executed: {ToolName} ({Duration}ms, {ResultLength} chars) " +
                        "authorizationAttemptId={AuthorizationAttemptId} sessionId={SessionId} callId={CallId}",
                        toolCall.Name,
                        sw.ElapsedMilliseconds,
                        modelResult.Length,
                        context.Approval.AuthorizationAttemptId.Value,
                        context.SessionId,
                        toolCall.CallId);
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

    private static async IAsyncEnumerable<ToolCallUpdate> SingleCompletion(string result)
    {
        await Task.CompletedTask;
        yield return new ToolCompletedUpdate(result);
    }

    private static void CompleteExceptionOutcome(
        ToolExecutionContext context,
        Exception exception,
        CancellationToken callerToken)
    {
        if (exception is OperationCanceledException && callerToken.IsCancellationRequested)
            return;

        if (exception is ToolApprovalRequiredException or ToolCorrectionRequiredException)
            return;

        var category = exception switch
        {
            ToolAccessDeniedException => ToolInvocationOutcomeCategory.AccessDenied,
            UnauthorizedAccessException => ToolInvocationOutcomeCategory.AccessDenied,
            FileNotFoundException or DirectoryNotFoundException => ToolInvocationOutcomeCategory.NotFound,
            IOException or TimeoutException => ToolInvocationOutcomeCategory.TransientFailure,
            _ => ToolInvocationOutcomeCategory.TransientFailure
        };
        context.Outputs.TryComplete(new ToolInvocationReceipt.OtherOutcome(category));
    }

    /// <summary>
    /// Asks the linear authorizer for one call and logs the decision. Every gate
    /// run comes here: the first attempt, the retry after consent, the
    /// background launch, and the shell re-check at process start.
    /// </summary>
    internal async Task<AuthorizationDecision> EvaluateAuthorizationAsync(
        FunctionCallContent toolCall,
        ToolExecutionContext context,
        CancellationToken ct)
    {
        var decision = await Authorizer.AuthorizeAsync(toolCall, context, ct);
        LogAuthorizationDecision(toolCall, context, decision);
        return decision;
    }

    public async Task<ShellProcessLaunch> PrepareShellLaunchAsync(
        FunctionCallContent toolCall,
        ToolExecutionContext context,
        CancellationToken ct)
    {
        if (context.RunScope.Session is not ToolSessionScope.Bound || context.Boundary is null)
            throw new InvalidOperationException("A background launch requires a bound session and a trust boundary.");

        var (tool, allowed) = await GetAuthorizedToolAsync(toolCall, context, ct);
        if (tool is not ShellTool shellTool || allowed.Analysis is not { } analysis)
            throw new InvalidOperationException("Background execution requires an authorized shell tool.");

        return CreateShellLaunch(shellTool, toolCall.CallId, context, analysis);
    }

    private ShellProcessLaunch CreateShellLaunch(
        ShellTool tool,
        string callId,
        ToolExecutionContext context,
        ShellCommandAnalysis analysis)
    {
        if (!ReferenceEquals(tool.ShellEnvironment, _policy.ShellEnvironment))
            throw new InvalidOperationException("Shell execution and authorization must use the same environment.");

        var workingDirectory = analysis.WorkingDirectory
            ?? throw new InvalidOperationException("Authorized shell execution requires a working directory.");
        var launchContext = new ToolExecutionContext(context.RunScope, context.ExecutionTimeout);
        launchContext.Approval.RestoreAuthorizationAttemptId(context.Approval.AuthorizationAttemptId);
        if (context.Approval.OneTimeConsent is { } oneTimeConsent)
            launchContext.Approval.SeedOneTimeConsent(oneTimeConsent);
        if (context.Approval.ManagedTemporaryRetry is { } retry)
            launchContext.Approval.MarkManagedTemporaryRetry(retry);

        // Use the authorized source, not the caller's mutable argument dictionary.
        var exactCall = new FunctionCallContent(callId, ShellTool.ToolName, new Dictionary<string, object?>
        {
            ["Command"] = analysis.Source,
            ["WorkingDirectory"] = workingDirectory
        });
        return tool.CreateLaunch(
            analysis.Source,
            workingDirectory,
            launchContext.Invocation,
            async cancellationToken =>
            {
                await GetAuthorizedToolAsync(exactCall, launchContext, cancellationToken);
            });
    }

    ApprovalShell IApprovalShellProvider.Shell => _policy.Shell;

    // The execution boundary: an executor signals consent, advice, and denial
    // to the pipeline and the sub-agent loop with these exceptions.
    private async Task<(INetclawTool Tool, AuthorizationDecision.Allowed Allowed)> GetAuthorizedToolAsync(
        FunctionCallContent toolCall,
        ToolExecutionContext context,
        CancellationToken ct)
        => await EvaluateAuthorizationAsync(toolCall, context, ct) switch
        {
            AuthorizationDecision.Allowed allowed => (
                _registry.GetByName(toolCall.Name)
                ?? throw new InvalidOperationException("Allowed decision is missing its registered tool."),
                allowed),
            AuthorizationDecision.NeedsConsent consent => throw new ToolApprovalRequiredException(consent.Request),
            AuthorizationDecision.CorrectionRequired correction =>
                throw new ToolCorrectionRequiredException(correction.Corrections),
            AuthorizationDecision.Denied denied => throw new ToolAccessDeniedException(denied.Reason, denied.Message),
            var unknown => throw new ArgumentOutOfRangeException(nameof(toolCall), unknown, "Unknown authorization decision.")
        };

    private void LogAuthorizationDecision(
        FunctionCallContent toolCall,
        ToolExecutionContext context,
        AuthorizationDecision decision)
    {
        // The outcome names stay those of ToolAuthorizationOutcome, so log
        // queries keep working.
        switch (decision)
        {
            case AuthorizationDecision.Allowed allowed:
                _logger.LogDebug(
                    "Tool authorization evaluated: {ToolName} outcome={AuthorizationOutcome} " +
                    "reason={AuthorizationReason} explanation={AuthorizationExplanation} " +
                    "authorizationAttemptId={AuthorizationAttemptId} sessionId={SessionId} callId={CallId}",
                    toolCall.Name,
                    nameof(ToolAuthorizationOutcome.Allowed),
                    allowed.Reason.ToString(),
                    allowed.Reason.GetDescription(),
                    context.Approval.AuthorizationAttemptId.Value,
                    context.SessionId,
                    toolCall.CallId);
                break;
            case AuthorizationDecision.NeedsConsent or AuthorizationDecision.CorrectionRequired:
                _logger.LogInformation(
                    "Tool authorization evaluated: {ToolName} outcome={AuthorizationOutcome} " +
                    "authorizationAttemptId={AuthorizationAttemptId} sessionId={SessionId} callId={CallId}",
                    toolCall.Name,
                    decision is AuthorizationDecision.NeedsConsent
                        ? nameof(ToolAuthorizationOutcome.RequiresApproval)
                        : nameof(ToolAuthorizationOutcome.RequiresAgentCorrection),
                    context.Approval.AuthorizationAttemptId.Value,
                    context.SessionId,
                    toolCall.CallId);
                break;
            case AuthorizationDecision.Denied denied:
                _logger.LogWarning(
                    "Tool authorization evaluated: {ToolName} outcome={AuthorizationOutcome} reason={AuthorizationReason} " +
                    "authorizationAttemptId={AuthorizationAttemptId} sessionId={SessionId} callId={CallId}",
                    toolCall.Name,
                    nameof(ToolAuthorizationOutcome.Denied),
                    denied.Reason,
                    context.Approval.AuthorizationAttemptId.Value,
                    context.SessionId,
                    toolCall.CallId);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(decision), decision, "Unknown authorization decision.");
        }

        LogShellPolicyTrace(toolCall, context, decision.Trace);
    }

    internal void LogShellPolicyTrace(
        FunctionCallContent toolCall,
        ToolExecutionContext context,
        ShellPolicyDecisionTrace trace)
    {
        foreach (var row in trace.Rows)
        {
            _logger.LogInformation(
                "Shell policy trace: stage={PolicyStage} outcome={PolicyOutcome} reason={PolicyReason} " +
                "candidate_id={CandidateId} executable={ExecutableBasename} " +
                "coverage={CoverageKind} scope_relation={ScopeRelation} grant_timestamp={GrantTimestamp} " +
                "authorizationAttemptId={AuthorizationAttemptId} sessionId={SessionId} callId={CallId}",
                row.Stage.ToString(),
                row.Outcome.ToString(),
                row.Reason.ToString(),
                row.CandidateId?.Value,
                row.ExecutableBasename,
                row.Coverage.ToString(),
                row.ScopeRelation.ToString(),
                row.GrantTimestamp,
                context.Approval.AuthorizationAttemptId.Value,
                context.SessionId,
                toolCall.CallId);
        }
    }
}
