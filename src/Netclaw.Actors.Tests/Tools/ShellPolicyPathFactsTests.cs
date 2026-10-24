// -----------------------------------------------------------------------
// <copyright file="ShellPolicyPathFactsTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Actors.Authorization.Consent;
using Netclaw.Actors.Protocol;
using Netclaw.Actors.Tools;
using Netclaw.Configuration;
using Netclaw.Security;
using Netclaw.Security.Authorization.Consent;
using Netclaw.Security.Authorization.Filesystem;
using Netclaw.Tests.Utilities;
using ShellSyntaxTree;
using Xunit;

namespace Netclaw.Actors.Tests.Tools;

public sealed class ShellPolicyPathFactsTests
{
    [Theory]
    [InlineData("/dev/tcp/host/80")]
    [InlineData("/dev/udp/host/80")]
    [InlineData("/dev/fd/9")]
    public void Compound_redirects_reject_bash_special_device_targets(string target)
    {
        var environment = ShellExecutionEnvironment.CreateBash(ShellPlatform.Linux);
        var analysis = new ShellCommandAnalyzer(environment)
            .Analyze($"while false; do :; done > {target}", "/work/repo");

        Assert.False(ShellPolicyPathFacts.TryCreateCompoundRedirectViews(analysis, out _));
    }

    [Fact]
    public void Compound_redirect_to_dev_null_is_a_known_non_file_effect()
    {
        var environment = ShellExecutionEnvironment.CreateBash(ShellPlatform.Linux);
        var analysis = new ShellCommandAnalyzer(environment)
            .Analyze("while false; do :; done > /dev/null", "/work/repo");

        Assert.True(ShellPolicyPathFacts.TryCreateCompoundRedirectViews(analysis, out var views));
        var view = Assert.Single(Assert.IsAssignableFrom<IReadOnlyList<ShellPolicyCompoundRedirectPathFacts>>(views));
        Assert.Equal("/dev/null", Assert.Single(view.PathFact.Paths).Value);
    }

    [Fact]
    public void Compound_absolute_targets_do_not_require_an_owner_working_directory_but_relative_targets_do()
    {
        var environment = ShellExecutionEnvironment.CreateBash(ShellPlatform.Linux, new Version(5, 2));
        var analyzer = new ShellCommandAnalyzer(environment);
        var absolute = analyzer.Analyze("cd \"$target\" && while false; do :; done > /tmp/result.txt", "/work/repo");
        var relative = analyzer.Analyze("cd \"$target\" && while false; do :; done > result.txt", "/work/repo");

        Assert.True(ShellPolicyPathFacts.TryCreateCompoundRedirectViews(absolute, out var absoluteViews));
        Assert.Equal("/tmp/result.txt", Assert.Single(Assert.IsAssignableFrom<IReadOnlyList<ShellPolicyCompoundRedirectPathFacts>>(absoluteViews)).PathFact.Paths[0].Value);
        Assert.False(ShellPolicyPathFacts.TryCreateCompoundRedirectViews(relative, out _));
    }

