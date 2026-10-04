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
/// <c>netclaw doctor --fix</c> on a messy store shaped like real stores: grants
/// that name a solution file, and folder grants that an "anywhere" grant covers.
/// Only meaningful grants remain, and each call that the old store allowed is
/// still allowed.
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
        var phobos = Directory.CreateDirectory(Path.Combine(_root, "phobos", "src")).Parent!.FullName;
        var cmd = Directory.CreateDirectory(Path.Combine(_root, "petabridge.cmd")).FullName;
        File.WriteAllText(Path.Combine(phobos, "Phobos.slnx"), "solution");
        File.WriteAllText(Path.Combine(cmd, "Petabridge.Cmd.sln"), "solution");
        var missing = Path.Combine(_root, "deleted-worktree");
        var paths = new NetclawPaths(Path.Combine(_root, "netclaw"));
        Directory.CreateDirectory(paths.ConfigDirectory);
        WriteStore(paths,
        [
            Grant(["dotnet", "build"], null),
            Grant(["dotnet", "build", "Phobos.slnx"], null),
            Grant(["dotnet", "list", "Phobos.slnx", "package"], null),
            Grant(["dotnet", "build", "Petabridge.Cmd.sln"], null),
            Grant(["dotnet", "test"], phobos),
            Grant(["dotnet", "test"], null),
            Legacy("git push", null),
            Grant(["git", "push"], phobos),
            Grant(["npm", "run", "build"], Path.Combine(phobos, "src")),
            Grant(["npm", "run", "build"], phobos),
            Grant(["git", "status"], cmd),
            Grant(["docker", "compose"], missing),
            Grant(["git", "fetch", "upstream", "dev", "master"], null),
        ]);
        (string Command, string Directory)[] calls =
        [
            ("dotnet build Phobos.slnx -c Release", phobos),
            ("dotnet build Petabridge.Cmd.sln", cmd),
            ("dotnet list Phobos.slnx package --vulnerable", phobos),
            ("dotnet test", phobos),
            ("git push", phobos),
            ("npm run build", Path.Combine(phobos, "src")),
            ("npm run build --if-present", phobos),
            ("git status", cmd),
            ("git fetch upstream dev master", cmd),
        ];
        var before = Load(paths);
        var allowedBefore = calls.Where(call => Allowed(before, call.Command, call.Directory)).ToArray();

        var check = await new ToolApprovalHygieneDoctorCheck(paths).RunAsync(TestContext.Current.CancellationToken);
        var fixService = new DoctorFixService(paths);
        var plan = await fixService.BuildPlanAsync(TestContext.Current.CancellationToken);
        await fixService.ApplyAsync(plan, TestContext.Current.CancellationToken);

        Assert.Equal(DoctorSeverity.Warning, check.Severity);
        var after = Load(paths);
        Assert.Equal(
            [
                "dotnet build anywhere",
                "dotnet test anywhere",
                "git push anywhere",
                $"npm run build in {phobos}",
                $"git status in {cmd}",
                $"docker compose in {missing}",
                "git fetch upstream dev master anywhere",
            ],
            after.Select(static entry => $"{entry.Verb} {(entry.Directory is { } directory ? $"in {directory}" : "anywhere")}"));
        Assert.True(
            calls.Length - 1 == allowedBefore.Length,
            "Allowed before: " + string.Join("; ", allowedBefore.Select(static call => call.Command)));
        Assert.All(allowedBefore, call => Assert.True(
            Allowed(after, call.Command, call.Directory),
            $"'{call.Command}' in {call.Directory} was allowed before the fix."));
        var recheck = await new ToolApprovalHygieneDoctorCheck(paths).RunAsync(TestContext.Current.CancellationToken);
        Assert.Contains("the folder does not exist; kept", recheck.Message, StringComparison.Ordinal);
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
