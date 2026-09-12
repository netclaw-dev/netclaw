// -----------------------------------------------------------------------
// <copyright file="ShellPolicyEvaluation.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Security;

namespace Netclaw.Actors.Tools;

/// <summary>
/// Represents the final shell authorization result before an execution adapter acts on it.
/// </summary>
/// <remarks>
/// An authorized result always owns the exact analysis that the shell process can execute.
/// A tool-validation result lets the shell tool report malformed arguments before a process exists.
/// A stopped result cannot carry executable analysis.
/// </remarks>
internal abstract record ShellAuthorizationResult
{
    private protected ShellAuthorizationResult(ToolAuthorizationDecision decision)
    {
        ArgumentNullException.ThrowIfNull(decision);
        Decision = decision;
    }

    internal ToolAuthorizationDecision Decision { get; }

    internal sealed record Authorized : ShellAuthorizationResult
    {
        internal Authorized(
            ToolAuthorizationDecision decision,
            ShellCommandAnalysis analysis)
            : base(decision)
        {
            ArgumentNullException.ThrowIfNull(analysis);
            if (decision.Outcome != ToolAuthorizationOutcome.Allowed)
            {
                throw new ArgumentException(
                    "An authorized shell result requires an allowed decision.",
                    nameof(decision));
            }

            Analysis = analysis;
        }

        internal ShellCommandAnalysis Analysis { get; }
    }

    internal sealed record ToolValidation : ShellAuthorizationResult
    {
        internal ToolValidation(ToolAuthorizationDecision decision)
            : base(decision)
        {
            if (decision.Outcome != ToolAuthorizationOutcome.Allowed)
            {
                throw new ArgumentException(
                    "Shell tool validation requires an allowed decision.",
                    nameof(decision));
            }
        }
    }

    internal sealed record Stopped : ShellAuthorizationResult
    {
        internal Stopped(ToolAuthorizationDecision decision)
            : base(decision)
        {
            if (decision.Outcome == ToolAuthorizationOutcome.Allowed)
            {
                throw new ArgumentException(
                    "An allowed shell decision cannot use a stopped result.",
                    nameof(decision));
            }
        }
    }

    internal static ShellAuthorizationResult Create(
        ToolAuthorizationDecision decision,
        ShellCommandAnalysis? authorizedAnalysis)
        => (decision.Outcome, authorizedAnalysis) switch
        {
            (ToolAuthorizationOutcome.Allowed, not null) => new Authorized(decision, authorizedAnalysis),
            (ToolAuthorizationOutcome.Allowed, null) => new ToolValidation(decision),
            (_, null) => new Stopped(decision),
            _ => throw new ArgumentException(
                "A stopped shell result cannot carry executable analysis.",
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
    private protected ShellPolicyPreflightResult(ToolAuthorizationDecision decision)
    {
        ArgumentNullException.ThrowIfNull(decision);
        Decision = decision;
    }

    internal ToolAuthorizationDecision Decision { get; }

    internal sealed record Complete : ShellPolicyPreflightResult
    {
        internal Complete(
            ToolAuthorizationDecision decision,
            ShellCommandAnalysis? authorizedAnalysis)
            : base(decision)
        {
            if (authorizedAnalysis is not null
                && decision.Outcome != ToolAuthorizationOutcome.Allowed)
            {
                throw new ArgumentException(
                    "Only an immediate shell allow can carry analysis.",
                    nameof(authorizedAnalysis));
            }
            AuthorizedAnalysis = authorizedAnalysis;
        }

        internal ShellCommandAnalysis? AuthorizedAnalysis { get; }
    }

    internal sealed record Continue : ShellPolicyPreflightResult
    {
        internal Continue(
            ShellCommandAnalysis analysis,
            ToolApprovalContext approvalContext,
            ShellExecutionEnvironment environment)
            : base(ToolAuthorizationDecision.RequiresApproval(approvalContext))
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

    private static ShellPolicyCoverageSource ToCoverageSource(ShellCoverageKind coverage)
        => coverage switch
        {
            ShellCoverageKind.Session => ShellPolicyCoverageSource.Session,
            ShellCoverageKind.PersistentGlobal => ShellPolicyCoverageSource.PersistentGlobal,
            ShellCoverageKind.PersistentFolder => ShellPolicyCoverageSource.PersistentFolder,
            _ => throw new InvalidOperationException("Invalid actor coverage kind."),
        };
}
