// -----------------------------------------------------------------------
// <copyright file="ShellAssignmentMutationTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Collections.Immutable;
using System.Reflection;
using Netclaw.Actors.Protocol;
using Netclaw.Actors.Sessions;
using Netclaw.Actors.Tools;
using Netclaw.Configuration;
using Netclaw.Security;
using Netclaw.Tools;
using ShellSyntaxTree;
using Xunit;

namespace Netclaw.Actors.MutationTests;

public sealed class ShellAssignmentMutationTests
{
    private static readonly ApprovalAssignmentDigest FirstDigest = new(
        $"sha256:{new string('a', 64)}");
    private static readonly ApprovalAssignmentDigest SecondDigest = new(
        $"sha256:{new string('b', 64)}");

    [Fact]
    public void Assignment_grants_require_the_same_explicit_constraint()
    {
        var qualified = Candidate(ApprovalAssignmentConstraint.ExactDigest(FirstDigest));
        var unqualified = Candidate(ApprovalAssignmentConstraint.None);
        var matching = Entry(FirstDigest);
        var changed = Entry(SecondDigest);
        var absent = Entry(assignmentDigest: null);

        Assert.True(ApprovalPatternMatching.MatchesShellApproval(
            qualified,
            "/work",
            [matching]));
        Assert.False(ApprovalPatternMatching.MatchesShellApproval(
            qualified,
            "/work",
            [changed]));
        Assert.False(ApprovalPatternMatching.MatchesShellApproval(
            qualified,
            "/work",
            [absent]));
        Assert.True(ApprovalPatternMatching.MatchesShellApproval(
            unqualified,
            "/work",
            [absent]));
        Assert.False(ApprovalPatternMatching.MatchesShellApproval(
            unqualified,
            "/work",
            [matching]));
        Assert.False(ApprovalPatternMatching.MatchesShellApproval(
            Candidate(default),
            "/work",
            [absent]));
    }

    [Fact]
    public void Reviewed_safe_policy_rejects_an_assignment_constraint()
    {
        var environment = ShellExecutionEnvironment.CreateBash(
            ShellPlatform.Linux,
            new Version(5, 2));
        var matcher = new ShellApprovalMatcher(environment);
        var qualified = Assert.Single(matcher.ExtractCandidates(
            new ToolName("shell_execute"),
            Arguments("mode='fast'; grep item")));
        var unqualified = Assert.Single(matcher.ExtractCandidates(
            new ToolName("shell_execute"),
            Arguments("grep item")));
        var policy = new ReviewedSafeShellPolicy(
            SafeVerbList.FromVerbs(ApprovalShell.Bash, ["grep"]),
            new PathAccessPolicy(
                new ToolConfig(),
                new NetclawPaths(),
                new ToolPathPolicy(environment, [])));

        Assert.False(policy.IsReviewedDiagnosticInvocation(
            [qualified],
            ShellPathStyle.Posix));
        Assert.True(policy.IsReviewedDiagnosticInvocation(
            [unqualified],
            ShellPathStyle.Posix));
    }

    [Fact]
    public void PowerShell_assignment_span_can_cover_only_one_opaque_node()
    {
        const string Source = " $mode='fast'; inspect item";
        var environment = ShellExecutionEnvironment.CreatePowerShell(
            @"C:\PowerShell\7\pwsh.exe",
            PwshDialect.PowerShell7);
        var parsed = environment.Parse(Source, @"C:\work");
        var block = Assert.IsType<ShellBlockSyntax>(parsed.Syntax);
        var list = Assert.IsType<CommandListSyntax>(Assert.Single(block.Statements));
        var assignmentNode = list.Items[0].Command;
        Assert.True(ShellCommandAnalysis.AssignmentSyntaxReconciliation.TryCreate(
            Source,
            parsed.Commands,
            out var reconciliation));

        Assert.True(reconciliation.TryConsume(assignmentNode));
        Assert.True(reconciliation.AllConsumed);
        Assert.False(reconciliation.TryConsume(assignmentNode));
        Assert.False(reconciliation.TryConsume(list.Items[1].Command));
    }

    [Fact]
    public void Bash_strong_initial_state_requires_the_complete_host_identity()
    {
        AssertBounded(ShellExecutionEnvironment.CreateBash(
            ShellPlatform.Linux,
            new Version(5, 2)));
        AssertBounded(ShellExecutionEnvironment.CreateBash(
            ShellPlatform.Linux,
            new Version(5, 3)));

        AssertUnknown(ShellExecutionEnvironment.CreateBash(
            ShellPlatform.Linux,
            new Version(5, 1)));
        AssertUnknown(ShellExecutionEnvironment.CreateBash(
            ShellPlatform.Linux,
            new Version(5, 4)));
        AssertUnknown(CreateBashEnvironment(
            "/usr/bin/bash",
            ["-c"],
            new Version(5, 2)));
        AssertUnknown(CreateBashEnvironment(
            "/bin/bash",
            ["--noprofile", "-c"],
            new Version(5, 2)));
    }

