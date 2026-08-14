// -----------------------------------------------------------------------
// <copyright file="ShellPolicyEvaluation.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Security;

namespace Netclaw.Actors.Tools;

internal enum ShellPolicyFault
{
    InvalidCandidateId = 0,
    CandidateFactsChanged = 1,
    InvalidCoverage = 2,
    CoverageAlreadyAssigned = 3,
    InvalidTerminalTransition = 4,
    InvalidStageResult = 5,
    StageException = 6,
    InvalidProjection = 7,
    InvalidActorEvidence = 8,
}

internal abstract record ShellPolicyPreflightResult
{
    private ShellPolicyPreflightResult()
    {
    }

    internal sealed record Complete : ShellPolicyPreflightResult
    {
        internal Complete(
            ToolAccessDecision decision,
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

        internal ToolAccessDecision Decision { get; }

        internal ShellCommandAnalysis? AuthorizedAnalysis { get; }
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

internal enum ShellPolicyStageOutcome
{
    Invalid = 0,
    Continue = 1,
    Complete = 2,
}

internal sealed record ShellPolicyAuthorization
{
    internal ShellPolicyAuthorization(
        ToolAuthorizationDecision decision,
        ShellCommandAnalysis? authorizedAnalysis)
    {
        ArgumentNullException.ThrowIfNull(decision);
        if (authorizedAnalysis is not null
            && decision.Outcome != ToolAuthorizationOutcome.Allowed)
        {
            throw new ArgumentException(
                "Only an allowed shell decision can carry analysis.",
                nameof(authorizedAnalysis));
        }

        Decision = decision;
        AuthorizedAnalysis = authorizedAnalysis;
    }

    internal ToolAuthorizationDecision Decision { get; }

    internal ShellCommandAnalysis? AuthorizedAnalysis { get; }
}

internal sealed class ShellPolicyEvaluation
{
    private readonly ShellPolicyCoverageSource[] _coverage;
    private readonly ShellPolicyDecisionTraceBuilder _trace = new();
    private ToolAuthorizationDecision? _terminalDecision;
    private ShellPolicyDecisionTrace? _completedTrace;
    private ShellPolicyFault? _terminalFault;
    private ValidatedShellGrantEvidence? _grantEvidence;
    private (string? SessionDirectory, ToolApprovalContext Context)? _uncoveredApprovalContext;

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

    internal ToolApprovalContext GetUncoveredApprovalContext(string? sessionDirectory)
    {
        var uncovered = UncoveredCandidates;
        if (uncovered.Count == 0)
            throw new InvalidOperationException("No uncovered shell candidates remain.");

        if (_uncoveredApprovalContext is { } cached
            && string.Equals(cached.SessionDirectory, sessionDirectory, StringComparison.Ordinal))
        {
            return cached.Context;
        }

        var context = Projection.HasCausalIntent
            ? Projection.ApprovalContext
            : ToolAccessPolicy.NarrowShellApprovalContext(
                Projection.ApprovalContext,
                uncovered.Select(static candidate => candidate.Candidate).ToArray(),
                sessionDirectory,
                Projection.Environment.PathStyle);
        _uncoveredApprovalContext = (sessionDirectory, context);
        return context;
    }

    internal ToolAuthorizationDecision? TerminalDecision => _terminalDecision;

    internal ShellPolicyDecisionTrace? CompletedTrace => _completedTrace;

    internal ShellPolicyFault? TerminalFault => _terminalFault;

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

    internal ShellPolicyStageOutcome ApplyActorEvidence(ValidatedShellGrantEvidence evidence)
    {
        ArgumentNullException.ThrowIfNull(evidence);
        if (_terminalDecision is not null)
            return ShellPolicyStageOutcome.Complete;

        if (_grantEvidence is not null
            || !ReferenceEquals(evidence.SourceCandidates, Projection.GrantCandidates))
            return Fail(ShellPolicyFault.InvalidActorEvidence);

        _grantEvidence = evidence;
        foreach (var candidateEvidence in evidence.CandidateEvidence)
        {
            var actorEvidence = candidateEvidence.ActorEvidence;
            if (actorEvidence.GrantCoverage is { } grantCoverage)
            {
                var result = Cover(
                    candidateEvidence.Candidate,
                    ToCoverageSource(grantCoverage),
                    actorEvidence.GrantCreatedAt);
                if (result != ShellPolicyStageOutcome.Continue)
                    return result;
            }
            else
            {
                _trace.AddActorEvidence(candidateEvidence.Candidate, actorEvidence);
            }
        }

        return ShellPolicyStageOutcome.Continue;
    }

    internal ShellPolicyStageOutcome Cover(
        ShellPolicyCandidate candidate,
        ShellPolicyCoverageSource source,
        DateTimeOffset? grantTimestamp = null)
    {
        ArgumentNullException.ThrowIfNull(candidate);

        if (_terminalDecision is not null)
            return ShellPolicyStageOutcome.Complete;

        var index = candidate.Id.Value;
        if ((uint)index >= (uint)Candidates.Count)
            return Fail(ShellPolicyFault.InvalidCandidateId);

        if (!ReferenceEquals(candidate, Projection.Candidates[index]))
            return Fail(ShellPolicyFault.CandidateFactsChanged);

        if (_coverage[index] != ShellPolicyCoverageSource.Uncovered)
            return Fail(ShellPolicyFault.CoverageAlreadyAssigned);

        if (!Enum.IsDefined(source)
            || source == ShellPolicyCoverageSource.Uncovered
            || grantTimestamp is not null
            && source is not
                (ShellPolicyCoverageSource.PersistentGlobal
                or ShellPolicyCoverageSource.PersistentFolder))
        {
            return Fail(ShellPolicyFault.InvalidCoverage);
        }

        _trace.AddCoverage(source, candidate, grantTimestamp);
        _coverage[index] = source;
        _uncoveredApprovalContext = null;
        return ShellPolicyStageOutcome.Continue;
    }

    internal ShellPolicyStageOutcome Complete(
        ToolAuthorizationDecision decision,
        bool allowsUncoveredOneTime = false)
    {
        ArgumentNullException.ThrowIfNull(decision);

        if (_terminalDecision is not null)
            return ShellPolicyStageOutcome.Complete;

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
            return Fail(ShellPolicyFault.InvalidTerminalTransition);

        _completedTrace = _trace.Complete(decision);
        _terminalDecision = decision;
        return ShellPolicyStageOutcome.Complete;
    }

    internal bool ApplyStageOutcome(ShellPolicyStageOutcome outcome)
    {
        switch (outcome)
        {
            case ShellPolicyStageOutcome.Continue when _terminalDecision is null:
                return true;
            case ShellPolicyStageOutcome.Complete when _terminalDecision is not null:
                return false;
            default:
                InvalidateStage(ShellPolicyFault.InvalidStageResult);
                return false;
        }
    }

    internal ShellPolicyStageOutcome Fault(ShellPolicyFault reason)
    {
        if (!Enum.IsDefined(reason))
            reason = ShellPolicyFault.InvalidStageResult;

        if (_terminalFault is not null)
            return ShellPolicyStageOutcome.Complete;

        if (_terminalDecision is not null)
            return ShellPolicyStageOutcome.Complete;

        return Fail(reason);
    }

    internal ShellPolicyStageOutcome InvalidateStage(ShellPolicyFault reason) =>
        Fail(
            Enum.IsDefined(reason) ? reason : ShellPolicyFault.InvalidStageResult,
            replaceCompletion: true);

    private ShellPolicyStageOutcome Fail(
        ShellPolicyFault reason,
        bool replaceCompletion = false)
    {
        var decision = ToolAuthorizationDecision.Deny("internal_policy_failure");
        _completedTrace = replaceCompletion
            ? _trace.ReplaceCompletion(decision)
            : _trace.Complete(decision);
        _terminalDecision = decision;
        _terminalFault = reason;
        return ShellPolicyStageOutcome.Complete;
    }

    private static ShellPolicyCoverageSource ToCoverageSource(ShellCoverageKind coverage)
        => coverage switch
        {
            ShellCoverageKind.Session => ShellPolicyCoverageSource.Session,
            ShellCoverageKind.PersistentGlobal => ShellPolicyCoverageSource.PersistentGlobal,
            ShellCoverageKind.PersistentFolder => ShellPolicyCoverageSource.PersistentFolder,
            _ => throw new InvalidOperationException("Invalid actor coverage kind."),
        };
}
