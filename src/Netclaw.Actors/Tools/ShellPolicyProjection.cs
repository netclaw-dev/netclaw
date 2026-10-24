// -----------------------------------------------------------------------
// <copyright file="ShellPolicyProjection.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Configuration;
using Netclaw.Security;
using Netclaw.Tools;
using Netclaw.Tools.Authorization.Consent;
using System.Diagnostics.CodeAnalysis;
using ShellSyntaxTree;

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
/// its candidates came from the directory proof: no reviewed-safe policy of the real directory.
/// Only its diagnostics can use the intent rule. The side-effect exemption does not read the role.
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

    internal ShellSyntaxTree.RedirectedCompoundSyntax? SourceCompoundOwner { get; init; }

    internal ShellSyntaxTree.CompoundRedirect? SourceCompoundRedirect { get; init; }

    /// <summary>The directory that a causal list changed to before this diagnostic, or null.</summary>
    internal string? IntentDirectory { get; init; }

    // A command that runs no program needs no grant: the file rules judge its redirects.
    internal bool CanRequestStoredGrant =>
        SourceCompoundOwner is null
        && !ApprovalPatternMatching.IsPureSideEffect(Candidate)
        && !Candidate.RunsNoProgram;

    internal bool CanUseRealReviewedSafePolicy =>
        Role == ShellPolicyCandidateRole.Ordinary
        && SourceCompoundOwner is null
        && Candidate.AssignmentDigest is null;
}

/// <summary>
/// The immutable policy-facing projection of one shell approval context.
/// </summary>
internal sealed record ShellPolicyProjection
{
    private ShellPolicyProjection(
        ShellExecutionEnvironment environment,
        ToolApprovalContext approvalContext,
        IReadOnlyList<ShellPolicyCandidate> candidates,
        OneTimeConsent? oneTimeConsent)
    {
        Environment = environment;
        ApprovalContext = approvalContext;
        Candidates = candidates;
        OneTimeConsent = oneTimeConsent;
    }

    internal ShellExecutionEnvironment Environment { get; }

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
        ShellCommandAnalysis analysis,
        out ShellPolicyProjection? projection)
    {
        ArgumentNullException.ThrowIfNull(environment);
        ArgumentNullException.ThrowIfNull(approvalContext);
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(analysis);

        projection = null;
        if (approvalContext.Candidates is null)
            return false;

        if (!ShellPolicyPathFacts.TryCreateCompoundRedirectViews(analysis, out var compoundViews))
            return false;

        var isCausalList = directoryScopes?.IsCausalList == true;
        var candidates = new ShellPolicyCandidate[approvalContext.Candidates.Count];
        var candidateCopies = new ApprovalCandidate[approvalContext.Candidates.Count];
        for (var index = 0; index < approvalContext.Candidates.Count; index++)
        {
            var source = approvalContext.Candidates[index];
            if (source is null)
                return false;

            if (!HasValidCompoundCandidate(source, analysis, compoundViews))
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
                SourceOccurrence = null,
                SourceCompoundOwner = null,
                SourceCompoundRedirect = null
            };
            candidateCopies[index] = copy;
            candidates[index] = new ShellPolicyCandidate(
                new ShellPolicyCandidateId(index),
                copy,
                source.SourceOccurrence)
            {
                Role = role,
                IntentDirectory = intentDirectory,
                SourceCompoundOwner = source.SourceCompoundOwner,
                SourceCompoundRedirect = source.SourceCompoundRedirect
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

    private static bool HasValidCompoundCandidate(
        ApprovalCandidate candidate,
        ShellCommandAnalysis analysis,
        IReadOnlyList<ShellPolicyCompoundRedirectPathFacts> compoundViews)
    {
        var owner = candidate.SourceCompoundOwner;
        if (owner is null && candidate.SourceCompoundRedirect is null)
            return true;

        if (owner is null
            || candidate.SourceOccurrence is not null
            || candidate.Shell != ApprovalShell.Bash
            || candidate.VerbTokens is not null
            || candidate.Unresolved != ShellUnresolvedPart.Command
            || candidate.RunsNoProgram)
        {
            return false;
        }

        if (!compoundViews.Any(view => ReferenceEquals(view.Owner.Syntax, owner)))
            return false;

        var redirect = candidate.SourceCompoundRedirect;
        if (redirect is null)
            return string.Equals(candidate.Verb, analysis.Source, StringComparison.Ordinal)
                   && !candidate.UsesFileToolRedirects
                   && string.Equals(candidate.Directory, analysis.WorkingDirectory, StringComparison.Ordinal);

        if (!candidate.UsesFileToolRedirects)
            return false;

        foreach (var view in compoundViews)
        {
            var mode = view.PathFact.Source.RedirectMode;
            if (!ReferenceEquals(view.Owner.Syntax, owner)
                || !ReferenceEquals(view.Redirect, redirect)
                || mode is null
                || view.PathFact.State != ShellPolicyPathResolutionState.Known)
            {
                continue;
            }

            var tool = mode.Value == FileRedirectMode.Input ? "read" : "write";
            var workingDirectory = (owner.WorkingDirectory as ShellValueDomain.Exact)?.Value;
            if (view.PathFact.Paths.Any(path =>
                    !ShellRedirectPolicyFacts.IsNullDevice(path)
                    && string.Equals(candidate.Verb, $"{tool} {path.Value}", StringComparison.Ordinal)
                    && string.Equals(candidate.Directory, workingDirectory, StringComparison.Ordinal)))
            {
                return true;
            }
        }

        return false;
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
            contextCopy,
            candidateView,
            context.Approval.OneTimeConsent);
    }
}
