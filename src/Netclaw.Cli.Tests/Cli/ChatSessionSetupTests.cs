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

    private async Task<(ChatViewModel Chat, DaemonClient Client)> StartChatAsync(ChatNavigationState? navigation = null)
    {
        var client = new DaemonClient(
            "http://localhost", _transport, reconnectDelays: [TimeSpan.Zero], rpcTimeout: TimeSpan.FromSeconds(30));
        var chat = new ChatViewModel(
            client,
            TimeProvider.System,
            new ModelCapabilities { ModelId = "test-model" },
            navigation ?? new ChatNavigationState(),
            _paths);
        chat.OnActivated();
        await _firstSetupReached.Task.WaitAsync(Timeout, TestContext.Current.CancellationToken);
        return (chat, client);
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
        var statuses = new ConcurrentQueue<string>();
        using var subscription = chat.StatusMessage.Subscribe(statuses.Enqueue);

        await chat.SubmitAsync("A").WaitAsync(Timeout, TestContext.Current.CancellationToken);
        await chat.SubmitAsync("B").WaitAsync(Timeout, TestContext.Current.CancellationToken);
        _releaseFirstSetup.SetResult();
        await WaitForSendsAsync(1);

        // A closing message: it is only delivered if A is no longer first in line.
        await chat.SubmitAsync("closing").WaitAsync(Timeout, TestContext.Current.CancellationToken);
        await WaitForSendsAsync(1);

        Assert.Equal(["B", "closing"], _sent.ToArray());
        Assert.Equal(1, Volatile.Read(ref attemptsOfA));
        Assert.Contains(statuses, status => status.StartsWith("A message could not be sent and was dropped", StringComparison.Ordinal));
        // The first set-up, the one that delivered B, and the closing message's own EnsureSession
        // when it was sent directly. Nothing retries A, so there is no reconnect storm.
        Assert.InRange(_transport.EnsureSessionCalls, 2, 3);
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

    // A transient failure sending the initial hidden trigger (e.g. the connection drops during
    // the onboarding prompt) must NOT permanently drop it. The trigger is only consumed after a
    // successful send, so a later set-up pass retries it — otherwise the onboarding interview
    // never runs and the smoke tape 'init-redo-chat' hangs waiting for the assistant reply.
    [Fact]
    public async Task A_transient_trigger_send_failure_is_retried_on_the_next_setup()
    {
        var triggerAttempts = 0;
        var firstAttempt = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _sendGate = text =>
        {
            // The hidden trigger is the only message; fail its first send attempt only.
            if (text == "the-trigger")
            {
                if (Interlocked.Increment(ref triggerAttempts) == 1)
                {
                    firstAttempt.TrySetResult();
                    throw new IOException("the connection dropped during the trigger send");
                }
            }

            return Task.CompletedTask;
        };
        var navigation = new ChatNavigationState();
        navigation.StartOnboarding("the-trigger");
        var (chat, client) = await StartChatAsync(navigation);
        using var _ = chat;
        await using var __ = client;

        // Release the first set-up. The connect loop re-runs set-up after the failed send and
        // must re-send the trigger (the fix), not drop it. We wait for the first failed attempt
        // before counting sends, so the assertion below cannot race the retry.
        _releaseFirstSetup.SetResult();
        await firstAttempt.Task.WaitAsync(Timeout, TestContext.Current.CancellationToken);

        await WaitForSendsAsync(1);
        Assert.Equal(["the-trigger"], _sent.ToArray());
        Assert.True(triggerAttempts >= 2, $"Expected the trigger to be retried, but it was attempted {triggerAttempts} time(s).");
    }
}
