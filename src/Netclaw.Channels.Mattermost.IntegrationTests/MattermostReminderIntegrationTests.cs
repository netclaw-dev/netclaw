// -----------------------------------------------------------------------
// <copyright file="MattermostReminderIntegrationTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Akka;
using Akka.Actor;
using Akka.Hosting;
using Akka.Hosting.TestKit;
using Akka.Persistence.Hosting;
using Akka.Reminders;
using Akka.Reminders.Sharding;
using Akka.Streams;
using Akka.Streams.Dsl;
using Mattermost;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Netclaw.Actors.Channels;
using Netclaw.Actors.Hosting;
using Netclaw.Actors.Protocol;
using Netclaw.Actors.Reminders;
using Netclaw.Channels;
using Netclaw.Channels.Mattermost.Transport;
using Netclaw.Configuration;
using Netclaw.Security;
using Netclaw.Tests.Utilities;
using Netclaw.Tools;
using Xunit;
using static Netclaw.Actors.Reminders.ReminderProtocol;
using static Netclaw.Actors.Sessions.SessionProtocol;

namespace Netclaw.Channels.Mattermost.IntegrationTests;

[Collection("Mattermost")]
public sealed class MattermostReminderIntegrationTests(
    MattermostFixture fixture, ITestOutputHelper output) : TestKit(output: output), IAsyncDisposable
{
    private readonly TestSessionTempDirectory _state = TestSessionTempDirectory.Create(
        prefix: "netclaw-mattermost-reminder-", createDirectoryTree: true);
    private readonly TestShardRegionResolver _resolver = new();
    private readonly ReminderReplyPipeline _pipeline = new();
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    protected override void ConfigureAkka(AkkaConfigurationBuilder builder, IServiceProvider provider)
    {
        builder.WithInMemoryJournal().WithInMemorySnapshotStore().WithNetclawSerialization();
        builder.WithLocalReminders(reminders =>
        {
            reminders.WithInMemoryStorage();
            reminders.WithResolver(_ => _resolver);
        });
        builder.StartActors((system, registry, _) =>
        {
            var manager = system.ActorOf(Props.Create(() => new ReminderManagerActor(
                _pipeline,
                new EffectivePolicyDefaults(DeploymentPosture.Team, TrustAudience.Team, ShellExecutionMode.Off, false),
                new SchedulingConfig(), TimeProvider.System,
                new ReminderDefinitionStore(_state.Paths), new ReminderHistoryStore(_state.Paths),
                NullNotificationSink.Instance, NullReminderChannelNotifier.Instance)), "reminders");
            registry.Register<ReminderManagerActorKey>(manager);
            _resolver.RegisterShardRegion(ReminderManagerActor.ShardRegionName, manager);
        });
    }

    // TestKit stops the actor system only after AfterAllAsync returns, and it fails
    // the test when AfterAllAsync takes more than 5 seconds. Delete the directory
    // after TestKit has disposed, and not in AfterAllAsync.
    async ValueTask IAsyncDisposable.DisposeAsync()
    {
        try
        {
            await base.DisposeAsync();
        }
        finally
        {
            await _state.DisposeAsync();
        }
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(true, true)]
    [InlineData(false, true)]
    public async Task Current_session_reminder_records_the_actual_thread_delivery_result(bool validToken, bool existingSession)
    {
        fixture.SkipIfUnavailable();
        var ct = TestContext.Current.CancellationToken;
        var rootId = await fixture.PostAsTestUserAsync(fixture.ChannelId, "Remind me in this thread");
        var sessionId = new SessionId($"{fixture.ChannelId}/{rootId}");
        using var bot = new MattermostClient(fixture.ServerUrl, validToken ? fixture.BotToken : "invalid-token");
        var replyClient = new MattermostNetReplyClient(bot);
        var gateway = Sys.ActorOf(MattermostGatewayActor.CreateProps(new MattermostGatewayDependencies(
            Pipeline: _pipeline, IngressGate: null, TimeProvider: TimeProvider.System,
            Options: new MattermostChannelOptions
            {
                MentionOnly = true, AllowedChannelIds = [fixture.ChannelId], AllowedUserIds = [fixture.TestUserId]
            },
            DefaultChannelId: null, ChannelRegistry: MattermostIntegrationRegistries.WithProcessingRenderer(replyClient), ReplyClient: replyClient,
            ContentScanner: new NullContentScanner(), AudienceProfiles: ToolAudienceProfileDefaults.CreateProfiles(),
            ModelCapabilities: new ModelCapabilities { ModelId = "test", InputModalities = ModelModality.Text },
            StorageResolver: new TestSessionStorageResolver(_state.Paths),
            PromptInjectionDetector: new RegexPromptInjectionDetector(NullLogger<RegexPromptInjectionDetector>.Instance))), "mattermost");
        ActorRegistry.Register<MattermostGatewayActorKey>(gateway);

        if (existingSession)
        {
            await gateway.Ask<MattermostProactiveThreadAck>(new StartMattermostProactiveThread(
                new MattermostChannelId(fixture.ChannelId), new MattermostRootPostId(rootId), sessionId), Timeout, ct);
            await _pipeline.Created.Task.WaitAsync(Timeout, ct);
        }

        var manager = ActorRegistry.Get<ReminderManagerActorKey>();
        var id = new ReminderId("mattermost-check-back");
        var result = await CreateReminderAsync(manager, id, sessionId, audience: null);
        Assert.DoesNotContain("Error:", result);

        // Read from disk so the execution consumes the same target that the tool saved.
        var stored = new ReminderDefinitionStore(_state.Paths).Get(id);
        Assert.NotNull(stored);
        Assert.Equal(DeliveryKind.CurrentSession, stored.Delivery.Kind);
        Assert.Equal(sessionId.Value, stored.Delivery.SessionId);
        Assert.Equal(ChannelType.Mattermost, stored.Delivery.OriginChannelType);
        Assert.Equal(TrustAudience.Team, stored.Audience);
        Assert.Equal(TrustBoundary.Team, stored.Boundary);
        Assert.True(stored.DeliveryRequired);

        // Inject the scheduler envelope after creation to avoid a wall-clock wait.
        var due = stored.Schedule.FireAt!.Value;
        manager.Tell(new ReminderEnvelope<ReminderPayload>(
            new ReminderEntity(ReminderManagerActor.ShardRegionName, ReminderManagerActor.EntityId),
            new ReminderKey(id.Value), due, ReminderDeadline.Infinite, new ReminderPayload { Id = id }));

        var input = await _pipeline.Received.Task.WaitAsync(Timeout, ct);
        Assert.Equal(sessionId, _pipeline.SessionId);
        Assert.Equal(fixture.ChannelId, input.ChannelId);
        Assert.Equal(TrustAudience.Team, input.Audience);
        Assert.Equal(TrustBoundary.Team, input.Boundary);
        Assert.Equal(PrincipalClassification.VerifiedAutomation, input.Principal);
        Assert.Equal(new ReminderId($"{id}:{due.ToUnixTimeMilliseconds()}"), input.ReminderId);
        Assert.Equal(rootId, input.DefaultDeliveryTarget?.ThreadOrRootId);
        Assert.Equal(1, _pipeline.CreateCount);

        await AwaitAssertAsync(async () =>
        {
            if (validToken)
            {
                // The manager deletes a successful one-shot definition and its history.
                Assert.Null(new ReminderDefinitionStore(_state.Paths).Get(id));
                var health = await manager.Ask<ReminderHealthResponse>(GetReminderHealthQuery.Instance, Timeout, ct);
                Assert.Equal(0, health.ActiveExecutions);
            }
            else
            {
                var history = await new ReminderHistoryStore(_state.Paths).ReadAsync(id, 10);
                var record = Assert.Single(history);
                Assert.Equal(sessionId.Value, record.SessionId);
                Assert.False(record.Success);
                Assert.Contains("Mattermost post did not succeed", record.ErrorMessage);
            }
        }, duration: Timeout, cancellationToken: ct);

        using var reader = new MattermostClient(fixture.ServerUrl, fixture.BotToken);
        var thread = await reader.GetThreadPostsAsync(rootId);
        var replies = thread.Posts.Values.Where(post => post.Text == ReminderReplyPipeline.ReplyText).ToArray();
        if (validToken)
        {
            var reply = Assert.Single(replies);
            Assert.Equal(fixture.ChannelId, reply.ChannelId);
            Assert.Equal(rootId, reply.RootId);
            Assert.Equal(fixture.BotUserId, reply.UserId);
        }
        else
        {
            Assert.Empty(replies);
        }
    }

    [Fact]
    public async Task Current_session_reminder_rejects_broader_audience_before_persistence()
    {
        var manager = ActorRegistry.Get<ReminderManagerActorKey>();
        var id = new ReminderId("mattermost-escalation");
        var sessionId = new SessionId("abcdefghijklmnopqrstuvwxyz/zyxwvutsrqponmlkjihgfedcba");
        var result = await CreateReminderAsync(manager, id, sessionId, audience: "personal");
        Assert.Contains("Error:", result);
        Assert.Contains("exceeds creator authority", result);
        Assert.Null(new ReminderDefinitionStore(_state.Paths).Get(id));
    }

    private Task<string> CreateReminderAsync(IActorRef manager, ReminderId id, SessionId sessionId, string? audience)
    {
        var context = TestToolExecutionContext.CreateBound(sessionId.Value, null, new TestToolExecutionContextOptions
        {
            Audience = TrustAudience.Team, Boundary = TrustBoundary.Team, ChannelType = "mattermost"
        });
        return new SetReminderTool(manager, TimeProvider.System, new SchedulingConfig()).ExecuteAsync(
            new Dictionary<string, object?>
            {
                ["Id"] = id.Value, ["Name"] = id.Value, ["Prompt"] = "Reply in the original thread",
                ["ScheduleType"] = "once", ["Schedule"] = "1h", ["DeliveryKind"] = "current_session",
                ["DeliveryRequired"] = true, ["Audience"] = audience
            }, context.Invocation, TestContext.Current.CancellationToken);
    }

    private sealed class ReminderReplyPipeline : ISessionPipeline
    {
        public const string ReplyText = "The reminder returned to the original Mattermost thread.";
        public TaskCompletionSource<ChannelInput> Received { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Created { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public SessionId SessionId { get; private set; }
        public int CreateCount { get; private set; }

        public Task<MaterializedSession> CreateAsync(SessionId sessionId, SessionPipelineOptions options,
            IMaterializer? materializer = null, CancellationToken cancellationToken = default)
        {
            SessionId = sessionId;
            CreateCount++;
            var (queue, output) = Source.Queue<SessionOutput>(16, OverflowStrategy.Fail)
                .PreMaterialize(materializer ?? throw new InvalidOperationException("The pipeline requires a materializer."));
            var input = Sink.ForEachAsync<ChannelInput>(1, async message =>
            {
                message.AckTarget?.Tell(CommandAck.For(sessionId));
                Received.TrySetResult(message);
                await queue.OfferAsync(new TextOutput(ReplyText) { SessionId = sessionId });
                await queue.OfferAsync(new TurnCompleted
                {
                    SessionId = sessionId, TurnNumber = new TurnNumber(1), SourceReminderId = message.ReminderId
                });
            }).MapMaterializedValue(_ => NotUsed.Instance);
            var killSwitch = KillSwitches.Shared("reminder-proof");
            Created.TrySetResult();
            return Task.FromResult(new MaterializedSession(input, output.Via(killSwitch.Flow<SessionOutput>()), killSwitch));
        }

        public Task SendFeedbackAsync(IWithSessionId feedback, CancellationToken ct = default) => Task.CompletedTask;
        public Task<ISessionResponse> SendFeedbackAndWaitAsync(IWithSessionId feedback, CancellationToken ct = default)
            => Task.FromResult<ISessionResponse>(CommandAck.For(feedback.SessionId));
    }
}
