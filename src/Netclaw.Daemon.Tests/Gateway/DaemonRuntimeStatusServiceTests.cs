using System.Net;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Logging.Abstractions;
using Netclaw.Actors.Memory;
using Netclaw.Actors.Tools;
using Netclaw.Channels;
using Netclaw.Channels.Discord;
using Netclaw.Channels.Slack;
using Netclaw.Channels.Telemetry;
using Netclaw.Configuration;
using Netclaw.Daemon.Configuration;
using Netclaw.Daemon.Gateway;
using Netclaw.Daemon.Mcp;
using Netclaw.Daemon.Services;
using Netclaw.Providers.OAuth;
using Netclaw.Tools;
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

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    public async ValueTask DisposeAsync()
    {
        await TryDeleteDirectoryAsync(_tempBase);
    }

    private static async Task TryDeleteDirectoryAsync(string path)
    {
        if (!Directory.Exists(path))
            return;

        // Clear only the connection pool for THIS test's database, not all pools.
        // Using ClearAllPools() would interfere with other parallel tests.
        var dbPath = Path.Combine(path, "netclaw.db");
        if (File.Exists(dbPath))
        {
            var connectionString = new SqliteConnectionStringBuilder { DataSource = dbPath }.ToString();
            SqliteConnection.ClearPool(new SqliteConnection(connectionString));
        }

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
            discordOptions: new DiscordChannelOptions { Enabled = false },
            persistenceOptions: new DaemonPersistenceOptions(),
            telemetryOptions: Options.Create(new TelemetryOptions()),
            modelCapabilities: DefaultModelCapabilities,
            modelSelection: DefaultModelSelection,
            daemonConfig: new DaemonConfig(),
            paths: CreatePaths());

        var status = await service.GetStatusAsync(TestContext.Current.CancellationToken);
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
            discordOptions: new DiscordChannelOptions { Enabled = false },
            persistenceOptions: new DaemonPersistenceOptions(),
            telemetryOptions: Options.Create(new TelemetryOptions()),
            modelCapabilities: DefaultModelCapabilities,
            modelSelection: DefaultModelSelection,
            daemonConfig: new DaemonConfig(),
            paths: CreatePaths());

        var status = await service.GetStatusAsync(TestContext.Current.CancellationToken);
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
            discordOptions: new DiscordChannelOptions { Enabled = false },
            persistenceOptions: new DaemonPersistenceOptions(),
            telemetryOptions: Options.Create(new TelemetryOptions()),
            modelCapabilities: DefaultModelCapabilities,
            modelSelection: DefaultModelSelection,
            daemonConfig: new DaemonConfig(),
            paths: CreatePaths());

        var status = await service.GetStatusAsync(TestContext.Current.CancellationToken);

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
                discordOptions: new DiscordChannelOptions { Enabled = false },
                persistenceOptions: new DaemonPersistenceOptions(),
                telemetryOptions: Options.Create(new TelemetryOptions()),
                modelCapabilities: DefaultModelCapabilities,
                modelSelection: DefaultModelSelection,
                daemonConfig: new DaemonConfig(),
                paths: CreatePaths(),
                mcpClientManager: manager);

            var status = await service.GetStatusAsync(TestContext.Current.CancellationToken);

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
        var authRequired = McpClientManager.CreateAwaitingAuthStatus(new McpServerName("textforge"));
        var authFailed = McpClientManager.CreateAuthFailedStatus(
            new McpServerName("notion"),
            new HttpRequestException(httpRequestError: HttpRequestError.Unknown, "Unauthorized", null, HttpStatusCode.Unauthorized),
            oauthManaged: true);

        var connectors = new[]
        {
            DaemonRuntimeStatusService.ToConnector(new McpServerName("textforge"), authRequired),
            DaemonRuntimeStatusService.ToConnector(new McpServerName("notion"), authFailed),
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
        await sqliteStore.InitializeAsync(TestContext.Current.CancellationToken);

        var service = new DaemonRuntimeStatusService(
            new DaemonStartClock(TimeProvider.System),
            TimeProvider.System,
            channels: Array.Empty<IChannel>(),
            slackOptions: new SlackChannelOptions { Enabled = false },
            discordOptions: new DiscordChannelOptions { Enabled = false },
            persistenceOptions: new DaemonPersistenceOptions(),
            telemetryOptions: Options.Create(new TelemetryOptions()),
            modelCapabilities: DefaultModelCapabilities,
            modelSelection: DefaultModelSelection,
            daemonConfig: new DaemonConfig(),
            paths: paths,
            sqliteMemoryStore: sqliteStore);

        var status = await service.GetStatusAsync(TestContext.Current.CancellationToken);

        Assert.NotNull(status.Memory);
        Assert.Equal("sqlite", status.Memory.Provider);
        Assert.Equal("healthy", status.Memory.Status);
        Assert.Equal(paths.MemorySqliteDbPath, status.Memory.DatabasePath);
        Assert.Equal(0, status.Memory.PendingCheckpoints);
    }

    [Fact]
    public async Task StatusIncludesChannelCountersForEnabledChannels()
    {
        var slack = ChannelTelemetry.For(Netclaw.Actors.Channels.ChannelType.Slack);
        var before = slack.GetSnapshot();

        slack.RecordEventReceived("message");
        slack.RecordEventDropped("channel_not_allowed");
        slack.RecordEventRouted("message");
        slack.RecordMessageEnqueued();
        slack.RecordReplyPosted(42);
        slack.RecordReplyFailed(77);

        var service = new DaemonRuntimeStatusService(
            new DaemonStartClock(TimeProvider.System),
            TimeProvider.System,
            channels: Array.Empty<IChannel>(),
            slackOptions: new SlackChannelOptions { Enabled = true },
            discordOptions: new DiscordChannelOptions { Enabled = false },
            persistenceOptions: new DaemonPersistenceOptions(),
            telemetryOptions: Options.Create(new TelemetryOptions()),
            modelCapabilities: DefaultModelCapabilities,
            modelSelection: DefaultModelSelection,
            daemonConfig: new DaemonConfig(),
            paths: CreatePaths());

        var status = await service.GetStatusAsync(TestContext.Current.CancellationToken);

        var slackActivity = Assert.Single(status.Telemetry.Channels);
        Assert.Equal("slack", slackActivity.ChannelType);
        Assert.True(slackActivity.EventsReceived >= before.EventsReceived + 1);
        Assert.True(slackActivity.EventsDropped >= before.EventsDropped + 1);
        Assert.True(slackActivity.EventsRouted >= before.EventsRouted + 1);
        Assert.True(slackActivity.RepliesPosted >= before.RepliesPosted + 1);
        Assert.True(slackActivity.RepliesFailed >= before.RepliesFailed + 1);
    }

    [Fact]
    public async Task StatusIncludesSelfUpdateDisabledFlag()
    {
        var service = new DaemonRuntimeStatusService(
            new DaemonStartClock(TimeProvider.System),
            TimeProvider.System,
            channels: Array.Empty<IChannel>(),
            slackOptions: new SlackChannelOptions { Enabled = false },
            discordOptions: new DiscordChannelOptions { Enabled = false },
            persistenceOptions: new DaemonPersistenceOptions(),
            telemetryOptions: Options.Create(new TelemetryOptions()),
            modelCapabilities: DefaultModelCapabilities,
            modelSelection: DefaultModelSelection,
            daemonConfig: new DaemonConfig { DisableSelfUpdate = true },
            paths: CreatePaths());

        var status = await service.GetStatusAsync(TestContext.Current.CancellationToken);

        Assert.NotNull(status.Update);
        Assert.True(status.Update!.SelfUpdateDisabled);
    }
}
