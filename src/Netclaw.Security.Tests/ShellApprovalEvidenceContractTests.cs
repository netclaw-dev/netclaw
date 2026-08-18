// -----------------------------------------------------------------------
// <copyright file="ShellApprovalEvidenceContractTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Netclaw.Configuration;
using Netclaw.Tools;
using ShellSyntaxTree;
using Xunit;

namespace Netclaw.Security.Tests;

public sealed partial class ShellApprovalEvidenceContractTests
{
    private const string ApprovalMatrixFile = "approval-matrix.json";
    private const string PolicyFixturesFile = "netclaw-policy-fixtures.json";
    private const string PostMergeHarvestFile = "post-1890-approval-harvest.json";
    private const string PostSwapHarvestFile = "post-1925-binary-swap-approval-harvest.json";
    private const string ExtendedPostSwapHarvestFile = "post-1925-extended-approval-harvest.json";
    private const string Post1952HarvestFile = "post-1952-live-approval-harvest.json";
    private const string FreshSessionHarvestFile =
        "post-265d606f-fresh-session-approval-harvest.json";
    private const string FreshSessionPolicyFixturesFile = "fresh-session-policy-fixtures.json";
    private const string FreshSessionEvalBaselineFile =
        "pre-guidance-fresh-session-eval-baseline.json";
    private const string FreshSessionEvalResultsFile =
        "post-guidance-fresh-session-eval-results.json";
    private const string ApprovalMatrixSha256 =
        "0169105efe87b345d9a82d777ef86909e31fa81a5255cc0cc30f32fbe4d0d6b0";
    private const string LiveRegressionCasesSha256 =
        "684b89f8e01f6abc8d4b9cff49c1e1ab16d3df9cd6aaf028e2aa0822509c421a";
    private const string FreshSessionHarvestSha256 =
        "4a6acc38746dd23df75e6a95fa4fa84d43ae74a35ea3ca6dd17a8dfd3bc3b511";
    private const string FreshSessionPolicyFixturesSha256 =
        "d6cb08dd3ca0f81ada828f8b45426bfa7bd6c4cfab99128798e51fa5ad759184";
    private const string FreshSessionEvalBaselineSha256 =
        "be1c2fe0fc646f4692da75b0d5398fb4f8c3c5ea2707625266915b8d2e6cd31e";
    private const string FreshSessionEvalResultsSha256 =
        "f728ba445e16e02b24c191b336f20542109ce0992dcfd954f87a4d77832bee6f";

    [Fact]
    public void Fresh_session_eval_baseline_separates_completion_from_approval_friction()
    {
        var bytes = File.ReadAllBytes(EvidencePath(FreshSessionEvalBaselineFile));
        var baseline = DeserializeFreshSessionEvalBaseline(bytes);

        Assert.Equal(FreshSessionEvalBaselineSha256, ComputeSha256(bytes));
        Assert.DoesNotContain((byte)'\r', bytes);
        Assert.Equal(1, baseline.SchemaVersion);
        Assert.Equal("265d606f318205e8a4fdd12b479882f774b6d608", baseline.Runtime.Commit);
        Assert.Null(baseline.Runtime.BaseCommit);
        Assert.Equal("deepseek-v4-flash", baseline.Runtime.Model);
        Assert.Equal(5, baseline.Runtime.RunsPerCase);
        Assert.False(baseline.Runtime.InteractiveApprovalAvailable);
        Assert.Null(baseline.Runtime.ExpectedBoundaryDefinition);
        Assert.Null(baseline.Summary);
        Assert.Equal(
            Enumerable.Range(1, 8).Select(number => $"B{number:00}"),
            baseline.Cases.Select(item => item.Id));
        Assert.All(baseline.Cases, item =>
        {
            Assert.Equal(5, item.Runs);
            Assert.InRange(item.BehaviorPassCount, 0, item.Runs);
            Assert.InRange(item.TaskCompletionCount, 0, item.Runs);
            Assert.Equal(5, item.LlmRequestCount);
            Assert.All(item.ParentToolCalls.Values, count => Assert.True(count >= 0));
            Assert.All(item.ChildToolCalls.Values, count => Assert.True(count >= 0));
            Assert.False(string.IsNullOrWhiteSpace(item.ExpectedIntervention));
            Assert.Null(item.BaselineComparison);
        });
        Assert.Equal(16, baseline.Cases.Sum(item => item.BehaviorPassCount));
        Assert.Equal(35, baseline.Cases.Sum(item => item.TaskCompletionCount));
        Assert.Equal(40, baseline.Cases.Sum(item => item.LlmRequestCount));
        Assert.Equal(32, baseline.Cases.Sum(item => item.ApprovalPromptEquivalentCount));
        Assert.Equal(6, baseline.Cases.Sum(item => item.TrustZoneHardDenyCount));
        Assert.Equal(17, baseline.Cases.Sum(item => item.SuccessfulShellCallCount));
        Assert.Equal(4, baseline.Cases.Count(item =>
            item.Classification == "AgentAlignmentDebt"));
        Assert.Equal(3, baseline.Cases.Count(item =>
            item.Classification == "BaselineGuardrail"));
        Assert.Single(baseline.Cases, item => item.Classification == "ExpectedApproval");

        var child = Assert.Single(baseline.Cases, item => item.Id == "B03");
        Assert.Equal(6, child.ChildAttemptCount);
        Assert.Equal(3, child.ChildFailureCount);
        Assert.Equal(4, child.ChildProjectDeclarationCount);
        Assert.Equal(9, child.ChildToolCalls["shell_execute"]);

        var deliberateTransition = Assert.Single(baseline.Cases, item => item.Id == "B08");
        Assert.Equal(0, deliberateTransition.TaskCompletionCount);
        Assert.Equal(12, deliberateTransition.ApprovalPromptEquivalentCount);
        Assert.Equal(6, deliberateTransition.TrustZoneHardDenyCount);
    }

    [Theory]
    [InlineData("\"behaviorPassCount\": 0", "\"behaviorPassCount\": 5")]
    [InlineData("\"approvalPromptEquivalentCount\": 7", "\"approvalPromptEquivalentCount\": 0")]
    [InlineData("\"childProjectDeclarationCount\": 4", "\"childProjectDeclarationCount\": 5")]
    [InlineData("\"classification\": \"ExpectedApproval\"", "\"classification\": \"AgentAlignmentDebt\"")]
    public void Fresh_session_eval_digest_detects_measurement_mutation(
        string original,
        string replacement)
    {
        var json = File.ReadAllText(EvidencePath(FreshSessionEvalBaselineFile));
        var mutated = json.Replace(original, replacement, StringComparison.Ordinal);

        Assert.NotEqual(json, mutated);
        Assert.NotEqual(
            ComputeSha256(Encoding.UTF8.GetBytes(json)),
            ComputeSha256(Encoding.UTF8.GetBytes(mutated)));
    }

