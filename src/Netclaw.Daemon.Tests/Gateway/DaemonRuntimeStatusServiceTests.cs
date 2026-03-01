using Microsoft.Extensions.Options;
using Netclaw.Channels;
using Netclaw.Channels.Slack;
using Netclaw.Configuration;
using Netclaw.Daemon.Configuration;
using Netclaw.Daemon.Gateway;
using Xunit;

namespace Netclaw.Daemon.Tests.Gateway;

public sealed class DaemonRuntimeStatusServiceTests
{
    private static readonly SessionConfig DefaultSessionConfig = new()
    {
        ModelId = "test-model",
        InputModalities = ModelModality.Text | ModelModality.Image,
        OutputModalities = ModelModality.Text,
        ContextWindowTokens = 32_768
    };

    private static readonly ModelSelection DefaultModelSelection = new()
    {
        Main = new ModelReference { Provider = "test-provider", ModelId = "test-model" }
    };

    [Fact]
    public async Task IncludesSlackConnectorAsDisabled_WhenNotEnabled()
    {
        var service = new DaemonRuntimeStatusService(
            TimeProvider.System,
            channels: Array.Empty<IChannel>(),
            slackOptions: new SlackChannelOptions { Enabled = false },
            persistenceOptions: new DaemonPersistenceOptions(),
            telemetryOptions: Options.Create(new TelemetryOptions()),
            sessionConfig: DefaultSessionConfig,
            modelSelection: DefaultModelSelection);

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
            telemetryOptions: Options.Create(new TelemetryOptions()),
            sessionConfig: DefaultSessionConfig,
            modelSelection: DefaultModelSelection);

        var status = await service.GetStatusAsync();
        var slack = status.Connectors.Single(c => c.Key == "slack");

        Assert.True(slack.Enabled);
        Assert.Equal("disconnected", slack.Status);
    }

    [Fact]
    public async Task StatusIncludesModelCapabilities()
    {
        var service = new DaemonRuntimeStatusService(
            TimeProvider.System,
            channels: Array.Empty<IChannel>(),
            slackOptions: new SlackChannelOptions { Enabled = false },
            persistenceOptions: new DaemonPersistenceOptions(),
            telemetryOptions: Options.Create(new TelemetryOptions()),
            sessionConfig: DefaultSessionConfig,
            modelSelection: DefaultModelSelection);

        var status = await service.GetStatusAsync();

        Assert.Equal("test-model", status.Model.ModelId);
        Assert.Equal("test-provider", status.Model.Provider);
        Assert.Equal("Text, Image", status.Model.InputModalities);
        Assert.Equal("Text", status.Model.OutputModalities);
        Assert.Equal(32_768, status.Model.ContextWindow);
    }
}
