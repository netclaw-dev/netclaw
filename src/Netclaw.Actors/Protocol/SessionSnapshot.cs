// -----------------------------------------------------------------------
// <copyright file="SessionSnapshot.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Actors.Jobs;
using Netclaw.Actors.Sessions;

namespace Netclaw.Actors.Protocol;

/// <summary>
/// Snapshot of session state for fast recovery. Persisted after compaction
/// and periodically based on <see cref="Sessions.SessionConfig.SnapshotInterval"/>.
/// </summary>
public sealed class SessionSnapshot
{
    public sealed class AdoptedContextSnapshotRecord
    {
        public sealed class AdoptedContextSnapshotMessage
        {
            public string MessageId { get; set; } = string.Empty;

            public string SenderId { get; set; } = string.Empty;

            public long TimestampMs { get; set; }

            public string AuthorityAtInclusion { get; set; } = string.Empty;
        }

        public string AuthorizedMessageId { get; set; } = string.Empty;

        public string? AuthorizerSenderId { get; set; }

        public string? LowerBound { get; set; }

        public string? UpperBound { get; set; }

        public string Projection { get; set; } = string.Empty;

        public bool ProjectionPersisted { get; set; }

        public List<AdoptedContextSnapshotMessage> Messages { get; set; } = [];
    }

    public List<SerializableChatMessage> History { get; set; } = [];

    public int TurnCount { get; set; }

    public string? Title { get; set; }

    /// <summary>
    /// Persisted so a recovered session can handle late-arriving
    /// <see cref="DeliveryFailed"/> feedback after passivation.
    /// Null when no turn is eligible (initial state or retries exhausted).
    /// </summary>
    public int? EligibleDeliveryTurnNumber { get; set; }

    /// <summary>
    /// Durable working-context state (recent files). Null when the session
    /// has never set a non-empty context — <see cref="Sessions.SessionState.FromSnapshot"/>
    /// defaults to <see cref="WorkingContext.Empty"/> in that case.
    /// </summary>
    public WorkingContext? WorkingContext { get; set; }

    /// <summary>
    /// Background jobs this session is waiting on. Persisted because jobs
    /// are long-lived and must survive recovery.
    /// </summary>
    public List<ActiveJobInfo> ActiveBackgroundJobs { get; set; } = [];

    public List<AdoptedContextSnapshotRecord> AdoptedContextRecords { get; set; } = [];
}
