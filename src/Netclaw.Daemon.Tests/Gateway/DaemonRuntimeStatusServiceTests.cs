// -----------------------------------------------------------------------
// <copyright file="DaemonRuntimeStatusServiceTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Net;
using Microsoft.Extensions.AI;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Logging.Abstractions;
using Netclaw.Actors.Channels;
using Netclaw.Actors.Memory;
using Netclaw.Actors.Tools;
using Netclaw.Channels;
using Netclaw.Channels.Telemetry;
using Netclaw.Configuration;
using Netclaw.Configuration.Secrets;
using Netclaw.Daemon.Configuration;
using Netclaw.Daemon.Gateway;
using Netclaw.Daemon.Mcp;
using Netclaw.Daemon.Tests.Mcp;
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

    private DaemonRuntimeStatusService CreateService(
        IChannelRegistry? channelRegistry = null,
        DaemonPersistenceOptions? persistenceOptions = null,
        IOptions<TelemetryOptions>? telemetryOptions = null,
        ModelCapabilities? modelCapabilities = null,
        ModelSelection? modelSelection = null,
        DaemonConfig? daemonConfig = null,
        NetclawPaths? paths = null,
        McpClientManager? mcpClientManager = null,
        SQLiteMemoryStore? sqliteMemoryStore = null,
        IChatClientProvider? chatClientProvider = null,
        ProviderRuntimeValidation? providerValidation = null,
        MemoryEmbedderHolder? memoryEmbedderHolder = null,
        MemoryConfig? memoryConfig = null)
    {
        return new DaemonRuntimeStatusService(
            new DaemonStartClock(TimeProvider.System),
            TimeProvider.System,
            channelRegistry ?? CreateRegistry([]),
            persistenceOptions ?? new DaemonPersistenceOptions(),
            telemetryOptions ?? Options.Create(new TelemetryOptions()),
            modelCapabilities ?? DefaultModelCapabilities,
            modelSelection ?? DefaultModelSelection,
            daemonConfig ?? new DaemonConfig(),
            paths ?? CreatePaths(),
            chatClientProvider ?? new TestChatClientProvider(),
            providerValidation ?? new ProviderRuntimeValidation(ProviderRuntimeStatus.Valid, null, []),
            mcpClientManager,
            sqliteMemoryStore,
            memoryEmbedderHolder,
            memoryConfig);
    }

    private static IChannelRegistry CreateRegistry(
        IReadOnlyCollection<ChannelDescriptor> descriptors,
        IReadOnlyCollection<IChannel>? channels = null)
    {
        channels ??= [];
        return new ChannelRegistry(
            descriptors.Select(descriptor => new StaticChannelDescriptorProvider(descriptor)),
            descriptors.Select(descriptor => new DescriptorChannelRuntimeSnapshotProvider(descriptor, channels)));
    }

    private static ChannelDescriptor Descriptor(
        ChannelType channelType,
        bool enabled,
        ChannelKind kind = ChannelKind.RemoteChat)
    {
        return new ChannelDescriptor(
            ChannelDescriptorKey.FromChannelType(channelType),
            channelType,
            kind,
            channelType.ToString(),
            enabled,
            ChannelCapabilities.RuntimeHealth,
            ToolIntents: new HashSet<ChannelToolIntentKind>(),
            AddressKinds: new HashSet<ChannelAddressKind>(),
            SupportedOutputEffects: new HashSet<ChannelOutputEffectKind>());
    }

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
    public async Task DegradedChatClient_FlagsModelAsDegradedAndOverallAsDegraded()
    {
        var noOpProvider = new NoOpChatClientProvider(new[] { "github-copilot", "my-openrouter" });
        var validation = new ProviderRuntimeValidation(
            ProviderRuntimeStatus.NoProviderConfigured,
            "model 'Main' references provider 'ollama-local1' which is not configured (available: ollama-local)",
            new[] { "ollama-local" });

        var service = CreateService(
            modelCapabilities: new ModelCapabilities
            {
                ModelId = "qwen3:30b",
                InputModalities = ModelModality.Text,
                OutputModalities = ModelModality.Text,
                ContextWindowTokens = 32_768
            },
            modelSelection: new ModelSelection
            {
                Main = new ModelReference { Provider = "local-ollama", ModelId = "qwen3:30b" }
            },
            chatClientProvider: noOpProvider,
            providerValidation: validation);

        var status = await service.GetStatusAsync(TestContext.Current.CancellationToken);

        Assert.NotNull(status.Model);
        Assert.True(status.Model.Degraded);
        Assert.Equal("", status.Model.ModelId);
        Assert.Equal("", status.Model.Provider);
        Assert.Equal(0, status.Model.ContextWindow);
        Assert.Null(status.Model.DisplayName);
        Assert.Contains("ollama-local1", status.Model.DegradedReason);
        Assert.Equal("degraded", status.Overall);
    }

    [Fact]
    public async Task IncludesSlackConnectorAsDisabled_WhenNotEnabled()
    {
        var service = CreateService(channelRegistry: CreateRegistry([
            Descriptor(ChannelType.Slack, enabled: false)
        ]));

        var status = await service.GetStatusAsync(TestContext.Current.CancellationToken);
        var slack = status.Connectors.Single(c => c.Key == "slack");

        Assert.False(slack.Enabled);
        Assert.Equal("disabled", slack.Status);
    }

    [Fact]
    public async Task ReportsSlackAsDisconnected_WhenEnabledButMissingRuntimeChannel()
    {
        var service = CreateService(channelRegistry: CreateRegistry([
            Descriptor(ChannelType.Slack, enabled: true)
        ]));

        var status = await service.GetStatusAsync(TestContext.Current.CancellationToken);
        var slack = status.Connectors.Single(c => c.Key == "slack");

        Assert.True(slack.Enabled);
        Assert.Equal("disconnected", slack.Status);
    }

    [Fact]
    public async Task StatusEnumeratesDescriptorBackedOutputChannels()
    {
        var service = CreateService(channelRegistry: CreateRegistry([
            Descriptor(ChannelType.Slack, enabled: false),
            Descriptor(ChannelType.Discord, enabled: false),
            Descriptor(ChannelType.Mattermost, enabled: false),
            Descriptor(ChannelType.Tui, enabled: true, ChannelKind.LocalInteractiveClient)
        ]));

        var status = await service.GetStatusAsync(TestContext.Current.CancellationToken);

        Assert.Equal(
            new[] { "discord", "mattermost", "slack", "tui" },
            status.Connectors.Select(connector => connector.Key).Order(StringComparer.Ordinal));

        var mattermost = status.Connectors.Single(connector => connector.Key == "mattermost");
        Assert.False(mattermost.Enabled);
        Assert.Equal("disabled", mattermost.Status);

        var tui = status.Connectors.Single(connector => connector.Key == "tui");
        Assert.True(tui.Enabled);
        Assert.Equal("healthy", tui.Status);
    }

    [Fact]
    public async Task StatusMapsRuntimeChannelHealthThroughSnapshotProvider()
    {
        var channel = new TestChannel(
            ChannelType.Slack,
            new ChannelHealth(ChannelHealthStatus.Healthy));

        var service = CreateService(channelRegistry: CreateRegistry([
            Descriptor(ChannelType.Slack, enabled: true)
        ], [channel]));

        var status = await service.GetStatusAsync(TestContext.Current.CancellationToken);
        var slack = status.Connectors.Single(connector => connector.Key == "slack");

        Assert.True(slack.Enabled);
        Assert.Equal("healthy", slack.Status);
        Assert.Null(slack.Message);
    }

    [Fact]
    public async Task StatusIncludesModelCapabilities()
    {
        var service = CreateService();

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

        var paths = new NetclawPaths(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString()));
        paths.EnsureDirectoriesExist();
        var credentials = new McpOAuthCredentialStore(
            paths,
            TimeProvider.System,
            new NullSecretsProtector(),
            NullLogger<McpOAuthCredentialStore>.Instance);
        using var flowBroker = new McpOAuthFlowBroker(TimeProvider.System, CancellationToken.None);
        var dependencies = McpManagerTestDependencies.Create();
        var manager = new McpClientManager(
            mcpServers,
            new ToolRegistry(),
            dependencies.SkillRegistry,
            dependencies.SkillIndexPublisher,
            dependencies.ToolAccessPolicy,
            dependencies.ToolConfig,
            credentials,
            McpOAuthTestDoubles.UnusedRegistrar(),
            flowBroker,
            new DaemonConfig(),
            NullNotificationSink.Instance,
            TimeProvider.System,
            new McpClientRuntime(),
            NullLogger<McpClientManager>.Instance,
            new SessionConfig());

        await manager.StartAsync(CancellationToken.None);
        try
        {
            var service = CreateService(mcpClientManager: manager);

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
        var errorAt = DateTimeOffset.Parse("2026-07-22T12:00:00Z");
        var authRequired = McpClientManager.CreateAwaitingAuthStatus(
            new McpServerName("textforge"), errorAt);
        var authFailed = McpClientManager.CreateAuthFailedStatus(
            new McpServerName("notion"),
            new HttpRequestException(httpRequestError: HttpRequestError.Unknown, "Unauthorized", null, HttpStatusCode.Unauthorized),
            oauthManaged: true,
            errorAt);

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

        var service = CreateService(paths: paths, sqliteMemoryStore: sqliteStore);

        var status = await service.GetStatusAsync(TestContext.Current.CancellationToken);

        Assert.NotNull(status.Memory);
        Assert.Equal("sqlite", status.Memory.Provider);
        Assert.Equal("healthy", status.Memory.Status);
        Assert.Equal(paths.MemorySqliteDbPath, status.Memory.DatabasePath);
        Assert.Equal(0, status.Memory.PendingCheckpoints);
    }

    [Fact]
    public async Task StatusReportsEmbeddingsDisabled_WhenConfigOff()
    {
        var paths = CreatePaths();
        paths.EnsureDirectoriesExist();
        var sqliteStore = new SQLiteMemoryStore(paths.MemorySqliteDbPath, TimeProvider.System);
        await sqliteStore.InitializeAsync(TestContext.Current.CancellationToken);

        var service = CreateService(
            paths: paths,
            sqliteMemoryStore: sqliteStore,
            memoryConfig: new MemoryConfig { Embeddings = { Enabled = false } });

        var status = await service.GetStatusAsync(TestContext.Current.CancellationToken);

        Assert.Equal("disabled", status.Memory!.Embeddings!.Status);
    }

    [Fact]
    public async Task StatusReportsEmbeddingsOk_WhenHolderIsAvailable()
    {
        var paths = CreatePaths();
        paths.EnsureDirectoriesExist();
        var sqliteStore = new SQLiteMemoryStore(paths.MemorySqliteDbPath, TimeProvider.System);
        await sqliteStore.InitializeAsync(TestContext.Current.CancellationToken);

        var holder = new MemoryEmbedderHolder(new FakeAvailableEmbedder("tiny-fixture"), initialQueryPrefix: "", initialCalibratedMinCosineSimilarity: null);
        var service = CreateService(
            paths: paths,
            sqliteMemoryStore: sqliteStore,
            memoryEmbedderHolder: holder,
            memoryConfig: new MemoryConfig { Embeddings = { Enabled = true, ModelId = "tiny-fixture" } });

        var status = await service.GetStatusAsync(TestContext.Current.CancellationToken);

        Assert.Equal("ok", status.Memory!.Embeddings!.Status);
        Assert.Equal("tiny-fixture", status.Memory.Embeddings.ModelId);
    }

    [Fact]
    public async Task StatusReportsEmbeddingsDegraded_WhenEnabledButHolderIsUnavailable()
    {
        var paths = CreatePaths();
        paths.EnsureDirectoriesExist();
        var sqliteStore = new SQLiteMemoryStore(paths.MemorySqliteDbPath, TimeProvider.System);
        await sqliteStore.InitializeAsync(TestContext.Current.CancellationToken);

        var holder = new MemoryEmbedderHolder(new UnavailableMemoryEmbedder("tiny-fixture", "model missing"), initialQueryPrefix: "", initialCalibratedMinCosineSimilarity: null);
        var service = CreateService(
            paths: paths,
            sqliteMemoryStore: sqliteStore,
            memoryEmbedderHolder: holder,
            memoryConfig: new MemoryConfig { Embeddings = { Enabled = true, ModelId = "tiny-fixture" } });

        var status = await service.GetStatusAsync(TestContext.Current.CancellationToken);

        Assert.Equal("degraded", status.Memory!.Embeddings!.Status);
    }

    [Fact]
    public async Task StatusIncludesChannelCountersForEnabledChannels()
    {
        var slack = ChannelTelemetry.For(Netclaw.Actors.Channels.ChannelType.Slack);
        var before = slack.GetSnapshot();

        slack.RecordEventReceived("message");
        slack.RecordEventDropped(AclDenyReasons.ChannelNotAllowed);
        slack.RecordEventRouted("message");
        slack.RecordMessageEnqueued();
        slack.RecordReplyPosted(42);
        slack.RecordReplyFailed(77);

        var service = CreateService(channelRegistry: CreateRegistry([
            Descriptor(ChannelType.Slack, enabled: true),
            Descriptor(ChannelType.Discord, enabled: false)
        ]));

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
        var service = CreateService(daemonConfig: new DaemonConfig { DisableSelfUpdate = true });

        var status = await service.GetStatusAsync(TestContext.Current.CancellationToken);

        Assert.NotNull(status.Update);
        Assert.True(status.Update!.SelfUpdateDisabled);
    }

    private sealed class TestChannel(
        ChannelType channelType,
        ChannelHealth health) : IChannel
    {
        public ChannelType ChannelType => channelType;

        public string DisplayName => channelType.ToString();

        public ValueTask<ChannelHealth> GetHealthAsync(CancellationToken cancellationToken = default)
        {
            return ValueTask.FromResult(health);
        }

        public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class TestChatClientProvider : IChatClientProvider
    {
        public IChatClient GetClient(ModelRole role) => throw new NotSupportedException();
    }

    private sealed class FakeAvailableEmbedder(string modelId) : IMemoryEmbedder
    {
        public string ModelId => modelId;

        public int Dimensions => 8;

        public bool IsAvailable => true;

        public ValueTask<ReadOnlyMemory<float>> EmbedAsync(string text, EmbeddingPurpose purpose, CancellationToken ct)
            => ValueTask.FromResult<ReadOnlyMemory<float>>(new float[Dimensions]);

        public ValueTask<IReadOnlyList<ReadOnlyMemory<float>>> EmbedBatchAsync(IReadOnlyList<string> texts, EmbeddingPurpose purpose, CancellationToken ct)
            => ValueTask.FromResult<IReadOnlyList<ReadOnlyMemory<float>>>(texts.Select(_ => (ReadOnlyMemory<float>)new float[Dimensions]).ToList());
    }
}
