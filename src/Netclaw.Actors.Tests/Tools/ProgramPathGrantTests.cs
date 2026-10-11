// -----------------------------------------------------------------------
// <copyright file="ProgramPathGrantTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Actors.Authorization.Consent;
using Netclaw.Actors.Protocol;
using Netclaw.Actors.Tools;
using Netclaw.Configuration;
using Netclaw.Security;
using Netclaw.Security.Authorization.Consent;
using Netclaw.Tools;
using Xunit;

namespace Netclaw.Actors.Tests.Tools;

/// <summary>
/// R1: a program path means the file, not the spelling. The candidate builder
/// joins a relative program path with the working directory of its occurrence,
/// after each <c>cd</c>, and the grant stores that absolute path. One grant
/// then covers each spelling of the file, and no other file with that name.
/// </summary>
[Collection(ShellApprovalMatrixCollection.Name)]
public sealed class ProgramPathGrantTests(ShellApprovalMatrixFixture fixture)
{
    public static bool IsPosix => !OperatingSystem.IsWindows();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // Each spelling names {project}/tools/ilspycmd from a different directory.
    private static readonly string[] Spellings =
    [
        "cd {tools} && ./ilspycmd --version",
        "{tools}/ilspycmd --version",
        "cd {other} && ../tools/ilspycmd --version",
        "{other}/../tools/./ilspycmd --version",
    ];

    public static TheoryData<string> ApprovedSpellings => new(Spellings);

    [SlopwatchSuppress("SW001", "The Bash cases require a POSIX host.")]
    [Theory(SkipUnless = nameof(IsPosix), Skip = "The Bash cases require a POSIX host.")]
    [MemberData(nameof(ApprovedSpellings))]
    public async Task One_grant_covers_every_spelling_of_the_file(string approvedSpelling)
    {
        await using var harness = await CreateHarnessAsync(Approvals.None);
        var layout = Layout.Create(harness);
        var program = Path.Combine(layout.Tools, "ilspycmd");

        var (prompt, stored) = await ApproveEverywhereAsync(harness, layout.Expand(approvedSpelling));

        // The prompt shows the file that the operator approves.
        Assert.Equal([program], prompt.CandidateVerbs);
        Assert.Equal([program], Assert.Single(stored).VerbTokens!);
        foreach (var spelling in Spellings)
            await AssertAllowedByStoredGrantAsync(harness, layout.Expand(spelling));

        // The same name in another folder is another file.
        await AssertNeedsApprovalAsync(harness, layout.Expand("cd {other} && ./ilspycmd --version"));
        await AssertNeedsApprovalAsync(harness, layout.Expand("{other}/ilspycmd --version"));
        await AssertNeedsApprovalAsync(harness, layout.Expand("{tools}/ilspycmd2 --version"));
    }

    // The rule is lexical, so it does not read links. A link spelling is its own
    // grant, and a ".." after a link never reuses a grant: the OS follows the
    // link before it applies "..", so the lexical path can name another file.
    [SlopwatchSuppress("SW001", "The Bash cases require a POSIX host.")]
    [Fact(SkipUnless = nameof(IsPosix), Skip = "The Bash cases require a POSIX host.")]
    public async Task Symbolic_link_working_directory_uses_the_lexical_path()
    {
        await using var harness = await CreateHarnessAsync(Approvals.None);
        var layout = Layout.Create(harness);
        var linkParent = Directory.CreateDirectory(Path.Combine(harness.ProjectDirectory, "links")).FullName;
        var link = Path.Combine(linkParent, "tools");
        Directory.CreateSymbolicLink(link, layout.Tools);

        var (_, stored) = await ApproveEverywhereAsync(harness, $"cd {link} && ./ilspycmd --version");

        Assert.Contains(stored, entry => entry.VerbTokens!.SequenceEqual([Path.Combine(link, "ilspycmd")]));
        await AssertAllowedByStoredGrantAsync(harness, $"{link}/ilspycmd --version");
        await AssertNeedsApprovalAsync(harness, $"{layout.Tools}/ilspycmd --version");

        // Lexically {links}/ilspycmd. The OS runs {project}/ilspycmd.
        await ApproveEverywhereAsync(harness, $"{linkParent}/ilspycmd --version");
        await AssertNeedsApprovalAsync(harness, $"cd {link} && ../ilspycmd --version");
    }

