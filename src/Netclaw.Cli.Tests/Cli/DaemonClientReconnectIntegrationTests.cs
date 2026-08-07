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

        var sessionId = await WaitFor(
            client.CreateSessionAsync(ChannelType.Tui, TestContext.Current.CancellationToken),
            TimeSpan.FromSeconds(10));

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

        var ensured = await WaitFor(
            client.EnsureSessionAsync(ChannelType.Tui, TestContext.Current.CancellationToken),
            TimeSpan.FromSeconds(10));
        Assert.Equal(sessionId, ensured);

        await WaitFor(
            client.SendAsync("after", TestContext.Current.CancellationToken),
            TimeSpan.FromSeconds(10));

        await WaitFor(reconnectedOutput.Task, TimeSpan.FromSeconds(5));
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
