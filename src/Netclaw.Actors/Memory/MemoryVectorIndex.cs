// -----------------------------------------------------------------------
// <copyright file="MemoryVectorIndex.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Numerics.Tensors;

namespace Netclaw.Actors.Memory;

/// <summary>
/// A single nearest-neighbor match returned by <see cref="MemoryVectorIndex.TopK"/>.
/// </summary>
public sealed record MemoryVectorMatch(string ItemId, string ItemKind, double Cosine);

/// <summary>
/// In-memory brute-force kNN index over one embedding model's vectors (memory-core-redesign
/// D3). Brute force is deliberate, not a placeholder: at the audited corpus scale (~1,200
/// documents, ~1.8 MB of float32 vectors) a full scan is sub-millisecond, and an ANN index
/// would add a dependency (native or otherwise) for zero measured benefit — revisit only if
/// the corpus grows past roughly 50k items.
///
/// <para>
/// The index snapshots <see cref="SQLiteMemoryStore.GetEmbeddingsForModelAsync"/> into a flat
/// <c>float[]</c> (row-major, one <see cref="Dimensions"/>-wide slice per item) plus parallel
/// id/kind arrays, bundled into an immutable <see cref="Snapshot"/> so a reader never observes
/// a torn combination of old ids with new vectors. Reloading is keyed on
/// <see cref="SQLiteMemoryStore.EmbeddingDataVersion"/> — a process-local monotonic counter
/// bumped by every embedding upsert/delete — so <see cref="ReloadIfStaleAsync"/> is a cheap
/// no-op on every call except the ones that raced a real data change. Cross-process
/// invalidation (multiple daemons against one SQLite file) is out of scope for the
/// single-process MVP; if that ever changes, the version counter would need to move to a
/// persisted <c>data_version</c> column instead of an in-process field.
/// </para>
/// </summary>
public sealed class MemoryVectorIndex
{
    private readonly SQLiteMemoryStore _store;
    private readonly object _reloadGate = new();
    private Snapshot _snapshot = Snapshot.Empty;

    public MemoryVectorIndex(SQLiteMemoryStore store, string modelId, int dimensions)
    {
        ArgumentNullException.ThrowIfNull(store);
        if (string.IsNullOrWhiteSpace(modelId))
            throw new ArgumentException("Model id is required.", nameof(modelId));
        if (dimensions <= 0)
            throw new ArgumentOutOfRangeException(nameof(dimensions), dimensions, "Dimensions must be positive.");

        _store = store;
        ModelId = modelId;
        Dimensions = dimensions;
    }

    /// <summary>The embedding model this index serves vectors for.</summary>
    public string ModelId { get; }

    /// <summary>Vector width for <see cref="ModelId"/>; every loaded row must match this.</summary>
    public int Dimensions { get; }

    /// <summary>Number of vectors currently loaded into the index.</summary>
    public int Count => Volatile.Read(ref _snapshot).Ids.Length;

    /// <summary>
    /// Reloads from the store when <see cref="SQLiteMemoryStore.EmbeddingDataVersion"/> has
    /// advanced past the version this index last loaded. Returns true when a reload was
    /// attempted (the store had newer data at the time this call started) — not necessarily
    /// that this call's snapshot is the one that ended up installed, since a concurrent faster
    /// reload for an even newer version is allowed to win instead (see <see cref="Snapshot"/>
    /// install below). Safe to call from multiple callers concurrently.
    /// </summary>
    public async Task<bool> ReloadIfStaleAsync(CancellationToken ct)
    {
        var currentVersion = _store.EmbeddingDataVersion;
        if (Volatile.Read(ref _snapshot).Version == currentVersion)
            return false;

        var rows = await _store.GetEmbeddingsForModelAsync(ModelId, ct).ConfigureAwait(false);
        var vectors = new float[rows.Count * Dimensions];
        var ids = new string[rows.Count];
        var itemKinds = new string[rows.Count];
        for (var i = 0; i < rows.Count; i++)
        {
            if (rows[i].Vector.Length != Dimensions)
                throw new InvalidOperationException(
                    $"Embedding row for item '{rows[i].ItemId}' has {rows[i].Vector.Length} dimensions; " +
                    $"index '{ModelId}' expects {Dimensions}. Mixed-model rows must not share a model id.");

            ids[i] = rows[i].ItemId;
            itemKinds[i] = rows[i].ItemKind;
            rows[i].Vector.Span.CopyTo(vectors.AsSpan(i * Dimensions, Dimensions));
        }

        var candidate = new Snapshot(currentVersion, vectors, ids, itemKinds);

        lock (_reloadGate)
        {
            // Only install if nothing fresher has already landed — a slower reload racing a
            // faster one must not clobber newer data with stale data.
            if (candidate.Version > Volatile.Read(ref _snapshot).Version)
                Volatile.Write(ref _snapshot, candidate);
        }

        return true;
    }