    [Fact]
    public void Fresh_session_eval_results_lock_variable_outcomes_and_retained_boundaries()
    {
        var bytes = File.ReadAllBytes(EvidencePath(FreshSessionEvalResultsFile));
        var results = DeserializeFreshSessionEvalBaseline(bytes);

        Assert.Equal(FreshSessionEvalResultsSha256, ComputeSha256(bytes));
        Assert.DoesNotContain((byte)'\r', bytes);
        Assert.Equal(1, results.SchemaVersion);
        Assert.Equal("265d606f318205e8a4fdd12b479882f774b6d608", results.Runtime.BaseCommit);
        Assert.Null(results.Runtime.Commit);
        Assert.Equal("deepseek-v4-flash", results.Runtime.Model);
        Assert.Equal(5, results.Runtime.RunsPerCase);
        Assert.False(results.Runtime.InteractiveApprovalAvailable);
        Assert.False(string.IsNullOrWhiteSpace(results.Runtime.ExpectedBoundaryDefinition));
        Assert.Equal(
            Enumerable.Range(1, 8).Select(number => $"B{number:00}"),
            results.Cases.Select(item => item.Id));
        Assert.All(results.Cases, item =>
        {
            Assert.Equal(5, item.Runs);
            Assert.False(string.IsNullOrWhiteSpace(item.BaselineComparison));
            Assert.Null(item.ExpectedIntervention);
        });

        var summary = Assert.IsType<FreshSessionEvalSummary>(results.Summary);
        Assert.Equal(results.Cases.Sum(item => item.BehaviorPassCount), summary.BehaviorPassCount);
        Assert.Equal(results.Cases.Sum(item => item.Runs), summary.BehaviorRunCount);
        Assert.Equal(
            results.Cases.Sum(item => item.ApprovalPromptEquivalentCount),
            summary.ApprovalPromptEquivalentCount);
        Assert.Equal(
            results.Cases.Sum(item => item.TrustZoneHardDenyCount),
            summary.TrustZoneHardDenyCount);
        Assert.Equal(17, summary.BehaviorPassCount);
        Assert.Equal(40, summary.BehaviorRunCount);
        Assert.Equal(25, summary.ApprovalPromptEquivalentCount);
        Assert.Equal(5, summary.TrustZoneHardDenyCount);
        Assert.Equal(32, summary.BaselineApprovalPromptEquivalentCount);
        Assert.Equal(6, summary.BaselineTrustZoneHardDenyCount);

        var child = Assert.Single(results.Cases, item => item.Id == "B03");
        Assert.Equal(2, child.BehaviorPassCount);
        Assert.Equal(5, child.ChildAttemptCount);
        Assert.Equal(2, child.ChildFailureCount);
        var deliberateTransition = Assert.Single(results.Cases, item => item.Id == "B08");
        Assert.Equal(0, deliberateTransition.TaskCompletionCount);
        Assert.Equal(6, deliberateTransition.ApprovalPromptEquivalentCount);
        Assert.Equal(5, deliberateTransition.TrustZoneHardDenyCount);
    }

    [Theory]
    [InlineData("\"approvalPromptEquivalentCount\": 25", "\"approvalPromptEquivalentCount\": 24")]
    [InlineData("\"behaviorPassCount\": 2", "\"behaviorPassCount\": 5")]
    [InlineData("\"childFailureCount\": 2", "\"childFailureCount\": 0")]
    [InlineData("\"taskCompletionCount\": 0", "\"taskCompletionCount\": 5")]
    public void Fresh_session_eval_results_digest_detects_measurement_mutation(
        string original,
        string replacement)
    {
        var json = File.ReadAllText(EvidencePath(FreshSessionEvalResultsFile));
        var mutated = json.Replace(original, replacement, StringComparison.Ordinal);

        Assert.NotEqual(json, mutated);
        Assert.NotEqual(
            ComputeSha256(Encoding.UTF8.GetBytes(json)),
            ComputeSha256(Encoding.UTF8.GetBytes(mutated)));
    }

    [Fact]
    public void Fresh_session_harvest_classifies_the_complete_fixed_window()
    {
        var bytes = File.ReadAllBytes(EvidencePath(FreshSessionHarvestFile));
        var harvest = DeserializeFreshSessionHarvest(bytes);

        Assert.Equal(FreshSessionHarvestSha256, ComputeSha256(bytes));
        Assert.DoesNotContain((byte)'\r', bytes);
        Assert.Equal(1, harvest.SchemaVersion);
        Assert.Equal("0.26.0-beta.4", harvest.SourceRuntime.Version);
        Assert.Equal("265d606f318205e8a4fdd12b479882f774b6d608", harvest.SourceRuntime.Commit);
        Assert.Equal(6, harvest.SourceRuntime.ParentSessionCount);
        Assert.Equal(7, harvest.SourceRuntime.ChildLogCount);
        Assert.Equal(239, harvest.SourceRuntime.ShellCallCount);
        Assert.Equal(48, harvest.SourceRuntime.ApprovalPromptCount);
        Assert.Equal(
            Enumerable.Range(1, 6).Select(number => $"S{number:00}"),
            harvest.Sessions.Select(item => item.Id));
        Assert.Equal(
            Enumerable.Range(1, 48).Select(number => $"F{number:00}"),
            harvest.PromptClassifications.Select(item => item.Id));
        Assert.Equal(
            Enumerable.Range(1, 10).Select(number => $"R{number:00}"),
            harvest.RepresentativeCases.Select(item => item.Id));
        Assert.Equal(
            harvest.SourceRuntime.ShellCallCount,
            harvest.Sessions.Sum(item => item.ParentShellCalls + item.ChildShellCalls));
        Assert.Equal(
            harvest.SourceRuntime.ChildLogCount,
            harvest.Sessions.Sum(item => item.ChildLogCount));
        Assert.Equal(
            harvest.SourceRuntime.ApprovalPromptCount,
            harvest.Sessions.Sum(item => item.ApprovalPromptCount));
        Assert.Equal(39, harvest.PromptClassifications.Count(item =>
            item.Classification == "ExpectedApproval"));
        Assert.Equal(9, harvest.PromptClassifications.Count(item =>
            item.Classification == "AgentAlignmentDebt"));
        Assert.DoesNotContain(harvest.PromptClassifications, item =>
            item.Classification is "ShellSyntaxTreeFactGap" or "NetclawPolicyDefect");
        Assert.Equal(42, harvest.PromptClassifications.Count(item => item.Source == "Parent"));
        Assert.Equal(6, harvest.PromptClassifications.Count(item => item.Source == "Subagent"));

        foreach (var session in harvest.Sessions)
        {
            var prompts = harvest.PromptClassifications
                .Where(item => item.Session == session.Id)
                .ToList();
            Assert.Equal(session.ApprovalPromptCount, prompts.Count);
            Assert.Equal(
                session.ExpectedApprovalCount,
                prompts.Count(item => item.Classification == "ExpectedApproval"));
            Assert.Equal(
                session.AgentAlignmentDebtCount,
                prompts.Count(item => item.Classification == "AgentAlignmentDebt"));
            Assert.Equal(
                session.ShellSyntaxTreeFactGapCount,
                prompts.Count(item => item.Classification == "ShellSyntaxTreeFactGap"));
            Assert.Equal(
                session.NetclawPolicyDefectCount,
                prompts.Count(item => item.Classification == "NetclawPolicyDefect"));
        }
    }

    [Fact]
    public void Fresh_session_representative_cases_preserve_links_and_shell_grammar()
    {
        var harvest = DeserializeFreshSessionHarvest(
            File.ReadAllBytes(EvidencePath(FreshSessionHarvestFile)));
        var promptIds = harvest.PromptClassifications
            .Select(item => item.Id)
            .ToHashSet(StringComparer.Ordinal);
        var analyzer = new ShellCommandAnalyzer(
            ShellExecutionEnvironment.CreateBash(ShellPlatform.Linux));
        var strictCases = new List<(string Id, string Failure, int CommandCount)>();

        foreach (var item in harvest.RepresentativeCases)
        {
            Assert.False(string.IsNullOrWhiteSpace(item.CommandShape));
            Assert.False(string.IsNullOrWhiteSpace(item.WorkingDirectory));
            Assert.False(string.IsNullOrWhiteSpace(item.Reason));
            if (item.Source.StartsWith('F'))
                Assert.Contains(item.Source, promptIds);

            var analysis = analyzer.Analyze(item.CommandShape, item.WorkingDirectory);
            if (analysis.Failure != ShellAnalysisFailure.None || analysis.Commands.Count == 0)
                strictCases.Add((item.Id, analysis.Failure.ToString(), analysis.Commands.Count));
        }

        Assert.Equal([("R07", "Unresolved", 0)], strictCases);

        Assert.Equal(8, harvest.RepresentativeCases.Count(item => item.IncludedInBaseline));
        var policyDefect = Assert.Single(harvest.RepresentativeCases, item =>
            item.Classification == "NetclawPolicyDefect");
        Assert.False(policyDefect.IncludedInBaseline);
        Assert.Equal("RequiresOneTimeApproval", policyDefect.ObservedOutcome);
        Assert.Equal("RequiresReusableApproval", policyDefect.TargetOutcome);
        var alias = Assert.Single(policyDefect.FileSystemFacts!);
        Assert.Equal("FileSymlink", alias.Kind);
        Assert.Equal("/work/project/CLAUDE.md", alias.Path);
        Assert.Equal("/work/project/AGENTS.md", alias.Target);
        var protectedPair = Assert.Single(harvest.RepresentativeCases, item =>
            item.Source == "AdversarialPair");
        Assert.Equal("Deny", protectedPair.TargetOutcome);
    }

