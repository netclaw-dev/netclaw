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
        {
            ArgumentNullException.ThrowIfNull(decision);
            Decision = decision;
        }

        internal ToolAuthorizationDecision Decision { get; }
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
    private readonly ShellCandidateCoverage[] _coverage;
    private readonly ShellPolicyDecisionTraceBuilder _trace = new();
    private ToolAuthorizationDecision? _terminalDecision;
    private ShellPolicyDecisionTrace? _completedTrace;
    private ShellPolicyFault? _terminalFault;

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

    internal ShellPolicyStageResult Complete(ToolAuthorizationDecision decision)
    {
        ArgumentNullException.ThrowIfNull(decision);

        if (_terminalDecision is not null)
            return new ShellPolicyStageResult.Complete(_terminalDecision);

        var mayComplete = decision.Outcome switch
        {
            ToolAuthorizationOutcome.Allowed => AllCovered,
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
}
