// -----------------------------------------------------------------------
// <copyright file="TelegramSessionBindingOutputTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Akka.Actor;
using Akka.Hosting;
using Akka.Hosting.TestKit;
using Akka.Persistence.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Netclaw.Actors.Channels;
using Netclaw.Actors.Hosting;
using Netclaw.Actors.Protocol;
using Netclaw.Actors.Reminders;
using Netclaw.Actors.Tests.Channels.TestHelpers;
using Netclaw.Channels;
using Netclaw.Channels.Telegram;
using Netclaw.Configuration;
using Netclaw.Security;
using Netclaw.Tools;
using Xunit;
using static Netclaw.Actors.Sessions.SessionProtocol;
using static Netclaw.Actors.Reminders.ReminderProtocol;

namespace Netclaw.Actors.Tests.Channels;

/// <summary>
/// Focused coverage for the Telegram binding actor's migrated output and
/// delivery bookkeeping (shared ChannelOutputEngine + SafeTransportCall):
/// delivery-failure feedback, empty-turn fallback and its suppression, and
/// reminder delivery settlement.
/// </summary>
public sealed class TelegramSessionBindingOutputTests(ITestOutputHelper output) : TestKit(output: output)
{
    private const string ChatId = "77";

    protected override void ConfigureAkka(AkkaConfigurationBuilder builder, IServiceProvider provider)
    {
        // The binding journals pending-approval prompts, so the test system
        // needs the same persistence setup as the channel contract tests.
        builder.WithInMemoryJournal().WithInMemorySnapshotStore().WithNetclawSerialization();
    }

    [Fact]
    public async Task Transport_failure_reports_DeliveryFailed_and_keeps_processing()
    {
        var fake = new TelegramTransportTests.FakeTelegramBotApiClient();
        fake.SendMessageFailures.Enqueue(new InvalidOperationException("telegram down"));
        fake.SendMessageFailures.Enqueue(new InvalidOperationException("telegram down again"));
        var pipeline = new RecordingSessionPipeline(sid => new List<SessionOutput>
        {
            new TextOutput("first reply") { SessionId = sid },
            new TurnCompleted { SessionId = sid, TurnNumber = new TurnNumber(1) },
            new TextOutput("second reply") { SessionId = sid },
            new TurnCompleted { SessionId = sid, TurnNumber = new TurnNumber(2) }
        });
        var actor = CreateActor(pipeline, fake);

        WarmUp(actor);
        await pipeline.Created.WaitAsync(TestContext.Current.CancellationToken);

        await AwaitAssertAsync(() =>
        {
            var failures = pipeline.RecordedFeedback.OfType<DeliveryFailed>().ToList();
            Assert.Equal(2, failures.Count);
            Assert.All(failures, failure =>
            {
                Assert.Equal(DeliveryFailureKind.TransportFailure, failure.FailureKind);
                Assert.Contains("telegram down", failure.ErrorMessage);
                Assert.Equal(ChannelType.Telegram, failure.ChannelType);
            });
        }, cancellationToken: TestContext.Current.CancellationToken);

        // Both posts were attempted and failed; the empty-turn fallback must
        // not fire for either turn because a reply WAS produced.
        Assert.Equal(2, fake.SentTexts.Count);
        var probe = CreateTestProbe();
        probe.Watch(actor);
        Assert.False(probe.HasMessages);
    }

