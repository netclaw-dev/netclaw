// -----------------------------------------------------------------------
// <copyright file="IMemoryEmbedder.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
namespace Netclaw.Actors.Memory;

/// <summary>
/// Consumer-defined seam for computing memory embeddings (memory-core-redesign D1). Owned by
/// the memory subsystem, not the embedding runtime, so actor code never references OnnxRuntime
/// or any other inference library: <c>Netclaw.Embeddings</c>'s <c>OnnxMemoryEmbedder</c>
/// implements this interface and is wired in by the daemon; <c>Netclaw.Actors</c> never
/// references that project.
///
/// <para>
/// <see cref="IsAvailable"/> is the degraded-mode contract. When false, every write and
/// recall path that would otherwise consult embeddings MUST fall back to its lexical path
/// instead — loudly (a logged degradation event and a doctor/status surface land in later
/// slices), never silently. <see cref="EmbedAsync"/> and <see cref="EmbedBatchAsync"/> are
/// only ever meant to be called when <see cref="IsAvailable"/> is true; an implementation
/// whose model failed to load (<see cref="UnavailableMemoryEmbedder"/>) throws rather than
/// returning a zero or garbage vector, because a garbage vector would silently corrupt
/// cosine-similarity scoring instead of visibly failing the caller that skipped the check.
/// </para>
/// </summary>
public interface IMemoryEmbedder
{
    /// <summary>
    /// The allowlisted model id this embedder was provisioned with (e.g.
    /// <c>snowflake-arctic-embed-m</c>). Vectors are keyed by <c>(item id, model id)</c> in
    /// storage so a model change never silently compares vectors across incompatible spaces.
    /// </summary>
    string ModelId { get; }

    /// <summary>Embedding vector width produced by <see cref="ModelId"/>.</summary>
    int Dimensions { get; }

    /// <summary>
    /// True when this embedder can actually compute embeddings right now. False is a real,
    /// expected operating mode (model not yet provisioned, hash verification failed, runtime
    /// load error) — not a condition for the embedder itself to throw on; only calling
    /// <see cref="EmbedAsync"/> or <see cref="EmbedBatchAsync"/> while unavailable throws.
    /// </summary>
    bool IsAvailable { get; }

    /// <summary>
    /// Embed a single piece of text. Callers MUST check <see cref="IsAvailable"/> first;
    /// calling this while unavailable throws rather than degrading silently.
    /// </summary>
    ValueTask<ReadOnlyMemory<float>> EmbedAsync(string text, CancellationToken ct);

    /// <summary>
    /// Embed a batch of texts, preserving input order in the output list. Batching lets
    /// callers (backfill, gap-repair) amortize per-call overhead that the single-item path
    /// pays every time.
    /// </summary>
    ValueTask<IReadOnlyList<ReadOnlyMemory<float>>> EmbedBatchAsync(IReadOnlyList<string> texts, CancellationToken ct);
}

/// <summary>
/// Degraded-mode stub used when no embedding model is provisioned, hash verification failed,
/// or the runtime failed to load. <see cref="IsAvailable"/> is permanently false for an
/// instance of this type. It intentionally lives in <c>Netclaw.Actors</c> rather than
/// <c>Netclaw.Embeddings</c> — it needs no OnnxRuntime dependency, and keeping it beside
/// <see cref="IMemoryEmbedder"/> means a caller can always construct a safe default without
/// referencing the embeddings project at all (e.g. in tests, or a config path that disables
/// embeddings entirely).
///
/// <para>
/// This type does not log on its own: it does not know whether it is degrading a write or a
/// recall path, and logging here would double-count against the caller's own degradation log
/// (<c>memory_recall_vector_degraded</c> and friends, added in later slices). Calling
/// <see cref="EmbedAsync"/> or <see cref="EmbedBatchAsync"/> anyway is a caller bug — code that
/// didn't check <see cref="IsAvailable"/> first — so both throw rather than returning a zero
/// vector that would silently poison cosine-similarity scoring.
/// </para>
/// </summary>
public sealed class UnavailableMemoryEmbedder(string modelId, string reason) : IMemoryEmbedder
{
    public string ModelId { get; } = modelId;

    /// <summary>
    /// No model is loaded, so there is no real vector width; 0 is the sentinel value for
    /// "produces no vectors."
    /// </summary>
    public int Dimensions => 0;

    public bool IsAvailable => false;

    public ValueTask<ReadOnlyMemory<float>> EmbedAsync(string text, CancellationToken ct)
        => throw new InvalidOperationException(BuildMessage(nameof(EmbedAsync)));

    public ValueTask<IReadOnlyList<ReadOnlyMemory<float>>> EmbedBatchAsync(IReadOnlyList<string> texts, CancellationToken ct)
        => throw new InvalidOperationException(BuildMessage(nameof(EmbedBatchAsync)));

    private string BuildMessage(string calledMethod)
        => $"Embedding model '{ModelId}' is unavailable ({reason}). Provision it (auto-download " +
           "or `netclaw memory backfill-embeddings`) and check `netclaw doctor` for remediation. " +
           $"Callers must check IsAvailable before calling {calledMethod}.";
}
