// -----------------------------------------------------------------------
// <copyright file="DeliveryRetryHandlerReminderTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Actors.Channels;
using Netclaw.Actors.Protocol;
using Netclaw.Actors.Reminders;
using Netclaw.Actors.Sessions.Handlers;
using Netclaw.Channels;
using Xunit;

namespace Netclaw.Actors.Tests.Sessions;

/// <summary>
/// Spec: <c>netclaw-session</c>, "A failed post on a reminder turn is visible to the operator".
/// The mutation project compiles this fixture with xUnit 2.
/// </summary>
public sealed class DeliveryRetryHandlerReminderTests
{
    private static readonly ReminderId FireA = new("check-pr:1712000000000");
    private static readonly ReminderId FireB = new("nightly:1712000600000");

    [Theory]
    [InlineData("content rejected", DeliveryFailureKind.ContentRejected, false, 0, (int)ReadyDeliveryDecision.Retry)]
    [InlineData("message too large", DeliveryFailureKind.MessageTooLarge, false, 0, (int)ReadyDeliveryDecision.Retry)]
    [InlineData("unsupported content", DeliveryFailureKind.UnsupportedContent, false, 0, (int)ReadyDeliveryDecision.Retry)]
    [InlineData("second retry", DeliveryFailureKind.ContentRejected, false, 1, (int)ReadyDeliveryDecision.Retry)]
    [InlineData("budget spent", DeliveryFailureKind.ContentRejected, false, 2, (int)ReadyDeliveryDecision.BudgetSpent)]
    [InlineData("budget exceeded", DeliveryFailureKind.ContentRejected, false, 3, (int)ReadyDeliveryDecision.BudgetSpent)]
    [InlineData("routed skill", DeliveryFailureKind.MessageTooLarge, true, 0, (int)ReadyDeliveryDecision.RoutedSkill)]
    [InlineData("routed skill before budget", DeliveryFailureKind.ContentRejected, true, 2, (int)ReadyDeliveryDecision.RoutedSkill)]
    [InlineData("transport failure", DeliveryFailureKind.TransportFailure, false, 0, (int)ReadyDeliveryDecision.AddNudgeAndReport)]
    [InlineData("permission denied", DeliveryFailureKind.PermissionDenied, false, 0, (int)ReadyDeliveryDecision.AddNudgeAndReport)]
    [InlineData("unknown failure", DeliveryFailureKind.Unknown, false, 0, (int)ReadyDeliveryDecision.AddNudgeAndReport)]
    [InlineData("unrecognized failure", (DeliveryFailureKind)99, false, 0, (int)ReadyDeliveryDecision.AddNudgeAndReport)]
    [InlineData("non-retryable routed skill", DeliveryFailureKind.Unknown, true, 2, (int)ReadyDeliveryDecision.AddNudgeAndReport)]
    public void Ready_feedback_selects_the_action_for_the_current_turn(
        string _, DeliveryFailureKind kind, bool routedSkill, int retryCount, int expected)
    {
        var turn = new TurnNumber(5);

        Assert.Equal((ReadyDeliveryDecision)expected,
            DeliveryRetryHandler.DecideReady(turn, kind, turn, routedSkill, retryCount));
    }

    [Theory]
    [InlineData("no eligible turn", null, DeliveryFailureKind.ContentRejected, true, 2)]
    [InlineData("earlier eligible turn", 4, DeliveryFailureKind.ContentRejected, false, 0)]
    [InlineData("later eligible turn", 6, DeliveryFailureKind.ContentRejected, true, 2)]
    [InlineData("non-retryable stale turn", 4, DeliveryFailureKind.Unknown, true, 2)]
    public void Ready_stale_feedback_is_rejected_before_any_other_decision(
        string _, int? eligible, DeliveryFailureKind kind, bool routedSkill, int retryCount)
    {
        var eligibleTurn = eligible is { } value ? new TurnNumber(value) : (TurnNumber?)null;

        Assert.Equal(ReadyDeliveryDecision.Stale,
            DeliveryRetryHandler.DecideReady(new TurnNumber(5), kind, eligibleTurn, routedSkill, retryCount));
    }

    [Theory]
    [InlineData("processing retryable", DeliveryFailureKind.ContentRejected, false, false, (int)BusyDeliveryDecision.NoRetry)]
    [InlineData("compacting retryable", DeliveryFailureKind.MessageTooLarge, true, false, (int)BusyDeliveryDecision.NoRetry)]
    [InlineData("processing unsupported", DeliveryFailureKind.UnsupportedContent, false, false, (int)BusyDeliveryDecision.NoRetry)]
    [InlineData("processing transport", DeliveryFailureKind.TransportFailure, false, false, (int)BusyDeliveryDecision.AddNudge)]
    [InlineData("processing permission", DeliveryFailureKind.PermissionDenied, false, false, (int)BusyDeliveryDecision.AddNudge)]
    [InlineData("processing unknown", DeliveryFailureKind.Unknown, false, false, (int)BusyDeliveryDecision.AddNudge)]
    [InlineData("processing unrecognized", (DeliveryFailureKind)99, false, false, (int)BusyDeliveryDecision.AddNudge)]
    [InlineData("compacting transport", DeliveryFailureKind.TransportFailure, true, false, (int)BusyDeliveryDecision.CompactionDropsNudge)]
    [InlineData("compacting unknown", DeliveryFailureKind.Unknown, true, false, (int)BusyDeliveryDecision.CompactionDropsNudge)]
    [InlineData("drain retryable", DeliveryFailureKind.ContentRejected, false, true, (int)BusyDeliveryDecision.Drain)]
    [InlineData("drain during compaction", DeliveryFailureKind.Unknown, true, true, (int)BusyDeliveryDecision.Drain)]
    [InlineData("drain non-retryable", DeliveryFailureKind.PermissionDenied, false, true, (int)BusyDeliveryDecision.Drain)]
    public void Busy_feedback_selects_the_action_for_the_phase(
        string _, DeliveryFailureKind kind, bool compacting, bool restartDrainRequested, int expected)
    {
        Assert.Equal((BusyDeliveryDecision)expected,
            DeliveryRetryHandler.DecideBusy(kind, compacting, restartDrainRequested));
    }

    [Theory]
    [InlineData(DeliveryFailureKind.Unknown)]
    [InlineData((DeliveryFailureKind)99)]
    public void Unknown_failure_gives_the_model_the_existing_guidance(DeliveryFailureKind kind)
    {
        var failure = new DeliveryFailed
        {
            SessionId = new SessionId("test-channel/unknown-failure"),
            TurnNumber = new TurnNumber(5),
            ChannelType = ChannelType.Slack,
            FailureKind = kind,
            ErrorMessage = "Unexpected channel error"
        };

        Assert.Contains("unknown delivery error", DeliveryRetryHandler.BuildNudge(failure), StringComparison.OrdinalIgnoreCase);
    }

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
