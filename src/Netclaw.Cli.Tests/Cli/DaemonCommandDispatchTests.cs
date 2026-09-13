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
    [InlineData("pair")]
    [InlineData("devices")]
    [InlineData("help")]
    public void ShouldShowHelpInsteadOfExecuting_false_for_verbs_with_their_own_help_handling(string verb)
    {
        // `pair`/`devices` guard their own trailing --help inline in Program.cs, and "help"
        // itself is normalized away before this check runs — none should be double-guarded here.
        Assert.False(DaemonCommandDispatch.ShouldShowHelpInsteadOfExecuting(verb, ["daemon", verb, "--help"]));
    }
}
