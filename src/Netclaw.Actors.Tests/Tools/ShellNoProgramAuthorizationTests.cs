// -----------------------------------------------------------------------
// <copyright file="ShellNoProgramAuthorizationTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Actors.Authorization;
using Netclaw.Actors.Protocol;
using Netclaw.Actors.Tools;
using Netclaw.Configuration;
using Netclaw.Security;
using Netclaw.Security.Authorization.Consent;
using Netclaw.Tools;
using Netclaw.Security.Authorization.Filesystem;
using Xunit;

namespace Netclaw.Actors.Tests.Tools;

/// <summary>
/// Owner decision (October 2026): a command that runs no program gets no
/// prompt. An assignment, a command with only redirects, and a data command
/// (<c>echo</c>, <c>printf</c>, <c>:</c>, <c>true</c>) have no effect outside
/// the shell except their redirects. The file rules of the audience judge each
/// redirect target: a write target gets the write rules, and an input redirect
/// also gets the <c>file_read</c> rules. The call is allowed or denied, never
/// prompted. A prompt for an unknown program always shows the command text.
/// </summary>
[Collection(ShellApprovalMatrixCollection.Name)]
public sealed class ShellNoProgramAuthorizationTests(ShellApprovalMatrixFixture fixture)
{
    public static bool IsPosix => !OperatingSystem.IsWindows();

    private const string SessionId = "signalr/no-program";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // {C} is the config directory, {P} the project directory, and {X} a folder
    // outside the project.
    [SlopwatchSuppress("SW001", "The Bash cases require a POSIX host.")]
    [Theory(SkipUnless = nameof(IsPosix), Skip = "The Bash cases require a POSIX host.")]
    [InlineData(": > {C}/secrets.json", true)]
    [InlineData(": > {C}/secrets.json", false)]
    [InlineData("echo x > {C}/tool-approvals.json", true)]
    [InlineData("echo x > {C}/tool-approvals.json", false)]
    [InlineData("> {C}/netclaw.json", true)]
    [InlineData("printf a >> {C}/netclaw.json", true)]
    [InlineData("true > {C}/new.json", true)]
    [InlineData(": < {C}/secrets.json", true)]
    [InlineData("x=$(< {C}/secrets.json)", true)]
    public async Task Redirect_into_a_protected_path_is_denied(string command, bool attended)
    {
        await using var scope = await CreateHarnessAsync(attended);
        var harness = scope.Harness;

        var observed = await harness.EvaluateShellAsync(Expand(harness, command), Ct);

        Assert.Equal(ApprovalOutcome.Denied, observed.Outcome);
        Assert.Contains(observed.DenyReason, new[] { "shell_path_protected", "shell_references_protected_path" });
    }

    [SlopwatchSuppress("SW001", "The Bash cases require a POSIX host.")]
    [Theory(SkipUnless = nameof(IsPosix), Skip = "The Bash cases require a POSIX host.")]
    [InlineData("printf a > ../outside.txt")]
    [InlineData("> {X}/out.txt")]
    [InlineData("echo hi >> {X}/out.txt")]
    public async Task Redirect_outside_the_write_roots_is_denied(string command)
    {
        await using var scope = await CreateHarnessAsync(attended: true, ConfineWritesToProject);
        var harness = scope.Harness;

        var observed = await harness.EvaluateShellAsync(Expand(harness, command), Ct);

        Assert.Equal(ApprovalOutcome.Denied, observed.Outcome);
        Assert.Equal("shell_path_outside_trusted_roots", observed.DenyReason);
    }

    // Positive control for the bounded profile: a write inside the project root.
    [SlopwatchSuppress("SW001", "The Bash cases require a POSIX host.")]
    [Fact(SkipUnless = nameof(IsPosix), Skip = "The Bash cases require a POSIX host.")]
    public async Task Redirect_inside_the_write_roots_is_allowed()
    {
        await using var scope = await CreateHarnessAsync(attended: true, ConfineWritesToProject);
        var harness = scope.Harness;

        var observed = await harness.EvaluateShellAsync("printf a > drafts.txt && : > drafts.json", Ct);

        Assert.Equal(ApprovalOutcome.Allowed, observed.Outcome);
        Assert.Equal(ApprovalAllowReason.ApprovalExemptShellCandidates, observed.AllowReason);
        Assert.Empty(observed.ApprovalMatches);
    }