    [Fact]
    public void Bash_sanitizer_removes_every_startup_and_loader_override()
    {
        var exactNames = new[]
        {
            "BASH_ENV",
            "ENV",
            "SHELLOPTS",
            "BASHOPTS",
            "CDPATH",
            "GLOBIGNORE",
            "IFS",
            "POSIXLY_CORRECT",
            "BASH_COMPAT",
            "LIBPATH",
            "SHLIB_PATH",
        };
        var familyNames = new[]
        {
            "BASH_FUNC_tool%%",
            "LD_PRELOAD",
            "DYLD_INSERT_LIBRARIES",
        };
        var environment = exactNames
            .Concat(familyNames)
            .ToDictionary(static name => name, static _ => (string?)"blocked");
        environment["ORDINARY_VALUE"] = "preserved";

        ShellExecutionEnvironment.RemoveBashStartupOverrides(environment);

        Assert.All(exactNames, name => Assert.DoesNotContain(name, environment.Keys));
        Assert.All(familyNames, name => Assert.DoesNotContain(name, environment.Keys));
        Assert.Equal("preserved", environment["ORDINARY_VALUE"]);
    }

    [Fact]
    public void Assignment_prompts_never_emit_a_legacy_reusable_option_key()
    {
        var profileType = typeof(ToolAccessPolicy).GetNestedType(
            "ApprovalOptionProfile",
            BindingFlags.NonPublic)!;
        var profile = Enum.Parse(profileType, "StandardWithDirectory");
        var method = typeof(ToolAccessPolicy).GetMethod(
            "BuildApprovalOptions",
            BindingFlags.Static | BindingFlags.NonPublic)!;

        var qualified = Assert.IsAssignableFrom<IReadOnlyList<ToolApprovalOption>>(
            method.Invoke(null, [profile, true, true]));
        var unqualified = Assert.IsAssignableFrom<IReadOnlyList<ToolApprovalOption>>(
            method.Invoke(null, [profile, true, false]));

        Assert.True(ToolAccessPolicy.HasAssignmentConstraint(
            [Candidate(ApprovalAssignmentConstraint.ExactDigest(FirstDigest))]));
        Assert.True(ToolAccessPolicy.HasAssignmentConstraint(
            [
                Candidate(ApprovalAssignmentConstraint.None),
                Candidate(ApprovalAssignmentConstraint.ExactDigest(FirstDigest)),
            ]));
        Assert.False(ToolAccessPolicy.HasAssignmentConstraint(
            [Candidate(ApprovalAssignmentConstraint.None)]));
        Assert.False(ToolAccessPolicy.HasAssignmentConstraint(
            [Candidate(default)]));
        Assert.All(
            new[]
            {
                ApprovalOptionKeys.ApproveAssignmentSessionV1,
                ApprovalOptionKeys.ApproveAssignmentAlwaysV1,
                ApprovalOptionKeys.ApproveAssignmentRepositoryV1,
                ApprovalOptionKeys.ApproveAssignmentEverywhereV1,
            },
            optionKey => Assert.False(LlmSessionActor.IsOfferedApprovalOption(
                [],
                optionKey,
                "/work/repository/.git")));

        foreach (var (assignmentKey, legacyKey) in new[]
                 {
                     (ApprovalOptionKeys.ApproveAssignmentSessionV1, ApprovalOptionKeys.ApproveSession),
                     (ApprovalOptionKeys.ApproveAssignmentAlwaysV1, ApprovalOptionKeys.ApproveAlways),
                     (ApprovalOptionKeys.ApproveAssignmentEverywhereV1, ApprovalOptionKeys.ApproveEverywhere),
                 })
        {
            Assert.True(LlmSessionActor.IsOfferedApprovalOption(
                [assignmentKey],
                assignmentKey,
                repositoryCommonDirectory: null));
            Assert.False(LlmSessionActor.IsOfferedApprovalOption(
                [legacyKey],
                assignmentKey,
                repositoryCommonDirectory: null));
            Assert.False(LlmSessionActor.IsOfferedApprovalOption(
                [assignmentKey],
                legacyKey,
                repositoryCommonDirectory: null));
        }

        Assert.False(LlmSessionActor.IsOfferedApprovalOption(
            [ApprovalOptionKeys.ApproveAssignmentRepositoryV1],
            ApprovalOptionKeys.ApproveAssignmentRepositoryV1,
            repositoryCommonDirectory: null));
        Assert.True(LlmSessionActor.IsOfferedApprovalOption(
            [ApprovalOptionKeys.ApproveAssignmentRepositoryV1],
            ApprovalOptionKeys.ApproveAssignmentRepositoryV1,
            "/work/repository/.git"));
        Assert.False(LlmSessionActor.IsOfferedApprovalOption(
            [ApprovalOptionKeys.ApproveRepository],
            ApprovalOptionKeys.ApproveAssignmentRepositoryV1,
            "/work/repository/.git"));
        Assert.False(LlmSessionActor.IsOfferedApprovalOption(
            [ApprovalOptionKeys.ApproveAssignmentRepositoryV1],
            ApprovalOptionKeys.ApproveRepository,
            "/work/repository/.git"));
        Assert.False(LlmSessionActor.IsOfferedApprovalOption(
            [ApprovalOptionKeys.ApproveRepository],
            ApprovalOptionKeys.ApproveRepository,
            repositoryCommonDirectory: null));
        Assert.True(LlmSessionActor.IsOfferedApprovalOption(
            [ApprovalOptionKeys.ApproveRepository],
            ApprovalOptionKeys.ApproveRepository,
            "/work/repository/.git"));
        Assert.False(LlmSessionActor.IsOfferedApprovalOption(
            [],
            ApprovalOptionKeys.ApproveRepository,
            "/work/repository/.git"));
        Assert.True(LlmSessionActor.IsOfferedApprovalOption(
            [],
            ApprovalOptionKeys.ApproveAlways,
            repositoryCommonDirectory: null));

        Assert.Equal(
            [
                ApprovalOptionKeys.ApproveOnce,
                ApprovalOptionKeys.ApproveAssignmentSessionV1,
                ApprovalOptionKeys.ApproveAssignmentAlwaysV1,
                ApprovalOptionKeys.ApproveAssignmentRepositoryV1,
                ApprovalOptionKeys.ApproveAssignmentEverywhereV1,
                ApprovalOptionKeys.Deny,
            ],
            qualified.Select(static option => option.Key.Value));
        Assert.Equal(
            [
                ApprovalOptionKeys.ApproveOnce,
                ApprovalOptionKeys.ApproveSession,
                ApprovalOptionKeys.ApproveAlways,
                ApprovalOptionKeys.ApproveRepository,
                ApprovalOptionKeys.ApproveEverywhere,
                ApprovalOptionKeys.Deny,
            ],
            unqualified.Select(static option => option.Key.Value));
    }

