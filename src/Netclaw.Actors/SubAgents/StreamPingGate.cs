// -----------------------------------------------------------------------
// <copyright file="StreamPingGate.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Akka.Actor;
using Netclaw.Actors.Sessions.Pipelines;
using Netclaw.Configuration;

namespace Netclaw.Actors.SubAgents;

/// <summary>
/// Throttles the stream pings that a sub-agent LLM call sends to its actor, so the
/// actor mailbox does not get one message for each token. The gate reads time from
/// <see cref="ITimeProvider"/> (the actor system scheduler), not from the wall clock,
/// so a test that uses a virtual scheduler controls the throttle window.
/// <para>
/// The throttle must not hide the progress that the watchdog needs. A ping always
/// goes out for the first substantive update and for an answer update that follows a
/// ping of lower rank, because an answer update resets the thinking cap. A ping that
/// goes out later reports the strongest activity seen since the last ping.
/// </para>
/// </summary>
internal sealed class StreamPingGate
{
    private readonly ITimeProvider _clock;
    private readonly TimeSpan _interval;
    private DateTimeOffset _lastPing;
    private StreamActivity _lastSent = StreamActivity.Keepalive;
    private StreamActivity _pending = StreamActivity.Keepalive;

    public StreamPingGate(ITimeProvider clock, TimeSpan interval)
    {
        _clock = clock;
        _interval = interval;
        _lastPing = clock.Now;
    }

    /// <summary>
    /// Decide whether the update must reach the actor now. When it returns true,
    /// <paramref name="activity"/> is the activity that the ping carries.
    /// </summary>
    public bool TryPing(StreamUpdateClassification update, out StreamActivity activity)
    {
        if (update.Activity > _pending)
            _pending = update.Activity;

        var now = _clock.Now;
        var due = update.IsFirstSubstantive
                  || now - _lastPing >= _interval
                  || (update.Activity == StreamActivity.Answer && _lastSent != StreamActivity.Answer);
        if (!due)
        {
            activity = StreamActivity.Keepalive;
            return false;
        }

        activity = _pending;
        _lastPing = now;
        _lastSent = _pending;
        _pending = StreamActivity.Keepalive;
        return true;
    }
}