    [SlopwatchSuppress("SW001", "The Bash cases require a POSIX host.")]
    [Theory(SkipUnless = nameof(IsPosix), Skip = "The Bash cases require a POSIX host.")]
    [InlineData(": < {X}/notes.txt", true)]
    [InlineData(": < {X}/notes.txt", false)]
    [InlineData("< {X}/notes.txt", true)]
    [InlineData("x=$(< {X}/notes.txt)", true)]
    public async Task Input_redirect_outside_the_read_roots_is_denied(string command, bool attended)
    {
        await using var scope = await CreateHarnessAsync(attended, ConfineReadsToProject);
        var harness = scope.Harness;
        var target = Expand(harness, "{X}/notes.txt");

        var observed = await harness.EvaluateShellAsync(Expand(harness, command), Ct);

        Assert.Equal(ApprovalOutcome.Denied, observed.Outcome);
        Assert.Equal("shell_redirect_read_denied", observed.DenyReason);
        Assert.Contains(target, observed.DenyMessage, StringComparison.Ordinal);
    }

    // Positive control: the same read inside the project root, which file_read may read.
    [SlopwatchSuppress("SW001", "The Bash cases require a POSIX host.")]
    [Fact(SkipUnless = nameof(IsPosix), Skip = "The Bash cases require a POSIX host.")]
    public async Task Input_redirect_inside_the_read_roots_is_allowed()
    {
        await using var scope = await CreateHarnessAsync(attended: true, ConfineReadsToProject);
        var harness = scope.Harness;

        var observed = await harness.EvaluateShellAsync(": < notes.txt", Ct);

        Assert.Equal(ApprovalOutcome.Allowed, observed.Outcome);
        Assert.Equal(ApprovalAllowReason.ApprovalExemptShellCandidates, observed.AllowReason);
    }

    // A program still prompts: the file rules alone never allow it.
    [SlopwatchSuppress("SW001", "The Bash cases require a POSIX host.")]
    [Fact(SkipUnless = nameof(IsPosix), Skip = "The Bash cases require a POSIX host.")]
    public async Task Program_with_an_input_redirect_still_prompts()
    {
        await using var scope = await CreateHarnessAsync(attended: true);
        var harness = scope.Harness;

        var observed = await harness.EvaluateShellAsync("tee copy.txt < notes.txt", Ct);

        Assert.Equal(ApprovalOutcome.RequiresApproval, observed.Outcome);
        Assert.Equal(["tee"], observed.Prompt!.CandidateVerbs);
    }

    // The default Team and Public profiles do not admit the shell tool, so
    // such a chat never reaches the file rules.
    [SlopwatchSuppress("SW001", "The Bash cases require a POSIX host.")]
    [Theory(SkipUnless = nameof(IsPosix), Skip = "The Bash cases require a POSIX host.")]
    [InlineData(TrustAudience.Team)]
    [InlineData(TrustAudience.Public)]
    public async Task Team_and_public_audiences_are_denied(TrustAudience audience)
    {
        await using var harness = await ShellApprovalHarness.CreateAsync(
            "no-program-audience",
            new ShellApprovalInvocation(
                ": > drafts.json",
                Audience: audience,
                Host: ShellApprovalHost.Bash52),
            Approvals.None,
            fixture.ActorSystem,
            Ct);

        var observed = await harness.EvaluateAsync(Ct);

        Assert.Equal(ApprovalOutcome.Denied, observed.Outcome);
        Assert.Equal("tool_not_allowed_for_audience_profile", observed.DenyReason);
    }

