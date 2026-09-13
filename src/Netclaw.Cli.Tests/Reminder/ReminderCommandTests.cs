// -----------------------------------------------------------------------
// <copyright file="ReminderCommandTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Cli.Reminder;
using Xunit;

namespace Netclaw.Cli.Tests.Reminder;

/// <summary>
/// Covers the missed-help pattern audited alongside the canary-reported
/// <c>netclaw memory backfill-embeddings --help</c> bug: none of
/// <see cref="ReminderCommand"/>'s subcommand handlers had their own <c>--help</c>
/// check, so a trailing help token was silently ignored and the subcommand ran for
/// real. <c>list</c> is the sharpest example — it takes no positional arguments at
/// all, so `reminder list --help` used to reach the live daemon instead of printing
/// help. All tests pass <c>daemonApi: null</c> to prove the help check short-circuits
/// before the "requires a running daemon" branch is ever reached.
/// </summary>
public sealed class ReminderCommandTests
{
    [Theory]
    [InlineData("--help")]
    [InlineData("-h")]
    [InlineData("help")]
    public async Task List_TrailingHelpFlag_PrintsHelp_WithoutRequiringDaemon(string helpToken)
    {
        var (exitCode, stdout) = await RunCapturedAsync(["reminder", "list", helpToken]);

        Assert.Equal(0, exitCode);
        Assert.Contains("Usage: netclaw reminder <subcommand>", stdout);
        Assert.DoesNotContain("requires a running daemon", stdout);
    }

    [Fact]
    public async Task List_WithoutHelpFlag_StillRequiresDaemon()
    {
        // Regression guard: the new trailing-help scan must not swallow ordinary
        // subcommand invocations that legitimately need the daemon.
        var (exitCode, _, stderr) = await RunCapturedWithStderrAsync(["reminder", "list"]);

        Assert.Equal(1, exitCode);
        Assert.Contains("requires a running daemon", stderr);
    }

    [Fact]
    public async Task Create_TrailingHelpFlag_AfterFullArgs_PrintsHelp_WithoutRequiringDaemon()
    {
        var (exitCode, stdout) = await RunCapturedAsync(
            ["reminder", "create", "id", "once", "30m", "do it", "--help"]);

        Assert.Equal(0, exitCode);
        Assert.Contains("Usage: netclaw reminder <subcommand>", stdout);
    }

    [Fact]
    public async Task TopLevelHelp_StillPrintsHelp()
    {
        var (exitCode, stdout) = await RunCapturedAsync(["reminder", "--help"]);

        Assert.Equal(0, exitCode);
        Assert.Contains("Usage: netclaw reminder <subcommand>", stdout);
    }

    private static async Task<(int ExitCode, string Stdout)> RunCapturedAsync(string[] args)
    {
        var (exitCode, stdout, _) = await RunCapturedWithStderrAsync(args);
        return (exitCode, stdout);
    }

    private static async Task<(int ExitCode, string Stdout, string Stderr)> RunCapturedWithStderrAsync(string[] args)
    {
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();
        var exitCode = await ReminderCommand.RunAsync(args, daemonApi: null, output: stdout, error: stderr);
        return (exitCode, stdout.ToString(), stderr.ToString());
    }
}
