using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Netclaw.Actors.Protocol;
using Netclaw.Cli.Daemon;
using Netclaw.Daemon.Gateway;
using R3;
using Xunit;

namespace Netclaw.Cli.Tests.Cli;

public sealed class DaemonClientSessionTests
{
    [Fact]
    public async Task ResumeSessionAsync_reattaches_to_existing_session_via_EnsureSession()
    {
        var port = GetFreeTcpPort();
        using var host = await StartFakeHubAsync(port);

        await using var client = new DaemonClient($"http://127.0.0.1:{port}");

        // Create an initial session to get a known session ID
        var originalSessionId = await client.CreateSessionAsync("tui");
        Assert.StartsWith("signalr/", originalSessionId);

        // Simulate a "new client" by creating a fresh DaemonClient
        // that resumes the same session ID
        await using var client2 = new DaemonClient($"http://127.0.0.1:{port}");

        var outputReceived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var sub = client2.SessionOutput.Subscribe(output =>
        {
            if (output is TextOutput { Text: "echo:hello-resumed" })
                outputReceived.TrySetResult();
        });

        var resumedSessionId = await client2.ResumeSessionAsync(originalSessionId);

        // EnsureSession should return the same session ID, not create a new one
        Assert.Equal(originalSessionId, resumedSessionId);

        // Verify the session is functional — can send and receive messages
        await client2.SendAsync(new Netclaw.Actors.Channels.ChannelInput
        {
            SenderId = "test",
            Contents = [new Microsoft.Extensions.AI.TextContent("hello-resumed")],
            ReceivedAt = DateTimeOffset.UtcNow
        });

        await outputReceived.Task.WaitAsync(TimeSpan.FromSeconds(5));
    }

    private static async Task<IHost> StartFakeHubAsync(int port)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseKestrel();
        builder.WebHost.UseUrls($"http://127.0.0.1:{port}");
        builder.Services.AddSignalR();
        builder.Services.AddSingleton<FakeSessionState>();

        var app = builder.Build();
        app.MapHub<FakeResumeHub>("/hub/session", options =>
        {
            options.Transports = HttpTransportType.WebSockets | HttpTransportType.LongPolling;
        });

        await app.StartAsync();
        return app;
    }

    private static int GetFreeTcpPort() => TestNetworkHelpers.GetFreeTcpPort();

    private sealed class FakeSessionState
    {
        private readonly object _gate = new();
        private readonly HashSet<string> _sessions = [];
        private readonly Dictionary<string, string> _connectionSessions = new();

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
        {
            lock (_gate)
            {
                _connectionSessions.Remove(connectionId);
            }
        }
    }

    private sealed class FakeResumeHub : Hub<ISessionHubClient>
    {
        private readonly FakeSessionState _state;

        public FakeResumeHub(FakeSessionState state)
        {
            _state = state;
        }

        public Task<SessionEnsureResultDto> EnsureSession(string? sessionId, string channelType)
            => Task.FromResult(_state.Ensure(Context.ConnectionId, sessionId));

        public async Task SendMessage(string sessionId, string text)
        {
            if (!_state.IsAttached(Context.ConnectionId, sessionId))
                throw new HubException("session not attached");

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
