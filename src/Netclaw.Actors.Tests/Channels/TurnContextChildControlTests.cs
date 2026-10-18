// -----------------------------------------------------------------------
// <copyright file="TurnContextChildControlTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Actors.Channels;
using Netclaw.Actors.Protocol;
using Netclaw.Configuration;
using Netclaw.Tools;
using Xunit;
using static Netclaw.Actors.Sessions.SessionProtocol;

namespace Netclaw.Actors.Tests.Channels;

/// <summary>
/// Issue #2524. A reminder turn and any other turn of one session manage each other's child runs. The comparison for child status
/// and cancel is <see cref="TurnContext.CanControlChildRunOf"/>. The instance is the requesting turn and the
/// argument is the turn that started the run. <see cref="TurnContext.HasSameRequester"/> keeps its meaning,
/// because input batching also uses it (see <see cref="TurnContextRequesterTests"/>).
/// The focused mutation gate links this file.
/// Source: openspec/changes/reminder-turn-manages-own-children, design D1.
/// </summary>
public sealed class TurnContextChildControlTests
{
    [Fact]
    public void A_reminder_turn_controls_a_run_that_a_person_started_in_the_same_session()
    {
        // The exact shape of the swap-1 exercise: sender local and Operator started the run; the fire has
        // sender reminder-system and VerifiedAutomation.
        Assert.True(Reminder().CanControlChildRunOf(Person()));
    }

    [Theory]
    [InlineData("session")]
    [InlineData("audience")]
    [InlineData("boundary")]
    [InlineData("channel")]
    public void A_reminder_turn_that_differs_in_a_kept_fact_does_not_control_the_run(string field)
    {
        var owner = Person();
        var reminder = field switch
        {
            "session" => Reminder() with { SessionId = new SessionId("other-session") },
            "audience" => Reminder() with { Audience = TrustAudience.Team },
            "boundary" => Reminder() with { Boundary = TrustBoundary.Public },
            "channel" => Reminder() with { ChannelType = ChannelType.Slack },
            _ => throw new ArgumentOutOfRangeException(nameof(field), field, null)
        };

        Assert.False(reminder.CanControlChildRunOf(owner));
    }

    // A reminder turn ignores the sender and the principal of the owner. A sender that the owner lacks, a
    // missing sender, and another principal all give the same answer.
    [Theory]
    [InlineData("alice-slack-id", PrincipalClassification.Operator)]
    [InlineData("alice-slack-id", PrincipalClassification.TrustedInternal)]
    [InlineData(null, PrincipalClassification.Operator)]
    [InlineData("reminder-system", PrincipalClassification.VerifiedAutomation)]
    public void A_reminder_turn_ignores_the_sender_and_the_principal_of_the_owner(string? sender, PrincipalClassification principal)
    {
        var owner = Person() with
        {
            RequesterSenderId = sender is null ? null : new SenderId(sender),
            RequesterPrincipal = principal
        };

        Assert.True(Reminder().CanControlChildRunOf(owner));
    }

    // These fields describe one message. A reminder fire differs from the person turn in all of them.
    [Fact]
    public void A_reminder_turn_ignores_the_per_turn_facts()
    {
        var owner = Person() with
        {
            HasAdoptedContext = true,
            AdoptedSpeakerIds = ["local"],
            DefaultDeliveryTarget = new ChannelDeliveryTargetInfo("tui", "session", "s1"),
            SupportsInteractiveApproval = true
        };
        var reminder = Reminder() with { SupportsInteractiveApproval = false, TurnId = new TurnId("reminder:1") };

        Assert.True(reminder.CanControlChildRunOf(owner));
    }

    // Owner answer of 18 October 2026: the relaxation is symmetric. A person turn of the same session controls
    // a run that a reminder turn started, whatever the sender of the person.
    [Theory]
    [InlineData("local")]
    [InlineData("bob")]
    public void A_person_controls_a_run_that_a_reminder_turn_started_in_the_same_session(string sender)
    {
        var person = Person() with { RequesterSenderId = new SenderId(sender) };

        Assert.True(person.CanControlChildRunOf(Reminder()));
    }