    [Theory]
    [InlineData("\"approvalPromptCount\": 48", "\"approvalPromptCount\": 47")]
    [InlineData("\"classification\": \"AgentAlignmentDebt\"", "\"classification\": \"Changed\"")]
    [InlineData("grep -rn \\\"Mode B\\\" docs/ *.md", "grep -rn \\\"Changed\\\" docs/ *.md")]
    [InlineData("\"targetOutcome\": \"RequiresReusableApproval\"", "\"targetOutcome\": \"Allow\"")]
    [InlineData("\"target\": \"/work/project/AGENTS.md\"", "\"target\": \"/external/cache/AGENTS.md\"")]
    [InlineData("\"owner\": \"NetclawProjection\"", "\"owner\": \"ChangedOwner\"")]
    public void Fresh_session_digest_detects_security_significant_mutation(
        string original,
        string replacement)
    {
        var json = File.ReadAllText(EvidencePath(FreshSessionHarvestFile));
        var mutated = json.Replace(original, replacement, StringComparison.Ordinal);

        Assert.NotEqual(json, mutated);
        Assert.NotEqual(
            ComputeSha256(Encoding.UTF8.GetBytes(json)),
            ComputeSha256(Encoding.UTF8.GetBytes(mutated)));
    }

    [Fact]
    public void Fresh_session_harvest_contains_no_raw_runtime_identity()
    {
        var text = string.Join(
            '\n',
            File.ReadAllText(EvidencePath(FreshSessionHarvestFile)),
            File.ReadAllText(EvidencePath(FreshSessionPolicyFixturesFile)),
            File.ReadAllText(EvidencePath(FreshSessionEvalBaselineFile)),
            File.ReadAllText(EvidencePath(FreshSessionEvalResultsFile)));

        Assert.DoesNotMatch(SlackChannelPattern(), text);
        Assert.DoesNotMatch(SlackThreadPattern(), text);
        Assert.DoesNotMatch(EmailPattern(), text);
        Assert.DoesNotMatch(PrivateHomePattern(), text);
        Assert.DoesNotMatch(KnownSourceIdentityPattern(), text);
        Assert.DoesNotMatch(CallIdPattern(), text);
        Assert.DoesNotMatch(ExactSourceTimestampPattern(), text);
        Assert.DoesNotMatch(GuidPattern(), text);
        Assert.DoesNotContain("akka://", text, StringComparison.Ordinal);
        Assert.DoesNotContain("[INF]", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Fresh_session_policy_fixtures_bind_source_authority_and_trace()
    {
        var bytes = File.ReadAllBytes(EvidencePath(FreshSessionPolicyFixturesFile));
        var fixtures = DeserializeFixtures(bytes);
        var harvest = DeserializeFreshSessionHarvest(
            File.ReadAllBytes(EvidencePath(FreshSessionHarvestFile)));
        var sourceCases = harvest.RepresentativeCases.ToDictionary(item => item.Id);

        Assert.Equal(FreshSessionPolicyFixturesSha256, ComputeSha256(bytes));
        Assert.DoesNotContain((byte)'\r', bytes);
        Assert.Equal(1, fixtures.SchemaVersion);
        Assert.Empty(fixtures.Cases);
        Assert.Empty(fixtures.AdversarialCases);
        Assert.Equal(
            Enumerable.Range(1, 10).Select(number => $"R{number:00}"),
            fixtures.LiveRegressionCases.Select(item => item.PolicyCase.Id));
        Assert.Equal(
            10,
            fixtures.LiveRegressionCases
                .Select(item => (item.SourceEvidenceFile, item.SourceEvidenceId))
                .Distinct()
                .Count());
        Assert.All(fixtures.LiveRegressionCases, item =>
        {
            Assert.Equal(FreshSessionHarvestFile, item.SourceEvidenceFile);
            var source = sourceCases[item.SourceEvidenceId];
            Assert.Equal(source.CommandShape, item.PolicyCase.Command);
            Assert.Equal(source.Classification, item.Classification);
            Assert.Equal(item.TargetOutcome, item.PolicyCase.Expected.Outcome);
            Assert.NotNull(item.PolicyCase.Expected.CandidateCoverage);
            Assert.NotNull(item.PolicyCase.Expected.Trace);
            var completion = item.PolicyCase.Expected.Trace![^1];
            Assert.Equal("Completion", completion.Stage);
            Assert.Equal(item.TargetOutcome, completion.Outcome);
            Assert.Null(completion.CandidateId);
            Assert.True(item.PolicyCase.Expected.ActorCheckCount >= 0);
        });
        Assert.Single(fixtures.LiveRegressionCases, item => item.TargetOutcome == "Allow");
        Assert.Equal(
            8,
            fixtures.LiveRegressionCases.Count(item => item.TargetOutcome == "RequiresApproval"));
        Assert.Single(fixtures.LiveRegressionCases, item => item.TargetOutcome == "Deny");
        Assert.All(
            fixtures.LiveRegressionCases.Where(item =>
                item.Classification == "AgentAlignmentDebt"),
            item => Assert.Equal("RequiresApproval", item.TargetOutcome));

        var aliasCase = Assert.Single(
            fixtures.LiveRegressionCases,
            item => item.PolicyCase.Id == "R09").PolicyCase;
        Assert.True(aliasCase.UsePhysicalHarnessScope);
        Assert.Equal("ProjectFileSymlink", Assert.Single(aliasCase.FileSystemFacts!).Kind);
        Assert.False(aliasCase.Expected.IsMessy);
        Assert.Equal(
            ["approve_once", "approve_session", "approve_always", "approve_everywhere", "deny"],
            aliasCase.Expected.OptionKeys);

        var protectedCase = Assert.Single(
            fixtures.LiveRegressionCases,
            item => item.PolicyCase.Id == "R10").PolicyCase;
        Assert.Equal("shell_references_protected_path", protectedCase.Expected.DenyReason);
        Assert.Equal(0, protectedCase.Expected.ActorCheckCount);
        Assert.Equal(["/protected/control.json"], protectedCase.DeniedPaths);
    }

    [Theory]
    [InlineData("\"classification\": \"ExpectedApproval\"", "\"classification\": \"Changed\"")]
    [InlineData("git status && git branch --show-current", "git status && git branch --changed")]
    [InlineData("\"targetOutcome\": \"Deny\"", "\"targetOutcome\": \"Allow\"")]
    [InlineData("\"agentCorrection\": null", "\"agentCorrection\": \"ChangedCorrection\"")]
    [InlineData("\"approve_always\"", "\"changed_option\"")]
    [InlineData("\"candidateId\": 2, \"coverage\": \"Session\"", "\"candidateId\": 2, \"coverage\": \"Uncovered\"")]
    [InlineData("\"stage\": \"ReviewedSafePolicy\"", "\"stage\": \"Completion\"")]
    [InlineData("\"actorCheckCount\": 0", "\"actorCheckCount\": 7")]
    [InlineData("\"target\": \"AGENTS.md\"", "\"target\": \"outside.md\"")]
    [InlineData("\"/protected/control.json\"", "\"/changed/control.json\"")]
    public void Fresh_session_policy_fixture_digest_detects_security_significant_mutation(
        string original,
        string replacement)
    {
        var json = File.ReadAllText(EvidencePath(FreshSessionPolicyFixturesFile));
        var mutated = json.Replace(original, replacement, StringComparison.Ordinal);

        Assert.NotEqual(json, mutated);
        Assert.NotEqual(
            ComputeSha256(Encoding.UTF8.GetBytes(json)),
            ComputeSha256(Encoding.UTF8.GetBytes(mutated)));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Fresh_session_policy_fixture_detects_linked_source_mutation(bool mutateCommand)
    {
        var fixtures = DeserializeFixtures(
            File.ReadAllBytes(EvidencePath(FreshSessionPolicyFixturesFile)));
        var harvest = DeserializeFreshSessionHarvest(
            File.ReadAllBytes(EvidencePath(FreshSessionHarvestFile)));
        var sourceCases = harvest.RepresentativeCases.ToDictionary(item => item.Id);
        var fixture = fixtures.LiveRegressionCases[0];
        var source = sourceCases[fixture.SourceEvidenceId];
        sourceCases[fixture.SourceEvidenceId] = mutateCommand
            ? source with { CommandShape = "changed source shape" }
            : source with { Classification = "ChangedClassification" };

        var changed = sourceCases[fixture.SourceEvidenceId];
        Assert.True(
            changed.CommandShape != fixture.PolicyCase.Command
            || changed.Classification != fixture.Classification);
    }

    [Fact]
    public void Approval_matrix_matches_the_locked_cross_repository_artifact()
    {
        var bytes = File.ReadAllBytes(EvidencePath(ApprovalMatrixFile));
        var hash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        var matrix = DeserializeMatrix(bytes);

        Assert.Equal(ApprovalMatrixSha256, hash);
        Assert.Equal("Netclaw 0.26.0-beta.3", matrix.SourceRelease);
        Assert.Equal(
            Enumerable.Range(1, 18).Select(number => $"D{number:00}"),
            matrix.Cases.Select(item => item.Id));
        string[] allowedClassifications =
        [
            "CorrectPrompt",
            "IrreduciblyDynamic",
            "NetclawPolicyDefect",
            "ShellSyntaxTreeFactGap"
        ];
        Assert.All(matrix.Cases, item =>
            Assert.Contains(item.Classification, allowedClassifications));
    }

    [Fact]
    public void Policy_fixtures_load_exact_authority_and_trace_fields()
    {
        var matrix = DeserializeMatrix(File.ReadAllBytes(EvidencePath(ApprovalMatrixFile)));
        var fixtures = DeserializeFixtures(File.ReadAllBytes(EvidencePath(PolicyFixturesFile)));
        var commands = matrix.Cases.ToDictionary(item => item.Id, item => item.Command);

        Assert.Equal(3, fixtures.SchemaVersion);
        Assert.Equal("shell_execute", fixtures.FixtureDefaults.ToolName);
        Assert.Equal("Personal", fixtures.FixtureDefaults.Audience);
        Assert.Equal("Approval", fixtures.FixtureDefaults.ApprovalMode);
        Assert.Equal("Available", fixtures.FixtureDefaults.InteractiveApprovalCapability);
        Assert.Equal("2026-08-13T00:00:00+00:00", fixtures.FixtureDefaults.ClockUtc);
        Assert.Equal("Ready", fixtures.FixtureDefaults.PersistentStoreStatus);
        Assert.Equal("fixture-session", fixtures.FixtureDefaults.Session.SessionId);
        Assert.Equal("/work", fixtures.FixtureDefaults.Session.SessionDirectory);
        Assert.Equal("/work", fixtures.FixtureDefaults.ProjectDirectory);
        Assert.Null(fixtures.FixtureDefaults.InheritedWorkingDirectory);
        Assert.Equal(10, fixtures.Cases.Count);
        var sourceEvidence = LoadLiveRegressionEvidence();
        Assert.Equal(
            Enumerable.Range(1, 32).Select(number => $"L{number:00}"),
            fixtures.LiveRegressionCases.Select(item => item.PolicyCase.Id));
        Assert.Equal(
            new[] { "S18", "S22", "S40", "S16", "S11", "S10", "S24", "S20", "S13", "S21", "S44" }
                .Concat(Enumerable.Range(1, 21).Select(number => $"T{number:00}")),
            fixtures.LiveRegressionCases.Select(item => item.SourceEvidenceId));
        Assert.Equal(
            fixtures.LiveRegressionCases.Count,
            fixtures.LiveRegressionCases
                .Select(item => (item.SourceEvidenceFile, item.SourceEvidenceId))
                .Distinct()
                .Count());
        Assert.All(fixtures.LiveRegressionCases, item =>
        {
            Assert.Contains(
                item.Classification,
                new[]
                {
                    "ExpectedApproval",
                    "AgentAlignmentDebt",
                    "NetclawPolicyDebt",
                    "ShellSyntaxTreeFactGap"
                });
            Assert.Contains(item.TargetOutcome, new[] { "Allow", "RequiresApproval" });
            Assert.Equal(item.TargetOutcome, item.PolicyCase.Expected.Outcome);
            var sourceKey = (item.SourceEvidenceFile, item.SourceEvidenceId);
            Assert.True(sourceEvidence.TryGetValue(sourceKey, out var sourceCase));
            Assert.Equal(sourceCase.Classification, item.Classification);
        });
        Assert.Equal(4, fixtures.LiveRegressionCases.Count(item => item.TargetOutcome == "Allow"));
        Assert.Equal(
            28,
            fixtures.LiveRegressionCases.Count(item => item.TargetOutcome == "RequiresApproval"));
        var post1952Cases = fixtures.LiveRegressionCases
            .Where(item => item.SourceEvidenceFile == Post1952HarvestFile)
            .ToList();
        Assert.Equal(21, post1952Cases.Count);
        Assert.All(post1952Cases, item =>
        {
            Assert.Equal("RequiresApproval", item.TargetOutcome);
            Assert.DoesNotMatch("<[^>]+>", item.PolicyCase.Command);
        });
        Assert.Equal(8, post1952Cases.Count(item => item.Classification == "ExpectedApproval"));
        Assert.Equal(10, post1952Cases.Count(item => item.Classification == "AgentAlignmentDebt"));
        Assert.Equal(3, post1952Cases.Count(item => item.Classification == "ShellSyntaxTreeFactGap"));
        Assert.Equal(
            Enumerable.Range(1, 12).Select(number => $"A{number:00}"),
            fixtures.AdversarialCases.Select(item => item.Id));
        Assert.Equal(
            12,
            fixtures.AdversarialCases.Select(item => item.Category).Distinct().Count());
        Assert.All(fixtures.AdversarialCases, item =>
        {
            Assert.False(string.IsNullOrWhiteSpace(item.Command));
            Assert.False(string.IsNullOrWhiteSpace(item.Expected.Outcome));
            Assert.True(item.Expected.ActorCheckCount >= 0);
        });

        foreach (var fixture in fixtures.Cases)
        {
            Assert.Equal(commands[fixture.EvidenceId], fixture.Command);
            Assert.Equal("shell_execute", fixtures.FixtureDefaults.ToolName);
            Assert.Equal("Ready", fixtures.FixtureDefaults.PersistentStoreStatus);
            Assert.Equal(
                Enumerable.Range(0, fixture.Candidates.Count),
                fixture.Candidates.Select(candidate => candidate.Id));
            Assert.All(fixture.Available.PersistentGrants, grant =>
            {
                Assert.False(string.IsNullOrWhiteSpace(grant.Shell));
                Assert.NotEmpty(grant.Tokens);
            });

            foreach (var candidate in fixture.Candidates)
            {
                Assert.NotEmpty(candidate.Tokens);
                Assert.Single(fixture.ExpectedTrace, row =>
                    row.CandidateId == candidate.Id
                    && row.Coverage == candidate.ExpectedCoverage);
            }

            var completion = fixture.ExpectedTrace[^1];
            Assert.Equal("Completion", completion.Stage);
            Assert.Null(completion.CandidateId);
            Assert.Equal(fixture.ExpectedFinal.Outcome, completion.Outcome);
            Assert.Equal(fixture.ExpectedFinal.Reason, completion.Reason);
        }
    }

    [Fact]
    public void Live_regression_evidence_section_matches_locked_digest()
    {
        var bytes = File.ReadAllBytes(EvidencePath(PolicyFixturesFile));

        Assert.Equal(LiveRegressionCasesSha256, ComputeLiveRegressionDigest(bytes));
    }

    [Theory]
    [InlineData("\"sourceEvidenceId\": \"T01\"", "\"sourceEvidenceId\": \"T99\"")]
    [InlineData("\"classification\": \"ExpectedApproval\"", "\"classification\": \"Changed\"")]
    [InlineData("mkdir -p /tmp/review-workspace", "mkdir -p /tmp/changed-workspace")]
    [InlineData("\"targetOutcome\": \"RequiresApproval\"", "\"targetOutcome\": \"Allow\"")]
    [InlineData("\"approvalCandidates\": []", "\"approvalCandidates\": [\"unexpected\"]")]
    [InlineData("\"denyReason\": null, \"approvalCandidates\"", "\"denyReason\": null, \"agentCorrection\": \"ChangedCorrection\", \"approvalCandidates\"")]
    [InlineData("\"actorCheckCount\": 0", "\"actorCheckCount\": 7")]
    public void Live_regression_digest_detects_security_significant_mutation(
        string original,
        string replacement)
    {
        var json = File.ReadAllText(EvidencePath(PolicyFixturesFile));
        var mutated = json.Replace(original, replacement, StringComparison.Ordinal);

        Assert.NotEqual(json, mutated);
        Assert.NotEqual(
            ComputeLiveRegressionDigest(Encoding.UTF8.GetBytes(json)),
            ComputeLiveRegressionDigest(Encoding.UTF8.GetBytes(mutated)));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Live_regression_digest_detects_linked_source_mutation(bool mutateCommandShape)
    {
        var fixtureBytes = File.ReadAllBytes(EvidencePath(PolicyFixturesFile));
        var sourceEvidence = LoadLiveRegressionEvidence().ToDictionary();
        var sourceKey = (File: Post1952HarvestFile, Id: "T01");
        var sourceCase = sourceEvidence[sourceKey];
        sourceEvidence[sourceKey] = mutateCommandShape
            ? sourceCase with { CommandShape = "changed source shape" }
            : sourceCase with { Classification = "ChangedClassification" };

        Assert.NotEqual(
            ComputeLiveRegressionDigest(fixtureBytes),
            ComputeLiveRegressionDigest(fixtureBytes, sourceEvidence));
    }

    private static IReadOnlyDictionary<(string File, string Id), PostMergeApprovalCase>
        LoadLiveRegressionEvidence()
    {
        string[] evidenceFiles =
        [
            PostSwapHarvestFile,
            ExtendedPostSwapHarvestFile,
            Post1952HarvestFile
        ];

        return evidenceFiles
            .SelectMany(file => JsonSerializer.Deserialize(
                                    File.ReadAllBytes(EvidencePath(file)),
                                    ShellApprovalEvidenceJsonContext.Default.PostMergeApprovalHarvest)!
                                .Cases
                                .Select(item => (Key: (File: file, Id: item.Id), Case: item)))
            .ToDictionary(item => item.Key, item => item.Case);
    }

    private static string ComputeLiveRegressionDigest(byte[] fixtureBytes)
        => ComputeLiveRegressionDigest(fixtureBytes, LoadLiveRegressionEvidence());

    private static string ComputeLiveRegressionDigest(
        byte[] fixtureBytes,
        IReadOnlyDictionary<(string File, string Id), PostMergeApprovalCase> sourceEvidence)
    {
        using var document = JsonDocument.Parse(fixtureBytes);
        var liveCasesJson = document.RootElement.GetProperty("liveRegressionCases").GetRawText();
        var fixtures = DeserializeFixtures(fixtureBytes);
        var lockedEvidence = new StringBuilder(liveCasesJson);
        foreach (var item in fixtures.LiveRegressionCases)
        {
            var sourceKey = (item.SourceEvidenceFile, item.SourceEvidenceId);
            if (!sourceEvidence.TryGetValue(sourceKey, out var sourceCase))
            {
                lockedEvidence.Append("\nmissing:").Append(item.SourceEvidenceFile)
                    .Append(':').Append(item.SourceEvidenceId);
                continue;
            }

            lockedEvidence.Append('\n').Append(sourceCase.CommandShape.Length).Append(':')
                .Append(sourceCase.CommandShape).Append('\n')
                .Append(sourceCase.Classification.Length).Append(':')
                .Append(sourceCase.Classification);
        }

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(lockedEvidence.ToString())))
            .ToLowerInvariant();
    }

    [Fact]
    public void Policy_fixture_schema_rejects_unknown_members()
    {
        var json = File.ReadAllText(EvidencePath(PolicyFixturesFile));
        var malformed = json.Replace(
            "\"schemaVersion\": 3,",
            "\"schemaVersion\": 3, \"unexpected\": true,",
            StringComparison.Ordinal);

        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize(
            malformed,
            ShellPolicyFixtureJsonContext.Default.PolicyFixtureCatalog));
    }

    [Fact]
    public void Exact_symbolic_parser_facts_match_the_policy_fixtures()
    {
        var fixtures = DeserializeFixtures(File.ReadAllBytes(EvidencePath(PolicyFixturesFile)));
        var factCases = fixtures.Cases.Where(item =>
            item.ValueFacts is { Count: > 0 }
            || item.AuthoredPathFacts is { Count: > 0 }
            || item.ShellEffects is not null);

        foreach (var fixture in factCases)
        {
            var environment = CreateEnvironment(fixture.Environment);
            var analysis = new ShellCommandAnalyzer(environment).Analyze(
                fixture.Command,
                fixture.InitialWorkingDirectory);
            var allowedPathPolicy = new ToolPathPolicy(environment, ["/protected"]);

            Assert.Equal(ShellAnalysisFailure.None, analysis.Failure);
            foreach (var fact in fixture.ValueFacts ?? [])
            {
                var command = analysis.Commands[fact.CommandIndex];
                Assert.Equal(fact.VerbTokens, command.Clause.Verb.Tokens);
                var argument = command.Arguments[fact.ArgumentIndex];
                var concatenation = Assert.IsType<ShellValueDomain.Concatenation>(argument.Value);
                Assert.Equal("Concatenation", fact.Domain);
                Assert.Equal(fact.Parts.Count, concatenation.Parts.Count);

                for (var index = 0; index < fact.Parts.Count; index++)
                {
                    AssertValuePart(fact.Parts[index], concatenation.Parts[index]);
                }
            }

            foreach (var fact in fixture.AuthoredPathFacts ?? [])
            {
                var command = analysis.Commands[fact.CommandIndex];
                Assert.Equal(fact.VerbTokens, command.Clause.Verb.Tokens);
                var argument = Assert.Single(
                    command.Arguments,
                    item => item.AuthoredPathShape.ToString() == fact.AuthoredPathShape);
                Assert.IsType<ShellValueDomain.Unknown>(argument.Value);
                Assert.Equal("Unknown", fact.EffectiveValue);
                var authored = Assert.IsType<ShellValueDomain.FiniteSet>(argument.AuthoredValue);
                Assert.Equal(fact.AuthoredValues, authored.Values);
                var authoredFileSystem = Assert.IsType<ShellValueDomain.FiniteSet>(
                    argument.AuthoredFileSystemValue);
                Assert.Equal(fact.AuthoredFileSystemValues, authoredFileSystem.Values);
                Assert.Equal(fact.AuthoredPathShape, argument.AuthoredPathShape.ToString());
            }

            foreach (var expected in fixture.ShellEffects?.Redirects ?? [])
            {
                var redirect = Assert.Single(
                    analysis.Commands[expected.CommandIndex].Redirects.OfType<FileRedirectAnalysis>());
                var target = Assert.IsType<ShellValueDomain.Exact>(redirect.Target);
                Assert.Equal(expected.Target, target.Value);
                Assert.Equal(expected.Mode, redirect.Mode.ToString());
                Assert.Equal(
                    expected.ExpectedPathPolicy,
                    allowedPathPolicy.CommandReferencesDeniedPath(analysis) ? "Deny" : "Allow");
                Assert.True(new ToolPathPolicy(environment, [expected.Target])
                    .CommandReferencesDeniedPath(StructuredOnly(analysis)));
            }
        }
    }

    [Fact]
    public void Authored_path_fixture_uses_the_strong_filesystem_domain()
    {
        var fixtures = DeserializeFixtures(File.ReadAllBytes(EvidencePath(PolicyFixturesFile)));
        var fixture = Assert.Single(fixtures.Cases, item => item.AuthoredPathFacts is { Count: > 0 });
        var fact = Assert.Single(fixture.AuthoredPathFacts!);
        var environment = CreateEnvironment(fixture.Environment);
        var analysis = new ShellCommandAnalyzer(environment).Analyze(
            fixture.Command,
            fixture.InitialWorkingDirectory);
        var argument = Assert.Single(
            analysis.Commands[fact.CommandIndex].Arguments,
            item => item.AuthoredPathShape.ToString() == fact.AuthoredPathShape);

        Assert.False(fact.ArgumentIsPath);
        Assert.Equal(fact.ArgumentIsPath, argument.Argument.IsPath);
        var authoredFileSystem = Assert.IsType<ShellValueDomain.FiniteSet>(
            argument.AuthoredFileSystemValue);
        Assert.Equal(fact.AuthoredFileSystemValues, authoredFileSystem.Values);
        Assert.Equal("Allow", fact.ExpectedPathPolicy);
        Assert.False(new ToolPathPolicy(environment, ["/protected"])
            .CommandReferencesDeniedPath(analysis));
        Assert.All(fact.AuthoredFileSystemValues, path =>
            Assert.True(new ToolPathPolicy(environment, [path])
                .CommandReferencesDeniedPath(StructuredOnly(analysis))));
        Assert.Equal("Allow", fixture.ExpectedFinal.Outcome);
    }

    [Fact]
    public void D14_fixture_is_covered_by_its_typed_grant_and_safe_paths()
    {
        var fixtures = DeserializeFixtures(File.ReadAllBytes(EvidencePath(PolicyFixturesFile)));
        var fixture = Assert.Single(fixtures.Cases, item => item.EvidenceId == "D14");
        var environment = CreateEnvironment(fixture.Environment);
        var matcher = new ShellApprovalMatcher(environment);
        var arguments = new Dictionary<string, object?>
        {
            ["Command"] = fixture.Command,
            ["WorkingDirectory"] = fixture.InitialWorkingDirectory,
        };
        var grants = fixture.Available.PersistentGrants.Select(grant =>
            ApprovalEntry.CreateTokenPrefix(
                Enum.Parse<ApprovalShell>(grant.Shell),
                grant.Tokens,
                grant.Directory)).ToList();

        var invocation = matcher.AnalyzeInvocation(
            new ToolName("shell_execute"),
            arguments);

        Assert.False(invocation.IsMessy);
        Assert.True(matcher.IsApproved(
            new ToolName("shell_execute"),
            arguments,
            grants,
            fixture.InitialWorkingDirectory));
        Assert.False(new ToolPathPolicy(environment, ["/protected"])
            .CommandReferencesDeniedPath(
                fixture.Command,
                fixture.InitialWorkingDirectory));
    }

    [Fact]
    public void Approval_evidence_contains_no_source_identity()
    {
        var evidenceDirectory = Path.GetDirectoryName(EvidencePath(ApprovalMatrixFile))
                                ?? throw new InvalidDataException("Approval evidence has no directory.");
        foreach (var path in Directory.EnumerateFiles(evidenceDirectory, "*.json"))
        {
            var text = File.ReadAllText(path);
            Assert.DoesNotMatch(SlackChannelPattern(), text);
            Assert.DoesNotMatch(SlackThreadPattern(), text);
            Assert.DoesNotMatch(EmailPattern(), text);
            Assert.DoesNotMatch(PrivateHomePattern(), text);
            Assert.DoesNotMatch(PrivateWindowsUserPattern(), text);
            Assert.DoesNotMatch(KnownSourceIdentityPattern(), text);
            Assert.DoesNotMatch(AccessTokenPattern(), text);
            Assert.DoesNotMatch(BearerCredentialPattern(), text);
            Assert.DoesNotMatch(CredentialAssignmentPattern(), text);

            foreach (Match match in UriHostPattern().Matches(text))
            {
                Assert.Contains(
                    match.Groups["host"].Value,
                    new[]
                    {
                        "api.github.com",
                        "packages.example.invalid",
                        "service.example.invalid"
                    },
                    StringComparer.OrdinalIgnoreCase);
            }

            foreach (Match match in RemoteRepositoryPattern().Matches(text))
            {
                Assert.Equal("example/project", match.Groups["repository"].Value);
            }
        }
    }

    [Theory]
    [InlineData("ghp_000000000000000000000000000000000000")]
    [InlineData("github_pat_00000000000000000000000000000000")]
    [InlineData("xoxb-0000000000-0000000000-0000000000")]
    [InlineData("Authorization: Bearer example-credential-value")]
    [InlineData("api_key=examplecredentialvalue")]
    public void Pii_audit_detects_common_credential_shapes(string value)
    {
        Assert.True(
            AccessTokenPattern().IsMatch(value)
            || BearerCredentialPattern().IsMatch(value)
            || CredentialAssignmentPattern().IsMatch(value));
    }

    [Fact]
    public void Post_merge_harvest_classifies_every_prompt_in_the_frozen_window()
    {
        var harvest = JsonSerializer.Deserialize(
                          File.ReadAllBytes(EvidencePath(PostMergeHarvestFile)),
                          ShellApprovalEvidenceJsonContext.Default.PostMergeApprovalHarvest)
                      ?? throw new InvalidDataException($"{PostMergeHarvestFile} has no root object.");

        Assert.Equal(1, harvest.SchemaVersion);
        Assert.Equal("0.26.0", harvest.SourceRuntime.Version);
        Assert.Equal("e35444c", harvest.SourceRuntime.Commit);
        Assert.Equal(112, harvest.SourceRuntime.ShellCallCount);
        Assert.Equal(25, harvest.SourceRuntime.ApprovalPromptCount);
        Assert.Equal(
            Enumerable.Range(1, 25).Select(number => $"P{number:00}"),
            harvest.Cases.Select(item => item.Id));
        Assert.Equal(
            harvest.Cases.Select(item => item.SourcePromptTimeUtc).Order(),
            harvest.Cases.Select(item => item.SourcePromptTimeUtc));
        Assert.All(harvest.Cases, item =>
        {
            Assert.InRange(
                item.SourcePromptTimeUtc,
                DateTimeOffset.Parse(harvest.SourceRuntime.WindowStartUtc),
                DateTimeOffset.Parse(harvest.SourceRuntime.WindowEndUtc));
        });
        Assert.Equal(18, harvest.Cases.Count(item => item.Classification == "ExpectedApproval"));
        Assert.Equal(6, harvest.Cases.Count(item => item.Classification == "AgentAlignmentDebt"));
        Assert.Single(harvest.Cases, item => item.Classification == "NetclawPolicyDebt");
        Assert.DoesNotContain(
            harvest.Cases,
            item => item.Classification == "ShellSyntaxTreeFactGap");
        Assert.All(harvest.Cases, item =>
        {
            Assert.False(string.IsNullOrWhiteSpace(item.CommandShape));
            Assert.False(string.IsNullOrWhiteSpace(item.Reason));
        });
    }

    [Fact]
    public void Post_swap_harvest_classifies_every_prompt_in_the_frozen_window()
    {
        var harvest = JsonSerializer.Deserialize(
                          File.ReadAllBytes(EvidencePath(PostSwapHarvestFile)),
                          ShellApprovalEvidenceJsonContext.Default.PostMergeApprovalHarvest)
                      ?? throw new InvalidDataException($"{PostSwapHarvestFile} has no root object.");

        Assert.Equal(1, harvest.SchemaVersion);
        Assert.Equal("0.26.0", harvest.SourceRuntime.Version);
        Assert.Equal("ba83530", harvest.SourceRuntime.Commit);
        Assert.Equal(62, harvest.SourceRuntime.ShellCallCount);
        Assert.Equal(9, harvest.SourceRuntime.ApprovalPromptCount);
        Assert.Equal(
            Enumerable.Range(1, 9).Select(number => $"S{number:00}"),
            harvest.Cases.Select(item => item.Id));
        Assert.Equal(
            harvest.Cases.Select(item => item.SourcePromptTimeUtc).Order(),
            harvest.Cases.Select(item => item.SourcePromptTimeUtc));
        Assert.All(harvest.Cases, item =>
        {
            Assert.InRange(
                item.SourcePromptTimeUtc,
                DateTimeOffset.Parse(harvest.SourceRuntime.WindowStartUtc),
                DateTimeOffset.Parse(harvest.SourceRuntime.WindowEndUtc));
        });
        Assert.Equal(6, harvest.Cases.Count(item => item.Classification == "ExpectedApproval"));
        Assert.Equal(3, harvest.Cases.Count(item => item.Classification == "AgentAlignmentDebt"));
        Assert.DoesNotContain(
            harvest.Cases,
            item => item.Classification is "NetclawPolicyDebt" or "ShellSyntaxTreeFactGap");
        Assert.All(harvest.Cases, item =>
        {
            Assert.False(string.IsNullOrWhiteSpace(item.CommandShape));
            Assert.False(string.IsNullOrWhiteSpace(item.Reason));
        });
    }

    [Fact]
    public void Extended_post_swap_harvest_classifies_every_prompt_in_the_frozen_window()
    {
        var harvest = JsonSerializer.Deserialize(
                          File.ReadAllBytes(EvidencePath(ExtendedPostSwapHarvestFile)),
                          ShellApprovalEvidenceJsonContext.Default.PostMergeApprovalHarvest)
                      ?? throw new InvalidDataException(
                          $"{ExtendedPostSwapHarvestFile} has no root object.");

        Assert.Equal(1, harvest.SchemaVersion);
        Assert.Equal("0.26.0", harvest.SourceRuntime.Version);
        Assert.Equal("ba83530", harvest.SourceRuntime.Commit);
        Assert.Equal(140, harvest.SourceRuntime.ShellCallCount);
        Assert.Equal(42, harvest.SourceRuntime.ApprovalPromptCount);
        Assert.Equal(
            Enumerable.Range(10, 42).Select(number => $"S{number:00}"),
            harvest.Cases.Select(item => item.Id));
        Assert.Equal(
            harvest.Cases.Select(item => item.SourcePromptTimeUtc).Order(),
            harvest.Cases.Select(item => item.SourcePromptTimeUtc));
        Assert.All(harvest.Cases, item =>
        {
            Assert.InRange(
                item.SourcePromptTimeUtc,
                DateTimeOffset.Parse(harvest.SourceRuntime.WindowStartUtc),
                DateTimeOffset.Parse(harvest.SourceRuntime.WindowEndUtc));
        });
        Assert.Equal(28, harvest.Cases.Count(item => item.Classification == "ExpectedApproval"));
        Assert.Equal(9, harvest.Cases.Count(item => item.Classification == "AgentAlignmentDebt"));
        Assert.Equal(5, harvest.Cases.Count(item => item.Classification == "NetclawPolicyDebt"));
        Assert.DoesNotContain(
            harvest.Cases,
            item => item.Classification == "ShellSyntaxTreeFactGap");
        Assert.All(harvest.Cases, item =>
        {
            Assert.False(string.IsNullOrWhiteSpace(item.CommandShape));
            Assert.False(string.IsNullOrWhiteSpace(item.Reason));
        });
    }

    [Fact]
    public void Post_1952_harvest_samples_every_live_approval_bucket()
    {
        var harvest = JsonSerializer.Deserialize(
                          File.ReadAllBytes(EvidencePath(Post1952HarvestFile)),
                          ShellApprovalEvidenceJsonContext.Default.PostMergeApprovalHarvest)
                      ?? throw new InvalidDataException(
                          $"{Post1952HarvestFile} has no root object.");

        Assert.Equal(1, harvest.SchemaVersion);
        Assert.Equal("0.26.0", harvest.SourceRuntime.Version);
        Assert.Equal("b45bf8d", harvest.SourceRuntime.Commit);
        Assert.Equal(285, harvest.SourceRuntime.ShellCallCount);
        Assert.Equal(69, harvest.SourceRuntime.ApprovalPromptCount);
        Assert.Equal(
            Enumerable.Range(1, 21).Select(number => $"T{number:00}"),
            harvest.Cases.Select(item => item.Id));
        Assert.Equal(
            harvest.Cases.Select(item => item.SourcePromptTimeUtc).Order(),
            harvest.Cases.Select(item => item.SourcePromptTimeUtc));
        Assert.All(harvest.Cases, item =>
        {
            Assert.InRange(
                item.SourcePromptTimeUtc,
                DateTimeOffset.Parse(harvest.SourceRuntime.WindowStartUtc),
                DateTimeOffset.Parse(harvest.SourceRuntime.WindowEndUtc));
        });
        Assert.Equal(8, harvest.Cases.Count(item => item.Classification == "ExpectedApproval"));
        Assert.Equal(10, harvest.Cases.Count(item => item.Classification == "AgentAlignmentDebt"));
        Assert.Equal(3, harvest.Cases.Count(item => item.Classification == "ShellSyntaxTreeFactGap"));
        Assert.DoesNotContain(
            harvest.Cases,
            item => item.Classification == "NetclawPolicyDebt");
        Assert.All(harvest.Cases, item =>
        {
            Assert.False(string.IsNullOrWhiteSpace(item.CommandShape));
            Assert.False(string.IsNullOrWhiteSpace(item.Reason));
        });
    }

    private static void AssertValuePart(PolicyValuePart expected, ShellValueDomain actual)
    {
        if (expected.Exact is { } exact)
        {
            Assert.Equal(exact, Assert.IsType<ShellValueDomain.Exact>(actual).Value);
            return;
        }

        var range = Assert.IsType<ShellValueDomain.IntegerRange>(actual);
        Assert.NotNull(expected.IntegerRange);
        Assert.Equal(2, expected.IntegerRange.Count);
        Assert.Equal(expected.IntegerRange[0], range.MinimumInclusive);
        Assert.Equal(expected.IntegerRange[1], range.MaximumInclusive);
    }

    private static ShellExecutionEnvironment CreateEnvironment(PolicyFixtureEnvironment environment)
        => environment.Grammar switch
        {
            "Bash" when environment.Platform == "Linux"
                && environment.PathStyle == "Posix"
                && environment.ExecutablePath == "/bin/bash"
                && environment.CommandArguments.SequenceEqual(["-c"])
                => ShellExecutionEnvironment.CreateBash(ShellPlatform.Linux),
            _ => throw new InvalidDataException(
                $"Unsupported fixture environment: {environment.Platform}/{environment.Grammar}.")
        };

    private static ShellCommandAnalysis StructuredOnly(ShellCommandAnalysis analysis)
        => new(
            analysis.Environment,
            source: string.Empty,
            analysis.WorkingDirectory,
            analysis.Commands,
            ShellAnalysisFailure.None);

    private static ApprovalEvidenceMatrix DeserializeMatrix(byte[] bytes)
        => JsonSerializer.Deserialize(
               bytes,
               ShellApprovalEvidenceJsonContext.Default.ApprovalEvidenceMatrix)
           ?? throw new InvalidDataException($"{ApprovalMatrixFile} has no root object.");

    private static PolicyFixtureCatalog DeserializeFixtures(byte[] bytes)
        => JsonSerializer.Deserialize(
               bytes,
               ShellPolicyFixtureJsonContext.Default.PolicyFixtureCatalog)
           ?? throw new InvalidDataException($"{PolicyFixturesFile} has no root object.");

    private static FreshSessionApprovalHarvest DeserializeFreshSessionHarvest(byte[] bytes)
        => JsonSerializer.Deserialize(
               bytes,
               ShellApprovalEvidenceJsonContext.Default.FreshSessionApprovalHarvest)
           ?? throw new InvalidDataException($"{FreshSessionHarvestFile} has no root object.");

    private static FreshSessionEvalBaseline DeserializeFreshSessionEvalBaseline(byte[] bytes)
        => JsonSerializer.Deserialize(
               bytes,
               ShellApprovalEvidenceJsonContext.Default.FreshSessionEvalBaseline)
           ?? throw new InvalidDataException($"{FreshSessionEvalBaselineFile} has no root object.");

    private static string ComputeSha256(byte[] bytes)
        => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private static string EvidencePath(string fileName)
        => Path.Combine(AppContext.BaseDirectory, "ApprovalEvidence", fileName);

    [GeneratedRegex(@"\bD[A-Z0-9]{10}\b", RegexOptions.CultureInvariant)]
    private static partial Regex SlackChannelPattern();

    [GeneratedRegex(@"\b\d{10}\.\d{6}\b", RegexOptions.CultureInvariant)]
    private static partial Regex SlackThreadPattern();

    [GeneratedRegex(@"[a-zA-Z0-9._%+-]+@[a-zA-Z0-9.-]+\.[a-zA-Z]{2,}", RegexOptions.CultureInvariant)]
    private static partial Regex EmailPattern();

    [GeneratedRegex(@"/home/(?!user/|test/|foo/|dev/|runner/|gh-actions/|ci/)[a-zA-Z0-9_.-]+/", RegexOptions.CultureInvariant)]
    private static partial Regex PrivateHomePattern();

    [GeneratedRegex(@"[A-Za-z]:[\\/]Users[\\/](?!user[\\/]|test[\\/]|foo[\\/]|dev[\\/]|runner[\\/]|ci[\\/])[A-Za-z0-9_.-]+[\\/]", RegexOptions.CultureInvariant)]
    private static partial Regex PrivateWindowsUserPattern();

    [GeneratedRegex(@"petabridge|stannard|testlab|D0AC6", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex KnownSourceIdentityPattern();

    [GeneratedRegex(
        @"\b(?:gh[pousr]_[A-Za-z0-9]{20,}|github_pat_[A-Za-z0-9_]{20,}|xox[baprs]-[A-Za-z0-9-]{10,}|AKIA[0-9A-Z]{16}|eyJ[A-Za-z0-9_-]{20,}\.[A-Za-z0-9_-]{10,})\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex AccessTokenPattern();

    [GeneratedRegex(@"\bBearer\s+[A-Za-z0-9._~+/-]{8,}=*", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex BearerCredentialPattern();

    [GeneratedRegex(
        @"[""']?(?:token|access[_-]?token|api[_-]?key|client[_-]?secret|password)[""']?\s*[:=]\s*[""']?(?!<)[A-Za-z0-9+/=_-]{8,}",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex CredentialAssignmentPattern();

    [GeneratedRegex(
        @"https?://(?<host>[A-Za-z0-9.-]+)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex UriHostPattern();

    [GeneratedRegex(
        @"(?:--repo\s+|gh\s+api\s+repos/|api\.github\.com/repos/)(?<repository>[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex RemoteRepositoryPattern();

    [GeneratedRegex(@"\bcall_[A-Za-z0-9_-]+\b", RegexOptions.CultureInvariant)]
    private static partial Regex CallIdPattern();

    [GeneratedRegex(
        @"\b20\d{2}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(?:\.\d+)?Z?\b",
        RegexOptions.CultureInvariant)]
    private static partial Regex ExactSourceTimestampPattern();

    [GeneratedRegex(
        @"\b[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}\b",
        RegexOptions.CultureInvariant)]
    private static partial Regex GuidPattern();
}

internal sealed record ApprovalEvidenceMatrix
{
    public required string SourceRelease { get; init; }

    public required string CutoffUtc { get; init; }

    public required Dictionary<string, string> Sanitization { get; init; }

    public required List<ApprovalEvidenceCase> Cases { get; init; }
}

internal sealed record ApprovalEvidenceCase
{
    public required string Id { get; init; }

    public required string Command { get; init; }

    public required string Observed { get; init; }

    public required string Classification { get; init; }

    public required string Owner { get; init; }

    public required string SstExpectation { get; init; }

    public required string NetclawExpectation { get; init; }
}

internal sealed record PostMergeApprovalHarvest
{
    public required int SchemaVersion { get; init; }

    public required PostMergeSourceRuntime SourceRuntime { get; init; }

    public required Dictionary<string, string> Sanitization { get; init; }

    public required List<PostMergeApprovalCase> Cases { get; init; }
}

internal sealed record PostMergeSourceRuntime
{
    public required string Version { get; init; }

    public required string Commit { get; init; }

    public required string WindowStartUtc { get; init; }

    public required string WindowEndUtc { get; init; }

    public required int ShellCallCount { get; init; }

    public required int ApprovalPromptCount { get; init; }
}

internal sealed record PostMergeApprovalCase
{
    public required string Id { get; init; }

    public required DateTimeOffset SourcePromptTimeUtc { get; init; }

    public required string CommandShape { get; init; }

    public required string ObservedResponse { get; init; }

    public required string Classification { get; init; }

    public required string Owner { get; init; }

    public required string Reason { get; init; }
}

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow)]
[JsonSerializable(typeof(ApprovalEvidenceMatrix))]
[JsonSerializable(typeof(PostMergeApprovalHarvest))]
[JsonSerializable(typeof(FreshSessionApprovalHarvest))]
[JsonSerializable(typeof(FreshSessionEvalBaseline))]
internal sealed partial class ShellApprovalEvidenceJsonContext : JsonSerializerContext;
