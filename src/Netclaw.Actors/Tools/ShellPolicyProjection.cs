// -----------------------------------------------------------------------
// <copyright file="ShellPolicyProjection.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Configuration;
using Netclaw.Security;
using Netclaw.Tools;
using Netclaw.Tools.Authorization.Consent;

namespace Netclaw.Actors.Tools;

internal readonly record struct ShellPolicyCandidateId
{
    internal ShellPolicyCandidateId(int value)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(value);
        Value = value;
    }

    internal int Value { get; }
}

/// <summary>
/// Selects the coverage rules of a candidate. A causal list keeps the rules that it had before
/// its candidates came from the directory proof: no reviewed-safe policy of the real directory and
/// no side-effect exemption. Only its diagnostics can use the intent rule.
/// </summary>
internal enum ShellPolicyCandidateRole
{
    Ordinary = 0,
    CausalPrerequisite = 1,
    CausalIntentConsumer = 2,
}

internal sealed record ShellPolicyCandidate(
    ShellPolicyCandidateId Id,
    ApprovalCandidate Candidate,
    ShellSyntaxTree.CommandOccurrence? SourceOccurrence)
{
    internal ShellPolicyCandidateRole Role { get; init; }

    /// <summary>The directory that a causal list changed to before this diagnostic, or null.</summary>
    internal string? IntentDirectory { get; init; }

    internal bool CanRequestStoredGrant => !ApprovalPatternMatching.IsPureSideEffect(Candidate);

    internal bool CanUseRealReviewedSafePolicy =>
        Role == ShellPolicyCandidateRole.Ordinary
        && Candidate.AssignmentDigest is null;
}

/// <summary>
/// The immutable policy-facing projection of one shell approval context.
/// </summary>
internal sealed record ShellPolicyProjection
{
    private ShellPolicyProjection(
        ShellExecutionEnvironment environment,
        InteractiveApprovalCapability interactiveApproval,
        ToolApprovalContext approvalContext,
        IReadOnlyList<ShellPolicyCandidate> candidates,
        OneTimeConsent? oneTimeConsent)
    {
        Environment = environment;
        InteractiveApproval = interactiveApproval;
        ApprovalContext = approvalContext;
        Candidates = candidates;
        OneTimeConsent = oneTimeConsent;
    }

    internal ShellExecutionEnvironment Environment { get; }

    internal InteractiveApprovalCapability InteractiveApproval { get; }

    internal ToolApprovalContext ApprovalContext { get; }

    internal IReadOnlyList<ShellPolicyCandidate> Candidates { get; }

    /// <summary>The "Once" answer that this attempt carries, or null.</summary>
    internal OneTimeConsent? OneTimeConsent { get; }

    internal bool HasExactOneTimeApproval(
        string toolName,
        ToolApprovalContext approvalContext)
        => OneTimeApprovalKeys.Matches(OneTimeConsent, toolName, approvalContext);

    internal static bool TryCreate(
        ShellExecutionEnvironment environment,
        ToolApprovalContext approvalContext,
        BashDirectoryScopeProjection? directoryScopes,
        ToolExecutionContext context,
        out ShellPolicyProjection? projection)
    {
        ArgumentNullException.ThrowIfNull(environment);
        ArgumentNullException.ThrowIfNull(approvalContext);
        ArgumentNullException.ThrowIfNull(context);

        projection = null;
        if (approvalContext.Candidates is null)
            return false;

        var isCausalList = directoryScopes?.IsCausalList == true;
        var candidates = new ShellPolicyCandidate[approvalContext.Candidates.Count];
        var candidateCopies = new ApprovalCandidate[approvalContext.Candidates.Count];
        for (var index = 0; index < approvalContext.Candidates.Count; index++)
        {
            var source = approvalContext.Candidates[index];
            if (source is null)
                return false;

            var role = ShellPolicyCandidateRole.Ordinary;
            string? intentDirectory = null;
            if (isCausalList)
            {
                // Every causal candidate comes from one slice of the directory proof.
                if (directoryScopes!.FindSlice(source.SourceOccurrence) is not { } slice)
                    return false;

                intentDirectory = slice.IntentDirectory;
                role = intentDirectory is null
                    ? ShellPolicyCandidateRole.CausalPrerequisite
                    : ShellPolicyCandidateRole.CausalIntentConsumer;
            }

            var copy = source with
            {
                VerbTokens = source.VerbTokens is null
                    ? null
                    : Array.AsReadOnly(source.VerbTokens.ToArray()),
                SourceOccurrence = null
            };
            candidateCopies[index] = copy;
            candidates[index] = new ShellPolicyCandidate(
                new ShellPolicyCandidateId(index),
                copy,
                source.SourceOccurrence)
            {
                Role = role,
                IntentDirectory = intentDirectory
            };
        }

        projection = Create(
            environment,
            approvalContext,
            context,
            candidates,
            candidateCopies);
        return true;
    }

    private static ShellPolicyProjection Create(
        ShellExecutionEnvironment environment,
        ToolApprovalContext approvalContext,
        ToolExecutionContext context,
        ShellPolicyCandidate[] candidates,
        IReadOnlyList<ApprovalCandidate> approvalCandidates)
    {
        var contextCopy = approvalContext with
        {
            Patterns = Array.AsReadOnly(approvalContext.Patterns.ToArray()),
            CandidateVerbs = Array.AsReadOnly(approvalContext.CandidateVerbs.ToArray()),
            Options = Array.AsReadOnly(approvalContext.Options.ToArray()),
            Candidates = Array.AsReadOnly(approvalCandidates.ToArray())
        };
        var candidateView = Array.AsReadOnly(candidates);
        return new ShellPolicyProjection(
            environment,
            context.RunScope.InteractiveApproval,
            contextCopy,
            candidateView,
            context.Approval.OneTimeConsent);
    }
}
