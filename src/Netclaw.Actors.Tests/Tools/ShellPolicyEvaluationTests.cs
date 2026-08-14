// -----------------------------------------------------------------------
// <copyright file="ShellPolicyEvaluationTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Actors.Tools;
using Netclaw.Configuration;
using Netclaw.Security;
using Netclaw.Tests.Utilities;
using Netclaw.Tools;
using Xunit;

namespace Netclaw.Actors.Tests.Tools;

public sealed class ShellPolicyEvaluationTests
{
    public static bool IsPosix => !OperatingSystem.IsWindows();

    [Fact]
    public async Task Pipeline_stops_after_the_first_complete_result()
    {
        var evaluation = CreateEvaluation();
        var prompt = ToolAuthorizationDecision.RequiresApproval(evaluation.Projection.ApprovalContext);
        var visited = new List<int>();
        ShellPolicyStage[] stages =
        [
            (_, _) =>
            {
                visited.Add(1);
                return ValueTask.FromResult<ShellPolicyStageResult>(new ShellPolicyStageResult.Continue());
            },
            (_, _) =>
            {
                visited.Add(2);
                return ValueTask.FromResult<ShellPolicyStageResult>(new ShellPolicyStageResult.Complete(prompt));
            },
            (_, _) =>
            {
                visited.Add(3);
                return ValueTask.FromResult<ShellPolicyStageResult>(new ShellPolicyStageResult.Continue());
            }
        ];

        var result = Assert.IsType<ShellPolicyStageResult.Complete>(
            await ShellPolicyPipeline.RunAsync(evaluation, stages, TestContext.Current.CancellationToken));

        Assert.Same(prompt, result.Decision);
        Assert.Equal([1, 2], visited);
        Assert.Same(prompt, evaluation.TerminalDecision);
        Assert.NotNull(evaluation.CompletedTrace);
        Assert.Single(evaluation.CompletedTrace.Rows);
    }

    [Fact]
    public async Task Pipeline_stops_after_a_stage_fault()
    {
        var evaluation = CreateEvaluation(BashCandidate("git status"));
        var visited = new List<int>();
        ShellPolicyStage[] stages =
        [
            (_, _) =>
            {
                visited.Add(1);
                return ValueTask.FromResult<ShellPolicyStageResult>(
                    new ShellPolicyStageResult.Fault(ShellPolicyFault.InvalidCoverage));
            },
            (_, _) =>
            {
                visited.Add(2);
                return ValueTask.FromResult<ShellPolicyStageResult>(new ShellPolicyStageResult.Continue());
            }
        ];

        var result = Assert.IsType<ShellPolicyStageResult.Fault>(
            await ShellPolicyPipeline.RunAsync(evaluation, stages, TestContext.Current.CancellationToken));

        Assert.Equal(ShellPolicyFault.InvalidCoverage, result.Reason);
        Assert.Equal([1], visited);
        Assert.Equal(ToolAuthorizationOutcome.Denied, evaluation.TerminalDecision?.Outcome);
        Assert.Equal(ShellPolicyFault.InvalidCoverage, evaluation.TerminalFault);
    }

    [Fact]
    public async Task Pipeline_preserves_coverage_trace_when_a_later_stage_throws()
    {
        var evaluation = CreateEvaluation(BashCandidate("git status"));
        var candidate = Assert.Single(evaluation.Candidates);
        ShellPolicyStage[] stages =
        [
            (state, _) => ValueTask.FromResult(state.Cover(
                candidate,
                ShellCoverageKind.Session,
                ShellPolicyReason.SessionGrant,
                ShellScopeRelation.ThisChat)),
            static (_, _) => throw new InvalidOperationException("stage failed")
        ];

        var result = Assert.IsType<ShellPolicyStageResult.Fault>(
            await ShellPolicyPipeline.RunAsync(evaluation, stages, TestContext.Current.CancellationToken));

        Assert.Equal(ShellPolicyFault.StageException, result.Reason);
        Assert.NotNull(evaluation.CompletedTrace);
        Assert.Collection(
            evaluation.CompletedTrace.Rows,
            row => Assert.Equal(ShellPolicyTraceStage.StoredGrantMatch, row.Stage),
            row =>
            {
                Assert.Equal(ShellPolicyTraceStage.Completion, row.Stage);
                Assert.Equal(ShellPolicyTraceOutcome.Deny, row.Outcome);
            });
    }

    [Fact]
    public async Task Pipeline_propagates_caller_cancellation()
    {
        var evaluation = CreateEvaluation();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        ShellPolicyStage[] stages =
        [
            static (_, _) => ValueTask.FromResult<ShellPolicyStageResult>(new ShellPolicyStageResult.Continue())
        ];

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await ShellPolicyPipeline.RunAsync(evaluation, stages, cancellation.Token));

        Assert.Null(evaluation.TerminalDecision);
        Assert.Null(evaluation.CompletedTrace);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Pipeline_propagates_cancellation_before_inspecting_stages(bool hasNullStage)
    {
        var evaluation = CreateEvaluation();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        ShellPolicyStage[] stages = hasNullStage ? [null!] : [];

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await ShellPolicyPipeline.RunAsync(evaluation, stages, cancellation.Token));

