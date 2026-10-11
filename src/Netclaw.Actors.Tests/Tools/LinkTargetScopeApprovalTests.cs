// -----------------------------------------------------------------------
// <copyright file="LinkTargetScopeApprovalTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Actors.Authorization;
using Netclaw.Actors.Tools;
using Netclaw.Tests.Utilities;
using Netclaw.Tools;
using Xunit;

namespace Netclaw.Actors.Tests.Tools;

/// <summary>
/// Issue #2375: a word that names a link has two scopes, the folder of the
/// link and its final target. A folder or repository grant covers the word only when it
/// covers both. In the project, <c>ext.txt</c> points to a file in
/// <c>other/</c>, a sibling of the project.
/// </summary>
[Collection(ShellApprovalMatrixCollection.Name)]
public sealed class LinkTargetScopeApprovalTests(ShellApprovalMatrixFixture fixture)
{
    public static bool IsPosix => !OperatingSystem.IsWindows();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // Each form names the same link: a path word, a plain word, and an option value.
    public static TheoryData<string, string> LinkOutCommands => new()
    {
        { "mytool read", "mytool read ext.txt" },
        { "mytool read", "mytool read ./ext.txt" },
        { "mytool read", "mytool read {P}/ext.txt" },
        { "mytool read", "mytool read extlink" },
        { "mytool read", "mytool read ./extlink" },
        { "mytool read", "mytool read sub/../extlink" },
        { "gh api", "gh api --input ext.txt x" },
        // An option value is a path word when it names a link (#2364).
        { "mytool read", "mytool read --input=ext.txt" },
        { "mytool read", "mytool read --input=extlink" },
        { "mytool write", "mytool write outdir" },
        { "mytool write", "mytool write chain.txt" },
        { "mytool write", "mytool write dangling.txt" },
    };

    [SlopwatchSuppress("SW001", "The case uses POSIX symbolic links and Bash authorization behavior.")]
    [Theory(SkipUnless = nameof(IsPosix), Skip = "The case uses POSIX symbolic links and Bash authorization behavior.")]
    [MemberData(nameof(LinkOutCommands))]
    public async Task Folder_grant_does_not_cover_a_link_to_a_path_outside_the_folder(string grant, string command)
    {
        await using var harness = await CreateHarnessAsync(
            Approvals.PersistentHere(ApprovalDirectoryShape.Project, grant));

        var decision = await harness.EvaluateShellAsync(Expand(harness, command), Ct);

        Assert.True(
            decision.Outcome == ApprovalOutcome.RequiresApproval,
            $"'{command}' was {decision.Outcome} ({decision.AllowReason}); the link target is outside the folder.");
        Assert.Contains(OtherDirectory(harness), decision.Prompt!.CandidateDirectories ?? []);
    }

    // A directory link in the middle of an option value keeps the link rule
    // below the grant root: the folder grant does not cover it.
    [SlopwatchSuppress("SW001", "The case uses POSIX symbolic links and Bash authorization behavior.")]
    [Theory(SkipUnless = nameof(IsPosix), Skip = "The case uses POSIX symbolic links and Bash authorization behavior.")]
    [InlineData("mytool read --input=outdir/notes.txt")]
    [InlineData("mytool read outdir/notes.txt")]
    public async Task Folder_grant_does_not_cover_a_directory_link_in_the_middle_of_a_word(string command)
    {
        await using var harness = await CreateHarnessAsync(
            Approvals.PersistentHere(ApprovalDirectoryShape.Project, "mytool read"));

        var decision = await harness.EvaluateShellAsync(command, Ct);

        Assert.Equal(ApprovalOutcome.RequiresApproval, decision.Outcome);
    }

