// -----------------------------------------------------------------------
// <copyright file="DaemonShutdownConfigurationTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Akka.Configuration;
using Netclaw.Configuration;
using Xunit;

namespace Netclaw.Daemon.Tests;

/// <summary>
/// Tests for <see cref="DaemonShutdownConfiguration.BuildCoordinatedShutdownHocon"/>, which
/// replaced the inline HOCON literal Program.cs used to prepend a hardcoded 200s
/// before-service-unbind phase timeout (netclaw-dev/netclaw#1664, #1665). Proves the emitted
/// HOCON tracks whatever <see cref="DaemonConfig.GracefulShutdownBudget"/> resolves to instead
/// of drifting back to a literal that could disagree with the daemon-stop drain's own bound.
/// </summary>
public sealed class DaemonShutdownConfigurationTests
{
    [Theory]
    [InlineData(200, 200)]
    [InlineData(90, 90)]
    public void BuildCoordinatedShutdownHocon_interpolates_the_given_budget_in_seconds(
        int budgetSeconds, int expectedPhaseTimeoutSeconds)
    {
        var hocon = DaemonShutdownConfiguration.BuildCoordinatedShutdownHocon(TimeSpan.FromSeconds(budgetSeconds));

        var config = ConfigurationFactory.ParseString(hocon);

        Assert.Equal(TimeSpan.FromSeconds(expectedPhaseTimeoutSeconds),
            config.GetTimeSpan("akka.coordinated-shutdown.phases.before-service-unbind.timeout"));
        Assert.False(config.GetBoolean("akka.coordinated-shutdown.exit-clr"));
    }

    [Fact]
    public void BuildCoordinatedShutdownHocon_tracks_DaemonConfig_GracefulShutdownBudget()
    {
        var hocon = DaemonShutdownConfiguration.BuildCoordinatedShutdownHocon(DaemonConfig.GracefulShutdownBudget);

        var config = ConfigurationFactory.ParseString(hocon);

        Assert.Equal(
            DaemonConfig.GracefulShutdownBudget,
            config.GetTimeSpan("akka.coordinated-shutdown.phases.before-service-unbind.timeout"));
    }
}
