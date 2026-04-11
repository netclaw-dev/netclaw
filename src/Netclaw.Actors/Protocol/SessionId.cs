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

    public override string ToString() => Value;
}