    [Fact]
    public async Task Empty_turn_posts_fallback()
    {
        var fake = new TelegramTransportTests.FakeTelegramBotApiClient();
        var pipeline = new RecordingSessionPipeline(sid => new List<SessionOutput>
        {
            new TurnCompleted { SessionId = sid, TurnNumber = new TurnNumber(1) }
        });
        var actor = CreateActor(pipeline, fake);

        WarmUp(actor);
        await pipeline.Created.WaitAsync(TestContext.Current.CancellationToken);

        await AwaitAssertAsync(() =>
        {
            // The transport posts HTML-escaped text, so assert on the stem.
            var post = Assert.Single(fake.SentTexts);
            Assert.Contains("manage to produce a reply", post.Text);
        }, cancellationToken: TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Delivered_turn_does_not_post_fallback()
    {
        var fake = new TelegramTransportTests.FakeTelegramBotApiClient();
        var pipeline = new RecordingSessionPipeline(sid => new List<SessionOutput>
        {
            new TextOutput("real reply") { SessionId = sid },
            new TurnCompleted { SessionId = sid, TurnNumber = new TurnNumber(1) }
        });
        var actor = CreateActor(pipeline, fake);

        WarmUp(actor);
        await pipeline.Created.WaitAsync(TestContext.Current.CancellationToken);

        await AwaitAssertAsync(() =>
        {
            var post = Assert.Single(fake.SentTexts);
            Assert.Contains("real reply", post.Text);
        }, cancellationToken: TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Failed_content_post_reports_DeliveryFailed_and_skips_fallback()
    {
        var fake = new TelegramTransportTests.FakeTelegramBotApiClient();
        fake.SendMessageFailures.Enqueue(new InvalidOperationException("content rejected by transport"));
        var pipeline = new RecordingSessionPipeline(sid => new List<SessionOutput>
        {
            new TextOutput("real reply") { SessionId = sid },
            new TurnCompleted { SessionId = sid, TurnNumber = new TurnNumber(1) },
            new TextOutput("barrier reply") { SessionId = sid },
            new TurnCompleted { SessionId = sid, TurnNumber = new TurnNumber(2) }
        });
        var actor = CreateActor(pipeline, fake);

        WarmUp(actor);
        await pipeline.Created.WaitAsync(TestContext.Current.CancellationToken);

        // The failed content post must not be followed by the fallback, and
        // the second turn must still run.
        await AwaitAssertAsync(() =>
        {
            Assert.Contains(fake.SentTexts, post => post.Text.Contains("barrier reply", StringComparison.Ordinal));
            var failures = pipeline.RecordedFeedback.OfType<DeliveryFailed>().ToList();
            Assert.Single(failures);
        }, cancellationToken: TestContext.Current.CancellationToken);

        Assert.DoesNotContain(
            fake.SentTexts,
            post => post.Text.Contains("didn't manage to produce a reply", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Reminder_turn_reports_success_to_observer()
    {
        var fake = new TelegramTransportTests.FakeTelegramBotApiClient();
        const string reminderKey = "reminder-1:123456";
        var pipeline = new RecordingSessionPipeline(sid => new List<SessionOutput>
        {
            new TextOutput("reminder output") { SessionId = sid },
            new TurnCompleted
            {
                SessionId = sid,
                TurnNumber = new TurnNumber(1),
                SourceReminderId = new ReminderId(reminderKey)
            }
        }, reactive: true);
        var actor = CreateActor(pipeline, fake);

        var observer = CreateTestProbe();
        // The trusted turn itself triggers the lazy pipeline initialization,
        // so it must be sent before waiting on pipeline.Created.
        actor.Tell(new DeliverTrustedSessionTurn(
            new SessionId($"{ChatId}/chat"),
            "run the reminder",
            ReminderSource(reminderKey, observer.Ref)));

        var result = await observer.ExpectMsgAsync<ReminderDeliveryResult>(
            TimeSpan.FromSeconds(5), cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(result.Delivered);
        Assert.Equal(ChannelType.Telegram, result.ChannelType);
    }

    [Fact]
    public async Task Reminder_turn_reports_failure_when_post_fails_without_crashing()
    {
        var fake = new TelegramTransportTests.FakeTelegramBotApiClient();
        fake.SendMessageFailures.Enqueue(new InvalidOperationException("channel api down"));
        const string reminderKey = "reminder-1:654321";
        var pipeline = new RecordingSessionPipeline(sid => new List<SessionOutput>
        {
            new TextOutput("reminder output") { SessionId = sid },
            new TurnCompleted
            {
                SessionId = sid,
                TurnNumber = new TurnNumber(1),
                SourceReminderId = new ReminderId(reminderKey)
            }
        }, reactive: true);
        var actor = CreateActor(pipeline, fake);

        var observer = CreateTestProbe();
        // The trusted turn itself triggers the lazy pipeline initialization,
        // so it must be sent before waiting on pipeline.Created.
        actor.Tell(new DeliverTrustedSessionTurn(
            new SessionId($"{ChatId}/chat"),
            "run the reminder",
            ReminderSource(reminderKey, observer.Ref)));

        var result = await observer.ExpectMsgAsync<ReminderDeliveryResult>(
            TimeSpan.FromSeconds(5), cancellationToken: TestContext.Current.CancellationToken);
        Assert.False(result.Delivered);

        // The post failure escalates through DeliveryFailed and the actor
        // survives to process the next turn.
        Assert.Contains(
            pipeline.RecordedFeedback.OfType<DeliveryFailed>(),
            failure => failure.ErrorMessage.Contains("channel api down"));
    }

    [Fact]
    public async Task Approval_prompt_post_failure_auto_denies()
    {
        var fake = new TelegramTransportTests.FakeTelegramBotApiClient();
        fake.SendMessageFailures.Enqueue(new InvalidOperationException("prompt post failed"));
        var sid = new SessionId($"{ChatId}/chat");
        var pipeline = new RecordingSessionPipeline(_ => new List<SessionOutput>
        {
            new ToolInteractionRequest
            {
                SessionId = sid,
                Kind = "approval",
                CallId = new ToolCallId("call-auto-deny"),
                ToolName = new ToolName("execute_shell"),
                DisplayText = "dangerous command",
                Options =
                [
                    new ToolInteractionOption(ApprovalOptionKeys.ApproveOnceKey, ApprovalOptionKeys.ApproveOnceLabel),
                    new ToolInteractionOption(ApprovalOptionKeys.DenyKey, ApprovalOptionKeys.DenyLabel)
                ]
            }
        });
        var actor = CreateActor(pipeline, fake);

        WarmUp(actor);
        await pipeline.Created.WaitAsync(TestContext.Current.CancellationToken);

        await AwaitAssertAsync(() =>
        {
            var deny = Assert.Single(pipeline.RecordedFeedback.OfType<ToolInteractionResponse>());
            Assert.Equal("call-auto-deny", deny.CallId.Value);
            Assert.Equal(ApprovalOptionKeys.Deny, deny.SelectedKey.Value);
        }, cancellationToken: TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Processing_state_with_no_registry_does_not_crash_actor()
    {
        var fake = new TelegramTransportTests.FakeTelegramBotApiClient();
        var pipeline = new RecordingSessionPipeline(sid => new List<SessionOutput>
        {
            new ProcessingStateOutput(true) { SessionId = sid },
            new TurnCompleted { SessionId = sid, TurnNumber = new TurnNumber(1) }
        });
        var actor = CreateActor(pipeline, fake);

        WarmUp(actor);
        await pipeline.Created.WaitAsync(TestContext.Current.CancellationToken);

        await AwaitAssertAsync(() =>
            Assert.Single(fake.SentTexts), cancellationToken: TestContext.Current.CancellationToken);

        var probe = CreateTestProbe();
        probe.Watch(actor);
        Assert.False(probe.HasMessages);
    }

    private IActorRef CreateActor(
        RecordingSessionPipeline pipeline,
        TelegramTransportTests.FakeTelegramBotApiClient fake)
    {
        var options = new TelegramChannelOptions
        {
            Enabled = true,
            BotToken = new SensitiveString("12345:test-token"),
            AllowedChatIds = [ChatId]
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
            ChannelRegistry: null);

        return Sys.ActorOf(TelegramSessionBindingActor.CreateProps(
            new SessionId($"{ChatId}/chat"),
            new TelegramChatId(77),
            dependencies));
    }

    // The binding initializes its pipeline lazily on the first entry point;
    // a trusted turn is the entry point that needs no channel UI round trip.
    private static void WarmUp(IActorRef actor) =>
        actor.Tell(new DeliverTrustedSessionTurn(
            new SessionId($"{ChatId}/chat"),
            "warm up",
            ReminderSource("warmup:1", deliveryObserver: null)));

    private static MessageSource ReminderSource(string reminderKey, IActorRef? deliveryObserver) => new()
    {
        ChannelType = ChannelType.Telegram,
        SenderId = new SenderId("reminder-system"),
        MessageId = $"telegram:{reminderKey}",
        Audience = TrustAudience.Public,
        Boundary = TrustBoundary.TrustedInstance,
        Principal = PrincipalClassification.VerifiedAutomation,
        Provenance = new SourceProvenance(TransportAuthenticity.LocalProcess, PayloadTaint.Trusted),
        ReceivedAt = DateTimeOffset.UtcNow,
        ReminderId = new ReminderId(reminderKey),
        DeliveryObserver = deliveryObserver
    };
}