    [Theory]
    [InlineData("session")]
    [InlineData("audience")]
    [InlineData("boundary")]
    [InlineData("channel")]
    public void A_person_that_differs_in_a_kept_fact_does_not_control_a_run_that_a_reminder_turn_started(string field)
    {
        var person = field switch
        {
            "session" => Person() with { SessionId = new SessionId("other-session") },
            "audience" => Person() with { Audience = TrustAudience.Team },
            "boundary" => Person() with { Boundary = TrustBoundary.Public },
            "channel" => Person() with { ChannelType = ChannelType.Slack },
            _ => throw new ArgumentOutOfRangeException(nameof(field), field, null)
        };

        Assert.False(person.CanControlChildRunOf(Reminder()));
    }

    [Fact]
    public void A_later_reminder_turn_controls_a_run_that_a_reminder_turn_started()
    {
        Assert.True(Reminder().CanControlChildRunOf(Reminder() with { TurnId = new TurnId("reminder:2") }));
    }

    [Fact]
    public void Another_person_in_the_same_session_does_not_control_the_run()
    {
        var bob = Person() with { RequesterSenderId = new SenderId("bob") };

        Assert.False(bob.CanControlChildRunOf(Person()));
    }

    // The rule names the reminder turn, not a person. Any other turn of the session, such as the delivery turn
    // of a background job (sender background-job-system, VerifiedAutomation, source kind background-job),
    // manages a run that a reminder turn started, and the reminder turn manages a run that such a turn started.
    [Fact]
    public void A_background_job_turn_controls_a_run_that_a_reminder_turn_started_in_the_same_session()
    {
        Assert.True(BackgroundJob().CanControlChildRunOf(Reminder()));
    }

    [Fact]
    public void A_reminder_turn_controls_a_run_that_a_background_job_turn_started_in_the_same_session()
    {
        Assert.True(Reminder().CanControlChildRunOf(BackgroundJob()));
    }

    [Theory]
    [InlineData("session")]
    [InlineData("audience")]
    [InlineData("boundary")]
    [InlineData("channel")]
    public void A_background_job_turn_that_differs_in_a_kept_fact_does_not_control_a_run_that_a_reminder_turn_started(string field)
    {
        var job = field switch
        {
            "session" => BackgroundJob() with { SessionId = new SessionId("other-session") },
            "audience" => BackgroundJob() with { Audience = TrustAudience.Team },
            "boundary" => BackgroundJob() with { Boundary = TrustBoundary.Public },
            "channel" => BackgroundJob() with { ChannelType = ChannelType.Slack },
            _ => throw new ArgumentOutOfRangeException(nameof(field), field, null)
        };

        Assert.False(job.CanControlChildRunOf(Reminder()));
    }

    // A background job turn is not a reminder turn, so two such turns keep the six-fact rule.
    [Fact]
    public void A_background_job_turn_does_not_control_a_run_that_a_person_started()
    {
        Assert.False(BackgroundJob().CanControlChildRunOf(Person()));
        Assert.False(Person().CanControlChildRunOf(BackgroundJob()));
    }

    // S1 of the review of PR #2550. A webhook sender sets the source kind through the event header or the route
    // name, and WebhookExecutionActor gives the turn VerifiedAutomation. The source kind alone must not make a
    // reminder turn. The webhook turn has the sender "webhook:<route>" and the authenticity Verified. The
    // reminder fire has the sender "reminder-system" and the authenticity LocalProcess, and no webhook sets them.
    [Theory]
    [InlineData("webhook:github")]
    [InlineData("webhook:reminder")]
    public void A_webhook_turn_with_the_reminder_source_kind_is_not_a_reminder_turn(string sender)
    {
        var webhook = Webhook(sender);
        var bob = Person() with { RequesterSenderId = new SenderId("bob") };

        // As the requester: it cannot manage the run of a person.
        Assert.False(webhook.CanControlChildRunOf(Person()));
        Assert.False(webhook.CanControlChildRunOf(bob));
        // As the run starter: a person of another sender cannot manage its run.
        Assert.False(bob.CanControlChildRunOf(webhook));
        Assert.False(Person().CanControlChildRunOf(webhook));
        // It cannot manage the run of another automation turn either.
        Assert.False(webhook.CanControlChildRunOf(BackgroundJob()));
    }

