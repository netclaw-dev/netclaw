// -----------------------------------------------------------------------
// <copyright file="TelegramSessionBindingApprovalTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Akka.Actor;
using Akka.Hosting;
using Akka.Hosting.TestKit;
using Akka.Persistence.Hosting;
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
using Telegram.Bot.Types.ReplyMarkups;
using Xunit;
using static Netclaw.Actors.Sessions.SessionProtocol;
using static Netclaw.Actors.Reminders.ReminderProtocol;

namespace Netclaw.Actors.Tests.Channels;

/// <summary>
/// Focused coverage for the Telegram approval flow on the shared
/// ApprovalResponseFlow machinery. Telegram resolves a callback by the
/// prompt's Telegram message id, and callback_data carries only the option
/// key, so requester and call identity come from the query sender and the
/// recovered pending request — never from the button payload.
/// </summary>
public sealed class TelegramSessionBindingApprovalTests(ITestOutputHelper output) : TestKit(output: output)
{
    private const long ChatId = 77;
    private const long RequesterId = 1001;

    protected override void ConfigureAkka(AkkaConfigurationBuilder builder, IServiceProvider provider)
    {
        // The binding journals pending-approval prompts, so the test system
        // needs the same persistence setup as the channel contract tests.
        builder.WithInMemoryJournal().WithInMemorySnapshotStore().WithNetclawSerialization();
    }

    [Fact]
    public async Task Approval_prompt_renders_option_key_only_callback_data()
    {
        var ct = TestContext.Current.CancellationToken;
        var fake = new TelegramTransportTests.FakeTelegramBotApiClient();
        var pipeline = ApprovalPipeline("call-1", requesterId: RequesterId.ToString(), principal: null);
        var actor = CreateActor(pipeline, fake);

        WarmUp(actor);
        await pipeline.Created.WaitAsync(ct);

        var buttons = await AwaitButtonsAsync(fake, ct);
        Assert.Equal(2, buttons.Length);

        // Option keys are short, pipe-free, and far below the 64-byte
        // callback_data ceiling; no opaque token remains.
        Assert.All(buttons, button =>
        {
            Assert.True(button.CallbackData!.Length < 64);
            Assert.DoesNotContain("|", button.CallbackData, StringComparison.Ordinal);
            Assert.DoesNotContain("nc_", button.CallbackData, StringComparison.Ordinal);
        });
        Assert.Equal(
            new[] { ApprovalOptionKeys.ApproveOnce, ApprovalOptionKeys.Deny },
            buttons.Select(button => button.CallbackData).ToArray());
        Assert.Equal(
            new[] { ApprovalOptionKeys.ApproveOnceLabel, ApprovalOptionKeys.DenyLabel },
            buttons.Select(button => button.Text).ToArray());
    }

    [Fact]
    public async Task Requester_button_click_approves_and_reports_to_session()
    {
        var ct = TestContext.Current.CancellationToken;
        var fake = new TelegramTransportTests.FakeTelegramBotApiClient();
        var pipeline = ApprovalPipeline("call-2", requesterId: RequesterId.ToString(), principal: null);
        var actor = CreateActor(pipeline, fake);

        WarmUp(actor);
        await pipeline.Created.WaitAsync(ct);
        var buttons = await AwaitButtonsAsync(fake, ct);
        var postedMessageId = fake.SentKeyboards.Single().MessageId;

        actor.Tell(new TelegramCallbackQuery(
            ChatId, RequesterId, postedMessageId, "q-approve", ApprovalOptionKeys.ApproveOnce));

        await AwaitAssertAsync(() =>
        {
            var feedback = pipeline.RecordedFeedback.OfType<ToolInteractionResponse>().ToList();
            Assert.Single(feedback);
            Assert.Equal("call-2", feedback[0].CallId.Value);
            Assert.Equal(ApprovalOptionKeys.ApproveOnce, feedback[0].SelectedKey.Value);
            Assert.Equal(RequesterId.ToString(), feedback[0].SenderId.Value);

            // The answer and the redraw follow the recorded feedback on the
            // actor path, so they must be polled under the same deadline.
            Assert.Contains(fake.AnsweredCallbacks, answered =>
                answered.QueryId == "q-approve"
                && answered.Text == TelegramSessionBindingActor.DecisionRecordedText
                && !answered.ShowAlert);

            // The resolved prompt is redrawn in place, so the buttons clear.
            var edit = Assert.Single(fake.EditedMessages);
            Assert.Equal(postedMessageId, edit.MessageId);
            Assert.Contains("Tool approval resolved", edit.Text, StringComparison.Ordinal);
        }, cancellationToken: ct);
    }

