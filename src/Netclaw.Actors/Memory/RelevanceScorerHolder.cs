// -----------------------------------------------------------------------
// <copyright file="RelevanceScorerHolder.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
namespace Netclaw.Actors.Memory;

/// <summary>
/// Mutable holder for the process's <see cref="IRelevanceScorer"/> singleton
/// (memory-relevance-gate D4) — mirrors <see cref="MemoryEmbedderHolder"/>'s exact reason for
/// existing as a mutable holder rather than a plain DI singleton: the real scorer is only known
/// once <c>EmbeddingWarmupHostedService</c> (Netclaw.Daemon) finishes provisioning and loading
/// the relevance model, which necessarily runs after the DI container has already been built.
/// Consumers MUST read <see cref="Current"/> at the time they actually need to score (never
/// cache the value they read), so the transition from unavailable to available surfaces without
/// a process restart.
///
/// <para>
/// <b>Why the holder also carries <see cref="CalibratedThreshold"/>, not just the scorer:</b>
/// design D3's "the threshold travels with the model id" rule means a config default of
/// <c>null</c> for <c>Memory.Recall.RelevanceGate.Threshold</c> must resolve to whichever
/// threshold was calibrated for the model id currently loaded — a value <c>Netclaw.Actors</c>
/// otherwise has no way to learn, since the manifest entry that carries it
/// (<c>RelevanceModelManifestEntry</c>) lives in <c>Netclaw.Embeddings</c>, which
/// <c>Netclaw.Actors</c> never references. Keeping the threshold on this holder — set in the
/// same call that sets the scorer — keeps <see cref="IRelevanceScorer"/> itself pure (matching
/// D1's exact interface shape) while still letting the coordinator resolve "the active model's
/// calibrated threshold" through a seam it already depends on.
/// </para>
/// </summary>
public sealed class RelevanceScorerHolder : IDisposable
{
    private volatile IRelevanceScorer _current;
    private double _calibratedThreshold;
    private int _disposed;

    public RelevanceScorerHolder(IRelevanceScorer initial, double initialCalibratedThreshold)
    {
        ArgumentNullException.ThrowIfNull(initial);
        _current = initial;
        _calibratedThreshold = initialCalibratedThreshold;
    }

    /// <summary>The scorer to use right now. Always non-null.</summary>
    public IRelevanceScorer Current => _current;

    /// <summary>
    /// The calibrated operating threshold for whichever model id <see cref="Current"/> is
    /// currently scoring with — set atomically alongside the scorer by <see cref="Set"/>, so a
    /// reader can never observe a scorer paired with a stale (different model's) threshold.
    /// </summary>
    public double CalibratedThreshold => Volatile.Read(ref _calibratedThreshold);

    /// <summary>
    /// Replaces the current scorer and its calibrated threshold together. Called only by
    /// <c>EmbeddingWarmupHostedService</c> once provisioning completes — successfully (an
    /// <c>OnnxCrossEncoderScorer</c> paired with its manifest entry's
    /// <c>CalibratedThreshold</c>) or not (a fresh <see cref="UnavailableRelevanceScorer"/>
    /// carrying the failure reason, paired with the same manifest threshold since that value
    /// describes the model id, not whether it loaded).
    /// </summary>
    public void Set(IRelevanceScorer scorer, double calibratedThreshold)
    {
        ArgumentNullException.ThrowIfNull(scorer);
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

        var previous = Interlocked.Exchange(ref _current, scorer);
        Volatile.Write(ref _calibratedThreshold, calibratedThreshold);

        if (!ReferenceEquals(previous, scorer))
            (previous as IDisposable)?.Dispose();
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
            (_current as IDisposable)?.Dispose();
    }
}
