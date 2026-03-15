namespace Netclaw.Actors.Memory;

/// <summary>
/// Strongly-typed memory identity that includes a kind prefix (doc: or rec:).
/// Centralizes parse/format logic previously duplicated across tool and store files.
/// </summary>
public readonly record struct MemoryTypedId(MemoryKind Kind, string Id)
{
    /// <summary>
    /// Formats as the prefixed wire representation: "doc:{id}" or "rec:{id}".
    /// </summary>
    public string ToWireValue() => Kind switch
    {
        MemoryKind.Document => $"doc:{Id}",
        MemoryKind.Record => $"rec:{Id}",
        _ => Id
    };

    /// <summary>
    /// Parses a prefixed string like "doc:abc123" or "rec:def456" into a typed ID.
    /// Returns <see cref="MemoryKind.Unknown"/> with the raw value when the prefix is unrecognized.
    /// </summary>
    public static MemoryTypedId Parse(string raw)
    {
        if (raw.StartsWith("doc:", StringComparison.OrdinalIgnoreCase))
            return new MemoryTypedId(MemoryKind.Document, raw[4..]);
        if (raw.StartsWith("rec:", StringComparison.OrdinalIgnoreCase))
            return new MemoryTypedId(MemoryKind.Record, raw[4..]);
        return new MemoryTypedId(MemoryKind.Unknown, raw);
    }

    public override string ToString() => ToWireValue();
}
