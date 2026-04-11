using Netclaw.Actors.Sessions;
using ProtoBuf;

namespace Netclaw.Actors.Protocol;

/// <summary>
/// Snapshot of session state for fast recovery. Persisted after compaction
/// and periodically based on <see cref="Sessions.SessionConfig.SnapshotInterval"/>.
/// </summary>
[ProtoContract]
public sealed class SessionSnapshot
{
    [ProtoMember(1)]
    public List<SerializableChatMessage> History { get; set; } = new();

    [ProtoMember(2)]
    public int TurnCount { get; set; }

    [ProtoMember(3)]
    public string? Title { get; set; }

    /// <summary>
    /// Persisted so a recovered session can handle late-arriving
    /// <see cref="DeliveryFailed"/> feedback after passivation.
    /// Null when no turn is eligible (initial state or retries exhausted).
    /// </summary>
    [ProtoMember(4)]
    public int? EligibleDeliveryTurnNumber { get; set; }

    // ProtoMember(5) reserved — formerly CompactionBoundaryIndex, removed
    // before the compaction-rework change merged. Do not reuse this field
    // number; local dev snapshots written during the rework development
    // window may still contain an int at position 5, and re-binding it to
    // a different type would fail deserialization silently.

    /// <summary>
    /// Durable working-context state (recent files). Null when the session
    /// has never set a non-empty context — <see cref="Sessions.SessionState.FromSnapshot"/>
    /// defaults to <see cref="WorkingContext.Empty"/> in that case.
    /// </summary>
    [ProtoMember(6)]
    public WorkingContext? WorkingContext { get; set; }
}
