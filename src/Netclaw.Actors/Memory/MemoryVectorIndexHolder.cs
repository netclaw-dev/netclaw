// -----------------------------------------------------------------------
// <copyright file="MemoryVectorIndexHolder.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
namespace Netclaw.Actors.Memory;

/// <summary>
/// Process-singleton owner of the <see cref="MemoryVectorIndex"/> the embedding kNN nominator
/// queries (memory-core-redesign Slice 3 Stage B, task 3.1). Mirrors
/// <see cref="MemoryEmbedderHolder"/>'s reason for existing as a mutable holder rather than a
/// plain DI singleton: <see cref="MemoryVectorIndex"/> requires a known, positive
/// <see cref="MemoryVectorIndex.Dimensions"/> at construction, but the real embedder (and its
/// dimensions) is only known once <c>EmbeddingWarmupHostedService</c> finishes provisioning —
/// which runs after every other singleton has already resolved its constructor dependencies.
/// This holder defers index construction to first use, and rebuilds it if the active embedder's
/// model id ever changes (an operator flipping <c>Memory.Embeddings.ModelId</c> and restarting).
///
/// <para>
/// <b>Callers must call <see cref="GetCurrentAsync"/> at the time they actually need to query</b>
/// — never cache the returned index across calls — so a model change or the transition from
/// unavailable to available surfaces without a process restart, exactly like
/// <see cref="MemoryEmbedderHolder.Current"/>.
/// </para>
/// </summary>
public sealed class MemoryVectorIndexHolder
{
    private readonly SQLiteMemoryStore _store;
    private readonly object _gate = new();
    private MemoryVectorIndex? _index;

    public MemoryVectorIndexHolder(SQLiteMemoryStore store)
    {
        ArgumentNullException.ThrowIfNull(store);
        _store = store;
    }

    /// <summary>
    /// Returns the vector index for <paramref name="embedder"/>'s current model, reloaded to the
    /// store's latest committed embeddings (memory-core-redesign Slice 3: cheap when nothing
    /// changed — <see cref="MemoryVectorIndex.ReloadIfStaleAsync"/> only does real work when
    /// <see cref="SQLiteMemoryStore.EmbeddingDataVersion"/> has advanced). Returns null when
    /// <paramref name="embedder"/> cannot currently produce vectors — there is nothing to index,
    /// and callers should treat this identically to "no vector evidence available" (the
    /// degraded/lexical path).
    /// </summary>
    public async Task<MemoryVectorIndex?> GetCurrentAsync(IMemoryEmbedder embedder, CancellationToken ct)
    {
        if (!embedder.IsAvailable)
            return null;

        var index = Volatile.Read(ref _index);
        if (index is null || !string.Equals(index.ModelId, embedder.ModelId, StringComparison.Ordinal))
        {
            lock (_gate)
            {
                index = _index;
                if (index is null || !string.Equals(index.ModelId, embedder.ModelId, StringComparison.Ordinal))
                {
                    index = new MemoryVectorIndex(_store, embedder.ModelId, embedder.Dimensions);
                    Volatile.Write(ref _index, index);
                }
            }
        }

        await index.ReloadIfStaleAsync(ct).ConfigureAwait(false);
        return index;
    }
}