    // A redirect is a write (or a read) without the file tool. It gets the
    // consent mode of that tool: Deny denies the call.
    [SlopwatchSuppress("SW001", "The Bash cases require a POSIX host.")]
    [Theory(SkipUnless = nameof(IsPosix), Skip = "The Bash cases require a POSIX host.")]
    [InlineData("file_write", "echo x > out.txt", true)]
    [InlineData("file_write", "echo x > out.txt", false)]
    [InlineData("file_write", "> out.txt", true)]
    [InlineData("file_write", ": >> out.txt", true)]
    [InlineData("file_read", ": < notes.txt", true)]
    [InlineData("file_read", "< notes.txt", false)]
    public async Task Denied_file_tool_denies_the_redirect(string tool, string command, bool attended)
    {
        await using var scope = await CreateHarnessAsync(attended, fileToolModes: new() { [tool] = ToolApprovalMode.Deny });
        var harness = scope.Harness;

        var observed = await harness.EvaluateShellAsync(command, Ct);

        Assert.Equal(ApprovalOutcome.Denied, observed.Outcome);
        Assert.Equal("shell_redirect_file_tool_denied", observed.DenyReason);
        Assert.Contains(tool, observed.DenyMessage, StringComparison.Ordinal);
    }

    // Approval keeps a prompt. The prompt names the write or the read, shows
    // the full command, and offers only "Once" and "Deny".
    [SlopwatchSuppress("SW001", "The Bash cases require a POSIX host.")]
    [Theory(SkipUnless = nameof(IsPosix), Skip = "The Bash cases require a POSIX host.")]
    [InlineData("file_write", "echo x > out.txt", "write {P}/out.txt")]
    [InlineData("file_write", "> out.txt", "write {P}/out.txt")]
    [InlineData("file_write", "printf a > a.txt 2> b.txt", "write {P}/a.txt, write {P}/b.txt")]
    [InlineData("file_read", ": < notes.txt", "read {P}/notes.txt")]
    public async Task File_tool_with_approval_mode_keeps_a_prompt_that_names_the_file(
        string tool,
        string command,
        string expected)
    {
        await using var scope = await CreateHarnessAsync(attended: true, fileToolModes: new() { [tool] = ToolApprovalMode.Approval });
        var harness = scope.Harness;

        var initial = await harness.EvaluateShellAsync(command, Ct);

        Assert.Equal(ApprovalOutcome.RequiresApproval, initial.Outcome);
        Assert.Equal([Expand(harness, expected)], initial.Prompt!.CandidateVerbs);
        Assert.Equal(command, initial.Prompt.DisplayText);
        Assert.Equal([ObservedOptionKeys.ApproveOnce, ObservedOptionKeys.Deny], initial.Prompt.OptionKeys);

        harness.SeedOneTimeApproval(initial.Prompt);
        var retry = await harness.EvaluateShellAsync(command, Ct);
        Assert.Equal(ApprovalOutcome.Allowed, retry.Outcome);
        Assert.Equal(ApprovalAllowReason.OneTimeApproval, retry.AllowReason);
    }

    // Each redirect of one command gets its own decision before the prompt. A
    // redirect that needs consent must not hide a denied redirect.
    [SlopwatchSuppress("SW001", "The Bash cases require a POSIX host.")]
    [Theory(SkipUnless = nameof(IsPosix), Skip = "The Bash cases require a POSIX host.")]
    [InlineData("file_read", "file_write", ": < notes.txt > out.txt")]
    [InlineData("file_read", "file_write", "< notes.txt > out.txt")]
    [InlineData("file_write", "file_read", ": < notes.txt > out.txt")]
    public async Task Denied_redirect_denies_a_command_with_another_redirect_that_needs_consent(
        string approvalTool,
        string deniedTool,
        string command)
    {
        await using var scope = await CreateHarnessAsync(
            attended: true,
            fileToolModes: new() { [approvalTool] = ToolApprovalMode.Approval, [deniedTool] = ToolApprovalMode.Deny });

        var observed = await scope.Harness.EvaluateShellAsync(command, Ct);

        Assert.Equal(ApprovalOutcome.Denied, observed.Outcome);
        Assert.Equal("shell_redirect_file_tool_denied", observed.DenyReason);
    }

    [SlopwatchSuppress("SW001", "The Bash cases require a POSIX host.")]
    [Fact(SkipUnless = nameof(IsPosix), Skip = "The Bash cases require a POSIX host.")]
    public async Task Refused_read_denies_a_command_with_a_write_that_needs_consent()
    {
        await using var scope = await CreateHarnessAsync(
            attended: true,
            ConfineReadsToProject,
            new() { ["file_write"] = ToolApprovalMode.Approval });
        var harness = scope.Harness;

        var observed = await harness.EvaluateShellAsync(Expand(harness, ": < {X}/notes.txt > out.txt"), Ct);

        Assert.Equal(ApprovalOutcome.Denied, observed.Outcome);
        Assert.Equal("shell_redirect_read_denied", observed.DenyReason);
    }

