// -----------------------------------------------------------------------
// <copyright file="LinkParentSegmentApprovalTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Actors.Authorization;
using Netclaw.Configuration;
using Netclaw.Tests.Utilities;
using Xunit;

namespace Netclaw.Actors.Tests.Tools;

/// <summary>
/// The OS follows a link before it applies "..". In the project,
/// <c>lnk</c> points to an external directory, so <c>lnk/../notes.txt</c>
/// names a file outside the project. Its lexical form names
/// <c>project/notes.txt</c>. A grant or the reviewed-safe policy must not
/// cover such a path. The operator gives exact consent for one call.
/// </summary>
[Collection(ShellApprovalMatrixCollection.Name)]
public sealed class LinkParentSegmentApprovalTests(ShellApprovalMatrixFixture fixture)
{
    public static bool IsPosix => !OperatingSystem.IsWindows();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // {P} is the project directory.
    public static TheoryData<string, string> LinkParentCommands => new()
    {
        { "touch", "touch lnk/../notes.txt" },
        { "touch", "touch {P}/lnk/../notes.txt" },
        { "cat", "cat notes.txt > lnk/../copy.txt" },
    };

    [SlopwatchSuppress("SW001", "The case uses a POSIX symbolic link and Bash authorization behavior.")]
    [Theory(SkipUnless = nameof(IsPosix), Skip = "The case uses a POSIX symbolic link and Bash authorization behavior.")]
    [MemberData(nameof(LinkParentCommands))]
    public async Task Folder_grant_does_not_cover_a_parent_segment_after_a_link(string grant, string command)
    {
        await using var harness = await CreateLinkedProjectHarnessAsync(
            "link-parent-folder-grant",
            Approvals.PersistentHere(ApprovalDirectoryShape.Project, grant));

        var source = command.Replace("{P}", harness.ProjectDirectory, StringComparison.Ordinal);
        var decision = await harness.EvaluateShellAsync(source, Ct);

        AssertExactConsentOnly(decision, source);
    }

    [SlopwatchSuppress("SW001", "The case uses a POSIX symbolic link and Bash authorization behavior.")]
    [Fact(SkipUnless = nameof(IsPosix), Skip = "The case uses a POSIX symbolic link and Bash authorization behavior.")]
    public async Task Reviewed_safe_policy_does_not_cover_a_parent_segment_after_a_link()
    {
        await using var harness = await CreateLinkedProjectHarnessAsync(
            "link-parent-reviewed-safe",
            Approvals.None);

        var decision = await harness.EvaluateShellAsync("cat lnk/../notes.txt", Ct);

        AssertExactConsentOnly(decision, "cat lnk/../notes.txt");
    }

    [SlopwatchSuppress("SW001", "The case uses a POSIX symbolic link and Bash authorization behavior.")]
    [Fact(SkipUnless = nameof(IsPosix), Skip = "The case uses a POSIX symbolic link and Bash authorization behavior.")]
    public async Task Unattended_call_does_not_use_a_grant_for_a_parent_segment_after_a_link()
    {
        await using var harness = await CreateLinkedProjectHarnessAsync(
            "link-parent-unattended",
            Approvals.PersistentHere(ApprovalDirectoryShape.Project, "touch"),
            interactive: false);

        var decision = await harness.EvaluateShellAsync("touch lnk/../notes.txt", Ct);

        // No operator can give exact consent to an unattended call (D2).
        Assert.Equal(ApprovalOutcome.Denied, decision.Outcome);
        Assert.Equal(ToolAuthorizer.UnattendedApprovalRequired, decision.DenyReason);
    }

