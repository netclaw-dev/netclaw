using System.Net;
using Microsoft.Data.Sqlite;
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
using Netclaw.Daemon.Services;
using Netclaw.Providers.OAuth;
using Xunit;

namespace Netclaw.Daemon.Tests.Gateway;

public sealed class DaemonRuntimeStatusServiceTests : IAsyncLifetime
{
    private static readonly ModelCapabilities DefaultModelCapabilities = new()
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

    private readonly string _tempBase = Path.Combine(Path.GetTempPath(), $"netclaw-status-test-{Guid.NewGuid():N}");

    private NetclawPaths CreatePaths() => new(_tempBase);

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        await TryDeleteDirectoryAsync(_tempBase);
    }

    private static async Task TryDeleteDirectoryAsync(string path)
    {
        if (!Directory.Exists(path))
            return;

        SqliteConnection.ClearAllPools();

        for (var i = 0; i < 8; i++)
        {
            try
            {
                Directory.Delete(path, recursive: true);
                return;
            }
            catch (IOException) when (i < 7)
            {
                await Task.Delay(25 * (i + 1));
            }
            catch (UnauthorizedAccessException) when (i < 7)
            {
                await Task.Delay(25 * (i + 1));
            }
        }

        // Best effort cleanup: file handles can remain briefly open on Windows CI.
        // Leaving temp dirs behind is preferable to failing the test run.
    }

    [Fact]
    public async Task IncludesSlackConnectorAsDisabled_WhenNotEnabled()
    {
        var service = new DaemonRuntimeStatusService(
            new DaemonStartClock(TimeProvider.System),
            TimeProvider.System,
            channels: Array.Empty<IChannel>(),
            slackOptions: new SlackChannelOptions { Enabled = false },
            persistenceOptions: new DaemonPersistenceOptions(),
            telemetryOptions: Options.Create(new TelemetryOptions()),
            modelCapabilities: DefaultModelCapabilities,
            modelSelection: DefaultModelSelection,
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
            new DaemonStartClock(TimeProvider.System),
            TimeProvider.System,
            channels: Array.Empty<IChannel>(),
            slackOptions: new SlackChannelOptions { Enabled = true, AllowedChannelIds = ["C1"] },
            persistenceOptions: new DaemonPersistenceOptions(),
            telemetryOptions: Options.Create(new TelemetryOptions()),
            modelCapabilities: DefaultModelCapabilities,
            modelSelection: DefaultModelSelection,
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
            new DaemonStartClock(TimeProvider.System),
            TimeProvider.System,
            channels: Array.Empty<IChannel>(),
            slackOptions: new SlackChannelOptions { Enabled = false },
            persistenceOptions: new DaemonPersistenceOptions(),
            telemetryOptions: Options.Create(new TelemetryOptions()),
            modelCapabilities: DefaultModelCapabilities,
            modelSelection: DefaultModelSelection,
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

        var pkceService = new OAuthPkceService(new HttpClient());
        var oauthService = new McpOAuthService(
            new HttpClient(),
            new NetclawPaths(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString())),
            TimeProvider.System,
            NullLogger<McpOAuthService>.Instance,
            pkceService,
            NullNotificationSink.Instance);
        var manager = new McpClientManager(
            mcpServers,
            new ToolRegistry(),
            new ToolConfig(),
            oauthService,
            NullNotificationSink.Instance,
            TimeProvider.System,
            NullLogger<McpClientManager>.Instance);

        await manager.StartAsync(CancellationToken.None);
        try
        {
            var service = new DaemonRuntimeStatusService(
                new DaemonStartClock(TimeProvider.System),
                TimeProvider.System,
                channels: Array.Empty<IChannel>(),
                slackOptions: new SlackChannelOptions { Enabled = false },
                persistenceOptions: new DaemonPersistenceOptions(),
                telemetryOptions: Options.Create(new TelemetryOptions()),
                modelCapabilities: DefaultModelCapabilities,
                modelSelection: DefaultModelSelection,
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
    public async Task IncludesAuthRequiredAndAuthFailedMcpConnectorStatuses()
    {
        var authRequired = McpClientManager.CreateAwaitingAuthStatus("textforge");
        var authFailed = McpClientManager.CreateAuthFailedStatus(
            "notion",
            new HttpRequestException(httpRequestError: HttpRequestError.Unknown, "Unauthorized", null, HttpStatusCode.Unauthorized),
            oauthManaged: true);

        var connectors = new[]
        {
            DaemonRuntimeStatusService.ToConnector("textforge", authRequired),
            DaemonRuntimeStatusService.ToConnector("notion", authFailed),
        };

        var authRequiredConnector = connectors.Single(c => c.Key == "mcp:textforge");
        Assert.Equal("auth-required", authRequiredConnector.Status);
        Assert.Contains("netclaw mcp auth textforge", authRequiredConnector.Message);

        var authFailedConnector = connectors.Single(c => c.Key == "mcp:notion");
        Assert.Equal("auth-failed", authFailedConnector.Status);
        Assert.Contains("netclaw mcp auth notion", authFailedConnector.Message);

        Assert.Equal("degraded", DaemonRuntimeStatusService.ResolveOverallStatus(connectors));
    }

    [Fact]
    public async Task StatusIncludesMemory_SqliteBackend()
    {
        var paths = CreatePaths();
        paths.EnsureDirectoriesExist();

        var sqliteStore = new SQLiteMemoryStore(paths.MemorySqliteDbPath, TimeProvider.System);
        await sqliteStore.InitializeAsync();

        var service = new DaemonRuntimeStatusService(
            new DaemonStartClock(TimeProvider.System),
            TimeProvider.System,
            channels: Array.Empty<IChannel>(),
            slackOptions: new SlackChannelOptions { Enabled = false },
            persistenceOptions: new DaemonPersistenceOptions(),
            telemetryOptions: Options.Create(new TelemetryOptions()),
            modelCapabilities: DefaultModelCapabilities,
            modelSelection: DefaultModelSelection,
            paths: paths,
            sqliteMemoryStore: sqliteStore);

        var status = await service.GetStatusAsync();

        Assert.NotNull(status.Memory);
        Assert.Equal("sqlite", status.Memory.Provider);
        Assert.Equal("healthy", status.Memory.Status);
        Assert.Equal(paths.MemorySqliteDbPath, status.Memory.DatabasePath);
        Assert.Equal(0, status.Memory.PendingCheckpoints);
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
            new DaemonStartClock(TimeProvider.System),
            TimeProvider.System,
            channels: Array.Empty<IChannel>(),
            slackOptions: new SlackChannelOptions { Enabled = false },
            persistenceOptions: new DaemonPersistenceOptions(),
            telemetryOptions: Options.Create(new TelemetryOptions()),
            modelCapabilities: DefaultModelCapabilities,
            modelSelection: DefaultModelSelection,
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
