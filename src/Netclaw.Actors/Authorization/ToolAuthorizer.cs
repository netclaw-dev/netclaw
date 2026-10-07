// -----------------------------------------------------------------------
// <copyright file="ToolAuthorizer.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Microsoft.Extensions.AI;
using Netclaw.Actors.Authorization.Consent;
using Netclaw.Actors.Protocol;
using Netclaw.Actors.Tools;
using Netclaw.Configuration;
using Netclaw.Security;
using Netclaw.Tools;

namespace Netclaw.Actors.Authorization;

/// <summary>
/// Decides whether one tool call can run. It applies one fixed, ordered list of
/// rules and returns the first decision as an <see cref="AuthorizationDecision"/>.
/// </summary>
/// <remarks>
/// <para>
/// This class owns only the order. Each rule asks the component that owns its
/// question (admission, prohibition, filesystem authority, consent, advice) and
/// does not repeat that component's check.
/// </para>
/// <para>
/// Every tool call uses this class. It is the only gate: authorization PR 6d
/// deleted the old one. The rule order reproduces the old gate exactly,
/// including the rule that the trusted root check precedes a covering grant.
/// The corpus differential (scripts/authorization-corpus) proves that the
/// decisions match dev.
/// </para>
/// </remarks>
internal sealed class ToolAuthorizer
{
    private const string InternalPolicyFailure = "internal_policy_failure";

    /// <summary>The deny reason for a consent request in a run where nobody can answer it (D2).</summary>
    internal const string UnattendedApprovalRequired = "approval_required_unattended";

    private readonly ToolRegistry _registry;
    private readonly ToolAccessPolicy _policy;
    private readonly IToolApprovalService? _approvalService;
    private readonly ShellPolicyCoordinator _shell;

