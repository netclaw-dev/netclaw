// -----------------------------------------------------------------------
// <copyright file="IAclDecision.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Actors.Channels;
using Netclaw.Configuration;

namespace Netclaw.Channels;

public interface IAclDecision
{
    bool IsAllowed { get; }
    string? DenyReason { get; }
    TrustAudience Audience { get; }
    PrincipalClassification Principal { get; }
    SourceProvenance Provenance { get; }
}

public sealed record ChannelAclDecision(
    bool IsAllowed,
    string? DenyReason,
    TrustAudience Audience,
    PrincipalClassification Principal,
    SourceProvenance Provenance) : IAclDecision
{
    public static ChannelAclDecision Deny(string reason) => new(
        false,
        reason,
        TrustAudience.Public,
        PrincipalClassification.UntrustedExternal,
        SourceProvenance.StrictDefault());

    public static ChannelAclDecision Allow(
        TrustAudience audience,
        PrincipalClassification principal,
        SourceProvenance provenance) => new(
        true,
        null,
        audience,
        principal,
        provenance);
}

public static class AclDenyReasons
{
    public const string MissingUserId = "missing_user_id";
    public const string ChannelNotAllowed = "channel_not_allowed";
    public const string UserNotAllowed = "user_not_allowed";
    public const string DirectMessagesDisabled = "direct_messages_disabled";
    public const string InvalidChannelAudiencePrefix = "invalid_channel_audience";
}
