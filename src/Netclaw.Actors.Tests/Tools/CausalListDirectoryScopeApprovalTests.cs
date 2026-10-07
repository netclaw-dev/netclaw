// -----------------------------------------------------------------------
// <copyright file="CausalListDirectoryScopeApprovalTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Actors.Authorization.Consent;
using Netclaw.Actors.Tools;
using Netclaw.Configuration;
using Netclaw.Security;
using Netclaw.Security.Authorization.Consent;
using Netclaw.Tools;
using Xunit;

namespace Netclaw.Actors.Tests.Tools;

/// <summary>
/// A causal list (<c>cd dir &amp;&amp; action; diagnostic</c>) gets its candidates from the
/// directory proof. These cases go through the production registration and pin the
/// grant scope, the protected paths, and the directory advice of such a list.
/// </summary>
[Collection(ShellApprovalMatrixCollection.Name)]
public sealed class CausalListDirectoryScopeApprovalTests(ShellApprovalMatrixFixture fixture)
{
    public static bool IsPosix => !OperatingSystem.IsWindows();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [SlopwatchSuppress("SW001", "The Bash cases require a POSIX host.")]
    [Fact(SkipUnless = nameof(IsPosix), Skip = "The Bash cases require a POSIX host.")]
    public async Task Always_answer_stores_each_grant_in_the_directory_where_its_occurrence_runs()
    {
        await using var harness = await CreateHarnessAsync("causal-list-grant-scope");
        var external = ExternalDirectory(harness);
        var other = Directory.CreateDirectory(Path.Combine(Path.GetDirectoryName(external)!, "other")).FullName;
        var command = $"cd {external} && inspect; cat ./*.md";

        var prompt = await harness.EvaluateShellDecisionAsync(command, Ct);

        // Before causal lists used the directory proof, this prompt offered only Once.
        Assert.Equal(ToolAuthorizationOutcome.RequiresApproval, prompt.Outcome);
        var approval = Assert.IsType<ToolApprovalContext>(prompt.ApprovalContext);
        Assert.False(approval.IsMessy);
        Assert.Contains(approval.Options, option => option.Key.Value == ObservedOptionKeys.ApproveAlways);
        var grants = GrantBuilder.Build(
            approval.Candidates!,
            GrantScopeKind.Folder,
            approval.Cwd,
            harness.SessionDirectory,
            repositoryCommonDirectory: null);

        // cd and inspect run only after the change. The diagnostic can also run in the
        // original directory when cd fails, so it gets a grant there too.
        Assert.Equal(
            [
                $"cd@{external}",
                $"inspect@{external}",
                $"cat@{harness.ProjectDirectory}",
                $"cat@{external}",
            ],
            grants.Select(grant => $"{grant.Candidate.Verb}@{Assert.IsType<GrantScope.Folder>(grant.Scope).Directory}"));

        // Another chat gives the answer, so only the stored folder grants can apply here.
        await harness.ApprovalService.RecordApprovalCandidatesAsync(
            (ToolApprovalSessionId)"signalr/other-session",
            TrustAudience.Personal,
            new ToolName(ShellTool.ToolName),
            grants,
            Ct);
        var reused = await harness.EvaluateShellAsync(command, Ct);
        var elsewhere = await harness.EvaluateShellAsync($"cd {other} && inspect; cat ./*.md", Ct);

        Assert.Equal(ApprovalOutcome.Allowed, reused.Outcome);
        Assert.Equal(ApprovalAllowReason.StoredApproval, reused.AllowReason);
        // Negative control: the grants do not follow a cd into another directory.
        Assert.Equal(ApprovalOutcome.RequiresApproval, elsewhere.Outcome);
        Assert.Equal(["cd", "inspect", "cat"], elsewhere.Prompt!.CandidateVerbs);
        Assert.All(elsewhere.Prompt.CandidateDirectories!, directory => Assert.Equal(other, directory));
    }

    [SlopwatchSuppress("SW001", "The Bash cases require a POSIX host.")]
    [Fact(SkipUnless = nameof(IsPosix), Skip = "The Bash cases require a POSIX host.")]
    public async Task Protected_path_read_after_a_directory_change_is_denied()
    {
        await using var harness = await CreateHarnessAsync(
            "causal-list-protected-read",
            Approvals.PersistentAnywhere("cd", "inspect", "cat"));
        var home = Path.GetDirectoryName(harness.Paths.ConfigDirectory)!;
        var config = Path.GetFileName(harness.Paths.ConfigDirectory);

        var observed = await harness.EvaluateShellAsync($"cd {home} && inspect; cat {config}/secrets.json", Ct);

        // The base denied this call with the trusted-root path check. The directory
        // proof now checks each slice first and reports the protected path.
        Assert.Equal(ApprovalOutcome.Denied, observed.Outcome);
        Assert.Equal("shell_references_protected_path", observed.DenyReason);
    }

