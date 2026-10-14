// -----------------------------------------------------------------------
// <copyright file="DaemonClientSessionTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Netclaw.Actors.Protocol;
using Netclaw.Cli.Daemon;
using Netclaw.Daemon.Gateway;
using R3;
using Xunit;
using static Netclaw.Actors.Sessions.SessionProtocol;

namespace Netclaw.Cli.Tests.Cli;

public sealed class DaemonClientSessionTests
{
    [Fact]
    public async Task ResumeSessionAsync_reattaches_to_existing_session_via_EnsureSession()
    {
        using var host = await StartFakeHubAsync();
        await using var client = InMemorySignalRClientFactory.Create(host);

        // Create an initial session to get a known session ID
        var originalSessionId = await client.CreateSessionAsync(Netclaw.Actors.Channels.ChannelType.Tui, TestContext.Current.CancellationToken);
        Assert.StartsWith("signalr/", originalSessionId);

        // Simulate a "new client" by creating a fresh DaemonClient
        // that resumes the same session ID
        await using var client2 = InMemorySignalRClientFactory.Create(host);

        var outputReceived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var sub = client2.SessionOutput.Subscribe(output =>
        {
            if (output is TextOutput { Text: "echo:hello-resumed" })
                outputReceived.TrySetResult();
        });

        var resumedSessionId = await client2.ResumeSessionAsync(originalSessionId, Netclaw.Actors.Channels.ChannelType.Tui, TestContext.Current.CancellationToken);

        // EnsureSession should return the same session ID, not create a new one
        Assert.Equal(originalSessionId, resumedSessionId);

        // Verify the session is functional — can send and receive messages
        await client2.SendAsync("hello-resumed", TestContext.Current.CancellationToken);

        await outputReceived.Task.WaitAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task RespondToInteractionAsync_invokes_hub_method()
    {
        using var host = await StartFakeHubAsync();
        var state = host.Services.GetRequiredService<FakeSessionState>();

        await using var client = InMemorySignalRClientFactory.Create(host);
        await client.CreateSessionAsync(Netclaw.Actors.Channels.ChannelType.Tui, TestContext.Current.CancellationToken);

        await client.RespondToInteractionAsync("call-1", ApprovalOptionKeys.ApproveOnce, TestContext.Current.CancellationToken);

        Assert.Equal(("call-1", ApprovalOptionKeys.ApproveOnce), state.LastInteractionResponse);
    }

    [Fact]
    public async Task RespondToInteractionAsync_supports_session_scope()
    {
        using var host = await StartFakeHubAsync();
        var state = host.Services.GetRequiredService<FakeSessionState>();

        await using var client = InMemorySignalRClientFactory.Create(host);
        await client.CreateSessionAsync(Netclaw.Actors.Channels.ChannelType.Tui, TestContext.Current.CancellationToken);

        await client.RespondToInteractionAsync("call-2", ApprovalOptionKeys.ApproveSession, TestContext.Current.CancellationToken);

        Assert.Equal(("call-2", ApprovalOptionKeys.ApproveSession), state.LastInteractionResponse);
    }


    [Theory]
    [InlineData(40_000)]
    [InlineData(SessionHubLimits.MaxReceiveMessageBytes - SessionHubLimits.EnvelopeHeadroomBytes)]
    public async Task SendAsync_delivers_large_plain_text_up_to_the_limit(int length)
    {
        var text = new string('a', length);
        Assert.Null(SessionHubLimits.CheckText(text));

        using var host = await StartFakeHubAsync();
        var state = host.Services.GetRequiredService<FakeSessionState>();
        await using var client = InMemorySignalRClientFactory.Create(host);
        await client.CreateSessionAsync(Netclaw.Actors.Channels.ChannelType.Tui, TestContext.Current.CancellationToken);

        await client.SendAsync(text, TestContext.Current.CancellationToken);

        Assert.Equal(text, Assert.Single(state.Messages));
    }

    [Fact]
    public async Task SendAsync_delivers_the_largest_accepted_text_made_of_characters_json_escapes()
    {
        // Quotes, backslashes, control characters, markup characters, BMP non-ASCII and a surrogate pair all
        // expand when JSON-encoded. Fill right up to the client-side limit: if the check under-measured, the
        // server would drop the connection and this send would throw.
        string[] units = ["\"", "\\", "\n", "\u0001", "<&>'+", "\u00e9", "\u20ac", "\ud83d\ude00"];
        var text = new System.Text.StringBuilder();
        var total = 0;
        for (var i = 0; ; i++)
        {
            var unit = units[i % units.Length];
            var size = SessionHubLimits.MeasureEncodedTextBytes(unit);
            if (total + size > SessionHubLimits.MaxReceiveMessageBytes - SessionHubLimits.EnvelopeHeadroomBytes) break;
            text.Append(unit);
            total += size;
        }
        var payload = text.ToString();
        Assert.Null(SessionHubLimits.CheckText(payload));

        using var host = await StartFakeHubAsync();
        var state = host.Services.GetRequiredService<FakeSessionState>();
        await using var client = InMemorySignalRClientFactory.Create(host);
        await client.CreateSessionAsync(Netclaw.Actors.Channels.ChannelType.Tui, TestContext.Current.CancellationToken);

        await client.SendAsync(payload, TestContext.Current.CancellationToken);

        Assert.Equal(payload, Assert.Single(state.Messages));
    }

    [Fact]
    public async Task SendAsync_refuses_over_limit_text_without_sending_and_keeps_the_connection()
    {
        var text = new string('a', SessionHubLimits.MaxReceiveMessageBytes + 100_000);

        using var host = await StartFakeHubAsync();
        var state = host.Services.GetRequiredService<FakeSessionState>();
        await using var client = InMemorySignalRClientFactory.Create(host);
        await client.CreateSessionAsync(Netclaw.Actors.Channels.ChannelType.Tui, TestContext.Current.CancellationToken);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => client.SendAsync(text, TestContext.Current.CancellationToken));
        Assert.Equal("Message is too large to send (610 KB; limit 512 KB).", error.Message);
        Assert.Empty(state.Messages);

        // The same connection still works afterwards.
        await client.SendAsync("still-here", TestContext.Current.CancellationToken);
        Assert.Equal("still-here", Assert.Single(state.Messages));
    }

