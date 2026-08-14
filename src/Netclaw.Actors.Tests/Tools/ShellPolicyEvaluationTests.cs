// -----------------------------------------------------------------------
// <copyright file="ShellPolicyEvaluationTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Actors.Tools;
using Netclaw.Configuration;
using Netclaw.Security;
using Netclaw.Tools;
using Xunit;

namespace Netclaw.Actors.Tests.Tools;

public sealed class ShellPolicyEvaluationTests
{
    [Fact]
    public void Coverage_and_trace_change_together()
    {
        var evaluation = CreateEvaluation(BashCandidate("git status"));
        var candidate = Assert.Single(evaluation.Candidates);
        var grantTimestamp = new DateTimeOffset(2026, 8, 14, 0, 0, 0, TimeSpan.Zero);

        var result = evaluation.Cover(
            candidate,
            ShellCoverageKind.PersistentGlobal,
            ShellPolicyReason.PersistentGlobalGrant,
            ShellScopeRelation.Global,
            grantTimestamp);

        Assert.IsType<ShellPolicyStageResult.Continue>(result);
        Assert.Equal(ShellCoverageKind.PersistentGlobal, evaluation.CoverageFor(candidate.Id).Kind);

        var decision = ToolAuthorizationDecision.Allow(ToolAllowReason.StoredApproval);
        Assert.IsType<ShellPolicyStageResult.Complete>(evaluation.Complete(decision));
        Assert.NotNull(evaluation.CompletedTrace);
        var rows = evaluation.CompletedTrace.Rows;
        Assert.Collection(
            rows,
            row =>
            {
                Assert.Equal(ShellPolicyTraceStage.StoredGrantMatch, row.Stage);
                Assert.Equal(ShellCoverageKind.PersistentGlobal, row.Coverage);
                Assert.Equal(ShellPolicyTraceReason.PersistentGlobalGrant, row.Reason);
                Assert.Equal(ShellScopeRelation.Global, row.ScopeRelation);
                Assert.Equal(grantTimestamp, row.GrantTimestamp);
            },
            row =>
            {
                Assert.Equal(ShellPolicyTraceStage.Completion, row.Stage);
                Assert.Equal(ShellPolicyTraceOutcome.Allow, row.Outcome);
            });
    }

    [Fact]
    public void Candidate_view_cannot_replace_projection_identity()
    {
        var evaluation = CreateEvaluation(BashCandidate("git status"));

        Assert.IsNotType<ShellPolicyCandidate[]>(evaluation.Candidates);
        var list = Assert.IsAssignableFrom<IList<ShellPolicyCandidate>>(evaluation.Candidates);
        Assert.Throws<NotSupportedException>(() =>
            list[0] = list[0] with { Candidate = BashCandidate("git push") });
    }

    [Fact]
    public void Duplicate_coverage_fails_without_a_second_trace_row()
    {
        var evaluation = CreateEvaluation(BashCandidate("git status"));
        var candidate = Assert.Single(evaluation.Candidates);
        Assert.IsType<ShellPolicyStageResult.Continue>(evaluation.Cover(
            candidate,
            ShellCoverageKind.Session,
            ShellPolicyReason.SessionGrant,
            ShellScopeRelation.ThisChat));

        var duplicate = Assert.IsType<ShellPolicyStageResult.Fault>(evaluation.Cover(
            candidate,
            ShellCoverageKind.PersistentGlobal,
            ShellPolicyReason.PersistentGlobalGrant,
            ShellScopeRelation.Global));

        Assert.Equal(ShellPolicyFault.CoverageAlreadyAssigned, duplicate.Reason);
        Assert.Equal(ShellCoverageKind.Session, evaluation.CoverageFor(candidate.Id).Kind);
        Assert.Equal(ToolAuthorizationOutcome.Denied, evaluation.TerminalDecision?.Outcome);
        Assert.Equal(ShellPolicyFault.CoverageAlreadyAssigned, evaluation.TerminalFault);
        Assert.NotNull(evaluation.CompletedTrace);
        Assert.Equal(2, evaluation.CompletedTrace.Rows.Count);
        Assert.Equal(ShellPolicyTraceOutcome.Deny, evaluation.CompletedTrace.Rows[^1].Outcome);
    }

