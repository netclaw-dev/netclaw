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

internal abstract record ShellPolicyStageResult
{
    private ShellPolicyStageResult()
    {
    }

    internal sealed record Continue : ShellPolicyStageResult;

    internal sealed record Complete : ShellPolicyStageResult
    {
        internal Complete(ToolAuthorizationDecision decision)
            : this(decision, allowsUncoveredOneTime: false)
        {
        }

        private Complete(
            ToolAuthorizationDecision decision,
            bool allowsUncoveredOneTime)
        {
            ArgumentNullException.ThrowIfNull(decision);
            Decision = decision;
            AllowsUncoveredOneTime = allowsUncoveredOneTime;
        }

        internal ToolAuthorizationDecision Decision { get; }

        internal bool AllowsUncoveredOneTime { get; }

        internal static Complete ExactOneTime(ToolAuthorizationDecision decision)
        {
            ArgumentNullException.ThrowIfNull(decision);
            if (decision.Outcome != ToolAuthorizationOutcome.Allowed
                || decision.AllowReason != ToolAllowReason.OneTimeApproval)
            {
                throw new ArgumentException(
                    "An uncovered completion requires an exact one-time allow.",
                    nameof(decision));
            }

            return new Complete(decision, allowsUncoveredOneTime: true);
        }
    }

    internal sealed record Fault(ShellPolicyFault Reason)
        : ShellPolicyStageResult;
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
    private readonly ShellPolicyCandidate[] _candidates;
    private readonly IReadOnlyList<ShellPolicyCandidate> _candidateView;
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
        _candidates = projection.Candidates.ToArray();
        _candidateView = Array.AsReadOnly(_candidates);
        _coverage = new ShellPolicyCoverageSource[_candidates.Length];
    }

    internal ShellPolicyProjection Projection { get; }

    internal IReadOnlyList<ShellPolicyCandidate> Candidates => _candidateView;

    internal bool AllCovered => !_coverage.Contains(ShellPolicyCoverageSource.Uncovered);

    internal IReadOnlyList<ShellPolicyCandidate> UncoveredCandidates =>
        Array.AsReadOnly(_candidates
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

    internal ShellPolicyStageResult ApplyActorEvidence(ValidatedShellGrantEvidence evidence)
    {
        ArgumentNullException.ThrowIfNull(evidence);
        if (_terminalDecision is not null)
            return new ShellPolicyStageResult.Complete(_terminalDecision);

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
                if (result is not ShellPolicyStageResult.Continue)
                    return result;
            }
            else
            {
                _trace.AddActorEvidence(candidateEvidence.Candidate, actorEvidence);
            }
        }

        return new ShellPolicyStageResult.Continue();
    }

    internal ShellPolicyStageResult Cover(
        ShellPolicyCandidate candidate,
        ShellPolicyCoverageSource source,
        DateTimeOffset? grantTimestamp = null)
    {
        ArgumentNullException.ThrowIfNull(candidate);

        if (_terminalDecision is not null)
            return new ShellPolicyStageResult.Complete(_terminalDecision);

        var index = candidate.Id.Value;
        if ((uint)index >= (uint)_candidates.Length)
            return Fail(ShellPolicyFault.InvalidCandidateId);

        if (!ReferenceEquals(candidate, _candidates[index]))
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
        return new ShellPolicyStageResult.Continue();
    }

    internal ShellPolicyStageResult Complete(
        ToolAuthorizationDecision decision,
        bool allowsUncoveredOneTime = false)
    {
        ArgumentNullException.ThrowIfNull(decision);

        if (_terminalDecision is not null)
            return new ShellPolicyStageResult.Complete(_terminalDecision);

        var mayComplete = decision.Outcome switch
        {
            ToolAuthorizationOutcome.Allowed => AllCovered
                                                || allowsUncoveredOneTime
                                                && decision.AllowReason == ToolAllowReason.OneTimeApproval,
            ToolAuthorizationOutcome.RequiresApproval => _candidates.Length == 0 || !AllCovered,
            ToolAuthorizationOutcome.Denied => true,
            _ => false,
        };
        if (!mayComplete)
            return Fail(ShellPolicyFault.InvalidTerminalTransition);

        _completedTrace = _trace.Complete(decision);
        _terminalDecision = decision;
        return new ShellPolicyStageResult.Complete(decision);
    }

    internal bool ApplyStageResult(ShellPolicyStageResult? result)
    {
        switch (result)
        {
            case ShellPolicyStageResult.Continue when _terminalDecision is null:
                return true;
            case ShellPolicyStageResult.Complete complete when _terminalDecision is null:
                Complete(complete.Decision, complete.AllowsUncoveredOneTime);
                return false;
            case ShellPolicyStageResult.Complete complete
                when ReferenceEquals(_terminalDecision, complete.Decision):
                return false;
            case ShellPolicyStageResult.Fault fault when _terminalFault == fault.Reason:
                return false;
            case ShellPolicyStageResult.Fault fault when _terminalDecision is null:
                Fault(fault.Reason);
                return false;
            default:
                InvalidateStage(ShellPolicyFault.InvalidStageResult);
                return false;
        }
    }

    internal ShellPolicyStageResult Fault(ShellPolicyFault reason)
    {
        if (!Enum.IsDefined(reason))
            reason = ShellPolicyFault.InvalidStageResult;

        if (_terminalFault is { } terminalFault)
            return new ShellPolicyStageResult.Fault(terminalFault);

        if (_terminalDecision is not null)
            return new ShellPolicyStageResult.Complete(_terminalDecision);

        return Fail(reason);
    }

    internal ShellPolicyStageResult.Fault InvalidateStage(ShellPolicyFault reason) =>
        Fail(
            Enum.IsDefined(reason) ? reason : ShellPolicyFault.InvalidStageResult,
            replaceCompletion: true);

    private ShellPolicyStageResult.Fault Fail(
        ShellPolicyFault reason,
        bool replaceCompletion = false)
    {
        var decision = ToolAuthorizationDecision.Deny("internal_policy_failure");
        _completedTrace = replaceCompletion
            ? _trace.ReplaceCompletion(decision)
            : _trace.Complete(decision);
        _terminalDecision = decision;
        _terminalFault = reason;
        return new ShellPolicyStageResult.Fault(reason);
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