    // ShellSyntaxTree gives no command words for a "~" program, so the call gets
    // a rewrite correction. The full path is the same file and the same grant.
    [SlopwatchSuppress("SW001", "The Bash cases require a POSIX host.")]
    [Fact(SkipUnless = nameof(IsPosix), Skip = "The Bash cases require a POSIX host.")]
    public async Task Home_program_path_is_one_grant_with_its_full_path()
    {
        await using var harness = await CreateHarnessAsync(Approvals.None);
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var program = $"{home}/.dotnet/tools/ilspycmd";

        var decision = await harness.EvaluateShellDecisionAsync("~/.dotnet/tools/ilspycmd --version", Ct);

        Assert.Equal(ToolAuthorizationOutcome.RequiresAgentCorrection, decision.Outcome);
        var correction = Assert.IsType<ToolCorrection.ShellCommandWordsRewriteSuggested>(decision.AgentCorrection);
        Assert.Equal(ShellCommandWordsRewrite.WriteProgramPathInFull, correction.Rewrite);

        var (_, stored) = await ApproveEverywhereAsync(harness, $"{program} --version");
        Assert.Equal([program], Assert.Single(stored).VerbTokens!);
        await AssertAllowedByStoredGrantAsync(harness, $"{home}/.dotnet/./tools//ilspycmd --version");
    }

    // Grants that older versions saved by spelling keep their files without a
    // new prompt. The store gives "~/x" and "/abs/x" their absolute path and
    // joins a folder grant's "./x" with its folder. A "./x" grant with no folder
    // covers each file that its spelling can reach, as before, and no other file.
    [SlopwatchSuppress("SW001", "The Bash cases require a POSIX host.")]
    [Theory(SkipUnless = nameof(IsPosix), Skip = "The Bash cases require a POSIX host.")]
    [InlineData("""{ "shell": "Bash", "match": "LegacyExact", "verb": "~/.dotnet/tools/ilspycmd", "directory": null, "createdAt": null }""", "{home}/.dotnet/tools/ilspycmd --version", true)]
    [InlineData("""{ "shell": "Bash", "match": "TokenPrefix", "verbTokens": ["{tools}/../tools/ilspycmd"], "directory": null, "createdAt": null }""", "cd {tools} && ./ilspycmd --version", true)]
    [InlineData("""{ "shell": "Bash", "match": "LegacyExact", "verb": "./prune.sh", "directory": "{tools}", "createdAt": null }""", "cd {tools}/sub && ../prune.sh", true)]
    [InlineData("""{ "shell": "Bash", "match": "LegacyExact", "verb": "./prune.sh", "directory": "{tools}", "createdAt": null }""", "cd {tools}/sub && ./prune.sh", false)]
    [InlineData("""{ "shell": "Bash", "match": "TokenPrefix", "verbTokens": ["./ilspycmd"], "directory": null, "createdAt": null }""", "cd {other} && ./ilspycmd --version", true)]
    [InlineData("""{ "shell": "Bash", "match": "TokenPrefix", "verbTokens": ["./ilspycmd"], "directory": null, "createdAt": null }""", "{tools}/ilspycmd --version", true)]
    [InlineData("""{ "shell": "Bash", "match": "TokenPrefix", "verbTokens": ["./ilspycmd"], "directory": null, "createdAt": null }""", "{tools}/my-ilspycmd --version", false)]
    public async Task Grant_saved_by_spelling_keeps_its_files(string storedEntry, string command, bool allowed)
    {
        await using var harness = await CreateHarnessAsync(Approvals.None);
        var layout = Layout.Create(harness);
        Directory.CreateDirectory(Path.Combine(layout.Tools, "sub"));
        Directory.CreateDirectory(harness.Paths.ConfigDirectory);
        await File.WriteAllTextAsync(
            harness.Paths.ToolApprovalsPath,
            $$"""{ "version": 3, "audiences": { "personal": { "shell_execute": [ {{layout.Expand(storedEntry)}} ] } } }""",
            Ct);

        if (allowed)
            await AssertAllowedByStoredGrantAsync(harness, layout.Expand(command));
        else
            await AssertNeedsApprovalAsync(harness, layout.Expand(command));
    }

