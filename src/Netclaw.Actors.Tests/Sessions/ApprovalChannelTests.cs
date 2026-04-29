// -----------------------------------------------------------------------
// <copyright file="ApprovalChannelTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Actors.Sessions;
using Netclaw.Tools;
using Xunit;

namespace Netclaw.Actors.Tests.Sessions;

public sealed class ApprovalChannelTests
{
    [Fact]
    public async Task WaitAndComplete_returns_decision()
    {
        var channel = new ApprovalChannel();

        var waitTask = channel.WaitForApprovalAsync(new ToolCallId("call-1"), TimeSpan.FromSeconds(30), CancellationToken.None);

        // Complete from another context (simulating actor mailbox)
        channel.Complete(new ToolCallId("call-1"), ApprovalDecision.ApprovedOnce);

        var result = await waitTask;
        Assert.Equal(ApprovalDecision.ApprovedOnce, result);
    }

    [Fact]
    public async Task WaitAndComplete_approve_always()
    {
        var channel = new ApprovalChannel();

        var waitTask = channel.WaitForApprovalAsync(new ToolCallId("call-2"), TimeSpan.FromSeconds(30), CancellationToken.None);
        channel.Complete(new ToolCallId("call-2"), ApprovalDecision.ApprovedAlways);

        Assert.Equal(ApprovalDecision.ApprovedAlways, await waitTask);
    }

    [Fact]
    public async Task WaitAndComplete_approve_session()
    {
        var channel = new ApprovalChannel();

        var waitTask = channel.WaitForApprovalAsync(new ToolCallId("call-2b"), TimeSpan.FromSeconds(30), CancellationToken.None);
        channel.Complete(new ToolCallId("call-2b"), ApprovalDecision.ApprovedSession);

        Assert.Equal(ApprovalDecision.ApprovedSession, await waitTask);
    }

    [Fact]
    public async Task WaitAndComplete_denied()
    {
        var channel = new ApprovalChannel();

        var waitTask = channel.WaitForApprovalAsync(new ToolCallId("call-3"), TimeSpan.FromSeconds(30), CancellationToken.None);
        channel.Complete(new ToolCallId("call-3"), ApprovalDecision.Denied);

        Assert.Equal(ApprovalDecision.Denied, await waitTask);
    }

    [Fact]
    public async Task Timeout_returns_TimedOut()
    {
        var channel = new ApprovalChannel();

        var result = await channel.WaitForApprovalAsync(new ToolCallId("call-timeout"), TimeSpan.FromMilliseconds(50), CancellationToken.None);

        Assert.Equal(ApprovalDecision.TimedOut, result);
    }

    [Fact]
    public async Task Cancellation_throws_operation_canceled()
    {
        var channel = new ApprovalChannel();
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));

        await Assert.ThrowsAsync<OperationCanceledException>(async () =>
            await channel.WaitForApprovalAsync(new ToolCallId("call-cancel"), TimeSpan.FromSeconds(30), cts.Token));
    }

    [Fact]
    public async Task Infinite_timeout_waits_until_completed()
    {
        var channel = new ApprovalChannel();

        var waitTask = channel.WaitForApprovalAsync(new ToolCallId("call-infinite"), Timeout.InfiniteTimeSpan, CancellationToken.None);
        channel.Complete(new ToolCallId("call-infinite"), ApprovalDecision.Denied);

        // If infinite-timeout were broken (e.g. timeout task fired immediately),
        // the wait would resolve to TimedOut instead of the Denied we just signaled.
        Assert.Equal(ApprovalDecision.Denied, await waitTask);
    }

    [Fact]
    public void Complete_unknown_callId_is_noop()
    {
        var channel = new ApprovalChannel();
        // Should not throw
        channel.Complete(new ToolCallId("nonexistent"), ApprovalDecision.Denied);
    }

    [Fact]
    public async Task Multiple_concurrent_waits()
    {
        var channel = new ApprovalChannel();

        var wait1 = channel.WaitForApprovalAsync(new ToolCallId("call-a"), TimeSpan.FromSeconds(30), CancellationToken.None);
        var wait2 = channel.WaitForApprovalAsync(new ToolCallId("call-b"), TimeSpan.FromSeconds(30), CancellationToken.None);

        channel.Complete(new ToolCallId("call-b"), ApprovalDecision.Denied);
        channel.Complete(new ToolCallId("call-a"), ApprovalDecision.ApprovedSession);

        Assert.Equal(ApprovalDecision.ApprovedSession, await wait1);
        Assert.Equal(ApprovalDecision.Denied, await wait2);
    }
}
