// -----------------------------------------------------------------------
// <copyright file="ShellPolicyEvaluation.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Configuration;
using Netclaw.Security;

namespace Netclaw.Actors.Tools;

/// <summary>
/// Represents the final tool authorization result before an execution adapter acts on it.
/// </summary>
/// <remarks>
/// A shell result owns the exact analysis that the process can execute.
/// A direct result lets the registered tool handle its invocation.
/// A stopped result cannot carry a shell analysis.
/// </remarks>
internal abstract record ToolAuthorizationResult
{
    private protected ToolAuthorizationResult(
        ToolAuthorizationDecision decision,
        bool isAllowedResult)
    {
        ArgumentNullException.ThrowIfNull(decision);
        if ((decision.Outcome == ToolAuthorizationOutcome.Allowed) != isAllowedResult)
            throw new ArgumentException("The authorization result contradicts its decision.", nameof(decision));

        Decision = decision;
    }

    internal ToolAuthorizationDecision Decision { get; }

    internal sealed record ShellExecution : ToolAuthorizationResult
    {
        internal ShellExecution(
            ToolAuthorizationDecision decision,
            ShellCommandAnalysis analysis)
            : base(decision, isAllowedResult: true)
        {
            ArgumentNullException.ThrowIfNull(analysis);
            Analysis = analysis;
        }

        internal ShellCommandAnalysis Analysis { get; }
    }

    internal sealed record DirectExecution : ToolAuthorizationResult
    {
        internal DirectExecution(ToolAuthorizationDecision decision)
            : base(decision, isAllowedResult: true) { }
    }

    internal sealed record ShellValidation : ToolAuthorizationResult
    {
        internal ShellValidation(ToolAuthorizationDecision decision)
            : base(decision, isAllowedResult: true) { }
    }

    internal sealed record Stopped : ToolAuthorizationResult
    {
        internal Stopped(ToolAuthorizationDecision decision)
            : base(decision, isAllowedResult: false) { }
    }

    internal static ToolAuthorizationResult CreateDirect(ToolAuthorizationDecision decision)
        => decision.Outcome == ToolAuthorizationOutcome.Allowed
            ? new DirectExecution(decision)
            : new Stopped(decision);

    internal static ToolAuthorizationResult CreateShell(
        ToolAuthorizationDecision decision,
        ShellCommandAnalysis? authorizedAnalysis)
        => (decision.Outcome, authorizedAnalysis) switch
        {
            (ToolAuthorizationOutcome.Allowed, not null) => new ShellExecution(decision, authorizedAnalysis),
            (ToolAuthorizationOutcome.Allowed, null) => new ShellValidation(decision),
            (_, null) => new Stopped(decision),
            _ => throw new ArgumentException(
                "A stopped shell result cannot carry analysis.",
                nameof(authorizedAnalysis)),
        };

    internal static Stopped Stop(ToolAuthorizationDecision decision)
        => new(decision);
}

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
        internal Complete(ToolAuthorizationResult result)
        {
            if (result is not (ToolAuthorizationResult.ShellExecution
                or ToolAuthorizationResult.ShellValidation
                or ToolAuthorizationResult.Stopped))
            {
                throw new ArgumentException(
                    "A shell preflight cannot authorize direct execution.",
                    nameof(result));
            }

            Result = result;
        }

        internal ToolAuthorizationResult Result { get; }
    }

    internal sealed record Continue : ShellPolicyPreflightResult
    {
        internal Continue(
            ShellCommandAnalysis analysis,
            ToolApprovalContext approvalContext)
        {
            ArgumentNullException.ThrowIfNull(analysis);
            ArgumentNullException.ThrowIfNull(approvalContext);

            Analysis = analysis;
            ApprovalContext = approvalContext;
        }

        internal ShellCommandAnalysis Analysis { get; }

        internal ToolApprovalContext ApprovalContext { get; }
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
        internal ShellCoverageKind Coverage { get; private set; }

        internal void ValidateActorEvidence()
        {
            if (Coverage != ShellCoverageKind.Uncovered
                || GrantEvidence is not null
                || GrantEvidenceOrder is not null)
            {
                throw new InvalidOperationException("Shell candidate already has coverage evidence.");
            }
        }

        internal void ApplyActorEvidence(ShellGrantCandidateResult evidence, int order)
        {
            ValidateActorEvidence();
            (GrantEvidence, GrantEvidenceOrder, Coverage) = (evidence, order, evidence.Coverage);
        }

        internal void Cover(ShellCoverageKind coverage)
        {
            if (Coverage != ShellCoverageKind.Uncovered)
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

    internal bool AllCovered => _candidates.All(static state =>
        state.Coverage != ShellCoverageKind.Uncovered);

    internal IReadOnlyList<ShellPolicyCandidate> UncoveredCandidates =>
        Array.AsReadOnly(_candidates
            .Where(static state => state.Coverage == ShellCoverageKind.Uncovered)
            .Select(static state => state.Candidate)
            .ToArray());

    internal ApprovalStoreFailure? PersistentStoreFailure { get; private set; }

    internal IReadOnlyList<ToolApprovalMatch> ApprovalMatches =>
        _candidates
            .Where(static state => state.GrantEvidence is
                { Coverage: not ShellCoverageKind.Uncovered })
            .OrderBy(static state => state.GrantEvidenceOrder)
            .Select(static state => state.GrantEvidence!.FormatMatch(state.Candidate.Candidate))
            .ToArray();

    internal ToolApprovalContext GetUncoveredApprovalContext(
        IReadOnlyCollection<string> sessionOwnedDirectories)
    {
        var uncovered = UncoveredCandidates;
        if (uncovered.Count == 0)
            throw new InvalidOperationException("No uncovered shell candidates remain.");

        return Projection.HasCausalIntent
            ? Projection.ApprovalContext
            : ToolAccessPolicy.NarrowShellApprovalContext(
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

        return _candidates[index].Coverage != ShellCoverageKind.Uncovered;
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
        ShellCoverageKind coverage)
    {
        if (coverage is not (ShellCoverageKind.OneTime
            or ShellCoverageKind.ReviewedSafeReal
            or ShellCoverageKind.ReviewedSafeIntent
            or ShellCoverageKind.ApprovalExemptSideEffect))
        {
            throw new InvalidOperationException("Invalid shell candidate coverage.");
        }

        ArgumentNullException.ThrowIfNull(candidate);

        var index = candidate.Id.Value;
        if ((uint)index >= (uint)Candidates.Count)
            throw new InvalidOperationException("Invalid shell candidate ID.");

        var state = _candidates[index];
        if (!ReferenceEquals(candidate, state.Candidate))
            throw new InvalidOperationException("Shell candidate facts changed.");

        state.Cover(coverage);
        _trace.AddCoverage(
            state.Candidate,
            state.Coverage,
            state.GrantEvidence?.GrantCreatedAt);
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
