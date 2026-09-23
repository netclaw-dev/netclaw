// -----------------------------------------------------------------------
// <copyright file="ShellPolicyEvaluation.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
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
            ToolApprovalContext approvalContext,
            ShellExecutionEnvironment environment)
        {
            ArgumentNullException.ThrowIfNull(analysis);
            ArgumentNullException.ThrowIfNull(approvalContext);
            ArgumentNullException.ThrowIfNull(environment);

            Analysis = analysis;
            ApprovalContext = approvalContext;
            Environment = environment;
        }

        internal ShellCommandAnalysis Analysis { get; }

        internal ToolApprovalContext ApprovalContext { get; }

        internal ShellExecutionEnvironment Environment { get; }
    }
}

internal sealed class ShellPolicyEvaluation
{
    private readonly bool[] _coverage;
    private readonly ShellPolicyDecisionTraceBuilder _trace = new();
    private ShellApprovalMatchResult? _grantEvidence;

    internal ShellPolicyEvaluation(ShellPolicyProjection projection)
    {
        ArgumentNullException.ThrowIfNull(projection);

        Projection = projection;
        _coverage = new bool[projection.Candidates.Count];
    }

    internal ShellPolicyProjection Projection { get; }

    internal IReadOnlyList<ShellPolicyCandidate> Candidates => Projection.Candidates;

    internal bool AllCovered => _coverage.All(static covered => covered);

    internal IReadOnlyList<ShellPolicyCandidate> UncoveredCandidates =>
        Array.AsReadOnly(Projection.Candidates
            .Where((_, index) => !_coverage[index])
            .ToArray());

    internal ShellApprovalMatchResult? GrantEvidence => _grantEvidence;

    internal IReadOnlyList<ToolApprovalMatch> ApprovalMatches =>
        _grantEvidence?.Candidates
            .Where(static result => result.Coverage != ShellCoverageKind.Uncovered)
            .Select(result => result.FormatMatch(Candidates[result.CandidateId.Value].Candidate))
            .ToArray() ?? [];

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
        if ((uint)index >= (uint)_coverage.Length)
            throw new ArgumentOutOfRangeException(nameof(candidateId));

        return _coverage[index];
    }

    internal void ApplyActorEvidence(ShellApprovalMatchResult evidence)
    {
        ArgumentNullException.ThrowIfNull(evidence);
        if (_grantEvidence is not null)
            throw new InvalidOperationException("Invalid shell approval evidence.");

        var currentCandidates = Projection.GrantCandidates
            .Select(candidate => new ShellGrantCandidate(
                candidate.Id,
                candidate.Candidate,
                Projection.ApprovalContext.Cwd))
            .ToArray();
        evidence = ShellApprovalMatchResult.Create(
            currentCandidates,
            evidence.PersistentStoreFailure,
            evidence.Candidates);
        _grantEvidence = evidence;
        foreach (var candidateEvidence in evidence.Candidates)
        {
            var candidateId = candidateEvidence.CandidateId;
            if ((uint)candidateId.Value >= (uint)Candidates.Count)
                throw new InvalidOperationException("Invalid shell approval evidence.");

            var candidate = Candidates[candidateId.Value];
            if (candidateEvidence.Coverage != ShellCoverageKind.Uncovered)
            {
                var index = ValidateCoverageAssignment(candidate, candidateEvidence.Coverage);
                _trace.AddActorEvidence(candidate, candidateEvidence);
                _coverage[index] = true;
                continue;
            }

            _trace.AddActorEvidence(candidate, candidateEvidence);
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

        var index = ValidateCoverageAssignment(candidate, coverage);
        _trace.AddCoverage(coverage, candidate);
        _coverage[index] = true;
    }

    private int ValidateCoverageAssignment(
        ShellPolicyCandidate candidate,
        ShellCoverageKind coverage)
    {
        ArgumentNullException.ThrowIfNull(candidate);

        var index = candidate.Id.Value;
        if ((uint)index >= (uint)Candidates.Count)
            throw new InvalidOperationException("Invalid shell candidate ID.");

        if (!ReferenceEquals(candidate, Projection.Candidates[index]))
            throw new InvalidOperationException("Shell candidate facts changed.");

        if (_coverage[index])
            throw new InvalidOperationException("Shell candidate coverage was assigned twice.");

        if (coverage == ShellCoverageKind.Uncovered)
            throw new InvalidOperationException("Invalid shell candidate coverage.");

        return index;
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

    internal ToolAuthorizationDecision InternalFailure() => Complete(
        ToolAuthorizationDecision.Deny("internal_policy_failure"));

}