    [Fact]
    public void A_webhook_turn_with_the_reminder_source_kind_keeps_the_six_fact_rule_with_itself()
    {
        var webhook = Webhook("webhook:github");

        Assert.True((webhook with { TurnId = new TurnId("delivery-2") }).CanControlChildRunOf(webhook));
        Assert.False(Webhook("webhook:other").CanControlChildRunOf(webhook));
    }

    // Both facts make a reminder turn. One of them alone gets the six-fact rule.
    [Fact]
    public void Automation_with_another_source_kind_is_not_a_reminder_turn()
    {
        var webhook = Reminder() with
        {
            Provenance = new SourceProvenance(TransportAuthenticity.LocalProcess, PayloadTaint.Trusted)
            { SourceKind = new SourceKind("webhook") }
        };
        var noKind = Reminder() with
        {
            Provenance = new SourceProvenance(TransportAuthenticity.LocalProcess, PayloadTaint.Trusted)
        };

        Assert.False(webhook.CanControlChildRunOf(Person()));
        Assert.False(noKind.CanControlChildRunOf(Person()));
        // The same holds when such a turn started the run.
        var bob = Person() with { RequesterSenderId = new SenderId("bob") };
        Assert.False(bob.CanControlChildRunOf(webhook));
        Assert.False(bob.CanControlChildRunOf(noKind));
    }

    [Fact]
    public void A_person_turn_that_carries_the_reminder_source_kind_is_not_a_reminder_turn()
    {
        var forged = Person() with
        {
            RequesterSenderId = new SenderId("bob"),
            Provenance = new SourceProvenance(TransportAuthenticity.Verified, PayloadTaint.Trusted)
            { SourceKind = new SourceKind("reminder") }
        };

        Assert.False(forged.CanControlChildRunOf(Person()));
    }

    // A non-reminder requester keeps the six-fact rule, so the same person still owns the run.
    [Fact]
    public void The_same_person_in_a_later_turn_controls_the_run()
    {
        Assert.True((Person() with { TurnId = new TurnId("turn-2") }).CanControlChildRunOf(Person()));
    }

    private static TurnContext Person() => new()
    {
        SessionId = new SessionId("tui-session"),
        TurnId = new TurnId("turn-1"),
        Audience = TrustAudience.Personal,
        Boundary = TrustBoundary.TrustedInstance,
        ChannelType = ChannelType.Tui,
        RequesterSenderId = new SenderId("local"),
        RequesterPrincipal = PrincipalClassification.Operator,
        Provenance = new SourceProvenance(TransportAuthenticity.Verified, PayloadTaint.Trusted)
    };

    // The source shape of ReminderExecutionActor.InitializeCurrentSessionAsync.
    private static TurnContext Reminder() => Person() with
    {
        TurnId = new TurnId("swap1-verdict-check:1"),
        RequesterSenderId = new SenderId("reminder-system"),
        RequesterPrincipal = PrincipalClassification.VerifiedAutomation,
        Provenance = new SourceProvenance(TransportAuthenticity.LocalProcess, PayloadTaint.Trusted)
        { SourceKind = new SourceKind("reminder") }
    };

    // The source shape of BackgroundJobManagerActor.DeliverResultToSession.
    private static TurnContext BackgroundJob() => Person() with
    {
        TurnId = new TurnId("background-job:1"),
        RequesterSenderId = new SenderId("background-job-system"),
        RequesterPrincipal = PrincipalClassification.VerifiedAutomation,
        Provenance = new SourceProvenance(TransportAuthenticity.LocalProcess, PayloadTaint.Trusted)
        { SourceKind = new SourceKind("background-job") }
    };

    // The source shape of WebhookExecutionActor: the sender and the source kind come from the webhook request.
    private static TurnContext Webhook(string sender) => Person() with
    {
        TurnId = new TurnId("delivery-1"),
        RequesterSenderId = new SenderId(sender),
        RequesterPrincipal = PrincipalClassification.VerifiedAutomation,
        Provenance = new SourceProvenance(TransportAuthenticity.Verified, PayloadTaint.Trusted)
        { SourceKind = new SourceKind("reminder") }
    };
}
