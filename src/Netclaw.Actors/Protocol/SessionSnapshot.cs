using Netclaw.Actors.Jobs;
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
    [ProtoContract]
    public sealed class AdoptedContextSnapshotRecord
    {
        [ProtoContract]
        public sealed class AdoptedContextSnapshotMessage
        {
            [ProtoMember(1)]
            public string MessageId { get; set; } = string.Empty;

            [ProtoMember(2)]
            public string SenderId { get; set; } = string.Empty;

            [ProtoMember(3)]
            public long TimestampMs { get; set; }

            [ProtoMember(4)]
            public string AuthorityAtInclusion { get; set; } = string.Empty;
        }

        [ProtoMember(1)]
        public string AuthorizedMessageId { get; set; } = string.Empty;

        [ProtoMember(2)]
        public string? AuthorizerSenderId { get; set; }

        [ProtoMember(3)]
        public string? LowerBound { get; set; }

        [ProtoMember(4)]
        public string? UpperBound { get; set; }

        [ProtoMember(5)]
        public string Projection { get; set; } = string.Empty;

        [ProtoMember(6)]
        public bool ProjectionPersisted { get; set; }

        [ProtoMember(7)]
        public List<AdoptedContextSnapshotMessage> Messages { get; set; } = new();
    }

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

    /// <summary>
    /// Background jobs this session is waiting on. Persisted because jobs
    /// are long-lived and must survive recovery.
    /// </summary>
    [ProtoMember(7)]
    public List<ActiveJobInfo> ActiveBackgroundJobs { get; set; } = new();

    [ProtoMember(8)]
    public List<AdoptedContextSnapshotRecord> AdoptedContextRecords { get; set; } = new();
}
