// -----------------------------------------------------------------------
// <copyright file="TelemetryServiceNameTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Daemon.Configuration;
using Xunit;

namespace Netclaw.Daemon.Tests.Configuration;

public sealed class TelemetryServiceNameTests
{
    [Fact]
    public void ExplicitServiceName_WinsOverEnvironment()
    {
        var telemetry = new TelemetryOptions { ServiceName = "netclaw-supervisor" };

        var resolved = TelemetryRegistrationExtensions.ResolveServiceName(
            telemetry, otelServiceNameEnv: "from-env");

        Assert.Equal("netclaw-supervisor", resolved);
    }

    [Fact]
    public void NoConfig_FallsBackToOtelServiceNameEnvironmentVariable()
    {
        var telemetry = new TelemetryOptions();

        var resolved = TelemetryRegistrationExtensions.ResolveServiceName(
            telemetry, otelServiceNameEnv: "netclaw-discord-qa");

        Assert.Equal("netclaw-discord-qa", resolved);
    }

    [Fact]
    public void NoConfigAndNoEnvironment_FallsBackToDefault()
    {
        var telemetry = new TelemetryOptions();

        var resolved = TelemetryRegistrationExtensions.ResolveServiceName(
            telemetry, otelServiceNameEnv: null);

        Assert.Equal(TelemetryRegistrationExtensions.DefaultServiceName, resolved);
    }

    [Fact]
    public void WhitespaceValues_AreTreatedAsUnset()
    {
        var telemetry = new TelemetryOptions { ServiceName = "   " };

        var resolved = TelemetryRegistrationExtensions.ResolveServiceName(
            telemetry, otelServiceNameEnv: "   ");

        Assert.Equal("netclawd", resolved);
    }
}