    [Fact]
    public void Compound_consent_candidates_must_name_the_retained_source_owner_and_operation()
    {
        const string source = "for item in one; do value=1; done > result.txt";
        const string cwd = "/work/repo";
        var environment = ShellExecutionEnvironment.CreateBash(ShellPlatform.Linux, new Version(5, 2));
        var analysis = new ShellCommandAnalyzer(environment).Analyze(source, cwd);
        var owner = Assert.Single(Assert.IsAssignableFrom<IReadOnlyList<ShellCompoundRedirectOwner>>(
            GetOwners(analysis)));
        var redirect = Assert.Single(owner.Syntax.Redirects);
        var context = TestToolExecutionContext.CreateBound(
            "signalr/compound-source-binding",
            "/work/session",
            TrustAudience.Personal);

        var sourceBinder = SourceBinder(source, cwd, owner.Syntax);
        var fileOperation = FileOperation("write /work/repo/result.txt", cwd, owner.Syntax, redirect);
        Assert.True(TryCreate([sourceBinder, fileOperation], out var validProjection));
        Assert.All(validProjection!.Candidates, candidate =>
        {
            Assert.False(candidate.CanRequestStoredGrant);
            Assert.False(candidate.CanUseRealReviewedSafePolicy);
        });
        Assert.False(TryCreate([SourceBinder("for changed in one; do value=1; done > result.txt", cwd, owner.Syntax)], out _));

        var foreignAnalysis = new ShellCommandAnalyzer(environment).Analyze(source, cwd);
        var foreignOwner = Assert.Single(Assert.IsAssignableFrom<IReadOnlyList<ShellCompoundRedirectOwner>>(
            GetOwners(foreignAnalysis)));
        Assert.False(TryCreate([SourceBinder(source, cwd, foreignOwner.Syntax)], out _));

        var wrongOperation = FileOperation("read /work/repo/result.txt", cwd, owner.Syntax, redirect);
        Assert.False(TryCreate([wrongOperation], out _));

        Assert.False(TryCreate([sourceBinder with { RunsNoProgram = true }], out _));
        Assert.False(TryCreate([sourceBinder with { Unresolved = ShellUnresolvedPart.None }], out _));
        Assert.False(TryCreate([sourceBinder with { UsesFileToolRedirects = true }], out _));
        Assert.False(TryCreate([fileOperation with { RunsNoProgram = true }], out _));
        Assert.False(TryCreate([fileOperation with { Unresolved = ShellUnresolvedPart.None }], out _));
        Assert.False(TryCreate([fileOperation with { UsesFileToolRedirects = false }], out _));

        bool TryCreate(IReadOnlyList<ApprovalCandidate> candidates, out ShellPolicyProjection? projection)
        {
            var approvalContext = new ToolApprovalContext(
                ShellTool.ToolName,
                source,
                candidates.Select(static candidate => candidate.Verb).ToArray(),
                candidates.Select(static candidate => candidate.Verb).ToArray(),
                [],
                Cwd: cwd,
                Candidates: candidates);
            return ShellPolicyProjection.TryCreate(
                environment,
                approvalContext,
                directoryScopes: null,
                context,
                analysis,
                out projection);
        }

        static IReadOnlyList<ShellCompoundRedirectOwner> GetOwners(ShellCommandAnalysis analysis)
        {
            Assert.True(analysis.TryGetCompoundRedirectOwners(out var owners));
            return Assert.IsAssignableFrom<IReadOnlyList<ShellCompoundRedirectOwner>>(owners);
        }

        static ApprovalCandidate SourceBinder(
            string text,
            string directory,
            RedirectedCompoundSyntax owner)
            => new(text, directory)
            {
                Shell = ApprovalShell.Bash,
                VerbTokens = null,
                Unresolved = ShellUnresolvedPart.Command,
                SourceCompoundOwner = owner,
            };

        static ApprovalCandidate FileOperation(
            string text,
            string directory,
            RedirectedCompoundSyntax owner,
            CompoundRedirect redirect)
            => new(text, directory)
            {
                Shell = ApprovalShell.Bash,
                VerbTokens = null,
                Unresolved = ShellUnresolvedPart.Command,
                UsesFileToolRedirects = true,
                SourceCompoundOwner = owner,
                SourceCompoundRedirect = redirect,
            };
    }

    [Theory]
    [InlineData(false, "head /external/file.log", "/work", "/external/file.log")]
    [InlineData(true, @"Get-Content C:\external\file.log", @"C:\work", @"C:\external\file.log")]
    public void Absolute_paths_are_not_rebased_beneath_the_resolution_base(
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

        var facts = ShellPolicyOccurrencePathFacts.Create(occurrence).Resolve(
            resolutionBase,
            environment.PathStyle,
            windowsStyle ? ApprovalShell.PowerShell : ApprovalShell.Bash);

        Assert.Contains(
            facts.Facts,
            fact => fact.Source.Origin == ShellPolicyPathOrigin.EffectiveArgument
                    && fact.State == ShellPolicyPathResolutionState.Known
                    && fact.Paths.Any(path => path.Value == expected));
    }

