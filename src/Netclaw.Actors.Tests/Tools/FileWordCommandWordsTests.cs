// -----------------------------------------------------------------------
// <copyright file="FileWordCommandWordsTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Text.RegularExpressions;
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
/// A command word after the verb slot that names an existing file or directory
/// in the occurrence directory is an operand. A grant never stores it, and a
/// grant without it covers the call. The verb slot never drops.
/// </summary>
[Collection(ShellApprovalMatrixCollection.Name)]
public sealed class FileWordCommandWordsTests(ShellApprovalMatrixFixture fixture)
{
    public static bool IsPosix => !OperatingSystem.IsWindows();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly ToolApprovalSessionId OtherSession = (ToolApprovalSessionId)"signalr/other-session";

    // The owner's regression: each solution name became a command word.
    [SlopwatchSuppress("SW001", "The Bash cases require a POSIX host.")]
    [Theory(SkipUnless = nameof(IsPosix), Skip = "The Bash cases require a POSIX host.")]
    [InlineData("dotnet build", "dotnet build Phobos.slnx -c Release", "Phobos.slnx")]
    [InlineData("dotnet list package", "dotnet list Phobos.slnx package --vulnerable", "Phobos.slnx")]
    [InlineData("dotnet test", "dotnet test X.sln", "X.sln")]
    [InlineData("dotnet build", "dotnet build src", "src/")]
    public async Task Grant_without_the_file_word_covers_the_call(string grant, string command, string entry)
    {
        await using var harness = await CreateHarnessAsync(Approvals.PersistentAnywhere(grant), interactive: true);
        CreateEntry(harness, entry);

        await AssertAllowedByStoredGrantAsync(harness, command);
    }

    [SlopwatchSuppress("SW001", "The Bash cases require a POSIX host.")]
    [Fact(SkipUnless = nameof(IsPosix), Skip = "The Bash cases require a POSIX host.")]
    public async Task Saved_grant_does_not_store_the_file_word()
    {
        await using var harness = await CreateHarnessAsync(Approvals.None, interactive: true);
        CreateEntry(harness, "Phobos.slnx");
        CreateEntry(harness, "Other.sln");

        var stored = await ApproveEverywhereAsync(harness, "dotnet build Phobos.slnx");

        Assert.Equal(["dotnet", "build"], Assert.Single(stored).VerbTokens!);
        await AssertAllowedByStoredGrantAsync(harness, "dotnet build Other.sln -c Release");
    }

    // Negative controls: a word without a file stays a command word. A grant
    // word is never free, so another word in its position needs approval.
    [SlopwatchSuppress("SW001", "The Bash cases require a POSIX host.")]
    [Theory(SkipUnless = nameof(IsPosix), Skip = "The Bash cases require a POSIX host.")]
    [InlineData("git push origin feature-x", "git push origin main")]
    [InlineData("systemctl stop nginx.service", "systemctl stop sshd.service")]
    public async Task Word_without_a_file_stays_a_command_word(string grant, string command)
    {
        await using var harness = await CreateHarnessAsync(Approvals.PersistentAnywhere(grant), interactive: true);

        await AssertNeedsApprovalAsync(harness, command);
    }

    // SECURITY: a planted file named like a subcommand must not change the
    // identity of the command. The verb slot never drops.
    [SlopwatchSuppress("SW001", "The Bash cases require a POSIX host.")]
    [Fact(SkipUnless = nameof(IsPosix), Skip = "The Bash cases require a POSIX host.")]
    public async Task Verb_slot_word_never_drops()
    {
        await using var harness = await CreateHarnessAsync(Approvals.PersistentAnywhere("git"), interactive: true);
        CreateEntry(harness, "push");

        await AssertNeedsApprovalAsync(harness, "git push origin topic");
        var stored = await ApproveEverywhereAsync(harness, "git push origin topic");
        Assert.Contains(stored, entry => entry.VerbTokens!.SequenceEqual(["git", "push", "origin", "topic"]));
    }

