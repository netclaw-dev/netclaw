// -----------------------------------------------------------------------
// <copyright file="DaemonCommandDispatchTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Cli.Daemon;
using Xunit;

namespace Netclaw.Cli.Tests.Cli;

/// <summary>
/// Regression coverage for the canary finding that <c>netclaw daemon stop --help</c> (and
/// start/status/install/uninstall) executed the real lifecycle action instead of printing
/// help, because Program.cs's daemon dispatch only checked the subcommand slot (args[1]) for
/// a help token. Program.cs is top-level statements, so the decision is extracted into
/// <see cref="DaemonCommandDispatch"/> to make it independently unit-testable — mirroring
/// <c>DaemonCliArgs</c>'s <c>netclawd --version</c> extraction for the same reason.
/// </summary>
public sealed class DaemonCommandDispatchTests
{
    [Theory]
    [InlineData("start")]
    [InlineData("stop")]
    [InlineData("status")]
    [InlineData("install")]
    [InlineData("uninstall")]
    public void ShouldShowHelpInsteadOfExecuting_true_for_lifecycle_verb_with_trailing_help(string verb)
    {
        Assert.True(DaemonCommandDispatch.ShouldShowHelpInsteadOfExecuting(verb, ["daemon", verb, "--help"]));
    }

    [Theory]
    [InlineData("start")]
    [InlineData("stop")]
    [InlineData("status")]
    [InlineData("install")]
    [InlineData("uninstall")]
    public void ShouldShowHelpInsteadOfExecuting_false_for_lifecycle_verb_without_help(string verb)
    {
        Assert.False(DaemonCommandDispatch.ShouldShowHelpInsteadOfExecuting(verb, ["daemon", verb]));
    }

    [Theory]
    [InlineData("daemon", "devices", "help")]
    [InlineData("daemon", "devices", "--help")]
    [InlineData("daemon", "devices", "list", "-h")]
    [InlineData("daemon", "devices", "revoke", "--help")]
    [InlineData("daemon", "devices", "revoke", "laptop", "-h")]
    public void ShouldShowDevicesHelp_true_for_subcommand_help_and_trailing_flags(params string[] args)
    {
        Assert.True(DaemonCommandDispatch.ShouldShowDevicesHelp(args));
    }

    [Theory]
    [InlineData("daemon", "devices")]
    [InlineData("daemon", "devices", "list")]
    [InlineData("daemon", "devices", "revoke", "laptop")]
    [InlineData("daemon", "devices", "revoke", "help")]
    public void ShouldShowDevicesHelp_false_when_a_device_is_called_help(params string[] args)
    {
        Assert.False(DaemonCommandDispatch.ShouldShowDevicesHelp(args));
    }

    [Theory]
    [InlineData("pair")]
    [InlineData("devices")]
    [InlineData("help")]
    public void ShouldShowHelpInsteadOfExecuting_false_for_verbs_with_their_own_help_handling(string verb)
    {
        // `pair`/`devices` guard their own trailing --help inline in Program.cs, and "help"
        // itself is normalized away before this check runs — none should be double-guarded here.
        Assert.False(DaemonCommandDispatch.ShouldShowHelpInsteadOfExecuting(verb, ["daemon", verb, "--help"]));
    }

    [Fact]
    public void TryParseInstallArguments_accepts_no_arguments()
    {
        Assert.True(DaemonCommandDispatch.TryParseInstallArguments(["daemon", "install"], out var entries, out var error), error);
        Assert.Empty(entries);
    }

    [Theory]
    [InlineData(new[] { "daemon", "install", "--env", "NETCLAW_A=1" }, new[] { "NETCLAW_A=1" })]
    [InlineData(new[] { "daemon", "install", "--env", "NETCLAW_A=1", "--env", "NETCLAW_B=2" }, new[] { "NETCLAW_A=1", "NETCLAW_B=2" })]
    public void TryParseInstallArguments_collects_each_env_flag(string[] args, string[] expected)
    {
        Assert.True(DaemonCommandDispatch.TryParseInstallArguments(args, out var entries, out var error), error);
        Assert.Equal(expected, entries);
    }

    [Theory]
    [InlineData("daemon", "install", "--env")]
    [InlineData("daemon", "install", "--env", "NETCLAW_A=1", "--env")]
    [InlineData("daemon", "install", "NETCLAW_A=1")]
    [InlineData("daemon", "install", "--envs", "NETCLAW_A=1")]
    public void TryParseInstallArguments_refuses_a_flag_with_no_value_and_unknown_arguments(params string[] args)
    {
        Assert.False(DaemonCommandDispatch.TryParseInstallArguments(args, out _, out var error));
        Assert.NotEmpty(error);
    }
}
