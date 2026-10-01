// -----------------------------------------------------------------------
// <copyright file="ShellPolicyEvaluation.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Actors.Authorization.Consent;
using Netclaw.Configuration;
using Netclaw.Security;

namespace Netclaw.Actors.Tools;

/// <summary>
/// Represents the synchronous shell access phase before the coordinator checks approval evidence.
/// </summary>
/// <remarks>
/// A complete result ends evaluation. A continuation carries canonical facts into correction and approval selection.
/// </remarks>
internal abstract record ShellPolicyPreflightResult
{
    internal sealed record Complete : ShellPolicyPreflightResult
    {
        /// <param name="decision">The screen denial, the automatic allow, or a consent request without analysis.</param>
        /// <param name="authorizedAnalysis">The analysis that the process may execute. Only an allowed decision carries one.</param>
        internal Complete(ToolAuthorizationDecision decision, ShellCommandAnalysis? authorizedAnalysis)
        {
            ArgumentNullException.ThrowIfNull(decision);
            if (authorizedAnalysis is not null && decision.Outcome != ToolAuthorizationOutcome.Allowed)
                throw new ArgumentException("Only an allowed shell decision can carry analysis.", nameof(authorizedAnalysis));

            Decision = decision;
            AuthorizedAnalysis = authorizedAnalysis;
        }

        internal ToolAuthorizationDecision Decision { get; }

        internal ShellCommandAnalysis? AuthorizedAnalysis { get; }
    }

    internal sealed record Continue : ShellPolicyPreflightResult
    {
        internal Continue(
            ShellCommandAnalysis analysis,
            ToolApprovalContext approvalContext,
            BashDirectoryScopeProjection? directoryScopes)
        {
            ArgumentNullException.ThrowIfNull(analysis);
            ArgumentNullException.ThrowIfNull(approvalContext);

            Analysis = analysis;
            ApprovalContext = approvalContext;
            DirectoryScopes = directoryScopes;
        }

        internal ShellCommandAnalysis Analysis { get; }

        internal ToolApprovalContext ApprovalContext { get; }

        /// <summary>The directory proof that supplied the candidates, or null when the main parse supplied them.</summary>
        internal BashDirectoryScopeProjection? DirectoryScopes { get; }
    }
}

internal sealed class ShellPolicyEvaluation
{
    internal sealed class CandidateState(
        ShellPolicyCandidate candidate,
        ShellPolicyCandidatePathFacts pathFacts)
    {
        internal ShellPolicyCandidate Candidate { get; } = candidate;
        internal ShellPolicyCandidatePathFacts PathFacts { get; } = pathFacts;
        internal ShellGrantCandidateResult? GrantEvidence { get; private set; }
        internal int? GrantEvidenceOrder { get; private set; }

        /// <summary>Why the candidate needs no prompt, or null while it is uncovered.</summary>
        internal Coverage? Coverage { get; private set; }

        internal void ValidateActorEvidence()
        {
            if (Coverage != null
                || GrantEvidence is not null
                || GrantEvidenceOrder is not null)
            {
                throw new InvalidOperationException("Shell candidate already has coverage evidence.");
            }
        }

        internal void ApplyActorEvidence(ShellGrantCandidateResult evidence, int order)
        {
            ArgumentNullException.ThrowIfNull(evidence);
            if (evidence.CandidateId != Candidate.Id || order < 0)
                throw new InvalidOperationException("Invalid shell candidate approval evidence.");

            ValidateActorEvidence();
            (GrantEvidence, GrantEvidenceOrder, Coverage) = (evidence, order, evidence.Grant);
        }

        internal void Cover(Coverage coverage)
        {
            ArgumentNullException.ThrowIfNull(coverage);

            // A stored grant arrives only as actor evidence.
            if (coverage is Coverage.Stored)
                throw new InvalidOperationException("Invalid shell candidate coverage.");

            if (Coverage is not null)
                throw new InvalidOperationException("Shell candidate coverage was assigned twice.");

            Coverage = coverage;
        }
    }

    private readonly IReadOnlyList<CandidateState> _candidates;
    private readonly ShellPolicyDecisionTraceBuilder _trace;
    private bool _hasGrantEvidence;

    internal ShellPolicyEvaluation(
        ShellPolicyProjection projection,
        ShellPolicyDecisionTraceBuilder trace)
    {
        ArgumentNullException.ThrowIfNull(projection);
        ArgumentNullException.ThrowIfNull(trace);

        Projection = projection;
        _trace = trace;
        var pathFacts = ShellPolicyPathFacts.Create(
            projection.Candidates,
            projection.Environment.PathStyle);
        _candidates = Array.AsReadOnly(projection.Candidates
            .Select((candidate, index) => new CandidateState(candidate, pathFacts[index]))
            .ToArray());
    }

    internal ShellPolicyProjection Projection { get; }

    internal IReadOnlyList<ShellPolicyCandidate> Candidates => Projection.Candidates;