    [Fact]
    public void Execution_views_retain_provider_qualified_power_shell_paths()
    {
        var environment = ShellExecutionEnvironment.CreatePowerShell(
            @"C:\Program Files\PowerShell\7\pwsh.exe",
            PwshDialect.PowerShell7);
        var analysis = new ShellCommandPolicy(environment)
            .Analyze(@"Get-Content 'FileSystem::C:\external\file.log'");

        var view = Assert.Single(ShellPolicyPathFacts.CreateExecutionViews(analysis));

        Assert.Contains(
            view.Facts,
            fact => fact.State == ShellPolicyPathResolutionState.Known
                    && fact.Paths.Any(path => string.Equals(
                        path.Value,
                        @"C:\external\file.log",
                        StringComparison.OrdinalIgnoreCase)));
    }

    [Theory]
    [InlineData(@"\external\file.log")]
    [InlineData(@"D:file.log")]
    [InlineData(@"FileSystem::C:\external\file.log")]
    public void Ambiguous_windows_root_forms_remain_strict(string value)
    {
        Assert.False(CanonicalPath.TryCreate(
            value,
            @"C:\work",
            ShellPathStyle.Windows,
            out _));
    }

    [Fact]
    public void Candidate_scope_remains_separate_from_the_command_base()
    {
        var environment = ShellExecutionEnvironment.CreateBash(ShellPlatform.Linux);
        var occurrence = Assert.Single(
            new ShellCommandPolicy(environment)
                .Analyze("cat /work/sub/file.txt", "/work")
                .Commands);
        var candidate = Candidate(
            occurrence,
            directory: "/work/sub",
            ApprovalShell.Bash,
            "cat");

        var facts = Assert.Single(ShellPolicyPathFacts.Create(
            [candidate],
            ShellPathStyle.Posix));

        Assert.Equal("/work/sub", facts.RealScope.Path?.Value);
        Assert.Equal("/work", facts.Real.ResolutionBase.Path?.Value);
        Assert.Contains(
            facts.Real.Facts,
            fact => fact.Source.Origin == ShellPolicyPathOrigin.EffectiveArgument
                    && fact.Paths.Any(path => path.Value == "/work/sub/file.txt"));
    }

    [Fact]
    public void Intent_and_real_resolutions_remain_distinct()
    {
        var environment = ShellExecutionEnvironment.CreateBash(ShellPlatform.Linux);
        var occurrence = Assert.Single(
            new ShellCommandPolicy(environment).Analyze("head result.log", "/work").Commands);
        var candidate = Candidate(
            occurrence,
            directory: "/work",
            ApprovalShell.Bash,
            "head") with
        {
            Role = ShellPolicyCandidateRole.CausalIntentConsumer,
            IntentDirectory = "/tmp"
        };

        var facts = Assert.Single(ShellPolicyPathFacts.Create(
            [candidate],
            ShellPathStyle.Posix));

        Assert.Equal("/tmp", facts.Intent?.ResolutionBase.Path?.Value);
        Assert.Equal("/work", facts.Real.ResolutionBase.Path?.Value);
        Assert.Contains(
            Assert.IsType<ShellPolicyResolvedPathView>(facts.Intent).Facts,
            fact => fact.Paths.Any(path => path.Value == "/tmp/result.log"));
        Assert.Contains(
            facts.Real.Facts,
            fact => fact.Paths.Any(path => path.Value == "/work/result.log"));
    }

    [Theory]
    [InlineData(null, nameof(ShellPolicyPathResolutionState.UnknownDynamic))]
    [InlineData("relative", nameof(ShellPolicyPathResolutionState.InvalidKnownValue))]
    [InlineData("/work", nameof(ShellPolicyPathResolutionState.Known))]
    public void Scope_resolution_distinguishes_unknown_invalid_and_known(
        string? value,
        string expectedName)
    {
        var expected = Enum.Parse<ShellPolicyPathResolutionState>(expectedName);
        var scope = ShellPolicyPathFacts.ResolveScope(value, ShellPathStyle.Posix);

        Assert.Equal(expected, scope.State);
        Assert.Equal(expected == ShellPolicyPathResolutionState.Known, scope.Path is not null);
    }