    /// <param name="registry">The tools that a call can name.</param>
    /// <param name="policy">Admission, prohibition, filesystem, and consent-request rules.</param>
    /// <param name="approvalService">
    /// The grant store. Null means that no grant store exists: every candidate
    /// stays uncovered, and the side-effect exemption does not apply. This is the
    /// state that <see cref="DispatchingToolExecutor"/> supports today.
    /// </param>
    /// <param name="shell">Shell advice selection and shell coverage.</param>
    internal ToolAuthorizer(
        ToolRegistry registry,
        ToolAccessPolicy policy,
        IToolApprovalService? approvalService,
        ShellPolicyCoordinator shell)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentNullException.ThrowIfNull(shell);
        _registry = registry;
        _policy = policy;
        _approvalService = approvalService;
        _shell = shell;
    }

    /// <summary>Decides one tool call. The call must already have its metadata arguments removed.</summary>
    internal async Task<AuthorizationDecision> AuthorizeAsync(
        FunctionCallContent call,
        ToolExecutionContext context,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(call);
        ArgumentNullException.ThrowIfNull(context);

        if (_registry.GetByName(call.Name) is not { } tool)
            return AuthorizationDecision.From(ToolAuthorizationDecision.Deny("tool_not_found"), analysis: null);

        var isShell = string.Equals(tool.Name, ShellTool.ToolName, StringComparison.Ordinal);
        var decision = isShell
            ? await AuthorizeShellAsync(new ShellCall(this, tool, call, context), ct)
            : AuthorizationDecision.From(await DecideOtherAsync(new OtherCall(this, tool, call, context), ct), analysis: null);
        decision = DenyConsentWhenUnattended(decision, context);
        return isShell ? CorrectCommandTooLongToShow(decision) : decision;
    }

    /// <summary>
    /// The tool result for a consent request that reaches a run with no approval
    /// bridge. The authorizer denies such a request first, so this is a defect.
    /// The call does not run, and the result is not retryable.
    /// </summary>
    internal static string ConsentWithoutBridgeResult(string toolName)
        => $"Tool access denied: {toolName} asked for consent in a run with no approval bridge. "
           + "The call did not run. This is a Netclaw defect: the authorizer must deny this call first.";

    // The one difference between an attended and an unattended run (decision D2).
    // Both use the same rules above. Nobody can answer a consent request in an
    // unattended run, so the request becomes a denial. A saved grant that covers
    // the call already allowed it above.
    private static AuthorizationDecision DenyConsentWhenUnattended(
        AuthorizationDecision decision,
        ToolExecutionContext context)
        => decision is AuthorizationDecision.NeedsConsent consent
           && context.Invocation.RunScope.InteractiveApproval is InteractiveApprovalCapability.Unavailable
            ? new AuthorizationDecision.Denied(
                UnattendedApprovalRequired,
                $"Tool access denied: {consent.Request.ToolName} needs approval, and nobody can answer a prompt "
                + "in an unattended run. Save an \"Always\" grant for this call in a chat with the same "
                + "audience (for example with /run-reminder), then run it again.",
                consent.Trace)
            : decision;

    // The operator must see the full command that they approve. A shell prompt
    // whose text does not fit on every channel becomes a correction: the call
    // does not run and does not prompt. This rule runs after the unattended
    // denial, so only an attended consent request reaches it. Allowed, denied,
    // and unattended calls do not change. The same long call gets the same
    // correction again, never a prompt. The trace keeps the policy result, as
    // for the unattended denial.
    private static AuthorizationDecision CorrectCommandTooLongToShow(AuthorizationDecision decision)
    {
        if (decision is not AuthorizationDecision.NeedsConsent consent)
            return decision;

        // The display text holds the full command. An exact candidate verb can
        // also be the full command text, and the header and verb list show it.
        var length = consent.Request.CandidateVerbs
            .Select(static verb => verb.Length)
            .Append(consent.Request.DisplayText.Length)
            .Max();
        return length > ApprovalOptionKeys.MaxCommandTextChars
            ? new AuthorizationDecision.CorrectionRequired(
                new ToolCorrectionCollection([new ToolCorrection.ShellCommandTooLongToShow(length)]),
                consent.Matches,
                consent.Trace)
            : decision;
    }

    private async Task<AuthorizationDecision> AuthorizeShellAsync(ShellCall call, CancellationToken ct)
    {
        try
        {
            var decision = await DecideShellAsync(call, ct);
            return AuthorizationDecision.From(decision, call.AuthorizedAnalysis(decision));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            // A rule that fails denies the call. It never allows it.
            ct.ThrowIfCancellationRequested();
            return AuthorizationDecision.From(
                call.Complete(ToolAuthorizationDecision.Deny(InternalPolicyFailure)),
                analysis: null);
        }
    }

    // ---------------------------------------------------------------------
    // The rule order for shell_execute. Each line is one rule. The first rule
    // that returns a decision wins, and "??=" skips every later rule. A later
    // rule can assume that every earlier rule returned null.
    //
    // The trusted-root rules (the directory-proof slices, the analysis, and the
    // projected candidates) precede the covering grant. An attended and an
    // unattended call use the same rules (D2). The only difference comes after
    // this method: the session denies a consent request that nobody can answer.
    // An attended consent request with a command too long to show then becomes
    // a correction (CorrectCommandTooLongToShow).
    // ---------------------------------------------------------------------
    private async Task<ToolAuthorizationDecision> DecideShellAsync(ShellCall call, CancellationToken ct)
    {
        var decision = AdmitAudience(call);
        decision ??= ShellCapability(call);
        decision ??= HardDeny(call);
        decision ??= ProtectedPath(call);
        decision ??= WorkingDirectoryParentSegment(call);
        decision ??= DirectoryProofScreen(call);
        decision ??= LiteralTwinScreen(call);
        decision ??= TrustedRoot(call);
        decision ??= ApprovalModeDenial(call);
        decision ??= NativeToolAdvice(call);
        decision ??= AutomaticApprovalMode(call);
        decision ??= CallWithoutCommandText(call);
        decision ??= MissingProjection(call);
        decision ??= ProjectedTrustedRoot(call);
        decision ??= UnresolvedInput(call);
        decision ??= await CoveringGrantAsync(call, ct);
        return decision ?? UncoveredCandidates(call, ct);
    }

    // ---------------------------------------------------------------------
    // The rule order for every other tool.
    // ---------------------------------------------------------------------
    private async Task<ToolAuthorizationDecision> DecideOtherAsync(OtherCall call, CancellationToken ct)
    {
        var decision = AdmitAudience(call);
        decision ??= BackgroundJobControl(call);
        decision ??= FilePathAccess(call);
        decision ??= ApprovalModeDenial(call);
        decision ??= AutomaticApprovalMode(call);
        decision ??= await CoveringToolGrantAsync(call, ct);
        decision ??= StoreUnavailable(call);
        decision ??= OneTimeConsent(call);
        decision ??= TemporaryDirectoryAdvice(call);
        return (decision ?? ConsentRequest(call)).WithApprovalMatches(call.Matches);
    }

    // Admission: may this audience use the tool (profile, MCP server and tool lists)?
    private ToolAuthorizationDecision? AdmitAudience(ShellCall call)
        => call.Finish(_policy.AdmitAudience(call.Tool, call.Context));

    // Admission: is shell enabled on this host, for this audience?
    private ToolAuthorizationDecision? ShellCapability(ShellCall call)
        => call.Finish(_policy.EvaluateShellCapability(call.Context.Invocation));

    // Prohibition: the hard-deny list, before any grant lookup.
    private ToolAuthorizationDecision? HardDeny(ShellCall call)
        => call.Analysis is { } analysis ? call.Finish(_policy.ScreenHardDeny(analysis)) : null;

    // Prohibition: shell text that names a protected path, whatever the consent.
    private ToolAuthorizationDecision? ProtectedPath(ShellCall call)
        => call.Analysis is { } analysis ? call.Finish(_policy.ScreenProtectedShellText(analysis)) : null;

    // Filesystem authority: a ".." in the working directory.
    private static ToolAuthorizationDecision? WorkingDirectoryParentSegment(ShellCall call)
        => call.Finish(ToolAccessPolicy.ScreenShellWorkingDirectory(call.WorkingDirectory));

    // Prohibition and filesystem authority for each slice of a cd directory proof.
    private ToolAuthorizationDecision? DirectoryProofScreen(ShellCall call)
        => call.DirectoryProof is { } proof
            ? call.Finish(_policy.ScreenDirectoryScopes(proof, call.Context))
            : null;

    // Prohibition and filesystem authority for each literal twin, as for a typed command (F1).
    private ToolAuthorizationDecision? LiteralTwinScreen(ShellCall call)
        => call.LiteralTwins is { } twins
            ? call.Finish(_policy.ScreenLiteralTwins(twins, call.Context))
            : null;

    // Filesystem authority: the working directory and every known path must be inside a trusted root.
    private ToolAuthorizationDecision? TrustedRoot(ShellCall call)
        => call.Analysis is { } analysis
            ? call.Finish(_policy.ScreenShellTrustZone(analysis, call.WorkingDirectory, call.Context))
            : null;

    // Admission: a Deny consent mode.
    private static ToolAuthorizationDecision? ApprovalModeDenial(ShellCall call)
        => call.Finish(ToolAccessPolicy.ScreenApprovalModeDenial(call.Mode));

    // Advice: a native tool replaces the shell call. Shell consent cannot authorize that replacement.
    private static ToolAuthorizationDecision? NativeToolAdvice(ShellCall call)
        => call.Corrections?.Items.Any(static correction => correction is ToolCorrection.NativeToolSuggested) == true
            ? call.Finish(ToolAuthorizationDecision.RequireAgentCorrection(call.Corrections))
            : null;

    // Admission: Auto mode allows the call, after the agent receives any directory advice.
    private static ToolAuthorizationDecision? AutomaticApprovalMode(ShellCall call)
    {
        if (call.Preflight is not ShellPolicyPreflightResult.Complete complete
            || complete.Decision.AllowReason != ToolAllowReason.PolicyAuto)
        {
            return null;
        }

        return call.Finish(call.Corrections is { } corrections
            ? ToolAuthorizationDecision.RequireAgentCorrection(corrections)
            : complete.Decision);
    }

    // Unresolved input: a call without command text gets one exact retry, or a consent request.
    private static ToolAuthorizationDecision? CallWithoutCommandText(ShellCall call)
    {
        if (call.Preflight is not ShellPolicyPreflightResult.Complete complete)
            return null;

        var decision = complete.Decision;
        if (decision.NeedsApproval
            && decision.ApprovalContext is { } approvalContext
            && OneTimeApprovalKeys.Matches(call.Context.Approval.OneTimeConsent, call.Call.Name, approvalContext))
        {
            decision = ToolAuthorizationDecision.Allow(ToolAllowReason.OneTimeApproval);
        }

        return call.Finish(decision);
    }

    // Consent: the candidates must project to one policy view.
    private static ToolAuthorizationDecision? MissingProjection(ShellCall call)
        => call.Projection is null
            ? call.Finish(ToolAuthorizationDecision.Deny(InternalPolicyFailure))
            : null;

    // Filesystem authority: every candidate path again, including the intent view of a causal list.
    private ToolAuthorizationDecision? ProjectedTrustedRoot(ShellCall call)
        => call.Finish(_policy.EnforceProjectedShellFileProtection(
            call.Evaluation.CandidateStates
                .Select(static state => (state.PathFacts, state.Candidate.SourceOccurrence))
                .ToArray(),
            call.CandidateAnalyses,
            call.Context.Invocation));

    // Unresolved input: syntax without reusable candidates gets one exact retry, advice, or a Once-only prompt.
    private static ToolAuthorizationDecision? UnresolvedInput(ShellCall call)
        => ShellPolicyCoordinator.RequiresExactApproval(call.Evaluation.Projection)
            ? ShellPolicyCoordinator.CompleteOneTimeOrPrompt(call.Evaluation, call.Call.Name, call.Corrections)
            : null;

    // Consent: a stored grant, the side-effect exemption, or the reviewed-safe policy covers every candidate.
    private async Task<ToolAuthorizationDecision?> CoveringGrantAsync(ShellCall call, CancellationToken ct)
    {
        await CoverOnceAsync(call, ct);
        return call.Evaluation.AllCovered
            ? ShellPolicyCoordinator.CompleteCovered(call.Evaluation, ct)
            : null;
    }

    // One batched stored-grant lookup for each call.
    private async Task CoverOnceAsync(ShellCall call, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (call.Covered)
            return;

        await _shell.CoverAsync(call.Tool, call.Context, call.Evaluation, ct);
        call.Covered = true;
    }

    // Consent: a one-time answer, a store failure, advice, or a prompt for the uncovered candidates.
    private static ToolAuthorizationDecision UncoveredCandidates(ShellCall call, CancellationToken ct)
        => ShellPolicyCoordinator.CompleteUncovered(call.Evaluation, call.Context, call.Call.Name, call.Corrections, ct);

    // Admission: may this audience use the tool (profile, MCP server and tool lists)?
    private ToolAuthorizationDecision? AdmitAudience(OtherCall call)
        => _policy.AdmitAudience(call.Tool, call.Context);

    // Admission: background job control needs shell capability, and the first shell consent covers it.
    private ToolAuthorizationDecision? BackgroundJobControl(OtherCall call)
        => call.Tool is not McpToolAdapter
           && string.Equals(call.Tool.Name, Jobs.CheckBackgroundJobTool.ToolName, StringComparison.Ordinal)
            ? _policy.AuthorizeBackgroundJobControl(call.Context)
            : null;

    // Filesystem authority and protection for a file tool path, including control-plane writes.
    private ToolAuthorizationDecision? FilePathAccess(OtherCall call)
        => call.Tool is McpToolAdapter
            ? null
            : _policy.PreflightStructuredPathAccess(call.Tool, call.Context.Invocation, call.Call.Arguments);

    // Admission: a Deny consent mode.
    private static ToolAuthorizationDecision? ApprovalModeDenial(OtherCall call)
        => ToolAccessPolicy.ScreenApprovalModeDenial(call.Mode);

    // Admission: Auto mode allows the call.
    private static ToolAuthorizationDecision? AutomaticApprovalMode(OtherCall call)
        => call.Mode == ToolApprovalMode.Auto
            ? ToolAuthorizationDecision.Allow(ToolAllowReason.PolicyAuto)
            : null;

    // Consent: one stored-grant lookup covers every candidate.
    private async Task<ToolAuthorizationDecision?> CoveringToolGrantAsync(OtherCall call, CancellationToken ct)
    {
        var check = _approvalService is null
            ? StoredGrantCheck.NotRun
            : await StoredGrantCheck.RunAsync(
                _approvalService,
                call.ToolName,
                call.Request,
                call.Context,
                ct);
        call.RecordGrantCheck(check);
        return check.AllCovered
            ? ToolAuthorizationDecision.Allow(ToolAllowReason.StoredApproval)
            : null;
    }

    // Consent: without the persistent store, only a one-time answer can cover a miss.
    private static ToolAuthorizationDecision? StoreUnavailable(OtherCall call)
    {
        if (!call.GrantCheck.StoreUnavailableForMiss)
            return null;

        return call.HasOneTimeConsent
            ? ToolAuthorizationDecision.Allow(ToolAllowReason.OneTimeApproval)
            : ToolAuthorizationDecision.Deny("approval_store_unavailable");
    }

    // Consent: the operator's "Once" answer for this exact request.
    private static ToolAuthorizationDecision? OneTimeConsent(OtherCall call)
        => call.HasOneTimeConsent
            ? ToolAuthorizationDecision.Allow(ToolAllowReason.OneTimeApproval)
            : null;

    // Advice: write the file in the managed temporary directory instead.
    private static ToolAuthorizationDecision? TemporaryDirectoryAdvice(OtherCall call)
        => call.ConsentDecision.AgentCorrection is ToolCorrection.ManagedTemporaryDirectorySuggested temporary
            ? ToolAuthorizationDecision.RequireAgentCorrection(temporary, call.Matches)
            : null;

    // Consent: ask the operator.
    private static ToolAuthorizationDecision ConsentRequest(OtherCall call)
        => call.ConsentDecision;

    /// <summary>
    /// The call-local facts of one shell call. Each fact comes from its owning
    /// component on first use, so a fact never runs before the rule that needs it.
    /// </summary>
    private sealed class ShellCall(
        ToolAuthorizer authorizer,
        INetclawTool tool,
        FunctionCallContent call,
        ToolExecutionContext context)
    {
        private readonly ShellPolicyDecisionTraceBuilder _trace = new();
        private readonly ToolName _toolName = new(tool.Name);
        private readonly Lazy<string?> _workingDirectory = new(() =>
            ToolAccessPolicy.ResolveShellWorkingDirectory(context, call.Arguments));
        private Lazy<ShellCommandAnalysis?>? _analysis;
        private Lazy<ShellApprovalAnalysis?>? _parsedApproval;
        private Lazy<BashDirectoryScopeProjection?>? _directoryProof;
        private Lazy<BashLiteralTwinSlices?>? _literalTwins;
        private Lazy<ToolApprovalMode>? _mode;
        private Lazy<ShellPolicyPreflightResult>? _preflight;
        private Lazy<ToolCorrectionCollection?>? _corrections;
        private Lazy<ShellPolicyProjection?>? _projection;
        private Lazy<ShellPolicyEvaluation>? _evaluation;

        internal INetclawTool Tool => tool;

        internal FunctionCallContent Call => call;

        internal ToolExecutionContext Context => context;

        internal string? WorkingDirectory => _workingDirectory.Value;

        /// <summary>The parsed command, or null when the call has no command text.</summary>
        /// <remarks>
        /// The parser uses the variables that the launcher sets for this session's
        /// temporary location (ShellProcessLaunch parses with the same location).
        /// </remarks>
        internal ShellCommandAnalysis? Analysis => (_analysis ??= new(() =>
            ToolAccessPolicy.ExtractShellCommand(call.Arguments) is { } command
                ? authorizer._policy.ShellCommandPolicy.Analyze(
                    command,
                    WorkingDirectory,
                    context.SessionStorage?.ManagedTemporary)
                : null)).Value;

        /// <summary>The directory proof of an unresolved Bash compound, or null when none applies.</summary>
        internal BashDirectoryScopeProjection? DirectoryProof => (_directoryProof ??= new(() =>
            Analysis is { } analysis
            && ParsedApproval is { } approval
            && authorizer._policy.TryProveDirectoryScopes(analysis, approval, out var proof)
                ? proof
                : null)).Value;

        /// <summary>
        /// The literal twins of the Bash commands, or null when no command has
        /// twins. A directory proof already gives each command its exact
        /// directory, so a call with one gets no twins.
        /// </summary>
        internal BashLiteralTwinSlices? LiteralTwins => (_literalTwins ??= new(() =>
            DirectoryProof is null
            && Analysis is { } analysis
            && authorizer._policy.TryProjectLiteralTwins(analysis, out var twins)
                ? twins
                : null)).Value;

        /// <summary>
        /// The consent candidates: from the directory proof when one applies,
        /// else one candidate set for each command, with the candidates of the
        /// literal twins in place of their source command.
        /// </summary>
        internal ShellApprovalAnalysis? Approval => DirectoryProof is { } proof
            ? ToolAccessPolicy.WithDirectoryScopes(ParsedApproval!, proof)
            : ParsedApproval is { } parsed
                ? WithLiteralTwins(ToolAccessPolicy.WithCommandCandidates(parsed))
                : null;

        /// <summary>The analyses that own the candidate occurrences: the call and each literal twin.</summary>
        internal IReadOnlyList<ShellCommandAnalysis> CandidateAnalyses =>
        [
            .. Analysis is { } analysis ? [analysis] : Array.Empty<ShellCommandAnalysis>(),
            .. LiteralTwins?.Slices.Select(static slice => slice.Analysis) ?? []
        ];

        private ShellApprovalAnalysis WithLiteralTwins(ShellApprovalAnalysis approval)
            => LiteralTwins is { } twins ? ToolAccessPolicy.WithLiteralTwins(approval, twins) : approval;

        internal ToolApprovalMode Mode => (_mode ??= new(() =>
            authorizer._policy.GetShellApprovalMode(_toolName, context, call.Arguments, Analysis))).Value;

        /// <summary>True after the one stored-grant lookup of this call.</summary>
        internal bool Covered { get; set; }

        /// <summary>
        /// The result after every screen passed: an automatic allow, a consent
        /// request without analysis, or the facts that coverage selection reads.
        /// </summary>
        internal ShellPolicyPreflightResult Preflight => (_preflight ??= new(() =>
            ToolAccessPolicy.CompleteShellPreflight(
                authorizer._policy.AuthorizeShellApproval(
                    _toolName,
                    context,
                    call.Arguments,
                    Mode,
                    Approval,
                    WorkingDirectory),
                Analysis,
                DirectoryProof))).Value;

        internal ToolCorrectionCollection? Corrections => (_corrections ??= new(() =>
            PreflightAnalysis is { } analysis
                ? authorizer._shell.CollectApplicableCorrections(analysis, call, context, Preflight)
                : null)).Value;

        internal ShellPolicyProjection? Projection => (_projection ??= new(() =>
            Preflight is ShellPolicyPreflightResult.Continue continuation
            && ShellPolicyProjection.TryCreate(
                continuation.Analysis.Environment,
                continuation.ApprovalContext,
                continuation.DirectoryScopes,
                context,
                out var projection)
                ? projection
                : null)).Value;

        internal ShellPolicyEvaluation Evaluation => (_evaluation ??= new(() =>
            new ShellPolicyEvaluation(
                Projection ?? throw new InvalidOperationException("The shell call has no policy projection."),
                _trace))).Value;

        private ShellApprovalAnalysis? ParsedApproval => (_parsedApproval ??= new(() =>
            Analysis is { } analysis
                ? authorizer._policy.AnalyzeShellApproval(_toolName, call.Arguments, WorkingDirectory, analysis)
                : null)).Value;

        // The analysis that the preflight carries forward, as the coordinator reads it.
        private ShellCommandAnalysis? PreflightAnalysis => Preflight switch
        {
            ShellPolicyPreflightResult.Complete { AuthorizedAnalysis: { } authorized } => authorized,
            ShellPolicyPreflightResult.Continue continuation => continuation.Analysis,
            _ => null,
        };

        /// <summary>Attaches the decision trace to a rule's decision. Returns null for null.</summary>
        internal ToolAuthorizationDecision? Finish(ToolAuthorizationDecision? decision)
            => decision is null ? null : Complete(decision);

        /// <summary>Attaches the decision trace to a final decision.</summary>
        internal ToolAuthorizationDecision Complete(ToolAuthorizationDecision decision)
            => ShellPolicyCoordinator.Complete(decision, [], _trace);

        /// <summary>The analysis that the process may execute, when the decision allows the call.</summary>
        internal ShellCommandAnalysis? AuthorizedAnalysis(ToolAuthorizationDecision decision)
            => decision.Outcome == ToolAuthorizationOutcome.Allowed ? PreflightAnalysis : null;
    }

    /// <summary>The call-local facts of one call that is not a shell call.</summary>
    private sealed class OtherCall(
        ToolAuthorizer authorizer,
        INetclawTool tool,
        FunctionCallContent call,
        ToolExecutionContext context)
    {
        private readonly Lazy<IDictionary<string, object?>?> _approvalArguments = new(() =>
            ToolAccessPolicy.GetApprovalArguments(tool, call.Arguments));
        private Lazy<IToolApprovalMatcher>? _matcher;
        private Lazy<ToolApprovalMode>? _mode;
        private Lazy<ToolAuthorizationDecision>? _consentDecision;
        private StoredGrantCheck? _grantCheck;

        internal INetclawTool Tool => tool;

        internal ToolName ToolName { get; } = new(tool.Name);

        internal FunctionCallContent Call => call;

        internal ToolExecutionContext Context => context;

        internal ToolApprovalMode Mode => (_mode ??= new(() =>
            authorizer._policy.GetApprovalMode(ToolName, context, _approvalArguments.Value, Matcher))).Value;

        /// <summary>The consent request decision, with any temporary-directory advice.</summary>
        internal ToolAuthorizationDecision ConsentDecision => (_consentDecision ??= new(() =>
            authorizer._policy.BuildNonShellConsentRequest(
                ToolName,
                context,
                _approvalArguments.Value,
                Matcher))).Value;

        internal ToolApprovalContext Request => ConsentDecision.ApprovalContext
            ?? throw new InvalidOperationException("Approval decision missing approval context.");

        internal bool HasOneTimeConsent
            => OneTimeApprovalKeys.Matches(context.Approval.OneTimeConsent, call.Name, Request);

        /// <summary>The grant lookup. A rule that reads it before the lookup rule fails loudly.</summary>
        internal StoredGrantCheck GrantCheck
            => _grantCheck ?? throw new InvalidOperationException("The stored-grant lookup has not run.");

        /// <summary>The grants that matched, or none before the lookup and in Auto mode.</summary>
        internal IReadOnlyList<ToolApprovalMatch> Matches => _grantCheck?.Matches ?? [];

        private IToolApprovalMatcher Matcher => (_matcher ??= new(() =>
            authorizer._policy.SelectApprovalMatcher(tool))).Value;

        internal void RecordGrantCheck(StoredGrantCheck check)
        {
            if (_grantCheck is not null)
                throw new InvalidOperationException("The stored-grant lookup ran twice.");

            _grantCheck = check;
        }
    }
}
