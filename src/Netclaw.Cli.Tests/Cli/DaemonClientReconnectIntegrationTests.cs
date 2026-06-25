// -----------------------------------------------------------------------
// <copyright file="DaemonClientReconnectIntegrationTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Collections.Concurrent;
using Microsoft.AspNetCore.Builder;
using R3;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Netclaw.Actors.Channels;
using Netclaw.Actors.Protocol;
using Netclaw.Cli.Daemon;
using Netclaw.Daemon.Gateway;
using Xunit;
using static Netclaw.Actors.Sessions.SessionProtocol;

namespace Netclaw.Cli.Tests.Cli;

public sealed class DaemonClientReconnectIntegrationTests
{
    [Fact]
    public async Task EnsureSession_reattaches_same_session_after_transport_disconnect()
    {
        using var host = await StartFakeHubAsync();
        var port = TestNetworkHelpers.GetBoundPort(host);

        await using var client = new DaemonClient(
            $"http://127.0.0.1:{port}",
            serverTimeout: TimeSpan.FromSeconds(2));

        var disconnected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var reconnected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var reconnectedOutput = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        // Sync on Reconnecting OR TransportClosed: SignalR fires Reconnecting from
        // its state machine as soon as the transport drops, before any retry
        // attempt. Waiting for TransportClosed alone requires WithAutomaticReconnect
        // to exhaust its retries, which on Windows can exceed the test budget
        // because ConnectEx to a closed loopback port is not immediate (Winsock
        // SYN-retransmit path, exacerbated by WFP/AV filter drivers on hosted
        // runners). Either event is sufficient evidence the client has observed
        // the drop. (Disconnected is now terminal-only — emitted solely when the
        // supervised reconnect loop exhausts its budget — so it is not a drop signal.)
        using var connectionSub = client.ConnectionEvents.Subscribe(evt =>
        {
            if (evt.State is DaemonConnectionState.Reconnecting or DaemonConnectionState.TransportClosed)
                disconnected.TrySetResult();

            if (evt.State is DaemonConnectionState.Connected && disconnected.Task.IsCompleted)
                reconnected.TrySetResult();
        });

        using var outputSub = client.SessionOutput.Subscribe(output =>
        {
            if (output is TextOutput { Text: "echo:after" })
                reconnectedOutput.TrySetResult();
        });

        var sessionId = await client.CreateSessionAsync(ChannelType.Tui, TestContext.Current.CancellationToken);

        try
        {
            await client.SendAsync("drop", TestContext.Current.CancellationToken);
        }
        catch (Exception ex)
        {
            Assert.False(string.IsNullOrWhiteSpace(ex.Message));
        }

        await WaitFor(disconnected.Task, TimeSpan.FromSeconds(5));
        await WaitFor(reconnected.Task, TimeSpan.FromSeconds(10));

        var ensured = await client.EnsureSessionAsync(ChannelType.Tui, TestContext.Current.CancellationToken);
        Assert.Equal(sessionId, ensured);

        await client.SendAsync("after", TestContext.Current.CancellationToken);

        await WaitFor(reconnectedOutput.Task, TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task EnsureSession_recreates_session_after_server_restart()
    {
        var host1 = await StartFakeHubAsync();
        var port = TestNetworkHelpers.GetBoundPort(host1);

        // Fast reconnect delays keep the post-restart reconnect tight on the happy
        // path. Short ServerTimeout (2s) bounds how long the client can spend
        // Connected-but-blind after host1 stops, on platforms where TCP-level
        // detection is slow.
        await using var client = new DaemonClient(
            $"http://127.0.0.1:{port}",
            reconnectDelays: [TimeSpan.Zero, TimeSpan.FromMilliseconds(50)],
            serverTimeout: TimeSpan.FromSeconds(2));

        var outputs = new ConcurrentQueue<SessionOutput>();
        var firstResponseReceived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondResponseReceived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var clientDisconnected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        // Sync on Reconnecting OR TransportClosed. SignalR fires Reconnecting from
        // its state machine as soon as the transport drops, before any retry
        // attempt. Waiting for TransportClosed alone requires WithAutomaticReconnect
        // to exhaust its retries, which on Windows can exceed the test budget:
        // ConnectEx to a closed loopback port is not immediate (Winsock
        // SYN-retransmit path, exacerbated by WFP/AV filter drivers on hosted
        // runners), so each StartAsync retry can take seconds rather than the
        // sub-millisecond ECONNREFUSED seen on Linux.
        //
        // host1's port release is already synchronous via host1.Dispose(), so it
        // is not gated on the client observing anything.
        using var connectionSub = client.ConnectionEvents.Subscribe(evt =>
        {
            if (evt.State is DaemonConnectionState.Reconnecting or DaemonConnectionState.TransportClosed)
                clientDisconnected.TrySetResult();
        });

        using var sub = client.SessionOutput.Subscribe(output =>
        {
            outputs.Enqueue(output);

            if (output is TextOutput { Text: "echo:first" })
                firstResponseReceived.TrySetResult();

            if (output is TextOutput { Text: "echo:second" })
                secondResponseReceived.TrySetResult();
        });

        var firstSessionId = await client.CreateSessionAsync(ChannelType.Tui, TestContext.Current.CancellationToken);
        await client.SendAsync("first", TestContext.Current.CancellationToken);

        await WaitFor(firstResponseReceived.Task, TimeSpan.FromSeconds(5));

        await host1.StopAsync(TestContext.Current.CancellationToken);
        host1.Dispose();

        // Wait for the client to observe the drop (Reconnecting or Disconnected).
        // We do not gate on retry exhaustion: that is platform-dependent on
        // Windows and not what this test cares about. host1's listener socket
        // was released synchronously by host1.Dispose(); listener sockets do
        // not enter TIME_WAIT.
        await WaitFor(clientDisconnected.Task, TimeSpan.FromSeconds(10));

        // Start the replacement server, then drive the user-visible contract
        // directly. Waiting for a passive Connected event is timing-sensitive on
        // Windows because SignalR can stay in an in-flight reconnect attempt to
        // the closed loopback socket after host2 is already listening.
        var host2State = new FakeHubState();
        using var host2 = await StartFakeHubAsync(port, host2State);

        var recreatedSessionId = await client.EnsureSessionAsync(ChannelType.Tui, TestContext.Current.CancellationToken);
        var host2SessionId = await WaitFor(host2State.SessionEnsured.Task, TimeSpan.FromSeconds(10));
        Assert.Equal(host2SessionId, recreatedSessionId);
        Assert.NotEqual(firstSessionId, recreatedSessionId);

        await client.SendAsync("second", TestContext.Current.CancellationToken);

        var host2Text = await WaitFor(host2State.MessageReceived.Task, TimeSpan.FromSeconds(10));
        Assert.Equal("second", host2Text);
        await WaitFor(secondResponseReceived.Task, TimeSpan.FromSeconds(10));

        var outputSnapshot = outputs.ToArray();
        var textOutputs = outputSnapshot.OfType<TextOutput>().ToList();
        Assert.Contains(textOutputs, o => o.Text == "echo:first");
        Assert.Contains(textOutputs, o => o.Text == "echo:second");
        Assert.Contains(outputSnapshot, o => o is TurnCompleted);
    }

    private static async Task WaitFor(Task task, TimeSpan timeout)
    {
        await task.WaitAsync(timeout, TestContext.Current.CancellationToken);
    }

    private static async Task<T> WaitFor<T>(Task<T> task, TimeSpan timeout)
    {
        return await task.WaitAsync(timeout, TestContext.Current.CancellationToken);
    }

    // port: 0 (default) lets Kestrel bind a free ephemeral port and hold it for the
    // host's lifetime; callers read the actual port back via TestNetworkHelpers
    // .GetBoundPort. A non-zero port is passed only to rebind a replacement host to a
    // prior host's now-released port (the server-restart scenario).
    private static async Task<IHost> StartFakeHubAsync(int port = 0, FakeHubState? state = null)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseKestrel();
        builder.WebHost.UseUrls($"http://127.0.0.1:{port}");

        // Both DaemonClients in this file use serverTimeout: 2s so they notice a
        // dead host fast. SignalR's contract is that the client's ServerTimeout
        // must be at least 2x the server's KeepAliveInterval — otherwise the
        // client tears down a perfectly healthy *idle* connection when no server
        // ping arrives within ServerTimeout. The default KeepAliveInterval is
        // 15s, so a 2s ServerTimeout would drop every idle connection after 2s.
        // On a slow CI runner that turns the post-restart reconnect into an
        // unbounded flap loop that never settles on a stable Connected event.
        // A 200ms keep-alive keeps the 2s ServerTimeout valid (10x margin) so
        // reconnected connections stay up.
        builder.Services.AddSignalR(options =>
            options.KeepAliveInterval = TimeSpan.FromMilliseconds(200));
        builder.Services.AddSingleton(state ?? new FakeHubState());

        var app = builder.Build();
        app.MapHub<FakeSessionHub>("/hub/session", options =>
        {
            options.Transports = HttpTransportType.WebSockets | HttpTransportType.LongPolling;
        });

        await app.StartAsync();
        return app;
    }

    private sealed class FakeHubState
    {
        private readonly object _gate = new();
        private readonly HashSet<string> _sessions = [];
        private readonly ConcurrentDictionary<string, string> _connectionSessions = new();

        public TaskCompletionSource<string> SessionEnsured { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource<string> MessageReceived { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public SessionEnsureResultDto Ensure(string connectionId, string? sessionId)
        {
            SessionEnsureResultDto result;
            lock (_gate)
            {
                if (!string.IsNullOrWhiteSpace(sessionId) && _sessions.Contains(sessionId))
                {
                    _connectionSessions[connectionId] = sessionId;
                    result = new SessionEnsureResultDto(sessionId, false);
                }
                else
                {
                    var created = $"signalr/{Guid.NewGuid():N}";
                    _sessions.Add(created);
                    _connectionSessions[connectionId] = created;
                    result = new SessionEnsureResultDto(created, true);
                }
            }

            SessionEnsured.TrySetResult(result.SessionId);
            return result;
        }

        public bool IsAttached(string connectionId, string sessionId)
            => _connectionSessions.TryGetValue(connectionId, out var attached)
               && string.Equals(attached, sessionId, StringComparison.Ordinal);

        public void Disconnect(string connectionId)
            => _connectionSessions.TryRemove(connectionId, out _);

        public void RecordMessage(string text)
            => MessageReceived.TrySetResult(text);
    }

    private sealed class FakeSessionHub : Hub<ISessionHubClient>
    {
        private readonly FakeHubState _state;

        public FakeSessionHub(FakeHubState state)
        {
            _state = state;
        }

        public Task<SessionEnsureResultDto> EnsureSession(string? sessionId, string channelType)
            => Task.FromResult(_state.Ensure(Context.ConnectionId, sessionId));

        public async Task SendMessage(string sessionId, string text)
        {
            if (!_state.IsAttached(Context.ConnectionId, sessionId))
                throw new HubException("session not attached");

            if (string.Equals(text, "drop", StringComparison.Ordinal))
            {
                Context.Abort();
                return;
            }

            _state.RecordMessage(text);

            await Clients.Caller.ReceiveOutput(new SessionOutputDto
            {
                Type = "text",
                SessionId = sessionId,
                TimestampMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                Text = $"echo:{text}"
            });

            await Clients.Caller.ReceiveOutput(new SessionOutputDto
            {
                Type = "turn_completed",
                SessionId = sessionId,
                TimestampMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                TurnNumber = new Netclaw.Actors.Protocol.TurnNumber(1)
            });
        }

        public override Task OnDisconnectedAsync(Exception? exception)
        {
            _state.Disconnect(Context.ConnectionId);
            return base.OnDisconnectedAsync(exception);
        }
    }
}
