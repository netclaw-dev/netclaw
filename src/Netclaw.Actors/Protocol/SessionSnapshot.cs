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
}
