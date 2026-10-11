// -----------------------------------------------------------------------
// <copyright file="ReminderModelUnreachableTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Net.Sockets;
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
/// Runs a scheduled reminder through the real session pipeline against a chat client that throws
/// the exceptions the HTTP stack throws when the model cannot be reached. The retry layer sits
/// below the session in production and is exhausted by the time the exception arrives here, so
/// these exceptions are the final failure of the model call.
/// </summary>
public sealed class ReminderModelUnreachableTests : LlmSessionTestBase
{
    private readonly FakeChatClient _chatClient = new();

    public ReminderModelUnreachableTests(ITestOutputHelper output) : base(output)
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

    public static TheoryData<Exception> UnreachableModelFailures() => new()
    {
        // Connection refused, as the closed port of a stopped model server.
        new HttpRequestException(
            "Connection refused (127.0.0.1:9)",
            new SocketException((int)SocketError.ConnectionRefused)),
        // The connect or response timed out.
        new TaskCanceledException(
            "The request was canceled due to the configured HttpClient.Timeout.",
            new TimeoutException()),
        // The name does not resolve.
        new HttpRequestException(
            "Name or service not known (nonexistent.invalid:80)",
            new SocketException((int)SocketError.HostNotFound)),
    };

    [Theory]
    [MemberData(nameof(UnreachableModelFailures))]
    public async Task Unreachable_model_fails_the_run_and_is_not_logged_as_a_success(Exception failure)
    {
        _chatClient.PlannedExceptions.Enqueue(failure);

        var pipeline = Host.Services.GetRequiredService<ISessionPipeline>();
        var probe = CreateTestProbe();

        // The session closes a failed turn with TurnCompleted after its ErrorOutput. That closing
        // event used to be logged as "Completed ... success=True" even though the run had failed.
        await EventFilter.Info(contains: "ReminderExecution Completed")
            .ExpectAsync(0, async () =>
            {
                Sys.ActorOf(Props.Create(() => new AcceptingParent(probe.Ref, NewDefinition("unreachable-model"), pipeline)));
                var completed = await probe.ExpectMsgAsync<ReminderExecutionCompleted>(
                    TimeSpan.FromSeconds(30),
                    cancellationToken: TestContext.Current.CancellationToken);

                Assert.False(completed.Success);
                Assert.False(completed.History.Success);
                Assert.False(string.IsNullOrWhiteSpace(completed.ErrorMessage));
            }, cancellationToken: TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Reachable_model_still_completes_the_run()
    {
        _chatClient.PlannedResponses.Enqueue([new TextContent("Disk is clean.")]);

        var pipeline = Host.Services.GetRequiredService<ISessionPipeline>();
        var probe = CreateTestProbe();

        Sys.ActorOf(Props.Create(() => new AcceptingParent(probe.Ref, NewDefinition("reachable-model"), pipeline)));
        var completed = await probe.ExpectMsgAsync<ReminderExecutionCompleted>(
            TimeSpan.FromSeconds(30),
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(completed.Success, completed.ErrorMessage);
        Assert.True(completed.History.Success);
    }

    private static ReminderDefinition NewDefinition(string id)
    {
        var now = TimeProvider.System.GetUtcNow();
        return new ReminderDefinition
        {
            Id = new ReminderId(id),
            Title = id,
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
