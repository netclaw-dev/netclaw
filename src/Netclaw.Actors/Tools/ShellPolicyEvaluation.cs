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

internal delegate ValueTask<ShellPolicyStageResult> ShellPolicyStage(
    ShellPolicyEvaluation evaluation,
    CancellationToken cancellationToken);

internal static class ShellPolicyPipeline
{
    internal static async ValueTask<ShellPolicyStageResult> RunAsync(
        ShellPolicyEvaluation evaluation,
        IReadOnlyList<ShellPolicyStage> stages,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(evaluation);
        ArgumentNullException.ThrowIfNull(stages);

        cancellationToken.ThrowIfCancellationRequested();
        if (evaluation.TerminalFault is { } terminalFault)
            return new ShellPolicyStageResult.Fault(terminalFault);

        if (evaluation.TerminalDecision is { } terminalDecision)
            return new ShellPolicyStageResult.Complete(terminalDecision);

        foreach (var stage in stages)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (stage is null)
                return evaluation.InvalidateStage(ShellPolicyFault.InvalidStageResult);

            ShellPolicyStageResult? result;
            try
            {
                result = await stage(evaluation, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception)
            {
                cancellationToken.ThrowIfCancellationRequested();
                return evaluation.InvalidateStage(ShellPolicyFault.StageException);
            }

            cancellationToken.ThrowIfCancellationRequested();
            switch (result)
            {
                case ShellPolicyStageResult.Continue
                    when evaluation.TerminalDecision is null:
                    continue;
                case ShellPolicyStageResult.Complete complete
                    when evaluation.TerminalDecision is null:
                    return evaluation.Complete(
                        complete.Decision,
                        complete.AllowsUncoveredOneTime);
                case ShellPolicyStageResult.Complete complete
                    when ReferenceEquals(evaluation.TerminalDecision, complete.Decision):
                    return complete;
                case ShellPolicyStageResult.Fault fault
                    when evaluation.TerminalFault == fault.Reason:
                    return fault;
                case ShellPolicyStageResult.Fault fault
                    when evaluation.TerminalDecision is null:
                    return evaluation.Fault(fault.Reason);
                default:
                    return evaluation.InvalidateStage(ShellPolicyFault.InvalidStageResult);
            }
        }

        return new ShellPolicyStageResult.Continue();
    }
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
    private readonly ShellCandidateCoverage[] _coverage;
    private readonly ShellPolicyDecisionTraceBuilder _trace = new();
    private ToolAuthorizationDecision? _terminalDecision;
    private ShellPolicyDecisionTrace? _completedTrace;
    private ShellPolicyFault? _terminalFault;
    private ValidatedShellGrantEvidence? _grantEvidence;

    internal ShellPolicyEvaluation(ShellPolicyProjection projection)
    {
        ArgumentNullException.ThrowIfNull(projection);

        Projection = projection;
        _candidates = projection.Candidates.ToArray();
        _candidateView = Array.AsReadOnly(_candidates);
        _coverage = new ShellCandidateCoverage[_candidates.Length];
        for (var index = 0; index < _candidates.Length; index++)
        {
            var candidate = _candidates[index];
            if (candidate.Id.Value != index)
                throw new InvalidOperationException("Shell candidate IDs must match projection order.");

            _coverage[index] = new ShellCandidateCoverage(
                candidate.Id,
                ShellCoverageKind.Uncovered,
                ShellPolicyReason.None);
        }
    }

    internal ShellPolicyProjection Projection { get; }

    internal IReadOnlyList<ShellPolicyCandidate> Candidates => _candidateView;

    internal IReadOnlyList<ShellPolicyCandidateId> UncoveredIds => Array.AsReadOnly(
        _coverage
            .Where(static item => item.Kind == ShellCoverageKind.Uncovered)
            .Select(static item => item.CandidateId)
            .ToArray());

    internal bool AllCovered => _coverage.All(static item =>
        item.Kind is not ShellCoverageKind.Uncovered and not ShellCoverageKind.Denied);

