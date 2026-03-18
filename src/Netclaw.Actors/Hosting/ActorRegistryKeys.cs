namespace Netclaw.Actors.Hosting;

/// <summary>
/// Marker type for ActorRegistry lookup of the session manager
/// (GenericChildPerEntityParent routing to LlmSessionActors).
/// </summary>
public sealed class SessionManagerActorKey;

/// <summary>
/// Marker type for ActorRegistry lookup of the model capability cache actor.
/// </summary>
public sealed class ModelCapabilityActorKey;

/// <summary>
/// Marker type for ActorRegistry lookup of the reminder manager actor.
/// </summary>
public sealed class ReminderManagerActorKey;

/// <summary>
/// Marker type for ActorRegistry lookup of the daily stats buffering actor.
/// </summary>
public sealed class DailyStatsActorKey;
