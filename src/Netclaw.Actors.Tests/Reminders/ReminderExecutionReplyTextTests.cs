// -----------------------------------------------------------------------
// <copyright file="ReminderExecutionReplyTextTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Akka.Actor;
using Akka.Reminders;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Netclaw.Actors.Channels;
using Netclaw.Actors.Reminders;
using Netclaw.Actors.Sessions;
using Netclaw.Actors.Tests.Sessions;
using Netclaw.Actors.Tests.Tools;
using Netclaw.Actors.Tools;
using Netclaw.Configuration;
using Netclaw.Security;
using Xunit;

namespace Netclaw.Actors.Tests.Reminders;

/// <summary>
/// Runs a scheduled reminder through the real session pipeline. The session
/// streams text deltas only after a second text chunk, so a model reply in one
/// chunk reaches subscribers only as the final <c>TextOutput</c>.
/// </summary>
public sealed class ReminderExecutionReplyTextTests : LlmSessionTestBase
{
    private readonly FakeChatClient _chatClient = new();

    public ReminderExecutionReplyTextTests(ITestOutputHelper output) : base(output)
    {
    }

    protected override void ConfigureSessionServices(IServiceCollection services)
    {
        services.AddSingleton<IChatClientProvider>(new SingleClientProvider(_chatClient));
        services.AddSingleton(new ModelCapabilities
        {
            ModelId = "fake-model",
            ContextWindowTokens = 128_000,
        });
        services.AddSingleton(new SessionConfig
        {
            Tuning = new SessionTuning
            {
                SnapshotInterval = 5,
                TitleGenerationInterval = 0,
            }
        });
        services.AddSingleton<ISystemPromptProvider>(new StaticSystemPromptProvider(
            "You are a test assistant."));

        var registry = new ToolRegistry();
        var toolAccessPolicy = TestToolAccessPolicy.Create(new ToolConfig());
        services.AddSingleton(registry);
        services.AddSingleton(toolAccessPolicy);
        services.AddSingleton<IToolExecutor>(new FakeToolExecutor());
        services.AddSingleton(new TrustContextDeriver(new EffectivePolicyDefaults(
            DeploymentPosture.Team,
            TrustAudience.Team,
            ShellExecutionMode.Off,
            UsedStrictFallback: false)));
    }

    [Fact]
    public async Task One_chunk_model_reply_is_captured_as_reminder_output()
    {
        const string reply = "Disk is clean.";
        _chatClient.PlannedResponses.Enqueue([new TextContent(reply)]);

        var now = TimeProvider.System.GetUtcNow();
        var definition = new ReminderDefinition
        {
            Id = new ReminderId("one-chunk-reply"),
            Title = "One chunk reply",
            Instructions = "Check the disk.",
            Delivery = new ReminderDelivery { Kind = DeliveryKind.None },
            Schedule = new ReminderSchedule { Type = ReminderScheduleType.OneShot, FireAt = now },
            Audience = TrustAudience.Team,
            Boundary = TrustBoundary.Team,
            Enabled = true,
            CreatedBy = "test",
            CreatedAt = now,
            UpdatedAt = now
        };
        var pipeline = Host.Services.GetRequiredService<ISessionPipeline>();
        var probe = CreateTestProbe();

        // Before the fix the reminder saw no text and logged output_length=0.
        await EventFilter.Info(contains: $"output_length={reply.Length} ")
            .ExpectAsync(1, async () =>
            {
                Sys.ActorOf(Props.Create(() => new AcceptingParent(probe.Ref, definition, pipeline)));
                var completed = await probe.ExpectMsgAsync<ReminderExecutionCompleted>(
                    TimeSpan.FromSeconds(30),
                    cancellationToken: TestContext.Current.CancellationToken);
                Assert.True(completed.Success, completed.ErrorMessage);
            }, cancellationToken: TestContext.Current.CancellationToken);
    }

    /// <summary>Plays the reminder manager: it accepts the completion so the execution actor stops.</summary>
    private sealed class AcceptingParent : ReceiveActor
    {
        public AcceptingParent(IActorRef probe, ReminderDefinition definition, ISessionPipeline pipeline)
        {
            var envelope = new ReminderEnvelope<ReminderPayload>(
                new ReminderEntity(ReminderManagerActor.ShardRegionName, ReminderManagerActor.EntityId),
                new ReminderKey(definition.Id.Value),
                definition.Schedule.FireAt!.Value,
                ReminderDeadline.Infinite,
                new ReminderPayload { Id = definition.Id });
            Context.ActorOf(ReminderExecutionActor.CreateProps(
                Guid.NewGuid(), definition, pipeline, TimeProvider.System, envelope));

            Receive<ReminderExecutionCompleted>(completed =>
            {
                probe.Tell(completed);
                Sender.Tell(new ReminderExecutionAccepted(completed.ExecutionId));
            });
        }
    }
}
