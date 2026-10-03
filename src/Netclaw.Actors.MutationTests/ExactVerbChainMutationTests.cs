// -----------------------------------------------------------------------
// <copyright file="ExactVerbChainMutationTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Actors.Tools;
using Netclaw.Configuration;
using Netclaw.Security;
using Netclaw.Tools;
using Xunit;

namespace Netclaw.Actors.MutationTests;

/// <summary>
/// A shell grant covers exactly its verb chain. A mutant that turns the
/// equality check back into prefix matching lets a "gh" grant cover
/// "gh auth logout", so these tests must reject it.
/// </summary>
public sealed class ExactVerbChainMutationTests
{
    [Fact]
    public void Grant_covers_the_equal_verb_chain()
    {
        Assert.True(Matches(Grant("gh"), "gh"));
        Assert.True(Matches(Grant("gh", "pr", "view"), "gh", "pr", "view"));
    }

    [Fact]
    public void Bare_program_grant_does_not_cover_a_subcommand()
    {
        Assert.False(Matches(Grant("gh"), "gh", "auth", "logout"));
        Assert.False(Matches(Grant("git"), "git", "push"));
    }

    [Fact]
    public void Subcommand_grant_does_not_cover_a_longer_or_shorter_chain()
    {
        Assert.False(Matches(Grant("git", "push"), "git", "push", "origin"));
        Assert.False(Matches(Grant("gh", "pr", "view"), "gh", "pr"));
    }

    [Fact]
    public void Legacy_phrase_does_not_cover_a_longer_chain()
    {
        var legacy = ApprovalEntry.CreateLegacyExact(ApprovalShell.Bash, "git push origin");

        Assert.True(Matches(legacy, "git", "push", "origin"));
        Assert.False(Matches(legacy, "git", "push", "origin", "v1.5.1"));
    }

    // R1: a program path names its file. A mutant that skips the join with the
    // working directory lets a "./tool" grant run any file named tool.
    [Fact]
    public void Program_path_names_the_file_in_its_working_directory()
    {
        if (OperatingSystem.IsWindows())
            return;

        Assert.Equal("/opt/tools/ilspycmd", ProgramWord("cd /opt/tools && ./ilspycmd --version"));
        Assert.Equal("/opt/tools/ilspycmd", ProgramWord("cd /opt/other && ../tools/ilspycmd --version"));
        Assert.Equal("/opt/tools/ilspycmd", ProgramWord("/opt/./tools/ilspycmd --version"));
        Assert.Equal("dotnet", ProgramWord("dotnet --info"));
    }

    // An older "./tool" grant with no folder covers only files named "tool".
    [Fact]
    public void Legacy_relative_grant_covers_only_its_file_name()
    {
        Assert.True(ShellProgramPath.MatchesLegacyRelative("./ilspycmd", "/opt/tools/ilspycmd"));
        Assert.True(ShellProgramPath.MatchesLegacyRelative("../bin/tool", "/opt/bin/tool"));
        Assert.False(ShellProgramPath.MatchesLegacyRelative("./ilspycmd", "/opt/tools/my-ilspycmd"));
        Assert.False(ShellProgramPath.MatchesLegacyRelative("../bin/tool", "/opt/sbin/tool"));
    }

    private static string ProgramWord(string command)
    {
        var analysis = new ShellApprovalMatcher(ShellExecutionEnvironment.CreateBash(ShellPlatform.Linux))
            .AnalyzeInvocation(
                new ToolName(ShellTool.ToolName),
                new Dictionary<string, object?>
                {
                    ["Command"] = command,
                    ["WorkingDirectory"] = "/opt",
                });
        return Assert.Single(analysis.Candidates, static candidate => candidate.Verb != "cd").VerbTokens![0];
    }

    private static ApprovalEntry Grant(params string[] tokens)
        => ApprovalEntry.CreateTokenPrefix(ApprovalShell.Bash, tokens);

    private static bool Matches(ApprovalEntry grant, params string[] tokens)
        => ApprovalPatternMatching.MatchesShellApproval(
            new ApprovalCandidate(string.Join(' ', tokens), Directory: null)
            {
                VerbTokens = Array.AsReadOnly(tokens),
                Shell = ApprovalShell.Bash,
            },
            cwd: null,
            [grant]);
}