    [Fact]
    public void Changed_candidate_facts_fail_closed()
    {
        var evaluation = CreateEvaluation(BashCandidate("git status"));
        var candidate = Assert.Single(evaluation.Candidates);
        var changed = candidate with
        {
            Candidate = BashCandidate("git push")
        };

        var result = Assert.IsType<ShellPolicyStageResult.Fault>(evaluation.Cover(
            changed,
            ShellCoverageKind.Session,
            ShellPolicyReason.SessionGrant,
            ShellScopeRelation.ThisChat));

        Assert.Equal(ShellPolicyFault.CandidateFactsChanged, result.Reason);
        Assert.Equal(ShellCoverageKind.Uncovered, evaluation.CoverageFor(candidate.Id).Kind);
        Assert.Equal(ToolAuthorizationOutcome.Denied, evaluation.TerminalDecision?.Outcome);
        Assert.Equal(ShellPolicyFault.CandidateFactsChanged, evaluation.TerminalFault);
        Assert.NotNull(evaluation.CompletedTrace);
        Assert.Single(evaluation.CompletedTrace.Rows);

        var later = Assert.IsType<ShellPolicyStageResult.Complete>(evaluation.Cover(
            candidate,
            ShellCoverageKind.Session,
            ShellPolicyReason.SessionGrant,
            ShellScopeRelation.ThisChat));
        Assert.Same(evaluation.TerminalDecision, later.Decision);
        Assert.Equal(ShellCoverageKind.Uncovered, evaluation.CoverageFor(candidate.Id).Kind);
    }

    [Fact]
    public void Invalid_candidate_id_fails_closed()
    {
        var evaluation = CreateEvaluation(BashCandidate("git status"));
        var changed = Assert.Single(evaluation.Candidates) with
        {
            Id = new ShellPolicyCandidateId(7)
        };

        var result = Assert.IsType<ShellPolicyStageResult.Fault>(evaluation.Cover(
            changed,
            ShellCoverageKind.Session,
            ShellPolicyReason.SessionGrant,
            ShellScopeRelation.ThisChat));

        Assert.Equal(ShellPolicyFault.InvalidCandidateId, result.Reason);
        Assert.Single(evaluation.UncoveredIds);
        Assert.Equal(ToolAuthorizationOutcome.Denied, evaluation.TerminalDecision?.Outcome);
    }

    [Theory]
    [InlineData(nameof(ShellCoverageKind.Uncovered), nameof(ShellPolicyReason.None), nameof(ShellScopeRelation.None))]
    [InlineData(nameof(ShellCoverageKind.Denied), nameof(ShellPolicyReason.None), nameof(ShellScopeRelation.None))]
    [InlineData(nameof(ShellCoverageKind.Session), nameof(ShellPolicyReason.PersistentGlobalGrant), nameof(ShellScopeRelation.ThisChat))]
    [InlineData(nameof(ShellCoverageKind.Session), nameof(ShellPolicyReason.SessionGrant), nameof(ShellScopeRelation.Global))]
    public void Invalid_coverage_transition_fails_closed(
        string coverageName,
        string reasonName,
        string scopeRelationName)
    {
        var evaluation = CreateEvaluation(BashCandidate("git status"));
        var candidate = Assert.Single(evaluation.Candidates);
        var coverage = Enum.Parse<ShellCoverageKind>(coverageName);
        var reason = Enum.Parse<ShellPolicyReason>(reasonName);
        var scopeRelation = Enum.Parse<ShellScopeRelation>(scopeRelationName);

        var result = Assert.IsType<ShellPolicyStageResult.Fault>(evaluation.Cover(
            candidate,
            coverage,
            reason,
            scopeRelation));

        Assert.Equal(ShellPolicyFault.InvalidCoverage, result.Reason);
        Assert.Equal(ShellCoverageKind.Uncovered, evaluation.CoverageFor(candidate.Id).Kind);
        Assert.Equal(ToolAuthorizationOutcome.Denied, evaluation.TerminalDecision?.Outcome);
    }

