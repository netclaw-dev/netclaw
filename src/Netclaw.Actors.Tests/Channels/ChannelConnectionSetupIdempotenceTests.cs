// -----------------------------------------------------------------------
// <copyright file="ChannelConnectionSetupIdempotenceTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Akka.Hosting;
using Akka.Hosting.TestKit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Netclaw.Actors.Channels;
using Netclaw.Actors.Tests.Channels.TestHelpers;
using Netclaw.Channels;
using Netclaw.Channels.Discord;
using Netclaw.Channels.Mattermost;
using Netclaw.Configuration;
using Netclaw.Security;
using Xunit;

namespace Netclaw.Actors.Tests.Channels;

/// <summary>
/// ConnectionRestored publishes on EVERY transition to Ready (the fix for the
/// Healthy-but-deaf gap), so a normal operator connect reaches
/// CompleteConnectionSetup from both the connect-ask reply and the event
/// handler. The channel-side setup must therefore be exactly-once: duplicate
/// invocations must not double-subscribe ingress handlers or create a second
/// gateway actor (a second ActorOf would throw on the duplicate actor name).
/// </summary>
public sealed class ChannelConnectionSetupIdempotenceTests(ITestOutputHelper output) : TestKit(output: output)
{
    protected override void ConfigureServices(HostBuilderContext context, IServiceCollection services)
    {
    }

    protected override void ConfigureAkka(AkkaConfigurationBuilder builder, IServiceProvider provider)
    {
    }

    [Fact]
    public async Task Discord_connection_setup_runs_exactly_once_across_duplicate_restored_events()
    {
        var gateway = new RaisableDiscordGatewayClient();
        var replyClient = new UnconfiguredDiscordReplyClient();
        _ = new DiscordChannel(
            Sys,
            new FailingSessionPipeline(new InvalidOperationException("not used")),
            new SessionIngressGate(),
            gateway,
            replyClient,
            TestChannelRegistries.DiscordWithProcessingRenderer(replyClient),
            new NullContentScanner(),
            SafePromptInjectionDetector.Instance,
            new FakeHttpClientFactory(),
            null,
            NullNotificationSink.Instance,
            TimeProvider.System,
            new DiscordChannelOptions
            {
                Enabled = true,
                BotToken = new SensitiveString("test-token"),
                AllowedChannelIds = ["ch-1"]
            },
            NullLogger<DiscordChannel>.Instance,
            new ToolConfig
            {
                AudienceProfiles = TestDiscordGatewayDeps.DefaultAudienceProfiles
            },
            TestDiscordGatewayDeps.DefaultVisionCapableModel,
            Netclaw.Actors.Protocol.TestSessionStorageResolver.Instance);

        var snapshot = new DiscordGatewaySnapshot(
            IsConnected: true,
            IsReady: true,
            HealthDetail: null,
            BotUserId: new DiscordUserId("42"));

        await gateway.RaiseConnectionRestoredAsync(snapshot);
        await gateway.RaiseConnectionRestoredAsync(snapshot);

        Assert.Equal(1, gateway.MessageSubscriptionCount);
        Assert.Equal(1, gateway.InteractionSubscriptionCount);
    }

    [Fact]
    public async Task Mattermost_connection_setup_runs_exactly_once_across_duplicate_restored_events()
    {
        var gateway = new RaisableMattermostGatewayClient();
        _ = new MattermostChannel(
            Sys,
            new FailingSessionPipeline(new InvalidOperationException("not used")),
            new SessionIngressGate(),
            gateway,
            new RecordingMattermostReplyClient(),
            new NullContentScanner(),
            SafePromptInjectionDetector.Instance,
            new FakeHttpClientFactory(),
            null,
            NullNotificationSink.Instance,
            TimeProvider.System,
            new MattermostChannelOptions
            {
                Enabled = true,
                ServerUrl = "https://mattermost.example.com",
                BotToken = new SensitiveString("test-token"),
                AllowedChannelIds = ["ch-1"]
            },
            NullLogger<MattermostChannel>.Instance,
            new ToolConfig
            {
                AudienceProfiles = TestMattermostGatewayDeps.DefaultAudienceProfiles
            },
            TestMattermostGatewayDeps.DefaultVisionCapableModel,
            Netclaw.Actors.Protocol.TestSessionStorageResolver.Instance);

        var snapshot = new MattermostGatewaySnapshot(
            IsConnected: true,
            IsReady: true,
            HealthDetail: null,
            BotUserId: new MattermostUserId("bot-1"),
            BotUsername: "netclaw");

        await gateway.RaiseConnectionRestoredAsync(snapshot);
        await gateway.RaiseConnectionRestoredAsync(snapshot);

        Assert.Equal(1, gateway.MessageSubscriptionCount);
    }