    [Fact]
    public void Dynamic_redirects_remain_unknown_instead_of_invalid()
    {
        var environment = ShellExecutionEnvironment.CreatePowerShell(
            @"C:\Program Files\PowerShell\7\pwsh.exe",
            PwshDialect.PowerShell7);
        var occurrence = Assert.Single(
            new ShellCommandPolicy(environment)
                .Analyze("Get-Date > $name", @"C:\work")
                .Commands);

        var facts = ShellPolicyOccurrencePathFacts.Create(occurrence).Resolve(
            @"C:\work",
            ShellPathStyle.Windows,
            ApprovalShell.PowerShell);

        Assert.Contains(
            facts.Facts,
            fact => fact.Source.Origin == ShellPolicyPathOrigin.Redirect
                    && fact.Source.Domain is ShellValueDomain.Unknown
                    && fact.State == ShellPolicyPathResolutionState.UnknownDynamic);
        Assert.DoesNotContain(
            facts.Facts,
            static fact => fact.State == ShellPolicyPathResolutionState.InvalidKnownValue);
    }

    [Fact]
    public void Redirects_retain_mode_completeness_and_domain()
    {
        var environment = ShellExecutionEnvironment.CreateBash(ShellPlatform.Linux);
        var occurrence = Assert.Single(
            new ShellCommandPolicy(environment)
                .Analyze("cat input.txt > output.txt", "/work")
                .Commands);

        var facts = ShellPolicyOccurrencePathFacts.Create(occurrence).Resolve(
            "/work",
            ShellPathStyle.Posix,
            ApprovalShell.Bash);
        var redirect = Assert.Single(
            facts.Facts,
            static fact => fact.Source.Origin == ShellPolicyPathOrigin.Redirect);

        Assert.Equal(FileRedirectMode.Output, redirect.Source.RedirectMode);
        Assert.True(redirect.Source.RedirectIsComplete);
        Assert.IsType<ShellValueDomain.Exact>(redirect.Source.Domain);
        Assert.Equal(ShellPolicyPathResolutionState.Known, redirect.State);
        Assert.Equal("/work/output.txt", Assert.Single(redirect.Paths).Value);
    }

    [Theory]
    [InlineData(@"Get-ChildItem -Path C:\WORK\PROJECT -Recurse", @"C:\WORK\PROJECT")]
    [InlineData(@"Get-ChildItem -Path C:\WORK\PROJECT\*.cs", @"C:\WORK\PROJECT")]
    public void Power_shell_tree_roots_use_the_parser_owned_path_fact(
        string command,
        string expectedRoot)
    {
        var environment = ShellExecutionEnvironment.CreatePowerShell(
            @"C:\Program Files\PowerShell\7\pwsh.exe",
            PwshDialect.PowerShell7);
        var occurrence = Assert.Single(
            new ShellCommandPolicy(environment)
                .Analyze(command, @"C:\WORK\PROJECT")
                .Commands);

        var facts = ShellPolicyOccurrencePathFacts.Create(occurrence).Resolve(
            @"C:\WORK\PROJECT",
            ShellPathStyle.Windows,
            ApprovalShell.PowerShell);
        var root = Assert.Single(
            facts.Facts,
            static fact => fact.Source.Origin == ShellPolicyPathOrigin.FileSystemTreeRoot);

        Assert.Equal(ShellPolicyPathResolutionState.Known, root.State);
        Assert.Equal(expectedRoot, Assert.Single(root.Paths).Value);
    }

