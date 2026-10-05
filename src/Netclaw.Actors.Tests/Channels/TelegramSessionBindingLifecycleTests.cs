// -----------------------------------------------------------------------
// <copyright file="TelegramSessionBindingLifecycleTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Akka.Actor;
using Akka.Hosting;
using Akka.Hosting.TestKit;
using Akka.Persistence.Hosting;
using Akka.Streams;
using Microsoft.Extensions.Logging.Abstractions;
using Netclaw.Actors.Channels;
using Netclaw.Actors.Hosting;
using Netclaw.Actors.Protocol;
using Netclaw.Actors.Tests.Channels.TestHelpers;
using Netclaw.Channels;
using Netclaw.Channels.Telegram;
using Netclaw.Configuration;
using Netclaw.Security;
using Xunit;
using static Netclaw.Actors.Sessions.SessionProtocol;

namespace Netclaw.Actors.Tests.Channels;

/// <summary>
/// Focused coverage for the Telegram binding actor's spawn-time lifecycle:
/// the pipeline initializes at actor start, inbounds that arrive during
/// initialization are stashed and unstashed, an initialization failure stops
/// the actor, and a fail-loud delivery-failure restart creates a fresh
/// pipeline. Synchronization uses gates and completion sources, not sleeps.
/// </summary>
public sealed class TelegramSessionBindingLifecycleTests(ITestOutputHelper output) : TestKit(output: output)
{
    private const long ChatId = 77;

    protected override void ConfigureAkka(AkkaConfigurationBuilder builder, IServiceProvider provider)
    {
        // The binding journals pending-approval prompts, so the test system
        // needs the same persistence setup as the channel contract tests.
        builder.WithInMemoryJournal().WithInMemorySnapshotStore().WithNetclawSerialization();
    }

    [Fact]
    public async Task Pipeline_is_created_on_spawn_without_any_inbound()
    {
        var ct = TestContext.Current.CancellationToken;
        var fake = new TelegramTransportTests.FakeTelegramBotApiClient();
        var pipeline = new RecordingSessionPipeline(_ => []);

        CreateActor(pipeline, fake);

        // No message was ever sent; only spawn-time initialization can have
        // created the pipeline.
        await pipeline.Created.WaitAsync(ct);
    }

    [Fact]
    public async Task Inbound_during_initialization_is_stashed_and_processed_after()
    {
        var ct = TestContext.Current.CancellationToken;
        var fake = new TelegramTransportTests.FakeTelegramBotApiClient();
        var inner = new RecordingSessionPipeline(_ => []);
        var gate = new GateSessionPipeline(inner);
        var actor = CreateActor(gate, fake);

        // The init command reached the pipeline but is parked on the gate.
        await gate.CreateStarted.WaitAsync(ct);

        // The actor is blocked inside initialization, so this inbound can
        // only be stashed (or queued behind the stash); it cannot be
        // enqueued into a pipeline that does not exist yet.
        actor.Tell(CreateInbound("stashed message"));

        Assert.Empty(inner.CapturedInputs);

        gate.Open();

        await inner.Created.WaitAsync(ct);
        await AwaitAssertAsync(() =>
        {
            Assert.Contains(inner.CapturedInputs, input =>
                input.Contents.OfType<Microsoft.Extensions.AI.TextContent>()
                    .Any(content => content.Text == "stashed message"));
        }, cancellationToken: ct);
    }

    [Fact]
    public async Task Initialization_failure_stops_the_actor()
    {
        var ct = TestContext.Current.CancellationToken;
        var fake = new TelegramTransportTests.FakeTelegramBotApiClient();
        var failing = new FailingSessionPipeline(new InvalidOperationException("init boom"));
        var actor = CreateActor(failing, fake);

        var probe = CreateTestProbe();
        probe.Watch(actor);
        await probe.ExpectTerminatedAsync(actor, cancellationToken: ct);
    }

