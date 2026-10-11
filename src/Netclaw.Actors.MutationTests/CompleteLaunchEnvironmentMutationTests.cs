// -----------------------------------------------------------------------
// <copyright file="CompleteLaunchEnvironmentMutationTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Actors.Tools;
using Netclaw.Configuration;
using Netclaw.Security;
using Netclaw.Tools;
using ShellSyntaxTree;
using Xunit;

namespace Netclaw.Actors.MutationTests;

/// <summary>
/// Owner decision F3: the parser gets the names of the one environment snapshot
/// that each shell process receives, and an assignment that stays in the shell
/// does not qualify a grant.
/// </summary>
public sealed class CompleteLaunchEnvironmentMutationTests
{
    private static readonly ToolName ShellToolName = new(ShellTool.ToolName);

    private static readonly KeyValuePair<string, string>[] DaemonEnvironment =
    [
        new("PATH", "/usr/bin"),
        new("GIT_DIR", "/work/.git"),
        new("BASH_ENV", "/work/startup.sh"),
    ];

    private static readonly ShellExecutionEnvironment Bash52 =
        ShellExecutionEnvironment.CreateBash(ShellPlatform.Linux, new Version(5, 2), DaemonEnvironment);

    [Fact]
    public void Parser_names_are_the_names_of_the_process_environment()
    {
        var names = Bash52.CreateLaunchEnvironment(temporary: null).CompleteEnvironmentNames;
        var process = Bash52.CreateProcessStartInfo("true").Environment;

        Assert.NotNull(names);
        Assert.Equal(
            process.Keys.Append("PWD").Append("TMPDIR").Append("TMP").Append("TEMP").Order(StringComparer.Ordinal),
            names.Order(StringComparer.Ordinal));
        Assert.Contains("PATH", names);
        // The launcher sets HOME only when the daemon home is a POSIX path, so
        // the value is null on a Windows host and the home folder elsewhere.
        Assert.Equal(Bash52.HomeDirectory, process.TryGetValue("HOME", out var home) ? home : null);
        Assert.Contains("GIT_DIR", names);
        Assert.DoesNotContain("BASH_ENV", names);
        Assert.False(process.ContainsKey("BASH_ENV"));
    }

    [Fact]
    public void PowerShell_declares_no_names_and_keeps_its_environment()
    {
        var environment = ShellExecutionEnvironment.CreatePowerShell(
            @"C:\Program Files\PowerShell\7\pwsh.exe",
            PwshDialect.PowerShell7);

        Assert.Null(environment.CreateLaunchEnvironment(temporary: null).CompleteEnvironmentNames);
    }

    [Theory]
    [InlineData("b=1; env", false)]
    [InlineData("b=$(date); env", false)]
    [InlineData("b=1; export b; env", true)]
    [InlineData("b=1 env", true)]
    [InlineData("GIT_DIR=/tmp/x; env", true)]
    public void Only_an_assignment_that_can_reach_the_program_qualifies_the_grant(string command, bool qualified)
    {
        var candidate = Single(command, "env");

        Assert.Equal(qualified, candidate.AssignmentDigest is not null);
    }

    // SECURITY: a data command reads no environment. Its digest guards an
    // operand that is not proved data, so it keeps the assignment and stays
    // out of the approval exemption.
    [Fact]
    public void Data_command_keeps_an_assignment_that_stays_in_the_shell()
    {
        var candidate = Single("d=key; echo ../netclaw/\"${d}s\"/*", "echo");

        Assert.NotNull(candidate.AssignmentDigest);
        Assert.False(ApprovalPatternMatching.IsPureSideEffect(candidate));
    }

    // A twin gets the same rule: a data command keeps every assignment.
    [Fact]
    public void Twin_candidates_keep_only_the_assignments_that_can_reach_them()
    {
        var policy = new ShellCommandPolicy(Bash52);
        var analysis = policy.Analyze("x=1; for n in a b; do gh api x/$n > out.txt; echo x > \"$n\"; done", "/work");
        var program = new ApprovalCandidate("gh api", "/work") { Shell = ApprovalShell.Bash, VerbTokens = ["gh", "api"] };
        var output = new ApprovalCandidate("echo", "/work") { Shell = ApprovalShell.Bash, VerbTokens = ["echo"] };

        Assert.True(ShellApprovalMatcher.TryQualifyTwinCandidates([program, output], analysis.Commands[1], out var qualified));

        Assert.Null(qualified[0].AssignmentDigest);
        Assert.NotNull(qualified[1].AssignmentDigest);
    }

    private static ApprovalCandidate Single(string command, string program)
    {
        var matcher = new ShellApprovalMatcher(Bash52);
        var approval = matcher.AnalyzeInvocation(
            ShellToolName,
            new Dictionary<string, object?> { ["Command"] = command, ["WorkingDirectory"] = "/work" });

        Assert.False(approval.IsMessy, approval.DisplayText);
        return Assert.Single(approval.Candidates, candidate => candidate.VerbTokens?[0] == program);
    }
}
