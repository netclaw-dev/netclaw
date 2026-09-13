// -----------------------------------------------------------------------
// <copyright file="MemoryEmbedderHolder.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
namespace Netclaw.Actors.Memory;

/// <summary>
/// Mutable holder for the process's <see cref="IMemoryEmbedder"/> singleton
/// (memory-core-redesign Slice 2, task 2.7).
///
/// <para>
/// <b>Why a holder, not a plain DI singleton:</b> the real embedder is only known once
/// <c>EmbeddingWarmupHostedService</c> (Netclaw.Daemon) finishes provisioning and loading the
/// model — an <see cref="Microsoft.Extensions.Hosting.IHostedService.StartAsync"/> step that
/// necessarily runs after the DI container has already been built and every other singleton
/// (the curation actor's session, the checkpoint worker) has already resolved its constructor
/// dependencies. A container builds its singleton graph once; there is no way to inject "the
/// embedder after warmup completes" into a constructor, only a slot that gets filled in later.
/// Consumers MUST read <see cref="Current"/> at the time they actually need to embed (never
/// cache the value they read), so the transition from unavailable to available — or the
/// reverse, if a future re-provision fails — surfaces without a process restart.
/// </para>
///
/// <para>
/// Every reader always sees a valid <see cref="IMemoryEmbedder"/> (construction requires an
/// initial value, typically an <see cref="UnavailableMemoryEmbedder"/> stub while warmup is
/// still running) — the holder itself is never null-valued, only whatever it currently holds
/// may report <see cref="IMemoryEmbedder.IsAvailable"/> as false.
/// </para>
///
/// <para>
/// <b>Why the holder also carries <see cref="QueryPrefix"/>/<see cref="CalibratedMinCosineSimilarity"/>,
/// not just the embedder (memory-query-prefix design D2/D3):</b> mirrors
/// <see cref="RelevanceScorerHolder.CalibratedThreshold"/>'s exact reasoning. The active model's
/// retrieval-query prefix and calibrated floor live on
/// <c>Netclaw.Embeddings.EmbeddingModelManifestEntry</c>, which <c>Netclaw.Actors</c> never
/// references — so those values must be carried alongside <see cref="Current"/>, set atomically
/// in the same <see cref="Set"/> call, rather than requiring <see cref="SQLiteMemoryRecallCoordinator"/>
/// (or the doctor check) to re-resolve the manifest entry themselves. A reader can therefore
/// never observe an embedder paired with a stale (different model's) prefix or floor.
/// </para>
/// </summary>
public sealed class MemoryEmbedderHolder : IDisposable
{
    private volatile IMemoryEmbedder _current;
    private volatile string _queryPrefix;
    private object? _calibratedMinCosineSimilarityBox;
    private int _disposed;

    public MemoryEmbedderHolder(IMemoryEmbedder initial, string initialQueryPrefix, double? initialCalibratedMinCosineSimilarity)
    {
        ArgumentNullException.ThrowIfNull(initial);
        ArgumentNullException.ThrowIfNull(initialQueryPrefix);
        _current = initial;
        _queryPrefix = initialQueryPrefix;
        _calibratedMinCosineSimilarityBox = initialCalibratedMinCosineSimilarity;
    }

    /// <summary>The embedder to use right now. Always non-null.</summary>
    public IMemoryEmbedder Current => _current;

    /// <summary>
    /// The active embedder's documented retrieval-query prefix (empty when the model documents
    /// none, or before warmup has populated a real value). Diagnostic use only (e.g. the doctor
    /// check reporting prefix presence) — the prefix is actually applied inside
    /// <c>OnnxMemoryEmbedder</c> itself when a caller passes <see cref="EmbeddingPurpose.RetrievalQuery"/>,
    /// not by any consumer of this holder.
    /// </summary>
    public string QueryPrefix => _queryPrefix;

    /// <summary>
    /// The calibrated absolute cosine floor for whichever model id <see cref="Current"/> is
    /// currently embedding with, in its documented retrieval-query encoding — set atomically
    /// alongside the embedder by <see cref="Set"/>. <c>null</c> means this model id's retrieval
    /// mode has not been calibrated: <see cref="SQLiteMemoryRecallCoordinator"/> treats null here
    /// combined with no explicit <c>Memory.Recall.MinCosineSimilarity</c> override as
    /// hybrid-recall-unavailable (design D3) rather than guessing a floor.
    /// </summary>
    public double? CalibratedMinCosineSimilarity => (double?)Volatile.Read(ref _calibratedMinCosineSimilarityBox);

    /// <summary>
    /// Replaces the current embedder and its manifest-carried prefix/calibration together.
    /// Called only by <c>EmbeddingWarmupHostedService</c> once provisioning completes —
    /// successfully (an <c>OnnxMemoryEmbedder</c> paired with its manifest entry's
    /// <c>QueryPrefix</c>/<c>CalibratedMinCosineSimilarity</c>) or not (a fresh
    /// <see cref="UnavailableMemoryEmbedder"/> carrying the failure reason, paired with the same
    /// manifest values since they describe the model id, not whether it loaded).
    /// </summary>
    public void Set(IMemoryEmbedder embedder, string queryPrefix, double? calibratedMinCosineSimilarity)
    {
        ArgumentNullException.ThrowIfNull(embedder);
        ArgumentNullException.ThrowIfNull(queryPrefix);
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

        var previous = Interlocked.Exchange(ref _current, embedder);
        _queryPrefix = queryPrefix;
        Volatile.Write(ref _calibratedMinCosineSimilarityBox, calibratedMinCosineSimilarity);

        if (!ReferenceEquals(previous, embedder))
            (previous as IDisposable)?.Dispose();
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
            (_current as IDisposable)?.Dispose();
    }
}
