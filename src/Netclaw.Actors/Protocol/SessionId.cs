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
    /// Extracts the prefix before the first '/' (e.g. "slack" from "slack/C123/1234.5678")
    /// and wraps it as "project:{prefix}". Falls back to "project:default".
    /// </summary>
    public string ToMemoryDomain()
    {
        var slash = Value.IndexOf('/', StringComparison.Ordinal);
        return slash > 0
            ? $"project:{Value[..slash].ToLowerInvariant()}"
            : "project:default";
    }

    public override string ToString() => Value;
}
