// -----------------------------------------------------------------------
// <copyright file="MattermostTypingPulseTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Akka.Actor;
using Akka.Configuration;
using Akka.Hosting;
using Akka.Hosting.TestKit;
using Akka.Persistence.Hosting;
using Microsoft.Extensions.Time.Testing;
using Netclaw.Actors.Channels;
using Netclaw.Actors.Hosting;
using Netclaw.Actors.Protocol;
using Netclaw.Actors.Tests.Channels.TestHelpers;
using Netclaw.Channels.Mattermost;
using Netclaw.Configuration;
using Netclaw.Security;
using Xunit;
using static Netclaw.Actors.Sessions.SessionProtocol;

namespace Netclaw.Actors.Tests.Channels;

/// <summary>
/// Start, repeat, and stop behavior of the Mattermost typing pulse timer. The
/// tests run on <see cref="Akka.TestKit.TestScheduler"/>, so the repeat timer
/// fires only when a test advances virtual time.
/// </summary>
public sealed class MattermostTypingPulseTests(ITestOutputHelper output) : TestKit(output: output)
{
    // Longer than several repeat intervals. A timer that was not cancelled
    // fires at least once in this window.
    private static readonly TimeSpan SeveralPulseIntervals = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan PulseInterval = TimeSpan.FromSeconds(3);

    private readonly RecordingMattermostReplyClient _replyClient = new();
    private readonly FakeTimeProvider _clock = new();

    protected override Config? Config => ConfigurationFactory.ParseString("""
        akka.scheduler.implementation = "Akka.TestKit.TestScheduler, Akka.TestKit"
        """);

    protected override void ConfigureAkka(AkkaConfigurationBuilder builder, IServiceProvider provider)
    {
        builder.WithInMemoryJournal().WithInMemorySnapshotStore().WithNetclawSerialization();
    }

    [Fact]
    public async Task Processing_state_starts_a_pulse_and_repeats_it_for_the_thread()
    {
        var ct = TestContext.Current.CancellationToken;
        var sid = new SessionId("session-mm-typing-repeat");
        var pipeline = new RecordingSessionPipeline(_ => [new ProcessingStateOutput(true) { SessionId = sid }]);

        CreateActor(sid, pipeline);

        await AwaitAssertAsync(() =>
        {
            var pulse = Assert.Single(_replyClient.TypingPulses);
            Assert.Equal("ch-test", pulse.ChannelId.Value);
            Assert.Equal("root-test", pulse.RootPostId);
        }, cancellationToken: ct);

        AdvanceScheduler(PulseInterval);
        await AwaitAssertAsync(() => Assert.Equal(2, _replyClient.TypingPulses.Count), cancellationToken: ct);

        AdvanceScheduler(PulseInterval);
        await AwaitAssertAsync(() => Assert.Equal(3, _replyClient.TypingPulses.Count), cancellationToken: ct);
    }

    [Fact]
    public async Task Processing_state_false_stops_the_pulses()
    {
        var ct = TestContext.Current.CancellationToken;
        var sid = new SessionId("session-mm-typing-stop");
        var pipeline = new RecordingSessionPipeline(_ =>
        [
            new ProcessingStateOutput(true) { SessionId = sid },
            new ProcessingStateOutput(false) { SessionId = sid },
            // The post proves that the actor handled the idle state before
            // the test advances virtual time.
            new TextOutput("done") { SessionId = sid }
        ]);

        var actor = CreateActor(sid, pipeline);
        await AwaitAssertAsync(
            () => Assert.Contains(_replyClient.Posts, post => post.Text == "done"),
            cancellationToken: ct);

        AdvanceScheduler(SeveralPulseIntervals);
        await AwaitInboundHandledAsync(actor, pipeline, ct);

        Assert.Single(_replyClient.TypingPulses);
    }

    [Fact]
    public async Task Pipeline_reinitialize_stops_the_pulses()
    {
        var ct = TestContext.Current.CancellationToken;
        var sid = new SessionId("session-mm-typing-reinit");
        var createCalls = 0;
        // Only the first pipeline reports processing. The replacement pipeline
        // stays silent, as it does when a reinitialize abandons the turn.
        var pipeline = new RecordingSessionPipeline(_ =>
            Interlocked.Increment(ref createCalls) == 1
                ? [new ProcessingStateOutput(true) { SessionId = sid }]
                : []);

        var actor = CreateActor(sid, pipeline);
        await AwaitAssertAsync(() => Assert.Single(_replyClient.TypingPulses), cancellationToken: ct);

        pipeline.TerminateOutputStream();
        await AwaitAssertAsync(() => Assert.Equal(2, pipeline.CreateCount), cancellationToken: ct);

        AdvanceScheduler(SeveralPulseIntervals);
        await AwaitInboundHandledAsync(actor, pipeline, ct);

        Assert.Single(_replyClient.TypingPulses);
    }