    internal IReadOnlyList<ShellPolicyCandidate> UncoveredCandidates
    {
        get
        {
            var uncovered = new List<ShellPolicyCandidate>();
            for (var index = 0; index < _coverage.Length; index++)
            {
                if (_coverage[index].Kind == ShellCoverageKind.Uncovered)
                    uncovered.Add(_candidates[index]);
            }

            return Array.AsReadOnly(uncovered.ToArray());
        }
    }

    internal ValidatedShellGrantEvidence? GrantEvidence => _grantEvidence;

    internal IReadOnlyList<ToolApprovalMatch> ApprovalMatches =>
        _grantEvidence?.ApprovalMatches ?? [];

    internal bool HasOneTimeCoverage => _coverage.Any(static item =>
        item.Kind == ShellCoverageKind.OneTime);

    internal ToolAuthorizationDecision? TerminalDecision => _terminalDecision;

    internal ShellPolicyDecisionTrace? CompletedTrace => _completedTrace;

    internal ShellPolicyFault? TerminalFault => _terminalFault;

    internal ShellCandidateCoverage CoverageFor(ShellPolicyCandidateId candidateId)
    {
        var index = candidateId.Value;
        if ((uint)index >= (uint)_coverage.Length)
            throw new ArgumentOutOfRangeException(nameof(candidateId));

        return _coverage[index];
    }

    internal bool IsCovered(ShellPolicyCandidateId candidateId)
    {
        var coverage = CoverageFor(candidateId);
        return coverage.Kind is not ShellCoverageKind.Uncovered and not ShellCoverageKind.Denied;
    }

