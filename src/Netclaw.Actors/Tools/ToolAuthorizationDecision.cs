// -----------------------------------------------------------------------
// <copyright file="ToolAuthorizationDecision.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Security;

namespace Netclaw.Actors.Tools;

/// <summary>
/// Specifies the authorization outcome for one tool invocation attempt.
/// </summary>
internal enum ToolAuthorizationOutcome
{
    /// <summary>
    /// The current attempt can execute without a user prompt.
    /// </summary>
    /// <remarks>
    /// The decision contains an <see cref="ToolAllowReason"/> value.
    /// A later tool failure does not change this authorization outcome.
    /// </remarks>
    Allowed,

    /// <summary>
    /// The current attempt cannot execute until the user grants approval.
    /// </summary>
    /// <remarks>
    /// The decision contains a <see cref="ToolApprovalContext"/> value.
    /// This outcome describes the authorization gate before a user response.
    /// A caller without an approval channel must fail closed.
    /// </remarks>
    RequiresApproval,

    /// <summary>
    /// The current attempt must not execute because the agent should call a native tool.
    /// </summary>
    RequiresAgentCorrection,

    /// <summary>
    /// The current attempt cannot execute and must not prompt the user.
    /// </summary>
    /// <remarks>
    /// The decision contains a stable deny reason.
    /// A new user approval cannot override this outcome.
    /// </remarks>
    Denied
}

/// <summary>
/// Specifies the rule that allowed one tool invocation attempt.
/// </summary>
internal enum ToolAllowReason
{
    /// <summary>
    /// The resolved approval policy sets the tool call to <c>Auto</c>.
    /// </summary>
    /// <remarks>
    /// This value covers explicit overrides and effective profile defaults.
    /// It does not cover safe verbs or prior approval grants.
    /// </remarks>
    PolicyAuto,

    /// <summary>
    /// The initial shell approval covers control of the session-owned job.
    /// </summary>
    BackgroundJobLifecycle,

    /// <summary>
    /// Reviewed-safe shell policy allows every command candidate.
    /// </summary>
    /// <remarks>
    /// The parser must produce a clean candidate set.
    /// Each phrase must occur in the reviewed list.
    /// Shared path authorization must allow every effective directory and path.
    /// </remarks>
    ReviewedSafePolicy,

    /// <summary>
    /// Every parsed shell candidate belongs to the fixed approval-exempt set.
    /// </summary>
    /// <remarks>
    /// The current set contains <c>echo</c>, <c>printf</c>, <c>:</c>,
    /// <c>true</c>, and <c>false</c>.
    /// A path or redirect disqualifies a candidate.
    /// Other candidates in the same command require separate authorization.
    /// This reason does not claim that the complete shell expression has no effects.
    /// </remarks>
    ApprovalExemptShellCandidates,

    /// <summary>
    /// Existing approval grants match every candidate that requires a grant.
    /// </summary>
    /// <remarks>
    /// The decision contains the matched grants as structured evidence.
    /// One compound call can use session and persistent approval sources.
    /// </remarks>
    StoredApproval,

    /// <summary>
    /// A one-time grant from an earlier user response allows this retry.
    /// </summary>
    /// <remarks>
    /// The tool name and all extracted patterns must match the retry state.
    /// This value does not represent a session or persistent approval.
    /// The pipeline clears the retry state after the attempt.
    /// </remarks>
    OneTimeApproval
}

/// <summary>
/// Provides operator-facing explanations for tool allow reasons.
/// </summary>
internal static class ToolAllowReasonExtensions
{
    /// <summary>
    /// Gets a human-readable explanation for an allow reason.
    /// </summary>
    /// <param name="reason">The allow reason.</param>
    /// <returns>A short explanation for logs and diagnostics.</returns>
    /// <exception cref="ArgumentOutOfRangeException">
    /// The reason is not a defined <see cref="ToolAllowReason"/> value.
    /// </exception>
    public static string GetDescription(this ToolAllowReason reason)
        => reason switch
        {
            ToolAllowReason.PolicyAuto =>
                "The resolved approval policy allowed the tool automatically.",
            ToolAllowReason.BackgroundJobLifecycle =>
                "The initial shell approval covered control of the session-owned background job.",
            ToolAllowReason.ReviewedSafePolicy =>
                "Reviewed-safe shell policy allowed every candidate after path authorization.",
            ToolAllowReason.ApprovalExemptShellCandidates =>
                "Every parsed shell candidate was exempt from stored approval checks.",
            ToolAllowReason.StoredApproval =>
                "Existing approval grants matched every candidate that required a grant.",
            ToolAllowReason.OneTimeApproval =>
                "A one-time approval matched this invocation retry.",
            _ => throw new ArgumentOutOfRangeException(nameof(reason), reason, "Unknown tool allow reason.")
        };
}

