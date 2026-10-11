// -----------------------------------------------------------------------
// <copyright file="ShellLaunchEnvironmentSnapshotTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Xunit;

namespace Netclaw.Security.Tests;

/// <summary>
/// Owner decision F3: the parser gets the names of the one environment snapshot
/// that the launcher copies to each shell process, so the two cannot drift.
/// </summary>
public sealed class ShellLaunchEnvironmentSnapshotTests
{
    private static readonly KeyValuePair<string, string>[] DaemonEnvironment =
    [
        new("PATH", "/usr/bin"),
        new("GIT_DIR", "/work/.git"),
        new("BASH_ENV", "/work/startup.sh"),
        new("SHELLOPTS", "allexport"),
        new("BASH_FUNC_git%%", "() { :; }"),
        new("LD_PRELOAD", "/work/x.so"),
    ];

    [Fact]
    public void Parser_names_hold_each_name_that_the_process_receives()
    {
        var environment = ShellExecutionEnvironment.CreateBash(ShellPlatform.Linux, new Version(5, 2), DaemonEnvironment);

        var names = environment.CreateLaunchEnvironment(temporary: null).CompleteEnvironmentNames;
        var process = environment.CreateProcessStartInfo("true").Environment;

        Assert.NotNull(names);
        Assert.Subset(names.ToHashSet(StringComparer.Ordinal), process.Keys.ToHashSet(StringComparer.Ordinal));
        Assert.Contains("GIT_DIR", names);
        Assert.Contains("PATH", process.Keys);
        // The launcher sets these names on each process after the start data.
        Assert.Contains("PWD", names);
        Assert.Contains("TMPDIR", names);
    }

    // SECURITY: a startup override in the declared names would make every
    // assignment count again. The process never receives one, so the names
    // must not hold it.
    [Fact]
    public void Removed_startup_overrides_are_in_neither_set()
    {
        var environment = ShellExecutionEnvironment.CreateBash(ShellPlatform.Linux, new Version(5, 2), DaemonEnvironment);

        var names = environment.CreateLaunchEnvironment(temporary: null).CompleteEnvironmentNames!;
        var process = environment.CreateProcessStartInfo("true").Environment;

        foreach (var removed in new[] { "BASH_ENV", "SHELLOPTS", "BASH_FUNC_git%%", "LD_PRELOAD" })
        {
            Assert.DoesNotContain(removed, names);
            Assert.False(process.ContainsKey(removed), removed);
        }
    }

    // A later change to the daemon environment reaches neither the process nor the parser.
    [Fact]
    public void Daemon_environment_change_after_creation_reaches_no_process()
    {
        var key = "NETCLAW_SNAPSHOT_TEST_" + Guid.NewGuid().ToString("N");
        var environment = ShellExecutionEnvironment.CreateBash(ShellPlatform.Linux, new Version(5, 2));
        try
        {
            Environment.SetEnvironmentVariable(key, "later");

            Assert.False(environment.CreateProcessStartInfo("true").Environment.ContainsKey(key));
            Assert.DoesNotContain(key, environment.CreateLaunchEnvironment(temporary: null).CompleteEnvironmentNames!);
        }
        finally
        {
            Environment.SetEnvironmentVariable(key, null);
        }
    }

    [Fact]
    public void PowerShell_declares_no_names()
    {
        var environment = ShellExecutionEnvironment.CreatePowerShell(
            @"C:\Program Files\PowerShell\7\pwsh.exe",
            ShellSyntaxTree.PwshDialect.PowerShell7);

        Assert.Null(environment.CreateLaunchEnvironment(temporary: null).CompleteEnvironmentNames);
    }
}