    private static ApprovalCandidate Candidate(ApprovalAssignmentConstraint constraint)
        => new("inspect", "/work", constraint)
        {
            Shell = ApprovalShell.Bash,
            VerbTokens = ["inspect"],
        };

    private static ApprovalEntry Entry(ApprovalAssignmentDigest? assignmentDigest)
        => ApprovalEntry.CreateTokenPrefix(
            ApprovalShell.Bash,
            ["inspect"],
            directory: "/work",
            assignmentDigest: assignmentDigest);

    private static Dictionary<string, object?> Arguments(string command) => new()
    {
        ["Command"] = command,
        ["WorkingDirectory"] = "/work",
    };

    private static void AssertBounded(ShellExecutionEnvironment environment)
    {
        var parsed = environment.Parse("mode='fast'; inspect item", "/work");

        Assert.False(parsed.IsUnparseable, parsed.UnparseableReason);
        Assert.Single(Assert.Single(parsed.Commands).Assignments);
    }

    private static void AssertUnknown(ShellExecutionEnvironment environment)
    {
        var parsed = environment.Parse("mode='fast'; inspect item", "/work");

        Assert.True(parsed.IsUnparseable);
        Assert.Empty(parsed.Commands);
    }

    private static ShellExecutionEnvironment CreateBashEnvironment(
        string executable,
        ImmutableArray<string> arguments,
        Version version)
    {
        var constructor = Assert.Single(typeof(ShellExecutionEnvironment)
            .GetConstructors(BindingFlags.Instance | BindingFlags.NonPublic));
        return Assert.IsType<ShellExecutionEnvironment>(constructor.Invoke(
        [
            ShellPlatform.Linux,
            executable,
            ShellGrammar.Bash,
            ShellPathStyle.Posix,
            arguments,
            version,
            null,
        ]));
    }
}
