// -----------------------------------------------------------------------
// <copyright file="DeliveryRetryHandler.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Actors.Protocol;
using Netclaw.Actors.Reminders;

namespace Netclaw.Actors.Sessions.Handlers;

/// <summary>How <see cref="DeliveryRetryHandler.ReminderOf"/> answers for a turn number.</summary>
internal enum ReminderLookupKind
{
    /// <summary>The turn is not the latest recorded turn. The handler cannot tell what started it.</summary>
    UnknownTurn,

    /// <summary>The turn is the latest recorded turn, and no reminder started it.</summary>
    NoReminder,

    /// <summary>The turn is the latest recorded turn, and a reminder fire started it.</summary>
    Reminder
}

/// <summary>The answer of <see cref="DeliveryRetryHandler.ReminderOf"/>. <c>FireKey</c> has a value only for <c>Reminder</c>.</summary>
internal readonly record struct ReminderLookup(ReminderLookupKind Kind, ReminderId? FireKey);

/// <summary>
/// Manages delivery retry state: eligibility tracking, retry counting,
/// and static helper methods for building nudge messages.
/// </summary>
internal sealed class DeliveryRetryHandler
{
    public const int MaxRetries = 2;

    private TurnNumber? _eligibleTurnNumber;
    private int _retryCount;
    private bool _chainActive;
    private TurnNumber? _recordedTurnNumber;
    private ReminderId? _recordedTurnReminder;
    private bool _recordedTurnRouted;
    private bool _recordedTurnReported;
    private bool _retryPending;

    public TurnNumber? EligibleTurnNumber => _eligibleTurnNumber;
    public int RetryCount => _retryCount;
    public bool ChainActive => _chainActive;

    /// <summary>
    /// Clear all delivery retry state. Called at the start of a new user turn
    /// and when a turn fails. It also ends the link of a pending retry.
    /// </summary>
    public void Clear()
    {
        _eligibleTurnNumber = null;
        _retryCount = 0;
        _chainActive = false;
        _retryPending = false;
    }

    /// <summary>
    /// Mark the given turn as eligible for delivery retries.
    /// </summary>
    public void MarkEligible(TurnNumber turnNumber)
    {
        _eligibleTurnNumber = turnNumber;
        if (!_chainActive)
            _retryCount = 0;
    }

    /// <summary>
    /// Remember which reminder fire, if any, started the latest recorded turn, and whether a sub-agent ran
    /// a routed skill for it. <see cref="Clear"/> keeps both. A new recorded turn replaces them and clears the
    /// report mark, so the session always knows the source of the latest turn that delivery feedback can name.
    /// A recorded turn also ends the link of a pending retry, so the link reaches only the retry turn itself.
    /// The session calls this for a live turn, for a turn it replays from the journal, and when it restores a snapshot.
    /// </summary>
    public void RecordTurn(TurnNumber turnNumber, ReminderId? sourceReminder, bool routedSkill)
    {
        _recordedTurnNumber = turnNumber;
        _recordedTurnReminder = sourceReminder;
        _recordedTurnRouted = routedSkill;
        _recordedTurnReported = false;
        _retryPending = false;
    }

    /// <summary>
    /// Which reminder fire started the given turn. The answer is <see cref="ReminderLookupKind.UnknownTurn"/>
    /// when the turn is not the latest recorded turn. It does not change any state.
    /// </summary>
    public ReminderLookup ReminderOf(TurnNumber turnNumber)
    {
        if (_recordedTurnNumber != turnNumber)
            return new ReminderLookup(ReminderLookupKind.UnknownTurn, null);

        return _recordedTurnReminder is { } fireKey
            ? new ReminderLookup(ReminderLookupKind.Reminder, fireKey)
            : new ReminderLookup(ReminderLookupKind.NoReminder, null);
    }

