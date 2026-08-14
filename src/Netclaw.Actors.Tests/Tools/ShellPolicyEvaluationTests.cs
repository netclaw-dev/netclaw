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
    public async Task Syntax_stage_prompts_before_a_later_stage_for_untyped_candidates()
    {
        var candidate = new ApprovalCandidate("git status", "/work");
        var evaluation = CreateEvaluation(candidate);
        var laterStageVisited = false;
        TestStage[] stages =
        [
            SyntaxStage(ShellTool.ToolName),
            (_, _) =>
            {
                laterStageVisited = true;
                return ValueTask.FromResult<ShellPolicyStageResult>(new ShellPolicyStageResult.Continue());
            }
        ];

        var result = Assert.IsType<ShellPolicyStageResult.Complete>(
            await RunStagesAsync(evaluation, stages, TestContext.Current.CancellationToken));

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
            await RunStagesAsync(
                evaluation,
                [SyntaxStage(ShellTool.ToolName)],
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
        TestStage[] stages =
        [
            SyntaxStage(ShellTool.ToolName),
            (_, _) =>
            {
                laterStageVisited = true;
                return ValueTask.FromResult<ShellPolicyStageResult>(new ShellPolicyStageResult.Continue());
            }
        ];

        var result = Assert.IsType<ShellPolicyStageResult.Fault>(
            await RunStagesAsync(evaluation, stages, TestContext.Current.CancellationToken));

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
        TestStage[] stages =
        [
            ProtectedCausalPathsStage(policy),
            (_, _) =>
            {
                laterStageVisited = true;
                return ValueTask.FromResult<ShellPolicyStageResult>(new ShellPolicyStageResult.Continue());
            }
        ];

        var result = Assert.IsType<ShellPolicyStageResult.Complete>(
            await RunStagesAsync(evaluation, stages, TestContext.Current.CancellationToken));

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
            await RunStagesAsync(
                evaluation,
                [ProtectedCausalPathsStage(policy)],
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
            @"C:\work",
            ShellPolicyPathResolutionState.Known,
            CreateCanonicalPath(@"C:\work", ShellPathStyle.Windows));
        var source = Assert.Single(
            ShellPolicyOccurrencePathFacts.Create(occurrence)
                .Resolve(
                    resolutionBase,
                    ShellPathStyle.Windows,
                    ApprovalShell.PowerShell)
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

        var result = await RunStagesAsync(
            evaluation,
            [CausalDirectoriesStage(policy, ShellTool.ToolName)],
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
                await RunStagesAsync(
                    evaluation,
                    [CausalDirectoriesStage(policy, ShellTool.ToolName)],
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

        var result = await RunStagesAsync(
            evaluation,
            [ActorEvidenceStage(
                new ShellApprovalEvidenceAdapter(service),
                (ToolApprovalSessionId)"signalr/shell-policy-actor-stage",
                TrustAudience.Personal,
                new ToolName(ShellTool.ToolName))],
            TestContext.Current.CancellationToken);

        Assert.IsType<ShellPolicyStageResult.Continue>(result);
        Assert.Equal(1, service.RequestCount);
        Assert.Equal(ShellPolicyCoverageSource.Session, evaluation.CoverageFor(candidate.Id));
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
        TestStage[] stages =
        [
            ActorEvidenceStage(
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
            await RunStagesAsync(
                evaluation,
                stages,
                TestContext.Current.CancellationToken));

        Assert.Equal(ShellPolicyFault.InvalidActorEvidence, result.Reason);
        Assert.Equal(1, service.RequestCount);
        Assert.False(laterStageVisited);
        Assert.Null(evaluation.GrantEvidence);
        Assert.Equal(ShellPolicyCoverageSource.Uncovered, evaluation.CoverageFor(candidate.Id));
        Assert.Equal("internal_policy_failure", evaluation.TerminalDecision?.DenyReason);
    }

    [Fact]
    public void Validated_actor_evidence_remains_bound_to_its_projection()
    {
        var source = CreateEvaluation(BashCandidate("git status"));
        var target = CreateEvaluation(BashCandidate("git status"));
        var candidate = Assert.Single(source.Projection.GrantCandidates);
        var actorResult = new ShellApprovalMatchResult(
            new PersistentGrantStoreStatus.Ready(),
            [
                new ShellGrantCandidateMatch(
                    candidate.Id,
                    new ToolApprovalMatch(candidate.Candidate.Verb, "session", "this chat"),
                    ShellCoverageKind.Session,
                    NearMisses: [])
            ]);
        Assert.True(ValidatedShellGrantEvidence.TryCreate(
            actorResult,
            source.Projection.GrantCandidates,
            source.Projection.ApprovalContext.Cwd,
            out var evidence));

        var result = Assert.IsType<ShellPolicyStageResult.Fault>(
            target.ApplyActorEvidence(Assert.IsType<ValidatedShellGrantEvidence>(evidence)));

        Assert.Equal(ShellPolicyFault.InvalidActorEvidence, result.Reason);
        Assert.Equal("internal_policy_failure", target.TerminalDecision?.DenyReason);
        Assert.Equal(
            ShellPolicyCoverageSource.Uncovered,
            target.CoverageFor(Assert.Single(target.Candidates).Id));
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

        var result = await RunStagesAsync(
            evaluation,
            [
                ActorEvidenceStage(
                    adapter,
                    (ToolApprovalSessionId)"signalr/shell-policy-trace-order",
                    TrustAudience.Personal,
                    new ToolName(ShellTool.ToolName)),
                ApprovalExemptSideEffectsStage(adapter.IsAvailable)
            ],
            TestContext.Current.CancellationToken);

        Assert.IsType<ShellPolicyStageResult.Continue>(result);
        Assert.Equal(ShellPolicyCoverageSource.Uncovered, evaluation.CoverageFor(grantCandidate.Id));
        Assert.Equal(
            ShellPolicyCoverageSource.ApprovalExemptSideEffect,
            evaluation.CoverageFor(sideEffect.Id));
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
    [InlineData(false, nameof(ShellPolicyCoverageSource.Uncovered))]
    [InlineData(true, nameof(ShellPolicyCoverageSource.ApprovalExemptSideEffect))]
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

        var result = await RunStagesAsync(
            evaluation,
            [
                ActorEvidenceStage(
                    adapter,
                    sessionId: null,
                    TrustAudience.Personal,
                    new ToolName(ShellTool.ToolName)),
                ApprovalExemptSideEffectsStage(adapter.IsAvailable)
            ],
            TestContext.Current.CancellationToken);

        Assert.IsType<ShellPolicyStageResult.Continue>(result);
        Assert.NotNull(evaluation.GrantEvidence);
        Assert.Equal(0, service.RequestCount);
        Assert.Equal(
            Enum.Parse<ShellPolicyCoverageSource>(expectedCoverageName),
            evaluation.CoverageFor(candidate.Id));
    }

    [Theory]
    [InlineData(false, nameof(ShellPolicyCoverageSource.Uncovered))]
    [InlineData(true, nameof(ShellPolicyCoverageSource.ReviewedSafeReal))]
    public async Task Reviewed_safe_real_scope_stage_requires_interactive_approval(
        bool interactive,
        string expectedCoverageName)
    {
        var (evaluation, policy, context) = CreateReviewedSafeEvaluation(
            "head README.md",
            interactive,
            "head");
        var candidate = Assert.Single(evaluation.Candidates);

        var result = await RunStagesAsync(
            evaluation,
            [RealScopeStage(policy, context.Invocation)],
            TestContext.Current.CancellationToken);

        Assert.IsType<ShellPolicyStageResult.Continue>(result);
        Assert.Equal(
            Enum.Parse<ShellPolicyCoverageSource>(expectedCoverageName),
            evaluation.CoverageFor(candidate.Id));
    }

    [Theory]
    [InlineData("grep -f ./patterns ./data.txt", true)]
    [InlineData("du -sh ./*", true)]
    [InlineData("tr -d '\\n'", true)]
    [InlineData("tool -d '\\n'", false)]
    [InlineData("tr *.txt x", false)]
    [InlineData("tr -d '\\n' > /external/out", false)]
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

        var result = await RunStagesAsync(
            evaluation,
            [RealScopeStage(policy, context.Invocation)],
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
    public void Unproved_non_file_semantics_keep_reviewed_safe_policy_strict()
    {
        var (evaluation, policy, context) = CreateReviewedSafeEvaluation(
            "tr -d '\\n'",
            interactive: true,
            "tr");
        var candidate = Assert.Single(evaluation.Candidates);
        var facts = evaluation.Projection.PathFacts.For(candidate.Id);
        var real = Assert.IsType<ShellPolicyResolvedPathView>(facts.Real);
        var invalid = facts with
        {
            Real = real with { HasUnprovedNonFileSystemSemantics = true }
        };

        Assert.False(policy.IsReviewedSafeCandidate(
            candidate.Candidate,
            invalid,
            context.Invocation));
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

        var result = await RunStagesAsync(
            evaluation,
            [RealScopeStage(policy, context.Invocation)],
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

        var beforeCoverage = await RunStagesAsync(
            evaluation,
            [IntentScopeStage(policy, context.Invocation)],
            TestContext.Current.CancellationToken);

        Assert.IsType<ShellPolicyStageResult.Continue>(beforeCoverage);
        Assert.Equal(ShellPolicyCoverageSource.Uncovered, evaluation.CoverageFor(consumer.Id));

        foreach (var prerequisiteId in consumer.IntentPrerequisites)
        {
            var prerequisite = evaluation.Candidates[prerequisiteId.Value];
            Assert.IsType<ShellPolicyStageResult.Continue>(evaluation.Cover(
                prerequisite,
                ShellPolicyCoverageSource.Session));
        }

        var afterCoverage = await RunStagesAsync(
            evaluation,
            [
                RealScopeStage(policy, context.Invocation),
                IntentScopeStage(policy, context.Invocation)
            ],
            TestContext.Current.CancellationToken);

        Assert.IsType<ShellPolicyStageResult.Continue>(afterCoverage);
        Assert.Equal(
            ShellPolicyCoverageSource.ReviewedSafeIntent,
            evaluation.CoverageFor(consumer.Id));
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

        var result = await RunStagesAsync(
            evaluation,
            [ExactOneTimeStage(
                new ToolName(ShellTool.ToolName),
                "/work/session")],
            TestContext.Current.CancellationToken);

        Assert.IsType<ShellPolicyStageResult.Continue>(result);
        Assert.True(evaluation.HasOneTimeCoverage);
        Assert.Equal(ShellPolicyCoverageSource.OneTime, evaluation.CoverageFor(candidate.Id));
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

        var result = await RunStagesAsync(
            evaluation,
            [ExactOneTimeStage(
                new ToolName("different_tool"),
                context.SessionDirectory)],
            TestContext.Current.CancellationToken);

        Assert.IsType<ShellPolicyStageResult.Continue>(result);
        Assert.False(evaluation.HasOneTimeCoverage);
        Assert.Equal(ShellPolicyCoverageSource.Uncovered, evaluation.CoverageFor(candidate.Id));

        var oneTimeContext = evaluation.GetUncoveredApprovalContext(context.SessionDirectory);
        var terminal = Assert.IsType<ShellPolicyStageResult.Complete>(
            await RunStagesAsync(
                evaluation,
                [CompleteStage(context)],
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
            ShellPolicyCoverageSource.Session));

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
                    && fact.Source.Domain is ShellValueDomain.Exact
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
            resolutionBase,
            ShellPolicyPathResolutionState.Known,
            CreateCanonicalPath(resolutionBase, environment.PathStyle));

        var facts = ShellPolicyOccurrencePathFacts.Create(occurrence)
            .Resolve(
                scope,
                environment.PathStyle,
                windowsStyle ? ApprovalShell.PowerShell : ApprovalShell.Bash);

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
            "C:/work",
            ShellPolicyPathResolutionState.Known,
            CreateCanonicalPath(@"C:\work", ShellPathStyle.Windows));

        var resolved = source.Resolve(
            realScope,
            ShellPathStyle.Windows,
            ApprovalShell.PowerShell);

        Assert.Contains(
            resolved.Facts,
            fact => fact.Source.Origin == ShellPolicyPathOrigin.Redirect
                    && fact.Source.Domain is ShellValueDomain.Unknown
                    && fact.State == ShellPolicyPathResolutionState.UnknownDynamic);
        Assert.DoesNotContain(
            resolved.Facts,
            static fact => fact.State == ShellPolicyPathResolutionState.InvalidKnownValue);
    }

    [Fact]
    public void Path_facts_retain_redirect_mode_and_domain()
    {
        var environment = ShellExecutionEnvironment.CreateBash(ShellPlatform.Linux);
        var policy = new ShellCommandPolicy(environment);
        var occurrence = Assert.Single(policy.Analyze("cat input.txt > output.txt", "/work").Commands);
        var source = ShellPolicyOccurrencePathFacts.Create(occurrence);
        var realScope = new ShellPolicyScopePathFact(
            "/work",
            ShellPolicyPathResolutionState.Known,
            CreateCanonicalPath("/work", ShellPathStyle.Posix));

        var resolved = source.Resolve(
            realScope,
            ShellPathStyle.Posix,
            ApprovalShell.Bash);
        var redirect = Assert.Single(
            resolved.Facts,
            static fact => fact.Source.Origin == ShellPolicyPathOrigin.Redirect);

        Assert.Equal(FileRedirectMode.Output, redirect.Source.RedirectMode);
        Assert.True(redirect.Source.RedirectIsComplete);
        Assert.IsType<ShellValueDomain.Exact>(redirect.Source.Domain);
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

        var result = await RunStagesAsync(
            evaluation,
            [
                ActorEvidenceStage(
                    new ShellApprovalEvidenceAdapter(service),
                    (ToolApprovalSessionId)"signalr/shell-policy-store-stage",
                    TrustAudience.Personal,
                    new ToolName(ShellTool.ToolName)),
                PersistentStoreAvailabilityStage()
            ],
            TestContext.Current.CancellationToken);

        Assert.Equal(1, service.RequestCount);
        if (!expectsDeny)
        {
            Assert.IsType<ShellPolicyStageResult.Continue>(result);
            Assert.Null(evaluation.TerminalDecision);
            Assert.Equal(ShellPolicyCoverageSource.Session, evaluation.CoverageFor(candidate.Id));
        }
        else
        {
            var complete = Assert.IsType<ShellPolicyStageResult.Complete>(result);
            Assert.Equal(ToolAuthorizationOutcome.Denied, complete.Decision.Outcome);
            Assert.Equal("approval_store_unavailable", complete.Decision.DenyReason);
            Assert.Equal(ShellPolicyCoverageSource.Uncovered, evaluation.CoverageFor(candidate.Id));
        }
    }

    [Fact]
    public async Task Persistent_store_stage_rejects_missing_actor_evidence()
    {
        var evaluation = CreateEvaluation(BashCandidate("git status"));

        var result = Assert.IsType<ShellPolicyStageResult.Fault>(
            await RunStagesAsync(
                evaluation,
                [PersistentStoreAvailabilityStage()],
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
            ShellPolicyCoverageSource.Session));
        var context = TestToolExecutionContext.CreateBound(
            "signalr/shell-policy-terminal-prompt",
            "/work/session",
            TrustAudience.Personal);

        var result = Assert.IsType<ShellPolicyStageResult.Complete>(
            await RunStagesAsync(
                evaluation,
                [CompleteStage(context)],
                TestContext.Current.CancellationToken));

        Assert.Equal(ToolAuthorizationOutcome.RequiresApproval, result.Decision.Outcome);
        var prompt = Assert.IsType<ToolApprovalContext>(result.Decision.ApprovalContext);
        Assert.Equal([uncovered.Candidate], prompt.Candidates);
        Assert.Equal(ShellPolicyCoverageSource.Session, evaluation.CoverageFor(covered.Id));
        Assert.Equal(ShellPolicyCoverageSource.Uncovered, evaluation.CoverageFor(uncovered.Id));
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
            await RunStagesAsync(
                evaluation,
                [
                    ExactOneTimeStage(
                        new ToolName(ShellTool.ToolName),
                        context.SessionDirectory),
                    CompleteStage(context)
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
            await RunStagesAsync(
                evaluation,
                [
                    ActorEvidenceStage(
                        new ShellApprovalEvidenceAdapter(service),
                        (ToolApprovalSessionId)"signalr/shell-policy-terminal-stored",
                        TrustAudience.Personal,
                        new ToolName(ShellTool.ToolName)),
                    CompleteStage(context)
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
            ShellPolicyCoverageSource.PersistentGlobal,
            grantTimestamp);

        Assert.IsType<ShellPolicyStageResult.Continue>(result);
        Assert.Equal(ShellPolicyCoverageSource.PersistentGlobal, evaluation.CoverageFor(candidate.Id));

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
            ShellPolicyCoverageSource.Session));

        var duplicate = Assert.IsType<ShellPolicyStageResult.Fault>(evaluation.Cover(
            candidate,
            ShellPolicyCoverageSource.PersistentGlobal));

        Assert.Equal(ShellPolicyFault.CoverageAlreadyAssigned, duplicate.Reason);
        Assert.Equal(ShellPolicyCoverageSource.Session, evaluation.CoverageFor(candidate.Id));
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
            ShellPolicyCoverageSource.Session));

        Assert.Equal(ShellPolicyFault.CandidateFactsChanged, result.Reason);
        Assert.Equal(ShellPolicyCoverageSource.Uncovered, evaluation.CoverageFor(candidate.Id));
        Assert.Equal(ToolAuthorizationOutcome.Denied, evaluation.TerminalDecision?.Outcome);
        Assert.Equal(ShellPolicyFault.CandidateFactsChanged, evaluation.TerminalFault);
        Assert.NotNull(evaluation.CompletedTrace);
        Assert.Single(evaluation.CompletedTrace.Rows);

        var later = Assert.IsType<ShellPolicyStageResult.Complete>(evaluation.Cover(
            candidate,
            ShellPolicyCoverageSource.Session));
        Assert.Same(evaluation.TerminalDecision, later.Decision);
        Assert.Equal(ShellPolicyCoverageSource.Uncovered, evaluation.CoverageFor(candidate.Id));
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
            ShellPolicyCoverageSource.Session));

        Assert.Equal(ShellPolicyFault.InvalidCandidateId, result.Reason);
        Assert.Single(evaluation.UncoveredCandidates);
        Assert.Equal(ToolAuthorizationOutcome.Denied, evaluation.TerminalDecision?.Outcome);
    }

    [Fact]
    public void Invalid_coverage_enum_fails_closed()
    {
        var evaluation = CreateEvaluation(BashCandidate("git status"));
        var candidate = Assert.Single(evaluation.Candidates);

        var result = Assert.IsType<ShellPolicyStageResult.Fault>(evaluation.Cover(
            candidate,
            (ShellPolicyCoverageSource)999));

        Assert.Equal(ShellPolicyFault.InvalidCoverage, result.Reason);
        Assert.Equal(ShellPolicyCoverageSource.Uncovered, evaluation.CoverageFor(candidate.Id));
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
            ShellPolicyCoverageSource.Session,
            new DateTimeOffset(2026, 8, 14, 0, 0, 0, TimeSpan.Zero)));

        Assert.Equal(ShellPolicyFault.InvalidCoverage, result.Reason);
        Assert.Equal(ShellPolicyCoverageSource.Uncovered, evaluation.CoverageFor(candidate.Id));
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
            ShellPolicyCoverageSource.Session));

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
            ShellPolicyCoverageSource.Session));
        var allow = ToolAuthorizationDecision.Allow(ToolAllowReason.StoredApproval);
        Assert.IsType<ShellPolicyStageResult.Complete>(evaluation.Complete(allow));

        var result = Assert.IsType<ShellPolicyStageResult.Complete>(evaluation.Cover(
            candidate,
            ShellPolicyCoverageSource.PersistentGlobal));

        Assert.Same(allow, result.Decision);
        Assert.Same(allow, evaluation.TerminalDecision);
        Assert.Equal(ShellPolicyCoverageSource.Session, evaluation.CoverageFor(candidate.Id));
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

    private delegate ValueTask<ShellPolicyStageResult> TestStage(
        ShellPolicyEvaluation evaluation,
        CancellationToken cancellationToken);

    private static async ValueTask<ShellPolicyStageResult> RunStagesAsync(
        ShellPolicyEvaluation evaluation,
        IReadOnlyList<TestStage> stages,
        CancellationToken cancellationToken)
    {
        foreach (var stage in stages)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var result = await stage(evaluation, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if (!evaluation.ApplyStageResult(result))
                return result;
        }

        return new ShellPolicyStageResult.Continue();
    }

    private static TestStage SyntaxStage(string toolName)
        => (evaluation, _) => ValueTask.FromResult(
            ShellPolicyInitialStages.Syntax(evaluation, toolName));

    private static TestStage ProtectedCausalPathsStage(ToolAccessPolicy policy)
        => (evaluation, _) => ValueTask.FromResult(
            ShellPolicyInitialStages.ProtectedCausalPaths(evaluation, policy));

    private static TestStage CausalDirectoriesStage(ToolAccessPolicy policy, string toolName)
        => (evaluation, _) => ValueTask.FromResult(
            ShellPolicyInitialStages.CausalDirectories(evaluation, policy, toolName));

    private static TestStage ActorEvidenceStage(
        ShellApprovalEvidenceAdapter approvalEvidence,
        ToolApprovalSessionId? sessionId,
        TrustAudience audience,
        ToolName toolName)
        => (evaluation, cancellationToken) => ShellPolicyGrantStages.ActorEvidenceAsync(
            evaluation,
            approvalEvidence,
            sessionId,
            audience,
            toolName,
            cancellationToken);

    private static TestStage ApprovalExemptSideEffectsStage(bool approvalEvidenceAvailable)
        => (evaluation, _) => ValueTask.FromResult(
            ShellPolicyGrantStages.ApprovalExemptSideEffects(
                evaluation,
                approvalEvidenceAvailable));

    private static TestStage RealScopeStage(
        ToolAccessPolicy policy,
        ToolInvocationContext invocation)
        => (evaluation, _) => ValueTask.FromResult(
            ShellPolicyReviewedSafeStages.RealScope(evaluation, policy, invocation));

    private static TestStage IntentScopeStage(
        ToolAccessPolicy policy,
        ToolInvocationContext invocation)
        => (evaluation, _) => ValueTask.FromResult(
            ShellPolicyReviewedSafeStages.IntentScope(evaluation, policy, invocation));

    private static TestStage ExactOneTimeStage(ToolName toolName, string? sessionDirectory)
        => (evaluation, _) => ValueTask.FromResult(
            ShellPolicyGrantStages.ExactOneTime(evaluation, toolName, sessionDirectory));

    private static TestStage PersistentStoreAvailabilityStage()
        => static (evaluation, _) => ValueTask.FromResult(
            ShellPolicyGrantStages.PersistentStoreAvailability(evaluation));

    private static TestStage CompleteStage(ToolExecutionContext context)
        => (evaluation, _) => ValueTask.FromResult(
            ShellPolicyTerminalStage.Complete(evaluation, context));

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
