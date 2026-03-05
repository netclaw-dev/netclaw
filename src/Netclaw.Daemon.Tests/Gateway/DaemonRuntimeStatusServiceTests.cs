using Microsoft.Extensions.Options;
using Microsoft.Extensions.Logging.Abstractions;
using Netclaw.Actors.Memory;
using Netclaw.Actors.Tools;
using Netclaw.Channels;
using Netclaw.Channels.Slack;
using Netclaw.Channels.Telemetry;
using Netclaw.Configuration;
using Netclaw.Daemon.Configuration;
using Netclaw.Daemon.Gateway;
using Netclaw.Daemon.Mcp;
using Xunit;

namespace Netclaw.Daemon.Tests.Gateway;

public sealed class DaemonRuntimeStatusServiceTests : IDisposable
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

    private static readonly MemoryConfig DefaultMemoryConfig = new();

    private readonly string _tempBase = Path.Combine(Path.GetTempPath(), $"netclaw-status-test-{Guid.NewGuid():N}");

    private NetclawPaths CreatePaths() => new(_tempBase);

    public void Dispose()
    {
        if (Directory.Exists(_tempBase))
            Directory.Delete(_tempBase, recursive: true);
    }

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
            modelSelection: DefaultModelSelection,
            memoryConfig: DefaultMemoryConfig,
            paths: CreatePaths());

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
            modelSelection: DefaultModelSelection,
            memoryConfig: DefaultMemoryConfig,
            paths: CreatePaths());

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
            modelSelection: DefaultModelSelection,
            memoryConfig: DefaultMemoryConfig,
            paths: CreatePaths());

        var status = await service.GetStatusAsync();

        var model = Assert.IsType<Netclaw.Configuration.DaemonRuntimeStatus.Model>(status.Model);
        Assert.Equal("test-model", model.ModelId);
        Assert.Equal("test-provider", model.Provider);
        Assert.Equal("Text, Image", model.InputModalities);
        Assert.Equal("Text", model.OutputModalities);
        Assert.Equal(32_768, model.ContextWindow);
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
            TimeProvider.System,
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
                memoryConfig: DefaultMemoryConfig,
                paths: CreatePaths(),
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

    [Fact]
    public async Task StatusIncludesMemory_FileBackend()
    {
        var paths = CreatePaths();
        paths.EnsureDirectoriesExist();
        var fileStore = new FileMemoryStore(paths.MemoriesDirectory, TimeProvider.System);

        await fileStore.StoreAsync("First Memory", "Content one.");
        await fileStore.StoreAsync("Second Memory", "Content two.");

        var service = new DaemonRuntimeStatusService(
            TimeProvider.System,
            channels: Array.Empty<IChannel>(),
            slackOptions: new SlackChannelOptions { Enabled = false },
            persistenceOptions: new DaemonPersistenceOptions(),
            telemetryOptions: Options.Create(new TelemetryOptions()),
            sessionConfig: DefaultSessionConfig,
            modelSelection: DefaultModelSelection,
            memoryConfig: new MemoryConfig { Provider = "files" },
            paths: paths,
            fileMemoryStore: fileStore);

        var status = await service.GetStatusAsync();

        Assert.NotNull(status.Memory);
        Assert.Equal("files", status.Memory.Provider);
        Assert.Equal("healthy", status.Memory.Status);
        Assert.Equal(2, status.Memory.MemoryCount);
        Assert.Equal(paths.MemoryIndexPath, status.Memory.IndexPath);
    }

    [Fact]
    public async Task StatusIncludesMemory_MemorizerNotConnected()
    {
        var service = new DaemonRuntimeStatusService(
            TimeProvider.System,
            channels: Array.Empty<IChannel>(),
            slackOptions: new SlackChannelOptions { Enabled = false },
            persistenceOptions: new DaemonPersistenceOptions(),
            telemetryOptions: Options.Create(new TelemetryOptions()),
            sessionConfig: DefaultSessionConfig,
            modelSelection: DefaultModelSelection,
            memoryConfig: new MemoryConfig { Provider = "memorizer" },
            paths: CreatePaths());

        var status = await service.GetStatusAsync();

        Assert.NotNull(status.Memory);
        Assert.Equal("memorizer", status.Memory.Provider);
        Assert.Equal("unavailable", status.Memory.Status);
    }

    [Fact]
    public async Task StatusIncludesSlackCountersSnapshot()
    {
        var before = ChannelTelemetry.GetSnapshot();

        ChannelTelemetry.RecordSlackEventReceived("message");
        ChannelTelemetry.RecordSlackEventDropped("channel_not_allowed");
        ChannelTelemetry.RecordSlackEventRouted("message");
        ChannelTelemetry.RecordSlackMessageEnqueued();
        ChannelTelemetry.RecordSlackReplyPosted(42);
        ChannelTelemetry.RecordSlackReplyFailed(77);

        var service = new DaemonRuntimeStatusService(
            TimeProvider.System,
            channels: Array.Empty<IChannel>(),
            slackOptions: new SlackChannelOptions { Enabled = false },
            persistenceOptions: new DaemonPersistenceOptions(),
            telemetryOptions: Options.Create(new TelemetryOptions()),
            sessionConfig: DefaultSessionConfig,
            modelSelection: DefaultModelSelection,
            memoryConfig: DefaultMemoryConfig,
            paths: CreatePaths());

        var status = await service.GetStatusAsync();

        Assert.NotNull(status.Telemetry.SlackCounters);
        Assert.True(status.Telemetry.SlackCounters!.EventsReceived >= before.SlackEventsReceived + 1);
        Assert.True(status.Telemetry.SlackCounters.EventsDropped >= before.SlackEventsDropped + 1);
        Assert.True(status.Telemetry.SlackCounters.EventsRouted >= before.SlackEventsRouted + 1);
        Assert.True(status.Telemetry.SlackCounters.MessagesEnqueued >= before.SlackMessagesEnqueued + 1);
        Assert.True(status.Telemetry.SlackCounters.RepliesPosted >= before.SlackRepliesPosted + 1);
        Assert.True(status.Telemetry.SlackCounters.RepliesFailed >= before.SlackRepliesFailed + 1);
    }
}
