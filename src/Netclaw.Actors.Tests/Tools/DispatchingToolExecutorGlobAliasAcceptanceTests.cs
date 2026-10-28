// -----------------------------------------------------------------------
// <copyright file="DispatchingToolExecutorGlobAliasAcceptanceTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Diagnostics;
using Netclaw.Actors.Tools;
using Netclaw.Configuration;
using Netclaw.Security;
using Netclaw.Security.Authorization.Filesystem;
using Netclaw.Tests.Utilities;
using Netclaw.Tools;
using Xunit;

namespace Netclaw.Actors.Tests.Tools;

public partial class DispatchingToolExecutorTests
{
    public static bool SupportsGlobAliasLaunch => OperatingSystem.IsLinux();

    [SlopwatchSuppress("SW001", "This native launch requires the Linux Bash host and POSIX directory aliases.")]
    [Fact(SkipUnless = nameof(SupportsGlobAliasLaunch), Skip = "This native launch requires Linux Bash.")]
    public async Task Glob_alias_diagnostic_runs_with_private_absolute_paths_and_the_captured_home()
    {
        using var directory = new DisposableTempDir();
        FileSystemAuthority.TryResolveLinks(directory.Path, out var home);
        var repositories = Directory.CreateDirectory(Path.Combine(home, "repositories")).FullName;
        var target = Directory.CreateDirectory(Path.Combine(repositories, "owner", "project")).FullName;
        Directory.CreateDirectory(Path.Combine(target, "leaf"));
        var alias = Path.Combine(repositories, "alias");
        Directory.CreateSymbolicLink(alias, target);
        var environment = await GlobAliasEnvironmentAsync();
        var (registry, policy) = CreateApprovalGatedShellRegistryAndPolicy(environment,
            safeVerbs: SafeVerbList.FromVerbs(ApprovalShell.Bash, ["ls", "head"]));
        var executor = new DispatchingToolExecutor(registry, policy, GrantEveryShellCandidate());
        var context = TestToolExecutionContext.CreateBound("launch/glob-alias", home,
            new TestToolExecutionContextOptions { Audience = TrustAudience.Personal, Boundary = TrustBoundary.Personal });
        var quotedRepositories = "'" + repositories.Replace("'", "'\"'\"'", StringComparison.Ordinal) + "'";
        var command = $"ls -d {quotedRepositories}/*/ {quotedRepositories}/*/*/ 2>/dev/null | head -60";
        var call = CreateToolCall("launch-glob-alias", ShellTool.ToolName, ToolInput.Create("Command", command));

        var launch = await executor.PrepareShellLaunchAsync(call, context, TestContext.Current.CancellationToken);
        using var process = await launch.StartAsync(TestContext.Current.CancellationToken);
        process.StandardInput.Close();
        var output = await process.StandardOutput.ReadToEndAsync(TestContext.Current.CancellationToken);
        var error = await process.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);
        await process.WaitForExitAsync(TestContext.Current.CancellationToken);

        Assert.Equal(environment.HomeDirectory, process.StartInfo.Environment["HOME"]);
        Assert.Contains(command, process.StartInfo.ArgumentList);
        Assert.Equal(0, process.ExitCode);
        Assert.Equal(string.Empty, error);
        Assert.Contains(alias + "/", output, StringComparison.Ordinal);
        Assert.Contains(Path.Combine(alias, "leaf") + "/", output, StringComparison.Ordinal);
        TestContext.Current.TestOutputHelper?.WriteLine($"Command: {command}\nHOME: {environment.HomeDirectory}\nExit: {process.ExitCode}\n{output}");
    }

    [SlopwatchSuppress("SW001", "This native launch requires the Linux Bash host and POSIX directory aliases.")]
    [Fact(SkipUnless = nameof(SupportsGlobAliasLaunch), Skip = "This native launch requires Linux Bash.")]
    public async Task Glob_alias_target_change_during_the_grant_await_stops_the_process()
    {
        using var directory = new DisposableTempDir();
        FileSystemAuthority.TryResolveLinks(directory.Path, out var home);
        var cover = Directory.CreateDirectory(Path.Combine(home, "cover")).FullName;
        var first = Directory.CreateDirectory(Path.Combine(cover, ".first")).FullName;
        var second = Directory.CreateDirectory(Path.Combine(cover, ".second")).FullName;
        var firstFile = Path.Combine(first, "item.txt");
        var secondFile = Path.Combine(second, "item.txt");
        await File.WriteAllTextAsync(firstFile, "first", TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(secondFile, "second", TestContext.Current.CancellationToken);
        var originalTime = new DateTimeOffset(2000, 1, 1, 0, 0, 0, TimeSpan.Zero).UtcDateTime;
        File.SetLastWriteTimeUtc(firstFile, originalTime);
        File.SetLastWriteTimeUtc(secondFile, originalTime);
        var alias = Path.Combine(cover, "alias");
        Directory.CreateSymbolicLink(alias, first);
        var environment = await GlobAliasEnvironmentAsync();
        var (registry, policy) = CreateApprovalGatedShellRegistryAndPolicy(environment);
        var checks = 0;
        var service = new FixedShellApprovalService(request =>
        {
            if (++checks == 2)
            {
                Directory.Delete(alias);
                Directory.CreateSymbolicLink(alias, second);
            }
            return LaunchGrantResult(request, true);
        });
        var executor = new DispatchingToolExecutor(registry, policy, service);
        var context = TestToolExecutionContext.CreateBound("launch/glob-alias-causal", home,
            new TestToolExecutionContextOptions { Audience = TrustAudience.Personal, Boundary = TrustBoundary.Personal });
        var command = $"touch '{cover}'/a*/*.txt";
        var call = CreateToolCall("launch-glob-alias-causal", ShellTool.ToolName, ToolInput.Create("Command", command));
        var launch = await executor.PrepareShellLaunchAsync(call, context, TestContext.Current.CancellationToken);

        var failure = await Assert.ThrowsAsync<ToolAccessDeniedException>(async () =>
        {
            using var process = await launch.StartAsync(TestContext.Current.CancellationToken);
            process.StandardInput.Close();
            await process.WaitForExitAsync(TestContext.Current.CancellationToken);
        });

        Assert.Equal("shell_launch_paths_changed", failure.DenyReason);
        Assert.Equal(2, checks);
        Assert.Equal(originalTime, File.GetLastWriteTimeUtc(firstFile));
        Assert.Equal(originalTime, File.GetLastWriteTimeUtc(secondFile));
    }

    private static async Task<ShellExecutionEnvironment> GlobAliasEnvironmentAsync()
    {
        using var versionProcess = new Process
        {
            StartInfo = new ProcessStartInfo("/bin/bash")
            {
                ArgumentList = { "--version" },
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            }
        };
        Assert.True(versionProcess.Start());
        var versionText = await versionProcess.StandardOutput.ReadToEndAsync(TestContext.Current.CancellationToken);
        await versionProcess.WaitForExitAsync(TestContext.Current.CancellationToken);
        Assert.Equal(0, versionProcess.ExitCode);
        var versionWord = versionText.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .First(word => char.IsAsciiDigit(word[0]));
        var version = Version.Parse(versionWord.Split('(')[0]);
        Assert.True(version >= new Version(5, 2), versionText);
        TestContext.Current.TestOutputHelper?.WriteLine(versionText);
        return ShellExecutionEnvironment.CreateBash(ShellPlatform.Linux, version,
            new Dictionary<string, string> { ["PATH"] = "/usr/bin:/bin" });
    }
}