    // A link keeps its word. The protected-path screen checks the link target,
    // but a link to an ordinary file keeps the decision of its grant: a folder
    // grant covers a link in its folder, attended or not.
    [SlopwatchSuppress("SW001", "The Bash cases require a POSIX host.")]
    [Theory(SkipUnless = nameof(IsPosix), Skip = "The Bash cases require a POSIX host.")]
    [InlineData("mytool write", "mytool write readmelink", true)]
    [InlineData("mytool write readmelink", "mytool write readmelink", true)]
    [InlineData("mytool write", "mytool write docs", true)]
    [InlineData("mytool write", "mytool write readmelink", false)]
    public async Task Folder_grant_covers_a_link_to_its_own_folder(string grant, string command, bool interactive)
    {
        await using var harness = await CreateHarnessAsync(
            Approvals.PersistentHere(ApprovalDirectoryShape.Project, grant),
            interactive);
        harness.CreateProjectFileSymlink("readmelink", "README.md");
        harness.CreateProjectDirectory("realdocs");
        Directory.CreateSymbolicLink(Path.Join(harness.ProjectDirectory, "docs"), Path.Join(harness.ProjectDirectory, "realdocs"));

        await AssertAllowedByStoredGrantAsync(harness, command);
    }

    [SlopwatchSuppress("SW001", "The Bash cases require a POSIX host.")]
    [Fact(SkipUnless = nameof(IsPosix), Skip = "The Bash cases require a POSIX host.")]
    public async Task Link_word_stays_a_command_word()
    {
        await using var harness = await CreateHarnessAsync(Approvals.None, interactive: true);
        harness.CreateProjectFileSymlinkToExternalFile("Linked.slnx");

        var prompt = await PromptAsync(harness, "dotnet build Linked.slnx");

        Assert.NotNull(prompt);
        Assert.Contains(
            prompt.Candidates!,
            candidate => candidate.VerbTokens!.SequenceEqual(["dotnet", "build", "Linked.slnx"]));
    }

    // SECURITY: a plain word that names a link to a protected path is denied,
    // command word or argument. A word with a digit is an argument (keys2), and
    // a verb grant covers later words, so neither form can hide the target.
    [SlopwatchSuppress("SW001", "The Bash cases require a POSIX host.")]
    [Theory(SkipUnless = nameof(IsPosix), Skip = "The Bash cases require a POSIX host.")]
    [InlineData(true, "git add keylink")]
    [InlineData(false, "git add keylink")]
    [InlineData(true, "git add keys2")]
    [InlineData(true, "git add key1link")]
    [InlineData(true, "git add README keylink")]
    public async Task Plain_word_link_to_a_protected_path_is_denied(bool everywhere, string command)
    {
        await using var harness = await CreateHarnessAsync(
            everywhere
                ? Approvals.PersistentAnywhere("git add")
                : Approvals.PersistentHere(ApprovalDirectoryShape.Project, "git add"),
            interactive: true);
        Directory.CreateDirectory(harness.Paths.KeysDirectory);
        await File.WriteAllTextAsync(Path.Join(harness.Paths.KeysDirectory, "a.pem"), "synthetic test data", Ct);
        Directory.CreateSymbolicLink(Path.Join(harness.ProjectDirectory, "keylink"), harness.Paths.KeysDirectory);
        Directory.CreateSymbolicLink(Path.Join(harness.ProjectDirectory, "keys2"), harness.Paths.KeysDirectory);
        File.CreateSymbolicLink(Path.Join(harness.ProjectDirectory, "key1link"), Path.Join(harness.Paths.KeysDirectory, "a.pem"));

        var decision = await harness.EvaluateShellDecisionAsync(command, Ct);

        Assert.Equal(ToolAuthorizationOutcome.Denied, decision.Outcome);
        Assert.Equal("shell_references_protected_path", decision.DenyReason);
        // Positive control: the same grant covers a plain later word.
        await AssertAllowedByStoredGrantAsync(harness, "git add README");
    }

    // SECURITY: a file word that leaves the command words is a path operand.
    // The protected-path checks still see it.
    [SlopwatchSuppress("SW001", "The Bash cases require a POSIX host.")]
    [Theory(SkipUnless = nameof(IsPosix), Skip = "The Bash cases require a POSIX host.")]
    [InlineData("cat secrets.json", "cat")]
    [InlineData("git add keys", "git add")]
    public async Task Protected_file_word_is_denied(string command, string grant)
    {
        await using var harness = await CreateHarnessAsync(Approvals.PersistentAnywhere(grant, "cd"), interactive: true);
        Directory.CreateDirectory(harness.Paths.KeysDirectory);
        Directory.CreateDirectory(harness.Paths.ConfigDirectory);
        await File.WriteAllTextAsync(Path.Combine(harness.Paths.ConfigDirectory, "secrets.json"), "{}", Ct);
        var directory = command.EndsWith("keys", StringComparison.Ordinal)
            ? Path.GetDirectoryName(harness.Paths.KeysDirectory)!
            : harness.Paths.ConfigDirectory;

        var decision = await harness.EvaluateShellDecisionAsync($"cd {directory} && {command}", Ct);

        Assert.Equal(ToolAuthorizationOutcome.Denied, decision.Outcome);
    }

