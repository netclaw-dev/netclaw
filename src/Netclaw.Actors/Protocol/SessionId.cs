using ProtoBuf;

namespace Netclaw.Actors.Protocol;

/// <summary>
/// Strongly-typed session identity. Wraps the entity key string used for
/// actor routing and persistence identity.
/// </summary>
[ProtoContract]
public readonly record struct SessionId(
    [property: ProtoMember(1)] string Value)
{
    public static explicit operator SessionId(string value) => new(value);

    /// <summary>
    /// Derives the memory domain from the session identity.
    /// Returns "project:default" for all transport-scoped sessions until
    /// a proper domain-scoping mechanism is designed. See GitHub #203.
    /// </summary>
    public string ToMemoryDomain() => "project:default";

    public override string ToString() => Value;
}
