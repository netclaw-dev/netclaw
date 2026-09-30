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
internal abstract record AuthorizationDecision
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

    /// <summary>
    /// Converts the value that the rule components return today into a case.
    /// </summary>
    /// <param name="decision">The completed decision of the rule that decided.</param>
    /// <param name="analysis">The authorized shell analysis for an allowed shell call, otherwise null.</param>
    /// <exception cref="InvalidOperationException">
    /// The decision lacks the data that its outcome requires, or a consent request carries advice.
    /// </exception>
    internal static AuthorizationDecision From(
        ToolAuthorizationDecision decision,
        ShellCommandAnalysis? analysis)
    {
        ArgumentNullException.ThrowIfNull(decision);
        if (analysis is not null && decision.Outcome != ToolAuthorizationOutcome.Allowed)
            throw new InvalidOperationException("Only an allowed decision can carry a shell analysis.");

        return decision.Outcome switch
        {
            ToolAuthorizationOutcome.Allowed => new Allowed(
                decision.AllowReason
                ?? throw new InvalidOperationException("Allowed decision missing an allow reason."),
                analysis,
                decision.ApprovalMatches,
                decision.ShellPolicyTrace),
            ToolAuthorizationOutcome.RequiresApproval => decision.AgentCorrections is null
                ? new NeedsConsent(
                    decision.ApprovalContext
                    ?? throw new InvalidOperationException("Approval decision missing approval context."),
                    decision.ApprovalMatches,
                    decision.ShellPolicyTrace)
                // Advice and a prompt are exclusive. Fail loudly instead of dropping the advice.
                : throw new InvalidOperationException("A consent request cannot carry agent advice."),
            ToolAuthorizationOutcome.RequiresAgentCorrection => new CorrectionRequired(
                decision.AgentCorrections
                ?? throw new InvalidOperationException("Agent correction decision missing correction facts."),
                decision.ApprovalMatches,
                decision.ShellPolicyTrace),
            ToolAuthorizationOutcome.Denied => new Denied(
                decision.DenyReason
                ?? throw new InvalidOperationException("Denied decision missing a deny reason."),
                decision.DenyMessage,
                decision.ShellPolicyTrace),
            _ => throw new ArgumentOutOfRangeException(
                nameof(decision),
                decision.Outcome,
                "Unknown authorization outcome.")
        };
    }
}
