// -----------------------------------------------------------------------
// <copyright file="MemoryTypedId.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
namespace Netclaw.Actors.Memory;

public readonly record struct MemoryStorageId(string Value)
{
    public bool IsEmpty => string.IsNullOrWhiteSpace(Value);

    public override string ToString() => Value;
}

/// <summary>
/// A memory identity: its <see cref="MemoryKind"/> plus its opaque storage id
/// (the primary key, e.g. "doc-{guid}" / "rec-{guid}"). The storage id IS the
/// model-facing handle — it is surfaced verbatim and passed back verbatim, so a
/// value always round-trips to the exact row it came from.
/// </summary>
public readonly record struct MemoryTypedId(MemoryKind Kind, MemoryStorageId Id)
{
    public MemoryTypedId(MemoryKind kind, string id)
        : this(kind, new MemoryStorageId(id))
    {
    }

    /// <summary>
    /// Resolves an id supplied by the model to its kind plus exact storage key. Storage ids
    /// are self-describing via their "doc-"/"rec-" prefix; a legacy "doc:"/"rec:" envelope is
    /// also accepted and stripped. The remaining string is used as the storage key verbatim —
    /// it is never rewritten — so there is exactly one key per input and no ambiguity.
    /// Returns <see cref="MemoryKind.Unknown"/> with the raw value when the prefix is unrecognized.
    /// </summary>
    /// <remarks>
    /// The kind prefix is matched case-insensitively (a tolerant envelope), but the storage key
    /// that follows is preserved verbatim and later matched case-sensitively against the canonical
    /// lowercase primary key. This is deliberate: generated ids are always lowercase, so a
    /// mis-cased key is treated as not-found (fail-loud) rather than silently coerced to a
    /// different row.
    /// </remarks>
    public static MemoryTypedId Parse(string raw)
    {
        raw = raw.Trim();
        if (raw.StartsWith("doc:", StringComparison.OrdinalIgnoreCase))
            return new MemoryTypedId(MemoryKind.Document, raw[4..]);
        if (raw.StartsWith("rec:", StringComparison.OrdinalIgnoreCase))
            return new MemoryTypedId(MemoryKind.Record, raw[4..]);
        if (raw.StartsWith("doc-", StringComparison.OrdinalIgnoreCase))
            return new MemoryTypedId(MemoryKind.Document, raw);
        if (raw.StartsWith("rec-", StringComparison.OrdinalIgnoreCase))
            return new MemoryTypedId(MemoryKind.Record, raw);
        return new MemoryTypedId(MemoryKind.Unknown, raw);
    }

    public override string ToString() => Id.Value;

    public static string AnchorId(string canonicalName)
        => $"anchor:{canonicalName.Trim().ToLowerInvariant().Replace(' ', '-')}";

    public static MemoryStorageId NewDocumentId() => new($"doc-{Guid.NewGuid():N}");

    public static MemoryStorageId NewRecordId() => new($"rec-{Guid.NewGuid():N}");
}
