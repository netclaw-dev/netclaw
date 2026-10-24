// -----------------------------------------------------------------------
// <copyright file="ShellOnceSourceIdentityMutationTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Actors.Tools;
using Netclaw.Configuration;
using Netclaw.Security;
using Xunit;

namespace Netclaw.Actors.MutationTests;

public sealed class ShellOnceSourceIdentityMutationTests
{
    [Fact]
    public void Once_keys_differ_when_shell_source_differs_but_candidates_match()
    {
        var original = CreateContext("git status # source-A");
        var sameSource = CreateContext("git status # source-A");
        var changedSource = CreateContext("git status # source-a");

        var originalKeys = OneTimeApprovalKeys.Create(original);
        var sameSourceKeys = OneTimeApprovalKeys.Create(sameSource);
        var changedSourceKeys = OneTimeApprovalKeys.Create(changedSource);

        Assert.True(originalKeys.SequenceEqual(sameSourceKeys));
        Assert.False(originalKeys.SequenceEqual(changedSourceKeys));
    }

    [Fact]
    public void Missing_shell_source_fails_closed_and_non_shell_once_stays_compatible()
    {
        var shellContext = CreateContext("git status") with { ShellSourceKey = null };
        var shellConsent = OneTimeApprovalKeys.CreateConsent("shell_execute", shellContext);
        var fileContext = shellContext with { ToolName = "file_read" };
        var fileConsent = OneTimeApprovalKeys.CreateConsent("file_read", fileContext);

        Assert.False(OneTimeApprovalKeys.Matches(shellConsent, "shell_execute", shellContext));
        Assert.True(OneTimeApprovalKeys.Matches(fileConsent, "file_read", fileContext));
    }

    private static ToolApprovalContext CreateContext(string command)
    {
        var candidate = new ApprovalCandidate("git status", Directory: "/repo")
        {
            Shell = ApprovalShell.Bash,
            VerbTokens = ["git", "status"]
        };

        return new ToolApprovalContext(
            "shell_execute",
            command,
            ["git status"],
            ["git status"],
            [],
            Cwd: "/repo",
            Candidates: [candidate])
        {
            ShellSourceKey = OneTimeApprovalKeys.CreateShellSourceKey(command)
        };
    }
}
