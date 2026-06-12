// -----------------------------------------------------------------------
// <copyright file="DiscordGatewayLifecycleContractTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Runtime.CompilerServices;
using Akka.Actor;
using Discord;
using Discord.WebSocket;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Netclaw.Channels.Discord;
using Netclaw.Channels.Discord.Transport;
using Xunit;

namespace Netclaw.Actors.Tests.Channels.Contracts;

public sealed class DiscordGatewayLifecycleContractTests(ITestOutputHelper output)
    : GatewayLifecycleContractTests(output)
{
    private FakeDiscordGatewayTransport _transport = null!;
    private RecordingGatewayEventSink _sink = null!;
    private FakeTimeProvider _timeProvider = null!;

    protected override IActorRef CreateLifecycleActor()
    {
        _transport = new FakeDiscordGatewayTransport { CurrentUserId = 42 };
        _sink = new RecordingGatewayEventSink();
        _timeProvider = new FakeTimeProvider();
        return Sys.ActorOf(DiscordNetGatewayLifecycleActor.CreateProps(
            _transport,
            _timeProvider,
            _sink,
            NullLogger.Instance));
    }

    protected override void AdvanceTimeProvider(TimeSpan offset) => _timeProvider.Advance(offset);

    protected override async Task<LifecycleSnapshotView> GetSnapshotAsync(IActorRef actor)
    {
        var snapshot = await actor.Ask<DiscordGatewaySnapshot>(
            DiscordNetGatewayLifecycleActor.GetSnapshot.Instance,
            TimeSpan.FromSeconds(3));
        return ToView(snapshot);
    }

    protected override async Task<LifecycleSnapshotView> ConnectAsync(IActorRef actor)
    {
        var snapshot = await actor.Ask<DiscordGatewaySnapshot>(
            new DiscordNetGatewayLifecycleActor.Connect("test-token"),
            TimeSpan.FromSeconds(3));
        return ToView(snapshot);
    }

    protected override Task DisconnectAsync(IActorRef actor) =>
        actor.Ask<DiscordGatewaySnapshot>(
            DiscordNetGatewayLifecycleActor.Disconnect.Instance,
            TimeSpan.FromSeconds(3));

    protected override async Task RaiseRuntimeDisconnectAsync(IActorRef actor, string reason)
    {
        // Discord.Net reconnects on its own after a transport drop, so the
        // lifecycle actor first re-enters Connecting and waits up to 30s for a
        // fresh READY before forcing the clean reconnect the contract expects.
        // Advance virtual time past that ready timeout to drive the decision.
        await _transport.RaiseDisconnectedAsync(new InvalidOperationException(reason));
        await GetSnapshotAsync(actor); // barrier: ready-timeout timer registered
        AdvanceScheduler(TimeSpan.FromSeconds(31));
    }

    protected override Task RaiseSpuriousReadySignalAsync()
    {
        _transport.ConnectionState = ConnectionState.Connected;
        return _transport.RaiseReadyAsync();
    }

    protected override Task RaiseIngressEventAsync()
    {
        // Discord.Net's SocketUserMessage has no reachable constructor, so an
        // uninitialized instance stands in. The not-ready drop path only reads
        // message.Id (default 0) and never dereferences nested entities, which
        // is all this contract needs — forwarding a fully-formed message is
        // not constructible for Discord and is covered by Mattermost's fixture.
        var message = (SocketUserMessage)RuntimeHelpers.GetUninitializedObject(typeof(SocketUserMessage));
        return _transport.RaiseMessageReceivedAsync(message);
    }

    protected override int ForwardedIngressCount => _sink.MessageCount;

    protected override int CleanReconnectCount => _sink.CleanReconnectCount;

    protected override int ConnectionRestoredCount => _sink.ConnectionRestoredCount;

    protected override int TransportStartCount => _transport.StartCount;

    protected override string DisconnectedHealthDetail => "Discord gateway disconnected.";

    protected override void AssertSingleTransportSubscription()
    {
        Assert.Equal(1, _transport.LogSubscriberCount);
        Assert.Equal(1, _transport.ConnectedSubscriberCount);
        Assert.Equal(1, _transport.ReadySubscriberCount);
        Assert.Equal(1, _transport.DisconnectedSubscriberCount);
        Assert.Equal(1, _transport.MessageSubscriberCount);
        Assert.Equal(1, _transport.ButtonSubscriberCount);
    }

    protected override void AssertNoTransportSubscriptions()
    {
        Assert.Equal(0, _transport.LogSubscriberCount);
        Assert.Equal(0, _transport.ConnectedSubscriberCount);
        Assert.Equal(0, _transport.ReadySubscriberCount);
        Assert.Equal(0, _transport.DisconnectedSubscriberCount);
        Assert.Equal(0, _transport.MessageSubscriberCount);
        Assert.Equal(0, _transport.ButtonSubscriberCount);
    }

    protected override void DeferTransportStop() => _transport.DeferStop();

    protected override void ReleaseTransportStop() => _transport.ReleaseStop();

    [Fact]
    public async Task Spurious_connected_event_while_ready_triggers_clean_reconnect()
    {
        var actor = CreateLifecycleActor();
        var ready = await ConnectAsync(actor);
        Assert.True(ready.IsReady);

        // Discord.Net raising Connected outside a clean startup cycle (e.g. an
        // internally resumed session) must force a clean reconnect. Mattermost
        // treats Connected-while-Ready as benign, so this stays Discord-specific.
        await _transport.RaiseConnectedAsync();

        await AwaitAssertAsync(async () =>
        {
            var snapshot = await GetSnapshotAsync(actor);
            Assert.False(snapshot.IsReady);
            Assert.False(snapshot.IsConnected);
            Assert.Equal(1, CleanReconnectCount);
        }, cancellationToken: TestContext.Current.CancellationToken);
    }

    private static LifecycleSnapshotView ToView(DiscordGatewaySnapshot snapshot) =>
        new(snapshot.IsConnected, snapshot.IsReady, snapshot.HealthDetail);

    private sealed class RecordingGatewayEventSink : IDiscordGatewayEventSink
    {
        public int MessageCount { get; private set; }

        public int CleanReconnectCount { get; private set; }

        public int ConnectionRestoredCount { get; private set; }

        public Task PublishMessageAsync(DiscordGatewayMessage message)
        {
            MessageCount++;
            return Task.CompletedTask;
        }

        public Task PublishInteractionAsync(DiscordGatewayInteraction interaction) => Task.CompletedTask;

        public Task PublishCleanReconnectRequiredAsync(string reason)
        {
            CleanReconnectCount++;
            return Task.CompletedTask;
        }

        public Task PublishConnectionRestoredAsync(DiscordGatewaySnapshot snapshot)
        {
            ConnectionRestoredCount++;
            return Task.CompletedTask;
        }
    }

    private sealed class FakeDiscordGatewayTransport : IDiscordGatewayTransport
    {
        private Func<Task>? _connected;
        private Func<Task>? _ready;
        private Func<Exception, Task>? _disconnected;
        private Func<LogMessage, Task>? _log;
        private Func<SocketMessage, Task>? _messageReceived;
        private Func<SocketMessageComponent, Task>? _buttonExecuted;
        private TaskCompletionSource? _pendingStop;

        public event Func<LogMessage, Task> Log
        {
            add
            {
                _log += value;
                LogSubscriberCount++;
            }
            remove
            {
                _log -= value;
                LogSubscriberCount--;
            }
        }

        public event Func<Task> Connected
        {
            add
            {
                _connected += value;
                ConnectedSubscriberCount++;
            }
            remove
            {
                _connected -= value;
                ConnectedSubscriberCount--;
            }
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
            add
            {
                _disconnected += value;
                DisconnectedSubscriberCount++;
            }
            remove
            {
                _disconnected -= value;
                DisconnectedSubscriberCount--;
            }
        }

        public event Func<SocketMessage, Task> MessageReceived
        {
            add
            {
                _messageReceived += value;
                MessageSubscriberCount++;
            }
            remove
            {
                _messageReceived -= value;
                MessageSubscriberCount--;
            }
        }

        public event Func<SocketMessageComponent, Task> ButtonExecuted
        {
            add
            {
                _buttonExecuted += value;
                ButtonSubscriberCount++;
            }
            remove
            {
                _buttonExecuted -= value;
                ButtonSubscriberCount--;
            }
        }

        public ConnectionState ConnectionState { get; set; }

        public ulong? CurrentUserId { get; set; }

        public int StartCount { get; private set; }

        public int LogSubscriberCount { get; private set; }

        public int ConnectedSubscriberCount { get; private set; }

        public int ReadySubscriberCount { get; private set; }

        public int DisconnectedSubscriberCount { get; private set; }

        public int MessageSubscriberCount { get; private set; }

        public int ButtonSubscriberCount { get; private set; }

        public Task LoginAsync(string botToken) => Task.CompletedTask;

        public Task StartAsync()
        {
            // Mirror a successful gateway handshake: Discord.Net connects and
            // raises READY shortly after StartAsync.
            StartCount++;
            ConnectionState = ConnectionState.Connected;
            return RaiseReadyAsync();
        }

        public Task StopAsync()
        {
            if (_pendingStop is not null)
                return _pendingStop.Task;

            ConnectionState = ConnectionState.Disconnected;
            return Task.CompletedTask;
        }

        public Task LogoutAsync() => Task.CompletedTask;

        public void DeferStop() =>
            _pendingStop = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        public void ReleaseStop()
        {
            ConnectionState = ConnectionState.Disconnected;
            var pending = _pendingStop;
            _pendingStop = null;
            pending?.TrySetResult();
        }

        public Task RaiseConnectedAsync() => _connected?.Invoke() ?? Task.CompletedTask;

        public Task RaiseReadyAsync() => _ready?.Invoke() ?? Task.CompletedTask;

        public Task RaiseDisconnectedAsync(Exception exception)
        {
            ConnectionState = ConnectionState.Disconnected;
            return _disconnected?.Invoke(exception) ?? Task.CompletedTask;
        }

        public Task RaiseMessageReceivedAsync(SocketMessage message) =>
            _messageReceived?.Invoke(message) ?? Task.CompletedTask;
    }
}
