using Netclaw.Actors.Protocol;

namespace Netclaw.Actors.Channels;

/// <summary>
/// Observer for session lifecycle events. Implemented by infrastructure services
/// (e.g. session catalog) that need to track all sessions regardless of channel type.
/// Injected into <see cref="SessionPipeline"/> via DI — every materialized session
/// automatically reports creation and output events.
/// </summary>
public interface ISessionLifecycleObserver
{
    /// <summary>
    /// Called when a new session is created (or re-materialized).
    /// </summary>
    void OnSessionCreated(SessionId sessionId, string channelType);

    /// <summary>
    /// Called for every <see cref="SessionOutput"/> emitted by the session.
    /// Implementations must be fast — this runs synchronously in the Akka.Streams pipeline.
    /// </summary>
    void OnOutput(SessionOutput output);
}
