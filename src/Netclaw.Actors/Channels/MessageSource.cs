// -----------------------------------------------------------------------
// <copyright file="MessageSource.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Akka.Actor;
using Netclaw.Configuration;

namespace Netclaw.Actors.Channels;

/// <summary>
/// Ephemeral metadata describing where a user message originated.
/// Used for ACL checks and audit logging — NOT persisted with the session.
/// <see cref="Protocol.SendUserMessage.Source"/> is excluded from the proto
/// wire format so runtime-only fields such as
/// <see cref="AckTarget"/> and <see cref="ReminderId"/> are safe to add.
/// </summary>
public sealed record MessageSource
{
    public sealed record AdoptedContextEntry(
        string MessageId,
        Protocol.SenderId SenderId,
        DateTimeOffset Timestamp,
        string AuthorityAtInclusion);

    /// <summary>
    /// Channel type identifier.
    /// </summary>
    public required ChannelType ChannelType { get; init; }

    /// <summary>
    /// Identity of the sender within the channel (e.g. Slack user ID, "local-user").
    /// </summary>
    public required Protocol.SenderId SenderId { get; init; }

    /// <summary>
    /// Optional channel-specific identifier (e.g. Slack channel ID).
    /// </summary>
    public string? ChannelId { get; init; }

    /// <summary>
    /// Optional source message identifier from the inbound transport.
    /// Useful for routing diagnostics and dedup correlation.
    /// </summary>
    public string? MessageId { get; init; }

    /// <summary>
    /// Correlation identifier for this turn. Propagated across session logs
    /// and actor boundaries for end-to-end traceability.
    /// </summary>
    public Protocol.TurnId? TurnId { get; init; }

    /// <summary>
    /// Effective source audience attached to the inbound message before any runtime
    /// trust-context derivation occurs.
    /// </summary>
    public required TrustAudience Audience { get; init; }

    /// <summary>
    /// Runtime-owned security boundary used to partition durable memory and other
    /// reusable state across trust domains.
    /// </summary>
    public required TrustBoundary Boundary { get; init; }

    /// <summary>
    /// Principal classification hint for the inbound sender.
    /// </summary>
    public required PrincipalClassification Principal { get; init; }

    /// <summary>
    /// Provenance markers used to separate transport authenticity from payload taint.
    /// </summary>
    public required SourceProvenance Provenance { get; init; }

    /// <summary>
    /// When the message was received by the channel.
    /// </summary>
    public DateTimeOffset ReceivedAt { get; init; }

    /// <summary>
    /// Executable text for the current turn. When threaded adapters prepend adopted
    /// quoted context, this remains the current authorized message text.
    /// </summary>
    public string? ExecutableText { get; init; }

    /// <summary>
    /// True when the model input contains a quoted adopted-context window.
    /// </summary>
    public bool HasAdoptedContext
        => (AdoptedSpeakerIds?.Count ?? 0) > 0
           || (AdoptedContextEntries?.Count ?? 0) > 0
           || !string.IsNullOrWhiteSpace(AdoptedContextProjection);

    /// <summary>
    /// True when the adopted-context window includes at least one sender other than
    /// the current authorized sender.
    /// </summary>
    public bool HasThirdPartyAdoptedContext { get; init; }

    /// <summary>
    /// Stable sender ids present in the adopted-context window for this turn.
    /// </summary>
    public IReadOnlyList<string> AdoptedSpeakerIds { get; init; } = [];

    /// <summary>
    /// Canonical adopted-context projection shown to the model for this turn.
    /// </summary>
    public string? AdoptedContextProjection { get; init; }

    /// <summary>
    /// Exclusive lower bound for the adopted-context window. Null when there is no prior watermark.
    /// </summary>
    public string? AdoptedContextLowerBound { get; init; }

    /// <summary>
    /// Exclusive upper bound for the adopted-context window, typically the current authorized message id.
    /// </summary>
    public string? AdoptedContextUpperBound { get; init; }

    /// <summary>
    /// Included adopted messages captured at inclusion time.
    /// </summary>
    public IReadOnlyList<AdoptedContextEntry> AdoptedContextEntries { get; init; } = [];

    /// <summary>
    /// Ephemeral dedup and forensic key for reminder-originated deliveries.
    /// Format is <c>"{reminderId}:{fireTimestampMs}"</c>. Null for regular
    /// user messages. The target session pre-checks this value against its
    /// in-memory <see cref="Sessions.SessionState.ProcessedReminderIds"/>
    /// ledger to catch Akka.Reminders redeliveries. Persisted through to
    /// <see cref="Protocol.TurnRecorded.SourceReminderId"/> when the turn
    /// is recorded. This field is runtime-only — <see cref="MessageSource"/>
    /// is never serialized.
    /// </summary>
    public string? ReminderId { get; init; }

    /// <summary>
    /// Ephemeral dedup key for background-job-originated deliveries.
    /// Format is <c>"bg-job:{jobId}"</c>. Null for regular user messages.
    /// </summary>
    public string? BackgroundJobId { get; init; }

    /// <summary>
    /// Optional reply target for ack-gated trusted deliveries. When set,
    /// <see cref="ChannelPipeline"/>'s stream sink uses this ref as the
    /// <c>sender</c> argument on its <c>Tell</c> to the session manager, so
    /// that the session's existing <c>TryReplyAck</c> routes
    /// <see cref="Protocol.CommandAck"/>/<see cref="Protocol.CommandNack"/>
    /// back to the dispatcher's <c>Ask</c> temp actor. Regular inbound
    /// messages leave this null, preserving fire-and-forget semantics.
    /// This field is runtime-only — an <see cref="IActorRef"/> is not
    /// serializable and <see cref="MessageSource"/> is never persisted.
    /// </summary>
    public IActorRef? AckTarget { get; init; }
}
