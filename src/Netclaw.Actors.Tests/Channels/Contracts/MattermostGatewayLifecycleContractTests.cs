// -----------------------------------------------------------------------
// <copyright file="MattermostGatewayLifecycleContractTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Akka.Actor;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Netclaw.Channels.Mattermost;
using Netclaw.Channels.Mattermost.Transport;
using Xunit;

namespace Netclaw.Actors.Tests.Channels.Contracts;

public sealed class MattermostGatewayLifecycleContractTests(ITestOutputHelper output)
    : GatewayLifecycleContractTests(output)
{
    private FakeMattermostGatewayTransport _transport = null!;
    private RecordingGatewayEventSink _sink = null!;
    private FakeTimeProvider _timeProvider = null!;

    protected override IActorRef CreateLifecycleActor()
    {
        _transport = new FakeMattermostGatewayTransport();
        _sink = new RecordingGatewayEventSink();
        _timeProvider = new FakeTimeProvider();
        return Sys.ActorOf(MattermostNetGatewayLifecycleActor.CreateProps(
            _transport,
            _timeProvider,
            _sink,
            NullLogger.Instance));
    }

    protected override void AdvanceTimeProvider(TimeSpan offset) => _timeProvider.Advance(offset);

    protected override async Task<LifecycleSnapshotView> GetSnapshotAsync(IActorRef actor)
    {
        var snapshot = await actor.Ask<MattermostGatewaySnapshot>(
            MattermostNetGatewayLifecycleActor.GetSnapshot.Instance,
            TimeSpan.FromSeconds(3));
        return ToView(snapshot);
    }

    protected override async Task<LifecycleSnapshotView> ConnectAsync(IActorRef actor)
    {
        var snapshot = await actor.Ask<MattermostGatewaySnapshot>(
            new MattermostNetGatewayLifecycleActor.Connect("https://mattermost.test", "test-token"),
            TimeSpan.FromSeconds(3));
        return ToView(snapshot);
    }

    protected override Task DisconnectAsync(IActorRef actor) =>
        actor.Ask<MattermostGatewaySnapshot>(
            MattermostNetGatewayLifecycleActor.Disconnect.Instance,
            TimeSpan.FromSeconds(3));

    protected override Task RaiseRuntimeDisconnectAsync(IActorRef actor, string reason) =>
        // Mattermost requests the clean reconnect immediately on a runtime
        // drop — no ready timeout to advance past.
        _transport.RaiseDisconnectedAsync(reason);

    protected override Task RaiseSpuriousReadySignalAsync()
    {
        _transport.IsConnected = true;
        return _transport.RaiseConnectedAsync();
    }

    protected override Task RaiseIngressEventAsync() =>
        _transport.RaiseMessageAsync(CreateMessage("event-1"));

    protected override int ForwardedIngressCount => _sink.Messages.Count;

    protected override int CleanReconnectCount => _sink.CleanReconnectCount;

    protected override int ConnectionRestoredCount => _sink.ConnectionRestoredCount;

    protected override int TransportStartCount => _transport.StartCount;

    protected override string DisconnectedHealthDetail => "Mattermost gateway disconnected.";

    protected override void AssertSingleTransportSubscription()
    {
        Assert.Equal(1, _transport.MessageSubscriberCount);
        Assert.Equal(1, _transport.ConnectedSubscriberCount);
        Assert.Equal(1, _transport.DisconnectedSubscriberCount);
        Assert.Equal(1, _transport.LogSubscriberCount);
    }

    protected override void AssertNoTransportSubscriptions()
    {
        Assert.Equal(0, _transport.MessageSubscriberCount);
        Assert.Equal(0, _transport.ConnectedSubscriberCount);
        Assert.Equal(0, _transport.DisconnectedSubscriberCount);
        Assert.Equal(0, _transport.LogSubscriberCount);
    }

    protected override void DeferTransportStop() => _transport.DeferStop();

    protected override void ReleaseTransportStop() => _transport.ReleaseStop();

    [Fact]
    public async Task Ingress_forwarded_exactly_once_after_reconnect_cycle()
    {
        // Proves the single registered handler actually forwards (exactly
        // once) when ready. Discord cannot drive a fully-formed inbound
        // message through its fake transport (Discord.Net entities are not
        // constructible), so the forwarding half of the no-duplicate-handlers
        // behavior is asserted here.
        var actor = CreateLifecycleActor();
        await GetSnapshotAsync(actor);

        await ConnectAsync(actor);
        await DisconnectAsync(actor);
        await ConnectAsync(actor);

        await RaiseIngressEventAsync();
        await GetSnapshotAsync(actor);

        Assert.Equal(1, ForwardedIngressCount);
    }

    private static LifecycleSnapshotView ToView(MattermostGatewaySnapshot snapshot) =>
        new(snapshot.IsConnected, snapshot.IsReady, snapshot.HealthDetail);

    private static MattermostGatewayMessage CreateMessage(string eventId) =>
        new(
            EventId: new MattermostEventId(eventId),
            ChannelId: new MattermostChannelId("ch-1"),
            PostId: new MattermostPostId("post-" + eventId),
            RootPostId: new MattermostRootPostId("root-1"),
            SenderId: new MattermostUserId("user-1"),
            IsBotMessage: false,
            IsDirectMessage: false,
            ContainsBotMention: true,
            Text: "hello",
            ReceivedAt: DateTimeOffset.Parse("2026-06-08T00:00:00Z"));

    private sealed class RecordingGatewayEventSink : IMattermostGatewayEventSink
    {
        private readonly List<MattermostGatewayMessage> _messages = [];

        public IReadOnlyList<MattermostGatewayMessage> Messages => _messages;

        public int CleanReconnectCount { get; private set; }

        public int ConnectionRestoredCount { get; private set; }

        public Task PublishMessageAsync(MattermostGatewayMessage message)
        {
            _messages.Add(message);
            return Task.CompletedTask;
        }

        public Task PublishCleanReconnectRequiredAsync(string reason)
        {
            CleanReconnectCount++;
            return Task.CompletedTask;
        }

        public Task PublishConnectionRestoredAsync(MattermostGatewaySnapshot snapshot)
        {
            ConnectionRestoredCount++;
            return Task.CompletedTask;
        }
    }

    private sealed class FakeMattermostGatewayTransport : IMattermostGatewayTransport
    {
        private Func<MattermostGatewayMessage, Task>? _messageReceived;
        private Func<Task>? _connected;
        private Func<MattermostGatewayDisconnect, Task>? _disconnected;
        private Func<string, Task>? _logReceived;
        private TaskCompletionSource? _pendingStop;

        public event Func<MattermostGatewayMessage, Task> MessageReceived
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

        public event Func<MattermostGatewayDisconnect, Task> Disconnected
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

        public event Func<string, Task> LogReceived
        {
            add
            {
                _logReceived += value;
                LogSubscriberCount++;
            }
            remove
            {
                _logReceived -= value;
                LogSubscriberCount--;
            }
        }

        public bool IsConnected { get; set; }

        public int StartCount { get; private set; }

        public int MessageSubscriberCount { get; private set; }

        public int ConnectedSubscriberCount { get; private set; }

        public int DisconnectedSubscriberCount { get; private set; }

        public int LogSubscriberCount { get; private set; }

        public Task<MattermostBotIdentity> StartAsync(
            string serverUrl, string botToken, CancellationToken cancellationToken = default)
        {
            StartCount++;
            IsConnected = true;
            return Task.FromResult(new MattermostBotIdentity("bot-1", "netclaw"));
        }

        public Task StopAsync()
        {
            if (_pendingStop is not null)
                return _pendingStop.Task;

            IsConnected = false;
            return Task.CompletedTask;
        }

        public void DeferStop() =>
            _pendingStop = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        public void ReleaseStop()
        {
            IsConnected = false;
            var pending = _pendingStop;
            _pendingStop = null;
            pending?.TrySetResult();
        }

        public Task RaiseMessageAsync(MattermostGatewayMessage message) =>
            _messageReceived?.Invoke(message) ?? Task.CompletedTask;

        public Task RaiseConnectedAsync() =>
            _connected?.Invoke() ?? Task.CompletedTask;

        public Task RaiseDisconnectedAsync(string reason)
        {
            IsConnected = false;
            return _disconnected?.Invoke(new MattermostGatewayDisconnect(reason)) ?? Task.CompletedTask;
        }
    }
}
