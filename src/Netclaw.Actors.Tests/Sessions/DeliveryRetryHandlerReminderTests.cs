// -----------------------------------------------------------------------
// <copyright file="DeliveryRetryHandlerReminderTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Actors.Protocol;
using Netclaw.Actors.Reminders;
using Netclaw.Actors.Sessions.Handlers;
using Xunit;

namespace Netclaw.Actors.Tests.Sessions;

/// <summary>
/// Spec: <c>netclaw-session</c>, "A failed post on a reminder turn is visible to the operator".
/// The handler answers two questions for a failure report. Which reminder started the named turn?
/// Did the session report that turn already? A wrong answer sends an alert for the wrong fire, or two alerts
/// for one reply. The mutation test project links this class, so a focused Stryker target can reuse it.
/// </summary>
public sealed class DeliveryRetryHandlerReminderTests
{
    private static readonly ReminderId FireA = new("check-pr:1712000000000");
    private static readonly ReminderId FireB = new("nightly:1712000600000");

    [Fact]
    public void ReminderOf_names_the_reminder_for_the_recorded_turn_only()
    {
        var handler = new DeliveryRetryHandler();
        handler.RecordTurn(new TurnNumber(2), FireA, routedSkill: false);

        var known = handler.ReminderOf(new TurnNumber(2));
        Assert.Equal(ReminderLookupKind.Reminder, known.Kind);
        Assert.Equal(FireA, known.FireKey);

        // The previous turn and a later turn are not the recorded turn. The handler cannot tell what started them.
        Assert.Equal(ReminderLookupKind.UnknownTurn, handler.ReminderOf(new TurnNumber(1)).Kind);
        Assert.Equal(ReminderLookupKind.UnknownTurn, handler.ReminderOf(new TurnNumber(3)).Kind);
        Assert.Null(handler.ReminderOf(new TurnNumber(1)).FireKey);
    }

    [Fact]
    public void ReminderOf_separates_a_turn_without_a_reminder_from_an_unknown_turn()
    {
        var handler = new DeliveryRetryHandler();
        handler.RecordTurn(new TurnNumber(5), null, routedSkill: false);

        var ordinary = handler.ReminderOf(new TurnNumber(5));
        Assert.Equal(ReminderLookupKind.NoReminder, ordinary.Kind);
        Assert.Null(ordinary.FireKey);
        Assert.Equal(ReminderLookupKind.UnknownTurn, handler.ReminderOf(new TurnNumber(4)).Kind);
    }

    [Fact]
    public void A_newer_recorded_turn_replaces_the_older_one()
    {
        var handler = new DeliveryRetryHandler();
        handler.RecordTurn(new TurnNumber(2), FireA, routedSkill: false);
        handler.RecordTurn(new TurnNumber(3), FireB, routedSkill: false);

        Assert.Equal(ReminderLookupKind.UnknownTurn, handler.ReminderOf(new TurnNumber(2)).Kind);
        Assert.Equal(FireB, handler.ReminderOf(new TurnNumber(3)).FireKey);
    }

    [Fact]
    public void Clear_keeps_the_recorded_turn()
    {
        // A new user input calls Clear. Late feedback for the earlier turn must still find its reminder.
        var handler = new DeliveryRetryHandler();
        handler.RecordTurn(new TurnNumber(2), FireA, routedSkill: false);

        handler.Clear();

        Assert.Equal(FireA, handler.ReminderOf(new TurnNumber(2)).FireKey);
    }

    [Fact]
    public void TryMarkReported_returns_true_once_for_a_reminder_turn()
    {
        var handler = new DeliveryRetryHandler();
        handler.RecordTurn(new TurnNumber(2), FireA, routedSkill: false);

        Assert.True(handler.TryMarkReported(new TurnNumber(2)));
        Assert.False(handler.TryMarkReported(new TurnNumber(2)));
        Assert.False(handler.TryMarkReported(new TurnNumber(2)));
    }

    [Fact]
    public void TryMarkReported_refuses_a_turn_that_is_not_the_recorded_reminder_turn()
    {
        var handler = new DeliveryRetryHandler();
        handler.RecordTurn(new TurnNumber(2), FireA, routedSkill: false);

        // A wrong turn must not use up the mark of the right turn.
        Assert.False(handler.TryMarkReported(new TurnNumber(1)));
        Assert.True(handler.TryMarkReported(new TurnNumber(2)));

        // A turn that no reminder started has nothing to report.
        handler.RecordTurn(new TurnNumber(3), null, routedSkill: false);
        Assert.False(handler.TryMarkReported(new TurnNumber(3)));
    }

