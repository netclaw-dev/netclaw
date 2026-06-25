// -----------------------------------------------------------------------
// <copyright file="TurnContext.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Actors.Protocol;
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