    private sealed class RaisableDiscordGatewayClient : IDiscordGatewayClient
    {
        private Func<DiscordGatewayMessage, Task>? _messageReceived;
        private Func<DiscordGatewayInteraction, Task>? _interactionReceived;
        private Func<DiscordGatewaySnapshot, Task>? _connectionRestored;

        public int MessageSubscriptionCount { get; private set; }

        public int InteractionSubscriptionCount { get; private set; }

        public event Func<DiscordGatewayMessage, Task>? MessageReceived
        {
            add
            {
                _messageReceived += value;
                MessageSubscriptionCount++;
            }
            remove
            {
                _messageReceived -= value;
                MessageSubscriptionCount--;
            }
        }

        public event Func<DiscordGatewayInteraction, Task>? InteractionReceived
        {
            add
            {
                _interactionReceived += value;
                InteractionSubscriptionCount++;
            }
            remove
            {
                _interactionReceived -= value;
                InteractionSubscriptionCount--;
            }
        }

        public event Func<string, Task>? CleanReconnectRequired { add { } remove { } }

        public event Func<DiscordGatewaySnapshot, Task>? ConnectionRestored
        {
            add => _connectionRestored += value;
            remove => _connectionRestored -= value;
        }

        public Task RaiseConnectionRestoredAsync(DiscordGatewaySnapshot snapshot) =>
            _connectionRestored?.Invoke(snapshot) ?? Task.CompletedTask;

        public Task<DiscordGatewaySnapshot> GetSnapshotAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new DiscordGatewaySnapshot(true, true, null, new DiscordUserId("42")));

        public Task<DiscordGatewaySnapshot> ConnectAsync(string botToken, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("Idempotence tests drive only the ConnectionRestored event path.");

        public Task DisconnectAsync(CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("Idempotence tests drive only the ConnectionRestored event path.");
    }

    private sealed class RaisableMattermostGatewayClient : IMattermostGatewayClient
    {
        private Func<MattermostGatewayMessage, Task>? _messageReceived;
        private Func<MattermostGatewaySnapshot, Task>? _connectionRestored;

        public int MessageSubscriptionCount { get; private set; }

        public event Func<MattermostGatewayMessage, Task>? MessageReceived
        {
            add
            {
                _messageReceived += value;
                MessageSubscriptionCount++;
            }
            remove
            {
                _messageReceived -= value;
                MessageSubscriptionCount--;
            }
        }

        public event Func<string, Task>? CleanReconnectRequired { add { } remove { } }

        public event Func<MattermostGatewaySnapshot, Task>? ConnectionRestored
        {
            add => _connectionRestored += value;
            remove => _connectionRestored -= value;
        }

        public bool IsConnected => true;

        public bool IsReady => true;

        public MattermostUserId? BotUserId => new("bot-1");

        public string? BotUsername => "netclaw";

        public Task RaiseConnectionRestoredAsync(MattermostGatewaySnapshot snapshot) =>
            _connectionRestored?.Invoke(snapshot) ?? Task.CompletedTask;

        public Task<MattermostGatewaySnapshot> GetSnapshotAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new MattermostGatewaySnapshot(true, true, null, BotUserId, BotUsername));

        public Task<MattermostGatewaySnapshot> ConnectAsync(
            string serverUrl, string botToken, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("Idempotence tests drive only the ConnectionRestored event path.");

        public Task DisconnectAsync(CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("Idempotence tests drive only the ConnectionRestored event path.");
    }
}
