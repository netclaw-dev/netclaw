// -----------------------------------------------------------------------
// <copyright file="ChatSessionSetupTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Collections.Concurrent;
using Netclaw.Cli.Daemon;
using Netclaw.Cli.Tui;
using Netclaw.Configuration;
using Netclaw.Tests.Utilities;
using R3;
using Xunit;
using static Netclaw.Actors.Sessions.SessionProtocol;

namespace Netclaw.Cli.Tests.Cli;

/// <summary>
/// The chat sets its session up from three places that can overlap: the connect loop, the
/// Connected connection event and a submit that finds the chat not ready. Each test holds the
/// first session set-up open and lets the others pile up behind it, so the overlap is certain
/// and no test sleeps or polls.
/// </summary>
public sealed class ChatSessionSetupTests : IDisposable
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    private readonly DisposableTempDir _dir = new();
    private readonly NetclawPaths _paths;
    private readonly FakeDaemonHubTransport _transport = new();
    private readonly ConcurrentQueue<string> _sent = new();
    private readonly SemaphoreSlim _sentSignal = new(0);
    private readonly TaskCompletionSource _firstSetupReached = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _releaseFirstSetup = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _gated;

    // Awaited by a SendMessage RPC before it is recorded; a test uses it to hold or fail a send.
    private Func<string, Task>? _sendGate;

    public ChatSessionSetupTests()
    {
        _paths = new NetclawPaths(_dir.Path);
        _paths.EnsureDirectoriesExist();

        // The first EnsureSession RPC is held until the test releases it.
        _transport.EnsureSessionGate = async _ =>
        {
            if (Interlocked.Exchange(ref _gated, 1) == 0)
            {
                _firstSetupReached.TrySetResult();
                await _releaseFirstSetup.Task;
            }
        };
        _transport.VoidInvokeHook = async (method, args, _) =>
        {
            if (method == "SendMessage" && args.Length > 1 && args[1] is string text)
            {
                if (_sendGate is not null)
                    await _sendGate(text);

                _sent.Enqueue(text);
                _sentSignal.Release();
            }
        };
    }

    public void Dispose()
    {
        _releaseFirstSetup.TrySetResult();
        _sentSignal.Dispose();
        _dir.Dispose();
    }

    private (ChatViewModel Chat, DaemonClient Client) CreateChat(ChatNavigationState? navigation = null, TimeProvider? timeProvider = null)
    {
        var client = new DaemonClient(
            "http://localhost", _transport, reconnectDelays: [TimeSpan.Zero], rpcTimeout: TimeSpan.FromSeconds(30));
        var chat = new ChatViewModel(
            client,
            timeProvider ?? TimeProvider.System,
            new ModelCapabilities { ModelId = "test-model" },
            navigation ?? new ChatNavigationState(),
            _paths);
        return (chat, client);
    }

    private async Task<(ChatViewModel Chat, DaemonClient Client)> StartChatAsync(ChatNavigationState? navigation = null)
    {
        // A chat resuming a session binds it as soon as it connects, which gives these tests a
        // set-up to hold open. A new chat binds nothing until its first message.
        var (chat, client) = CreateChat(navigation ?? new ChatNavigationState { ResumeSessionId = "resumed/session" });
        chat.OnActivated();
        await _firstSetupReached.Task.WaitAsync(Timeout, TestContext.Current.CancellationToken);
        return (chat, client);
    }

    private const string NotDelivered = "A message could not be sent and was not delivered.";

    // The transcript lines the page renders for ErrorOutput; unlike the status bar they are not overwritten.
    private static ConcurrentQueue<string> SubscribeToNotices(ChatViewModel chat)
    {
        var notices = new ConcurrentQueue<string>();
        chat.SessionOutput.Subscribe(output =>
        {
            if (output is ErrorOutput error)
                notices.Enqueue(error.Message);
        });
        return notices;
    }

    private async Task WaitForSendsAsync(int count)
    {
        for (var i = 0; i < count; i++)
            Assert.True(await _sentSignal.WaitAsync(Timeout, TestContext.Current.CancellationToken), $"Only {i} of {count} messages were sent.");
    }

    [Fact]
    public async Task A_second_set_up_that_overlaps_the_first_does_nothing_and_the_trigger_is_sent_once()
    {
        var navigation = new ChatNavigationState();
        navigation.StartOnboarding("the-trigger");
        var (chat, client) = await StartChatAsync(navigation);
        using var _ = chat;
        await using var __ = client;

        // A further set-up starts while the first is held inside its EnsureSession RPC.
        var overlapping = chat.EnsureSessionAndFlushAsync();
        _releaseFirstSetup.SetResult();
        await overlapping.WaitAsync(Timeout, TestContext.Current.CancellationToken);
        await WaitForSendsAsync(1);

        await chat.SubmitAsync("closing-turn").WaitAsync(Timeout, TestContext.Current.CancellationToken);
        await WaitForSendsAsync(1);

        // One set-up RPC, then the trigger; the next EnsureSession belongs to the closing turn.
        Assert.Equal(["the-trigger", "closing-turn"], _sent.ToArray());
        Assert.Equal(
            ["EnsureSession", "SendMessage", "EnsureSession", "SendMessage"],
            _transport.Invocations.Select(call => call.Method).ToArray());
    }

    [Fact]
    public async Task Messages_submitted_during_the_set_up_arrive_once_and_in_order()
    {
        var (chat, client) = await StartChatAsync();
        using var _ = chat;
        await using var __ = client;

        await chat.SubmitAsync("m1").WaitAsync(Timeout, TestContext.Current.CancellationToken);
        await chat.SubmitAsync("m2").WaitAsync(Timeout, TestContext.Current.CancellationToken);
        await chat.SubmitAsync("m3").WaitAsync(Timeout, TestContext.Current.CancellationToken);
        _releaseFirstSetup.SetResult();

        await WaitForSendsAsync(3);
        await chat.SubmitAsync("sentinel").WaitAsync(Timeout, TestContext.Current.CancellationToken);
        await WaitForSendsAsync(1);

        Assert.Equal(["m1", "m2", "m3", "sentinel"], _sent.ToArray());
    }

    // A message that can never be sent (too large for the connection, say) must not stay first in
    // line: every reconnect would try it again and fail again. It is attempted once and dropped.
    [Fact]
    public async Task A_message_whose_send_always_fails_is_attempted_once_and_dropped_and_the_next_one_goes_out()
    {
        var attemptsOfA = 0;
        _sendGate = text =>
        {
            if (text == "A")
            {
                Interlocked.Increment(ref attemptsOfA);
                throw new IOException("the connection dropped during the send");
            }

            return Task.CompletedTask;
        };
        var (chat, client) = await StartChatAsync();
        using var _ = chat;
        await using var __ = client;
        var notices = SubscribeToNotices(chat);

        await chat.SubmitAsync("A").WaitAsync(Timeout, TestContext.Current.CancellationToken);
        await chat.SubmitAsync("B").WaitAsync(Timeout, TestContext.Current.CancellationToken);
        _releaseFirstSetup.SetResult();
        await WaitForSendsAsync(1);

        // A closing message: it is only delivered if A is no longer first in line.
        await chat.SubmitAsync("closing").WaitAsync(Timeout, TestContext.Current.CancellationToken);
        await WaitForSendsAsync(1);

        Assert.Equal(["B", "closing"], _sent.ToArray());
        Assert.Equal(1, Volatile.Read(ref attemptsOfA));
        Assert.Equal([NotDelivered], notices.ToArray());
        // The resume bind, plus the closing message's own EnsureSession when it was sent directly.
        // Nothing retries A, so there is no reconnect storm.
        Assert.InRange(_transport.EnsureSessionCalls, 1, 2);
    }

    [Fact]
    public async Task A_message_submitted_while_the_queue_is_being_flushed_waits_its_turn()
    {
        var firstSendReached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirstSend = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _sendGate = async text =>
        {
            if (text == "q1")
            {
                firstSendReached.TrySetResult();
                await releaseFirstSend.Task;
            }
        };
        var (chat, client) = await StartChatAsync();
        using var _ = chat;
        await using var __ = client;

        await chat.SubmitAsync("q1").WaitAsync(Timeout, TestContext.Current.CancellationToken);
        _releaseFirstSetup.SetResult();
        await firstSendReached.Task.WaitAsync(Timeout, TestContext.Current.CancellationToken);

        // The flush is in flight and q1 is being sent. The chat is not ready yet, so q2 queues behind
        // q1 and the submit returns at once; a chat that was already ready would send q2 directly.
        var submit = chat.SubmitAsync("q2");
        var queuedAtOnce = submit.IsCompleted;
        releaseFirstSend.SetResult(); // before the assertion, so a failure cannot leave the send held
        Assert.True(queuedAtOnce, "q2 was sent directly instead of queueing behind q1");
        await submit.WaitAsync(Timeout, TestContext.Current.CancellationToken);

        await WaitForSendsAsync(2);
        await chat.SubmitAsync("sentinel").WaitAsync(Timeout, TestContext.Current.CancellationToken);
        await WaitForSendsAsync(1);

        Assert.Equal(["q1", "q2", "sentinel"], _sent.ToArray());
    }

    [Fact]
    public async Task A_failed_send_does_not_throw_out_of_the_flush_and_leaves_the_chat_not_ready()
    {
        _sendGate = _ => throw new IOException("the connection dropped during the send");
        var navigation = new ChatNavigationState();
        navigation.StartOnboarding("the-trigger");
        var (chat, client) = CreateChat(navigation);
        using var _ = chat;
        await using var __ = client;
        var notices = SubscribeToNotices(chat);
        var noticeSessions = new ConcurrentQueue<string>();
        using var noticeSubscription = chat.SessionOutput.Subscribe(output =>
        {
            if (output is ErrorOutput error)
                noticeSessions.Enqueue(error.SessionId.Value);
        });
        var statuses = new ConcurrentQueue<string>();
        using var subscription = chat.StatusMessage.Subscribe(statuses.Enqueue);
        _releaseFirstSetup.SetResult();

        await chat.EnsureSessionAndFlushAsync().WaitAsync(Timeout, TestContext.Current.CancellationToken);

        Assert.Equal([NotDelivered], notices.ToArray());
        Assert.Equal(["fake/session"], noticeSessions.ToArray());
        Assert.DoesNotContain("Ready", statuses);

        // Not ready, so the next message queues instead of being sent past the failure.
        _sendGate = null;
        await chat.SubmitAsync("next").WaitAsync(Timeout, TestContext.Current.CancellationToken);
        Assert.Contains(statuses, status => status.StartsWith("Queued 1 message(s)", StringComparison.Ordinal));
        await WaitForSendsAsync(1);
        Assert.Equal(["next"], _sent.ToArray());
    }

    [Fact]
    public async Task A_failed_send_that_closes_the_connection_drops_that_message_and_delivers_the_rest_once_after_the_reconnect()
    {
        var attemptsOfA = 0;
        _sendGate = text =>
        {
            if (text == "A")
            {
                Interlocked.Increment(ref attemptsOfA);
                _transport.RaiseClosed(new IOException("the connection closed during the send"));
                throw new IOException("the connection closed during the send");
            }

            return Task.CompletedTask;
        };
        var (chat, client) = await StartChatAsync();
        using var _ = chat;
        await using var __ = client;
        var notices = SubscribeToNotices(chat);

        await chat.SubmitAsync("A").WaitAsync(Timeout, TestContext.Current.CancellationToken);
        await chat.SubmitAsync("B").WaitAsync(Timeout, TestContext.Current.CancellationToken);
        await chat.SubmitAsync("C").WaitAsync(Timeout, TestContext.Current.CancellationToken);
        _releaseFirstSetup.SetResult();

        // B and C go out when the client reconnects and reports Connected.
        await WaitForSendsAsync(2);
        await chat.SubmitAsync("closing").WaitAsync(Timeout, TestContext.Current.CancellationToken);
        await WaitForSendsAsync(1);

        Assert.Equal(["B", "C", "closing"], _sent.ToArray());
        Assert.Equal(1, Volatile.Read(ref attemptsOfA));
        Assert.Equal([NotDelivered], notices.ToArray());
    }

    [Fact]
    public async Task A_send_that_fails_while_the_chat_is_ready_is_queued_again_and_sent_once()
    {
        var failures = 0;
        _sendGate = text =>
        {
            if (text == "X" && Interlocked.Increment(ref failures) == 1)
                throw new IOException("the connection dropped during the send");

            return Task.CompletedTask;
        };
        var (chat, client) = await StartChatAsync();
        using var _ = chat;
        await using var __ = client;

        await chat.SubmitAsync("m1").WaitAsync(Timeout, TestContext.Current.CancellationToken);
        _releaseFirstSetup.SetResult();
        await WaitForSendsAsync(1);
        await chat.EnsureSessionAndFlushAsync().WaitAsync(Timeout, TestContext.Current.CancellationToken); // the chat is ready

        // Ready, so X is sent directly; that send fails and X goes back in the queue.
        await chat.SubmitAsync("X").WaitAsync(Timeout, TestContext.Current.CancellationToken);
        await WaitForSendsAsync(1);

        await chat.SubmitAsync("closing").WaitAsync(Timeout, TestContext.Current.CancellationToken);
        await WaitForSendsAsync(1);

        Assert.Equal(["m1", "X", "closing"], _sent.ToArray());
        Assert.Equal(2, Volatile.Read(ref failures));
    }

    [Fact]
    public async Task A_set_up_whose_chat_is_disposed_as_it_finishes_completes_normally()
    {
        var (chat, client) = CreateChat();
        await using var _ = client;
        _releaseFirstSetup.SetResult();

        // "Ready" is the last status the set-up writes; the chat goes away while that write is being
        // delivered, so the set-up reaches its release with the gate already disposed.
        using var subscription = chat.StatusMessage.Subscribe(status =>
        {
            if (status == "Ready")
                chat.Dispose();
        });

        await chat.EnsureSessionAndFlushAsync().WaitAsync(Timeout, TestContext.Current.CancellationToken);
    }

    // Opening the chat and quitting without typing must not leave an empty session on the daemon.
    [Fact]
    public async Task A_new_chat_creates_its_session_with_the_first_message_and_not_when_it_opens()
    {
        _releaseFirstSetup.SetResult(); // nothing here holds a set-up open
        var (chat, client) = CreateChat();
        using var _ = chat;
        await using var __ = client;
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var statuses = new ConcurrentQueue<string>();
        using var subscription = chat.StatusMessage.Subscribe(status =>
        {
            statuses.Enqueue(status);
            if (status == "Ready")
                ready.TrySetResult();
        });

        chat.OnActivated();
        await ready.Task.WaitAsync(Timeout, TestContext.Current.CancellationToken);

        Assert.Equal(0, _transport.EnsureSessionCalls);
        Assert.Null(chat.SessionIdDisplay.Value);

        await chat.SubmitAsync("first").WaitAsync(Timeout, TestContext.Current.CancellationToken);
        await WaitForSendsAsync(1);

        Assert.Equal(["first"], _sent.ToArray());
        Assert.Equal(1, _transport.EnsureSessionCalls);
        Assert.Equal("fake/session", chat.SessionIdDisplay.Value);
        Assert.True(File.Exists(Path.Combine(_paths.LogsDirectory, "signalr-fake-session.log")), "the per-session usage log was not opened");
        Assert.DoesNotContain(statuses, status => status.StartsWith("Send failed", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_message_typed_before_a_new_chat_has_connected_creates_the_session_and_arrives_once()
    {
        _releaseFirstSetup.SetResult(); // nothing here holds a set-up open
        var connectionHeld = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _transport.StartHook = _ => connectionHeld.Task;
        var (chat, client) = CreateChat();
        using var _ = chat;
        await using var __ = client;
        chat.OnActivated();

        await chat.SubmitAsync("typed-early").WaitAsync(Timeout, TestContext.Current.CancellationToken);
        connectionHeld.SetResult();
        await WaitForSendsAsync(1);

        await chat.SubmitAsync("sentinel").WaitAsync(Timeout, TestContext.Current.CancellationToken);
        await WaitForSendsAsync(1);

        Assert.Equal(["typed-early", "sentinel"], _sent.ToArray());
        Assert.Equal("fake/session", chat.SessionIdDisplay.Value);
    }

    [Fact]
    public async Task A_resumed_chat_attaches_to_its_session_when_it_opens()
    {
        var (chat, client) = await StartChatAsync(new ChatNavigationState { ResumeSessionId = "existing/session" });
        using var _ = chat;
        await using var __ = client;
        _releaseFirstSetup.SetResult();

        await chat.SubmitAsync("hello").WaitAsync(Timeout, TestContext.Current.CancellationToken);
        await WaitForSendsAsync(1);

        Assert.Equal("existing/session", _transport.Invocations.First(call => call.Method == "EnsureSession").Args[0]);
        Assert.Equal("existing/session", chat.SessionIdDisplay.Value);
        Assert.All(
            _transport.Invocations.Where(call => call.Method == "EnsureSession"),
            call => Assert.Equal("existing/session", call.Args[0]));
    }

    [Fact]
    public async Task An_idle_new_chat_reconnects_after_a_drop_and_still_creates_no_session()
    {
        _releaseFirstSetup.SetResult();
        var (chat, client) = CreateChat();
        using var _ = chat;
        await using var __ = client;
        var dropSeen = false;
        var readyAgain = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var subscription = chat.StatusMessage.Subscribe(status =>
        {
            if (status.Contains("dropped", StringComparison.Ordinal))
                dropSeen = true;
            else if (dropSeen && status == "Ready")
                readyAgain.TrySetResult();
        });
        var connected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var connectedSubscription = client.ConnectionEvents.Subscribe(evt =>
        {
            if (evt.State is DaemonConnectionState.Connected)
                connected.TrySetResult();
        });
        chat.OnActivated();
        await connected.Task.WaitAsync(Timeout, TestContext.Current.CancellationToken);

        // The daemon restarts. A new chat has no session for DaemonClient to re-attach, so the
        // chat has to reconnect by itself.
        _transport.RaiseClosed(new IOException("daemon restarted"));

        await readyAgain.Task.WaitAsync(Timeout, TestContext.Current.CancellationToken);
        Assert.True(_transport.IsConnected);
        Assert.Equal(0, _transport.EnsureSessionCalls);
    }

    [Fact]
    public async Task A_resumed_chat_whose_first_attach_fails_attaches_on_the_retry()
    {
        var attempts = 0;
        _transport.EnsureSessionGate = _ =>
            Interlocked.Increment(ref attempts) == 1 ? throw new IOException("the attach failed") : Task.CompletedTask;
        var (chat, client) = CreateChat(new ChatNavigationState { ResumeSessionId = "existing/session" });
        using var _ = chat;
        await using var __ = client;
        var attached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var subscription = chat.SessionIdDisplay.Subscribe(id =>
        {
            if (id is not null)
                attached.TrySetResult();
        });

        chat.OnActivated();
        await attached.Task.WaitAsync(Timeout, TestContext.Current.CancellationToken);

        Assert.Equal("existing/session", chat.SessionIdDisplay.Value);
        Assert.All(
            _transport.Invocations.Where(call => call.Method == "EnsureSession"),
            call => Assert.Equal("existing/session", call.Args[0]));
    }

    [Fact]
    public async Task A_new_chat_that_is_connected_and_idle_shows_Ready_as_its_last_status()
    {
        _releaseFirstSetup.SetResult();
        var connectionHeld = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _transport.StartHook = _ => connectionHeld.Task;
        var (chat, client) = CreateChat();
        using var _ = chat;
        await using var __ = client;
        chat.OnActivated();

        // Subscribed after the chat, so it sees the Connected event once the chat's own handler has run.
        var connected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var subscription = client.ConnectionEvents.Subscribe(evt =>
        {
            if (evt.State is DaemonConnectionState.Connected)
                connected.TrySetResult();
        });
        connectionHeld.SetResult();
        await connected.Task.WaitAsync(Timeout, TestContext.Current.CancellationToken);
        await chat.EnsureSessionAndFlushAsync().WaitAsync(Timeout, TestContext.Current.CancellationToken); // any flush in flight is over

        Assert.Equal("Ready", chat.StatusMessage.Value);
    }

    [Fact]
    public async Task A_chat_disposed_while_its_first_message_binds_still_sends_it_and_does_not_throw()
    {
        _releaseFirstSetup.SetResult();
        var (chat, client) = CreateChat();
        await using var _ = client;
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var subscription = chat.StatusMessage.Subscribe(status =>
        {
            if (status == "Ready")
                ready.TrySetResult();
        });
        chat.OnActivated();
        await ready.Task.WaitAsync(Timeout, TestContext.Current.CancellationToken);

        var bindReached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseBind = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _transport.EnsureSessionGate = async _ =>
        {
            bindReached.TrySetResult();
            await releaseBind.Task;
        };

        // Enter, then an immediate quit: Dispose has to wait for the send, because the host tears the
        // connection down as soon as it returns.
        var submit = chat.SubmitAsync("typed-then-quit");
        await bindReached.Task.WaitAsync(Timeout, TestContext.Current.CancellationToken);
        var releaser = Task.Run(() =>
        {
            // Dispose blocks its caller, so the bind is released from here once Dispose is waiting;
            // there is nothing to observe, so it is a bounded wait.
            using var dispose = new ManualResetEventSlim(false);
            dispose.Wait(TimeSpan.FromMilliseconds(100));
            releaseBind.SetResult();
        }, TestContext.Current.CancellationToken);
        chat.Dispose();
        await client.DisposeAsync();

        await releaser;
        await submit.WaitAsync(Timeout, TestContext.Current.CancellationToken);
        Assert.Equal(["typed-then-quit"], _sent.ToArray());
        Assert.Empty(Directory.GetFiles(_paths.LogsDirectory, "signalr-*.log"));
    }

    // A submit and the flush race on a chat that has no session yet. The interleavings are too fine to
    // script (the window is a few instructions), so this runs the race many times; before the fix a run
    // of this size strands a message or sends one to no session in about one iteration in sixty.
    [Fact]
    public async Task A_submit_racing_the_flush_of_a_new_chat_is_sent_once_to_a_session()
    {
        _releaseFirstSetup.SetResult();
        const int iterations = 1000;
        int stranded = 0, notDelivered = 0, duplicated = 0, unbound = 0;

        for (var i = 0; i < iterations; i++)
        {
            var transport = new FakeDaemonHubTransport();
            var sent = new ConcurrentQueue<object?>();
            transport.VoidInvokeHook = (method, args, _) =>
            {
                if (method == "SendMessage")
                    sent.Enqueue(args[0]);
                return Task.CompletedTask;
            };
            var client = new DaemonClient(
                "http://localhost", transport, reconnectDelays: [TimeSpan.Zero], rpcTimeout: TimeSpan.FromSeconds(30));
            using var chat = new ChatViewModel(
                client, TimeProvider.System, new ModelCapabilities { ModelId = "test-model" }, new ChatNavigationState(), _paths);
            var notices = SubscribeToNotices(chat);
            await client.ConnectAsync(TestContext.Current.CancellationToken);

            using var start = new ManualResetEventSlim(false);
            var spin = i % 7;
            var ct = TestContext.Current.CancellationToken;
            var flush = Task.Run(async () =>
            {
                start.Wait(ct);
                await chat.EnsureSessionAndFlushAsync();
            }, ct);
            var submit = Task.Run(async () =>
            {
                start.Wait(ct);
                for (var yields = 0; yields < spin; yields++)
                    await Task.Yield();

                await chat.SubmitAsync("m");
            }, ct);
            start.Set();
            await Task.WhenAll(flush, submit).WaitAsync(Timeout, TestContext.Current.CancellationToken);

            // A message that is not here in half a second is stranded, not slow.
            var deadline = Environment.TickCount64 + 500;
            while (sent.IsEmpty && notices.IsEmpty && Environment.TickCount64 < deadline)
                await Task.Yield();

            if (sent.IsEmpty && notices.IsEmpty)
                stranded++;
            notDelivered += notices.Count;
            if (sent.Count > 1)
                duplicated++;
            if (sent.Any(sessionId => sessionId is null))
                unbound++;

            await client.DisposeAsync();
        }

        Assert.Equal((0, 0, 0, 0), (stranded, notDelivered, duplicated, unbound));
    }

    // Every EnsureSession on a known session makes the daemon send the session-joined output again,
    // which the page prints as "Session started". A first message that cannot be sent used to bind
    // twice (the submit, then the flush that retries it).
    [Fact]
    public async Task A_first_message_that_cannot_be_sent_binds_the_session_once()
    {
        _releaseFirstSetup.SetResult();
        _sendGate = _ => throw new IOException("the message is over the connection limit");
        var (chat, client) = CreateChat();
        using var _ = chat;
        await using var __ = client;
        var notDelivered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var subscription = chat.SessionOutput.Subscribe(output =>
        {
            if (output is ErrorOutput)
                notDelivered.TrySetResult();
        });
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var readySubscription = chat.StatusMessage.Subscribe(status =>
        {
            if (status == "Ready")
                ready.TrySetResult();
        });
        chat.OnActivated();
        await ready.Task.WaitAsync(Timeout, TestContext.Current.CancellationToken);

        await chat.SubmitAsync("over-the-limit").WaitAsync(Timeout, TestContext.Current.CancellationToken);
        await notDelivered.Task.WaitAsync(Timeout, TestContext.Current.CancellationToken);

        Assert.Equal(1, _transport.EnsureSessionCalls);
    }

    [Fact]
    public async Task A_failing_bind_retries_with_a_growing_back_off_in_a_single_loop()
    {
        _releaseFirstSetup.SetResult();
        var time = new Microsoft.Extensions.Time.Testing.FakeTimeProvider();
        _transport.EnsureSessionGate = _ => throw new IOException("the bind failed");
        var (chat, client) = CreateChat(timeProvider: time);
        using var _ = chat;
        await using var __ = client;
        chat.OnActivated();
        await chat.SubmitAsync("a");
        await chat.SubmitAsync("b");
        await chat.SubmitAsync("c");

        var deadline = Environment.TickCount64 + 10_000;
        while (chat.StatusMessage.Value != "Connecting... retry 5 in 10s" && Environment.TickCount64 < deadline)
        {
            time.Advance(TimeSpan.FromSeconds(10));
            await Task.Yield();
        }

        // One loop, one attempt per back-off step; the connect event adds one flush of its own.
        Assert.Equal("Connecting... retry 5 in 10s", chat.StatusMessage.Value);
        Assert.InRange(_transport.EnsureSessionCalls, 5, 6);
    }

    [Fact]
    public async Task A_message_is_not_taken_out_of_the_queue_before_the_session_is_bound()
    {
        _releaseFirstSetup.SetResult();
        var time = new Microsoft.Extensions.Time.Testing.FakeTimeProvider();
        var failures = 0;
        _transport.EnsureSessionGate = _ =>
            Interlocked.Increment(ref failures) == 1 ? throw new IOException("the bind failed") : Task.CompletedTask;
        var (chat, client) = CreateChat(timeProvider: time);
        using var _ = chat;
        await using var __ = client;
        chat.OnActivated();
        await chat.SubmitAsync("a");
        await chat.SubmitAsync("b");

        var deadline = Environment.TickCount64 + 10_000;
        while (_sent.Count < 2 && Environment.TickCount64 < deadline)
        {
            time.Advance(TimeSpan.FromSeconds(10));
            await Task.Yield();
        }

        Assert.Equal(["a", "b"], _sent.ToArray());
    }

    [Fact]
    public async Task A_connect_after_a_silent_drop_re_attaches_the_session()
    {
        _releaseFirstSetup.SetResult();
        var (chat, client) = CreateChat();
        using var _ = chat;
        await using var __ = client;
        await client.ResumeSessionAsync("existing/session", DaemonClient.TuiChannelType, TestContext.Current.CancellationToken);

        // The send killed the connection and the foreground reconnect got there before the drop
        // notification was processed, so DaemonClient's own reconnect has nothing to do.
        _transport.SetConnected(false);
        await client.ConnectAsync(TestContext.Current.CancellationToken);

        Assert.Equal(2, _transport.StartAttempts);
        Assert.Equal(2, _transport.EnsureSessionCalls); // the resume, then the re-attach on the new connection
        Assert.Equal("existing/session", _transport.Invocations[^1].Args[0]);
    }
}
