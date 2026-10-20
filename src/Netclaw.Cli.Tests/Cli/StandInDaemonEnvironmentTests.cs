// -----------------------------------------------------------------------
// <copyright file="StandInDaemonEnvironmentTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Microsoft.Extensions.Time.Testing;
using Netclaw.Tests.Utilities;
using Xunit;

namespace Netclaw.Cli.Tests.Cli;

public sealed class StandInDaemonEnvironmentTests
{
    [Theory]
    [InlineData("HOME=/tmp/profile\0")]
    [InlineData("NETCLAW_HOME=../relative\0HOME=/tmp/profile\0")]
    [InlineData("NETCLAW_HOME=~/.netclaw\0HOME=/tmp/profile\0")]
    [InlineData("NETCLAW_HOME=/tmp/test-home\0HOME=/tmp/profile\0")]
    public void Wait_requires_the_complete_environment_without_home_policy(string environment)
    {
        var expected = environment.Split('\0', StringSplitOptions.RemoveEmptyEntries).ToHashSet(StringComparer.Ordinal);
        var reads = 0;
        var yields = 0;
        var clock = new FakeTimeProvider();

        StandInDaemon.WaitForProcessEnvironment(
            expected,
            () => ++reads switch { 1 => "", 2 => "UNRELATED=value\0", _ => environment },
            () => false,
            clock,
            () => { yields++; clock.Advance(TimeSpan.FromSeconds(1)); });

        Assert.Equal(3, reads);
        Assert.Equal(2, yields);
    }

    [Theory]
    [InlineData("NETCLAW_HOME=/tmp/other\0HOME=/tmp/profile\0")]
    [InlineData("NETCLAW_HOME=/tmp/expected\0")]
    [InlineData("NETCLAW_HOME=/tmp/expected\0HOME=/tmp/profile\0EXTRA=value\0")]
    public void Wait_rejects_a_different_environment_at_the_deadline(string environment)
    {
        var clock = new FakeTimeProvider();
        var yields = 0;

        Assert.Throws<TimeoutException>(() => StandInDaemon.WaitForProcessEnvironment(
            new HashSet<string>(StringComparer.Ordinal) { "NETCLAW_HOME=/tmp/expected", "HOME=/tmp/profile" },
            () => environment,
            () => false,
            clock,
            () => { yields++; clock.Advance(TimeSpan.FromSeconds(10)); }));

        Assert.Equal(1, yields);
    }

    [Fact]
    public void Wait_rejects_a_child_that_exits_before_its_environment_is_available()
    {
        var exited = false;
        var clock = new FakeTimeProvider();

        Assert.Throws<InvalidOperationException>(() => StandInDaemon.WaitForProcessEnvironment(
            new HashSet<string>(StringComparer.Ordinal) { "HOME=/tmp/profile" },
            () => "",
            () => exited,
            clock,
            () => exited = true));
    }
}
