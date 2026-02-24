namespace Netclaw.App.Gateway;

/// <summary>
/// Trust level for a connection to the Netclaw gateway.
/// Every connection (SignalR, in-process channel) carries a security context
/// that identifies the trust level. See SPEC-011 §Security Context.
/// </summary>
public enum SecurityTrust
{
    /// <summary>Local connection, full trust. Only level implemented in Phase 1.</summary>
    LocalOperator,

    /// <summary>Validated remote sender, ACL-gated. Future.</summary>
    Authenticated,

    /// <summary>Default deny. Future.</summary>
    Anonymous
}

/// <summary>
/// Security context attached to every gateway connection.
/// Phase 1: all connections are <see cref="SecurityTrust.LocalOperator"/>.
/// </summary>
public sealed record ChannelSecurityContext
{
    public required SecurityTrust Trust { get; init; }

    public string? SenderId { get; init; }

    /// <summary>
    /// Creates a local operator context (full trust, Phase 1 default).
    /// </summary>
    public static ChannelSecurityContext LocalOperator(string? senderId = null) =>
        new() { Trust = SecurityTrust.LocalOperator, SenderId = senderId };
}
