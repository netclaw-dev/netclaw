// -----------------------------------------------------------------------
// <copyright file="ShellPolicyPathFactsTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Actors.Protocol;
using Netclaw.Actors.Tools;
using Netclaw.Configuration;
using Netclaw.Security;
using Netclaw.Tests.Utilities;
using ShellSyntaxTree;
using Xunit;

namespace Netclaw.Actors.Tests.Tools;

public sealed class ShellPolicyPathFactsTests
{
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
                    && fact.Paths.Any(path => ShellPathRules.Equals(
                        path.Value,
                        @"C:\external\file.log",
                        ShellPathStyle.Windows)));
    }

    [Theory]
    [InlineData(@"\external\file.log")]
    [InlineData(@"D:file.log")]
    [InlineData(@"FileSystem::C:\external\file.log")]
    public void Ambiguous_windows_root_forms_remain_strict(string value)
    {
        Assert.False(ShellPolicyOccurrencePathFacts.TryResolveCanonicalPath(
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
    public void Intent_and_fallback_resolutions_remain_distinct()
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
            IntentDirectory = "/tmp",
            IntentFallbackDirectories = ["/work"]
        };

        var facts = Assert.Single(ShellPolicyPathFacts.Create(
            [candidate],
            ShellPathStyle.Posix));

        Assert.Equal("/tmp", facts.Intent?.ResolutionBase.Path?.Value);
        Assert.Equal("/work", Assert.Single(facts.Fallbacks).ResolutionBase.Path?.Value);
        Assert.Contains(
            Assert.IsType<ShellPolicyResolvedPathView>(facts.Intent).Facts,
            fact => fact.Paths.Any(path => path.Value == "/tmp/result.log"));
        Assert.Contains(
            Assert.Single(facts.Fallbacks).Facts,
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

    [Fact]
    public void Uncovered_context_is_recomputed_for_coverage_and_session_scope()
    {
        var evaluation = CreateEvaluation(
            BashCandidate("git status", "/work/repo"),
            BashCandidate("git push", "/work/repo"));
        var sessionOwned = evaluation.GetUncoveredApprovalContext(["/work/repo"]);

        evaluation.Cover(evaluation.Candidates[0], ShellPolicyCoverageSource.Session);
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
    [InlineData("coverage")]
    [InlineData("timestamp")]
    public void Invalid_coverage_mutations_are_atomic(string mutation)
    {
        var evaluation = CreateEvaluation(BashCandidate("git status", "/work"));
        var candidate = Assert.Single(evaluation.Candidates);
        if (mutation == "duplicate")
            evaluation.Cover(candidate, ShellPolicyCoverageSource.Session);

        Action apply = mutation switch
        {
            "duplicate" => () => evaluation.Cover(
                candidate,
                ShellPolicyCoverageSource.PersistentGlobal),
            "identity" => () => evaluation.Cover(
                candidate with { Candidate = BashCandidate("git push", "/work") },
                ShellPolicyCoverageSource.Session),
            "id" => () => evaluation.Cover(
                candidate with { Id = new ShellPolicyCandidateId(7) },
                ShellPolicyCoverageSource.Session),
            "coverage" => () => evaluation.Cover(
                candidate,
                (ShellPolicyCoverageSource)999),
            "timestamp" => () => evaluation.Cover(
                candidate,
                ShellPolicyCoverageSource.Session,
                new DateTimeOffset(2026, 8, 14, 0, 0, 0, TimeSpan.Zero)),
            _ => throw new ArgumentOutOfRangeException(nameof(mutation))
        };

        Assert.Throws<InvalidOperationException>(apply);

        Assert.Equal(
            mutation == "duplicate"
                ? ShellPolicyCoverageSource.Session
                : ShellPolicyCoverageSource.Uncovered,
            evaluation.CoverageFor(candidate.Id));
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

        Assert.True(ShellPolicyProjection.TryCreate(
            environment,
            new ShellApprovalMatcher(environment),
            execution: null,
            approvalContext,
            context,
            static _ => false,
            out var projection));
        return new ShellPolicyEvaluation(Assert.IsType<ShellPolicyProjection>(projection));
    }

    private static ApprovalCandidate BashCandidate(string verb, string directory) => new(verb, directory)
    {
        Shell = ApprovalShell.Bash,
        VerbTokens = Array.AsReadOnly(verb.Split(' ', StringSplitOptions.RemoveEmptyEntries))
    };
}
