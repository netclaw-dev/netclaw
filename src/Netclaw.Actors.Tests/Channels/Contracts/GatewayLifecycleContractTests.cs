// -----------------------------------------------------------------------
// <copyright file="GatewayLifecycleContractTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Akka.Actor;
using Akka.Configuration;
using Akka.Event;
using Akka.Hosting;
using Akka.Hosting.TestKit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace Netclaw.Actors.Tests.Channels.Contracts;

/// <summary>
/// Channel-neutral projection of a gateway lifecycle snapshot
/// (<c>DiscordGatewaySnapshot</c> / <c>MattermostGatewaySnapshot</c>) so the
/// contract can assert on connection state without knowing the channel type.
/// </summary>
public sealed record LifecycleSnapshotView(bool IsConnected, bool IsReady, string? HealthDetail);

/// <summary>
/// Behavioral contract for the gateway lifecycle state machine
/// (Disconnected → Connecting → Ready, CleanReconnectRequired, retry with
/// backoff). Each channel with a transport lifecycle actor provides a fixture
/// wiring its actor to a controllable fake transport and recording event sink.
/// Slack has no lifecycle actor (socket-mode client manages its own
/// connection) and is intentionally excluded.
///
/// Tests run on a virtual-time scheduler (<see cref="Akka.TestKit.TestScheduler"/>)
/// so retry and ready-timeout timers are driven deterministically via
/// <see cref="AdvanceScheduler"/> instead of real-time waits.
/// </summary>
public abstract class GatewayLifecycleContractTests : TestKit
{
    protected GatewayLifecycleContractTests(ITestOutputHelper output) : base(output: output)
    {
    }

    protected override Config? Config => ConfigurationFactory.ParseString("""
        akka.test.default-timeout = 5s
        akka.scheduler.implementation = "Akka.TestKit.TestScheduler, Akka.TestKit"
        """);

    protected override void ConfigureServices(HostBuilderContext context, IServiceCollection services)
    {
    }

    protected override void ConfigureAkka(AkkaConfigurationBuilder builder, IServiceProvider provider)
    {
    }

    /// <summary>
    /// Moves virtual time forward, synchronously delivering any scheduler
    /// items (retry timers, ready timeouts) that fall due.
    /// </summary>
    protected void AdvanceScheduler(TimeSpan offset) =>
        ((Akka.TestKit.TestScheduler)Sys.Scheduler).Advance(offset);

    /// <summary>
    /// Advances the fixture's fake wall clock (<see cref="TimeProvider"/>),
    /// which drives the backoff-reset stability window. Independent of
    /// <see cref="AdvanceScheduler"/>: timers fire on virtual scheduler time,
    /// stability is measured on the fake clock.
    /// </summary>
    protected abstract void AdvanceTimeProvider(TimeSpan offset);

    /// <summary>
    /// Creates the lifecycle actor wired to a fresh fake transport and
    /// recording event sink (both stored on the fixture for the hooks below).
    /// </summary>
    protected abstract IActorRef CreateLifecycleActor();

    /// <summary>
    /// Asks the actor for its current snapshot, normalized to the
    /// channel-neutral view. Also serves as a mailbox barrier: when the ask
    /// completes, every message told to the actor beforehand has been processed.
    /// </summary>
    protected abstract Task<LifecycleSnapshotView> GetSnapshotAsync(IActorRef actor);

    /// <summary>
    /// Drives a full connect — including the channel's ready signal (explicit
    /// READY event for Discord, successful StartAsync for Mattermost) — and
    /// returns the snapshot the connect ask completed with.
    /// </summary>
    protected abstract Task<LifecycleSnapshotView> ConnectAsync(IActorRef actor);

    /// <summary>Drives an operator-requested disconnect to completion.</summary>
    protected abstract Task DisconnectAsync(IActorRef actor);

    /// <summary>
    /// Raises a runtime transport drop while the actor is Ready and drives the
    /// actor to the point where it has requested a clean reconnect — advancing
    /// virtual time if the channel defers that decision behind a ready timeout
    /// (Discord waits 30s for Discord.Net's internal reconnect to re-emit READY).
    /// </summary>
    protected abstract Task RaiseRuntimeDisconnectAsync(IActorRef actor, string reason);