    [SlopwatchSuppress("SW001", "The Bash cases require a POSIX host.")]
    [Fact(SkipUnless = nameof(IsPosix), Skip = "The Bash cases require a POSIX host.")]
    public async Task Redirect_into_a_protected_directory_after_a_directory_change_stays_one_time()
    {
        await using var harness = await CreateHarnessAsync(
            "causal-list-protected-redirect",
            Approvals.PersistentAnywhere("cd", "inspect", "cat"));
        var home = Path.GetDirectoryName(harness.Paths.ConfigDirectory)!;
        var config = Path.GetFileName(harness.Paths.ConfigDirectory);

        var observed = await harness.EvaluateShellAsync(
            $"cd {home} && inspect; cat notes.txt > {config}/netclaw.json",
            Ct);

        // A file-writing redirect is not a causal diagnostic, so this call keeps its
        // exact consent: the grants cannot cover it and no reusable option appears.
        Assert.Equal(ApprovalOutcome.RequiresApproval, observed.Outcome);
        // Only exact consent: "Once" or "Deny".
        Assert.Equal([ObservedOptionKeys.ApproveOnce, ObservedOptionKeys.Deny], observed.Prompt!.OptionKeys);
    }

    [SlopwatchSuppress("SW001", "The Bash cases require a POSIX host.")]
    [Fact(SkipUnless = nameof(IsPosix), Skip = "The Bash cases require a POSIX host.")]
    public async Task Linked_directory_keeps_exact_consent_and_a_protected_alias_is_denied()
    {
        await using var harness = await CreateHarnessAsync(
            "causal-list-linked-directory",
            Approvals.PersistentAnywhere("cd", "inspect", "head"));
        var target = Directory.CreateDirectory(
            Path.Combine(Path.GetDirectoryName(harness.ProjectDirectory)!, "target")).FullName;
        var linked = Path.Combine(harness.ProjectDirectory, "linked");
        Directory.CreateSymbolicLink(linked, target);
        Directory.CreateDirectory(harness.Paths.ConfigDirectory);
        var protectedAlias = Path.Combine(harness.ProjectDirectory, "config-alias");
        Directory.CreateSymbolicLink(protectedAlias, harness.Paths.ConfigDirectory);

        var viaLink = await harness.EvaluateShellAsync($"cd {linked} && inspect; head result.log", Ct);
        var viaProtectedAlias = await harness.EvaluateShellAsync(
            $"cd {linked} && inspect; cd {protectedAlias} && inspect; head result.log",
            Ct);

        // A linked scope directory is not a proof, so even existing grants give only Once.
        Assert.Equal(ApprovalOutcome.RequiresApproval, viaLink.Outcome);
        // Only exact consent: "Once" or "Deny".
        Assert.Equal([ObservedOptionKeys.ApproveOnce, ObservedOptionKeys.Deny], viaLink.Prompt!.OptionKeys);
        Assert.Equal(ApprovalOutcome.Denied, viaProtectedAlias.Outcome);
        Assert.Equal("shell_references_protected_path", viaProtectedAlias.DenyReason);
    }

    [SlopwatchSuppress("SW001", "The Bash cases require a POSIX host.")]
    [Fact(SkipUnless = nameof(IsPosix), Skip = "The Bash cases require a POSIX host.")]
    public async Task Unattended_causal_list_uses_the_same_grants_as_a_chat()
    {
        var grants = Approvals.PersistentAnywhere("cd", "inspect", "cat");
        await using var unattended = await CreateHarnessAsync("causal-list-unattended", grants, interactive: false);
        await using var interactive = await CreateHarnessAsync("causal-list-interactive", grants);
        // Each harness owns its external directory, so the glob reads no shared entries.
        var unattendedResult = await unattended.EvaluateShellAsync($"cd {ExternalDirectory(unattended)} && inspect; cat ./*.md", Ct);
        var interactiveResult = await interactive.EvaluateShellAsync($"cd {ExternalDirectory(interactive)} && inspect; cat ./*.md", Ct);

        // D2: the directory proof and the stored grants decide an unattended
        // call as they decide the same call in a chat.
        Assert.Equal(ApprovalOutcome.Allowed, interactiveResult.Outcome);
        Assert.Equal(ApprovalOutcome.Allowed, unattendedResult.Outcome);
        Assert.Equal(interactiveResult.AllowReason, unattendedResult.AllowReason);
    }

    [SlopwatchSuppress("SW001", "The Bash cases require a POSIX host.")]
    [Fact(SkipUnless = nameof(IsPosix), Skip = "The Bash cases require a POSIX host.")]
    public async Task Diagnostic_path_that_leaves_the_file_system_root_is_denied()
    {
        await using var harness = await CreateHarnessAsync(
            "causal-list-invalid-path",
            Approvals.PersistentAnywhere("cd", "inspect", "head"));

        var observed = await harness.EvaluateShellAsync("cd / && inspect; head ../x", Ct);

        // "../x" below "/" has no canonical form. The removed causal check denied it as
        // shell_references_protected_path. The check of each directory slice denies it now.
        Assert.Equal(ApprovalOutcome.Denied, observed.Outcome);
        Assert.Equal("shell_working_directory_outside_trust_zone", observed.DenyReason);
    }