    [Fact]
    public void Dynamic_power_shell_tree_state_stays_unknown()
    {
        var environment = ShellExecutionEnvironment.CreatePowerShell(
            @"C:\Windows\System32\WindowsPowerShell\v1.0\powershell.exe",
            PwshDialect.WindowsPowerShell51);
        var occurrence = Assert.Single(
            new ShellCommandPolicy(environment)
                .Analyze(
                    @"Get-ChildItem -Path C:\WORK\PROJECT -Recurse:$flag",
                    @"C:\WORK\PROJECT")
                .Commands);

        var facts = ShellPolicyOccurrencePathFacts.Create(occurrence).Resolve(
            @"C:\WORK\PROJECT",
            ShellPathStyle.Windows,
            ApprovalShell.PowerShell);
        var root = Assert.Single(
            facts.Facts,
            static fact => fact.Source.Origin == ShellPolicyPathOrigin.FileSystemTreeRoot);

        Assert.IsType<ShellValueDomain.Unknown>(root.Source.Domain);
        Assert.Equal(ShellPolicyPathResolutionState.UnknownDynamic, root.State);
        Assert.Empty(root.Paths);
    }

    [Fact]
    public void Uncovered_context_is_recomputed_for_coverage_and_session_scope()
    {
        var evaluation = CreateEvaluation(
            BashCandidate("git status", "/work/repo"),
            BashCandidate("git push", "/work/repo"));
        var sessionOwned = evaluation.GetUncoveredApprovalContext(["/work/repo"]);

        evaluation.Cover(evaluation.Candidates[0], new Coverage.ReviewedSafe(ReviewedSafeRoot.Real));
        var remaining = evaluation.GetUncoveredApprovalContext(["/work/session"]);

        Assert.NotSame(sessionOwned, remaining);
        Assert.Equal([evaluation.Candidates[1].Candidate], remaining.Candidates);
        Assert.DoesNotContain(
            sessionOwned.Options,
            static option => option.Key == ApprovalOptionKeys.ApproveAlwaysKey);
        Assert.Contains(
            remaining.Options,
            static option => option.Key == ApprovalOptionKeys.ApproveAlwaysKey);
    }

    [Theory]
    [InlineData("duplicate")]
    [InlineData("identity")]
    [InlineData("id")]
    [InlineData("session")]
    [InlineData("persistent")]
    public void Invalid_coverage_mutations_are_atomic(string mutation)
    {
        var evaluation = CreateEvaluation(BashCandidate("git status", "/work"));
        var candidate = Assert.Single(evaluation.Candidates);
        if (mutation == "duplicate")
            evaluation.Cover(candidate, new Coverage.ReviewedSafe(ReviewedSafeRoot.Real));

        Action apply = mutation switch
        {
            "duplicate" => () => evaluation.Cover(
                candidate,
                Coverage.OneTime.Instance),
            "identity" => () => evaluation.Cover(
                candidate with { Candidate = BashCandidate("git push", "/work") },
                new Coverage.ReviewedSafe(ReviewedSafeRoot.Real)),
            "id" => () => evaluation.Cover(
                candidate with { Id = new ShellPolicyCandidateId(7) },
                new Coverage.ReviewedSafe(ReviewedSafeRoot.Real)),
            "session" => () => evaluation.Cover(
                candidate,
                new Coverage.Stored(GrantScope.Session.Instance, GrantedAt: null)),
            "persistent" => () => evaluation.Cover(
                candidate,
                new Coverage.Stored(GrantScope.Everywhere.Instance, GrantedAt: null)),
            _ => throw new ArgumentOutOfRangeException(nameof(mutation))
        };

        Assert.Throws<InvalidOperationException>(apply);

        Assert.Equal(mutation == "duplicate", evaluation.IsCovered(candidate.Id));
        var coveredRows = evaluation.Complete(
                ToolAuthorizationDecision.Deny("internal_policy_failure"))
            .ShellPolicyTrace.Rows
            .Where(static row => row.Outcome == ShellPolicyTraceOutcome.Covered)
            .ToArray();
        if (mutation == "duplicate")
            Assert.Single(coveredRows);
        else
            Assert.Empty(coveredRows);
    }