    /// <summary>
    /// Fires the transport's connected/ready signal — with the transport
    /// claiming to be connected — without the actor having initiated a connect.
    /// </summary>
    protected abstract Task RaiseSpuriousReadySignalAsync();

    /// <summary>Raises an inbound message event on the fake transport.</summary>
    protected abstract Task RaiseIngressEventAsync();

    /// <summary>Ingress messages the actor forwarded to the event sink.</summary>
    protected abstract int ForwardedIngressCount { get; }

    /// <summary>Clean-reconnect-required events published to the event sink.</summary>
    protected abstract int CleanReconnectCount { get; }

    /// <summary>How many times the event sink has published ConnectionRestored.</summary>
    protected abstract int ConnectionRestoredCount { get; }

    /// <summary>StartAsync calls observed on the fake transport.</summary>
    protected abstract int TransportStartCount { get; }

    /// <summary>
    /// The health detail the actor reports once a stop has fully completed
    /// (e.g. "Discord gateway disconnected."). Set in the same message
    /// handling that registers the auto-reconnect retry timer, so asserting
    /// on it doubles as a barrier for the retry being scheduled.
    /// </summary>
    protected abstract string DisconnectedHealthDetail { get; }

    /// <summary>Asserts every transport event has exactly one subscriber.</summary>
    protected abstract void AssertSingleTransportSubscription();

    /// <summary>Asserts every transport event has zero subscribers.</summary>
    protected abstract void AssertNoTransportSubscriptions();

    /// <summary>
    /// Makes the fake transport's StopAsync pend until
    /// <see cref="ReleaseTransportStop"/>, leaving the transport claiming
    /// "connected" while the actor is mid-teardown.
    /// </summary>
    protected abstract void DeferTransportStop();

    /// <summary>Completes a deferred stop and marks the transport disconnected.</summary>
    protected abstract void ReleaseTransportStop();

    [Fact]
    public async Task Not_ready_ingress_is_dropped()
    {
        var actor = CreateLifecycleActor();
        await GetSnapshotAsync(actor);
        AssertSingleTransportSubscription();

        await RaiseIngressEventAsync();
        await GetSnapshotAsync(actor);

        Assert.Equal(0, ForwardedIngressCount);
    }

    [Fact]
    public async Task Runtime_disconnect_reports_not_ready_and_requests_clean_reconnect()
    {
        var actor = CreateLifecycleActor();
        var ready = await ConnectAsync(actor);
        Assert.True(ready.IsReady);

        await RaiseRuntimeDisconnectAsync(actor, "network lost");

        // The actor emits CleanReconnectRequired, then drives a full
        // stop/reconnect cycle and lands in Disconnected. The auto-reconnect
        // retry is scheduled on the virtual-time scheduler, so the state is
        // stable until the test advances time.
        await AwaitCleanReconnectSettledAsync(actor);
    }

