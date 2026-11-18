// -----------------------------------------------------------------------
// <copyright file="AuthorizationDecision.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Actors.Tools;
using Netclaw.Security;

namespace Netclaw.Actors.Authorization;

/// <summary>
/// The outcome of one authorization attempt, as a closed set of cases. A caller
/// matches on the case. No outcome travels as an exception.
/// </summary>
/// <remarks>
/// Every case carries its evidence: the grants that matched and, for a shell
/// call, the per-candidate coverage trace. The payload types are the existing
/// consent and advice types, so a case adds no second copy of a fact.
/// </remarks>
internal closed record AuthorizationDecision
{
    private AuthorizationDecision(
        IReadOnlyList<ToolApprovalMatch> matches,
        ShellPolicyDecisionTrace trace)
    {
        ArgumentNullException.ThrowIfNull(matches);
        ArgumentNullException.ThrowIfNull(trace);
        Matches = matches;
        Trace = trace;
    }

    /// <summary>The session and persistent grants that matched this attempt.</summary>
    internal IReadOnlyList<ToolApprovalMatch> Matches { get; }

    /// <summary>The shell decision trace, with the coverage of each candidate. It is empty for other tools.</summary>
    internal ShellPolicyDecisionTrace Trace { get; }

    /// <summary>The call can run.</summary>
    /// <param name="Reason">The rule that allowed the call.</param>
    /// <param name="Analysis">The exact shell analysis that the process may execute, or null for other tools and for a shell call without command text.</param>
    internal sealed record Allowed(
        ToolAllowReason Reason,
        ShellCommandAnalysis? Analysis,
        IReadOnlyList<ToolApprovalMatch> Matches,
        ShellPolicyDecisionTrace Trace) : AuthorizationDecision(Matches, Trace);

    /// <summary>The call waits for an operator answer.</summary>
    /// <param name="Request">The consent request. Its options are the answers that the prompt offers.</param>
    internal sealed record NeedsConsent(
        ToolApprovalContext Request,
        IReadOnlyList<ToolApprovalMatch> Matches,
        ShellPolicyDecisionTrace Trace) : AuthorizationDecision(Matches, Trace);

    /// <summary>The model must author a different call. Advice grants no authority.</summary>
    internal sealed record CorrectionRequired(
        ToolCorrectionCollection Corrections,
        IReadOnlyList<ToolApprovalMatch> Matches,
        ShellPolicyDecisionTrace Trace) : AuthorizationDecision(Matches, Trace);

    /// <summary>The call does not run, and no operator answer can change that.</summary>
    /// <param name="Reason">The stable deny reason code, for example <c>hard_deny_self_destructive</c>.</param>
    /// <param name="Message">The agent-facing denial text, when the rule supplies one.</param>
    internal sealed record Denied(
        string Reason,
        string? Message,
        ShellPolicyDecisionTrace Trace) : AuthorizationDecision([], Trace);

    internal static Allowed Allow(ToolAllowReason reason)
        => new(reason, null, [], ShellPolicyDecisionTrace.Empty);

    internal static Allowed Allow(ToolAllowReason reason, IReadOnlyList<ToolApprovalMatch> matches)
        => new(reason, null, [.. matches], ShellPolicyDecisionTrace.Empty);

    internal static Denied Deny(string reason, string? message = null)
        => new(reason, message, ShellPolicyDecisionTrace.Empty);

    internal static NeedsConsent RequiresApproval(ToolApprovalContext request)
        => new(request, [], ShellPolicyDecisionTrace.Empty);

    internal static NeedsConsent RequiresApproval(ToolApprovalContext request, IReadOnlyList<ToolApprovalMatch> matches)
        => new(request, [.. matches], ShellPolicyDecisionTrace.Empty);

    internal static CorrectionRequired RequireAgentCorrection(ToolCorrection correction, IReadOnlyList<ToolApprovalMatch> matches)
        => RequireAgentCorrection(new ToolCorrectionCollection([correction]), matches);

    internal static CorrectionRequired RequireAgentCorrection(ToolCorrectionCollection corrections)
        => new(corrections, [], ShellPolicyDecisionTrace.Empty);

    internal static CorrectionRequired RequireAgentCorrection(ToolCorrectionCollection corrections, IReadOnlyList<ToolApprovalMatch> matches)
        => new(corrections, [.. matches], ShellPolicyDecisionTrace.Empty);

    internal AuthorizationDecision WithApprovalMatches(IReadOnlyList<ToolApprovalMatch> matches)
        => this switch
        {
            Allowed allowed => new Allowed(allowed.Reason, allowed.Analysis, [.. matches], allowed.Trace),
            NeedsConsent consent => new NeedsConsent(consent.Request, [.. matches], consent.Trace),
            CorrectionRequired correction => correction,
            Denied denied => denied
        };

    internal AuthorizationDecision WithShellPolicyTrace(ShellPolicyDecisionTrace trace)
        => this switch
        {
            Allowed allowed => new Allowed(allowed.Reason, allowed.Analysis, allowed.Matches, trace),
            NeedsConsent consent => new NeedsConsent(consent.Request, consent.Matches, trace),
            CorrectionRequired correction => new CorrectionRequired(correction.Corrections, correction.Matches, trace),
            Denied denied => new Denied(denied.Reason, denied.Message, trace)
        };
}