    [Fact]
    public void Invalid_coverage_enum_fails_closed()
    {
        var evaluation = CreateEvaluation(BashCandidate("git status"));
        var candidate = Assert.Single(evaluation.Candidates);

        var result = Assert.IsType<ShellPolicyStageResult.Fault>(evaluation.Cover(
            candidate,
            (ShellCoverageKind)999,
            ShellPolicyReason.SessionGrant,
            ShellScopeRelation.ThisChat));

        Assert.Equal(ShellPolicyFault.InvalidCoverage, result.Reason);
        Assert.Equal(ShellCoverageKind.Uncovered, evaluation.CoverageFor(candidate.Id).Kind);
        Assert.Equal(ToolAuthorizationOutcome.Denied, evaluation.TerminalDecision?.Outcome);
        Assert.NotNull(evaluation.CompletedTrace);
        Assert.Single(evaluation.CompletedTrace.Rows);
    }

    [Fact]
    public void Session_coverage_rejects_a_persistent_timestamp()
    {
        var evaluation = CreateEvaluation(BashCandidate("git status"));
        var candidate = Assert.Single(evaluation.Candidates);

        var result = Assert.IsType<ShellPolicyStageResult.Fault>(evaluation.Cover(
            candidate,
            ShellCoverageKind.Session,
            ShellPolicyReason.SessionGrant,
            ShellScopeRelation.ThisChat,
            new DateTimeOffset(2026, 8, 14, 0, 0, 0, TimeSpan.Zero)));

        Assert.Equal(ShellPolicyFault.InvalidCoverage, result.Reason);
        Assert.Equal(ShellCoverageKind.Uncovered, evaluation.CoverageFor(candidate.Id).Kind);
        Assert.Equal(ToolAuthorizationOutcome.Denied, evaluation.TerminalDecision?.Outcome);
        Assert.NotNull(evaluation.CompletedTrace);
        Assert.Single(evaluation.CompletedTrace.Rows);
        Assert.Equal(ShellPolicyTraceStage.Completion, evaluation.CompletedTrace.Rows[0].Stage);
    }

    [Fact]
    public void Allow_requires_every_candidate_to_have_coverage()
    {
        var evaluation = CreateEvaluation(
            BashCandidate("git status"),
            BashCandidate("git diff"));
        Assert.IsType<ShellPolicyStageResult.Continue>(evaluation.Cover(
            evaluation.Candidates[0],
            ShellCoverageKind.Session,
            ShellPolicyReason.SessionGrant,
            ShellScopeRelation.ThisChat));

        var result = Assert.IsType<ShellPolicyStageResult.Fault>(evaluation.Complete(
            ToolAuthorizationDecision.Allow(ToolAllowReason.StoredApproval)));

        Assert.Equal(ShellPolicyFault.InvalidTerminalTransition, result.Reason);
        Assert.Equal(ToolAuthorizationOutcome.Denied, evaluation.TerminalDecision?.Outcome);
        Assert.Equal(ShellPolicyFault.InvalidTerminalTransition, evaluation.TerminalFault);
        Assert.NotNull(evaluation.CompletedTrace);
        Assert.Equal(2, evaluation.CompletedTrace.Rows.Count);
        Assert.Equal(ShellPolicyTraceStage.StoredGrantMatch, evaluation.CompletedTrace.Rows[0].Stage);
        Assert.Equal(ShellPolicyTraceOutcome.Deny, evaluation.CompletedTrace.Rows[1].Outcome);
    }

    [Fact]
    public void Multiple_terminal_results_return_the_first_terminal_result()
    {
        var evaluation = CreateEvaluation(BashCandidate("git status"));
        var prompt = ToolAuthorizationDecision.RequiresApproval(evaluation.Projection.ApprovalContext);
        Assert.IsType<ShellPolicyStageResult.Complete>(evaluation.Complete(prompt));

        var result = Assert.IsType<ShellPolicyStageResult.Complete>(evaluation.Complete(
            ToolAuthorizationDecision.Deny("internal_policy_failure")));

        Assert.Same(prompt, result.Decision);
        Assert.Same(prompt, evaluation.TerminalDecision);
        Assert.NotNull(evaluation.CompletedTrace);
        Assert.Single(evaluation.CompletedTrace.Rows);
    }