    // Control: the target path itself gets the same prompt.
    [SlopwatchSuppress("SW001", "The case uses POSIX symbolic links and Bash authorization behavior.")]
    [Fact(SkipUnless = nameof(IsPosix), Skip = "The case uses POSIX symbolic links and Bash authorization behavior.")]
    public async Task Folder_grant_does_not_cover_the_target_path()
    {
        await using var harness = await CreateHarnessAsync(
            Approvals.PersistentHere(ApprovalDirectoryShape.Project, "mytool read"));

        var decision = await harness.EvaluateShellAsync($"mytool read {OtherDirectory(harness)}/notes.txt", Ct);

        Assert.Equal(ApprovalOutcome.RequiresApproval, decision.Outcome);
    }

    // A link whose final target is in the folder keeps the grant. "back.txt"
    // goes out of the folder and back in: the final target decides. A dangling
    // link states its target path, and a write through it creates the file
    // there. So "danglingin.txt" (missing file in the folder) keeps the grant,
    // and "dangling.txt" (missing file outside) does not.
    [SlopwatchSuppress("SW001", "The case uses POSIX symbolic links and Bash authorization behavior.")]
    [Theory(SkipUnless = nameof(IsPosix), Skip = "The case uses POSIX symbolic links and Bash authorization behavior.")]
    [InlineData("mytool read", "mytool read inner.txt")]
    [InlineData("mytool read", "mytool read innerlink")]
    // Each spelling of one link gets the same answer.
    [InlineData("mytool read", "mytool read ./innerlink")]
    [InlineData("mytool read", "mytool read {P}/innerlink")]
    [InlineData("mytool read", "mytool read node_modules/.bin/tsc")]
    [InlineData("mytool write", "mytool write ./innerdir")]
    [InlineData("mytool read", "mytool read --input=innerlink")]
    [InlineData("mytool read", "mytool read --input=inner.txt")]
    [InlineData("mytool write", "mytool write innerdir")]
    [InlineData("mytool write", "mytool write back.txt")]
    [InlineData("mytool write", "mytool write danglingin.txt")]
    public async Task Folder_grant_covers_a_link_to_a_path_in_the_folder(string grant, string command)
    {
        await using var harness = await CreateHarnessAsync(
            Approvals.PersistentHere(ApprovalDirectoryShape.Project, grant));

        var decision = await harness.EvaluateShellAsync(Expand(harness, command), Ct);

        Assert.True(
            decision is { Outcome: ApprovalOutcome.Allowed, AllowReason: ApprovalAllowReason.StoredApproval },
            $"'{command}' was {decision.Outcome} ({decision.AllowReason}); the link target is in the folder.");
    }

    // A grant without a folder covers every path, so it covers the link too.
    [SlopwatchSuppress("SW001", "The case uses POSIX symbolic links and Bash authorization behavior.")]
    [Theory(SkipUnless = nameof(IsPosix), Skip = "The case uses POSIX symbolic links and Bash authorization behavior.")]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Grant_without_a_folder_covers_a_link_out_of_the_folder(bool persistent)
    {
        await using var harness = await CreateHarnessAsync(
            persistent ? Approvals.PersistentAnywhere("mytool read") : Approvals.Session("mytool read"));

        foreach (var command in new[] { "mytool read ext.txt", $"mytool read {OtherDirectory(harness)}/notes.txt" })
        {
            var decision = await harness.EvaluateShellAsync(command, Ct);
            Assert.True(
                decision.Outcome == ApprovalOutcome.Allowed,
                $"'{command}' was {decision.Outcome} ({decision.DenyReason}).");
        }
    }

