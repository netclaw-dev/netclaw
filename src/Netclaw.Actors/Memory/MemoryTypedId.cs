// -----------------------------------------------------------------------
// <copyright file="MemoryTypedId.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
namespace Netclaw.Actors.Memory;

/// <summary>
/// Strongly-typed memory identity with kind prefix (doc: or rec:).
/// Centralizes ID parsing, formatting, and generation for the memory subsystem.
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

    public static string AnchorId(string canonicalName)
        => $"anchor:{canonicalName.Trim().ToLowerInvariant().Replace(' ', '-')}";

    public static string NewDocumentId() => $"doc-{Guid.NewGuid():N}";

    public static string NewRecordId() => $"rec-{Guid.NewGuid():N}";
}
