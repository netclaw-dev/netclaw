// -----------------------------------------------------------------------
// <copyright file="DaemonClientReconnectTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Actors.Channels;
using Netclaw.Cli.Daemon;
using R3;
using Xunit;

namespace Netclaw.Cli.Tests.Cli;

/// <summary>
/// Deterministic tests for the <see cref="DaemonClient"/> reconnect and session
/// state machine. They drive the transport seam directly, so there is no socket,
/// no port, and no wall-clock race. These replace the end-to-end server-restart
/// scenario that hung Windows CI across six prior fixes.
/// </summary>
public sealed class DaemonClientReconnectTests
{
    private static readonly TimeSpan[] ImmediateDelays = [TimeSpan.Zero];

    [Fact]
    public async Task Drop_triggers_reconnect_and_reattaches_the_session()
    {
        var transport = new FakeDaemonHubTransport();
        await using var client = new DaemonClient(
            "http://localhost",
            transport,
            reconnectDelays: ImmediateDelays,
            rpcTimeout: TimeSpan.FromSeconds(5));

        await client.CreateSessionAsync(ChannelType.Tui, TestContext.Current.CancellationToken);
        Assert.Equal(1, transport.EnsureSessionCalls);

        var reconnected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var sub = client.ConnectionEvents.Subscribe(evt =>
        {
            if (evt.State is DaemonConnectionState.Connected
                && evt.Message.Contains("Reconnected", StringComparison.Ordinal))
                reconnected.TrySetResult();
        });

        transport.RaiseClosed();

        await reconnected.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        // The reconnect re-attached the session: create + one re-attach EnsureSession.
        Assert.Equal(2, transport.EnsureSessionCalls);
        Assert.True(client.IsConnected);
    }

    [Fact]
    public async Task Reconnect_that_never_succeeds_emits_terminal_Disconnected()
    {
        var transport = new FakeDaemonHubTransport();
        await using var client = new DaemonClient(
            "http://localhost",
            transport,
            reconnectDelays: ImmediateDelays,
            rpcTimeout: TimeSpan.FromSeconds(5));

        await client.CreateSessionAsync(ChannelType.Tui, TestContext.Current.CancellationToken);

        // Every reconnect StartAsync now fails. Zero backoff runs the whole
        // budget immediately, with no time to advance.
        transport.StartHook = _ => throw new InvalidOperationException("connection refused");

        var disconnected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var sub = client.ConnectionEvents.Subscribe(evt =>
        {
            if (evt.State is DaemonConnectionState.Disconnected)
                disconnected.TrySetResult();
        });

        transport.RaiseClosed();

        await disconnected.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        Assert.False(client.IsConnected);
    }

    [Fact]
    public async Task A_lost_RPC_response_faults_the_caller_instead_of_hanging()
    {
        var transport = new FakeDaemonHubTransport
        {
            // The server accepts the send but the response never arrives. The
            // RPC deadline must convert that into a fast fault, not a hang.
            VoidInvokeHook = async (method, _, token) =>
            {
                if (method == "SendMessage")
                    await HangUntilCancelled(token);
            }
        };

        await using var client = new DaemonClient(
            "http://localhost",
            transport,
            reconnectDelays: ImmediateDelays,
            rpcTimeout: TimeSpan.FromMilliseconds(200));

        await client.CreateSessionAsync(ChannelType.Tui, TestContext.Current.CancellationToken);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => client.SendAsync("hello", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Dispose_while_an_RPC_is_stuck_returns_promptly_and_faults_the_caller()
    {
        var transport = new FakeDaemonHubTransport
        {
            VoidInvokeHook = async (method, _, token) =>
            {
                if (method == "SendMessage")
                    await HangUntilCancelled(token);
            }
        };

        var client = new DaemonClient(
            "http://localhost",
            transport,
            reconnectDelays: ImmediateDelays,
            rpcTimeout: TimeSpan.FromSeconds(30));

        await client.CreateSessionAsync(ChannelType.Tui, TestContext.Current.CancellationToken);

        // This send parks in the owner on the stuck RPC.
        var pendingSend = client.SendAsync("hello", TestContext.Current.CancellationToken);

        // Dispose must cancel the in-flight RPC and return without waiting out
        // the 30s deadline.
        await client.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        await Assert.ThrowsAnyAsync<Exception>(() => pendingSend);
    }

    // Simulates a hub RPC whose response never arrives: it completes only when
    // its token is cancelled (by the RPC deadline or by dispose). This models a
    // lost response without a timer or a wall-clock delay.
    private static Task HangUntilCancelled(CancellationToken token)
    {
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        token.Register(() => tcs.TrySetCanceled(token));
        return tcs.Task;
    }
}