    [Fact]
    public async Task Fail_loud_delivery_feedback_restart_creates_fresh_pipeline()
    {
        var ct = TestContext.Current.CancellationToken;
        var fake = new TelegramTransportTests.FakeTelegramBotApiClient();
        for (var i = 0; i < 50; i++)
            fake.SendMessageFailures.Enqueue(new InvalidOperationException("channel api down"));

        // The failed post sends DeliveryFailed; the dead feedback pipe makes
        // that notify throw, the actor faults, and the supervised restart
        // must initialize a new pipeline generation.
        var pipeline = new RecordingSessionPipeline(sid => new List<SessionOutput>
        {
            new TextOutput("this will fail to post") { SessionId = sid },
            new TurnCompleted { SessionId = sid, TurnNumber = new TurnNumber(1) }
        })
        {
            FeedbackException = new InvalidOperationException("feedback pipe down")
        };

        CreateActor(pipeline, fake);

        await AwaitAssertAsync(() => Assert.True(
            pipeline.CreateCount >= 2,
            $"expected a supervised restart to re-create the pipeline; CreateCount={pipeline.CreateCount}"),
            cancellationToken: ct);
    }

    /// <summary>
    /// Wraps a pipeline and parks <see cref="ISessionPipeline.CreateAsync"/>
    /// on a test-controlled gate, so a test holds initialization open while
    /// messages arrive.
    /// </summary>
    private sealed class GateSessionPipeline(ISessionPipeline inner) : ISessionPipeline
    {
        private readonly TaskCompletionSource _gate = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        private readonly TaskCompletionSource _createStarted = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>Completes when <see cref="CreateAsync"/> has been entered.</summary>
        public Task CreateStarted => _createStarted.Task;

        public void Open() => _gate.TrySetResult();

        public async Task<MaterializedSession> CreateAsync(
            SessionId sessionId,
            SessionPipelineOptions options,
            IMaterializer? materializer = null,
            CancellationToken cancellationToken = default)
        {
            _createStarted.TrySetResult();
            await _gate.Task.WaitAsync(cancellationToken);
            return await inner.CreateAsync(sessionId, options, materializer, cancellationToken);
        }

        public Task SendFeedbackAsync(IWithSessionId feedback, CancellationToken ct = default)
            => inner.SendFeedbackAsync(feedback, ct);

        public Task<ISessionResponse> SendFeedbackAndWaitAsync(IWithSessionId feedback, CancellationToken ct = default)
            => inner.SendFeedbackAndWaitAsync(feedback, ct);
    }

    // --- helpers ---

    private TelegramSessionInbound CreateInbound(string text)
    {
        var options = new TelegramChannelOptions
        {
            Enabled = true,
            BotToken = new SensitiveString("12345:test-token"),
            AllowedChatIds = [ChatId.ToString()],
            AllowDirectMessages = true
        };
        var message = new TelegramInboundMessage(
            ChatId, UserId: 1001, MessageId: 55, Text: text, IsDirectMessage: true);
        var decision = TelegramAclPolicy.EvaluateInbound(message, options);
        Assert.True(decision.IsAllowed, "test inbound must pass the Telegram ACL");
        return new TelegramSessionInbound(
            new SessionId($"{ChatId}/chat"), message, decision, message.Text);
    }

    private IActorRef CreateActor(
        ISessionPipeline pipeline,
        TelegramTransportTests.FakeTelegramBotApiClient fake)
    {
        var options = new TelegramChannelOptions
        {
            Enabled = true,
            BotToken = new SensitiveString("12345:test-token"),
            AllowedChatIds = [ChatId.ToString()]
        };
        var transport = new TelegramTransport(
            options,
            NullLogger<TelegramTransport>.Instance,
            (_, _) => fake);
        transport.StartAsync(CancellationToken.None).GetAwaiter().GetResult();

        var dependencies = new TelegramGatewayDependencies(
            pipeline,
            IngressGate: null,
            TimeProvider: TimeProvider.System,
            options,
            transport,
            ContentScanner: new NullContentScanner(),
            AudienceProfiles: ToolAudienceProfileDefaults.CreateProfiles(),
            ModelCapabilities: new ModelCapabilities(),
            StorageResolver: Netclaw.Actors.Protocol.TestSessionStorageResolver.Instance,
            ChannelRegistry: null,
            PromptInjectionDetector: new ConfigurablePromptInjectionDetector(PromptInjectionResult.Safe()));

        return Sys.ActorOf(TelegramSessionBindingActor.CreateProps(
            new SessionId($"{ChatId}/chat"),
            new TelegramChatId(ChatId),
            dependencies));
    }
}