    [Fact]
    public void A_newly_recorded_turn_starts_with_a_fresh_mark()
    {
        // A retry turn of the same reminder has a new number. Its own final failure gets its own alert.
        var handler = new DeliveryRetryHandler();
        handler.RecordTurn(new TurnNumber(2), FireA, routedSkill: false);
        Assert.True(handler.TryMarkReported(new TurnNumber(2)));

        handler.RecordTurn(new TurnNumber(3), FireA, routedSkill: false);

        Assert.True(handler.TryMarkReported(new TurnNumber(3)));
        Assert.False(handler.TryMarkReported(new TurnNumber(3)));
    }

    [Fact]
    public void IsRoutedSkill_is_true_only_for_the_recorded_routed_turn()
    {
        // A routed-skill turn is eligible for the alert but never for a retry on the main model.
        var handler = new DeliveryRetryHandler();
        handler.RecordTurn(new TurnNumber(2), FireA, routedSkill: true);

        Assert.True(handler.IsRoutedSkill(new TurnNumber(2)));
        Assert.False(handler.IsRoutedSkill(new TurnNumber(1)));
        Assert.False(handler.IsRoutedSkill(new TurnNumber(3)));

        // The reminder lookup does not change for a routed turn.
        Assert.Equal(FireA, handler.ReminderOf(new TurnNumber(2)).FireKey);
    }

    [Fact]
    public void A_newer_recorded_turn_replaces_the_routed_mark()
    {
        var handler = new DeliveryRetryHandler();
        handler.RecordTurn(new TurnNumber(2), FireA, routedSkill: true);

        handler.RecordTurn(new TurnNumber(3), FireA, routedSkill: false);

        Assert.False(handler.IsRoutedSkill(new TurnNumber(3)));
        Assert.False(handler.IsRoutedSkill(new TurnNumber(2)));
    }

    [Fact]
    public void Clear_keeps_the_routed_mark()
    {
        // A new user input calls Clear. Late feedback for the routed turn must still not retry.
        var handler = new DeliveryRetryHandler();
        handler.RecordTurn(new TurnNumber(2), null, routedSkill: true);

        handler.Clear();

        Assert.True(handler.IsRoutedSkill(new TurnNumber(2)));
    }

    // --- A retry turn keeps the reminder link of the turn that it repeats ---
    // A child-delivery turn has no input, so its retry turn has no input source. The handler holds the link from
    // the retry until the session records the next turn. A link that leaks past that turn would name a reminder
    // for a person's turn. A link that is lost would hide the final failure of the retry turn.

    [Fact]
    public void A_pending_retry_keeps_the_link_of_the_turn_that_it_repeats()
    {
        var handler = new DeliveryRetryHandler();
        handler.RecordTurn(new TurnNumber(2), FireA, routedSkill: false);

        handler.RecordRetry();

        Assert.Equal(FireA, handler.RetriedTurnReminder);
    }

    [Fact]
    public void No_retry_means_no_retry_link()
    {
        var handler = new DeliveryRetryHandler();
        handler.RecordTurn(new TurnNumber(2), FireA, routedSkill: false);

        Assert.Null(handler.RetriedTurnReminder);
    }

    [Fact]
    public void The_next_recorded_turn_ends_the_retry_link()
    {
        var handler = new DeliveryRetryHandler();
        handler.RecordTurn(new TurnNumber(2), FireA, routedSkill: false);
        handler.RecordRetry();

        // The retry turn is recorded. A later turn must not take the link again.
        handler.RecordTurn(new TurnNumber(3), FireA, routedSkill: false);

        Assert.Null(handler.RetriedTurnReminder);
    }

    [Fact]
    public void Clear_ends_the_retry_link()
    {
        // A new input or a failed retry turn ends the retry chain, and the link with it.
        var handler = new DeliveryRetryHandler();
        handler.RecordTurn(new TurnNumber(2), FireA, routedSkill: false);
        handler.RecordRetry();

        handler.Clear();

        Assert.Null(handler.RetriedTurnReminder);
    }
}