    /// <summary>
    /// The regression gate. For each corpus command, the test creates a real
    /// file for each command word after the verb slot. Then (a) no prompt
    /// candidate keeps such a word, and (b) a grant saved from the prompt also
    /// covers the same command with another existing file in that position.
    /// </summary>
    [SlopwatchSuppress("SW001", "The Bash cases require a POSIX host.")]
    [Fact(SkipUnless = nameof(IsPosix), Skip = "The Bash cases require a POSIX host.")]
    public async Task File_words_never_become_grant_words()
    {
        var checkedWords = 0;
        var checkedGrants = 0;
        var failures = new List<string>();
        foreach (var command in GateCorpus())
        {
            await using var harness = await CreateHarnessAsync(Approvals.None, interactive: true);
            var before = await PromptAsync(harness, command);
            var fileWords = before?.Candidates?
                .SelectMany(static candidate => candidate.VerbTokens?.Skip(2) ?? [])
                .Where(IsPlainFileName)
                .ToHashSet(StringComparer.Ordinal) ?? [];
            if (fileWords.Count == 0)
                continue;

            foreach (var word in fileWords)
                CreateEntry(harness, word);

            var prompt = await PromptAsync(harness, command);
            if (prompt?.Candidates is not { } candidates)
                continue;

            // (a) No candidate keeps a file word after its verb slot. An exact
            // candidate of an unresolved command offers only "Once" and keeps
            // its words, because it has no path scope for the file word.
            checkedWords++;
            foreach (var candidate in candidates.Where(static candidate => candidate.Unresolved == ShellUnresolvedPart.None))
            {
                var kept = candidate.VerbTokens?.Skip(2).Where(fileWords.Contains).ToArray() ?? [];
                if (kept.Length > 0)
                    failures.Add($"(a) '{command}' kept {string.Join(", ", kept)} in [{string.Join(' ', candidate.VerbTokens!)}]");
            }

            // (b) The saved grant covers another existing file in the same position.
            var keptAnywhere = candidates.SelectMany(static candidate => candidate.VerbTokens ?? []).ToHashSet(StringComparer.Ordinal);
            var replaceable = fileWords.Where(word => !keptAnywhere.Contains(word)).ToArray();
            if (replaceable.Length == 0
                || !prompt.Options.Any(static option => option.Key.Value == ApprovalOptionKeys.ApproveEverywhere))
            {
                continue;
            }

            await RecordAsync(harness, GrantBuilder.Build(
                candidates,
                GrantScopeKind.Everywhere,
                prompt.Cwd,
                harness.SessionDirectory,
                prompt.RepositoryCommonDirectory));
            var other = command;
            foreach (var word in replaceable)
            {
                CreateEntry(harness, "other" + word);
                other = Regex.Replace(other, $"(?<=^|[\\s'\"]){Regex.Escape(word)}(?=$|[\\s'\";|&)])", "other" + word);
            }

            checkedGrants++;
            var decision = await harness.EvaluateShellDecisionAsync(other, Ct);
            if (decision.Outcome != ToolAuthorizationOutcome.Allowed)
                failures.Add($"(b) the grant for '{command}' did not cover '{other}': {decision.Outcome} {decision.DenyReason}");
        }

        Assert.True(failures.Count == 0, string.Join('\n', failures));
        // The gate must exercise real cases, or it proves nothing.
        Assert.True(checkedWords >= 20, $"Only {checkedWords} commands had a file word.");
        Assert.True(checkedGrants >= 15, $"Only {checkedGrants} grants were checked.");
    }

