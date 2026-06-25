// -----------------------------------------------------------------------
// <copyright file="StreamingToolCallTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Microsoft.Extensions.Time.Testing;
using Netclaw.Actors.Sessions.Pipelines;
using Netclaw.Actors.Tests.Memory;
using Netclaw.Actors.Tests.Sessions;
using Netclaw.Tools;
using Xunit;

namespace Netclaw.Actors.Tests.Sessions.Pipelines;

/// <summary>
/// Unit tests for the streaming tool-call contract: the per-call
/// <see cref="StreamingToolWatchdog"/> and the <c>INetclawTool</c> default
/// streaming adapter. Time is virtualized with <see cref="FakeTimeProvider"/>
/// so the watchdog's timeout behavior is deterministic.
/// </summary>
public sealed class StreamingToolCallTests
{
    private static readonly ToolWatchdogBudget FiveSeconds = ToolWatchdogBudget.Flat(TimeSpan.FromSeconds(5));

    [Fact]
    public async Task First_item_budget_trips_when_no_item_arrives()
    {
        var time = new FakeTimeProvider();
        var task = StreamingToolWatchdog.ConsumeAsync(
            StallAsync(TestContext.Current.CancellationToken), "stall_tool", FiveSeconds, time, onActivity: null, TestContext.Current.CancellationToken);

        time.Advance(TimeSpan.FromSeconds(6));

        var ex = await Assert.ThrowsAsync<TimeoutException>(() => task);
        Assert.Contains("stall_tool", ex.Message);
    }

    [Fact]
    public async Task Inter_item_budget_trips_after_activity_then_stall()
    {
        var time = new FakeTimeProvider();
        var channel = Channel.CreateUnbounded<ToolCallUpdate>();
        var activitySeen = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var task = StreamingToolWatchdog.ConsumeAsync(
            channel.Reader.ReadAllAsync(TestContext.Current.CancellationToken),
            "stream_tool",
            new ToolWatchdogBudget(TimeSpan.FromSeconds(100), TimeSpan.FromSeconds(5)),
            time,
            onActivity: _ => activitySeen.TrySetResult(),
            TestContext.Current.CancellationToken);

        channel.Writer.TryWrite(new ToolActivityUpdate("working"));
        await activitySeen.Task;

        // The first item switched the budget to the tighter inter-item value;
        // the stream then goes silent.
        time.Advance(TimeSpan.FromSeconds(6));

        await Assert.ThrowsAsync<TimeoutException>(() => task);
    }

    [Fact]
    public async Task Healthy_stream_returns_the_terminal_result()
    {
        var time = new FakeTimeProvider();

        var result = await StreamingToolWatchdog.ConsumeAsync(
            CompletingAsync("done"), "ok_tool", FiveSeconds, time, onActivity: null, TestContext.Current.CancellationToken);

        Assert.Equal("done", result);
    }

    [Fact]
    public async Task Stream_without_a_completion_item_throws()
    {
        var time = new FakeTimeProvider();
        var task = StreamingToolWatchdog.ConsumeAsync(
            ActivityOnlyAsync(), "no_result_tool", FiveSeconds, time, onActivity: null, TestContext.Current.CancellationToken);

        await Assert.ThrowsAsync<InvalidOperationException>(() => task);
    }

    [Fact]
    public async Task Completion_item_is_terminal_even_if_iterator_does_not_finish()
    {
        var time = new FakeTimeProvider();

        var result = await StreamingToolWatchdog.ConsumeAsync(
            CompleteThenStallAsync(TestContext.Current.CancellationToken),
            "complete_then_stall_tool",
            FiveSeconds,
            time,
            onActivity: null,
            TestContext.Current.CancellationToken);

        Assert.Equal("done", result);
    }