    [SlopwatchSuppress("SW001", "The case uses POSIX symbolic links and Bash authorization behavior.")]
    [Fact(SkipUnless = nameof(IsPosix), Skip = "The case uses POSIX symbolic links and Bash authorization behavior.")]
    public async Task Unattended_folder_grant_denies_a_link_out_of_the_folder()
    {
        await using var harness = await CreateHarnessAsync(
            Approvals.PersistentHere(ApprovalDirectoryShape.Project, "mytool read"),
            interactive: false);

        var outside = await harness.EvaluateShellAsync("mytool read ext.txt", Ct);
        var optionOutside = await harness.EvaluateShellAsync("mytool read --input=ext.txt", Ct);
        var inside = await harness.EvaluateShellAsync("mytool read inner.txt", Ct);

        // No operator can answer a prompt in an unattended call (D2).
        Assert.Equal(ApprovalOutcome.Denied, outside.Outcome);
        Assert.Equal(ToolAuthorizer.UnattendedApprovalRequired, outside.DenyReason);
        Assert.Equal(ApprovalOutcome.Denied, optionOutside.Outcome);
        Assert.Equal(ToolAuthorizer.UnattendedApprovalRequired, optionOutside.DenyReason);
        Assert.Equal(ApprovalOutcome.Allowed, inside.Outcome);
    }

    // A link without a known target fails closed. The OS follows a link before
    // it applies "..", so a ".." in a link text that leaves a link has no
    // lexical target. No grant covers such a word: the operator gives exact
    // consent for one call.
    [SlopwatchSuppress("SW001", "The case uses POSIX symbolic links and Bash authorization behavior.")]
    [Theory(SkipUnless = nameof(IsPosix), Skip = "The case uses POSIX symbolic links and Bash authorization behavior.")]
    [InlineData("mytool read dotdot.txt")]
    [InlineData("mytool read dotdotlink")]
    [InlineData("mytool read globdd/dd.txt")]
    public async Task Link_without_a_known_target_needs_exact_consent(string command)
    {
        await using var harness = await CreateHarnessAsync(Approvals.PersistentAnywhere("mytool read"));

        var decision = await harness.EvaluateShellAsync(command, Ct);

        Assert.Equal(ApprovalOutcome.RequiresApproval, decision.Outcome);
        var prompt = Assert.IsType<ApprovalPromptObservation>(decision.Prompt);
        Assert.Equal([command], prompt.CandidateVerbs);
        Assert.Equal([ObservedOptionKeys.ApproveOnce, ObservedOptionKeys.Deny], prompt.OptionKeys);
    }

    // SECURITY: the OS follows "ncdir" before it applies "..", so these links
    // open a protected file below the Netclaw root. The lexical target is a
    // file in the project. The protected-path screen reads the path that the
    // OS opens and denies the call, attended or not, with or without a grant.
    // The control "dotdot.txt" has the same shape and an ordinary target, so
    // it keeps exact consent (see the test above).
    [SlopwatchSuppress("SW001", "The case uses POSIX symbolic links and Bash authorization behavior.")]
    [Theory(SkipUnless = nameof(IsPosix), Skip = "The case uses POSIX symbolic links and Bash authorization behavior.")]
    [InlineData("mytool read keydd.txt", true, true)]
    [InlineData("mytool read keydd.txt", true, false)]
    [InlineData("mytool read keyddlink", true, true)]
    [InlineData("mytool read secretsdd.txt", true, true)]
    [InlineData("mytool read secretsdd.txt", true, false)]
    [InlineData("mytool read grantsdd.txt", true, true)]
    [InlineData("mytool read hopdd.txt", true, true)]
    [InlineData("cat keydd.txt", false, true)]
    [InlineData("cat keydd.txt", false, false)]
    public async Task Link_text_with_a_parent_segment_after_a_link_to_a_protected_path_is_denied(
        string command,
        bool grant,
        bool interactive)
    {
        await using var harness = await CreateHarnessAsync(
            grant ? Approvals.PersistentAnywhere("mytool read") : Approvals.None,
            interactive);
        var paths = harness.Paths;
        var project = harness.ProjectDirectory;
        Directory.CreateDirectory(paths.LogsDirectory);
        Directory.CreateDirectory(paths.KeysDirectory);
        Directory.CreateDirectory(paths.ConfigDirectory);
        await File.WriteAllTextAsync(Path.Join(paths.KeysDirectory, "a.pem"), "synthetic test data", Ct);
        if (!File.Exists(paths.SecretsPath))
            await File.WriteAllTextAsync(paths.SecretsPath, "{}", Ct);
        // ncdir is not protected. Each link text leaves it with "..".
        Directory.CreateSymbolicLink(Path.Join(project, "ncdir"), paths.LogsDirectory);
        File.CreateSymbolicLink(Path.Join(project, "keydd.txt"), "ncdir/../keys/a.pem");
        File.CreateSymbolicLink(Path.Join(project, "keyddlink"), "ncdir/../keys/a.pem");
        File.CreateSymbolicLink(Path.Join(project, "secretsdd.txt"), "ncdir/../config/secrets.json");
        File.CreateSymbolicLink(Path.Join(project, "grantsdd.txt"), "ncdir/../config/tool-approvals.json");
        // A second link in the chain does not hide the first one.
        File.CreateSymbolicLink(Path.Join(project, "hopdd.txt"), "keydd.txt");

        var decision = await harness.EvaluateShellDecisionAsync(command, Ct);

        // The text screen or the path screen denies, by the kind of the protected file.
        Assert.Equal(ToolAuthorizationOutcome.Denied, decision.Outcome);
        Assert.Contains(decision.DenyReason, new[] { "shell_references_protected_path", "shell_path_protected" });
    }

