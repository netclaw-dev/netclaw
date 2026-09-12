// -----------------------------------------------------------------------
// <copyright file="DaemonClientReconnectIntegrationTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Collections.Concurrent;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Netclaw.Actors.Channels;
using Netclaw.Actors.Protocol;
using Netclaw.Cli.Daemon;
using Netclaw.Daemon.Gateway;
using R3;
using Xunit;
using static Netclaw.Actors.Sessions.SessionProtocol;

namespace Netclaw.Cli.Tests.Cli;

public sealed class DaemonClientReconnectIntegrationTests
{
    [Fact]
    public async Task EnsureSession_reattaches_same_session_after_transport_disconnect()
    {
        using var host = await StartFakeHubAsync();
        var state = host.Services.GetRequiredService<FakeHubState>();
        await using var client = InMemorySignalRClientFactory.Create(host);

        var disconnected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var reconnected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var reconnectedOutput = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        using var connectionSub = client.ConnectionEvents.Subscribe(evt =>
        {
            if (evt.State is DaemonConnectionState.TransportClosed)
                disconnected.TrySetResult();

            if (evt.State is DaemonConnectionState.Connected && disconnected.Task.IsCompleted)
                reconnected.TrySetResult();
        });

        using var outputSub = client.SessionOutput.Subscribe(output =>
        {
            if (output is TextOutput { Text: "echo:after" })
                reconnectedOutput.TrySetResult();
        });

        var ct = TestContext.Current.CancellationToken;
        var sessionId = await client.CreateSessionAsync(ChannelType.Tui, ct);

        var drop = client.SendAsync("drop", ct);
        await state.DropObserved.Task.WaitAsync(ct);
        _ = await Record.ExceptionAsync(() => drop);

        await disconnected.Task.WaitAsync(ct);
        await reconnected.Task.WaitAsync(ct);

        var ensured = await client.EnsureSessionAsync(ChannelType.Tui, ct);
        Assert.Equal(sessionId, ensured);

        await client.SendAsync("after", ct);
        await reconnectedOutput.Task.WaitAsync(ct);
    }

    private static async Task<IHost> StartFakeHubAsync()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddSignalR();
        builder.Services.AddSingleton<FakeHubState>();

        var app = builder.Build();
        app.MapHub<FakeSessionHub>("/hub/session");

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

        public TaskCompletionSource DropObserved { get; } =
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
                _state.DropObserved.TrySetResult();
                Context.Abort();
                return;
            }

            _state.RecordMessage(text);

            await Clients.Caller.ReceiveOutput(new SessionOutputDto
            {
                Type = "text",
                SessionId = sessionId,
                TimestampMs = TimeProvider.System.GetUtcNow().ToUnixTimeMilliseconds(),
                Text = $"echo:{text}"
            });

            await Clients.Caller.ReceiveOutput(new SessionOutputDto
            {
                Type = "turn_completed",
                SessionId = sessionId,
                TimestampMs = TimeProvider.System.GetUtcNow().ToUnixTimeMilliseconds(),
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
