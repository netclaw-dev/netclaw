// -----------------------------------------------------------------------
// <copyright file="SessionSnapshot.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Actors.Jobs;
using Netclaw.Actors.Reminders;
using Netclaw.Actors.SubAgents;
using Netclaw.Actors.Serialization;
using Netclaw.Actors.Sessions;

namespace Netclaw.Actors.Protocol;

/// <summary>
/// Snapshot of session state for fast recovery. Persisted after compaction
/// and periodically based on <see cref="Sessions.SessionConfig.SnapshotInterval"/>.
/// </summary>
public sealed record SessionSnapshot : INetclawSerializableMessage
{
    public sealed record AdoptedContextSnapshotRecord
    {
        public sealed record AdoptedContextSnapshotMessage
        {
            public string MessageId { get; init; } = string.Empty;

            public SenderId SenderId { get; init; } = new(string.Empty);

            public long TimestampMs { get; init; }

            public string AuthorityAtInclusion { get; init; } = string.Empty;
        }

        public string AuthorizedMessageId { get; init; } = string.Empty;

        public SenderId? AuthorizerSenderId { get; init; }

        public string? LowerBound { get; init; }

        public string? UpperBound { get; init; }

        public string Projection { get; init; } = string.Empty;

        public bool HasAdoptedContext { get; init; }

        public bool HasThirdPartyAdoptedContext { get; init; }

        public IReadOnlyList<string> AdoptedSpeakerIds { get; init; } = Array.Empty<string>();

        public bool ProjectionPersisted { get; init; }

        public IReadOnlyList<AdoptedContextSnapshotMessage> Messages { get; init; } =
            Array.Empty<AdoptedContextSnapshotMessage>();
    }

    public IReadOnlyList<SerializableChatMessage> History { get; init; } =
        Array.Empty<SerializableChatMessage>();

    public IReadOnlyList<BackgroundChildRun> ChildRuns { get; init; } = [];

    public int TurnCount { get; init; }

    public string? Title { get; init; }

    /// <summary>
    /// Persisted so a recovered session can handle late-arriving
    /// <see cref="DeliveryFailed"/> feedback after passivation.
    /// Null when no turn is eligible (initial state or retries exhausted).
    /// </summary>
    public TurnNumber? EligibleDeliveryTurnNumber { get; init; }

    /// <summary>
    /// The reminder fire key (<c>{reminderId}:{fireTimeMs}</c>) that started the eligible turn.
    /// A session that restores from a snapshot does not replay that turn's <c>TurnRecorded</c> event,
    /// so a failed post of a reminder reply would find no reminder without this value. The session writes
    /// it only while the eligible turn has no report, so a later incarnation cannot alert twice for one reply.
    /// Null for an old snapshot, for a turn that a person started, and for a reported turn.
    /// </summary>
    public ReminderId? EligibleDeliveryReminderId { get; init; }

    /// <summary>
    /// True when a sub-agent ran a routed skill for the eligible turn. The session never retries such a
    /// turn on the main model, and a restored session must agree with a live one. False for an old snapshot.
    /// </summary>
    public bool? EligibleDeliveryTurnRoutedSkill { get; init; }

    /// <summary>
    /// Durable working-context state (recent files). Null when the session
    /// has never set a non-empty context — <see cref="Sessions.SessionState.FromSnapshot"/>
    /// defaults to <see cref="WorkingContext.Empty"/> in that case.
    /// </summary>
    public WorkingContext? WorkingContext { get; init; }

    /// <summary>
    /// Background jobs this session is waiting on. Persisted because jobs
    /// are long-lived and must survive recovery.
    /// </summary>
    public IReadOnlyList<ActiveJobInfo> ActiveBackgroundJobs { get; init; } =
        Array.Empty<ActiveJobInfo>();

    /// <summary>Background job IDs that the session already accepted.</summary>
    public IReadOnlyList<BackgroundJobId> ProcessedBackgroundJobIds { get; init; } =
        [];

    public IReadOnlyList<AdoptedContextSnapshotRecord> AdoptedContextRecords { get; init; } =
        Array.Empty<AdoptedContextSnapshotRecord>();

    public IReadOnlyList<LegacyCoordinationEvidence> LegacyCoordinationEvidence { get; init; } =
        Array.Empty<LegacyCoordinationEvidence>();

    public IReadOnlyList<SessionProtocol.InputAdmitted> PendingInputs { get; init; } =
        Array.Empty<SessionProtocol.InputAdmitted>();

    public IReadOnlyList<string> RecentSourceMessageKeys { get; init; } =
        Array.Empty<string>();

    /// <summary>
    /// The recorded authority of the turn that a restart drain stopped after its last tool batch (#2632). The restart
    /// reminder resumes that turn. Null for an old snapshot, and an old binary ignores the field.
    /// </summary>
    public SessionProtocol.TurnContextRecord? RestartStoppedTurn { get; init; }
}
