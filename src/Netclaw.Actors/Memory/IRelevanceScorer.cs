// -----------------------------------------------------------------------
// <copyright file="IRelevanceScorer.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
namespace Netclaw.Actors.Memory;

/// <summary>
/// Consumer-defined seam for cross-encoder relevance scoring (memory-relevance-gate D1) —
/// mirrors <see cref="IMemoryEmbedder"/>'s exact shape and contract so the memory subsystem
/// gains a second in-process model runtime without a second design vocabulary. Owned by the
/// memory subsystem, not the inference runtime: <c>Netclaw.Embeddings</c>'s
/// <c>OnnxCrossEncoderScorer</c> implements this interface and is wired in by the daemon;
/// <c>Netclaw.Actors</c> never references OnnxRuntime.
///
/// <para>
/// Unlike <see cref="IMemoryEmbedder"/>, a relevance scorer encodes the query and each
/// candidate <b>jointly</b> (one forward pass per pair) rather than independently — this is
/// what lets the score reflect "does this candidate help answer the query" rather than mere
/// topical similarity, and is the entire reason this seam exists alongside the embedder rather
/// than being folded into it.
/// </para>
///
/// <para>
/// <see cref="IsAvailable"/> is the same degraded-mode contract as
/// <see cref="IMemoryEmbedder.IsAvailable"/>: false is a real, expected operating state (not
/// provisioned, hash verification failed, runtime load error), and every recall path that would
/// otherwise consult the gate MUST fall back to floor-only behavior instead — loudly (a rate-
/// limited degradation log and doctor visibility), never silently. <see cref="ScoreAsync"/> is
/// only ever meant to be called when <see cref="IsAvailable"/> is true; an implementation whose
/// model failed to load (<see cref="UnavailableRelevanceScorer"/>) throws rather than returning
/// a fabricated score, because a fabricated score would silently corrupt threshold gating
/// instead of visibly failing the caller that skipped the check.
/// </para>
/// </summary>
public interface IRelevanceScorer
{
    /// <summary>
    /// The allowlisted relevance-model id this scorer was provisioned with. Scores are never
    /// compared across models — same rule as <see cref="IMemoryEmbedder.ModelId"/> for
    /// embedding vectors — because the calibrated operating threshold is calibrated against one
    /// specific model's score distribution (memory-relevance-gate D3).
    /// </summary>
    string ModelId { get; }

    /// <summary>
    /// True when this scorer can actually score right now. False is a real, expected operating
    /// mode (model not yet provisioned, hash verification failed, runtime load error) — not a
    /// condition for the scorer itself to throw on; only calling <see cref="ScoreAsync"/> while
    /// unavailable throws.
    /// </summary>
    bool IsAvailable { get; }

    /// <summary>
    /// Scores each of <paramref name="candidates"/> jointly against <paramref name="query"/>,
    /// preserving input order in the output list — one call per turn for the floor-surviving
    /// candidates (bounded to <c>Memory.AutoRecallMaxItems</c>), mirroring
    /// <see cref="IMemoryEmbedder.EmbedBatchAsync"/>'s batching rationale. Callers MUST check
    /// <see cref="IsAvailable"/> first; calling this while unavailable throws rather than
    /// degrading silently. Scores are raw sigmoid-activated probabilities in [0, 1]; the caller
    /// compares them against the active threshold, this method has no opinion on what "passes."
    /// </summary>
    ValueTask<IReadOnlyList<double>> ScoreAsync(string query, IReadOnlyList<string> candidates, CancellationToken ct);
}

/// <summary>
/// Degraded-mode stub used when no relevance model is provisioned, hash verification failed, or
/// the runtime failed to load. <see cref="IsAvailable"/> is permanently false for an instance of
/// this type. Mirrors <see cref="UnavailableMemoryEmbedder"/> byte for byte: it lives beside
/// <see cref="IRelevanceScorer"/> in <c>Netclaw.Actors</c> (no OnnxRuntime dependency) so any
/// caller can always construct a safe default, and it does not log on its own — the caller's
/// own rate-limited degradation log (<c>memory_recall_gate_degraded</c>) is the single place
/// that decision is recorded, so this stub logging too would double-count it.
/// </summary>
public sealed class UnavailableRelevanceScorer(string modelId, string reason) : IRelevanceScorer
{
    public string ModelId { get; } = modelId;

    public bool IsAvailable => false;

    public ValueTask<IReadOnlyList<double>> ScoreAsync(string query, IReadOnlyList<string> candidates, CancellationToken ct)
        => throw new InvalidOperationException(BuildMessage(nameof(ScoreAsync)));

    private string BuildMessage(string calledMethod)
        => $"Relevance model '{ModelId}' is unavailable ({reason}). Provision it (auto-download " +
           "at daemon startup) and check `netclaw doctor` for remediation. " +
           $"Callers must check IsAvailable before calling {calledMethod}.";
}