    [Fact]
    public async Task Wrong_requester_button_click_is_rejected_without_feedback()
    {
        var ct = TestContext.Current.CancellationToken;
        var fake = new TelegramTransportTests.FakeTelegramBotApiClient();
        var pipeline = ApprovalPipeline("call-3", requesterId: RequesterId.ToString(), principal: null);
        var actor = CreateActor(pipeline, fake);

        WarmUp(actor);
        await pipeline.Created.WaitAsync(ct);
        await AwaitButtonsAsync(fake, ct);
        var postedMessageId = fake.SentKeyboards.Single().MessageId;

        actor.Tell(new TelegramCallbackQuery(
            ChatId, RequesterId + 1, postedMessageId, "q-wrong", ApprovalOptionKeys.ApproveOnce));

        await AwaitAssertAsync(() =>
        {
            Assert.Contains(fake.AnsweredCallbacks, answered =>
                answered.QueryId == "q-wrong"
                && answered.Text == TelegramSessionBindingActor.WrongRequesterText
                && answered.ShowAlert);
        }, cancellationToken: ct);

        Assert.Empty(pipeline.RecordedFeedback.OfType<ToolInteractionResponse>());
        Assert.Empty(fake.EditedMessages);
    }

    [Fact]
    public async Task Resolved_approval_cannot_be_reused()
    {
        var ct = TestContext.Current.CancellationToken;
        var fake = new TelegramTransportTests.FakeTelegramBotApiClient();
        var pipeline = ApprovalPipeline("call-4", requesterId: RequesterId.ToString(), principal: null);
        var actor = CreateActor(pipeline, fake);

        WarmUp(actor);
        await pipeline.Created.WaitAsync(ct);
        await AwaitButtonsAsync(fake, ct);
        var postedMessageId = fake.SentKeyboards.Single().MessageId;

        actor.Tell(new TelegramCallbackQuery(
            ChatId, RequesterId, postedMessageId, "q-first", ApprovalOptionKeys.ApproveOnce));
        await AwaitAssertAsync(() =>
        {
            Assert.Single(pipeline.RecordedFeedback.OfType<ToolInteractionResponse>());
        }, cancellationToken: ct);

        // A replayed click on the resolved prompt fails closed as expired.
        actor.Tell(new TelegramCallbackQuery(
            ChatId, RequesterId, postedMessageId, "q-replay", ApprovalOptionKeys.ApproveOnce));

        await AwaitAssertAsync(() =>
        {
            Assert.Contains(fake.AnsweredCallbacks, answered =>
                answered.QueryId == "q-replay"
                && answered.Text == TelegramSessionBindingActor.ExpiredApprovalText
                && answered.ShowAlert);
        }, cancellationToken: ct);

        Assert.Single(pipeline.RecordedFeedback.OfType<ToolInteractionResponse>());
    }

    [Fact]
    public async Task Unknown_message_id_fails_closed()
    {
        var ct = TestContext.Current.CancellationToken;
        var fake = new TelegramTransportTests.FakeTelegramBotApiClient();
        var pipeline = ApprovalPipeline("call-5", requesterId: RequesterId.ToString(), principal: null);
        var actor = CreateActor(pipeline, fake);

        WarmUp(actor);
        await pipeline.Created.WaitAsync(ct);
        await AwaitButtonsAsync(fake, ct);

        actor.Tell(new TelegramCallbackQuery(
            ChatId, RequesterId, MessageId: 424242, "q-stale", ApprovalOptionKeys.ApproveOnce));

        await AwaitAssertAsync(() =>
        {
            Assert.Contains(fake.AnsweredCallbacks, answered =>
                answered.QueryId == "q-stale"
                && answered.Text == TelegramSessionBindingActor.ExpiredApprovalText
                && answered.ShowAlert);
        }, cancellationToken: ct);

        Assert.Empty(pipeline.RecordedFeedback.OfType<ToolInteractionResponse>());
    }

    [Fact]
    public async Task Unknown_option_key_fails_closed()
    {
        var ct = TestContext.Current.CancellationToken;
        var fake = new TelegramTransportTests.FakeTelegramBotApiClient();
        var pipeline = ApprovalPipeline("call-6", requesterId: RequesterId.ToString(), principal: null);
        var actor = CreateActor(pipeline, fake);

        WarmUp(actor);
        await pipeline.Created.WaitAsync(ct);
        await AwaitButtonsAsync(fake, ct);
        var postedMessageId = fake.SentKeyboards.Single().MessageId;

        actor.Tell(new TelegramCallbackQuery(
            ChatId, RequesterId, postedMessageId, "q-bad-key", "approve_everywhere"));

        await AwaitAssertAsync(() =>
        {
            Assert.Contains(fake.AnsweredCallbacks, answered =>
                answered.QueryId == "q-bad-key"
                && answered.Text == TelegramSessionBindingActor.ExpiredApprovalText
                && answered.ShowAlert);
        }, cancellationToken: ct);

        Assert.Empty(pipeline.RecordedFeedback.OfType<ToolInteractionResponse>());
    }