    // The interactive Bash catalog rows in the project directory, and command
    // shapes from real owner sessions. A directory change moves the occurrence
    // directory away from the files that the gate creates.
    private static IEnumerable<string> GateCorpus()
        => ShellApprovalCases.All
            .Where(static testCase => testCase.Invocation is
            {
                Host: ShellApprovalHost.Bash,
                WorkingDirectory: ApprovalDirectoryShape.Project,
                Audience: TrustAudience.Personal
            })
            .Select(static testCase => testCase.Invocation.Command)
            .Concat(
            [
                "dotnet build Phobos.slnx -c Release 2>&1 | tail -30",
                "dotnet test Phobos.slnx -c Release --no-restore",
                "dotnet restore Phobos.slnx",
                "dotnet list Phobos.slnx package --vulnerable --include-transitive",
                "dotnet build Petabridge.Cmd.sln",
                "dotnet list Petabridge.Cmd.sln package",
                "dotnet pack Akka.slnx -c Release",
                "dotnet format whitespace Akka.slnx --verify-no-changes",
                "kubectl apply deployment.yaml",
                "git add README.md CHANGELOG.md",
                "git diff README.md",
                "git checkout README.md",
                "git log README.md",
                "python3 manage.py migrate",
                "go run main.go",
                "docker compose build web",
                "make test",
                "npm run lint",
            ])
            .Where(static command => !Regex.IsMatch(command, @"(^|[\s;&|(])(cd|pushd|popd)(\s|$)"))
            .Distinct(StringComparer.Ordinal);

    private static bool IsPlainFileName(string word)
        => word.Length is > 0 and < 100
           && word.IndexOfAny(['/', '\0']) < 0
           && word is not ("." or "..");

    private static async Task<ToolApprovalContext?> PromptAsync(ShellApprovalHarness harness, string command)
    {
        var decision = await harness.EvaluateShellDecisionAsync(command, Ct);
        return decision.Outcome == ToolAuthorizationOutcome.RequiresApproval ? decision.ApprovalContext : null;
    }

    private static void CreateEntry(ShellApprovalHarness harness, string name)
    {
        var path = Path.Combine(harness.ProjectDirectory, name);
        if (name.EndsWith('/'))
            Directory.CreateDirectory(path);
        else if (!Path.Exists(path))
            File.WriteAllText(path, "synthetic test data");
    }

    private Task<ShellApprovalHarness> CreateHarnessAsync(ApprovalState approvals, bool interactive)
        => ShellApprovalHarness.CreateAsync(
            "file-word-command-words",
            new ShellApprovalInvocation("true", Interactive: interactive),
            approvals,
            fixture.ActorSystem,
            Ct);

    // The session actor builds and records the grants of an "Always anywhere" answer.
    private static async Task<IReadOnlyList<ApprovalEntry>> ApproveEverywhereAsync(
        ShellApprovalHarness harness,
        string command)
    {
        var approval = await PromptAsync(harness, command);
        Assert.NotNull(approval);
        Assert.Contains(approval.Options, option => option.Key.Value == ApprovalOptionKeys.ApproveEverywhere);

        await RecordAsync(harness, GrantBuilder.Build(
            approval.Candidates!,
            GrantScopeKind.Everywhere,
            approval.Cwd,
            harness.SessionDirectory,
            approval.RepositoryCommonDirectory));
        return harness.GetStoredShellEntries(TrustAudience.Personal);
    }

    private static Task RecordAsync(ShellApprovalHarness harness, IReadOnlyList<ToolApprovalGrant> grants)
        => harness.ApprovalService.RecordApprovalCandidatesAsync(
            OtherSession,
            TrustAudience.Personal,
            new ToolName(ShellTool.ToolName),
            grants,
            Ct);

    private static async Task AssertAllowedByStoredGrantAsync(ShellApprovalHarness harness, string command)
    {
        var decision = await harness.EvaluateShellDecisionAsync(command, Ct);
        Assert.True(
            decision.Outcome == ToolAuthorizationOutcome.Allowed
            && decision.AllowReason == ToolAllowReason.StoredApproval,
            $"'{command}' was {decision.Outcome} ({decision.AllowReason}); a stored grant should cover it.");
    }

    private static async Task AssertNeedsApprovalAsync(ShellApprovalHarness harness, string command)
    {
        var decision = await harness.EvaluateShellDecisionAsync(command, Ct);
        Assert.True(
            decision.Outcome == ToolAuthorizationOutcome.RequiresApproval,
            $"'{command}' was {decision.Outcome} ({decision.DenyReason}).");
    }
}
