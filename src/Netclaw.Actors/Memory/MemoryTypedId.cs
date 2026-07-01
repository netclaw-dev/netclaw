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
/// Strongly-typed model-facing memory handle with kind prefix (doc: or rec:).
/// Storage IDs are opaque and may include legacy doc-/rec- prefixes.
/// </summary>
public readonly record struct MemoryTypedId(MemoryKind Kind, MemoryStorageId Id)
{
    public MemoryTypedId(MemoryKind kind, string id)
        : this(kind, new MemoryStorageId(id))
    {
    }

    /// <summary>
    /// Formats as the prefixed wire representation: "doc:{id}" or "rec:{id}".
    /// </summary>
    public string ToWireValue() => Kind switch
    {
        MemoryKind.Document => $"doc:{Id.Value}",
        MemoryKind.Record => $"rec:{Id.Value}",
        _ => Id.Value
    };

    /// <summary>
    /// Formats a storage ID for model-visible output. Existing storage IDs are
    /// not rewritten; the kind prefix is added as the tool handle envelope.
    /// </summary>
    public static string ToWireValue(MemoryKind kind, string storageId)
        => new MemoryTypedId(kind, storageId).ToWireValue();

    public static string ToWireValue(MemoryKind kind, MemoryStorageId storageId)
        => new MemoryTypedId(kind, storageId).ToWireValue();

    /// <summary>
    /// Parses a model-visible handle like "doc:abc123" or "rec:def456".
    /// Also accepts legacy raw storage IDs such as "doc-abc123" and "rec-def456".
    /// Returns <see cref="MemoryKind.Unknown"/> with the raw value when the prefix is unrecognized.
    /// </summary>
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

    public IReadOnlyList<MemoryStorageId> CandidateStorageIds() => Kind switch
    {
        MemoryKind.Document => CandidateStorageIdsFor("doc-"),
        MemoryKind.Record => CandidateStorageIdsFor("rec-"),
        _ => [Id]
    };

    private IReadOnlyList<MemoryStorageId> CandidateStorageIdsFor(string legacyPrefix)
    {
        if (Id.Value.StartsWith(legacyPrefix, StringComparison.OrdinalIgnoreCase))
            return [Id];

        return [Id, new MemoryStorageId(legacyPrefix + Id.Value)];
    }

    public override string ToString() => ToWireValue();

    public static string AnchorId(string canonicalName)
        => $"anchor:{canonicalName.Trim().ToLowerInvariant().Replace(' ', '-')}";

    public static MemoryStorageId NewDocumentId() => new($"doc-{Guid.NewGuid():N}");

    public static MemoryStorageId NewRecordId() => new($"rec-{Guid.NewGuid():N}");
}
