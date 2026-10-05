// -----------------------------------------------------------------------
// <copyright file="ApprovalStoreHygieneInvariantTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Diagnostics;
using Netclaw.Configuration;
using Netclaw.Tools;
using Xunit;

namespace Netclaw.Security.Tests;

/// <summary>
/// The store invariant, checked with the real approval matcher: a save never
/// takes away a call that the store allowed, a saved grant allows what it
/// names, a folder grant never stores a file word, and the doctor removes only
/// grants whose calls stay allowed.
/// </summary>
public sealed class ApprovalStoreHygieneInvariantTests : IDisposable
{
    private const string Tool = "shell_execute";

    private static readonly string[][] Phrases =
    [
        ["git", "status"],
        ["npm", "run", "build"],
        ["dotnet", "build"],
        ["dotnet", "build", "Phobos.slnx"],
        // A verb grant covers longer grants; a program-only grant covers none.
        ["git", "push"],
        ["git", "push", "upstream"],
        ["git", "push", "upstream", "feature-x"],
        ["gh"],
        ["gh", "auth", "logout"],
    ];

    private static readonly string[] Commands =
    [
        "git status",
        "npm run build",
        "dotnet build",
        "dotnet build Phobos.slnx",
        "git push upstream",
        "git push upstream feature-x",
        "git push origin main",
        "gh --help",
        "gh auth logout",
    ];

    private readonly string _root = Directory.CreateDirectory(Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        $"netclaw-hygiene-{Guid.NewGuid():N}")).FullName;

    public static bool IsPosix => !OperatingSystem.IsWindows();

    [SlopwatchSuppress("SW001", "The probes use POSIX folders and Git repositories.")]
    [Fact(SkipUnless = nameof(IsPosix), Skip = "The probes use POSIX folders and Git repositories.")]
    public void Saves_and_doctor_never_take_away_an_allowed_call()
    {
        // A repository with a nested repository below a folder, a linked folder,
        // and a folder with a "build" script and a solution file.
        var repository = Path.Combine(_root, "repo");
        var sub = Directory.CreateDirectory(Path.Combine(repository, "sub")).FullName;
        var nested = Directory.CreateDirectory(Path.Combine(sub, "nested")).FullName;
        var tools = Directory.CreateDirectory(Path.Combine(_root, "tools")).FullName;
        RunGit(repository, "init", "-q");
        RunGit(nested, "init", "-q");
        File.WriteAllText(Path.Combine(repository, "Phobos.slnx"), "solution");
        File.WriteAllText(Path.Combine(tools, "build"), "#!/bin/sh");
        var link = Path.Combine(_root, "link");
        Directory.CreateSymbolicLink(link, sub);
        string[] directories = [repository, sub, nested, tools, link];
        string?[] folders = [null, repository, sub, nested, tools, link];

        var random = new Random(2339);
        for (var round = 0; round < 30; round++)
        {
            var store = new ToolApprovalStore(Path.Combine(_root, $"tool-approvals-{round}.json"));
            for (var save = 0; save < 10; save++)
            {
                var words = Phrases[random.Next(Phrases.Length)];
                var entry = random.Next(5) == 0
                    ? ApprovalEntry.CreateRepositoryTokenPrefix(ApprovalShell.Bash, words, Path.Combine(repository, ".git"))
                    : ApprovalEntry.CreateTokenPrefix(ApprovalShell.Bash, words, folders[random.Next(folders.Length)]);
                var before = Allowed(store, directories);

                var change = store.TryAddApprovals(TrustAudience.Personal, Tool, [entry]);

                var context = $"round {round}, save {save}: {entry.FormatScope()}";
                var stored = store.GetApprovedEntries(TrustAudience.Personal, Tool);
                var after = Allowed(store, directories);
                Assert.True(before.IsSubsetOf(after), $"{context} took away {string.Join("; ", before.Except(after))}");
                if (entry is { Repository: null, Directory: { } folder }
                    && ApprovalGrantHygiene.FileWords(entry, folder).Count > 0)
                {
                    Assert.IsType<ApprovalStoreChangeResult.Unavailable>(change);
                    continue;
                }

                Assert.IsType<ApprovalStoreChangeResult.Completed>(change);
                Assert.True(Allowed([entry], directories).IsSubsetOf(after), $"{context} does not allow what it names");
                Assert.All(stored, stored => Assert.Empty(stored is { Repository: null, Directory: { } own }
                    ? ApprovalGrantHygiene.FileWords(stored, own)
                    : []));
            }

            // The doctor removes only grants whose calls the rest still allow.
            var all = store.GetApprovedEntries(TrustAudience.Personal, Tool);
            var removable = ApprovalGrantHygiene.Analyze("personal", Tool, all)
                .Where(static finding => finding.Removable)
                .Select(static finding => finding.Entry)
                .ToHashSet();
            var kept = all.Where(entry => !removable.Contains(entry)).ToArray();
            Assert.True(
                Allowed(all, directories).SetEquals(Allowed(kept, directories)),
                $"round {round}: doctor removal changed a decision");
        }
    }

    // The calls that the stored grants allow, as "command in directory".
    private static HashSet<string> Allowed(ToolApprovalStore store, IReadOnlyList<string> directories)
        => Allowed(store.GetApprovedEntries(TrustAudience.Personal, Tool), directories);

    private static HashSet<string> Allowed(IReadOnlyList<ApprovalEntry> entries, IReadOnlyList<string> directories)
    {
        var allowed = new HashSet<string>(StringComparer.Ordinal);
        foreach (var directory in directories)
        foreach (var command in Commands)
        {
            var candidates = new ShellApprovalMatcher().ExtractCandidates(
                new ToolName(Tool),
                new Dictionary<string, object?> { ["Command"] = command, ["WorkingDirectory"] = directory });
            if (candidates.Count > 0
                && candidates.All(candidate => ApprovalPatternMatching.MatchesShellApproval(candidate, directory, entries)))
            {
                allowed.Add($"{command} in {directory}");
            }
        }

        return allowed;
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private static void RunGit(string directory, params string[] arguments)
    {
        Directory.CreateDirectory(directory);
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo("git")
            {
                WorkingDirectory = directory,
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                UseShellExecute = false,
            }
        };
        foreach (var argument in arguments)
            process.StartInfo.ArgumentList.Add(argument);

        Assert.True(process.Start());
        Assert.True(process.WaitForExit(10_000), "git timed out");
        Assert.Equal(0, process.ExitCode);
    }
}