    // One prompt names each read and write that needs consent.
    [SlopwatchSuppress("SW001", "The Bash cases require a POSIX host.")]
    [Fact(SkipUnless = nameof(IsPosix), Skip = "The Bash cases require a POSIX host.")]
    public async Task One_prompt_names_each_redirect_that_needs_consent()
    {
        await using var scope = await CreateHarnessAsync(
            attended: true,
            fileToolModes: new() { ["file_write"] = ToolApprovalMode.Approval, ["file_read"] = ToolApprovalMode.Approval });
        var harness = scope.Harness;

        var observed = await harness.EvaluateShellAsync(": < notes.txt > out.txt", Ct);

        Assert.Equal(ApprovalOutcome.RequiresApproval, observed.Outcome);
        var name = Assert.Single(observed.Prompt!.CandidateVerbs);
        Assert.Contains(Expand(harness, "read {P}/notes.txt"), name, StringComparison.Ordinal);
        Assert.Contains(Expand(harness, "write {P}/out.txt"), name, StringComparison.Ordinal);
        Assert.Equal([ObservedOptionKeys.ApproveOnce, ObservedOptionKeys.Deny], observed.Prompt.OptionKeys);
    }

    // The redirect gets the answer of the file tool, with the stored grants of
    // that tool: a file_write grant covers the write, and no other grant does.
    [SlopwatchSuppress("SW001", "The Bash cases require a POSIX host.")]
    [Theory(SkipUnless = nameof(IsPosix), Skip = "The Bash cases require a POSIX host.")]
    [InlineData("file_write", "echo x > out.txt", true)]
    [InlineData("file_write", "> out.txt", true)]
    [InlineData("file_read", "echo x > out.txt", false)]
    public async Task Stored_grant_of_the_file_tool_covers_the_redirect(string grantedTool, string command, bool covered)
    {
        await using var scope = await CreateHarnessAsync(
            attended: true,
            fileToolModes: new() { ["file_write"] = ToolApprovalMode.Approval });
        var harness = scope.Harness;
        await harness.ApprovalService.RecordApprovalCandidatesAsync(
            (ToolApprovalSessionId)SessionId,
            TrustAudience.Personal,
            new ToolName(grantedTool),
            [new ToolApprovalGrant(new ApprovalCandidate(grantedTool, Directory: null), GrantScope.Session.Instance)],
            Ct);

        var observed = await harness.EvaluateShellAsync(command, Ct);

        if (covered)
        {
            Assert.Equal(ApprovalOutcome.Allowed, observed.Outcome);
            Assert.Equal(ApprovalAllowReason.ApprovalExemptShellCandidates, observed.AllowReason);
        }
        else
        {
            Assert.Equal(ApprovalOutcome.RequiresApproval, observed.Outcome);
            Assert.Equal([Expand(harness, "write {P}/out.txt")], observed.Prompt!.CandidateVerbs);
        }
    }

    // The managed temporary directory advice replaces a prompt. A command that
    // runs no program has no prompt, so both spellings run with no advice.
    // {T} is the physical platform temporary root (macOS: /private/tmp).
    [SlopwatchSuppress("SW001", "The Bash cases require a POSIX host.")]
    [Theory(SkipUnless = nameof(IsPosix), Skip = "The Bash cases require a POSIX host.")]
    [InlineData(": > {T}/netclaw-no-program.txt")]
    [InlineData("cd {T} && : > netclaw-no-program.txt")]
    public async Task Redirect_into_the_temporary_root_gets_no_advice(string command)
    {
        await using var scope = await CreateHarnessAsync(attended: true);
        FileSystemAuthority.TryResolveLinks("/tmp", out var temporaryRoot);

        var observed = await scope.Harness.EvaluateShellAsync(
            command.Replace("{T}", temporaryRoot, StringComparison.Ordinal), Ct);

        Assert.Equal(ApprovalOutcome.Allowed, observed.Outcome);
        Assert.Null(observed.AgentCorrection);
    }