    [Fact]
    public void Coverage_cannot_change_after_a_terminal_allow()
    {
        var evaluation = CreateEvaluation(BashCandidate("git status"));
        var candidate = Assert.Single(evaluation.Candidates);
        Assert.IsType<ShellPolicyStageResult.Continue>(evaluation.Cover(
            candidate,
            ShellCoverageKind.Session,
            ShellPolicyReason.SessionGrant,
            ShellScopeRelation.ThisChat));
        var allow = ToolAuthorizationDecision.Allow(ToolAllowReason.StoredApproval);
        Assert.IsType<ShellPolicyStageResult.Complete>(evaluation.Complete(allow));

        var result = Assert.IsType<ShellPolicyStageResult.Complete>(evaluation.Cover(
            candidate,
            ShellCoverageKind.PersistentGlobal,
            ShellPolicyReason.PersistentGlobalGrant,
            ShellScopeRelation.Global));

        Assert.Same(allow, result.Decision);
        Assert.Same(allow, evaluation.TerminalDecision);
        Assert.Equal(ShellCoverageKind.Session, evaluation.CoverageFor(candidate.Id).Kind);
        Assert.NotNull(evaluation.CompletedTrace);
        Assert.Equal(2, evaluation.CompletedTrace.Rows.Count);
        Assert.Equal(ShellPolicyTraceOutcome.Allow, evaluation.CompletedTrace.Rows[^1].Outcome);
    }

    [Fact]
    public void Prompt_can_complete_without_reusable_candidates()
    {
        var evaluation = CreateEvaluation();
        var prompt = ToolAuthorizationDecision.RequiresApproval(evaluation.Projection.ApprovalContext);

        Assert.IsType<ShellPolicyStageResult.Complete>(evaluation.Complete(prompt));
        Assert.Same(prompt, evaluation.TerminalDecision);
        Assert.NotNull(evaluation.CompletedTrace);
        Assert.Equal(ShellPolicyTraceOutcome.RequiresApproval, evaluation.CompletedTrace.Rows[^1].Outcome);
    }

    [Fact]
    public void Analysis_cannot_attach_to_a_prompt_result()
    {
        var projection = CreateEvaluation(BashCandidate("git status")).Projection;
        var analysis = new ShellCommandPolicy(projection.Environment)
            .Analyze("git status", "/work");
        var prompt = ToolAuthorizationDecision.RequiresApproval(projection.ApprovalContext);

        Assert.Throws<ArgumentException>(() => new ShellPolicyAuthorization(prompt, analysis));
        Assert.Throws<ArgumentException>(() => new ShellPolicyPreflightResult.Complete(
            ToolAccessDecision.RequiresApproval(projection.ApprovalContext),
            analysis));
    }

    private static ShellPolicyEvaluation CreateEvaluation(params ApprovalCandidate[] candidates)
    {
        var environment = ShellExecutionEnvironment.CreateBash(ShellPlatform.Linux);
        var approvalContext = new ToolApprovalContext(
            ShellTool.ToolName,
            "shell command",
            candidates.Select(static candidate => candidate.Verb).ToArray(),
            candidates.Select(static candidate => candidate.Verb).ToArray(),
            [],
            Cwd: "/work",
            Candidates: candidates);
        var context = TestToolExecutionContext.CreateBound(
            "signalr/shell-policy-evaluation",
            "/work/session",
            TrustAudience.Personal);
        var created = ShellPolicyProjection.TryCreate(
            environment,
            new ShellApprovalMatcher(environment),
            execution: null,
            approvalContext,
            context,
            static _ => false,
            out var projection);

        Assert.True(created);
        Assert.NotNull(projection);
        return new ShellPolicyEvaluation(projection);
    }

    private static ApprovalCandidate BashCandidate(string verb) => new(verb, "/work")
    {
        Shell = ApprovalShell.Bash,
        VerbTokens = Array.AsReadOnly(verb.Split(' ', StringSplitOptions.RemoveEmptyEntries))
    };
}