    internal ShellPolicyStageResult ApplyActorEvidence(ValidatedShellGrantEvidence evidence)
    {
        ArgumentNullException.ThrowIfNull(evidence);
        if (_terminalDecision is not null)
            return new ShellPolicyStageResult.Complete(_terminalDecision);

        if (_grantEvidence is not null || !CanApplyActorEvidence(evidence))
            return Fail(ShellPolicyFault.InvalidActorEvidence);

        _grantEvidence = evidence;
        foreach (var candidateEvidence in evidence.CandidateEvidence)
        {
            var actorEvidence = candidateEvidence.ActorEvidence;
            if (actorEvidence.GrantCoverage is { } grantCoverage)
            {
                GetActorCoverageFacts(
                    grantCoverage,
                    out var reason,
                    out var scopeRelation);
                var result = Cover(
                    candidateEvidence.Candidate,
                    grantCoverage,
                    reason,
                    scopeRelation,
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
        ShellCoverageKind coverage,
        ShellPolicyReason reason,
        ShellScopeRelation scopeRelation,
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

        if (_coverage[index].Kind != ShellCoverageKind.Uncovered)
            return Fail(ShellPolicyFault.CoverageAlreadyAssigned);

        if (!TryGetTraceStage(
                coverage,
                reason,
                scopeRelation,
                grantTimestamp,
                out var traceStage))
        {
            return Fail(ShellPolicyFault.InvalidCoverage);
        }

        _trace.AddCoverage(
            traceStage,
            candidate,
            coverage,
            reason,
            scopeRelation,
            grantTimestamp);
        _coverage[index] = new ShellCandidateCoverage(candidate.Id, coverage, reason);
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

    internal ShellPolicyStageResult.Fault InvalidateStage(ShellPolicyFault reason)
    {
        if (!Enum.IsDefined(reason))
            reason = ShellPolicyFault.InvalidStageResult;

        var decision = ToolAuthorizationDecision.Deny("internal_policy_failure");
        _completedTrace = _trace.ReplaceCompletion(decision);
        _terminalDecision = decision;
        _terminalFault = reason;
        return new ShellPolicyStageResult.Fault(reason);
    }

    private ShellPolicyStageResult.Fault Fail(ShellPolicyFault reason)
    {
        var decision = ToolAuthorizationDecision.Deny("internal_policy_failure");
        _completedTrace = _trace.Complete(decision);
        _terminalDecision = decision;
        _terminalFault = reason;
        return new ShellPolicyStageResult.Fault(reason);
    }

    private static bool TryGetTraceStage(
        ShellCoverageKind coverage,
        ShellPolicyReason reason,
        ShellScopeRelation scopeRelation,
        DateTimeOffset? grantTimestamp,
        out ShellPolicyTraceStage traceStage)
    {
        traceStage = coverage switch
        {
            ShellCoverageKind.OneTime => ShellPolicyTraceStage.OneTimeApproval,
            ShellCoverageKind.Session or
                ShellCoverageKind.PersistentGlobal or
                ShellCoverageKind.PersistentFolder => ShellPolicyTraceStage.StoredGrantMatch,
            ShellCoverageKind.ReviewedSafePolicy => ShellPolicyTraceStage.ReviewedSafePolicy,
            _ => default,
        };

        return (coverage, reason, scopeRelation, grantTimestamp) switch
        {
            (ShellCoverageKind.OneTime, ShellPolicyReason.OneTimeGrant, ShellScopeRelation.None, null) => true,
            (ShellCoverageKind.Session, ShellPolicyReason.SessionGrant, ShellScopeRelation.ThisChat, null) => true,
            (ShellCoverageKind.PersistentGlobal, ShellPolicyReason.PersistentGlobalGrant, ShellScopeRelation.Global, _) => true,
            (ShellCoverageKind.PersistentFolder, ShellPolicyReason.PersistentFolderGrant, ShellScopeRelation.UnderGrantRoot, _) => true,
            (ShellCoverageKind.ReviewedSafePolicy, ShellPolicyReason.ReviewedSafePhrase,
                ShellScopeRelation.UnderRealRoot or ShellScopeRelation.UnderIntentRoot, null) => true,
            (ShellCoverageKind.ReviewedSafePolicy, ShellPolicyReason.ApprovalExemptSideEffect,
                ShellScopeRelation.None, null) => true,
            _ => false,
        };
    }

    private bool CanApplyActorEvidence(ValidatedShellGrantEvidence evidence)
    {
        var grantCandidates = Projection.GrantCandidates;
        if (evidence.CandidateEvidence.Count != grantCandidates.Count)
            return false;

        var expectedIds = grantCandidates
            .Select(static candidate => candidate.Id)
            .ToHashSet();
        foreach (var candidateEvidence in evidence.CandidateEvidence)
        {
            var candidate = candidateEvidence.Candidate;
            var index = candidate.Id.Value;
            if ((uint)index >= (uint)_candidates.Length
                || !ReferenceEquals(candidate, _candidates[index])
                || !expectedIds.Remove(candidate.Id)
                || candidateEvidence.ActorEvidence.CandidateId != candidate.Id)
            {
                return false;
            }

            if (candidateEvidence.ActorEvidence.GrantCoverage is { } coverage
                && (_coverage[index].Kind != ShellCoverageKind.Uncovered
                    || !IsActorCoverage(coverage)))
            {
                return false;
            }
        }

        return expectedIds.Count == 0;
    }

    private static bool IsActorCoverage(ShellCoverageKind coverage)
        => coverage is ShellCoverageKind.Session
            or ShellCoverageKind.PersistentGlobal
            or ShellCoverageKind.PersistentFolder;

    private static void GetActorCoverageFacts(
        ShellCoverageKind coverage,
        out ShellPolicyReason reason,
        out ShellScopeRelation scopeRelation)
    {
        (reason, scopeRelation) = coverage switch
        {
            ShellCoverageKind.Session => (
                ShellPolicyReason.SessionGrant,
                ShellScopeRelation.ThisChat),
            ShellCoverageKind.PersistentGlobal => (
                ShellPolicyReason.PersistentGlobalGrant,
                ShellScopeRelation.Global),
            ShellCoverageKind.PersistentFolder => (
                ShellPolicyReason.PersistentFolderGrant,
                ShellScopeRelation.UnderGrantRoot),
            _ => throw new InvalidOperationException("Invalid actor coverage kind."),
        };
    }
}