    // A redirect target that is a link gets the decision of the file tool for
    // the target of the link: a protected target and a target outside the
    // write roots deny the call. It never runs with no prompt.
    [SlopwatchSuppress("SW001", "The Bash cases require a POSIX host.")]
    [Theory(SkipUnless = nameof(IsPosix), Skip = "The Bash cases require a POSIX host.")]
    [InlineData(": > secretlink.json", false)]
    [InlineData("> secretlink.json", false)]
    [InlineData("echo x >> secretlink.json", false)]
    [InlineData(": > outsidelink.txt", true)]
    [InlineData("> outsidelink.txt", true)]
    public async Task Redirect_to_a_link_gets_the_decision_of_the_link_target(string command, bool boundedWrites)
    {
        await using var scope = await CreateHarnessAsync(attended: true, boundedWrites ? ConfineWritesToProject : null);
        var harness = scope.Harness;
        File.CreateSymbolicLink(Path.Combine(harness.ProjectDirectory, "secretlink.json"), harness.Paths.SecretsPath);
        File.CreateSymbolicLink(
            Path.Combine(harness.ProjectDirectory, "outsidelink.txt"),
            Path.Combine(OutsideDirectory(harness), "notes.txt"));

        var observed = await harness.EvaluateShellAsync(command, Ct);

        Assert.Equal(ApprovalOutcome.Denied, observed.Outcome);
    }

    // Limit: the file rules judge the lexical path of a folder grant scope, so
    // a target behind a link keeps a prompt that shows the command text.
    [SlopwatchSuppress("SW001", "The Bash cases require a POSIX host.")]
    [Fact(SkipUnless = nameof(IsPosix), Skip = "The Bash cases require a POSIX host.")]
    public async Task Target_behind_a_link_keeps_a_prompt()
    {
        await using var scope = await CreateHarnessAsync(attended: true);
        var harness = scope.Harness;
        Directory.CreateSymbolicLink(Path.Combine(harness.ProjectDirectory, "linked"), OutsideDirectory(harness));

        var observed = await harness.EvaluateShellAsync(": > linked/out.txt", Ct);

        Assert.Equal(ApprovalOutcome.RequiresApproval, observed.Outcome);
        Assert.Equal([": > linked/out.txt"], observed.Prompt!.CandidateVerbs);
        Assert.Equal([ObservedOptionKeys.ApproveOnce, ObservedOptionKeys.Deny], observed.Prompt.OptionKeys);
    }

    [SlopwatchSuppress("SW001", "The Bash cases require a POSIX host.")]
    [Fact(SkipUnless = nameof(IsPosix), Skip = "The Bash cases require a POSIX host.")]
    public async Task File_tool_with_approval_mode_denies_an_unattended_redirect()
    {
        await using var scope = await CreateHarnessAsync(attended: false, fileToolModes: new() { ["file_write"] = ToolApprovalMode.Approval });

        var observed = await scope.Harness.EvaluateShellAsync("echo x > out.txt", Ct);

        Assert.Equal(ApprovalOutcome.Denied, observed.Outcome);
        Assert.Equal(ToolAuthorizer.UnattendedApprovalRequired, observed.DenyReason);
    }

    // Controls: an Auto mode runs with no prompt, the mode of the other file
    // tool does not decide, and a program keeps its own prompt.
    [SlopwatchSuppress("SW001", "The Bash cases require a POSIX host.")]
    [Theory(SkipUnless = nameof(IsPosix), Skip = "The Bash cases require a POSIX host.")]
    [InlineData("file_write", ToolApprovalMode.Auto, "echo x > out.txt")]
    [InlineData("file_read", ToolApprovalMode.Deny, "echo x > out.txt")]
    [InlineData("file_write", ToolApprovalMode.Deny, ": < notes.txt")]
    [InlineData("file_write", ToolApprovalMode.Deny, "echo x > /dev/null")]
    [InlineData("file_write", ToolApprovalMode.Deny, "echo x 2>&1")]
    public async Task Other_file_tool_modes_leave_the_redirect_alone(string tool, ToolApprovalMode mode, string command)
    {
        await using var scope = await CreateHarnessAsync(attended: true, fileToolModes: new() { [tool] = mode });

        var observed = await scope.Harness.EvaluateShellAsync(command, Ct);

        Assert.Equal(ApprovalOutcome.Allowed, observed.Outcome);
        Assert.Equal(ApprovalAllowReason.ApprovalExemptShellCandidates, observed.AllowReason);
    }

