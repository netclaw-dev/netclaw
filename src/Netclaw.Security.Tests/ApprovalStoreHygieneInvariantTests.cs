// -----------------------------------------------------------------------
// <copyright file="ApprovalStoreHygieneInvariantTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Diagnostics;
using Netclaw.Configuration;
using Xunit;

namespace Netclaw.Security.Tests;

/// <summary>
/// The store invariant: after any sequence of saves, the stored set holds no
/// grant with a file word, no grant that another grant covers, and no less
/// authority than the saves gave.
/// </summary>
public sealed class ApprovalStoreHygieneInvariantTests : IDisposable
{
    private const string Tool = "shell_execute";

    private static readonly string[][] Phrases =
    [
        ["dotnet", "build"],
        ["dotnet", "build", "Phobos.slnx"],
        ["dotnet", "list", "Phobos.slnx", "package"],
        ["dotnet", "test"],
        ["git", "push", "origin", "main"],
    ];

    private readonly string _root = Directory.CreateDirectory(Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        $"netclaw-hygiene-{Guid.NewGuid():N}")).FullName;

    public static bool IsPosix => !OperatingSystem.IsWindows();

    [SlopwatchSuppress("SW001", "The scope facts use POSIX folders and a Git repository.")]
    [Fact(SkipUnless = nameof(IsPosix), Skip = "The scope facts use POSIX folders and a Git repository.")]
    public void Stored_set_stays_clean_after_any_sequence_of_saves()
    {
        var repository = Path.Combine(_root, "repo");
        var source = Directory.CreateDirectory(Path.Combine(repository, "src", "deep")).Parent!.FullName;
        var other = Directory.CreateDirectory(Path.Combine(_root, "other")).FullName;
        RunGit(repository, "init", "-q");
        File.WriteAllText(Path.Combine(repository, "Phobos.slnx"), string.Empty);
        File.WriteAllText(Path.Combine(other, "Petabridge.Cmd.sln"), string.Empty);
        var link = Path.Combine(_root, "link");
        Directory.CreateSymbolicLink(link, source);
        string?[] folders = [null, repository, source, Path.Combine(source, "deep"), other, link];
        string?[] commandDirectories = [null, repository, other];

        var random = new Random(2337);
        for (var round = 0; round < 40; round++)
        {
            var store = new ToolApprovalStore(
                Path.Combine(_root, $"tool-approvals-{round}.json"),
                ApprovalScopeFacts.Instance);
            var saved = new List<ApprovalAddition>();
            for (var save = 0; save < 12; save++)
            {
                var words = Phrases[random.Next(Phrases.Length)];
                var useRepository = random.Next(6) == 0;
                var folder = folders[random.Next(folders.Length)];
                var entry = useRepository
                    ? ApprovalEntry.CreateRepositoryTokenPrefix(ApprovalShell.Bash, words, Path.Combine(repository, ".git"))
                    : ApprovalEntry.CreateTokenPrefix(ApprovalShell.Bash, words, folder);
                var addition = new ApprovalAddition(entry, commandDirectories[random.Next(commandDirectories.Length)]);
                var before = store.GetApprovedEntries(TrustAudience.Personal, Tool).ToArray();

                var change = store.TryAddApprovals(TrustAudience.Personal, Tool, [addition]);

                var stored = store.GetApprovedEntries(TrustAudience.Personal, Tool);
                var fileWords = ApprovalGrantHygiene.FileWords(entry, addition.CommandDirectory);
                if (fileWords.Count > 0)
                {
                    Assert.IsType<ApprovalStoreChangeResult.Unavailable>(change);
                    Assert.Equal(before.Length, stored.Count);
                    continue;
                }

                Assert.IsType<ApprovalStoreChangeResult.Completed>(change);
                saved.Add(addition);
                AssertClean(stored, saved, $"round {round}, save {save}: {entry.FormatScope()}");
            }
        }
    }

    // No file word, no covered grant, and each accepted save still covered.
    private static void AssertClean(
        IReadOnlyList<ApprovalEntry> stored,
        IReadOnlyList<ApprovalAddition> saved,
        string context)
    {
        Assert.All(stored, entry => Assert.Empty(ApprovalGrantHygiene.FileWords(entry, commandDirectory: null)));
        for (var left = 0; left < stored.Count; left++)
        for (var right = 0; right < stored.Count; right++)
        {
            Assert.False(
                left != right && ApprovalGrantHygiene.Covers(stored[left], stored[right], ApprovalScopeFacts.Instance),
                $"{context}: {stored[left].FormatScope()} covers {stored[right].FormatScope()}");
        }

        Assert.All(saved, addition => Assert.Contains(stored, entry =>
            ToolApprovalEntryComparer.Equals(entry, addition.Entry)
            || ApprovalGrantHygiene.Covers(entry, addition.Entry, ApprovalScopeFacts.Instance)));
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
