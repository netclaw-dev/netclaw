// -----------------------------------------------------------------------
// <copyright file="AudienceResult.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Configuration;

namespace Netclaw.Channels;

public readonly record struct AudienceResult(TrustAudience Audience, string? Error)
{
    public AudienceResult(TrustAudience audience) : this(audience, null) { }
    public AudienceResult(string error) : this(default, error) { }

    /// <summary>
    /// Shared audience resolution: explicit channel ID → "dm" key → heuristic fallback.
    /// </summary>
    public static AudienceResult Resolve(
        string channelId,
        bool isDirectMessage,
        IReadOnlyDictionary<string, string> channelAudiences,
        bool isExplicitUser,
        bool isExplicitChannel)
    {
        if (channelAudiences.TryGetValue(channelId, out var channelOverride))
        {
            return SecurityPolicyDefaults.TryParseAudience(channelOverride, out var channelAudience)
                ? new AudienceResult(channelAudience)
                : new AudienceResult($"{AclDenyReasons.InvalidChannelAudiencePrefix}:{channelId}={channelOverride}");
        }

        if (isDirectMessage
            && channelAudiences.TryGetValue("dm", out var dmOverride))
        {
            return SecurityPolicyDefaults.TryParseAudience(dmOverride, out var dmAudience)
                ? new AudienceResult(dmAudience)
                : new AudienceResult($"{AclDenyReasons.InvalidChannelAudiencePrefix}:dm={dmOverride}");
        }

        // Team requires an operator-vetted sender: an allowlisted user or an
        // explicitly allowlisted channel. A direct message alone does NOT grant
        // Team — an unvetted DM (allowed only because AllowedUserIds is empty)
        // resolves to Public. Operators who want all DMs treated as Team set an
        // explicit ChannelAudiences "dm" override, handled above.
        var audience = (isExplicitUser || isExplicitChannel)
            ? TrustAudience.Team
            : TrustAudience.Public;
        return new AudienceResult(audience);
    }
}
