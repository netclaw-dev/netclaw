// -----------------------------------------------------------------------
// <copyright file="MemoryEmbedOnWriteCoordinator.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Akka.Event;
using Microsoft.Extensions.Logging;

namespace Netclaw.Actors.Memory;

/// <summary>
/// One <c>memory_documents</c> row written by a curation batch-apply
/// (<see cref="SQLiteMemoryStore.ApplyInlineCurationBatchAsync"/> or
/// <see cref="SQLiteMemoryStore.ApplyCurationBatchAsync"/>), carrying exactly what
/// <see cref="MemoryEmbedOnWriteCoordinator"/> needs to embed it: the final (post-anchor-
/// resolution) document id and the text that was persisted. Immutable <c>memory_records</c>
/// (Evidence) are never included — they bypass curation evaluation entirely (see
/// <see cref="MemoryCurationEvaluator.EvaluateAsync"/>'s "immutable record bypass") and are
/// excluded from embedding coverage by the same scope <see cref="SQLiteMemoryStore.GetEmbeddingCoverageAsync"/>
/// already uses (its coverage query only reads <c>memory_documents</c>).
/// </summary>
public sealed record MemoryDocumentWriteResult(string DocumentId, string Title, string Body);

/// <summary>
/// Embed-on-write hook for memory-core-redesign Slice 2 (task 2.8), called once per commit by
/// both curation write pipelines after their store batch-apply call returns:
/// <see cref="Netclaw.Actors.Memory.MemoryCurationActor"/> (inline per-session path, after
/// <see cref="SQLiteMemoryStore.ApplyInlineCurationBatchAsync"/>) and
/// <c>Netclaw.Daemon.Services.MemoryCurationWorkerService</c> (checkpoint-worker path, after
/// <see cref="SQLiteMemoryStore.ApplyCurationBatchAsync"/>). This is the one place embed-on-
/// write logic lives — the two call sites exist because two physically separate store commit
/// methods exist by design (D3: the store's standalone-initialization contract is preserved
/// per-pipeline), not because the logic itself is duplicated.
///
/// <para>
/// <b>Failure isolation:</b> by the time this runs, the memory write has already committed.
/// Vectors are derived data (design D3) — an embedding failure here must never fail, retry, or
/// roll back the write it followed. Each item's hash+embed+upsert is wrapped individually so
/// one bad item does not block the rest of the batch; a failure logs a warning and is left for
/// the startup gap-repair sweep (<c>EmbeddingWarmupHostedService</c>) or
/// <c>netclaw memory backfill-embeddings</c> to self-heal. There is no per-write degradation
/// log when the embedder is simply unavailable — that condition already gets a loud signal once
/// (the warmup failure log + doctor + daemon status), so logging it again on every write would
/// be spam, not signal; a debug-level line is enough for local troubleshooting.
/// </para>
/// </summary>
public static class MemoryEmbedOnWriteCoordinator
{
    /// <summary><c>item_kind</c> value written for every embedded <c>memory_documents</c> row.</summary>
    public const string DocumentItemKind = "document";

    /// <summary>Entry point for the inline per-session actor (Akka logging).</summary>
    public static Task EmbedWrittenDocumentsAsync(
        MemoryEmbedderHolder? holder,
        SQLiteMemoryStore store,
        IReadOnlyList<MemoryDocumentWriteResult> written,
        ILoggingAdapter log,
        CancellationToken ct = default)
        => EmbedWrittenDocumentsCoreAsync(holder, store, written, new AkkaCurationLog(log), ct);

    /// <summary>Entry point for the daemon checkpoint worker (Microsoft.Extensions.Logging).</summary>
    public static Task EmbedWrittenDocumentsAsync(
        MemoryEmbedderHolder? holder,
        SQLiteMemoryStore store,
        IReadOnlyList<MemoryDocumentWriteResult> written,
        ILogger log,
        CancellationToken ct = default)
        => EmbedWrittenDocumentsCoreAsync(holder, store, written, new MicrosoftCurationLog(log), ct);

    private static async Task EmbedWrittenDocumentsCoreAsync(
        MemoryEmbedderHolder? holder,
        SQLiteMemoryStore store,
        IReadOnlyList<MemoryDocumentWriteResult> written,
        ICurationLog log,
        CancellationToken ct)
    {
        if (written.Count == 0)
            return;

        var embedder = holder?.Current;
        if (embedder is null || !embedder.IsAvailable)
        {
            // Not the loud signal — the warmup failure log + doctor + daemon status already
            // cover that. This is local troubleshooting detail only.
            log.Debug("memory_embed_on_write_skipped reason=embedder_unavailable count={0}", written.Count);
            return;
        }

        foreach (var doc in written)
        {
            try
            {
                var hash = MemoryContentHasher.ComputeHash(doc.Title, doc.Body);
                var vector = await embedder.EmbedAsync($"{doc.Title}\n{doc.Body}", EmbeddingPurpose.Passage, ct).ConfigureAwait(false);
                await store.UpsertEmbeddingAsync(
                    doc.DocumentId, DocumentItemKind, embedder.ModelId, hash, vector, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                log.Warning(ex, "memory_embed_on_write_failed documentId={0}", doc.DocumentId);
            }
        }
    }
}