    [SlopwatchSuppress("SW001", "The Bash cases require a POSIX host.")]
    [Fact(SkipUnless = nameof(IsPosix), Skip = "The Bash cases require a POSIX host.")]
    public async Task Denied_file_tool_does_not_judge_a_program()
    {
        await using var scope = await CreateHarnessAsync(attended: true, fileToolModes: new() { ["file_write"] = ToolApprovalMode.Deny });

        var observed = await scope.Harness.EvaluateShellAsync("date > out.txt", Ct);

        Assert.Equal(ApprovalOutcome.RequiresApproval, observed.Outcome);
        Assert.Equal(["date"], observed.Prompt!.CandidateVerbs);
    }

    // The call runs: each file appears, and no grant exists afterward.
    [SlopwatchSuppress("SW001", "The Bash cases require a POSIX host.")]
    [Fact(SkipUnless = nameof(IsPosix), Skip = "The Bash cases require a POSIX host.")]
    public async Task Command_that_runs_no_program_runs_without_a_prompt()
    {
        await using var scope = await CreateHarnessAsync(attended: true);
        var harness = scope.Harness;

        var run = await harness.RunShellAsync("printf 'a\\tb\\n' > harvest.tsv && : > harvest.json && x=1", Ct);

        Assert.Equal(ApprovalOutcome.Allowed, run.Outcome);
        Assert.Equal("a\tb\n", await File.ReadAllTextAsync(Path.Combine(harness.ProjectDirectory, "harvest.tsv"), Ct));
        Assert.True(File.Exists(Path.Combine(harness.ProjectDirectory, "harvest.json")));
        Assert.Equal(0, harness.ApprovalService.CheckCount);
    }

    // An unknown program word prompts with the full command text, and the
    // "Once" answer of that prompt covers the retry of the same call.
    [SlopwatchSuppress("SW001", "The Bash cases require a POSIX host.")]
    [Theory(SkipUnless = nameof(IsPosix), Skip = "The Bash cases require a POSIX host.")]
    [InlineData("$cmd > drafts.txt")]
    [InlineData("eval x")]
    [InlineData("x=1 > drafts.txt")]
    public async Task Unknown_program_prompts_with_its_text_and_once_covers_the_retry(string command)
    {
        await using var harness = await ShellApprovalHarness.CreateAsync(
            "no-program-unknown",
            new ShellApprovalInvocation(command, Host: ShellApprovalHost.Bash52),
            Approvals.None,
            fixture.ActorSystem,
            Ct);

        var initial = await harness.EvaluateAsync(Ct);
        Assert.Equal(ApprovalOutcome.RequiresApproval, initial.Outcome);
        Assert.Equal([command], initial.Prompt!.CandidateVerbs);
        Assert.Equal(command, initial.Prompt.DisplayText);
        Assert.True(initial.Prompt.IsMessy);
        Assert.Equal([ObservedOptionKeys.ApproveOnce, ObservedOptionKeys.Deny], initial.Prompt.OptionKeys);

        harness.SeedOneTimeApproval(initial.Prompt);
        var retry = await harness.EvaluateAsync(Ct);

        Assert.Equal(ApprovalOutcome.Allowed, retry.Outcome);
        Assert.Equal(ApprovalAllowReason.OneTimeApproval, retry.AllowReason);
    }

