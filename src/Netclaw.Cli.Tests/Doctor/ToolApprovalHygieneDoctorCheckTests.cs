// -----------------------------------------------------------------------
// <copyright file="ToolApprovalHygieneDoctorCheckTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Text.Json;
using Netclaw.Cli.Doctor;
using Netclaw.Cli.Tests.Cli;
using Netclaw.Actors.Tools;
using Netclaw.Configuration;
using Netclaw.Security;
using Netclaw.Tools;
using Xunit;

namespace Netclaw.Cli.Tests.Doctor;

/// <summary>
/// <c>netclaw doctor --fix</c> on a messy store shaped like real stores. It
/// removes only grants that another grant covers. Each call that the old store
/// allowed is still allowed: an "anywhere" grant with a file-like word stays, a
/// folder grant with a file word stays for its subfolders, and a folder grant
/// above a nested repository stays.
/// </summary>
public sealed class ToolApprovalHygieneDoctorCheckTests : IDisposable
{
    private readonly string _root = Directory.CreateDirectory(Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        $"netclaw-doctor-hygiene-{Guid.NewGuid():N}")).FullName;

    public static bool IsPosix => !OperatingSystem.IsWindows();

    [SlopwatchSuppress("SW001", "The store uses POSIX folders.")]
    [Fact(SkipUnless = nameof(IsPosix), Skip = "The store uses POSIX folders.")]
    public async Task Fix_removes_only_grants_that_add_nothing()
    {
        var phobos = Directory.CreateDirectory(Path.Combine(_root, "phobos")).FullName;
        var tools = Directory.CreateDirectory(Path.Combine(_root, "tools")).FullName;
        // A folder grant whose word names a folder entry still covers a subfolder.
        var node = Directory.CreateDirectory(Path.Combine(_root, "node")).FullName;
        Directory.CreateDirectory(Path.Combine(node, "test"));
        var package = Directory.CreateDirectory(Path.Combine(node, "packages", "a")).FullName;
        var repository = Directory.CreateDirectory(Path.Combine(_root, "repo")).FullName;
        var sub = Directory.CreateDirectory(Path.Combine(repository, "sub")).FullName;
        var nested = Directory.CreateDirectory(Path.Combine(sub, "nested")).FullName;
        RunGit(repository, "init", "-q");
        RunGit(nested, "init", "-q");
        File.WriteAllText(Path.Combine(phobos, "Phobos.slnx"), "solution");
        File.WriteAllText(Path.Combine(tools, "build"), "#!/bin/sh");
        var missing = Path.Combine(_root, "deleted-worktree");
        var paths = new NetclawPaths(Path.Combine(_root, "netclaw"));
        Directory.CreateDirectory(paths.ConfigDirectory);
        WriteStore(paths,
        [
            Grant(["dotnet", "build"], null),
            Grant(["dotnet", "build", "Phobos.slnx"], null),
            Grant(["dotnet", "build", "Phobos.slnx"], phobos),
            Grant(["dotnet", "test"], phobos),
            Grant(["dotnet", "test"], null),
            Legacy("git push", null),
            Grant(["git", "push"], null),
            Grant(["git", "push"], phobos),
            Grant(["npm", "run", "test"], node),
            Grant(["npm", "run", "build"], null),
            Grant(["ls"], tools),
            Repository(["git", "status"], Path.Combine(repository, ".git")),
            Grant(["git", "status"], sub),
            Grant(["docker", "compose"], missing),
            Grant(["git", "fetch", "upstream", "dev", "master"], null),
            // A verb grant covers a longer grant. A program-only grant does not.
            Grant(["git", "push", "upstream", "feature-x"], null),
            Legacy("git push upstream", phobos),
            Grant(["gh"], null),
            Grant(["gh", "auth", "status"], null),
        ]);
        (string Command, string Directory)[] calls =
        [
            ("dotnet build Phobos.slnx -c Release", phobos),
            ("dotnet test", phobos),
            ("git push", phobos),
            ("npm run build", phobos),
            ("npm run test", package),
            ("ls", tools),
            ("git status", repository),
            ("git status", nested),
            ("git fetch upstream dev master", phobos),
            ("git push upstream feature-x", phobos),
            ("gh --help", phobos),
            ("gh auth status", phobos),
        ];
        var before = Load(paths);
        var allowedBefore = calls.Where(call => Allowed(before, call.Command, call.Directory)).ToArray();

        var check = await new ToolApprovalHygieneDoctorCheck(paths).RunAsync(TestContext.Current.CancellationToken);
        var fixService = new DoctorFixService(paths);
        var plan = await fixService.BuildPlanAsync(TestContext.Current.CancellationToken);
        await fixService.ApplyAsync(plan, TestContext.Current.CancellationToken);

        Assert.Equal(DoctorSeverity.Warning, check.Severity);
        var after = Load(paths);
        // Only covered grants go. A verb grant covers each grant whose words
        // start with its words: "dotnet build" covers "dotnet build Phobos.slnx",
        // and "git push" covers "git push upstream feature-x". A program-only
        // grant covers no longer grant, so "gh auth status" stays. A folder grant
        // with a file word stays: a subfolder uses it. A repository grant never
        // covers a folder grant: the nested repository needs the folder grant.
        // Of two equal grants, the canonical token-prefix grant stays and the
        // legacy phrase goes.
        Assert.Equal(
            [
                "TokenPrefix dotnet build anywhere",
                "TokenPrefix dotnet test anywhere",
                "TokenPrefix git push anywhere",
                $"TokenPrefix npm run test in {node}",
                "TokenPrefix npm run build anywhere",
                $"TokenPrefix ls in {tools}",
                $"TokenPrefix git status in repository {Path.Combine(repository, ".git")}",
                $"TokenPrefix git status in {sub}",
                $"TokenPrefix docker compose in {missing}",
                "TokenPrefix git fetch upstream dev master anywhere",
                "TokenPrefix gh anywhere",
                "TokenPrefix gh auth status anywhere",
            ],
            after.Select(static entry => $"{entry.Match} {entry.Verb} {(entry.Repository is { } common ? $"in repository {common}" : entry.Directory is { } directory ? $"in {directory}" : "anywhere")}"));
        Assert.True(
            calls.Length == allowedBefore.Length,
            "Allowed before: " + string.Join("; ", allowedBefore.Select(static call => $"{call.Command} in {call.Directory}")));
        Assert.All(allowedBefore, call => Assert.True(
            Allowed(after, call.Command, call.Directory),
            $"'{call.Command}' in {call.Directory} was allowed before the fix."));
        var recheck = await new ToolApprovalHygieneDoctorCheck(paths).RunAsync(TestContext.Current.CancellationToken);
        Assert.Contains("the folder does not exist; kept", recheck.Message, StringComparison.Ordinal);
        Assert.Contains("npm run test", recheck.Message, StringComparison.Ordinal);
    }

    private static void RunGit(string directory, params string[] arguments)
    {
        using var process = new System.Diagnostics.Process
        {
            StartInfo = new System.Diagnostics.ProcessStartInfo("git")
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

    // A call is allowed when a stored grant covers each of its candidates.
    private static bool Allowed(IReadOnlyList<ApprovalEntry> entries, string command, string directory)
    {
        var candidates = new ShellApprovalMatcher().ExtractCandidates(
            new ToolName(ShellTool.ToolName),
            new Dictionary<string, object?> { ["Command"] = command, ["WorkingDirectory"] = directory });
        return candidates.Count > 0
               && candidates.All(candidate => ApprovalPatternMatching.MatchesShellApproval(candidate, directory, entries));
    }

    private static IReadOnlyList<ApprovalEntry> Load(NetclawPaths paths)
        => ToolApprovalHygieneDoctorCheck.CreateStore(paths).GetApprovedEntries(TrustAudience.Personal, ShellTool.ToolName);

    private static Dictionary<string, object?> Grant(string[] words, string? directory)
        => new()
        {
            ["shell"] = "Bash",
            ["match"] = "TokenPrefix",
            ["verbTokens"] = words,
            ["directory"] = directory,
            ["createdAt"] = "2026-10-04T14:47:45+00:00",
        };

    private static Dictionary<string, object?> Repository(string[] words, string commonDirectory)
        => new()
        {
            ["shell"] = "Bash",
            ["match"] = "TokenPrefix",
            ["verbTokens"] = words,
            ["directory"] = null,
            ["repository"] = commonDirectory,
            ["createdAt"] = "2026-10-04T14:47:45+00:00",
        };

    private static Dictionary<string, object?> Legacy(string verb, string? directory)
        => new() { ["shell"] = "Bash", ["match"] = "LegacyExact", ["verb"] = verb, ["directory"] = directory, ["createdAt"] = null };

    private static void WriteStore(NetclawPaths paths, IReadOnlyList<Dictionary<string, object?>> grants)
        => File.WriteAllText(paths.ToolApprovalsPath, JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["version"] = 3,
            ["audiences"] = new Dictionary<string, object?>
            {
                ["personal"] = new Dictionary<string, object?> { [ShellTool.ToolName] = grants }
            }
        }));

    public void Dispose() => Directory.Delete(_root, recursive: true);
}