    [Fact]
    public void Actor_evidence_cannot_cover_a_different_evaluation_candidate()
    {
        var source = CreateEvaluation(BashCandidate("git status", "/work/repo"));
        var target = CreateEvaluation(BashCandidate("git push", "/work/repo"));
        var sourceCandidate = Assert.Single(source.Candidates);
        var grantCandidate = new ShellGrantCandidate(
            sourceCandidate.Id,
            sourceCandidate.Candidate,
            source.Projection.ApprovalContext.Cwd);
        var evidence = ShellApprovalMatchResult.Create(
            [grantCandidate],
            persistentStoreFailure: null,
            [ShellGrantCandidateResult.Session(grantCandidate)]);

        Assert.Throws<ArgumentException>(() => target.ApplyActorEvidence(evidence));
        Assert.False(target.IsCovered(Assert.Single(target.Candidates).Id));
    }

    [Fact]
    public void Actor_evidence_after_local_coverage_fails_atomically()
    {
        var evaluation = CreateEvaluation(
            BashCandidate("git status", "/work/repo"),
            BashCandidate("git push", "/work/repo"));
        var candidates = evaluation.Candidates;
        evaluation.Cover(candidates[1], new Coverage.ReviewedSafe(ReviewedSafeRoot.Real));
        var grantCandidates = candidates.Select(candidate => new ShellGrantCandidate(
                candidate.Id,
                candidate.Candidate,
                evaluation.Projection.ApprovalContext.Cwd))
            .ToArray();
        var evidence = ShellApprovalMatchResult.Create(
            grantCandidates,
            ApprovalStoreFailure.InvalidData,
            grantCandidates.Select(static candidate =>
                    ShellGrantCandidateResult.Uncovered(candidate))
                .ToArray());

        Assert.Throws<InvalidOperationException>(() => evaluation.ApplyActorEvidence(evidence));

        Assert.False(evaluation.IsCovered(candidates[0].Id));
        Assert.True(evaluation.IsCovered(candidates[1].Id));
        Assert.Null(evaluation.PersistentStoreFailure);
        Assert.Empty(evaluation.ApprovalMatches);
        var trace = evaluation.Complete(
            ToolAuthorizationDecision.Deny("internal_policy_failure"))
            .ShellPolicyTrace;
        Assert.DoesNotContain(
            trace.Rows,
            static row => row.Stage == ShellPolicyTraceStage.StoredGrantMatch);
        Assert.Single(
            trace.Rows,
            static row => row.Stage == ShellPolicyTraceStage.ReviewedSafePolicy);
    }

    private static ShellPolicyCandidate Candidate(
        CommandOccurrence occurrence,
        string directory,
        ApprovalShell shell,
        params string[] verbTokens)
        => new(
            new ShellPolicyCandidateId(0),
            new ApprovalCandidate(string.Join(' ', verbTokens), directory)
            {
                Shell = shell,
                VerbTokens = Array.AsReadOnly(verbTokens)
            },
            occurrence);

    private static ShellPolicyEvaluation CreateEvaluation(params ApprovalCandidate[] candidates)
    {
        var environment = ShellExecutionEnvironment.CreateBash(ShellPlatform.Linux);
        var approvalContext = new ToolApprovalContext(
            ShellTool.ToolName,
            "shell command",
            candidates.Select(static candidate => candidate.Verb).ToArray(),
            candidates.Select(static candidate => candidate.Verb).ToArray(),
            [],
            Cwd: "/work/repo",
            Candidates: candidates);
        var context = TestToolExecutionContext.CreateBound(
            "signalr/shell-policy-path-facts",
            "/work/session",
            TrustAudience.Personal);
        var analysis = new ShellCommandAnalyzer(environment).Analyze("echo", "/work/repo");

        Assert.True(ShellPolicyProjection.TryCreate(
            environment,
            approvalContext,
            directoryScopes: null,
            context,
            analysis,
            out var projection));
        return new ShellPolicyEvaluation(
            Assert.IsType<ShellPolicyProjection>(projection),
            new ShellPolicyDecisionTraceBuilder());
    }

    private static ApprovalCandidate BashCandidate(string verb, string directory) =>
        new(verb, directory)
        {
            Shell = ApprovalShell.Bash,
            VerbTokens = Array.AsReadOnly(verb.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        };
}