    [Fact]
    public async Task Activity_within_budget_keeps_the_call_alive()
    {
        var time = new FakeTimeProvider();
        var channel = Channel.CreateUnbounded<ToolCallUpdate>();
        var activitySeen = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var task = StreamingToolWatchdog.ConsumeAsync(
            channel.Reader.ReadAllAsync(TestContext.Current.CancellationToken),
            "slow_tool",
            FiveSeconds,
            time,
            onActivity: _ => activitySeen.TrySetResult(),
            TestContext.Current.CancellationToken);

        // Activity at 3s (within the 5s budget) resets the watchdog...
        time.Advance(TimeSpan.FromSeconds(3));
        channel.Writer.TryWrite(new ToolActivityUpdate("still working"));
        await activitySeen.Task;

        // ...so another 3s — 6s total, but only 3s since the reset — does not trip it.
        time.Advance(TimeSpan.FromSeconds(3));
        channel.Writer.TryWrite(new ToolCompletedUpdate("finished"));
        channel.Writer.Complete();

        Assert.Equal("finished", await task);
    }

    [Fact]
    public async Task Wall_clock_budget_is_not_reset_by_activity()
    {
        var time = new FakeTimeProvider();
        var channel = Channel.CreateUnbounded<ToolCallUpdate>();
        var activitySeen = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var task = StreamingToolWatchdog.ConsumeAsync(
            channel.Reader.ReadAllAsync(TestContext.Current.CancellationToken),
            "chatty_tool",
            ToolWatchdogBudget.WallClock(TimeSpan.FromSeconds(5)),
            time,
            onActivity: _ => activitySeen.TrySetResult(),
            TestContext.Current.CancellationToken);

        time.Advance(TimeSpan.FromSeconds(3));
        channel.Writer.TryWrite(new ToolActivityUpdate("stdout", "."));
        await activitySeen.Task;

        time.Advance(TimeSpan.FromSeconds(3));

        var ex = await Assert.ThrowsAsync<TimeoutException>(() => task);
        Assert.Contains("exceeded execution budget", ex.Message);
    }

    [Fact]
    public async Task Concurrent_calls_are_bounded_independently()
    {
        var time = new FakeTimeProvider();

        var healthy = StreamingToolWatchdog.ConsumeAsync(
            CompletingAsync("healthy"), "healthy_tool", FiveSeconds, time, onActivity: null, TestContext.Current.CancellationToken);
        var stalled = StreamingToolWatchdog.ConsumeAsync(
            StallAsync(TestContext.Current.CancellationToken), "stalled_tool", FiveSeconds, time, onActivity: null, TestContext.Current.CancellationToken);

        // The healthy call returns its real result, unaffected by the stalled sibling.
        Assert.Equal("healthy", await healthy);

        time.Advance(TimeSpan.FromSeconds(6));
        await Assert.ThrowsAsync<TimeoutException>(() => stalled);
    }

    [Fact]
    public async Task Non_streaming_tool_yields_one_terminal_completion_item()
    {
        // A tool that does not override ExecuteStreamAsync inherits the
        // INetclawTool default: exactly one terminal completion item.
        INetclawTool tool = new FakeNetclawTool("greet", "hello there");

        var updates = new List<ToolCallUpdate>();
        await foreach (var update in tool.ExecuteStreamAsync(
            new Dictionary<string, object?>(), ToolExecutionContext.Empty, TestContext.Current.CancellationToken))
        {
            updates.Add(update);
        }

        var completed = Assert.IsType<ToolCompletedUpdate>(Assert.Single(updates));
        Assert.Equal("hello there", completed.Result);
    }

    private static async IAsyncEnumerable<ToolCallUpdate> StallAsync(
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        // The stream deliberately produces no item: the per-call watchdog is
        // the only thing that can end it.
        await TestStreamingHelpers.ParkUntilCancelledAsync(ct);
        yield break;
    }

    private static async IAsyncEnumerable<ToolCallUpdate> CompletingAsync(string result)
    {
        await Task.Yield();
        yield return new ToolActivityUpdate("working");
        yield return new ToolCompletedUpdate(result);
    }

    private static async IAsyncEnumerable<ToolCallUpdate> ActivityOnlyAsync()
    {
        await Task.Yield();
        yield return new ToolActivityUpdate("working");
    }

    private static async IAsyncEnumerable<ToolCallUpdate> CompleteThenStallAsync(
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        yield return new ToolCompletedUpdate("done");
        await TestStreamingHelpers.ParkUntilCancelledAsync(ct);
    }
}