    [SlopwatchSuppress("SW001", "The Bash cases require a POSIX host.")]
    [Theory(SkipUnless = nameof(IsPosix), Skip = "The Bash cases require a POSIX host.")]
    [InlineData(true, "cd lnk/.. && git status")]
    [InlineData(true, "cd lnk/..; git status")]
    [InlineData(true, "cd {project}/lnk/.. && inspect; cat notes.txt")]
    [InlineData(false, "cd lnk/.. && git status")]
    [InlineData(false, "cd lnk/..; git status")]
    [InlineData(false, "cd {project}/lnk/.. && inspect; cat notes.txt")]
    public async Task Parent_segment_after_a_link_keeps_exact_consent(bool targetInsideProject, string command)
    {
        await using var harness = await CreateHarnessAsync(
            "causal-list-link-parent",
            Approvals.PersistentAnywhere("cd", "git status", "inspect", "cat"));
        var target = targetInsideProject
            ? Path.Combine(harness.ProjectDirectory, "real", "deep")
            : Path.Combine(Path.GetDirectoryName(harness.ProjectDirectory)!, "outside", "deep");
        Directory.CreateDirectory(target);
        Directory.CreateSymbolicLink(Path.Combine(harness.ProjectDirectory, "lnk"), target);

        var observed = await harness.EvaluateShellAsync(
            command.Replace("{project}", harness.ProjectDirectory, StringComparison.Ordinal),
            Ct);

        // The OS applies ".." after the link, so the lexical parent is not the real
        // directory (#2283). The directory proof cannot use that scope: even existing
        // grants give only exact consent.
        Assert.Equal(ApprovalOutcome.RequiresApproval, observed.Outcome);
        // Only exact consent: "Once" or "Deny".
        Assert.Equal([ObservedOptionKeys.ApproveOnce, ObservedOptionKeys.Deny], observed.Prompt!.OptionKeys);
    }

    [SlopwatchSuppress("SW001", "The Bash cases require a POSIX host.")]
    [Fact(SkipUnless = nameof(IsPosix), Skip = "The Bash cases require a POSIX host.")]
    public async Task Missing_target_offers_grants_for_that_folder_until_a_link_appears_there()
    {
        await using var harness = await CreateHarnessAsync("causal-list-missing-target");
        var missing = Path.Combine(ExternalDirectory(harness), "missing");
        var command = $"cd {missing} && inspect; cat ./*.md";

        var before = await harness.EvaluateShellAsync(command, Ct);
        Directory.CreateSymbolicLink(missing, harness.ProjectDirectory);
        var linked = await harness.EvaluateShellAsync(command, Ct);

        // A grant for a folder that does not exist covers only that exact folder. If cd
        // fails, the diagnostic runs in the project and has its own candidate there.
        Assert.Equal(ApprovalOutcome.RequiresApproval, before.Outcome);
        Assert.False(before.Prompt!.IsMessy);
        Assert.Contains(ObservedOptionKeys.ApproveAlways, before.Prompt.OptionKeys);
        Assert.Equal(
            [missing, missing, harness.ProjectDirectory, missing],
            before.Prompt.CandidateDirectories);
        // A link at that path later is not a proof, so the same call falls back to exact consent.
        // Only exact consent: "Once" or "Deny".
        Assert.Equal([ObservedOptionKeys.ApproveOnce, ObservedOptionKeys.Deny], linked.Prompt!.OptionKeys);
    }

    [SlopwatchSuppress("SW001", "The Bash cases require a POSIX host.")]
    [Fact(SkipUnless = nameof(IsPosix), Skip = "The Bash cases require a POSIX host.")]
    public async Task Project_causal_list_keeps_one_call_directory_advice()
    {
        await using var harness = await CreateHarnessAsync("causal-list-advice");
        var child = Directory.CreateDirectory(Path.Combine(harness.ProjectDirectory, "sub")).FullName;
        var command = $"cd {child} && inspect; head result.log";

        var advised = await harness.EvaluateShellAsync(command, Ct);

        Assert.Equal(ApprovalOutcome.RequiresAgentCorrection, advised.Outcome);
        Assert.Equal(ApprovalCorrection.ShellWorkingDirectory, advised.AgentCorrection);
        Assert.Equal(child, advised.AgentCorrectionTarget);
    }

    private Task<ShellApprovalHarness> CreateHarnessAsync(
        string caseId,
        ApprovalState? approvals = null,
        bool interactive = true)
        => ShellApprovalHarness.CreateAsync(
            caseId,
            new ShellApprovalInvocation("true", Interactive: interactive),
            approvals ?? Approvals.None,
            fixture.ActorSystem,
            Ct);

    // The harness keeps its external directory beside the project, outside every trusted root.
    private static string ExternalDirectory(ShellApprovalHarness harness)
        => Path.Combine(Path.GetDirectoryName(harness.ProjectDirectory)!, "workspaces", "external");
}
