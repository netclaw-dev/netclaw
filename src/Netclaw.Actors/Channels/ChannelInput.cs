// -----------------------------------------------------------------------
// <copyright file="ChannelInput.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Akka.Actor;
using Microsoft.Extensions.AI;
using Netclaw.Configuration;

namespace Netclaw.Actors.Channels;

/// <summary>
/// Strongly-typed inbound message for the session stream API.
/// Supports multi-modal content via <see cref="AIContent"/> from
/// Microsoft.Extensions.AI.Abstractions.
/// </summary>
public sealed record ChannelInput
{
    public sealed record AdoptedContextEntry
    {
        public required string MessageId { get; init; }
        public required string SenderId { get; init; }
        public required DateTimeOffset Timestamp { get; init; }
        public required string AuthorityAtInclusion { get; init; }
    }

    /// <summary>
    /// Identity of the user who sent this message.
    /// </summary>
    public required string SenderId { get; init; }

    /// <summary>
    /// Optional channel-specific identifier (e.g. Slack channel ID).
    /// </summary>
    public string? ChannelId { get; init; }

    /// <summary>
    /// Optional message ID for correlation and deduplication.
    /// </summary>
    public string? MessageId { get; init; }

    /// <summary>
    /// Optional source audience hint carried from the inbound adapter.
    /// When omitted, the channel pipeline applies strict defaults.
    /// </summary>
    public TrustAudience? Audience { get; init; }

    /// <summary>
    /// Optional trust boundary hint carried from the inbound adapter.
    /// When omitted, the channel pipeline applies adapter defaults.
    /// </summary>
    public string? Boundary { get; init; }

    /// <summary>
    /// Optional principal classification for the sender.
    /// When omitted, the channel pipeline applies strict defaults.
    /// </summary>
    public PrincipalClassification? Principal { get; init; }

    /// <summary>
    /// Provenance markers that distinguish transport verification from content taint.
    /// </summary>
    public SourceProvenance? Provenance { get; init; }

    /// <summary>
    /// Message content. Supports text (<see cref="TextContent"/>),
    /// images, and other modalities via the <see cref="AIContent"/> hierarchy.
    /// </summary>
    public required IReadOnlyList<AIContent> Contents { get; init; }

    /// <summary>
    /// When the message was received by the channel.
    /// </summary>
    public DateTimeOffset ReceivedAt { get; init; }

    /// <summary>
    /// Executable text for this turn. When omitted, the full text content is executable.
    /// Threaded adapters use this to keep adopted context quoted while restricting
    /// control paths to the current message.
    /// </summary>
    public string? ExecutableText { get; init; }

    /// <summary>
    /// True when the turn contains quoted adopted thread context ahead of the current
    /// executable message.
    /// </summary>
    public bool HasAdoptedContext { get; init; }

    /// <summary>
    /// Stable sender ids that appeared in the adopted context window.
    /// </summary>
    public IReadOnlyList<string> AdoptedSpeakerIds { get; init; } = [];

    /// <summary>
    /// Canonical adopted-context projection shown to the model when quoted context is present.
    /// </summary>
    public string? AdoptedContextProjection { get; init; }

    /// <summary>
    /// Exclusive lower bound for the adopted-context window. Null when there is no prior watermark.
    /// </summary>
    public string? AdoptedContextLowerBound { get; init; }

    /// <summary>
    /// Exclusive upper bound for the adopted-context window, usually the current authorized message id.
    /// </summary>
    public string? AdoptedContextUpperBound { get; init; }

    /// <summary>
    /// Included adopted messages captured at inclusion time for audit persistence.
    /// </summary>
    public IReadOnlyList<AdoptedContextEntry> AdoptedContextEntries { get; init; } = [];

    /// <summary>
    /// Ephemeral reminder dedup and forensic key. Set by channel leaf actors
    /// when handling a <c>DeliverTrustedSessionTurn</c> (Mode B reminder
    /// re-entry). Propagated through to
    /// <see cref="MessageSource.ReminderId"/> by
    /// <see cref="MessageSourceFactory"/>. Null for regular inbound ingress.
    /// </summary>
    public string? ReminderId { get; init; }

    /// <summary>
    /// Ephemeral ack reply target. Set by channel leaf actors when handling
    /// a <c>DeliverTrustedSessionTurn</c> to the <c>Sender</c> preserved via
    /// the gateway's <c>Forward</c> chain. The pipeline's stream sink uses
    /// this ref as the <c>sender</c> argument on its <c>Tell</c> to the
    /// session manager. Null for regular inbound ingress, which preserves
    /// fire-and-forget semantics via <see cref="ActorRefs.NoSender"/>.
    /// </summary>
    public IActorRef? AckTarget { get; init; }
}