    private Task<ShellApprovalHarness> CreateHarnessAsync(ApprovalState approvals)
        => ShellApprovalHarness.CreateAsync(
            "program-path-grant",
            new ShellApprovalInvocation("true"),
            approvals,
            fixture.ActorSystem,
            Ct);

    // The session actor builds and records the grants of an "Always anywhere" answer.
    private static async Task<(ToolApprovalContext Prompt, IReadOnlyList<ApprovalEntry> Stored)> ApproveEverywhereAsync(
        ShellApprovalHarness harness,
        string command)
    {
        var decision = await harness.EvaluateShellDecisionAsync(command, Ct);
        Assert.True(
            decision.Outcome == ToolAuthorizationOutcome.RequiresApproval,
            $"'{command}' was {decision.Outcome}; it needs a prompt.");
        var approval = Assert.IsType<ToolApprovalContext>(decision.ApprovalContext);
        Assert.Contains(approval.Options, option => option.Key.Value == ApprovalOptionKeys.ApproveEverywhere);

        var grants = GrantBuilder.Build(
            approval.Candidates!,
            GrantScopeKind.Everywhere,
            approval.Cwd,
            harness.SessionDirectory,
            approval.RepositoryCommonDirectory);
        await harness.ApprovalService.RecordApprovalCandidatesAsync(
            (ToolApprovalSessionId)"signalr/other-session",
            TrustAudience.Personal,
            new ToolName(ShellTool.ToolName),
            grants,
            Ct);
        return (approval, harness.GetStoredShellEntries(TrustAudience.Personal));
    }

    private static async Task AssertAllowedByStoredGrantAsync(ShellApprovalHarness harness, string command)
    {
        var decision = await harness.EvaluateShellDecisionAsync(command, Ct);
        Assert.True(
            decision.Outcome == ToolAuthorizationOutcome.Allowed
            && decision.AllowReason == ToolAllowReason.StoredApproval,
            $"'{command}' was {decision.Outcome} ({decision.AllowReason}{decision.DenyReason}); a stored grant should cover it. Candidates: {string.Join(", ", decision.ApprovalContext?.Candidates?.Select(c => string.Join(' ', c.VerbTokens ?? [c.Verb])) ?? [])}");
    }

    private static async Task AssertNeedsApprovalAsync(ShellApprovalHarness harness, string command)
    {
        var decision = await harness.EvaluateShellDecisionAsync(command, Ct);
        Assert.True(
            decision.Outcome == ToolAuthorizationOutcome.RequiresApproval,
            $"'{command}' was {decision.Outcome} ({decision.AllowReason}{decision.DenyReason}); no stored grant should cover it.");
    }

    private sealed record Layout(string Tools, string Other)
    {
        public static Layout Create(ShellApprovalHarness harness)
            => new(
                Directory.CreateDirectory(Path.Combine(harness.ProjectDirectory, "tools")).FullName,
                Directory.CreateDirectory(Path.Combine(harness.ProjectDirectory, "other")).FullName);

        public string Expand(string text)
            => text
                .Replace("{tools}", Tools, StringComparison.Ordinal)
                .Replace("{other}", Other, StringComparison.Ordinal)
                .Replace("{home}", Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), StringComparison.Ordinal);
    }
}
