// -----------------------------------------------------------------------
// <copyright file="ProcessingWatchdogTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Akka.Actor;
using Netclaw.Actors.Sessions;
using Netclaw.Actors.Sessions.Handlers;
using Xunit;

namespace Netclaw.Actors.Tests.Sessions;

/// <summary>
/// Deterministic unit tests for the two-phase streaming watchdog policy shared by
/// the main session (<c>LlmSessionActor</c>) and sub-agent (<c>SubAgentActor</c>)
/// paths. Uses a recording <see cref="ITimerScheduler"/> so we assert exactly which
/// budget each transition arms — no wall-clock, no Task.Delay, no actor system.
/// </summary>
public sealed class ProcessingWatchdogTests
{
    private static readonly TimeSpan Prefill = TimeSpan.FromSeconds(1800);
    private static readonly TimeSpan InterDelta = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan NoProgress = TimeSpan.FromSeconds(1200);

    [Fact]
    public void Keepalive_before_first_token_refreshes_the_prefill_budget()
    {
        var (watchdog, timers) = StartedCall();

        // A content-free keepalive while no substantive token has arrived: must keep
        // the GENEROUS prefill budget, not shrink to the tighter inter-delta budget —
        // this is the regression a wedged-but-chatty prefill depends on.
        var promoted = watchdog.OnStreamProgress(
            isSubstantive: false, alreadyPromoted: false, Prefill, InterDelta, timers);

        Assert.False(promoted);
        Assert.Equal(Prefill, timers.LastSingleTimerTimeout);
    }

    [Fact]
    public void First_substantive_delta_promotes_to_the_inter_delta_budget()
    {
        var (watchdog, timers) = StartedCall();

        var promoted = watchdog.OnStreamProgress(
            isSubstantive: true, alreadyPromoted: false, Prefill, InterDelta, timers);

        Assert.True(promoted);
        Assert.Equal(InterDelta, timers.LastSingleTimerTimeout);
    }

    [Fact]
    public void Subsequent_substantive_delta_refreshes_the_inter_delta_budget()
    {
        var (watchdog, timers) = StartedCall();

        var promoted = watchdog.OnStreamProgress(
            isSubstantive: true, alreadyPromoted: true, Prefill, InterDelta, timers);

        Assert.True(promoted);
        Assert.Equal(InterDelta, timers.LastSingleTimerTimeout);
    }

    [Fact]
    public void Keepalive_after_promotion_refreshes_inter_delta_not_prefill()
    {
        var (watchdog, timers) = StartedCall();

        // Once promoted, even a keepalive stays on the tight inter-delta budget.
        var promoted = watchdog.OnStreamProgress(
            isSubstantive: false, alreadyPromoted: true, Prefill, InterDelta, timers);

        Assert.True(promoted);
        Assert.Equal(InterDelta, timers.LastSingleTimerTimeout);
    }

    [Fact]
    public void Start_with_a_deadline_arms_both_the_liveness_and_no_progress_timers()
    {
        var watchdog = new ProcessingWatchdog();
        var timers = new RecordingTimerScheduler();

        watchdog.Start(ProcessingWatchdog.LlmCall, Prefill, timers, NoProgress);

        Assert.Equal(Prefill, timers.LastLivenessTimeout);
        Assert.Equal(NoProgress, timers.LastNoProgressTimeout);
    }

    [Fact]
    public void Start_without_a_deadline_leaves_the_no_progress_timer_unarmed()
    {
        var watchdog = new ProcessingWatchdog();
        var timers = new RecordingTimerScheduler();

        watchdog.Start(ProcessingWatchdog.LlmCall, Prefill, timers);

        Assert.Equal(Prefill, timers.LastLivenessTimeout);
        Assert.Null(timers.LastNoProgressTimeout);
    }

    [Fact]
    public void Keepalive_does_not_reset_the_no_progress_deadline()
    {
        var (watchdog, timers) = StartedCallWithDeadline();

        // Keepalives refresh liveness but must NOT touch the no-progress deadline —
        // that is exactly what lets the deadline catch a heartbeat-only wedge that
        // refreshes the liveness timer forever.
        watchdog.OnStreamProgress(
            isSubstantive: false, alreadyPromoted: false, Prefill, InterDelta, timers);

        Assert.Equal(Prefill, timers.LastLivenessTimeout);
        Assert.Null(timers.LastNoProgressTimeout);
    }

    [Fact]
    public void Substantive_delta_resets_the_no_progress_deadline()
    {
        var (watchdog, timers) = StartedCallWithDeadline();

        // Real output is the only signal that resets the deadline.
        watchdog.OnStreamProgress(
            isSubstantive: true, alreadyPromoted: true, Prefill, InterDelta, timers);

        Assert.Equal(InterDelta, timers.LastLivenessTimeout);
        Assert.Equal(NoProgress, timers.LastNoProgressTimeout);
    }

    private static (ProcessingWatchdog, RecordingTimerScheduler) StartedCall()
    {
        var watchdog = new ProcessingWatchdog();
        var timers = new RecordingTimerScheduler();
        watchdog.Start(ProcessingWatchdog.LlmCall, Prefill, timers);
        // Clear the arm from Start() so each test asserts only what OnStreamProgress did.
        timers.Reset();
        return (watchdog, timers);
    }

    private static (ProcessingWatchdog, RecordingTimerScheduler) StartedCallWithDeadline()
    {
        var watchdog = new ProcessingWatchdog();
        var timers = new RecordingTimerScheduler();
        watchdog.Start(ProcessingWatchdog.LlmCall, Prefill, timers, NoProgress);
        timers.Reset();
        return (watchdog, timers);
    }

    /// <summary>
    /// Minimal <see cref="ITimerScheduler"/> that records the timeout of the most
    /// recent <c>StartSingleTimer</c> call, split by whether the scheduled message
    /// is the liveness timer or the keepalive-immune no-progress deadline
    /// (distinguished by <see cref="ProcessingWatchdogExpired.NoProgress"/>). All
    /// other members are no-ops.
    /// </summary>
    private sealed class RecordingTimerScheduler : ITimerScheduler
    {
        public TimeSpan? LastSingleTimerTimeout { get; private set; }
        public TimeSpan? LastLivenessTimeout { get; private set; }
        public TimeSpan? LastNoProgressTimeout { get; private set; }

        public void Reset()
        {
            LastSingleTimerTimeout = null;
            LastLivenessTimeout = null;
            LastNoProgressTimeout = null;
        }

        public void StartSingleTimer(object key, object msg, TimeSpan timeout)
        {
            LastSingleTimerTimeout = timeout;
            if (msg is ProcessingWatchdogExpired { NoProgress: true })
                LastNoProgressTimeout = timeout;
            else if (msg is ProcessingWatchdogExpired)
                LastLivenessTimeout = timeout;
        }

        public void StartSingleTimer(object key, object msg, TimeSpan timeout, IActorRef sender)
            => StartSingleTimer(key, msg, timeout);

        public void StartPeriodicTimer(object key, object msg, TimeSpan interval) { }
        public void StartPeriodicTimer(object key, object msg, TimeSpan interval, IActorRef sender) { }
        public void StartPeriodicTimer(object key, object msg, TimeSpan initialDelay, TimeSpan interval) { }
        public void StartPeriodicTimer(object key, object msg, TimeSpan initialDelay, TimeSpan interval, IActorRef sender) { }
        public void Cancel(object key) { }
        public void CancelAll() { }
        public bool IsTimerActive(object key) => LastSingleTimerTimeout is not null;
        public IReadOnlyCollection<object> ActiveTimers => [];
    }
}