    [SlopwatchSuppress("SW001", "The repository uses POSIX paths, a POSIX symbolic link, and the git CLI.")]
    [Fact(SkipUnless = nameof(IsPosix), Skip = "The repository uses POSIX paths, a POSIX symbolic link, and the git CLI.")]
    public async Task Repository_grant_does_not_cover_a_parent_segment_after_a_link()
    {
        var root = ApprovalTestGit.CreateRoot("netclaw-link-parent-repository-");
        try
        {
            var repository = Directory.CreateDirectory(Path.Combine(root.FullName, "repository")).FullName;
            var session = Directory.CreateDirectory(Path.Combine(root.FullName, "session")).FullName;
            var outside = Directory.CreateDirectory(Path.Combine(root.FullName, "outside", "deep")).FullName;
            await ApprovalTestGit.CreateRepositoryAsync(repository);
            Directory.CreateSymbolicLink(Path.Combine(repository, "lnk"), outside);
            await using var harness = await ShellApprovalHarness.CreateAsync(
                "link-parent-repository-grant",
                new ShellApprovalInvocation("true"),
                Approvals.PersistentRepository("touch"),
                fixture.ActorSystem,
                Ct,
                scope: new ShellApprovalHarnessScope(repository, session, "signalr/link-parent-repository-grant", [])
                {
                    RepositoryGrantWorktree = repository
                });

            // Control: the same grant covers the lexical path without a link.
            var control = await harness.EvaluateShellAsync("touch tracked.txt", Ct);
            Assert.Equal(ApprovalOutcome.Allowed, control.Outcome);
            Assert.Equal(ApprovalAllowReason.StoredApproval, control.AllowReason);

            var decision = await harness.EvaluateShellAsync("touch lnk/../tracked.txt", Ct);

            AssertExactConsentOnly(decision, "touch lnk/../tracked.txt");
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }

    // Controls: ".." that leaves a real directory keeps its lexical meaning,
    // so the grant still covers it. A link without ".." keeps the existing
    // link rule: a prompt that names the candidate.
    [SlopwatchSuppress("SW001", "The case uses a POSIX symbolic link and Bash authorization behavior.")]
    [Fact(SkipUnless = nameof(IsPosix), Skip = "The case uses a POSIX symbolic link and Bash authorization behavior.")]
    public async Task Parent_segment_without_a_link_keeps_the_grant()
    {
        await using var harness = await CreateLinkedProjectHarnessAsync(
            "link-parent-controls",
            Approvals.PersistentHere(ApprovalDirectoryShape.Project, "touch"));
        harness.CreateProjectDirectory("sub");
        var projectName = Path.GetFileName(harness.ProjectDirectory);

        foreach (var command in new[]
                 {
                     "touch sub/../notes.txt",
                     $"touch ../{projectName}/notes.txt",
                     $"touch {harness.ProjectDirectory}/sub/../notes.txt",
                 })
        {
            var decision = await harness.EvaluateShellAsync(command, Ct);
            Assert.True(
                decision.Outcome == ApprovalOutcome.Allowed
                && decision.AllowReason == ApprovalAllowReason.StoredApproval,
                $"{command}: {decision.Outcome} {decision.AllowReason}");
        }

        var linkOnly = await harness.EvaluateShellAsync("touch lnk/notes.txt", Ct);
        Assert.Equal(ApprovalOutcome.RequiresApproval, linkOnly.Outcome);
        Assert.False(linkOnly.Prompt!.IsMessy);
        Assert.Equal(["touch"], linkOnly.Prompt.CandidateVerbs);
    }

    // A quoted glob character in a redirect target is plain path text. The
    // grant keeps covering it, until the directory before ".." is a link.
    [SlopwatchSuppress("SW001", "The case uses a POSIX symbolic link and Bash authorization behavior.")]
    [Theory(SkipUnless = nameof(IsPosix), Skip = "The case uses a POSIX symbolic link and Bash authorization behavior.")]
    [InlineData("d[1]", "echo hi > 'd[1]/../out.txt'")]
    [InlineData("a*b", "echo hi > \"a*b/../out.txt\"")]
    public async Task Quoted_redirect_text_is_a_literal_path(string directory, string command)
    {
        await using var harness = await CreateLinkedProjectHarnessAsync(
            "link-parent-quoted-redirect",
            Approvals.PersistentHere(ApprovalDirectoryShape.Project, "echo"));
        harness.CreateProjectDirectory(directory);

        var plain = await harness.EvaluateShellAsync(command, Ct);
        Assert.Equal(ApprovalOutcome.Allowed, plain.Outcome);
        Assert.Equal(ApprovalAllowReason.StoredApproval, plain.AllowReason);

        harness.ReplaceProjectDirectoryWithExternalSymlink(directory);
        var linked = await harness.EvaluateShellAsync(command, Ct);
        AssertExactConsentOnly(linked, command);
    }

    // The shell expands these words at run time, so the segment before ".."
    // is not known. Each stays exact consent only, with or without a link.
    [SlopwatchSuppress("SW001", "The case uses Bash authorization behavior on a POSIX host.")]
    [Theory(SkipUnless = nameof(IsPosix), Skip = "The case uses Bash authorization behavior on a POSIX host.")]
    [InlineData("echo hi > src/*/../out.txt")]
    [InlineData("echo hi > src/[ab]/../out.txt")]
    [InlineData("echo hi > src/{a,b}/../out.txt")]
    public async Task Unquoted_expansion_before_a_parent_segment_stays_unresolved(string command)
    {
        await using var harness = await CreateLinkedProjectHarnessAsync(
            "link-parent-unquoted-expansion",
            Approvals.PersistentHere(ApprovalDirectoryShape.Project, "echo"));
        harness.CreateProjectDirectory("src/a");
        harness.CreateProjectDirectory("src/b");

        var decision = await harness.EvaluateShellAsync(command, Ct);

        AssertExactConsentOnly(decision, command);
    }

    // The file tools canonicalize the path before the policy check and open
    // that canonical path. So file_read opens project/notes.txt, the path
    // that the policy checked, and not the external file.
    [SlopwatchSuppress("SW001", "The case uses a POSIX symbolic link.")]
    [Fact(SkipUnless = nameof(IsPosix), Skip = "The case uses a POSIX symbolic link.")]
    public async Task File_read_opens_the_checked_path_for_a_parent_segment_after_a_link()
    {
        await using var harness = await CreateLinkedProjectHarnessAsync(
            "link-parent-file-read",
            Approvals.None);
        await File.WriteAllTextAsync(Path.Combine(harness.ProjectDirectory, "notes.txt"), "project-notes", Ct);
        var external = Path.GetDirectoryName(
            Directory.ResolveLinkTarget(Path.Combine(harness.ProjectDirectory, "lnk"), returnFinalTarget: true)!.FullName)!;
        await File.WriteAllTextAsync(Path.Combine(external, "notes.txt"), "external-notes", Ct);

        foreach (var path in new[] { "lnk/../notes.txt", $"{harness.ProjectDirectory}/lnk/../notes.txt" })
        {
            var run = await harness.RunToolAsync("file_read", ToolInput.Create("Path", path), Ct);

            Assert.Equal(ApprovalOutcome.Allowed, run.Outcome);
            Assert.Contains("project-notes", run.Output, StringComparison.Ordinal);
            Assert.DoesNotContain("external-notes", run.Output, StringComparison.Ordinal);
        }
    }

    private async Task<ShellApprovalHarness> CreateLinkedProjectHarnessAsync(
        string id,
        ApprovalState approvals,
        bool interactive = true)
    {
        var harness = await ShellApprovalHarness.CreateAsync(
            id,
            new ShellApprovalInvocation("true", Interactive: interactive),
            approvals,
            fixture.ActorSystem,
            Ct);
        // project/lnk -> workspaces/external, outside the project.
        harness.ReplaceProjectDirectoryWithExternalSymlink("lnk");
        return harness;
    }

    // The command is one exact candidate: its text, and only "Once" or "Deny".
    private static void AssertExactConsentOnly(ApprovalObservation decision, string command)
    {
        Assert.Equal(ApprovalOutcome.RequiresApproval, decision.Outcome);
        var prompt = Assert.IsType<ApprovalPromptObservation>(decision.Prompt);
        Assert.Equal([command], prompt.CandidateVerbs);
        Assert.Equal([ObservedOptionKeys.ApproveOnce, ObservedOptionKeys.Deny], prompt.OptionKeys);
    }
}
