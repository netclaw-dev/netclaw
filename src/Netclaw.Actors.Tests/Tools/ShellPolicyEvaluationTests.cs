// -----------------------------------------------------------------------
// <copyright file="ShellPolicyEvaluationTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Actors.Protocol;
using Netclaw.Actors.Tools;
using Netclaw.Configuration;
using Netclaw.Security;
using Netclaw.Tests.Utilities;
using Netclaw.Tools;
using ShellSyntaxTree;
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
    public async Task Protected_causal_path_stage_checks_each_fallback_base()
    {
        var (evaluation, policy, _) = CreateCausalEvaluation(
            "cd /tmp && inspect; head result.log",
            ["/work/result.log"]);

        var result = Assert.IsType<ShellPolicyStageResult.Complete>(
            await ShellPolicyPipeline.RunAsync(
                evaluation,
                [ShellPolicyInitialStages.ProtectedCausalPaths(policy)],
                TestContext.Current.CancellationToken));

        Assert.Equal(ToolAuthorizationOutcome.Denied, result.Decision.Outcome);
        Assert.Equal("shell_references_protected_path", result.Decision.DenyReason);
    }

    [Fact]
    public void Causal_protected_path_check_denies_an_invalid_known_value()
    {
        var (evaluation, policy, _) = CreateCausalEvaluation(
            "cd /tmp && inspect; head result.log");
        var consumer = Assert.Single(
            evaluation.Candidates,
            static candidate => candidate.Role == ShellPolicyCandidateRole.CausalIntentConsumer);
        var facts = evaluation.Projection.PathFacts.For(consumer.Id);
        var source = Assert.Single(
            Assert.IsType<ShellPolicyResolvedPathView>(facts.Intent).Facts,
            static fact => fact.Source.Origin == ShellPolicyPathOrigin.EffectiveArgument).Source;
        var invalid = new ShellPolicyResolvedPathFact(
            source,
            ShellPolicyPathResolutionState.InvalidKnownValue,
            []);
        var invalidFacts = facts with
        {
            Intent = new ShellPolicyResolvedPathView(
                Assert.IsType<ShellPolicyScopePathFact>(facts.IntentScope),
                [invalid])
        };

        Assert.True(policy.CausalIntentReferencesProtectedPath(invalidFacts));
    }

    [Fact]
    public void Causal_protected_path_check_does_not_treat_unknown_as_a_denied_path()
    {
        var (evaluation, policy, _) = CreateCausalEvaluation(
            "cd /tmp && inspect; head result.log");
        var consumer = Assert.Single(
            evaluation.Candidates,
            static candidate => candidate.Role == ShellPolicyCandidateRole.CausalIntentConsumer);
        var facts = evaluation.Projection.PathFacts.For(consumer.Id);
        var environment = ShellExecutionEnvironment.CreatePowerShell(
            @"C:\Program Files\PowerShell\7\pwsh.exe",
            PwshDialect.PowerShell7);
        var occurrence = Assert.Single(
            new ShellCommandPolicy(environment)
                .Analyze("Get-Date > $name", @"C:\work")
                .Commands);
        var resolutionBase = new ShellPolicyScopePathFact(
            ShellPolicyPathBaseKind.Real,
            BaseIndex: 0,
            @"C:\work",
            ShellPolicyPathResolutionState.Known,
            CreateCanonicalPath(@"C:\work", ShellPathStyle.Windows));
        var source = Assert.Single(
            ShellPolicyOccurrencePathFacts.Create(occurrence)
                .Resolve(resolutionBase, ShellPathStyle.Windows)
                .Facts,
            static fact => fact.Source.Origin == ShellPolicyPathOrigin.Redirect).Source;
        var unknown = new ShellPolicyResolvedPathFact(
            source,
            ShellPolicyPathResolutionState.UnknownDynamic,
            []);
        var unknownFacts = facts with
        {
            Intent = new ShellPolicyResolvedPathView(
                Assert.IsType<ShellPolicyScopePathFact>(facts.IntentScope),
                [unknown]),
            Fallbacks = []
        };

        Assert.False(policy.CausalIntentReferencesProtectedPath(unknownFacts));
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

    [Theory]
    [InlineData("grep -f ./patterns ./data.txt", true)]
    [InlineData("du -sh ./*", true)]
    [InlineData("grep -f /external/patterns ./data.txt", false)]
    [InlineData("head 'C:\\temp\\file.log'", false)]
    public async Task Reviewed_safe_real_scope_stage_uses_projected_path_facts(
        string command,
        bool allCovered)
    {
        var phrase = command.Split(' ', 2)[0];
        var (evaluation, policy, context) = CreateReviewedSafeEvaluation(
            command,
            interactive: true,
            phrase);

        var result = await ShellPolicyPipeline.RunAsync(
            evaluation,
            [ShellPolicyReviewedSafeStages.RealScope(policy, context.Invocation)],
            TestContext.Current.CancellationToken);

        Assert.IsType<ShellPolicyStageResult.Continue>(result);
        var actual = evaluation.Candidates.All(candidate => evaluation.IsCovered(candidate.Id));
        Assert.True(
            actual == allCovered,
            string.Join(
                "; ",
                evaluation.Candidates.Select(candidate =>
                {
                    var facts = evaluation.Projection.PathFacts.For(candidate.Id);
                    return $"{candidate.Candidate.Verb}: "
                           + $"directory={candidate.Candidate.Directory}; "
                           + $"sourceCwd={candidate.SourceOccurrence?.WorkingDirectory}; "
                           + $"real={facts.RealScope}; "
                           + $"facts=[{string.Join(", ", facts.Real?.Facts ?? [])}]";
                })));
    }

    [Fact]
    public async Task Reviewed_safe_real_scope_stage_keeps_declared_windows_roots()
    {
        var environment = ShellExecutionEnvironment.CreatePowerShell(
            @"C:\Program Files\PowerShell\7\pwsh.exe",
            PwshDialect.PowerShell7);
        var (evaluation, policy, context) = CreateReviewedSafeEvaluation(
            @"Get-Content -LiteralPath C:\work\data.txt",
            true,
            environment,
            ApprovalShell.PowerShell,
            @"C:\work",
            "Get-Content");

        var result = await ShellPolicyPipeline.RunAsync(
            evaluation,
            [ShellPolicyReviewedSafeStages.RealScope(policy, context.Invocation)],
            TestContext.Current.CancellationToken);

        Assert.IsType<ShellPolicyStageResult.Continue>(result);
        var candidate = Assert.Single(evaluation.Candidates);
        Assert.True(evaluation.IsCovered(candidate.Id));
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
    public async Task Exact_one_time_and_prompt_share_one_uncovered_context()
    {
        var evaluation = CreateEvaluationWithExactOneTime(
            isMessy: false,
            BashCandidate("git status"));
        var candidate = Assert.Single(evaluation.Candidates);
        var context = TestToolExecutionContext.CreateBound(
            "signalr/shell-policy-shared-context",
            "/work/session",
            TrustAudience.Personal);

        var result = await ShellPolicyPipeline.RunAsync(
            evaluation,
            [ShellPolicyGrantStages.ExactOneTime(
                new ToolName("different_tool"),
                context.SessionDirectory)],
            TestContext.Current.CancellationToken);

        Assert.IsType<ShellPolicyStageResult.Continue>(result);
        Assert.False(evaluation.HasOneTimeCoverage);
        Assert.Equal(ShellCoverageKind.Uncovered, evaluation.CoverageFor(candidate.Id).Kind);

        var oneTimeContext = evaluation.GetUncoveredApprovalContext(context.SessionDirectory);
        var terminal = Assert.IsType<ShellPolicyStageResult.Complete>(
            await ShellPolicyPipeline.RunAsync(
                evaluation,
                [ShellPolicyTerminalStage.Complete(context)],
                TestContext.Current.CancellationToken));

        Assert.Same(oneTimeContext, terminal.Decision.ApprovalContext);
    }

    [Fact]
    public void Uncovered_context_reprojects_after_candidate_coverage_changes()
    {
        var evaluation = CreateEvaluation(
            BashCandidate("git status"),
            BashCandidate("git push"));
        var initial = evaluation.GetUncoveredApprovalContext("/work/session");
        var candidate = evaluation.Candidates[0];
        Assert.IsType<ShellPolicyStageResult.Continue>(evaluation.Cover(
            candidate,
            ShellCoverageKind.Session,
            ShellPolicyReason.SessionGrant,
            ShellScopeRelation.ThisChat));

        var reprojected = evaluation.GetUncoveredApprovalContext("/work/session");

        Assert.NotSame(initial, reprojected);
        Assert.Equal([evaluation.Candidates[1].Candidate], reprojected.Candidates);
    }

    [Fact]
    public void Uncovered_context_reprojects_when_session_directory_changes()
    {
        var candidate = BashCandidate("git status") with { Directory = "/work/repo" };
        var evaluation = CreateEvaluation(
            isMessy: false,
            hasExactOneTimeApproval: false,
            cwd: "/work/repo",
            candidates: [candidate]);
        var sessionScratch = evaluation.GetUncoveredApprovalContext("/work/repo");

        var ordinaryScope = evaluation.GetUncoveredApprovalContext("/work/session");

        Assert.NotSame(sessionScratch, ordinaryScope);
        Assert.DoesNotContain(
            sessionScratch.Options,
            static option => option.Key == ApprovalOptionKeys.ApproveAlwaysKey);
        Assert.Contains(
            ordinaryScope.Options,
            static option => option.Key == ApprovalOptionKeys.ApproveAlwaysKey);
    }

    [SlopwatchSuppress("SW001", "This test pins Bash causal approval intent on POSIX hosts.")]
    [Fact(SkipUnless = nameof(IsPosix), Skip = "POSIX-only shell directory semantics")]
    public void Causal_uncovered_context_retains_the_complete_approval_context()
    {
        var (evaluation, _, context) = CreateCausalEvaluation(
            "cd /tmp && inspect; head result.log",
            safeVerbs: SafeVerbList.FromVerbs(ApprovalShell.Bash, ["head"]));

        var uncovered = evaluation.GetUncoveredApprovalContext(context.SessionDirectory);

        Assert.Same(evaluation.Projection.ApprovalContext, uncovered);
    }

    [Fact]
    public void Path_facts_preserve_candidate_and_real_scope_identity()
    {
        var (evaluation, _, _) = CreateReviewedSafeEvaluation(
            "head README.md",
            interactive: true,
            "head");
        var candidate = Assert.Single(evaluation.Candidates);

        var facts = evaluation.Projection.PathFacts.For(candidate.Id);

        Assert.Same(candidate.SourceOccurrence, facts.SourceOccurrence);
        Assert.Equal(ShellPolicyPathResolutionState.Known, facts.RealScope.State);
        Assert.Equal("/work", facts.RealScope.Path?.Value);
        Assert.NotNull(facts.Real);
        Assert.Contains(
            facts.Real.Facts,
            fact => fact.Source.Origin == ShellPolicyPathOrigin.EffectiveArgument
                    && fact.Source.DomainKind == ShellPolicyPathDomainKind.Exact
                    && fact.State == ShellPolicyPathResolutionState.Known
                    && fact.Paths.Any(path => path.Value == "/work/README.md"));
    }

    [Theory]
    [InlineData(false, "head /external/file.log", "/work", "/external/file.log")]
    [InlineData(true, @"Get-Content C:\external\file.log", @"C:\work", @"C:\external\file.log")]
    public void Path_facts_do_not_rebase_absolute_paths_beneath_the_resolution_base(
        bool windowsStyle,
        string command,
        string resolutionBase,
        string expected)
    {
        var environment = windowsStyle
            ? ShellExecutionEnvironment.CreatePowerShell(
                @"C:\Program Files\PowerShell\7\pwsh.exe",
                PwshDialect.PowerShell7)
            : ShellExecutionEnvironment.CreateBash(ShellPlatform.Linux);
        var occurrence = Assert.Single(
            new ShellCommandPolicy(environment).Analyze(command, resolutionBase).Commands);
        var scope = new ShellPolicyScopePathFact(
            ShellPolicyPathBaseKind.Real,
            BaseIndex: 0,
            resolutionBase,
            ShellPolicyPathResolutionState.Known,
            CreateCanonicalPath(resolutionBase, environment.PathStyle));

        var facts = ShellPolicyOccurrencePathFacts.Create(occurrence)
            .Resolve(scope, environment.PathStyle);

        Assert.Contains(
            facts.Facts,
            fact => fact.Source.Origin == ShellPolicyPathOrigin.EffectiveArgument
                    && fact.State == ShellPolicyPathResolutionState.Known
                    && fact.Paths.Any(path => path.Value == expected));
    }

    [Theory]
    [InlineData(@"\external\file.log")]
    [InlineData(@"D:file.log")]
    [InlineData(@"FileSystem::C:\external\file.log")]
    public void Path_facts_keep_ambiguous_windows_root_forms_strict(string value)
    {
        Assert.False(ShellPolicyOccurrencePathFacts.TryResolveCanonicalPath(
            value,
            @"C:\work",
            ShellPathStyle.Windows,
            out _));
    }

    [Fact]
    public void Path_facts_keep_candidate_scope_separate_from_the_command_base()
    {
        var (evaluation, _, _) = CreateReviewedSafeEvaluation(
            "cat /work/sub/file.txt",
            interactive: true,
            "cat");
        var candidate = Assert.Single(
            evaluation.Candidates,
            static candidate => candidate.Candidate.Directory == "/work/sub");

        var facts = evaluation.Projection.PathFacts.For(candidate.Id);

        Assert.Equal("/work/sub", facts.RealScope.Path?.Value);
        Assert.Equal("/work", facts.Real?.ResolutionBase.Path?.Value);
        Assert.Contains(
            Assert.IsType<ShellPolicyResolvedPathView>(facts.Real).Facts,
            fact => fact.Source.Origin == ShellPolicyPathOrigin.EffectiveArgument
                    && fact.Paths.Any(path => path.Value == "/work/sub/file.txt"));
    }

    [SlopwatchSuppress("SW001", "This test pins Bash causal path facts on POSIX hosts.")]
    [Fact(SkipUnless = nameof(IsPosix), Skip = "POSIX-only shell directory semantics")]
    public void Causal_path_facts_keep_intent_and_fallback_resolutions_distinct()
    {
        var (evaluation, _, _) = CreateCausalEvaluation(
            "cd /tmp && inspect; head result.log");
        var candidate = Assert.Single(
            evaluation.Candidates,
            static candidate => candidate.Role == ShellPolicyCandidateRole.CausalIntentConsumer);

        var facts = evaluation.Projection.PathFacts.For(candidate.Id);

        Assert.Equal("/tmp", facts.IntentScope?.Path?.Value);
        Assert.Contains(facts.FallbackScopes, scope => scope.Path?.Value == "/work");
        Assert.Contains(
            Assert.IsType<ShellPolicyResolvedPathView>(facts.Intent).Facts,
            fact => fact.Source.Origin == ShellPolicyPathOrigin.EffectiveArgument
                    && fact.Paths.Any(path => path.Value == "/tmp/result.log"));
        Assert.Contains(
            facts.Fallbacks.SelectMany(static view => view.Facts),
            fact => fact.Source.Origin == ShellPolicyPathOrigin.EffectiveArgument
                    && fact.Paths.Any(path => path.Value == "/work/result.log"));
    }

    [Fact]
    public void Path_facts_distinguish_unknown_values_from_invalid_known_values()
    {
        var environment = ShellExecutionEnvironment.CreatePowerShell(
            @"C:\Program Files\PowerShell\7\pwsh.exe",
            PwshDialect.PowerShell7);
        var policy = new ShellCommandPolicy(environment);
        var occurrence = Assert.Single(policy.Analyze("Get-Date > $name", @"C:\work").Commands);
        var source = ShellPolicyOccurrencePathFacts.Create(occurrence);
        var realScope = new ShellPolicyScopePathFact(
            ShellPolicyPathBaseKind.Real,
            BaseIndex: 0,
            "C:/work",
            ShellPolicyPathResolutionState.Known,
            CreateCanonicalPath(@"C:\work", ShellPathStyle.Windows));

        var resolved = source.Resolve(realScope, ShellPathStyle.Windows);

        Assert.Contains(
            resolved.Facts,
            fact => fact.Source.Origin == ShellPolicyPathOrigin.Redirect
                    && fact.Source.DomainKind == ShellPolicyPathDomainKind.Unknown
                    && fact.State == ShellPolicyPathResolutionState.UnknownDynamic);
        Assert.DoesNotContain(
            resolved.Facts,
            static fact => fact.State == ShellPolicyPathResolutionState.InvalidKnownValue);
    }

    [Fact]
    public void Path_facts_retain_redirect_mode_and_domain_kind()
    {
        var environment = ShellExecutionEnvironment.CreateBash(ShellPlatform.Linux);
        var policy = new ShellCommandPolicy(environment);
        var occurrence = Assert.Single(policy.Analyze("cat input.txt > output.txt", "/work").Commands);
        var source = ShellPolicyOccurrencePathFacts.Create(occurrence);
        var realScope = new ShellPolicyScopePathFact(
            ShellPolicyPathBaseKind.Real,
            BaseIndex: 0,
            "/work",
            ShellPolicyPathResolutionState.Known,
            CreateCanonicalPath("/work", ShellPathStyle.Posix));

        var resolved = source.Resolve(realScope, ShellPathStyle.Posix);
        var redirect = Assert.Single(
            resolved.Facts,
            static fact => fact.Source.Origin == ShellPolicyPathOrigin.Redirect);

        Assert.Equal(FileRedirectMode.Output, redirect.Source.RedirectMode);
        Assert.True(redirect.Source.RedirectIsComplete);
        Assert.Equal(ShellPolicyPathDomainKind.Exact, redirect.Source.DomainKind);
        Assert.Equal(ShellPolicyPathResolutionState.Known, redirect.State);
        Assert.Equal("/work/output.txt", Assert.Single(redirect.Paths).Value);
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
    public async Task Terminal_stage_prompts_with_only_the_uncovered_candidates()
    {
        var evaluation = CreateEvaluation(
            BashCandidate("git status"),
            BashCandidate("git push"));
        var covered = evaluation.Candidates[0];
        var uncovered = evaluation.Candidates[1];
        Assert.IsType<ShellPolicyStageResult.Continue>(evaluation.Cover(
            covered,
            ShellCoverageKind.Session,
            ShellPolicyReason.SessionGrant,
            ShellScopeRelation.ThisChat));
        var context = TestToolExecutionContext.CreateBound(
            "signalr/shell-policy-terminal-prompt",
            "/work/session",
            TrustAudience.Personal);

        var result = Assert.IsType<ShellPolicyStageResult.Complete>(
            await ShellPolicyPipeline.RunAsync(
                evaluation,
                [ShellPolicyTerminalStage.Complete(context)],
                TestContext.Current.CancellationToken));

        Assert.Equal(ToolAuthorizationOutcome.RequiresApproval, result.Decision.Outcome);
        var prompt = Assert.IsType<ToolApprovalContext>(result.Decision.ApprovalContext);
        Assert.Equal([uncovered.Candidate], prompt.Candidates);
        Assert.Equal(ShellCoverageKind.Session, evaluation.CoverageFor(covered.Id).Kind);
        Assert.Equal(ShellCoverageKind.Uncovered, evaluation.CoverageFor(uncovered.Id).Kind);
    }

    [Fact]
    public async Task Terminal_stage_preserves_one_time_allow_precedence()
    {
        var evaluation = CreateEvaluationWithExactOneTime(
            isMessy: false,
            BashCandidate("git status"));
        var context = TestToolExecutionContext.CreateBound(
            "signalr/shell-policy-terminal-once",
            "/work/session",
            TrustAudience.Personal);

        var result = Assert.IsType<ShellPolicyStageResult.Complete>(
            await ShellPolicyPipeline.RunAsync(
                evaluation,
                [
                    ShellPolicyGrantStages.ExactOneTime(
                        new ToolName(ShellTool.ToolName),
                        context.SessionDirectory),
                    ShellPolicyTerminalStage.Complete(context)
                ],
                TestContext.Current.CancellationToken));

        Assert.Equal(ToolAuthorizationOutcome.Allowed, result.Decision.Outcome);
        Assert.Equal(ToolAllowReason.OneTimeApproval, result.Decision.AllowReason);
    }

    [Fact]
    public async Task Terminal_stage_records_a_complete_stored_match_decision()
    {
        var evaluation = CreateEvaluation(BashCandidate("git status"));
        var candidate = Assert.Single(evaluation.Candidates);
        var context = TestToolExecutionContext.CreateBound(
            "signalr/shell-policy-terminal-stored",
            "/work/session",
            TrustAudience.Personal);
        var service = new FixedShellApprovalService(_ => new ShellApprovalMatchResult(
            new PersistentGrantStoreStatus.Ready(),
            [
                new ShellGrantCandidateMatch(
                    candidate.Id,
                    new ToolApprovalMatch(candidate.Candidate.Verb, "session", "this chat"),
                    ShellCoverageKind.Session,
                    NearMisses: [])
            ]));

        var result = Assert.IsType<ShellPolicyStageResult.Complete>(
            await ShellPolicyPipeline.RunAsync(
                evaluation,
                [
                    ShellPolicyGrantStages.ActorEvidence(
                        new ShellApprovalEvidenceAdapter(service),
                        (ToolApprovalSessionId)"signalr/shell-policy-terminal-stored",
                        TrustAudience.Personal,
                        new ToolName(ShellTool.ToolName)),
                    ShellPolicyTerminalStage.Complete(context)
                ],
                TestContext.Current.CancellationToken));

        Assert.Equal(ToolAuthorizationOutcome.Allowed, result.Decision.Outcome);
        Assert.Equal(ToolAllowReason.StoredApproval, result.Decision.AllowReason);
        Assert.Equal("PreviouslyApproved", context.Approval.AppliedDecision);
        Assert.Equal("git status [session: this chat]", context.Approval.AppliedPattern);
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
        string cwd = "/work",
        params ApprovalCandidate[] candidates)
    {
        var environment = ShellExecutionEnvironment.CreateBash(ShellPlatform.Linux);
        var approvalContext = new ToolApprovalContext(
            ShellTool.ToolName,
            "shell command",
            candidates.Select(static candidate => candidate.Verb).ToArray(),
            candidates.Select(static candidate => candidate.Verb).ToArray(),
            [],
            Cwd: cwd,
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
        => CreateReviewedSafeEvaluation(
            command,
            interactive,
            ShellExecutionEnvironment.CreateBash(ShellPlatform.Linux),
            ApprovalShell.Bash,
            "/work",
            safeVerbs);

    private static (
        ShellPolicyEvaluation Evaluation,
        ToolAccessPolicy Policy,
        ToolExecutionContext Context) CreateReviewedSafeEvaluation(
        string command,
        bool interactive,
        ShellExecutionEnvironment environment,
        ApprovalShell approvalShell,
        string workingDirectory,
        params string[] safeVerbs)
    {
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
            safeVerbs: SafeVerbList.FromVerbs(approvalShell, safeVerbs));
        var matcher = new ShellApprovalMatcher(environment);
        var arguments = ToolInput.Create(
            "Command",
            command,
            "WorkingDirectory",
            workingDirectory);
        var execution = commandPolicy.Analyze(command, workingDirectory);
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
            Cwd: workingDirectory,
            approval.IsMessy,
            approval.Candidates);
        var context = TestToolExecutionContext.CreateBound(
            "signalr/shell-policy-reviewed-safe-stage",
            environment.PathStyle == ShellPathStyle.Windows
                ? $@"{workingDirectory}\session"
                : $"{workingDirectory}/session",
            new TestToolExecutionContextOptions
            {
                Audience = TrustAudience.Personal,
                ProjectDirectory = workingDirectory,
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

    private static CanonicalShellPath CreateCanonicalPath(
        string value,
        ShellPathStyle pathStyle)
    {
        Assert.True(CanonicalShellPath.TryCreate(value, pathStyle, out var path));
        return path;
    }

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