    [Theory]
    [InlineData(87_210, true)]
    [InlineData(87_211, false)]
    public void CheckText_counts_escaped_size_not_characters(int length, bool fits)
    {
        // Each e-acute is 2 UTF-8 bytes but 6 bytes once the hub JSON-escapes it, so the budget
        // (512 KB minus headroom) holds 87,210 of them.
        Assert.Equal(fits, SessionHubLimits.CheckText(new string('\u00e9', length)) is null);
    }

    private static async Task<IHost> StartFakeHubAsync()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddSessionHubSignalR();
        builder.Services.AddSingleton<FakeSessionState>();

        var app = builder.Build();
        app.MapHub<FakeResumeHub>("/hub/session");

        await app.StartAsync();
        return app;
    }

    private sealed class FakeSessionState
    {
        private readonly object _gate = new();
        private readonly HashSet<string> _sessions = [];
        private readonly Dictionary<string, string> _connectionSessions = [];
        public (string CallId, string SelectedKey)? LastInteractionResponse { get; private set; }
        private readonly List<string> _messages = [];
        public string[] Messages { get { lock (_gate) return [.. _messages]; } }
        public void RecordMessage(string text) { lock (_gate) _messages.Add(text); }

        public SessionEnsureResultDto Ensure(string connectionId, string? sessionId)
        {
            lock (_gate)
            {
                if (!string.IsNullOrWhiteSpace(sessionId) && _sessions.Contains(sessionId))
                {
                    _connectionSessions[connectionId] = sessionId;
                    return new SessionEnsureResultDto(sessionId, false) { TextAdmissionVersion = 1 };
                }

                var created = $"signalr/{Guid.NewGuid():N}";
                _sessions.Add(created);
                _connectionSessions[connectionId] = created;
                return new SessionEnsureResultDto(created, true) { TextAdmissionVersion = 1 };
            }
        }

        public bool IsAttached(string connectionId, string sessionId)
            => _connectionSessions.TryGetValue(connectionId, out var attached)
               && string.Equals(attached, sessionId, StringComparison.Ordinal);

        public void RecordInteractionResponse(string callId, string selectedKey)
        {
            lock (_gate)
            {
                LastInteractionResponse = (callId, selectedKey);
            }
        }

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

            _state.RecordMessage(text);

            // Echo only short text: the client's own receive limit is 32 KB, so echoing a big message would drop it.
            await Clients.Caller.ReceiveOutput(new SessionOutputDto
            {
                Type = "text",
                SessionId = sessionId,
                TimestampMs = TimeProvider.System.GetUtcNow().ToUnixTimeMilliseconds(),
                Text = text.Length <= 1000 ? $"echo:{text}" : $"echo-length:{text.Length}"
            });

            await Clients.Caller.ReceiveOutput(new SessionOutputDto
            {
                Type = "turn_completed",
                SessionId = sessionId,
                TimestampMs = TimeProvider.System.GetUtcNow().ToUnixTimeMilliseconds(),
                TurnNumber = new Netclaw.Actors.Protocol.TurnNumber(1)
            });
        }

        public Task RespondToInteraction(string sessionId, string callId, string selectedKey)
        {
            if (!_state.IsAttached(Context.ConnectionId, sessionId))
                throw new HubException("session not attached");

            _state.RecordInteractionResponse(callId, selectedKey);
            return Task.CompletedTask;
        }

        public override Task OnDisconnectedAsync(Exception? exception)
        {
            _state.Disconnect(Context.ConnectionId);
            return base.OnDisconnectedAsync(exception);
        }
    }
}
