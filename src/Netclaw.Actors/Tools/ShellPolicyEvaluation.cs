// -----------------------------------------------------------------------
// <copyright file="ShellPolicyEvaluation.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Security;

namespace Netclaw.Actors.Tools;

internal abstract record ShellPolicyPreflightResult
{
    private ShellPolicyPreflightResult()
    {
    }

    internal sealed record Complete : ShellPolicyPreflightResult
    {
        internal Complete(
            ToolAuthorizationDecision decision,
            ShellCommandAnalysis? authorizedAnalysis)
        {
            ArgumentNullException.ThrowIfNull(decision);
            if (authorizedAnalysis is not null
                && (!decision.Allowed || decision.NeedsApproval))
            {
                throw new ArgumentException(
                    "Only an immediate shell allow can carry analysis.",
                    nameof(authorizedAnalysis));
            }

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
            ShellExecutionEnvironment environment,
            ToolCorrection? correction)
        {
            ArgumentNullException.ThrowIfNull(analysis);
            ArgumentNullException.ThrowIfNull(approvalContext);
            ArgumentNullException.ThrowIfNull(environment);

            Analysis = analysis;
            ApprovalContext = approvalContext;
            Environment = environment;
            Correction = correction;
        }

        internal ShellCommandAnalysis Analysis { get; }

        internal ToolApprovalContext ApprovalContext { get; }

        internal ShellExecutionEnvironment Environment { get; }

        internal ToolCorrection? Correction { get; }
    }
}

internal sealed class ShellPolicyEvaluation
{
    private readonly ShellPolicyCoverageSource[] _coverage;
    private readonly ShellPolicyDecisionTraceBuilder _trace = new();
    private ValidatedShellGrantEvidence? _grantEvidence;

    internal ShellPolicyEvaluation(ShellPolicyProjection projection)
    {
        ArgumentNullException.ThrowIfNull(projection);

        Projection = projection;
        _coverage = new ShellPolicyCoverageSource[projection.Candidates.Count];
    }

    internal ShellPolicyProjection Projection { get; }

    internal IReadOnlyList<ShellPolicyCandidate> Candidates => Projection.Candidates;

    internal bool AllCovered => !_coverage.Contains(ShellPolicyCoverageSource.Uncovered);

    internal IReadOnlyList<ShellPolicyCandidate> UncoveredCandidates =>
        Array.AsReadOnly(Projection.Candidates
            .Where((_, index) => _coverage[index] == ShellPolicyCoverageSource.Uncovered)
            .ToArray());

    internal ValidatedShellGrantEvidence? GrantEvidence => _grantEvidence;

    internal IReadOnlyList<ToolApprovalMatch> ApprovalMatches =>
        _grantEvidence?.ApprovalMatches ?? [];

    internal bool HasOneTimeCoverage => _coverage.Contains(ShellPolicyCoverageSource.OneTime);

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

    internal ShellPolicyCoverageSource CoverageFor(ShellPolicyCandidateId candidateId)
    {
        var index = candidateId.Value;
        if ((uint)index >= (uint)_coverage.Length)
            throw new ArgumentOutOfRangeException(nameof(candidateId));

        return _coverage[index];
    }

    internal bool IsCovered(ShellPolicyCandidateId candidateId)
    {
        return CoverageFor(candidateId) != ShellPolicyCoverageSource.Uncovered;
    }

    internal void ApplyActorEvidence(ValidatedShellGrantEvidence evidence)
    {
        ArgumentNullException.ThrowIfNull(evidence);
        if (_grantEvidence is not null
            || !ReferenceEquals(evidence.SourceCandidates, Projection.GrantCandidates))
        {
            throw new InvalidOperationException("Invalid shell approval evidence.");
        }

        _grantEvidence = evidence;
        foreach (var candidateEvidence in evidence.CandidateEvidence)
        {
            var actorEvidence = candidateEvidence.ActorEvidence;
            if (actorEvidence.GrantCoverage is { } grantCoverage)
            {
                Cover(
                    candidateEvidence.Candidate,
                    ToCoverageSource(grantCoverage),
                    actorEvidence.GrantCreatedAt);
            }
            else
            {
                _trace.AddActorEvidence(candidateEvidence.Candidate, actorEvidence);
            }
        }

    }

    internal void Cover(
        ShellPolicyCandidate candidate,
        ShellPolicyCoverageSource source,
        DateTimeOffset? grantTimestamp = null)
    {
        ArgumentNullException.ThrowIfNull(candidate);

        var index = candidate.Id.Value;
        if ((uint)index >= (uint)Candidates.Count)
            throw new InvalidOperationException("Invalid shell candidate ID.");

        if (!ReferenceEquals(candidate, Projection.Candidates[index]))
            throw new InvalidOperationException("Shell candidate facts changed.");

        if (_coverage[index] != ShellPolicyCoverageSource.Uncovered)
            throw new InvalidOperationException("Shell candidate coverage was assigned twice.");

        if (!Enum.IsDefined(source)
            || source == ShellPolicyCoverageSource.Uncovered
            || grantTimestamp is not null
            && source is not
                (ShellPolicyCoverageSource.PersistentGlobal
                or ShellPolicyCoverageSource.PersistentFolder))
        {
            throw new InvalidOperationException("Invalid shell candidate coverage.");
        }

        _trace.AddCoverage(source, candidate, grantTimestamp);
        _coverage[index] = source;
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
            ToolAuthorizationOutcome.Denied => true,
            _ => false,
        };
        if (!mayComplete)
            throw new InvalidOperationException("Invalid shell terminal decision.");

        return decision.WithShellPolicyTrace(_trace.Complete(decision));
    }

    internal ToolAuthorizationDecision InternalFailure() => Complete(
        ToolAuthorizationDecision.Deny("internal_policy_failure"));

    private static ShellPolicyCoverageSource ToCoverageSource(ShellCoverageKind coverage)
        => coverage switch
        {
            ShellCoverageKind.Session => ShellPolicyCoverageSource.Session,
            ShellCoverageKind.PersistentGlobal => ShellPolicyCoverageSource.PersistentGlobal,
            ShellCoverageKind.PersistentFolder => ShellPolicyCoverageSource.PersistentFolder,
            _ => throw new InvalidOperationException("Invalid actor coverage kind."),
        };
}
