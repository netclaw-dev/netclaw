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
        Assert.Equal("Message is too large to send (609.8 KB; limit 512 KB).", error.Message);
        Assert.Empty(state.Messages);

        // The same connection still works afterwards.
        await client.SendAsync("still-here", TestContext.Current.CancellationToken);
        Assert.Equal("still-here", Assert.Single(state.Messages));
    }

    [Fact]
    public void CheckText_measures_the_exact_frame_so_raw_characters_are_not_over_counted()
    {
        // The hub protocol writes e-acute, CJK and <>&'+ as raw UTF-8, not as \\uXXXX escapes.
        Assert.Null(SessionHubFrame.CheckText(new string('\u00e9', 200_000)));
        Assert.Null(SessionHubFrame.CheckText(new string('\u4e2d', 150_000)));
        Assert.Null(SessionHubFrame.CheckText(string.Concat(Enumerable.Repeat("<&>'+", 100_000))));
    }

    [Fact]
    public void CheckText_boundary_is_the_limit_minus_headroom()
    {
        var overhead = SessionHubFrame.MeasureSendMessageFrame("");
        var fits = SessionHubLimits.MaxReceiveMessageBytes - SessionHubFrame.HeadroomBytes - overhead;
        Assert.Null(SessionHubFrame.CheckText(new string('a', fits)));
        Assert.NotNull(SessionHubFrame.CheckText(new string('a', fits + 1)));
    }

    [Fact]
    public void CheckText_counts_escaped_characters_at_their_escaped_size()
    {
        var overhead = SessionHubFrame.MeasureSendMessageFrame("");
        // Control characters are written as 6-byte \\u00XX escapes.
        Assert.Equal(overhead + 6 * 1000, SessionHubFrame.MeasureSendMessageFrame(new string('\u0001', 1000)));
        Assert.Null(SessionHubFrame.CheckText(new string('\u0001', 80_000)));
        Assert.NotNull(SessionHubFrame.CheckText(new string('\u0001', 90_000)));
    }

    [Fact]
    public void CheckText_does_not_throw_on_lone_surrogates()
    {
        var size = SessionHubFrame.MeasureSendMessageFrame("a\ud800b\udc00c");
        Assert.True(size > 0);
        Assert.Null(SessionHubFrame.CheckText("a\ud800b\udc00c"));
    }

    [Fact]
    public void Over_limit_error_never_prints_the_limit_as_the_size()
    {
        var overhead = SessionHubFrame.MeasureSendMessageFrame("");
        // One byte over the frame limit (headroom ignored): must read as visibly over 512 KB.
        var text = new string('a', SessionHubLimits.MaxReceiveMessageBytes - overhead + 1);
        Assert.Equal("Message is too large to send (512.1 KB; limit 512 KB).", SessionHubFrame.CheckText(text));
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

            // Echo only short text: echoing a large message back is not needed and would only slow the test.
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