    // A loop never ends. The protected-path screen cannot resolve it, so it
    // denies the word before the grant check (R13).
    [SlopwatchSuppress("SW001", "The case uses POSIX symbolic links and Bash authorization behavior.")]
    [Theory(SkipUnless = nameof(IsPosix), Skip = "The case uses POSIX symbolic links and Bash authorization behavior.")]
    [InlineData("mytool read loop1.txt")]
    [InlineData("mytool read looplink")]
    public async Task Link_loop_is_denied(string command)
    {
        await using var harness = await CreateHarnessAsync(Approvals.PersistentAnywhere("mytool read"));

        var decision = await harness.EvaluateShellDecisionAsync(command, Ct);

        Assert.Equal(ToolAuthorizationOutcome.Denied, decision.Outcome);
        Assert.Equal("shell_references_protected_path", decision.DenyReason);
    }

    // A glob word and a literal word read a link with the same reader. The
    // lexical target of "globdd/dd.txt" is "globdd/a.txt" in the folder, but
    // the OS opens a file outside the project. The glob must not keep the grant.
    [SlopwatchSuppress("SW001", "The case uses POSIX symbolic links and Bash authorization behavior.")]
    [Fact(SkipUnless = nameof(IsPosix), Skip = "The case uses POSIX symbolic links and Bash authorization behavior.")]
    public async Task Folder_grant_does_not_cover_a_glob_that_matches_a_link_out_of_the_folder()
    {
        await using var harness = await CreateHarnessAsync(
            Approvals.PersistentHere(ApprovalDirectoryShape.Project, "mytool read"));

        var linked = await harness.EvaluateShellAsync("mytool read globdd/*.txt", Ct);
        // Control: the same glob in a folder whose links stay inside keeps the grant.
        var contained = await harness.EvaluateShellAsync("mytool read globin/*.txt", Ct);

        Assert.NotEqual(ApprovalOutcome.Allowed, linked.Outcome);
        Assert.Equal(ApprovalOutcome.Allowed, contained.Outcome);
        Assert.Equal(ApprovalAllowReason.StoredApproval, contained.AllowReason);
    }

    // The protected-path screen still denies a link to a protected path first.
    [SlopwatchSuppress("SW001", "The case uses POSIX symbolic links and Bash authorization behavior.")]
    [Theory(SkipUnless = nameof(IsPosix), Skip = "The case uses POSIX symbolic links and Bash authorization behavior.")]
    [InlineData("cat keylink.txt")]
    [InlineData("cat keylink")]
    public async Task Link_to_a_protected_path_stays_denied(string command)
    {
        await using var harness = await CreateHarnessAsync(Approvals.PersistentAnywhere("cat"));
        Directory.CreateDirectory(harness.Paths.KeysDirectory);
        var key = Path.Join(harness.Paths.KeysDirectory, "a.pem");
        await File.WriteAllTextAsync(key, "synthetic test data", Ct);
        File.CreateSymbolicLink(Path.Join(harness.ProjectDirectory, "keylink.txt"), key);
        File.CreateSymbolicLink(Path.Join(harness.ProjectDirectory, "keylink"), key);

        var decision = await harness.EvaluateShellDecisionAsync(command, Ct);

        Assert.Equal(ToolAuthorizationOutcome.Denied, decision.Outcome);
        Assert.Equal("shell_references_protected_path", decision.DenyReason);
    }

