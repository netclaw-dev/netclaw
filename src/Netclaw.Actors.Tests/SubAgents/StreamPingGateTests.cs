// -----------------------------------------------------------------------
// <copyright file="StreamPingGateTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Akka.Actor;
using Netclaw.Actors.Sessions.Pipelines;
using Netclaw.Actors.SubAgents;
using Netclaw.Configuration;
using Xunit;

namespace Netclaw.Actors.Tests.SubAgents;

/// <summary>
/// The ping gate throttles on the injected clock, not the wall clock. The thinking cap
/// depends on it: an answer update that falls inside the throttle window must still
/// reach the watchdog.
/// </summary>
public sealed class StreamPingGateTests
{
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(2);

    [Fact]
    public void Thinking_updates_inside_the_window_are_throttled_on_the_injected_clock()
    {
        var clock = new ManualClock();
        var gate = new StreamPingGate(clock, Interval);

        Assert.True(gate.TryPing(Update(StreamActivity.Thinking, first: true), out _));

        clock.Advance(TimeSpan.FromMilliseconds(500));
        Assert.False(gate.TryPing(Update(StreamActivity.Thinking), out _));

        clock.Advance(Interval);
        Assert.True(gate.TryPing(Update(StreamActivity.Thinking), out var activity));
        Assert.Equal(StreamActivity.Thinking, activity);
    }

    [Fact]
    public void Answer_update_inside_the_window_pings_at_once()
    {
        var clock = new ManualClock();
        var gate = new StreamPingGate(clock, Interval);
        Assert.True(gate.TryPing(Update(StreamActivity.Thinking, first: true), out _));

        clock.Advance(TimeSpan.FromMilliseconds(100));

        Assert.True(gate.TryPing(Update(StreamActivity.Answer), out var activity));
        Assert.Equal(StreamActivity.Answer, activity);
    }

    [Fact]
    public void Answer_update_after_an_answer_ping_is_reported_by_the_next_ping()
    {
        var clock = new ManualClock();
        var gate = new StreamPingGate(clock, Interval);
        Assert.True(gate.TryPing(Update(StreamActivity.Answer, first: true), out _));

        clock.Advance(TimeSpan.FromMilliseconds(100));
        Assert.False(gate.TryPing(Update(StreamActivity.Answer), out _));

        // The next due update is a keepalive, but the window saw an answer.
        clock.Advance(Interval);
        Assert.True(gate.TryPing(Update(StreamActivity.Keepalive), out var activity));
        Assert.Equal(StreamActivity.Answer, activity);
    }

    private static StreamUpdateClassification Update(StreamActivity activity, bool first = false)
        => new(activity, first);

    private sealed class ManualClock : ITimeProvider
    {
        public DateTimeOffset Now { get; private set; } = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

        public TimeSpan MonotonicClock => Now - new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

        public TimeSpan HighResMonotonicClock => MonotonicClock;

        public void Advance(TimeSpan by) => Now += by;
    }
}