    /// <summary>
    /// Returns up to <paramref name="k"/> items whose cosine similarity to
    /// <paramref name="query"/> is at least <paramref name="minCosine"/>, ordered by
    /// descending similarity. Operates on the last snapshot installed by
    /// <see cref="ReloadIfStaleAsync"/> — callers that need current data must reload first.
    /// </summary>
    public IReadOnlyList<MemoryVectorMatch> TopK(ReadOnlySpan<float> query, int k, double minCosine)
        => TopK(query, k, minCosine, out _);

    /// <summary>
    /// Overload of <see cref="TopK(ReadOnlySpan{float}, int, double)"/> that additionally
    /// reports, via <paramref name="embeddedItemIds"/>, every item id that has ANY embedding row
    /// in this model's index — regardless of whether its cosine cleared
    /// <paramref name="minCosine"/> — computed from the IDENTICAL snapshot the returned matches
    /// were scored against (memory-core-redesign Slice 4 gap-repair fix, design D6). Callers that
    /// need to tell "embedded but below the absolute floor" apart from "never embedded" (a
    /// coverage gap the floor cannot apply to) must use this overload rather than a second,
    /// independent call: two separate snapshot reads could straddle a concurrent
    /// <see cref="ReloadIfStaleAsync"/> and observe a torn combination — matches from one
    /// snapshot, membership from another.
    /// </summary>
    public IReadOnlyList<MemoryVectorMatch> TopK(ReadOnlySpan<float> query, int k, double minCosine, out IReadOnlySet<string> embeddedItemIds)
    {
        var snapshot = Volatile.Read(ref _snapshot);
        embeddedItemIds = new HashSet<string>(snapshot.Ids, StringComparer.Ordinal);

        if (k <= 0)
            return [];
        if (query.Length != Dimensions)
            throw new ArgumentException($"Query vector has {query.Length} dimensions; index '{ModelId}' expects {Dimensions}.", nameof(query));
        if (snapshot.Ids.Length == 0)
            return [];

        // Full scan + sort: at corpus scale (D3: brute force is sub-ms up to ~50k items) this
        // is simpler and fast enough. A partial-selection heap is an optimization to reach for
        // only if profiling ever shows this method as hot.
        var matches = new List<MemoryVectorMatch>();
        for (var i = 0; i < snapshot.Ids.Length; i++)
        {
            var candidate = snapshot.Vectors.AsSpan(i * Dimensions, Dimensions);
            var cosine = TensorPrimitives.CosineSimilarity(query, candidate);
            if (cosine >= minCosine)
                matches.Add(new MemoryVectorMatch(snapshot.Ids[i], snapshot.ItemKinds[i], cosine));
        }

        matches.Sort((a, b) => b.Cosine.CompareTo(a.Cosine));
        return matches.Count <= k ? matches : matches.GetRange(0, k);
    }

    private sealed record Snapshot(long Version, float[] Vectors, string[] Ids, string[] ItemKinds)
    {
        public static readonly Snapshot Empty = new(-1, [], [], []);
    }
}
