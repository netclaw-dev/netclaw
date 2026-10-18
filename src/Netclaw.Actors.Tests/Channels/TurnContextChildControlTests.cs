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
/// Issue #2524. A reminder turn manages the child runs of its own session. The comparison for child status
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

    [Fact]
    public void A_person_does_not_control_a_run_that_a_reminder_turn_started()
    {
        // Unchanged rule: the relaxation reads the requester only.
        Assert.False(Person().CanControlChildRunOf(Reminder()));
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
}
