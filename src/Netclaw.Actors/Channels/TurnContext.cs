// -----------------------------------------------------------------------
// <copyright file="TurnContext.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Actors.Protocol;
using Netclaw.Actors.Reminders;
using Netclaw.Configuration;
using Netclaw.Tools;
using static Netclaw.Actors.Sessions.SessionProtocol;

namespace Netclaw.Actors.Channels;

/// <summary>
/// Durable execution authority for a session turn. Transport delivery details
/// stay on <see cref="MessageSource"/>; this model carries only security and
/// provenance fields that must survive approval pause and recovery.
/// </summary>
public sealed record TurnContext
{
    public required SessionId SessionId { get; init; }

    public required TurnId TurnId { get; init; }

    public required TrustAudience Audience { get; init; }

    public required TrustBoundary Boundary { get; init; }

    public ChannelType? ChannelType { get; init; }

    public SenderId? RequesterSenderId { get; init; }

    public required PrincipalClassification RequesterPrincipal { get; init; }

    public required SourceProvenance Provenance { get; init; }

    public ChannelDeliveryTargetInfo? DefaultDeliveryTarget { get; init; }

    public ChannelDeliveryTargetInfo? RequestedDeliveryTarget { get; init; }

    public ChannelDeliveryTargetInfo? EffectiveDeliveryTarget
        => RequestedDeliveryTarget ?? DefaultDeliveryTarget;

    public bool HasAdoptedContext { get; init; }

    public bool HasThirdPartyAdoptedContext { get; init; }

    public IReadOnlyList<string> AdoptedSpeakerIds { get; init; } = [];

    public bool SupportsInteractiveApproval { get; init; }

    public bool HasApprovalRequester
        => RequesterPrincipal == PrincipalClassification.VerifiedAutomation
           || RequesterSenderId is not null;

    // Security boundary: this check decides who owns delegated work, such as child status and cancellation.
    // It compares only the identity of the requester. The turn id, provenance, delivery targets, adopted
    // context, and approval capability describe one message, and they change from turn to turn for the same
    // person. An owner check that compares them locks the owner out of its own work in a later turn.
    internal bool HasSameRequester(TurnContext right)
        => SessionId == right.SessionId
           && Audience == right.Audience
           && Boundary == right.Boundary
           && ChannelType == right.ChannelType
           && RequesterSenderId == right.RequesterSenderId
           && RequesterPrincipal == right.RequesterPrincipal;

    // Security boundary: this check decides who may read the status of a child run and who may cancel it (#2524).
    // The instance is the requesting turn. The argument is the turn that started the run.
    // If either turn is a reminder turn, the sender and the principal do not count, because a reminder fire
    // always has the sender "reminder-system" and the principal VerifiedAutomation. The session, the audience,
    // the boundary, and the channel type must still match, so a turn never reaches a run of another session.
    // The session is the unit of trust for a reminder. All other pairs of turns keep the six-fact rule.
    // HasSameRequester stays strict, because input batching also uses it.
    internal bool CanControlChildRunOf(TurnContext owner)
        => IsReminderTurn || owner.IsReminderTurn
            ? SessionId == owner.SessionId
              && Audience == owner.Audience
              && Boundary == owner.Boundary
              && ChannelType == owner.ChannelType
            : owner.HasSameRequester(this);

    // Three facts make a reminder turn. A webhook turn also has VerifiedAutomation, and its sender sets the source
    // kind through the event header or the route name, so the source kind can read "reminder". The webhook path
    // sets the authenticity Verified in code. The reminder path sets LocalProcess in code, so a webhook sender
    // cannot copy it. A person turn cannot set the principal.
    private bool IsReminderTurn
        => RequesterPrincipal == PrincipalClassification.VerifiedAutomation
           && Provenance.TransportAuthenticity == TransportAuthenticity.LocalProcess
           && Provenance.SourceKind?.Value == ReminderExecutionActor.ReminderSourceKind;

    // Turn identity is separate: restart batches can span several turns. Use this check where one authority
    // must continue unchanged (start idempotency, approval and continuation recovery), not for ownership.
    internal bool HasSameAuthority(TurnContext right)
        => HasSameRequester(right)
           && Provenance == right.Provenance
           && DefaultDeliveryTarget == right.DefaultDeliveryTarget
           && RequestedDeliveryTarget == right.RequestedDeliveryTarget
           && HasAdoptedContext == right.HasAdoptedContext
           && HasThirdPartyAdoptedContext == right.HasThirdPartyAdoptedContext
           && AdoptedSpeakerIds.SequenceEqual(right.AdoptedSpeakerIds, StringComparer.Ordinal)
           && SupportsInteractiveApproval == right.SupportsInteractiveApproval;

