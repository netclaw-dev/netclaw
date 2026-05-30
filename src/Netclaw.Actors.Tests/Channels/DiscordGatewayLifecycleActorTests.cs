// -----------------------------------------------------------------------
// <copyright file="DiscordGatewayLifecycleActorTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Akka.Actor;
using Akka.Configuration;
using Akka.Hosting;
using Akka.Hosting.TestKit;
using Akka.Pattern;
using Discord;
using Discord.WebSocket;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Netclaw.Channels.Discord;
using Netclaw.Channels.Discord.Transport;
using Xunit;

namespace Netclaw.Actors.Tests.Channels;

public sealed class DiscordGatewayLifecycleActorTests(ITestOutputHelper output) : TestKit(output: output)
{
    protected override Config? Config =>
        ConfigurationFactory.ParseString("akka.test.default-timeout = 5s");

    protected override void ConfigureServices(HostBuilderContext context, IServiceCollection services)
    {
    }

    protected override void ConfigureAkka(AkkaConfigurationBuilder builder, IServiceProvider provider)
    {
    }

    [Fact]
    public async Task Ready_event_while_disconnected_requires_clean_reconnect()
    {
        var transport = new FakeDiscordGatewayTransport
        {
            ConnectionState = ConnectionState.Connected,
            CurrentUserId = 42
        };
        var sink = new RecordingGatewayEventSink();
        var actor = CreateLifecycleActor(transport, sink);
        await WaitForActorReadyAsync(actor);
        Assert.Equal(1, transport.ReadySubscriberCount);

        await transport.RaiseReadyAsync();

        await AwaitAssertAsync(async () =>
        {
            var snapshot = await actor.Ask<DiscordGatewaySnapshot>(
                DiscordNetGatewayLifecycleActor.GetSnapshot.Instance,
                TimeSpan.FromSeconds(3),
                TestContext.Current.CancellationToken);

            Assert.False(snapshot.IsReady);
            Assert.True(snapshot.IsConnected);
            Assert.Equal(
                "Discord gateway received READY outside a clean startup cycle; forcing a clean reconnect.",
                snapshot.HealthDetail);
            Assert.Equal(1, sink.CleanReconnectCount);
        }, cancellationToken: TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Clean_reconnect_state_reports_not_ready_even_when_transport_remains_connected()
    {
        var transport = new FakeDiscordGatewayTransport
        {
            ConnectionState = ConnectionState.Disconnected,
            CurrentUserId = 42
        };
        var sink = new RecordingGatewayEventSink();
        var actor = CreateLifecycleActor(transport, sink);
        await WaitForActorReadyAsync(actor);
        Assert.Equal(1, transport.ReadySubscriberCount);

        var connectTask = actor.Ask<DiscordGatewaySnapshot>(
            new DiscordNetGatewayLifecycleActor.Connect("test-token"),
            TimeSpan.FromSeconds(3),
            TestContext.Current.CancellationToken);

        await AwaitAssertAsync(() => Assert.Equal(1, transport.StartCount),
            cancellationToken: TestContext.Current.CancellationToken);

        transport.ConnectionState = ConnectionState.Connected;
        await transport.RaiseReadyAsync();

        var readySnapshot = await connectTask;
        Assert.True(readySnapshot.IsReady);

        await transport.RaiseConnectedAsync();

        await AwaitAssertAsync(async () =>
        {
            var snapshot = await actor.Ask<DiscordGatewaySnapshot>(
                DiscordNetGatewayLifecycleActor.GetSnapshot.Instance,
                TimeSpan.FromSeconds(3),
                TestContext.Current.CancellationToken);

            Assert.False(snapshot.IsReady);
            Assert.True(snapshot.IsConnected);
            Assert.Equal(
                "Discord gateway reconnected outside a clean startup cycle; forcing a clean reconnect.",
                snapshot.HealthDetail);
            Assert.Equal(1, sink.CleanReconnectCount);
        }, cancellationToken: TestContext.Current.CancellationToken);
    }

    private IActorRef CreateLifecycleActor(
        FakeDiscordGatewayTransport transport,
        RecordingGatewayEventSink sink)
    {
        return Sys.ActorOf(DiscordNetGatewayLifecycleActor.CreateProps(
            transport,
            TimeProvider.System,
            sink,
            NullLogger.Instance));
    }

    private static async Task WaitForActorReadyAsync(IActorRef actor)
    {
        await actor.Ask<DiscordGatewaySnapshot>(
            DiscordNetGatewayLifecycleActor.GetSnapshot.Instance,
            TimeSpan.FromSeconds(3));
    }

    private sealed class RecordingGatewayEventSink : IDiscordGatewayEventSink
    {
        public int CleanReconnectCount { get; private set; }

        public Task PublishMessageAsync(DiscordGatewayMessage message) => Task.CompletedTask;

        public Task PublishInteractionAsync(DiscordGatewayInteraction interaction) => Task.CompletedTask;

        public Task PublishCleanReconnectRequiredAsync(string reason)
        {
            CleanReconnectCount++;
            return Task.CompletedTask;
        }
    }

    private sealed class FakeDiscordGatewayTransport : IDiscordGatewayTransport
    {
        private Func<Task>? _connected;
        private Func<Task>? _ready;

        public event Func<LogMessage, Task> Log
        {
            add { }
            remove { }
        }

        public event Func<Task> Connected
        {
            add => _connected += value;
            remove => _connected -= value;
        }

        public event Func<Task> Ready
        {
            add
            {
                _ready += value;
                ReadySubscriberCount++;
            }
            remove
            {
                _ready -= value;
                ReadySubscriberCount--;
            }
        }

        public event Func<Exception, Task> Disconnected
        {
            add { }
            remove { }
        }

        public event Func<SocketMessage, Task> MessageReceived
        {
            add { }
            remove { }
        }

        public event Func<SocketMessageComponent, Task> ButtonExecuted
        {
            add { }
            remove { }
        }

        public ConnectionState ConnectionState { get; set; }

        public ulong? CurrentUserId { get; set; }

        public int StartCount { get; private set; }

        public int ReadySubscriberCount { get; private set; }

        public Task LoginAsync(string botToken) => Task.CompletedTask;

        public Task StartAsync()
        {
            StartCount++;
            return Task.CompletedTask;
        }

        public Task StopAsync()
        {
            ConnectionState = ConnectionState.Disconnected;
            return Task.CompletedTask;
        }

        public Task LogoutAsync() => Task.CompletedTask;

        public Task RaiseConnectedAsync() => _connected?.Invoke() ?? Task.CompletedTask;

        public Task RaiseReadyAsync() => _ready?.Invoke() ?? Task.CompletedTask;
    }
}