    [Fact]
    public async Task Automation_originated_approval_is_accepted_from_any_user()
    {
        var ct = TestContext.Current.CancellationToken;
        var fake = new TelegramTransportTests.FakeTelegramBotApiClient();
        var pipeline = ApprovalPipeline(
            "call-auto",
            requesterId: "reminder-system",
            principal: PrincipalClassification.VerifiedAutomation);
        var actor = CreateActor(pipeline, fake);

        WarmUp(actor);
        await pipeline.Created.WaitAsync(ct);
        await AwaitButtonsAsync(fake, ct);
        var postedMessageId = fake.SentKeyboards.Single().MessageId;

        actor.Tell(new TelegramCallbackQuery(
            ChatId, UserId: 3003, postedMessageId, "q-auto", ApprovalOptionKeys.ApproveOnce));

        await AwaitAssertAsync(() =>
        {
            var feedback = pipeline.RecordedFeedback.OfType<ToolInteractionResponse>().ToList();
            Assert.Single(feedback);
            Assert.Equal("call-auto", feedback[0].CallId.Value);
            Assert.Equal("3003", feedback[0].SenderId.Value);

            // The answer follows the recorded feedback on the actor path, so
            // it must be polled under the same deadline.
            Assert.Contains(fake.AnsweredCallbacks, answered =>
                answered.QueryId == "q-auto"
                && answered.Text == TelegramSessionBindingActor.DecisionRecordedText);
        }, cancellationToken: ct);
    }

    [Fact]
    public async Task Feedback_failure_answers_with_alert_and_keeps_actor_alive()
    {
        var ct = TestContext.Current.CancellationToken;
        var fake = new TelegramTransportTests.FakeTelegramBotApiClient();
        var pipeline = ApprovalPipeline("call-dead", requesterId: RequesterId.ToString(), principal: null);
        pipeline.ResponseFactory = (_, _) => throw new InvalidOperationException("session feedback pipe dead");
        var actor = CreateActor(pipeline, fake);

        WarmUp(actor);
        await pipeline.Created.WaitAsync(ct);
        await AwaitButtonsAsync(fake, ct);
        var postedMessageId = fake.SentKeyboards.Single().MessageId;

        actor.Tell(new TelegramCallbackQuery(
            ChatId, RequesterId, postedMessageId, "q-dead", ApprovalOptionKeys.ApproveOnce));

        await AwaitAssertAsync(() =>
        {
            Assert.Contains(fake.AnsweredCallbacks, answered =>
                answered.QueryId == "q-dead"
                && answered.Text == TelegramSessionBindingActor.FeedbackFailedText
                && answered.ShowAlert);
        }, cancellationToken: ct);

        // The actor survives the failed round trip and still answers callbacks.
        actor.Tell(new TelegramCallbackQuery(
            ChatId, RequesterId, MessageId: 999999, "q-liveness", ApprovalOptionKeys.Deny));
        await AwaitAssertAsync(() =>
        {
            Assert.Contains(fake.AnsweredCallbacks, answered =>
                answered.QueryId == "q-liveness"
                && answered.Text == TelegramSessionBindingActor.ExpiredApprovalText);
        }, cancellationToken: ct);

        var watcher = CreateTestProbe();
        watcher.Watch(actor);
        Assert.False(watcher.HasMessages);
    }

    [Fact]
    public async Task Resolved_prompt_edit_failure_keeps_acknowledgment()
    {
        var ct = TestContext.Current.CancellationToken;
        var fake = new TelegramTransportTests.FakeTelegramBotApiClient
        {
            EditMessageTextFailure = new InvalidOperationException("edit rejected by transport")
        };
        var pipeline = ApprovalPipeline("call-edit", requesterId: RequesterId.ToString(), principal: null);
        var actor = CreateActor(pipeline, fake);

        WarmUp(actor);
        await pipeline.Created.WaitAsync(ct);
        await AwaitButtonsAsync(fake, ct);
        var postedMessageId = fake.SentKeyboards.Single().MessageId;

        actor.Tell(new TelegramCallbackQuery(
            ChatId, RequesterId, postedMessageId, "q-edit", ApprovalOptionKeys.ApproveOnce));

        await AwaitAssertAsync(() =>
        {
            var feedback = pipeline.RecordedFeedback.OfType<ToolInteractionResponse>().ToList();
            Assert.Single(feedback);
            Assert.Contains(fake.AnsweredCallbacks, answered =>
                answered.QueryId == "q-edit"
                && answered.Text == TelegramSessionBindingActor.DecisionRecordedText);
        }, cancellationToken: ct);
    }