    public static TurnContext FromMessageSource(SessionId sessionId, TurnId turnId, MessageSource? source)
    {
        var audience = source?.Audience ?? SecurityPolicyDefaults.ResolveAudienceFromSessionId(sessionId.Value);
        var boundary = source?.Boundary ?? SecurityPolicyDefaults.ResolveBoundaryFromSessionId(sessionId.Value, audience);
        var provenance = source?.Provenance
            ?? new SourceProvenance(TransportAuthenticity.Unverified, PayloadTaint.Public);

        return new TurnContext
        {
            SessionId = sessionId,
            TurnId = turnId,
            Audience = audience,
            Boundary = boundary,
            ChannelType = source?.ChannelType,
            RequesterSenderId = source?.SenderId,
            RequesterPrincipal = source?.Principal ?? PrincipalClassification.UntrustedExternal,
            Provenance = provenance,
            DefaultDeliveryTarget = source?.DefaultDeliveryTarget,
            RequestedDeliveryTarget = source?.RequestedDeliveryTarget,
            HasAdoptedContext = source?.HasAdoptedContext ?? false,
            HasThirdPartyAdoptedContext = source?.HasThirdPartyAdoptedContext ?? false,
            AdoptedSpeakerIds = source?.AdoptedSpeakerIds ?? [],
            SupportsInteractiveApproval = source?.ChannelType.SupportsInteractiveApproval() ?? false
        };
    }

    public TurnContextRecord ToRecord()
    {
        var sourceScope = Provenance.SourceScope;
        var sourceKind = Provenance.SourceKind;

        return new TurnContextRecord
        {
            SessionId = SessionId,
            TurnId = TurnId.Value,
            Audience = Audience,
            Boundary = Boundary,
            ChannelType = ChannelType?.ToWireValue(),
            RequesterSenderId = RequesterSenderId,
            RequesterPrincipal = RequesterPrincipal,
            TransportAuthenticity = Provenance.TransportAuthenticity,
            PayloadTaint = Provenance.PayloadTaint,
            SourceScope = sourceScope is null ? null : sourceScope.Value.Value,
            SourceKind = sourceKind is null ? null : sourceKind.Value.Value,
            DefaultDeliveryTarget = DefaultDeliveryTarget,
            RequestedDeliveryTarget = RequestedDeliveryTarget,
            HasAdoptedContext = HasAdoptedContext,
            HasThirdPartyAdoptedContext = HasThirdPartyAdoptedContext,
            AdoptedSpeakerIds = [.. AdoptedSpeakerIds],
            SupportsInteractiveApproval = SupportsInteractiveApproval
        };
    }

    public static bool TryFromRecord(TurnContextRecord? record, out TurnContext? context, out string? reason)
    {
        context = null;
        reason = null;

        if (record is null)
        {
            reason = "missing turn context record";
            return false;
        }

        if (string.IsNullOrWhiteSpace(record.SessionId.Value))
        {
            reason = "missing session id";
            return false;
        }

        if (string.IsNullOrWhiteSpace(record.TurnId))
        {
            reason = "missing turn id";
            return false;
        }

        // Proto3 cannot tell a Public audience from an absent field, because
        // Public is enum value 0. It can carry an unknown number, for example
        // from a newer writer or a damaged record. Refuse that record. Do not
        // cast it into an audience that later code would compare as a rank.
        if (!Enum.IsDefined(record.Audience))
        {
            reason = $"invalid trust audience '{(int)record.Audience}'";
            return false;
        }

        if (record.Boundary is not { } boundary)
        {
            reason = "missing trust boundary";
            return false;
        }

        if (record.RequesterPrincipal is not { } requesterPrincipal)
        {
            reason = "missing requester principal";
            return false;
        }

        ChannelType? channelType = null;
        if (!string.IsNullOrWhiteSpace(record.ChannelType))
        {
            if (!ChannelTypeExtensions.TryFromWireValue(record.ChannelType, out var parsed))
            {
                reason = $"invalid channel type '{record.ChannelType}'";
                return false;
            }

            channelType = parsed;
        }

        context = new TurnContext
        {
            SessionId = record.SessionId,
            TurnId = new TurnId(record.TurnId),
            Audience = record.Audience,
            Boundary = boundary,
            ChannelType = channelType,
            RequesterSenderId = record.RequesterSenderId,
            RequesterPrincipal = requesterPrincipal,
            Provenance = new SourceProvenance(record.TransportAuthenticity, record.PayloadTaint)
            {
                SourceScope = string.IsNullOrWhiteSpace(record.SourceScope) ? null : new SourceScope(record.SourceScope),
                SourceKind = string.IsNullOrWhiteSpace(record.SourceKind) ? null : new SourceKind(record.SourceKind)
            },
            DefaultDeliveryTarget = record.DefaultDeliveryTarget,
            RequestedDeliveryTarget = record.RequestedDeliveryTarget,
            HasAdoptedContext = record.HasAdoptedContext,
            HasThirdPartyAdoptedContext = record.HasThirdPartyAdoptedContext,
            AdoptedSpeakerIds = record.AdoptedSpeakerIds,
            SupportsInteractiveApproval = record.SupportsInteractiveApproval
        };
        return true;
    }
}