    internal IReadOnlyList<CandidateState> CandidateStates => _candidates;

    internal IEnumerable<CandidateState> GrantCandidates =>
        _candidates.Where(static state => state.Candidate.CanRequestStoredGrant);

    internal bool AllCovered => _candidates.All(static state => state.Coverage is not null);

    internal IReadOnlyList<ShellPolicyCandidate> UncoveredCandidates =>
        Array.AsReadOnly(_candidates
            .Where(static state => state.Coverage is null)
            .Select(static state => state.Candidate)
            .ToArray());

    internal ApprovalStoreFailure? PersistentStoreFailure { get; private set; }

    internal IReadOnlyList<ToolApprovalMatch> ApprovalMatches =>
        _candidates
            .Where(static state => state.GrantEvidence is { Grant: not null })
            .OrderBy(static state => state.GrantEvidenceOrder)
            .Select(static state => state.GrantEvidence!.FormatMatch(state.Candidate.Candidate))
            .ToArray();

    internal ToolApprovalContext GetUncoveredApprovalContext(
        IReadOnlyCollection<string> sessionOwnedDirectories)
    {
        var uncovered = UncoveredCandidates;
        if (uncovered.Count == 0)
            throw new InvalidOperationException("No uncovered shell candidates remain.");

        return ToolAccessPolicy.NarrowShellApprovalContext(
            Projection.ApprovalContext,
            uncovered.Select(static candidate => candidate.Candidate).ToArray(),
            sessionOwnedDirectories,
            Projection.Environment.PathStyle);
    }

    internal bool IsCovered(ShellPolicyCandidateId candidateId)
    {
        var index = candidateId.Value;
        if ((uint)index >= (uint)_candidates.Count)
            throw new ArgumentOutOfRangeException(nameof(candidateId));

        return _candidates[index].Coverage is not null;
    }

    internal void ApplyActorEvidence(ShellApprovalMatchResult evidence)
    {
        ArgumentNullException.ThrowIfNull(evidence);
        if (_hasGrantEvidence)
            throw new InvalidOperationException("Invalid shell approval evidence.");

        var currentCandidates = GrantCandidates
            .Select(state => new ShellGrantCandidate(
                state.Candidate.Id,
                state.Candidate.Candidate,
                Projection.ApprovalContext.Cwd))
            .ToArray();
        evidence = ShellApprovalMatchResult.Create(
            currentCandidates,
            evidence.PersistentStoreFailure,
            evidence.Candidates);

        var states = new CandidateState[evidence.Candidates.Count];
        for (var order = 0; order < evidence.Candidates.Count; order++)
        {
            var candidateEvidence = evidence.Candidates[order];
            var candidateId = candidateEvidence.CandidateId;
            if ((uint)candidateId.Value >= (uint)Candidates.Count)
                throw new InvalidOperationException("Invalid shell approval evidence.");

            var state = _candidates[candidateId.Value];
            state.ValidateActorEvidence();
            states[order] = state;
        }

        PersistentStoreFailure = evidence.PersistentStoreFailure;
        _hasGrantEvidence = true;
        for (var order = 0; order < evidence.Candidates.Count; order++)
        {
            var candidateEvidence = evidence.Candidates[order];
            var state = states[order];
            state.ApplyActorEvidence(candidateEvidence, order);
            _trace.AddActorEvidence(state.Candidate, candidateEvidence);
        }
    }

    internal void Cover(
        ShellPolicyCandidate candidate,
        Coverage coverage)
    {
        ArgumentNullException.ThrowIfNull(candidate);

        var index = candidate.Id.Value;
        if ((uint)index >= (uint)Candidates.Count)
            throw new InvalidOperationException("Invalid shell candidate ID.");

        var state = _candidates[index];
        if (!ReferenceEquals(candidate, state.Candidate))
            throw new InvalidOperationException("Shell candidate facts changed.");

        state.Cover(coverage);
        _trace.AddCoverage(state.Candidate, coverage);
    }

    internal ToolAuthorizationDecision Complete(
        ToolAuthorizationDecision decision,
        bool allowsUncoveredOneTime = false)
    {
        ArgumentNullException.ThrowIfNull(decision);

        var mayComplete = decision.Outcome switch
        {
            ToolAuthorizationOutcome.Allowed => AllCovered
                                                || allowsUncoveredOneTime
                                                && decision.AllowReason == ToolAllowReason.OneTimeApproval,
            ToolAuthorizationOutcome.RequiresApproval => Candidates.Count == 0 || !AllCovered,
            ToolAuthorizationOutcome.RequiresAgentCorrection => Candidates.Count == 0 || !AllCovered,
            ToolAuthorizationOutcome.Denied => true,
            _ => false,
        };
        if (!mayComplete)
            throw new InvalidOperationException("Invalid shell terminal decision.");

        return decision.WithShellPolicyTrace(_trace.Complete(decision));
    }

}
