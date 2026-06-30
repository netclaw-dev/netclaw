// -----------------------------------------------------------------------
// <copyright file="IReminderChannelNotifier.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Tools;

namespace Netclaw.Actors.Reminders;

/// <summary>
/// Posts an operator-facing notice to a reminder's destination channel when an
/// execution fails. Implemented in the daemon over the channel outbound
/// registry so the actor layer stays transport-agnostic (the manager hands over
/// an already-resolved <see cref="ChannelDeliveryTargetInfo"/> and plain text).
/// Fire-and-forget (<c>void</c>) — the manager must never block on channel
/// delivery, mirroring <see cref="Configuration.IOperationalNotificationSink"/>.
/// </summary>
public interface IReminderChannelNotifier
{
    /// <summary>
    /// Posts <paramref name="text"/> to <paramref name="target"/>. Must be
    /// thread-safe and must not throw — delivery failures are the
    /// implementation's problem to log, never the caller's to handle.
    /// </summary>
    void NotifyFailure(ChannelDeliveryTargetInfo target, string text);
}

/// <summary>
/// No-op notifier for environments with no channel outbound path (e.g. tests, or
/// a daemon with no channels configured). A real Null Object — explicit, not a
/// silent fallback: callers still get a non-null required dependency.
/// </summary>
public sealed class NullReminderChannelNotifier : IReminderChannelNotifier
{
    public static readonly NullReminderChannelNotifier Instance = new();

    private NullReminderChannelNotifier()
    {
    }

    public void NotifyFailure(ChannelDeliveryTargetInfo target, string text)
    {
        // Intentionally does nothing.
    }
}