    [Fact]
    public async Task Cold_callback_after_restart_resolves_from_journal()
    {
        var ct = TestContext.Current.CancellationToken;
        var fake = new TelegramTransportTests.FakeTelegramBotApiClient();
        const string callId = "call-recover";
        var pipeline1 = ApprovalPipeline(callId, requesterId: RequesterId.ToString(), principal: null);
        var actor1 = CreateActor(pipeline1, fake);

        WarmUp(actor1);
        await pipeline1.Created.WaitAsync(ct);
        await AwaitButtonsAsync(fake, ct);
        var postedMessageId = fake.SentKeyboards.Single().MessageId;

        // A command processed after the prompt post proves the journal write
        // for the tracked prompt completed before the restart below.
        actor1.Tell(new TelegramCallbackQuery(
            ChatId, RequesterId, postedMessageId + 500, "q-barrier", ApprovalOptionKeys.Deny));
        await AwaitAssertAsync(() =>
        {
            Assert.Contains(fake.AnsweredCallbacks, answered =>
                answered.QueryId == "q-barrier"
                && answered.Text == TelegramSessionBindingActor.ExpiredApprovalText);
        }, cancellationToken: ct);
        Assert.Empty(pipeline1.RecordedFeedback.OfType<ToolInteractionResponse>());

        // Passivation/restart: a new binding actor for the same session replays
        // the tracked prompt from the journal and resolves the same message id.
        await actor1.GracefulStop(TimeSpan.FromSeconds(10));

        var pipeline2 = new RecordingSessionPipeline(_ => []);
        var actor2 = CreateActor(pipeline2, fake);
        actor2.Tell(new TelegramCallbackQuery(
            ChatId, RequesterId, postedMessageId, "q-cold", ApprovalOptionKeys.ApproveOnce));

        await AwaitAssertAsync(() =>
        {
            var feedback = pipeline2.RecordedFeedback.OfType<ToolInteractionResponse>().ToList();
            Assert.Single(feedback);
            Assert.Equal(callId, feedback[0].CallId.Value);
            Assert.Equal(ApprovalOptionKeys.ApproveOnce, feedback[0].SelectedKey.Value);
            Assert.Equal(RequesterId.ToString(), feedback[0].SenderId.Value);

            // The answer follows the recorded feedback on the actor path, so
            // it must be polled under the same deadline.
            Assert.Contains(fake.AnsweredCallbacks, answered =>
                answered.QueryId == "q-cold"
                && answered.Text == TelegramSessionBindingActor.DecisionRecordedText);
        }, cancellationToken: ct);
    }

    // --- helpers ---

    private static RecordingSessionPipeline ApprovalPipeline(
        string callId,
        string requesterId,
        PrincipalClassification? principal)
    {
        var sid = new SessionId($"{ChatId}/chat");
        return new RecordingSessionPipeline(_ =>
        [
            new ToolInteractionRequest
            {
                SessionId = sid,
                Kind = "approval",
                CallId = new ToolCallId(callId),
                ToolName = new ToolName("execute_shell"),
                DisplayText = "rm -rf /tmp",
                RequesterSenderId = new SenderId(requesterId),
                RequesterPrincipal = principal,
                Options =
                [
                    new ToolInteractionOption(ApprovalOptionKeys.ApproveOnceKey, ApprovalOptionKeys.ApproveOnceLabel),
                    new ToolInteractionOption(ApprovalOptionKeys.DenyKey, ApprovalOptionKeys.DenyLabel)
                ]
            }
        ]);
    }

    private async Task<InlineKeyboardButton[]> AwaitButtonsAsync(
        TelegramTransportTests.FakeTelegramBotApiClient fake,
        CancellationToken ct)
    {
        await AwaitAssertAsync(() => Assert.Single(fake.SentKeyboards), cancellationToken: ct);
        return fake.SentKeyboards.Single().Rows.SelectMany(row => row).ToArray();
    }

    private IActorRef CreateActor(
        RecordingSessionPipeline pipeline,
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
            ChannelRegistry: null);

        return Sys.ActorOf(TelegramSessionBindingActor.CreateProps(
            new SessionId($"{ChatId}/chat"),
            new TelegramChatId(ChatId),
            dependencies));
    }

    // The binding initializes its pipeline lazily on the first entry point;
    // a trusted turn is the entry point that needs no channel UI round trip.
    private static void WarmUp(IActorRef actor) =>
        actor.Tell(new DeliverTrustedSessionTurn(
            new SessionId($"{ChatId}/chat"),
            "warm up",
            ReminderSource("warmup:approval")));

    private static MessageSource ReminderSource(string reminderKey) => new()
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
        DeliveryObserver = null
    };
}
