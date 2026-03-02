using Microsoft.Extensions.Options;
using Microsoft.Extensions.Logging.Abstractions;
using Netclaw.Actors.Tools;
using Netclaw.Channels;
using Netclaw.Channels.Slack;
using Netclaw.Configuration;
using Netclaw.Daemon.Configuration;
using Netclaw.Daemon.Gateway;
using Netclaw.Daemon.Mcp;
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

    [Fact]
    public async Task IncludesMcpConnectorHealthFromRuntimeStatuses()
    {
        var mcpServers = new Dictionary<string, McpServerEntry>
        {
            ["browser_disabled"] = new()
            {
                Transport = "stdio",
                Enabled = false,
                Command = "npx"
            },
            ["browser_broken"] = new()
            {
                Transport = "stdio",
                Enabled = true,
                Command = "definitely-not-a-real-command"
            }
        };

        var oauthService = new McpOAuthService(
            new HttpClient(),
            new NetclawPaths(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString())),
            TimeProvider.System,
            NullLogger<McpOAuthService>.Instance);
        var manager = new McpClientManager(
            mcpServers,
            new ToolRegistry(),
            oauthService,
            NullLogger<McpClientManager>.Instance);

        await manager.StartAsync(CancellationToken.None);
        try
        {
            var service = new DaemonRuntimeStatusService(
                TimeProvider.System,
                channels: Array.Empty<IChannel>(),
                slackOptions: new SlackChannelOptions { Enabled = false },
                persistenceOptions: new DaemonPersistenceOptions(),
                telemetryOptions: Options.Create(new TelemetryOptions()),
                sessionConfig: DefaultSessionConfig,
                modelSelection: DefaultModelSelection,
                mcpClientManager: manager);

            var status = await service.GetStatusAsync();

            var disabled = status.Connectors.Single(c => c.Key == "mcp:browser_disabled");
            Assert.False(disabled.Enabled);
            Assert.Equal("disabled", disabled.Status);

            var broken = status.Connectors.Single(c => c.Key == "mcp:browser_broken");
            Assert.True(broken.Enabled);
            Assert.Equal("disconnected", broken.Status);
            Assert.False(string.IsNullOrWhiteSpace(broken.Message));
        }
        finally
        {
            await manager.StopAsync(CancellationToken.None);
        }
    }
}