    [SlopwatchSuppress("SW001", "The repository uses POSIX paths, POSIX symbolic links, and the git CLI.")]
    [Fact(SkipUnless = nameof(IsPosix), Skip = "The repository uses POSIX paths, POSIX symbolic links, and the git CLI.")]
    public async Task Repository_grant_does_not_cover_a_link_out_of_the_repository()
    {
        var root = ApprovalTestGit.CreateRoot("netclaw-link-target-repository-");
        try
        {
            var repository = Directory.CreateDirectory(Path.Combine(root.FullName, "repository")).FullName;
            var session = Directory.CreateDirectory(Path.Combine(root.FullName, "session")).FullName;
            var other = Directory.CreateDirectory(Path.Combine(root.FullName, "other")).FullName;
            await ApprovalTestGit.CreateRepositoryAsync(repository);
            await File.WriteAllTextAsync(Path.Combine(other, "notes.txt"), "synthetic test data", Ct);
            File.CreateSymbolicLink(Path.Combine(repository, "ext.txt"), Path.Combine(other, "notes.txt"));
            File.CreateSymbolicLink(Path.Combine(repository, "inner.txt"), "tracked.txt");
            await using var harness = await ShellApprovalHarness.CreateAsync(
                "link-target-repository-grant",
                new ShellApprovalInvocation("true"),
                Approvals.PersistentRepository("mytool read"),
                fixture.ActorSystem,
                Ct,
                scope: new ShellApprovalHarnessScope(repository, session, "signalr/link-target-repository-grant", [])
                {
                    RepositoryGrantWorktree = repository
                });

            var inside = await harness.EvaluateShellAsync("mytool read inner.txt", Ct);
            var outside = await harness.EvaluateShellAsync("mytool read ext.txt", Ct);

            Assert.Equal(ApprovalOutcome.Allowed, inside.Outcome);
            Assert.Equal(ApprovalAllowReason.StoredApproval, inside.AllowReason);
            Assert.Equal(ApprovalOutcome.RequiresApproval, outside.Outcome);
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }

    private async Task<ShellApprovalHarness> CreateHarnessAsync(ApprovalState approvals, bool interactive = true)
    {
        var harness = await ShellApprovalHarness.CreateAsync(
            "link-target-scope",
            new ShellApprovalInvocation("true", Interactive: interactive),
            approvals,
            fixture.ActorSystem,
            Ct);
        CreateLinks(harness);
        return harness;
    }

    // project/                    other/ (outside the project)
    //   notes.txt                   notes.txt
    //   ext.txt, extlink -> other/notes.txt
    //   outdir -> other/
    //   inner.txt, innerlink -> notes.txt
    //   innerdir -> sub/
    //   chain.txt -> hop.txt -> other/notes.txt
    //   back.txt -> other/hop.txt -> project/notes.txt
    //   dangling.txt -> other/missing.txt
    //   danglingin.txt -> missing.txt
    //   loop1.txt -> loop2.txt -> loop1.txt, looplink -> loop1.txt
    //   outlnk -> other/deep/, dotdot.txt and dotdotlink -> outlnk/../notes.txt
    //   node_modules/.bin/tsc -> ../tsc/bin.js
    //   globdd/dd.txt -> project/outdir/../globdd/a.txt (a file outside the project)
    //   globin/b.txt -> a.txt
    private static void CreateLinks(ShellApprovalHarness harness)
    {
        var project = harness.ProjectDirectory;
        var other = OtherDirectory(harness);
        Directory.CreateDirectory(Path.Join(other, "deep"));
        Directory.CreateDirectory(Path.Join(project, "sub"));
        File.WriteAllText(Path.Join(project, "notes.txt"), "synthetic test data");
        File.WriteAllText(Path.Join(other, "notes.txt"), "synthetic test data");

        File.CreateSymbolicLink(Path.Join(project, "ext.txt"), Path.Join(other, "notes.txt"));
        File.CreateSymbolicLink(Path.Join(project, "extlink"), Path.Join(other, "notes.txt"));
        Directory.CreateSymbolicLink(Path.Join(project, "outdir"), other);
        File.CreateSymbolicLink(Path.Join(project, "inner.txt"), "notes.txt");
        File.CreateSymbolicLink(Path.Join(project, "innerlink"), Path.Join(project, "notes.txt"));
        Directory.CreateSymbolicLink(Path.Join(project, "innerdir"), "sub");
        File.CreateSymbolicLink(Path.Join(project, "hop.txt"), Path.Join(other, "notes.txt"));
        File.CreateSymbolicLink(Path.Join(project, "chain.txt"), "hop.txt");
        File.CreateSymbolicLink(Path.Join(other, "hop.txt"), Path.Join(project, "notes.txt"));
        File.CreateSymbolicLink(Path.Join(project, "back.txt"), Path.Join(other, "hop.txt"));
        File.CreateSymbolicLink(Path.Join(project, "dangling.txt"), Path.Join(other, "missing.txt"));
        File.CreateSymbolicLink(Path.Join(project, "danglingin.txt"), "missing.txt");
        File.CreateSymbolicLink(Path.Join(project, "loop1.txt"), "loop2.txt");
        File.CreateSymbolicLink(Path.Join(project, "loop2.txt"), "loop1.txt");
        File.CreateSymbolicLink(Path.Join(project, "looplink"), "loop1.txt");
        Directory.CreateSymbolicLink(Path.Join(project, "outlnk"), Path.Join(other, "deep"));
        File.CreateSymbolicLink(Path.Join(project, "dotdot.txt"), "outlnk/../notes.txt");
        File.CreateSymbolicLink(Path.Join(project, "dotdotlink"), "outlnk/../notes.txt");

        Directory.CreateDirectory(Path.Join(project, "node_modules", ".bin"));
        Directory.CreateDirectory(Path.Join(project, "node_modules", "tsc"));
        File.WriteAllText(Path.Join(project, "node_modules", "tsc", "bin.js"), "synthetic test data");
        File.CreateSymbolicLink(Path.Join(project, "node_modules", ".bin", "tsc"), "../tsc/bin.js");

        // The OS opens <root>/globdd/a.txt for dd.txt, because outdir is a link.
        var root = Path.GetDirectoryName(project)!;
        Directory.CreateDirectory(Path.Join(project, "globdd"));
        Directory.CreateDirectory(Path.Join(root, "globdd"));
        File.WriteAllText(Path.Join(project, "globdd", "a.txt"), "synthetic test data");
        File.WriteAllText(Path.Join(root, "globdd", "a.txt"), "synthetic test data");
        File.CreateSymbolicLink(Path.Join(project, "globdd", "dd.txt"), Path.Join(project, "outdir", "..", "globdd", "a.txt"));
        Directory.CreateDirectory(Path.Join(project, "globin"));
        File.WriteAllText(Path.Join(project, "globin", "a.txt"), "synthetic test data");
        File.CreateSymbolicLink(Path.Join(project, "globin", "b.txt"), "a.txt");
    }

    private static string OtherDirectory(ShellApprovalHarness harness)
        => Path.Join(Path.GetDirectoryName(harness.ProjectDirectory)!, "other");

    private static string Expand(ShellApprovalHarness harness, string command)
        => command.Replace("{P}", harness.ProjectDirectory, StringComparison.Ordinal);
}