    [Fact]
    public async Task Spurious_ready_signal_while_disconnected_triggers_clean_reconnect()
    {
        var actor = CreateLifecycleActor();
        await GetSnapshotAsync(actor);

        await RaiseSpuriousReadySignalAsync();

        // A connected/ready signal outside a clean startup cycle must force a
        // clean reconnect: emit the event, then drive a full stop so the
        // transport ends up disconnected.
        await AwaitAssertAsync(async () =>
        {
            var snapshot = await GetSnapshotAsync(actor);
            Assert.False(snapshot.IsReady);
            Assert.False(snapshot.IsConnected);
            Assert.Equal(1, CleanReconnectCount);
        }, cancellationToken: TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Reconnect_cycle_does_not_duplicate_transport_handlers()
    {
        var actor = CreateLifecycleActor();
        await GetSnapshotAsync(actor);
        AssertSingleTransportSubscription();

        await ConnectAsync(actor);
        await DisconnectAsync(actor);
        await ConnectAsync(actor);

        AssertSingleTransportSubscription();

        Watch(actor);
        Sys.Stop(actor);
        await ExpectTerminatedAsync(actor, cancellationToken: TestContext.Current.CancellationToken);

        await AwaitAssertAsync(
            AssertNoTransportSubscriptions,
            cancellationToken: TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Clean_reconnect_reports_not_ready_while_transport_still_connected()
    {
        var actor = CreateLifecycleActor();
        await GetSnapshotAsync(actor);

        // Pend the stop so the clean-reconnect teardown holds with the
        // transport still claiming to be connected.
        DeferTransportStop();
        await RaiseSpuriousReadySignalAsync();

        await AwaitAssertAsync(async () =>
        {
            var snapshot = await GetSnapshotAsync(actor);
            Assert.True(snapshot.IsConnected);
            Assert.False(snapshot.IsReady);
            Assert.Equal(1, CleanReconnectCount);
        }, cancellationToken: TestContext.Current.CancellationToken);

        ReleaseTransportStop();

        await AwaitAssertAsync(async () =>
        {
            var snapshot = await GetSnapshotAsync(actor);
            Assert.False(snapshot.IsConnected);
            Assert.False(snapshot.IsReady);
        }, cancellationToken: TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Auto_reconnect_restores_ready_after_runtime_disconnect()
    {
        var actor = CreateLifecycleActor();
        var ready = await ConnectAsync(actor);
        Assert.True(ready.IsReady);
        Assert.Equal(1, TransportStartCount);

        await RaiseRuntimeDisconnectAsync(actor, "network lost");
        await AwaitCleanReconnectSettledAsync(actor);

        // The clean-reconnect cycle schedules an immediate retry; fire it.
        AdvanceScheduler(TimeSpan.FromSeconds(1));

        await AwaitAssertAsync(async () =>
        {
            var snapshot = await GetSnapshotAsync(actor);
            Assert.True(snapshot.IsReady);
            Assert.Equal(2, TransportStartCount);

            // Recovering via auto-retry MUST publish ConnectionRestored.
            // Expected count is 2 because ConnectionRestored now publishes on
            // EVERY transition to Ready (the initial operator connect counts
            // too) — the channel-side setup is idempotent, so always
            // publishing closes the Healthy-but-deaf gap where a client-side
            // ask timeout suppressed the only setup signal.
            Assert.Equal(2, ConnectionRestoredCount);
        }, cancellationToken: TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Operator_connect_publishes_connection_restored()
    {
        var actor = CreateLifecycleActor();
        var ready = await ConnectAsync(actor);
        Assert.True(ready.IsReady);

        // ConnectionRestored must publish on every transition to Ready — not
        // just auto-retries. If the operator's connect ask times out
        // client-side but the transport reaches Ready afterwards, this event
        // is the channel's only signal to finish its connection setup
        // (subscribe ingress, create the gateway actor).
        await AwaitAssertAsync(
            () => Assert.Equal(1, ConnectionRestoredCount),
            cancellationToken: TestContext.Current.CancellationToken);

        // ...and exactly once for a single connect (barrier, then re-check).
        await GetSnapshotAsync(actor);
        Assert.Equal(1, ConnectionRestoredCount);
    }

    [Fact]
    public async Task Flapping_connection_grows_retry_backoff()
    {
        var actor = CreateLifecycleActor();
        await ConnectAsync(actor);

        // Flap #1: drop inside the 60s stability window. The first failure
        // schedules an immediate retry (initial delay is zero) and grows the
        // backoff to 5s.
        await RaiseRuntimeDisconnectAsync(actor, "flap-1");
        await AwaitCleanReconnectSettledAsync(actor, expectedCleanReconnects: 1);
        AdvanceScheduler(TimeSpan.FromSeconds(1));
        await AwaitAssertAsync(async () =>
        {
            Assert.Equal(2, TransportStartCount);
            Assert.True((await GetSnapshotAsync(actor)).IsReady);
        }, cancellationToken: TestContext.Current.CancellationToken);

        // Flap #2, again inside the stability window: the backoff must NOT
        // reset to zero — the next retry honors the grown 5s delay instead of
        // hammering the transport with zero-delay reconnects.
        await RaiseRuntimeDisconnectAsync(actor, "flap-2");
        await AwaitCleanReconnectSettledAsync(actor, expectedCleanReconnects: 2);

        AdvanceScheduler(TimeSpan.FromSeconds(1));
        await GetSnapshotAsync(actor); // mailbox barrier
        Assert.Equal(2, TransportStartCount); // retry must NOT have fired yet

        AdvanceScheduler(TimeSpan.FromSeconds(5));
        await AwaitAssertAsync(
            () => Assert.Equal(3, TransportStartCount),
            cancellationToken: TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Stable_ready_period_resets_retry_backoff()
    {
        var actor = CreateLifecycleActor();
        await ConnectAsync(actor);

        // Grow the backoff to 5s with one quick flap.
        await RaiseRuntimeDisconnectAsync(actor, "flap");
        await AwaitCleanReconnectSettledAsync(actor, expectedCleanReconnects: 1);
        AdvanceScheduler(TimeSpan.FromSeconds(1));
        await AwaitAssertAsync(async () =>
        {
            Assert.Equal(2, TransportStartCount);
            Assert.True((await GetSnapshotAsync(actor)).IsReady);
        }, cancellationToken: TestContext.Current.CancellationToken);

        // Hold Ready past the stability window, then drop: the backoff must
        // reset, so the next retry fires immediately instead of after 5s.
        AdvanceTimeProvider(TimeSpan.FromSeconds(61));
        await RaiseRuntimeDisconnectAsync(actor, "drop after stable period");
        await AwaitCleanReconnectSettledAsync(actor, expectedCleanReconnects: 2);

        AdvanceScheduler(TimeSpan.FromSeconds(1));
        await AwaitAssertAsync(
            () => Assert.Equal(3, TransportStartCount),
            cancellationToken: TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Retry_timer_cancelled_on_actor_stop()
    {
        var actor = CreateLifecycleActor();
        await ConnectAsync(actor);
        await RaiseRuntimeDisconnectAsync(actor, "network lost");

        // Land in Disconnected with an auto-reconnect retry pending on the
        // virtual-time scheduler.
        await AwaitCleanReconnectSettledAsync(actor);

        Watch(actor);
        Sys.Stop(actor);
        await ExpectTerminatedAsync(actor, cancellationToken: TestContext.Current.CancellationToken);

        // PostStop must cancel the pending retry. If it leaked, advancing
        // virtual time would deliver RetryConnect to the terminated actor and
        // surface as a DeadLetter; TestScheduler skips cancelled items.
        var deadLetterProbe = CreateTestProbe();
        Sys.EventStream.Subscribe(deadLetterProbe.Ref, typeof(DeadLetter));

        AdvanceScheduler(TimeSpan.FromMinutes(10));

        await deadLetterProbe.ExpectNoMsgAsync(
            TimeSpan.FromMilliseconds(300),
            TestContext.Current.CancellationToken);
    }

    /// <summary>
    /// Waits until a clean-reconnect cycle has fully settled in Disconnected.
    /// Asserting on <see cref="DisconnectedHealthDetail"/> matters: it is only
    /// set once the stop completes — the same handling that registers the
    /// auto-reconnect retry — so tests that advance virtual time (or stop the
    /// actor) afterwards know the retry timer already exists.
    /// </summary>
    private async Task AwaitCleanReconnectSettledAsync(IActorRef actor, int expectedCleanReconnects = 1) =>
        await AwaitAssertAsync(async () =>
        {
            var snapshot = await GetSnapshotAsync(actor);
            Assert.False(snapshot.IsReady);
            Assert.False(snapshot.IsConnected);
            Assert.Equal(DisconnectedHealthDetail, snapshot.HealthDetail);
            Assert.Equal(expectedCleanReconnects, CleanReconnectCount);
        }, cancellationToken: TestContext.Current.CancellationToken);
}