    /// <summary>True when the given turn is the latest recorded turn and a sub-agent ran a routed skill for it.</summary>
    public bool IsRoutedSkill(TurnNumber turnNumber)
        => _recordedTurnNumber == turnNumber && _recordedTurnRouted;

    /// <summary>
    /// Marks the given reminder turn as reported. Returns true for the first call only, so a turn raises
    /// one alert. Returns false when the turn is not the latest recorded turn, when no reminder started it,
    /// or when an earlier call marked it.
    /// </summary>
    public bool TryMarkReported(TurnNumber turnNumber)
    {
        if (_recordedTurnNumber != turnNumber || _recordedTurnReminder is null || _recordedTurnReported)
            return false;

        _recordedTurnReported = true;
        return true;
    }

    /// <summary>
    /// The fire key a snapshot must carry: the reminder of the eligible turn, while that turn is the latest
    /// recorded turn and has no report. Null otherwise, so a later incarnation cannot alert twice for one reply.
    /// </summary>
    public ReminderId? UnreportedEligibleReminder
        => _eligibleTurnNumber is { } eligible && _recordedTurnNumber == eligible && !_recordedTurnReported
            ? _recordedTurnReminder
            : null;

    /// <summary>The routed mark a snapshot must carry: true when the eligible turn is a recorded routed-skill turn.</summary>
    public bool EligibleTurnIsRoutedSkill
        => _eligibleTurnNumber is { } eligible && IsRoutedSkill(eligible);

    /// <summary>
    /// Record a retry attempt. Sets the chain active flag.
    /// </summary>
    public void RecordRetry()
    {
        _retryCount++;
        _chainActive = true;
        _retryPending = true;
    }

    /// <summary>
    /// The reminder link that a retry turn keeps: the link of the latest recorded turn, from <see cref="RecordRetry"/>
    /// until the session records the next turn. Null when no retry is pending. A child-delivery turn has no input,
    /// so its retry turn has no input source either. Without this link, the final failure of that retry turn would
    /// raise no alert.
    /// </summary>
    public ReminderId? RetriedTurnReminder => _retryPending ? _recordedTurnReminder : null;

    /// <summary>
    /// Exhaust the retry budget for the current turn (marks ineligible).
    /// </summary>
    public void Exhaust()
    {
        _eligibleTurnNumber = null;
        _chainActive = false;
    }

    /// <summary>
    /// Check whether the given delivery failure is retryable.
    /// </summary>
    public static bool IsRetryable(DeliveryFailed msg)
        => msg.FailureKind is DeliveryFailureKind.ContentRejected
            or DeliveryFailureKind.MessageTooLarge
            or DeliveryFailureKind.UnsupportedContent;

    /// <summary>
    /// Build a nudge message describing the delivery failure for LLM context.
    /// </summary>
    public static string BuildNudge(DeliveryFailed msg)
    {
        var guidance = msg.FailureKind switch
        {
            DeliveryFailureKind.ContentRejected => "Produce a simpler channel-safe response and avoid the content pattern the channel rejected.",
            DeliveryFailureKind.MessageTooLarge => "Produce a shorter response that fits the channel's length limits.",
            DeliveryFailureKind.UnsupportedContent => "Avoid unsupported formatting or content types for this channel.",
            DeliveryFailureKind.TransportFailure => "The channel experienced a transport error. Your response content was likely fine. Acknowledge to the user that delivery failed due to a technical issue and offer to retry.",
            DeliveryFailureKind.PermissionDenied => "The bot lacks permission to post in this channel. Inform the user that a permissions issue prevented delivery.",
            DeliveryFailureKind.Unknown or _ => "An unknown delivery error occurred. Acknowledge the issue to the user."
        };

        return $"Your last response could not be delivered to the user via {msg.ChannelType}. "
            + $"The user did not receive it. Delivery failure kind: {msg.FailureKind}. "
            + $"Channel error: {msg.ErrorMessage}\n{guidance}";
    }
}