/// <summary>
/// Describes the complete authorization result for one tool invocation attempt.
/// </summary>
/// <remarks>
/// The dispatcher returns this result before tool execution or a user prompt.
/// The static factory methods enforce the fields that each outcome requires.
/// </remarks>
public sealed record ToolAuthorizationDecision
{
    private ToolAuthorizationDecision(
        ToolAuthorizationOutcome outcome,
        ToolAllowReason? allowReason,
        string? denyReason,
        string? denyMessage,
        ToolApprovalContext? approvalContext,
        ToolCorrection? agentCorrection,
        IReadOnlyList<ToolApprovalMatch> approvalMatches,
        ShellPolicyDecisionTrace? shellPolicyTrace = null)
    {
        Outcome = outcome;
        AllowReason = allowReason;
        DenyReason = denyReason;
        DenyMessage = denyMessage;
        ApprovalContext = approvalContext;
        AgentCorrection = agentCorrection;
        ApprovalMatches = approvalMatches;
        ShellPolicyTrace = shellPolicyTrace ?? ShellPolicyDecisionTrace.Empty;
    }

    /// <summary>
    /// Gets the action that the caller must take for this attempt.
    /// </summary>
    internal ToolAuthorizationOutcome Outcome { get; }

    /// <summary>
    /// Gets whether policy permits execution or permits an approval request.
    /// </summary>
    public bool Allowed => Outcome is ToolAuthorizationOutcome.Allowed
        or ToolAuthorizationOutcome.RequiresApproval;

    /// <summary>
    /// Gets whether the caller must obtain approval before execution.
    /// </summary>
    public bool NeedsApproval => Outcome is ToolAuthorizationOutcome.RequiresApproval;

    /// <summary>
    /// Gets the allow rule when <see cref="Outcome"/> is <see cref="ToolAuthorizationOutcome.Allowed"/>.
    /// </summary>
    internal ToolAllowReason? AllowReason { get; }

    /// <summary>
    /// Gets the stable deny reason when <see cref="Outcome"/> is <see cref="ToolAuthorizationOutcome.Denied"/>.
    /// </summary>
    public string? DenyReason { get; }

    /// <summary>
    /// Gets optional human-readable denial detail returned to the agent.
    /// </summary>
    internal string? DenyMessage { get; }

    /// <summary>
    /// Gets the prompt data when <see cref="Outcome"/> is <see cref="ToolAuthorizationOutcome.RequiresApproval"/>.
    /// </summary>
    public ToolApprovalContext? ApprovalContext { get; }

    /// <summary>
    /// Gets the typed correction when <see cref="Outcome"/> is
    /// <see cref="ToolAuthorizationOutcome.RequiresAgentCorrection"/>.
    /// </summary>
    internal ToolCorrection? AgentCorrection { get; }

    /// <summary>
    /// Gets the session or persistent grants that matched this attempt.
    /// </summary>
    /// <remarks>
    /// A prompt decision can contain partial matches for a compound command.
    /// An allowed stored-approval decision contains a match for each required candidate.
    /// A one-time decision can contain stored matches for part of a compound command.
    /// Policy, safe-rule, approval-exempt, and deny decisions contain an empty list.
    /// </remarks>
    internal IReadOnlyList<ToolApprovalMatch> ApprovalMatches { get; }

    internal ShellPolicyDecisionTrace ShellPolicyTrace { get; init; }