        Assert.Null(evaluation.TerminalDecision);
        Assert.Null(evaluation.CompletedTrace);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Pipeline_propagates_cancellation_set_by_a_stage(bool throwsAfterCancellation)
    {
        var evaluation = CreateEvaluation();
        using var cancellation = new CancellationTokenSource();
        var prompt = ToolAuthorizationDecision.RequiresApproval(evaluation.Projection.ApprovalContext);
        ShellPolicyStage[] stages =
        [
            (_, _) =>
            {
                cancellation.Cancel();
                if (throwsAfterCancellation)
                    throw new InvalidOperationException("stage failed after cancellation");

                return ValueTask.FromResult<ShellPolicyStageResult>(
                    new ShellPolicyStageResult.Complete(prompt));
            }
        ];

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await ShellPolicyPipeline.RunAsync(evaluation, stages, cancellation.Token));

        Assert.Null(evaluation.TerminalDecision);
        Assert.Null(evaluation.CompletedTrace);
    }

    [Fact]
    public async Task Pipeline_does_not_invoke_stages_after_a_terminal_decision()
    {
        var evaluation = CreateEvaluation();
        var prompt = ToolAuthorizationDecision.RequiresApproval(evaluation.Projection.ApprovalContext);
        Assert.IsType<ShellPolicyStageResult.Complete>(evaluation.Complete(prompt));
        var stageVisited = false;
        ShellPolicyStage[] stages =
        [
            (_, _) =>
            {
                stageVisited = true;
                return ValueTask.FromResult<ShellPolicyStageResult>(new ShellPolicyStageResult.Continue());
            }
        ];

        var result = Assert.IsType<ShellPolicyStageResult.Complete>(
            await ShellPolicyPipeline.RunAsync(evaluation, stages, TestContext.Current.CancellationToken));

        Assert.Same(prompt, result.Decision);
        Assert.False(stageVisited);
    }

    [Fact]
    public async Task Pipeline_does_not_invoke_stages_after_a_terminal_fault()
    {
        var evaluation = CreateEvaluation();
        Assert.IsType<ShellPolicyStageResult.Fault>(evaluation.Fault(ShellPolicyFault.InvalidCoverage));
        var stageVisited = false;
        ShellPolicyStage[] stages =
        [
            (_, _) =>
            {
                stageVisited = true;
                return ValueTask.FromResult<ShellPolicyStageResult>(new ShellPolicyStageResult.Continue());
            }
        ];

        var result = Assert.IsType<ShellPolicyStageResult.Fault>(
            await ShellPolicyPipeline.RunAsync(evaluation, stages, TestContext.Current.CancellationToken));

        Assert.Equal(ShellPolicyFault.InvalidCoverage, result.Reason);
        Assert.False(stageVisited);
    }

    [Fact]
    public async Task Pipeline_denies_when_a_stage_completes_then_returns_continue()
    {
        var evaluation = CreateEvaluation();
        var prompt = ToolAuthorizationDecision.RequiresApproval(evaluation.Projection.ApprovalContext);
        ShellPolicyStage[] stages =
        [
            (state, _) =>
            {
                Assert.IsType<ShellPolicyStageResult.Complete>(state.Complete(prompt));
                return ValueTask.FromResult<ShellPolicyStageResult>(new ShellPolicyStageResult.Continue());
            }
        ];

        var result = Assert.IsType<ShellPolicyStageResult.Fault>(
            await ShellPolicyPipeline.RunAsync(evaluation, stages, TestContext.Current.CancellationToken));

        Assert.Equal(ShellPolicyFault.InvalidStageResult, result.Reason);
        Assert.Equal(ToolAuthorizationOutcome.Denied, evaluation.TerminalDecision?.Outcome);
        Assert.Equal("internal_policy_failure", evaluation.TerminalDecision?.DenyReason);
        Assert.Single(Assert.IsType<ShellPolicyDecisionTrace>(evaluation.CompletedTrace).Rows);
    }

    [Fact]
    public async Task Pipeline_denies_when_a_stage_completes_with_a_different_result()
    {
        var evaluation = CreateEvaluation();
        var prompt = ToolAuthorizationDecision.RequiresApproval(evaluation.Projection.ApprovalContext);
        var deny = ToolAuthorizationDecision.Deny("shell_references_protected_path");
        ShellPolicyStage[] stages =
        [
            (state, _) =>
            {
                Assert.IsType<ShellPolicyStageResult.Complete>(state.Complete(prompt));
                return ValueTask.FromResult<ShellPolicyStageResult>(new ShellPolicyStageResult.Complete(deny));
            }
        ];

        var result = Assert.IsType<ShellPolicyStageResult.Fault>(
            await ShellPolicyPipeline.RunAsync(evaluation, stages, TestContext.Current.CancellationToken));

        Assert.Equal(ShellPolicyFault.InvalidStageResult, result.Reason);
        Assert.Equal("internal_policy_failure", evaluation.TerminalDecision?.DenyReason);
    }

    [Fact]
    public async Task Pipeline_denies_when_a_stage_faults_then_returns_continue()
    {
        var evaluation = CreateEvaluation();
        ShellPolicyStage[] stages =
        [
            (state, _) =>
            {
                Assert.IsType<ShellPolicyStageResult.Fault>(state.Fault(ShellPolicyFault.InvalidCoverage));
                return ValueTask.FromResult<ShellPolicyStageResult>(new ShellPolicyStageResult.Continue());
            }
        ];

        var result = Assert.IsType<ShellPolicyStageResult.Fault>(
            await ShellPolicyPipeline.RunAsync(evaluation, stages, TestContext.Current.CancellationToken));

        Assert.Equal(ShellPolicyFault.InvalidStageResult, result.Reason);
        Assert.Equal("internal_policy_failure", evaluation.TerminalDecision?.DenyReason);
    }

    [Fact]
    public async Task Pipeline_denies_when_a_stage_completes_then_throws()
    {
        var evaluation = CreateEvaluation();
        var prompt = ToolAuthorizationDecision.RequiresApproval(evaluation.Projection.ApprovalContext);
        ShellPolicyStage[] stages =
        [
            (state, _) =>
            {
                Assert.IsType<ShellPolicyStageResult.Complete>(state.Complete(prompt));
                throw new InvalidOperationException("stage failed");
            }
        ];

        var result = Assert.IsType<ShellPolicyStageResult.Fault>(
            await ShellPolicyPipeline.RunAsync(evaluation, stages, TestContext.Current.CancellationToken));

        Assert.Equal(ShellPolicyFault.StageException, result.Reason);
        Assert.Equal("internal_policy_failure", evaluation.TerminalDecision?.DenyReason);
    }

    [Fact]
    public async Task Pipeline_rejects_an_invalid_fault_enum()
    {
        var evaluation = CreateEvaluation();
        ShellPolicyStage[] stages =
        [
            static (_, _) => ValueTask.FromResult<ShellPolicyStageResult>(
                new ShellPolicyStageResult.Fault((ShellPolicyFault)999))
        ];

        var result = Assert.IsType<ShellPolicyStageResult.Fault>(
            await ShellPolicyPipeline.RunAsync(evaluation, stages, TestContext.Current.CancellationToken));

        Assert.Equal(ShellPolicyFault.InvalidStageResult, result.Reason);
        Assert.Equal(ShellPolicyFault.InvalidStageResult, evaluation.TerminalFault);
        Assert.Equal("internal_policy_failure", evaluation.TerminalDecision?.DenyReason);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Pipeline_rejects_null_stages_and_results_before_later_stages(bool nullStage)
    {
        var evaluation = CreateEvaluation();
        var laterStageVisited = false;
        ShellPolicyStage[] stages =
        [
            nullStage
                ? null!
                : static (_, _) => ValueTask.FromResult<ShellPolicyStageResult>(null!),
            (_, _) =>
            {
                laterStageVisited = true;
                return ValueTask.FromResult<ShellPolicyStageResult>(new ShellPolicyStageResult.Continue());
            }
        ];

        var result = Assert.IsType<ShellPolicyStageResult.Fault>(
            await ShellPolicyPipeline.RunAsync(evaluation, stages, TestContext.Current.CancellationToken));

        Assert.Equal(ShellPolicyFault.InvalidStageResult, result.Reason);
        Assert.Equal("internal_policy_failure", evaluation.TerminalDecision?.DenyReason);
        Assert.False(laterStageVisited);
    }

    [Fact]
    public async Task Syntax_stage_prompts_before_a_later_stage_for_untyped_candidates()
    {
        var candidate = new ApprovalCandidate("git status", "/work");
        var evaluation = CreateEvaluation(candidate);
        var laterStageVisited = false;
        ShellPolicyStage[] stages =
        [
            ShellPolicyInitialStages.Syntax(ShellTool.ToolName),
            (_, _) =>
            {
                laterStageVisited = true;
                return ValueTask.FromResult<ShellPolicyStageResult>(new ShellPolicyStageResult.Continue());
            }
        ];

        var result = Assert.IsType<ShellPolicyStageResult.Complete>(
            await ShellPolicyPipeline.RunAsync(evaluation, stages, TestContext.Current.CancellationToken));

        Assert.Equal(ToolAuthorizationOutcome.RequiresApproval, result.Decision.Outcome);
        Assert.False(laterStageVisited);
        Assert.Single(Assert.IsType<ToolApprovalContext>(result.Decision.ApprovalContext).Candidates!);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Syntax_stage_honors_exact_one_time_authority_before_prompting(bool isMessy)
    {
        var candidate = new ApprovalCandidate("git status", "/work");
        var evaluation = CreateEvaluationWithExactOneTime(isMessy, candidate);

        var result = Assert.IsType<ShellPolicyStageResult.Complete>(
            await ShellPolicyPipeline.RunAsync(
                evaluation,
                [ShellPolicyInitialStages.Syntax(ShellTool.ToolName)],
                TestContext.Current.CancellationToken));

        Assert.Equal(ToolAuthorizationOutcome.Allowed, result.Decision.Outcome);
        Assert.Equal(ToolAllowReason.OneTimeApproval, result.Decision.AllowReason);
        var trace = Assert.IsType<ShellPolicyDecisionTrace>(evaluation.CompletedTrace);
        var completion = Assert.Single(trace.Rows);
        Assert.Equal(ShellPolicyTraceStage.Completion, completion.Stage);
        Assert.Equal(ShellPolicyTraceOutcome.Allow, completion.Outcome);
    }

    [Fact]
    public async Task Syntax_stage_faults_before_a_later_stage_for_invalid_tokens()
    {
        var candidate = BashCandidate("git status") with
        {
            VerbTokens = Array.AsReadOnly(["git status"])
        };
        var evaluation = CreateEvaluation(candidate);
        var laterStageVisited = false;
        ShellPolicyStage[] stages =
        [
            ShellPolicyInitialStages.Syntax(ShellTool.ToolName),
            (_, _) =>
            {
                laterStageVisited = true;
                return ValueTask.FromResult<ShellPolicyStageResult>(new ShellPolicyStageResult.Continue());
            }
        ];

        var result = Assert.IsType<ShellPolicyStageResult.Fault>(
            await ShellPolicyPipeline.RunAsync(evaluation, stages, TestContext.Current.CancellationToken));

        Assert.Equal(ShellPolicyFault.InvalidProjection, result.Reason);
        Assert.False(laterStageVisited);
        Assert.Equal("internal_policy_failure", evaluation.TerminalDecision?.DenyReason);
    }

    [Fact]
    public async Task Protected_causal_path_stage_denies_before_a_later_stage()
    {
        var (evaluation, policy, _) = CreateCausalEvaluation(
            "cd /tmp && inspect; head private.log",
            ["/tmp/private.log"]);
        var laterStageVisited = false;
        ShellPolicyStage[] stages =
        [
            ShellPolicyInitialStages.ProtectedCausalPaths(policy),
            (_, _) =>
            {
                laterStageVisited = true;
                return ValueTask.FromResult<ShellPolicyStageResult>(new ShellPolicyStageResult.Continue());
            }
        ];

        var result = Assert.IsType<ShellPolicyStageResult.Complete>(
            await ShellPolicyPipeline.RunAsync(evaluation, stages, TestContext.Current.CancellationToken));

        Assert.Equal(ToolAuthorizationOutcome.Denied, result.Decision.Outcome);
        Assert.Equal("shell_references_protected_path", result.Decision.DenyReason);
        Assert.False(laterStageVisited);
    }

    [Fact]
    public async Task Causal_directory_stage_continues_for_eligible_directories()
    {
        var (evaluation, policy, _) = CreateCausalEvaluation(
            "cd /tmp && inspect; head result.log");

        var result = await ShellPolicyPipeline.RunAsync(
            evaluation,
            [ShellPolicyInitialStages.CausalDirectories(policy, ShellTool.ToolName)],
            TestContext.Current.CancellationToken);

        Assert.IsType<ShellPolicyStageResult.Continue>(result);
        Assert.Null(evaluation.TerminalDecision);
    }

    [SlopwatchSuppress("SW001", "This test requires native POSIX symbolic-link behavior.")]
    [Fact(SkipUnless = nameof(IsPosix), Skip = "POSIX-only symbolic-link semantics")]
    public async Task Causal_directory_stage_prompts_for_a_symbolic_link_directory()
    {
        var root = Directory.CreateTempSubdirectory("netclaw-policy-stage-");
        try
        {
            var target = Path.Combine(root.FullName, "target");
            var alias = Path.Combine(root.FullName, "alias");
            Directory.CreateDirectory(target);
            Directory.CreateSymbolicLink(alias, target);
            var (evaluation, policy, _) = CreateCausalEvaluation(
                $"cd {alias} && inspect; head result.log");

            var result = Assert.IsType<ShellPolicyStageResult.Complete>(
                await ShellPolicyPipeline.RunAsync(
                    evaluation,
                    [ShellPolicyInitialStages.CausalDirectories(policy, ShellTool.ToolName)],
                    TestContext.Current.CancellationToken));

            Assert.Equal(ToolAuthorizationOutcome.RequiresApproval, result.Decision.Outcome);
            Assert.True(Assert.IsType<ToolApprovalContext>(result.Decision.ApprovalContext).IsMessy);
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task Actor_evidence_stage_applies_one_validated_batch()
    {
        var evaluation = CreateEvaluation(BashCandidate("git status"));
        var candidate = Assert.Single(evaluation.Candidates);
        var service = new FixedShellApprovalService(request =>
        {
            var requested = Assert.Single(request.Candidates);
            Assert.Equal(candidate.Id, requested.CandidateId);
            return new ShellApprovalMatchResult(
                new PersistentGrantStoreStatus.Ready(),
                [
                    new ShellGrantCandidateMatch(
                        candidate.Id,
                        new ToolApprovalMatch(candidate.Candidate.Verb, "session", "this chat"),
                        ShellCoverageKind.Session,
                        NearMisses: [])
                ]);
        });

        var result = await ShellPolicyPipeline.RunAsync(
            evaluation,
            [ShellPolicyGrantStages.ActorEvidence(
                new ShellApprovalEvidenceAdapter(service),
                (ToolApprovalSessionId)"signalr/shell-policy-actor-stage",
                TrustAudience.Personal,
                new ToolName(ShellTool.ToolName))],
            TestContext.Current.CancellationToken);

        Assert.IsType<ShellPolicyStageResult.Continue>(result);
        Assert.Equal(1, service.RequestCount);
        Assert.Equal(ShellCoverageKind.Session, evaluation.CoverageFor(candidate.Id).Kind);
        Assert.NotNull(evaluation.GrantEvidence);
        Assert.Single(evaluation.ApprovalMatches);
        Assert.IsType<ShellPolicyStageResult.Complete>(evaluation.Complete(
            ToolAuthorizationDecision.Allow(
                ToolAllowReason.StoredApproval,
                evaluation.ApprovalMatches)));
        Assert.Collection(
            Assert.IsType<ShellPolicyDecisionTrace>(evaluation.CompletedTrace).Rows,
            row => Assert.Equal(ShellPolicyTraceStage.StoredGrantMatch, row.Stage),
            row => Assert.Equal(ShellPolicyTraceStage.Completion, row.Stage));
    }

    [Fact]
    public async Task Actor_evidence_stage_rejects_a_malformed_batch_before_later_stages()
    {
        var evaluation = CreateEvaluation(BashCandidate("git status"));
        var candidate = Assert.Single(evaluation.Candidates);
        var service = new FixedShellApprovalService(static _ => new ShellApprovalMatchResult(
            new PersistentGrantStoreStatus.Ready(),
            CandidateMatches: []));
        var laterStageVisited = false;
        ShellPolicyStage[] stages =
        [
            ShellPolicyGrantStages.ActorEvidence(
                new ShellApprovalEvidenceAdapter(service),
                (ToolApprovalSessionId)"signalr/shell-policy-invalid-actor-stage",
                TrustAudience.Personal,
                new ToolName(ShellTool.ToolName)),
            (_, _) =>
            {
                laterStageVisited = true;
                return ValueTask.FromResult<ShellPolicyStageResult>(new ShellPolicyStageResult.Continue());
            }
        ];

        var result = Assert.IsType<ShellPolicyStageResult.Fault>(
            await ShellPolicyPipeline.RunAsync(
                evaluation,
                stages,
                TestContext.Current.CancellationToken));

        Assert.Equal(ShellPolicyFault.InvalidActorEvidence, result.Reason);
        Assert.Equal(1, service.RequestCount);
        Assert.False(laterStageVisited);
        Assert.Null(evaluation.GrantEvidence);
        Assert.Equal(ShellCoverageKind.Uncovered, evaluation.CoverageFor(candidate.Id).Kind);
        Assert.Equal("internal_policy_failure", evaluation.TerminalDecision?.DenyReason);
    }

    [Fact]
    public async Task Actor_evidence_precedes_approval_exempt_trace_rows()
    {
        var evaluation = CreateEvaluation(
            BashCandidate("git status"),
            BashCandidate("echo") with { Directory = null });
        var grantCandidate = evaluation.Candidates[0];
        var sideEffect = evaluation.Candidates[1];
        Assert.True(ApprovalPatternMatching.IsPureSideEffect(sideEffect.Candidate));
        var service = new FixedShellApprovalService(request =>
        {
            Assert.Single(request.Candidates);
            return new ShellApprovalMatchResult(
                new PersistentGrantStoreStatus.Ready(),
                [
                    new ShellGrantCandidateMatch(
                        grantCandidate.Id,
                        Match: null,
                        GrantCoverage: null,
                        NearMisses: [])
                ]);
        });
        var adapter = new ShellApprovalEvidenceAdapter(service);

        var result = await ShellPolicyPipeline.RunAsync(
            evaluation,
            [
                ShellPolicyGrantStages.ActorEvidence(
                    adapter,
                    (ToolApprovalSessionId)"signalr/shell-policy-trace-order",
                    TrustAudience.Personal,
                    new ToolName(ShellTool.ToolName)),
                ShellPolicyGrantStages.ApprovalExemptSideEffects(adapter.IsAvailable)
            ],
            TestContext.Current.CancellationToken);

        Assert.IsType<ShellPolicyStageResult.Continue>(result);
        Assert.Equal(ShellCoverageKind.Uncovered, evaluation.CoverageFor(grantCandidate.Id).Kind);
        Assert.Equal(ShellCoverageKind.ReviewedSafePolicy, evaluation.CoverageFor(sideEffect.Id).Kind);
        Assert.IsType<ShellPolicyStageResult.Complete>(evaluation.Complete(
            ToolAuthorizationDecision.RequiresApproval(evaluation.Projection.ApprovalContext)));
        Assert.Collection(
            Assert.IsType<ShellPolicyDecisionTrace>(evaluation.CompletedTrace).Rows,
            row => Assert.Equal(ShellPolicyTraceStage.StoredGrantMatch, row.Stage),
            row =>
            {
                Assert.Equal(ShellPolicyTraceStage.ReviewedSafePolicy, row.Stage);
                Assert.Equal(ShellPolicyTraceReason.ApprovalExemptSideEffect, row.Reason);
            },
            row => Assert.Equal(ShellPolicyTraceStage.Completion, row.Stage));
    }

    [Theory]
    [InlineData(false, nameof(ShellCoverageKind.Uncovered))]
    [InlineData(true, nameof(ShellCoverageKind.ReviewedSafePolicy))]
    public async Task Approval_exempt_stage_follows_approval_service_availability(
        bool serviceAvailable,
        string expectedCoverageName)
    {
        var evaluation = CreateEvaluation(BashCandidate("echo") with { Directory = null });
        var candidate = Assert.Single(evaluation.Candidates);
        var service = new FixedShellApprovalService(static _ => new ShellApprovalMatchResult(
            new PersistentGrantStoreStatus.Ready(),
            CandidateMatches: []));
        var adapter = new ShellApprovalEvidenceAdapter(serviceAvailable ? service : null);

        var result = await ShellPolicyPipeline.RunAsync(
            evaluation,
            [
                ShellPolicyGrantStages.ActorEvidence(
                    adapter,
                    sessionId: null,
                    TrustAudience.Personal,
                    new ToolName(ShellTool.ToolName)),
                ShellPolicyGrantStages.ApprovalExemptSideEffects(adapter.IsAvailable)
            ],
            TestContext.Current.CancellationToken);

        Assert.IsType<ShellPolicyStageResult.Continue>(result);
        Assert.NotNull(evaluation.GrantEvidence);
        Assert.Equal(0, service.RequestCount);
        Assert.Equal(
            Enum.Parse<ShellCoverageKind>(expectedCoverageName),
            evaluation.CoverageFor(candidate.Id).Kind);
    }

    [Theory]
    [InlineData(false, nameof(ShellCoverageKind.Uncovered))]
    [InlineData(true, nameof(ShellCoverageKind.ReviewedSafePolicy))]
    public async Task Reviewed_safe_real_scope_stage_requires_interactive_approval(
        bool interactive,
        string expectedCoverageName)
    {
        var (evaluation, policy, context) = CreateReviewedSafeEvaluation(
            "head README.md",
            interactive,
            "head");
        var candidate = Assert.Single(evaluation.Candidates);

        var result = await ShellPolicyPipeline.RunAsync(
            evaluation,
            [ShellPolicyReviewedSafeStages.RealScope(policy, context.Invocation)],
            TestContext.Current.CancellationToken);

        Assert.IsType<ShellPolicyStageResult.Continue>(result);
        Assert.Equal(
            Enum.Parse<ShellCoverageKind>(expectedCoverageName),
            evaluation.CoverageFor(candidate.Id).Kind);
    }

    [SlopwatchSuppress("SW001", "This test pins Bash causal approval intent on POSIX hosts.")]
    [Fact(SkipUnless = nameof(IsPosix), Skip = "POSIX-only shell directory semantics")]
    public async Task Reviewed_safe_intent_stage_requires_real_prerequisite_coverage()
    {
        var (evaluation, policy, context) = CreateCausalEvaluation(
            "cd /tmp && inspect; head result.log",
            safeVerbs: SafeVerbList.FromVerbs(ApprovalShell.Bash, ["head"]));
        var consumer = Assert.Single(
            evaluation.Candidates,
            candidate => candidate.Role == ShellPolicyCandidateRole.CausalIntentConsumer);
        Assert.NotEmpty(consumer.IntentPrerequisites);

        var beforeCoverage = await ShellPolicyPipeline.RunAsync(
            evaluation,
            [ShellPolicyReviewedSafeStages.IntentScope(policy, context.Invocation)],
            TestContext.Current.CancellationToken);

        Assert.IsType<ShellPolicyStageResult.Continue>(beforeCoverage);
        Assert.Equal(ShellCoverageKind.Uncovered, evaluation.CoverageFor(consumer.Id).Kind);

        foreach (var prerequisiteId in consumer.IntentPrerequisites)
        {
            var prerequisite = evaluation.Candidates[prerequisiteId.Value];
            Assert.IsType<ShellPolicyStageResult.Continue>(evaluation.Cover(
                prerequisite,
                ShellCoverageKind.Session,
                ShellPolicyReason.SessionGrant,
                ShellScopeRelation.ThisChat));
        }

        var afterCoverage = await ShellPolicyPipeline.RunAsync(
            evaluation,
            [
                ShellPolicyReviewedSafeStages.RealScope(policy, context.Invocation),
                ShellPolicyReviewedSafeStages.IntentScope(policy, context.Invocation)
            ],
            TestContext.Current.CancellationToken);

        Assert.IsType<ShellPolicyStageResult.Continue>(afterCoverage);
        Assert.Equal(
            ShellCoverageKind.ReviewedSafePolicy,
            evaluation.CoverageFor(consumer.Id).Kind);
        Assert.IsType<ShellPolicyStageResult.Complete>(evaluation.Complete(
            ToolAuthorizationDecision.Allow(ToolAllowReason.StoredApproval)));
        Assert.Contains(
            Assert.IsType<ShellPolicyDecisionTrace>(evaluation.CompletedTrace).Rows,
            row => row is
            {
                Stage: ShellPolicyTraceStage.ReviewedSafePolicy,
                Reason: ShellPolicyTraceReason.ReviewedSafePhrase,
                ScopeRelation: ShellScopeRelation.UnderIntentRoot
            });
    }

    [Fact]
    public async Task Exact_one_time_stage_covers_the_remaining_candidate_set()
    {
        var evaluation = CreateEvaluationWithExactOneTime(
            isMessy: false,
            BashCandidate("git status"));
        var candidate = Assert.Single(evaluation.Candidates);

        var result = await ShellPolicyPipeline.RunAsync(
            evaluation,
            [ShellPolicyGrantStages.ExactOneTime(
                new ToolName(ShellTool.ToolName),
                "/work/session")],
            TestContext.Current.CancellationToken);

        Assert.IsType<ShellPolicyStageResult.Continue>(result);
        Assert.True(evaluation.HasOneTimeCoverage);
        Assert.Equal(ShellCoverageKind.OneTime, evaluation.CoverageFor(candidate.Id).Kind);
        Assert.IsType<ShellPolicyStageResult.Complete>(evaluation.Complete(
            ToolAuthorizationDecision.Allow(ToolAllowReason.OneTimeApproval)));
        Assert.Collection(
            Assert.IsType<ShellPolicyDecisionTrace>(evaluation.CompletedTrace).Rows,
            row =>
            {
                Assert.Equal(ShellPolicyTraceStage.OneTimeApproval, row.Stage);
                Assert.Equal(ShellPolicyTraceReason.OneTimeGrant, row.Reason);
            },
            row => Assert.Equal(ShellPolicyTraceStage.Completion, row.Stage));
    }

    [Fact]
    public async Task Exact_one_time_stage_leaves_a_different_tool_key_uncovered()
    {
        var evaluation = CreateEvaluationWithExactOneTime(
            isMessy: false,
            BashCandidate("git status"));
        var candidate = Assert.Single(evaluation.Candidates);

        var result = await ShellPolicyPipeline.RunAsync(
            evaluation,
            [ShellPolicyGrantStages.ExactOneTime(
                new ToolName("different_tool"),
                "/work/session")],
            TestContext.Current.CancellationToken);

        Assert.IsType<ShellPolicyStageResult.Continue>(result);
        Assert.False(evaluation.HasOneTimeCoverage);
        Assert.Equal(ShellCoverageKind.Uncovered, evaluation.CoverageFor(candidate.Id).Kind);
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public async Task Persistent_store_stage_denies_only_uncovered_candidates(
        bool coveredBySession,
        bool expectsDeny)
    {
        var evaluation = CreateEvaluation(BashCandidate("git status"));
        var candidate = Assert.Single(evaluation.Candidates);
        var service = new FixedShellApprovalService(_ => new ShellApprovalMatchResult(
            new PersistentGrantStoreStatus.Unavailable(ApprovalStoreFailure.IoFailure),
            [
                new ShellGrantCandidateMatch(
                    candidate.Id,
                    coveredBySession
                        ? new ToolApprovalMatch(candidate.Candidate.Verb, "session", "this chat")
                        : null,
                    coveredBySession ? ShellCoverageKind.Session : null,
                    NearMisses: [])
            ]));

        var result = await ShellPolicyPipeline.RunAsync(
            evaluation,
            [
                ShellPolicyGrantStages.ActorEvidence(
                    new ShellApprovalEvidenceAdapter(service),
                    (ToolApprovalSessionId)"signalr/shell-policy-store-stage",
                    TrustAudience.Personal,
                    new ToolName(ShellTool.ToolName)),
                ShellPolicyGrantStages.PersistentStoreAvailability()
            ],
            TestContext.Current.CancellationToken);

        Assert.Equal(1, service.RequestCount);
        if (!expectsDeny)
        {
            Assert.IsType<ShellPolicyStageResult.Continue>(result);
            Assert.Null(evaluation.TerminalDecision);
            Assert.Equal(ShellCoverageKind.Session, evaluation.CoverageFor(candidate.Id).Kind);
        }
        else
        {
            var complete = Assert.IsType<ShellPolicyStageResult.Complete>(result);
            Assert.Equal(ToolAuthorizationOutcome.Denied, complete.Decision.Outcome);
            Assert.Equal("approval_store_unavailable", complete.Decision.DenyReason);
            Assert.Equal(ShellCoverageKind.Uncovered, evaluation.CoverageFor(candidate.Id).Kind);
        }
    }

    [Fact]
    public async Task Persistent_store_stage_rejects_missing_actor_evidence()
    {
        var evaluation = CreateEvaluation(BashCandidate("git status"));

        var result = Assert.IsType<ShellPolicyStageResult.Fault>(
            await ShellPolicyPipeline.RunAsync(
                evaluation,
                [ShellPolicyGrantStages.PersistentStoreAvailability()],
                TestContext.Current.CancellationToken));

        Assert.Equal(ShellPolicyFault.InvalidActorEvidence, result.Reason);
        Assert.Equal("internal_policy_failure", evaluation.TerminalDecision?.DenyReason);
    }

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
        => CreateEvaluation(
            isMessy: false,
            hasExactOneTimeApproval: false,
            candidates: candidates);

    private static ShellPolicyEvaluation CreateEvaluationWithExactOneTime(
        bool isMessy,
        params ApprovalCandidate[] candidates)
        => CreateEvaluation(
            isMessy,
            hasExactOneTimeApproval: true,
            candidates: candidates);

    private static ShellPolicyEvaluation CreateEvaluation(
        bool isMessy,
        bool hasExactOneTimeApproval,
        params ApprovalCandidate[] candidates)
    {
        var environment = ShellExecutionEnvironment.CreateBash(ShellPlatform.Linux);
        var approvalContext = new ToolApprovalContext(
            ShellTool.ToolName,
            "shell command",
            candidates.Select(static candidate => candidate.Verb).ToArray(),
            candidates.Select(static candidate => candidate.Verb).ToArray(),
            [],
            Cwd: "/work",
            IsMessy: isMessy,
            Candidates: candidates);
        var context = TestToolExecutionContext.CreateBound(
            "signalr/shell-policy-evaluation",
            "/work/session",
            TrustAudience.Personal);
        if (hasExactOneTimeApproval)
        {
            context.OneTimeApprovedToolName = ShellTool.ToolName;
            context.SetOneTimeApprovedPatterns(OneTimeApprovalKeys.Create(approvalContext));
        }

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

    private static (
        ShellPolicyEvaluation Evaluation,
        ToolAccessPolicy Policy,
        ToolExecutionContext Context) CreateCausalEvaluation(
        string command,
        IEnumerable<string>? deniedPaths = null,
        SafeVerbList? safeVerbs = null)
    {
        var environment = ShellExecutionEnvironment.CreateBash(ShellPlatform.Linux);
        var commandPolicy = new ShellCommandPolicy(environment);
        var pathPolicy = new ToolPathPolicy(environment, deniedPaths ?? []);
        var config = new ToolConfig { ShellMode = ShellExecutionMode.HostAllowed };
        var policy = new ToolAccessPolicy(
            config,
            new EffectivePolicyDefaults(
                DeploymentPosture.Personal,
                TrustAudience.Personal,
                ShellExecutionMode.HostAllowed,
                UsedStrictFallback: false),
            commandPolicy,
            pathPolicy,
            safeVerbs: safeVerbs);
        var approvalContext = new ToolApprovalContext(
            ShellTool.ToolName,
            "shell command",
            [],
            [],
            [],
            Cwd: "/work",
            IsMessy: true,
            Candidates: []);
        var context = TestToolExecutionContext.CreateBound(
            "signalr/shell-policy-causal-stage",
            "/work/session",
            TrustAudience.Personal);
        var execution = commandPolicy.Analyze(command, "/work");
        var created = ShellPolicyProjection.TryCreate(
            environment,
            new ShellApprovalMatcher(environment),
            execution,
            approvalContext,
            context,
            policy.IsSafePlatformTemporaryPath,
            out var projection);

        Assert.True(created);
        Assert.NotNull(projection);
        Assert.True(projection.HasCausalIntent);
        return (new ShellPolicyEvaluation(projection), policy, context);
    }

    private static (
        ShellPolicyEvaluation Evaluation,
        ToolAccessPolicy Policy,
        ToolExecutionContext Context) CreateReviewedSafeEvaluation(
        string command,
        bool interactive,
        params string[] safeVerbs)
    {
        var environment = ShellExecutionEnvironment.CreateBash(ShellPlatform.Linux);
        var commandPolicy = new ShellCommandPolicy(environment);
        var pathPolicy = new ToolPathPolicy(environment, []);
        var policy = new ToolAccessPolicy(
            new ToolConfig { ShellMode = ShellExecutionMode.HostAllowed },
            new EffectivePolicyDefaults(
                DeploymentPosture.Personal,
                TrustAudience.Personal,
                ShellExecutionMode.HostAllowed,
                UsedStrictFallback: false),
            commandPolicy,
            pathPolicy,
            safeVerbs: SafeVerbList.FromVerbs(ApprovalShell.Bash, safeVerbs));
        var matcher = new ShellApprovalMatcher(environment);
        var arguments = ToolInput.Create(
            "Command",
            command,
            "WorkingDirectory",
            "/work");
        var execution = commandPolicy.Analyze(command, "/work");
        var approval = matcher.AnalyzeInvocation(
            new ToolName(ShellTool.ToolName),
            arguments,
            execution);
        var approvalContext = new ToolApprovalContext(
            ShellTool.ToolName,
            approval.DisplayText,
            approval.Patterns,
            approval.Candidates.Select(static candidate => candidate.Verb).ToArray(),
            [],
            Cwd: "/work",
            approval.IsMessy,
            approval.Candidates);
        var context = TestToolExecutionContext.CreateBound(
            "signalr/shell-policy-reviewed-safe-stage",
            "/work/session",
            new TestToolExecutionContextOptions
            {
                Audience = TrustAudience.Personal,
                ProjectDirectory = "/work",
                InteractiveApproval = TestToolExecutionContext.InteractiveApproval(interactive)
            });
        var created = ShellPolicyProjection.TryCreate(
            environment,
            matcher,
            execution,
            approvalContext,
            context,
            static _ => false,
            out var projection);

        Assert.True(created);
        Assert.NotNull(projection);
        return (new ShellPolicyEvaluation(projection), policy, context);
    }

    private static ApprovalCandidate BashCandidate(string verb) => new(verb, "/work")
    {
        Shell = ApprovalShell.Bash,
        VerbTokens = Array.AsReadOnly(verb.Split(' ', StringSplitOptions.RemoveEmptyEntries))
    };

    private sealed class FixedShellApprovalService(
        Func<ShellApprovalMatchRequest, ShellApprovalMatchResult> responseFactory)
        : IToolApprovalService, IShellApprovalMatchService
    {
        internal int RequestCount { get; private set; }

        public Task<ShellApprovalMatchResult> MatchShellCandidatesAsync(
            ShellApprovalMatchRequest request,
            CancellationToken cancellationToken)
        {
            RequestCount++;
            return Task.FromResult(responseFactory(request));
        }

        public Task<ToolApprovalCheckResult> CheckApprovalAsync(
            ToolApprovalSessionId? sessionId,
            TrustAudience audience,
            ToolName toolName,
            IReadOnlyList<ApprovalCandidate> candidates,
            string? cwd,
            CancellationToken ct = default)
            => throw new InvalidOperationException("The stage must use typed actor evidence.");

        public Task<IReadOnlyList<string>> GetUnapprovedPatternsAsync(
            ToolApprovalSessionId? sessionId,
            TrustAudience audience,
            ToolName toolName,
            IReadOnlyList<string> patterns,
            string? cwd,
            CancellationToken ct = default)
            => throw new InvalidOperationException("The stage must use typed actor evidence.");

        public Task RecordApprovalAsync(
            ToolApprovalSessionId sessionId,
            TrustAudience audience,
            ToolName toolName,
            IReadOnlyList<string> patterns,
            bool persistent,
            string? cwd,
            CancellationToken ct = default)
            => throw new InvalidOperationException("The stage must not record approvals.");
    }
}
