// -----------------------------------------------------------------------
// <copyright file="DaemonClientSessionTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.AI;
using Netclaw.Actors.Protocol;
using Netclaw.Cli.Daemon;
using Netclaw.Cli.Tui;
using Netclaw.Configuration;
using Netclaw.Daemon.Gateway;
using Netclaw.Tools;
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
        var port = TestNetworkHelpers.GetBoundPort(host);

        await using var client = new DaemonClient($"http://127.0.0.1:{port}");

        // Create an initial session to get a known session ID
        var originalSessionId = await client.CreateSessionAsync(Netclaw.Actors.Channels.ChannelType.Tui, TestContext.Current.CancellationToken);
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

        var resumedSessionId = await client2.ResumeSessionAsync(originalSessionId, Netclaw.Actors.Channels.ChannelType.Tui, TestContext.Current.CancellationToken);

        // EnsureSession should return the same session ID, not create a new one
        Assert.Equal(originalSessionId, resumedSessionId);

        // Verify the session is functional — can send and receive messages
        await client2.SendAsync("hello-resumed", TestContext.Current.CancellationToken);

        await outputReceived.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task ChatViewModel_initial_resume_uses_one_session_attach()
    {
        using var host = await StartFakeHubAsync();
        var port = TestNetworkHelpers.GetBoundPort(host);
        var state = host.Services.GetRequiredService<FakeSessionState>();
        await using var seedClient = new DaemonClient($"http://127.0.0.1:{port}");
        var sessionId = await seedClient.CreateSessionAsync(
            Netclaw.Actors.Channels.ChannelType.Tui,
            TestContext.Current.CancellationToken);
        state.ResetEnsureCount();

        await using var client = new DaemonClient($"http://127.0.0.1:{port}");
        var navigation = new ChatNavigationState { ResumeSessionId = sessionId };
        using var viewModel = new ChatViewModel(
            client,
            TimeProvider.System,
            new ModelCapabilities { ModelId = "test-model" },
            navigation,
            new NetclawPaths());
        var attached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var subscription = viewModel.SessionIdDisplay.Subscribe(value =>
        {
            if (string.Equals(value, sessionId, StringComparison.Ordinal))
                attached.TrySetResult();
        });

        viewModel.OnActivated();
        await attached.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        Assert.Equal(1, state.EnsureCount);
    }

    [Fact]
    public async Task ChatViewModel_retains_the_resume_id_after_a_transient_attach_failure()
    {
        const string resumedSessionId = "signalr/resume-target";
        var requestedSessionIds = new List<string?>();
        var failFirstAttach = true;
        var transport = new FakeDaemonHubTransport
        {
            EnsureSessionResponder = args =>
            {
                var requested = args[0] as string;
                requestedSessionIds.Add(requested);
                if (failFirstAttach)
                {
                    failFirstAttach = false;
                    throw new IOException("test attach failure");
                }

                return new SessionEnsureResultDto(resumedSessionId, false);
            }
        };
        await using var client = new DaemonClient(
            "http://127.0.0.1:1",
            transport,
            reconnectDelays: [TimeSpan.Zero]);
        using var viewModel = new ChatViewModel(
            client,
            TimeProvider.System,
            new ModelCapabilities { ModelId = "test-model" },
            new ChatNavigationState { ResumeSessionId = resumedSessionId },
            new NetclawPaths());
        var attached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var subscription = viewModel.SessionIdDisplay.Subscribe(value =>
        {
            if (string.Equals(value, resumedSessionId, StringComparison.Ordinal))
                attached.TrySetResult();
        });

        viewModel.OnActivated();
        await attached.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        Assert.True(requestedSessionIds.Count >= 2);
        Assert.All(requestedSessionIds, requested => Assert.Equal(resumedSessionId, requested));
    }

    [Fact]
    public async Task RespondToInteractionAsync_invokes_hub_method()
    {
        using var host = await StartFakeHubAsync();
        var port = TestNetworkHelpers.GetBoundPort(host);
        var state = host.Services.GetRequiredService<FakeSessionState>();

        await using var client = new DaemonClient($"http://127.0.0.1:{port}");
        await client.CreateSessionAsync(Netclaw.Actors.Channels.ChannelType.Tui, TestContext.Current.CancellationToken);

        await client.RespondToInteractionAsync("call-1", ApprovalOptionKeys.ApproveOnce, TestContext.Current.CancellationToken);

        Assert.Equal(("call-1", ApprovalOptionKeys.ApproveOnce), state.LastInteractionResponse);
    }

    [Fact]
    public async Task RespondToInteractionAsync_supports_session_scope()
    {
        using var host = await StartFakeHubAsync();
        var port = TestNetworkHelpers.GetBoundPort(host);
        var state = host.Services.GetRequiredService<FakeSessionState>();

        await using var client = new DaemonClient($"http://127.0.0.1:{port}");
        await client.CreateSessionAsync(Netclaw.Actors.Channels.ChannelType.Tui, TestContext.Current.CancellationToken);

        await client.RespondToInteractionAsync("call-2", ApprovalOptionKeys.ApproveSession, TestContext.Current.CancellationToken);

        Assert.Equal(("call-2", ApprovalOptionKeys.ApproveSession), state.LastInteractionResponse);
    }

    [Fact]
    public async Task ChatViewModel_keeps_queue_head_until_approval_outcome_arrives()
    {
        var transport = new FakeDaemonHubTransport();
        await using var client = new DaemonClient(
            "http://127.0.0.1:1",
            transport,
            reconnectDelays: [TimeSpan.Zero]);
        using var viewModel = new ChatViewModel(
            client,
            TimeProvider.System,
            new ModelCapabilities { ModelId = "test-model" },
            new ChatNavigationState(),
            new NetclawPaths());
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var subscription = viewModel.SessionIdDisplay.Subscribe(value =>
        {
            if (string.Equals(value, "fake/session", StringComparison.Ordinal))
                ready.TrySetResult();
        });

        viewModel.OnActivated();
        await ready.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        var first = Approval("call-a", 1);
        var second = Approval("call-b", 2);
        viewModel.SeedPendingInteractionForTesting(first);
        viewModel.SeedPendingInteractionForTesting(second);

        await viewModel.SubmitInteractionOptionAsync(first.CallId, ApprovalOptionKeys.ApproveOnceLabel);

        Assert.Equal("call-a", viewModel.CurrentInteraction?.CallId.Value);
        Assert.Contains(transport.Invocations, invocation =>
            string.Equals(invocation.Method, "RespondToInteraction", StringComparison.Ordinal));

        transport.PushOutput(SessionOutputDtoMapper.ToDto(new ApprovalOutcomeOutput
        {
            SessionId = new SessionId("fake/session"),
            TimestampMs = 3,
            CallId = first.CallId,
            ToolName = first.ToolName,
            SelectedKey = ApprovalOptionKeys.ApproveOnceKey
        }));

        Assert.Equal("call-b", viewModel.CurrentInteraction?.CallId.Value);
        Assert.Equal("Approval required", viewModel.StatusMessage.Value);
    }

    [Fact]
    public async Task ChatViewModel_keeps_prompts_until_the_agent_pulls_each_identity()
    {
        var transport = new FakeDaemonHubTransport();
        await using var client = new DaemonClient(
            "http://127.0.0.1:1",
            transport,
            reconnectDelays: [TimeSpan.Zero]);
        using var viewModel = CreateViewModel(client);
        await ActivateAsync(viewModel);
        viewModel.IsGenerating.Value = true;
        viewModel.StatusMessage.Value = "Generating...";

        await Task.WhenAll(
            viewModel.SubmitAsync("prompt A", "tui:a"),
            viewModel.SubmitAsync("prompt B", "tui:b"),
            viewModel.SubmitAsync("prompt C", "tui:c"));

        var sends = transport.Invocations
            .Where(invocation => string.Equals(invocation.Method, "SendMessageWithId", StringComparison.Ordinal))
            .Select(invocation => (
                Id: Assert.IsType<string>(invocation.Args[1]),
                Text: Assert.IsType<string>(invocation.Args[2])))
            .ToList();
        Assert.Equal(
            [("tui:a", "prompt A"), ("tui:b", "prompt B"), ("tui:c", "prompt C")],
            sends);
        Assert.Equal(3, viewModel.QueuedTurnMessageCount.Value);
        Assert.Equal("Generating...", viewModel.StatusMessage.Value);

        transport.PushOutput(SessionOutputDtoMapper.ToDto(new TurnCompleted
        {
            SessionId = new SessionId("fake/session"),
            TimestampMs = 1,
            TurnNumber = new TurnNumber(1),
            Outcome = TurnOutcome.Completed
        }));

        Assert.Equal(3, viewModel.QueuedTurnMessageCount.Value);
        Assert.True(viewModel.IsGenerating.Value);
        Assert.Equal(3, transport.Invocations.Count(invocation =>
            string.Equals(invocation.Method, "SendMessageWithId", StringComparison.Ordinal)));

        transport.PushOutput(SessionOutputDtoMapper.ToDto(new UserMessagesPulledOutput
        {
            SessionId = new SessionId("fake/session"),
            TimestampMs = 2,
            BatchId = "other-client-batch",
            TurnId = new Netclaw.Actors.Protocol.TurnId("turn-2"),
            Messages = [new PulledUserMessage("other:message", "Other client prompt")]
        }));
        Assert.Equal(3, viewModel.QueuedTurnMessageCount.Value);

        transport.PushOutput(SessionOutputDtoMapper.ToDto(new UserMessagesPulledOutput
        {
            SessionId = new SessionId("fake/session"),
            TimestampMs = 3,
            BatchId = "batch-1",
            TurnId = new Netclaw.Actors.Protocol.TurnId("turn-2"),
            Messages =
            [
                new PulledUserMessage("tui:a", "prompt A"),
                new PulledUserMessage("tui:b", "prompt B")
            ]
        }));
        Assert.Equal(1, viewModel.QueuedTurnMessageCount.Value);

        transport.PushOutput(SessionOutputDtoMapper.ToDto(new UserMessagesPulledOutput
        {
            SessionId = new SessionId("fake/session"),
            TimestampMs = 4,
            BatchId = "batch-2",
            TurnId = new Netclaw.Actors.Protocol.TurnId("turn-2"),
            Messages = [new PulledUserMessage("tui:c", "prompt C")]
        }));
        Assert.Equal(0, viewModel.QueuedTurnMessageCount.Value);
        Assert.True(viewModel.IsGenerating.Value);
    }

    [Fact]
    public async Task ChatViewModel_retries_a_rejected_active_turn_prompt_without_loss()
    {
        var accepted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var sendAttempts = 0;
        var transport = new FakeDaemonHubTransport
        {
            VoidInvokeHook = (method, _, _) =>
            {
                if (!string.Equals(method, "SendMessage", StringComparison.Ordinal))
                    return Task.CompletedTask;

                if (Interlocked.Increment(ref sendAttempts) == 1)
                    throw new IOException("test rejection");

                accepted.TrySetResult();
                return Task.CompletedTask;
            }
        };
        await using var client = new DaemonClient(
            "http://127.0.0.1:1",
            transport,
            reconnectDelays: [TimeSpan.Zero]);
        using var viewModel = CreateViewModel(client);
        await ActivateAsync(viewModel);
        viewModel.IsGenerating.Value = true;

        await viewModel.SubmitAsync("retain this prompt");
        await accepted.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        var sends = transport.Invocations
            .Where(invocation => string.Equals(invocation.Method, "SendMessage", StringComparison.Ordinal))
            .Select(invocation => Assert.IsType<string>(invocation.Args[1]))
            .ToList();
        Assert.Equal(["retain this prompt", "retain this prompt"], sends);
        Assert.Equal(1, viewModel.QueuedTurnMessageCount.Value);
    }

    [Fact]
    public async Task ChatViewModel_retains_the_queue_head_when_a_reconnect_flush_fails()
    {
        var accepted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var sendAttempts = 0;
        var transport = new FakeDaemonHubTransport
        {
            VoidInvokeHook = (method, _, _) =>
            {
                if (!string.Equals(method, "SendMessage", StringComparison.Ordinal))
                    return Task.CompletedTask;

                if (Interlocked.Increment(ref sendAttempts) < 3)
                    throw new IOException("test rejection");

                accepted.TrySetResult();
                return Task.CompletedTask;
            }
        };
        await using var client = new DaemonClient(
            "http://127.0.0.1:1",
            transport,
            reconnectDelays: [TimeSpan.Zero]);
        using var viewModel = CreateViewModel(client);
        await ActivateAsync(viewModel);
        viewModel.IsGenerating.Value = true;

        await viewModel.SubmitAsync("retain the queue head");
        await accepted.Task.WaitAsync(TimeSpan.FromSeconds(8), TestContext.Current.CancellationToken);

        var sends = transport.Invocations
            .Where(invocation => string.Equals(invocation.Method, "SendMessage", StringComparison.Ordinal))
            .Select(invocation => Assert.IsType<string>(invocation.Args[1]))
            .ToList();
        Assert.Equal(
            ["retain the queue head", "retain the queue head", "retain the queue head"],
            sends);
        Assert.Equal(1, viewModel.QueuedTurnMessageCount.Value);
        Assert.True(viewModel.IsGenerating.Value);
    }

    private static ChatViewModel CreateViewModel(DaemonClient client) => new(
        client,
        TimeProvider.System,
        new ModelCapabilities { ModelId = "test-model" },
        new ChatNavigationState(),
        new NetclawPaths());

    private static async Task ActivateAsync(ChatViewModel viewModel)
    {
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var subscription = viewModel.SessionIdDisplay.Subscribe(value =>
        {
            if (string.Equals(value, "fake/session", StringComparison.Ordinal))
                ready.TrySetResult();
        });

        viewModel.OnActivated();
        await ready.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
    }

    private static ToolInteractionRequest Approval(string callId, long timestampMs) => new()
    {
        SessionId = new SessionId("fake/session"),
        TimestampMs = timestampMs,
        Kind = "approval",
        CallId = new ToolCallId(callId),
        ToolName = new ToolName("shell_execute"),
        DisplayText = $"inspect {callId}",
        Options =
        [
            new ToolInteractionOption(
                ApprovalOptionKeys.ApproveOnceKey,
                ApprovalOptionKeys.ApproveOnceLabel),
            new ToolInteractionOption(ApprovalOptionKeys.DenyKey, ApprovalOptionKeys.DenyLabel)
        ]
    };

    // port: 0 (default) lets Kestrel bind a free ephemeral port and hold it for the
    // host's lifetime; callers read the actual port back via TestNetworkHelpers.GetBoundPort.
    private static async Task<IHost> StartFakeHubAsync(int port = 0)
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

    private sealed class FakeSessionState
    {
        private readonly object _gate = new();
        private readonly HashSet<string> _sessions = [];
        private readonly Dictionary<string, string> _connectionSessions = [];
        public (string CallId, string SelectedKey)? LastInteractionResponse { get; private set; }
        public int EnsureCount { get; private set; }

        public SessionEnsureResultDto Ensure(string connectionId, string? sessionId)
        {
            lock (_gate)
            {
                EnsureCount++;
                if (!string.IsNullOrWhiteSpace(sessionId) && _sessions.Contains(sessionId))
                {
                    _connectionSessions[connectionId] = sessionId;
                    return new SessionEnsureResultDto(sessionId, false);
                }

                var created = $"signalr/{Guid.NewGuid():N}";
                _sessions.Add(created);
                _connectionSessions[connectionId] = created;
                return new SessionEnsureResultDto(created, true);
            }
        }

        public void ResetEnsureCount()
        {
            lock (_gate)
                EnsureCount = 0;
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