    // The guard: a consent request with no candidate gets the full command
    // text and only "Once" and "Deny". Every other decision does not change.
    [Fact]
    public void Consent_request_without_a_candidate_shows_its_full_text()
    {
        var request = new ToolApprovalContext(
            ShellTool.ToolName,
            "> \"$f\"",
            [],
            [],
            [new ToolApprovalOption(ApprovalOptionKeys.ApproveSessionKey, "This chat")]);
        var consent = new AuthorizationDecision.NeedsConsent(request, [], ShellPolicyDecisionTrace.Empty);

        var shown = Assert.IsType<AuthorizationDecision.NeedsConsent>(ToolAuthorizer.ShowFullCommandText(consent));

        Assert.Equal(["> \"$f\""], shown.Request.CandidateVerbs);
        Assert.True(shown.Request.IsMessy);
        Assert.Equal(
            [ApprovalOptionKeys.ApproveOnce, ApprovalOptionKeys.Deny],
            shown.Request.Options.Select(static option => option.Key.Value));

        var named = consent with { Request = request with { CandidateVerbs = ["git push"] } };
        Assert.Same(named, ToolAuthorizer.ShowFullCommandText(named));
    }

    private static void ConfineWritesToProject(ToolConfig config, string project)
        => config.AudienceProfiles.Personal.WriteFiles = new ToolFilesystemAccessProfile
        {
            Mode = ToolFilesystemMode.Roots,
            Roots = [project]
        };

    private static void ConfineReadsToProject(ToolConfig config, string project)
    {
        config.AudienceProfiles.GlobalReadRoots = [];
        config.AudienceProfiles.Personal.ReadFiles = new ToolFilesystemAccessProfile
        {
            Mode = ToolFilesystemMode.Roots,
            Roots = [project]
        };
    }

    // A temporary root holds the project, the session, and a folder outside
    // the project. Disposal deletes the root.
    private async Task<HarnessScope> CreateHarnessAsync(
        bool attended,
        Action<ToolConfig, string>? configure = null,
        Dictionary<string, ToolApprovalMode>? fileToolModes = null)
    {
        var root = Directory.CreateTempSubdirectory("netclaw-no-program-");
        try
        {
            // A macOS temporary root has links. The file rules need the physical path.
            FileSystemAuthority.TryResolveLinks(root.FullName, out var physicalRoot);
            var project = Directory.CreateDirectory(Path.Combine(physicalRoot, "project")).FullName;
            var session = Directory.CreateDirectory(Path.Combine(physicalRoot, "session")).FullName;
            var outside = Directory.CreateDirectory(Path.Combine(physicalRoot, "outside")).FullName;
            var harness = await ShellApprovalHarness.CreateAsync(
                "no-program",
                new ShellApprovalInvocation("true", Interactive: attended, Host: ShellApprovalHost.Bash52),
                Approvals.None,
                fixture.ActorSystem,
                Ct,
                scope: new ShellApprovalHarnessScope(project, session, SessionId, []),
                policy: new ShellApprovalHarnessPolicy
                {
                    PersonalApprovalOverrides = fileToolModes ?? [],
                    ConfigureTools = configure is null ? null : config => configure(config, project)
                });
            var paths = harness.Paths;
            Directory.CreateDirectory(paths.ConfigDirectory);
            await File.WriteAllTextAsync(Path.Combine(outside, "notes.txt"), "outside", Ct);
            await File.WriteAllTextAsync(Path.Combine(project, "notes.txt"), "inside", Ct);
            await File.WriteAllTextAsync(Path.Combine(paths.ConfigDirectory, "netclaw.json"), "{}", Ct);
            await File.WriteAllTextAsync(Path.Combine(paths.ConfigDirectory, "tool-approvals.json"), "{}", Ct);
            await File.WriteAllTextAsync(paths.SecretsPath, "{}", Ct);
            return new HarnessScope(harness, root);
        }
        catch
        {
            root.Delete(recursive: true);
            throw;
        }
    }

    private sealed class HarnessScope(ShellApprovalHarness harness, DirectoryInfo root) : IAsyncDisposable
    {
        public ShellApprovalHarness Harness => harness;

        public async ValueTask DisposeAsync()
        {
            await harness.DisposeAsync();
            root.Delete(recursive: true);
        }
    }

    private static string Expand(ShellApprovalHarness harness, string command)
        => command
            .Replace("{C}", harness.Paths.ConfigDirectory, StringComparison.Ordinal)
            .Replace("{P}", harness.ProjectDirectory, StringComparison.Ordinal)
            .Replace("{X}", OutsideDirectory(harness), StringComparison.Ordinal);

    private static string OutsideDirectory(ShellApprovalHarness harness)
        => Path.Combine(Path.GetDirectoryName(harness.ProjectDirectory)!, "outside");
}
