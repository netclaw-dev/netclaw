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

namespace Netclaw.Cli.Tests.Cli;

public sealed class DaemonClientReconnectIntegrationTests
{
    [Fact]
    public async Task EnsureSession_reattaches_same_session_after_transport_disconnect()
    {
        var port = GetFreeTcpPort();
        using var host = await StartFakeHubAsync(port);

        await using var client = new DaemonClient(
            $"http://127.0.0.1:{port}",
            serverTimeout: TimeSpan.FromSeconds(2));

        var disconnected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var reconnected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var reconnectedOutput = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        // Sync on Reconnecting OR Disconnected: SignalR fires Reconnecting from
        // its state machine as soon as the transport drops, before any retry
        // attempt. Waiting for Disconnected alone requires WithAutomaticReconnect
        // to exhaust its retries, which on Windows can exceed the test budget
        // because ConnectEx to a closed loopback port is not immediate (Winsock
        // SYN-retransmit path, exacerbated by WFP/AV filter drivers on hosted
        // runners). Either event is sufficient evidence the client has observed
        // the drop.
        using var connectionSub = client.ConnectionEvents.Subscribe(evt =>
        {
            if (evt.State is DaemonConnectionState.Reconnecting or DaemonConnectionState.Disconnected)
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
        var port = GetFreeTcpPort();
        var host1 = await StartFakeHubAsync(port);

        // Fast reconnect delays keep the post-restart reconnect tight on the happy
        // path. Short ServerTimeout (2s) bounds how long the client can spend
        // Connected-but-blind after host1 stops, on platforms where TCP-level
        // detection is slow.
        await using var client = new DaemonClient(
            $"http://127.0.0.1:{port}",
            reconnectDelays: [TimeSpan.Zero, TimeSpan.FromMilliseconds(50)],
            serverTimeout: TimeSpan.FromSeconds(2));

        var outputs = new List<SessionOutput>();
        var firstResponseReceived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondResponseReceived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var clientDisconnected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var reconnectedAfterRestart = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        // Sync on Reconnecting OR Disconnected. SignalR fires Reconnecting from
        // its state machine as soon as the transport drops, before any retry
        // attempt. Waiting for Disconnected alone requires WithAutomaticReconnect
        // to exhaust its retries, which on Windows can exceed the test budget:
        // ConnectEx to a closed loopback port is not immediate (Winsock
        // SYN-retransmit path, exacerbated by WFP/AV filter drivers on hosted
        // runners), so each StartAsync retry can take seconds rather than the
        // sub-millisecond ECONNREFUSED seen on Linux.
        //
        // The IsCompleted guard for reconnectedAfterRestart is still safe: at
        // least one Reconnecting/Disconnected always precedes any subsequent
        // Connected emission. host1's port release is already synchronous via
        // host1.Dispose(), so it is not gated on the client observing anything.
        using var connectionSub = client.ConnectionEvents.Subscribe(evt =>
        {
            if (evt.State is DaemonConnectionState.Reconnecting or DaemonConnectionState.Disconnected)
                clientDisconnected.TrySetResult();

            if (evt.State is DaemonConnectionState.Connected && clientDisconnected.Task.IsCompleted)
                reconnectedAfterRestart.TrySetResult();
        });

        using var sub = client.SessionOutput.Subscribe(output =>
        {
            outputs.Add(output);

            if (output is TextOutput { Text: "echo:first" })
                firstResponseReceived.TrySetResult();

            if (output is TextOutput { Text: "echo:second" })
                secondResponseReceived.TrySetResult();
        });

        await client.CreateSessionAsync(ChannelType.Tui, TestContext.Current.CancellationToken);
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

        // Start the replacement server. ReconnectLoopAsync is already running at
        // this point; it will connect to host2, re-attach the session, then emit
        // Connected — which sets reconnectedAfterRestart.
        using var host2 = await StartFakeHubAsync(port);

        await WaitFor(reconnectedAfterRestart.Task, TimeSpan.FromSeconds(15));

        await client.EnsureSessionAsync(ChannelType.Tui, TestContext.Current.CancellationToken);
        await client.SendAsync("second", TestContext.Current.CancellationToken);

        await WaitFor(secondResponseReceived.Task, TimeSpan.FromSeconds(10));

        var textOutputs = outputs.OfType<TextOutput>().ToList();
        Assert.Contains(textOutputs, o => o.Text == "echo:first");
        Assert.Contains(textOutputs, o => o.Text == "echo:second");
        Assert.Contains(outputs, o => o is TurnCompleted);
    }

    private static async Task WaitFor(Task task, TimeSpan timeout)
    {
        await task.WaitAsync(timeout);
    }

    private static async Task<IHost> StartFakeHubAsync(int port)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseKestrel();
        builder.WebHost.UseUrls($"http://127.0.0.1:{port}");
        builder.Services.AddSignalR();
        builder.Services.AddSingleton<FakeHubState>();

        var app = builder.Build();
        app.MapHub<FakeSessionHub>("/hub/session", options =>
        {
            options.Transports = HttpTransportType.WebSockets | HttpTransportType.LongPolling;
        });

        await app.StartAsync();
        return app;
    }

    private static int GetFreeTcpPort() => TestNetworkHelpers.GetFreeTcpPort();

    private sealed class FakeHubState
    {
        private readonly object _gate = new();
        private readonly HashSet<string> _sessions = [];
        private readonly ConcurrentDictionary<string, string> _connectionSessions = new();

        public SessionEnsureResultDto Ensure(string connectionId, string? sessionId)
        {
            lock (_gate)
            {
                if (!string.IsNullOrWhiteSpace(sessionId) && _sessions.Contains(sessionId))
                {
                    _connectionSessions[connectionId] = sessionId;
                    return new SessionEnsureResultDto { SessionId = sessionId, Created = false };
                }

                var created = $"signalr/{Guid.NewGuid():N}";
                _sessions.Add(created);
                _connectionSessions[connectionId] = created;
                return new SessionEnsureResultDto { SessionId = created, Created = true };
            }
        }

        public bool IsAttached(string connectionId, string sessionId)
            => _connectionSessions.TryGetValue(connectionId, out var attached)
               && string.Equals(attached, sessionId, StringComparison.Ordinal);

        public void Disconnect(string connectionId)
            => _connectionSessions.TryRemove(connectionId, out _);
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
                TurnNumber = 1
            });
        }

        public override Task OnDisconnectedAsync(Exception? exception)
        {
            _state.Disconnect(Context.ConnectionId);
            return base.OnDisconnectedAsync(exception);
        }
    }
}
