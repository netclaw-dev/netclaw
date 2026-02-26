using Microsoft.Extensions.Options;
using Netclaw.Channels;
using Netclaw.Channels.Slack;
using Netclaw.Daemon.Configuration;
using Netclaw.Daemon.Gateway;
using Xunit;

namespace Netclaw.Daemon.Tests.Gateway;

public sealed class DaemonRuntimeStatusServiceTests
{
    [Fact]
    public async Task IncludesSlackConnectorAsDisabled_WhenNotEnabled()
    {
        var service = new DaemonRuntimeStatusService(
            TimeProvider.System,
            channels: Array.Empty<IChannel>(),
            slackOptions: new SlackChannelOptions { Enabled = false },
            persistenceOptions: new DaemonPersistenceOptions(),
            telemetryOptions: Options.Create(new TelemetryOptions()));

        var status = await service.GetStatusAsync();
        var slack = status.Connectors.Single(c => c.Key == "slack");

        Assert.False(slack.Enabled);
        Assert.Equal("disabled", slack.Status);
    }

    [Fact]
    public async Task ReportsSlackAsDisconnected_WhenEnabledButMissingRuntimeChannel()
    {
        var service = new DaemonRuntimeStatusService(
            TimeProvider.System,
            channels: Array.Empty<IChannel>(),
            slackOptions: new SlackChannelOptions { Enabled = true, AllowedChannelIds = ["C1"] },
            persistenceOptions: new DaemonPersistenceOptions(),
            telemetryOptions: Options.Create(new TelemetryOptions()));

        var status = await service.GetStatusAsync();
        var slack = status.Connectors.Single(c => c.Key == "slack");

        Assert.True(slack.Enabled);
        Assert.Equal("disconnected", slack.Status);
    }
}
