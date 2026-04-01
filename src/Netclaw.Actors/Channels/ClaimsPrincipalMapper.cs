using System.Security.Claims;
using Netclaw.Configuration;

namespace Netclaw.Actors.Channels;

/// <summary>
/// Maps an authenticated <see cref="ClaimsPrincipal"/> to a
/// <see cref="ConnectionIdentity"/> by reading Netclaw-specific claim types.
/// Falls back to <c>UntrustedExternal</c> / <c>Unknown</c> for any missing claim.
/// </summary>
public sealed class ClaimsPrincipalMapper
{
    private static readonly ConnectionIdentity UnauthenticatedFallback = new(
        PrincipalClassification.UntrustedExternal,
        TransportAuthenticity.Unknown,
        "unknown");

    /// <summary>
    /// Converts a <see cref="ClaimsPrincipal"/> to a <see cref="ConnectionIdentity"/>.
    /// A null or unauthenticated principal returns the strict-default fallback.
    /// </summary>
    public ConnectionIdentity Map(ClaimsPrincipal? principal)
    {
        if (principal is null)
            return UnauthenticatedFallback;

        var principalValue = principal.FindFirst(NetclawClaimTypes.PrincipalClassification)?.Value;
        var transportValue = principal.FindFirst(NetclawClaimTypes.TransportAuthenticity)?.Value;
        var senderId = principal.FindFirst(NetclawClaimTypes.DeviceId)?.Value ?? "unknown";

        var principalClassification = Enum.TryParse<PrincipalClassification>(principalValue, out var p)
            ? p
            : PrincipalClassification.UntrustedExternal;

        var transportAuthenticity = Enum.TryParse<TransportAuthenticity>(transportValue, out var t)
            ? t
            : TransportAuthenticity.Unknown;

        return new ConnectionIdentity(principalClassification, transportAuthenticity, senderId);
    }
}