    /// <summary>
    /// Creates an allowed result without stored approval matches.
    /// </summary>
    internal static ToolAuthorizationDecision Allow(ToolAllowReason reason)
    {
        ValidateAllowReason(reason);
        return new ToolAuthorizationDecision(ToolAuthorizationOutcome.Allowed, reason, null, null, null, null, []);
    }

    /// <summary>
    /// Creates an allowed result with structured approval matches.
    /// </summary>
    internal static ToolAuthorizationDecision Allow(
        ToolAllowReason reason,
        IReadOnlyList<ToolApprovalMatch> approvalMatches)
    {
        ValidateAllowReason(reason);
        ArgumentNullException.ThrowIfNull(approvalMatches);
        return new ToolAuthorizationDecision(
            ToolAuthorizationOutcome.Allowed,
            reason,
            null,
            null,
            null,
            null,
            [.. approvalMatches]);
    }

    /// <summary>
    /// Creates a hard-deny result.
    /// </summary>
    public static ToolAuthorizationDecision Deny(string reason, string? message = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        return new ToolAuthorizationDecision(ToolAuthorizationOutcome.Denied, null, reason, message, null, null, []);
    }

    /// <summary>
    /// Creates an approval-request result without existing approval matches.
    /// </summary>
    public static ToolAuthorizationDecision RequiresApproval(ToolApprovalContext context)
        => RequiresApproval(context, correction: null);

    /// <summary>
    /// Creates an approval result with a correction to use only if existing authority does not match.
    /// </summary>
    internal static ToolAuthorizationDecision RequiresApproval(
        ToolApprovalContext context,
        ToolCorrection? correction)
    {
        ArgumentNullException.ThrowIfNull(context);
        return new ToolAuthorizationDecision(
            ToolAuthorizationOutcome.RequiresApproval,
            null,
            null,
            null,
            context,
            correction,
            []);
    }

    /// <summary>
    /// Creates an approval-request result with partial stored approval matches.
    /// </summary>
    internal static ToolAuthorizationDecision RequiresApproval(
        ToolApprovalContext context,
        IReadOnlyList<ToolApprovalMatch> approvalMatches,
        ToolCorrection? correction = null)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(approvalMatches);
        return new ToolAuthorizationDecision(
            ToolAuthorizationOutcome.RequiresApproval,
            null,
            null,
            null,
            context,
            correction,
            [.. approvalMatches]);
    }

    /// <summary>
    /// Creates a typed agent-correction result that grants no execution authority.
    /// </summary>
    internal static ToolAuthorizationDecision RequireAgentCorrection(ToolCorrection correction)
    {
        ArgumentNullException.ThrowIfNull(correction);
        return new ToolAuthorizationDecision(
            ToolAuthorizationOutcome.RequiresAgentCorrection,
            null,
            null,
            null,
            null,
            correction,
            []);
    }

    internal ToolAuthorizationDecision WithShellPolicyTrace(ShellPolicyDecisionTrace trace)
    {
        ArgumentNullException.ThrowIfNull(trace);
        return this with { ShellPolicyTrace = trace };
    }

    internal ToolAuthorizationDecision WithApprovalMatches(
        IReadOnlyList<ToolApprovalMatch> approvalMatches)
    {
        ArgumentNullException.ThrowIfNull(approvalMatches);
        return Outcome switch
        {
            ToolAuthorizationOutcome.RequiresApproval when ApprovalContext is { } context =>
                RequiresApproval(context, approvalMatches, AgentCorrection),
            ToolAuthorizationOutcome.Allowed when AllowReason is { } reason =>
                Allow(reason, approvalMatches),
            _ => this
        };
    }

    private static void ValidateAllowReason(ToolAllowReason reason)
    {
        if (!Enum.IsDefined(reason))
            throw new ArgumentOutOfRangeException(nameof(reason), reason, "Unknown tool allow reason.");
    }
}

internal sealed class ToolCorrectionRequiredException : InvalidOperationException
{
    internal ToolCorrectionRequiredException(ToolCorrection correction)
        : base("Tool invocation requires agent correction.")
    {
        ArgumentNullException.ThrowIfNull(correction);
        Correction = correction;
    }

    internal ToolCorrection Correction { get; }
}