    [Fact]
    public async Task Pulses_stop_when_the_idle_state_never_arrives()
    {
        var ct = TestContext.Current.CancellationToken;
        var sid = new SessionId("session-mm-typing-max-duration");
        var pipeline = new RecordingSessionPipeline(_ => [new ProcessingStateOutput(true) { SessionId = sid }]);

        var actor = CreateActor(sid, pipeline);
        await AwaitAssertAsync(() => Assert.Single(_replyClient.TypingPulses), cancellationToken: ct);

        // The session never reports idle, as when its actor fails during a
        // turn. The next timer message finds the deadline in the past.
        _clock.Advance(TimeSpan.FromMinutes(11));
        AdvanceScheduler(SeveralPulseIntervals);
        await AwaitInboundHandledAsync(actor, pipeline, ct);

        Assert.Single(_replyClient.TypingPulses);

        // The stopped timer must not start again.
        AdvanceScheduler(SeveralPulseIntervals);
        actor.Tell(new Identify(1), TestActor);
        await ExpectMsgAsync<ActorIdentity>(cancellationToken: ct);
        Assert.Single(_replyClient.TypingPulses);
    }

    [Fact]
    public async Task Failed_pulse_does_not_stop_the_turn_or_the_repeat()
    {
        var ct = TestContext.Current.CancellationToken;
        var sid = new SessionId("session-mm-typing-failure");
        _replyClient.ThrowOnTyping = new HttpRequestException("typing rejected");
        var pipeline = new RecordingSessionPipeline(_ =>
        [
            new ProcessingStateOutput(true) { SessionId = sid },
            new TextOutput("reply") { SessionId = sid }
        ]);

        CreateActor(sid, pipeline);

        // The reply follows the failed pulse on the same message path.
        await AwaitAssertAsync(
            () => Assert.Contains(_replyClient.Posts, post => post.Text == "reply"),
            cancellationToken: ct);
        Assert.Single(_replyClient.TypingPulses);

        AdvanceScheduler(PulseInterval);
        await AwaitAssertAsync(() => Assert.Equal(2, _replyClient.TypingPulses.Count), cancellationToken: ct);
    }

    private void AdvanceScheduler(TimeSpan offset) =>
        ((Akka.TestKit.TestScheduler)Sys.Scheduler).Advance(offset);

    /// <summary>
    /// Sends one inbound message and waits until the pipeline receives it. The
    /// actor handles its mailbox in order, so a repeat pulse that the scheduler
    /// enqueued earlier is already handled when this returns.
    /// </summary>
    private async Task AwaitInboundHandledAsync(
        IActorRef actor,
        RecordingSessionPipeline pipeline,
        CancellationToken ct)
    {
        actor.Tell(new MattermostThreadInbound(
            SessionId: new SessionId("ignored"),
            ChannelId: new MattermostChannelId("ch-test"),
            PostId: new MattermostPostId("post-probe"),
            RootPostId: new MattermostRootPostId("root-test"),
            EventId: new MattermostEventId("evt-probe"),
            SenderId: new MattermostUserId("user-1"),
            Audience: TrustAudience.Team,
            Principal: PrincipalClassification.UntrustedExternal,
            Provenance: new SourceProvenance(TransportAuthenticity.Verified, PayloadTaint.Public)
            {
                SourceKind = new SourceKind("mattermost")
            },
            Text: "probe",
            ReceivedAt: TimeProvider.System.GetUtcNow()));

        await AwaitAssertAsync(() => Assert.Single(pipeline.CapturedInputs), cancellationToken: ct);
    }

    private IActorRef CreateActor(SessionId sessionId, ISessionPipeline pipeline)
    {
        var deps = new MattermostGatewayDependencies(
            Pipeline: pipeline,
            IngressGate: null,
            TimeProvider: _clock,
            Options: new MattermostChannelOptions(),
            DefaultChannelId: null,
            ChannelRegistry: TestChannelRegistries.MattermostWithProcessingRenderer(_replyClient),
            ReplyClient: _replyClient,
            ContentScanner: new NullContentScanner(),
            AudienceProfiles: TestMattermostGatewayDeps.DefaultAudienceProfiles,
            ModelCapabilities: TestMattermostGatewayDeps.DefaultVisionCapableModel,
            StorageResolver: TestSessionStorageResolver.Instance,
            PromptInjectionDetector: new ConfigurablePromptInjectionDetector(PromptInjectionResult.Safe()));

        return Sys.ActorOf(MattermostSessionBindingActor.CreateProps(
            sessionId,
            new MattermostChannelId("ch-test"),
            new MattermostRootPostId("root-test"),
            deps));
    }
}
