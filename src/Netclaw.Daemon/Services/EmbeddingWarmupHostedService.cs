// -----------------------------------------------------------------------
// <copyright file="EmbeddingWarmupHostedService.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Netclaw.Actors.Memory;
using Netclaw.Configuration;
using Netclaw.Embeddings;

namespace Netclaw.Daemon.Services;

/// <summary>
/// Provisions/loads the embedding model at daemon startup, warms it up with one inference call,
/// then runs a gap-repair sweep over documents missing a current-model embedding
/// (memory-core-redesign Slice 2, task 2.7). Populates <see cref="MemoryEmbedderHolder"/>, which
/// every embed-on-write and (in later slices) recall consumer resolves at time of use.
///
/// <para>
/// <b>Never fails startup:</b> ANY failure here (missing model with <c>AutoDownload=false</c>,
/// download/hash failure, ONNX load failure) leaves the holder pointed at an
/// <see cref="UnavailableMemoryEmbedder"/> carrying the failure reason, logs
/// <c>memory_embedding_unavailable</c> at error level, and returns normally — degraded is a
/// running state, not a startup fault (design D2, spec "Loud degradation without silent
/// fallback"). This runs on a background thread pool task rather than blocking
/// <see cref="StartAsync"/> so a slow/hanging download can never delay the rest of the host's
/// startup sequence either.
/// </para>
/// </summary>
internal sealed class EmbeddingWarmupHostedService(
    EmbeddingModelProvisioner provisioner,
    SQLiteMemoryStore store,
    MemoryEmbedderHolder holder,
    MemoryConfig memoryConfig,
    NetclawPaths paths,
    ILogger<EmbeddingWarmupHostedService> logger) : IHostedService
{
    /// <summary>
    /// Gap-repair batch size. Kept small and yielding between batches (task 2.7) so a large
    /// backlog on a fresh <c>Enabled=true</c> flip does not monopolize the CPU the daemon needs
    /// for everything else at startup.
    /// </summary>
    internal const int GapRepairBatchSize = 16;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _ = Task.Run(() => WarmUpAsync(CancellationToken.None), CancellationToken.None);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <summary>Internal entry point so tests can await warmup to completion deterministically.</summary>
    internal async Task WarmUpAsync(CancellationToken ct)
    {
        if (!memoryConfig.Embeddings.Enabled)
        {
            logger.LogInformation(
                "memory_embedding_disabled reason={Reason}",
                "Memory.Embeddings.Enabled is false");
            return;
        }

        var modelId = memoryConfig.Embeddings.ModelId;
        IMemoryEmbedder embedder;
        try
        {
            embedder = await LoadEmbedderAsync(modelId, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "memory_embedding_unavailable model={ModelId} reason={Reason}", modelId, ex.Message);
            holder.Set(new UnavailableMemoryEmbedder(modelId, ex.Message));
            return;
        }

        holder.Set(embedder);
        logger.LogInformation(
            "memory_embedding_ready model={ModelId} dims={Dimensions}",
            embedder.ModelId,
            embedder.Dimensions);

        try
        {
            await GapRepairAsync(embedder, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // The embedder itself is already loaded and the holder is already populated — a
            // gap-repair failure (e.g. a transient store error) must not undo that or leave an
            // unobserved exception on this fire-and-forget warmup task. The doctor check and
            // the next daemon restart's sweep both retry whatever remains unembedded.
            logger.LogWarning(ex, "memory_embedding_gap_repair_failed model={ModelId}", embedder.ModelId);
        }
    }

    private async Task<IMemoryEmbedder> LoadEmbedderAsync(string modelId, CancellationToken ct)
    {
        var modelDirectory = paths.EmbeddingModelDirectory(modelId);

        ProvisionedEmbeddingModel provisioned;
        if (memoryConfig.Embeddings.AutoDownload)
        {
            provisioned = await provisioner.ProvisionAsync(modelId, modelDirectory, ct).ConfigureAwait(false);
        }
        else
        {
            // AutoDownload=false gates the network path entirely — even to repair a corrupted
            // local copy. A missing/invalid model here is a loud degraded-mode condition, not a
            // fallback to fetching it anyway.
            provisioned = await provisioner.TryLoadVerifiedAsync(modelId, modelDirectory, ct).ConfigureAwait(false)
                ?? throw new InvalidOperationException(
                    $"Embedding model '{modelId}' is not provisioned (or failed hash verification) at " +
                    $"{modelDirectory}, and Memory.Embeddings.AutoDownload is false. Provision it manually " +
                    "or enable AutoDownload, then restart the daemon or run `netclaw memory backfill-embeddings`.");
        }

        var embedder = await OnnxMemoryEmbedder.LoadAsync(
            provisioned.ModelPath,
            provisioned.VocabPath,
            provisioned.ModelId,
            provisioned.Dimensions,
            ct: ct).ConfigureAwait(false);

        // Warm-up inference (design D1/D2): pays first-call ONNX session / JIT cost here rather
        // than on the first real memory write or recall query.
        await embedder.EmbedAsync("netclaw embedding warmup", ct).ConfigureAwait(false);

        return embedder;
    }

    /// <summary>
    /// Embeds every recallable document missing a current-model/current-hash embedding, in
    /// small batches, yielding between batches (task 2.7). This is what self-heals the gap
    /// described in design D3's failure/recovery note: a crash between a document commit and
    /// its embedding upsert leaves a missing-embedding row, which this sweep (and the embedding
    /// doctor check) both detect and repair.
    /// </summary>
    private async Task GapRepairAsync(IMemoryEmbedder embedder, CancellationToken ct)
    {
        var missing = await store.GetDocumentsNeedingEmbeddingAsync(embedder.ModelId, force: false, ct).ConfigureAwait(false);
        if (missing.Count == 0)
        {
            logger.LogInformation("memory_embedding_gap_repair_complete embedded=0 model={ModelId}", embedder.ModelId);
            return;
        }

        var embedded = 0;
        var failed = 0;
        for (var offset = 0; offset < missing.Count; offset += GapRepairBatchSize)
        {
            var batch = missing.Skip(offset).Take(GapRepairBatchSize).ToArray();
            var texts = batch.Select(d => $"{d.Title}\n{d.Body}").ToArray();

            try
            {
                var vectors = await embedder.EmbedBatchAsync(texts, ct).ConfigureAwait(false);
                for (var i = 0; i < batch.Length; i++)
                {
                    var hash = MemoryContentHasher.ComputeHash(batch[i].Title, batch[i].Body);
                    await store.UpsertEmbeddingAsync(
                        batch[i].DocumentId, MemoryEmbedOnWriteCoordinator.DocumentItemKind,
                        embedder.ModelId, hash, vectors[i], ct).ConfigureAwait(false);
                    embedded++;
                }
            }
            catch (Exception ex)
            {
                // One bad batch must not abort the sweep — the doctor check and the next
                // restart's sweep will retry whatever remains missing.
                failed += batch.Length;
                logger.LogWarning(ex, "memory_embedding_gap_repair_batch_failed count={Count}", batch.Length);
            }

            // Yield between batches so gap-repair on a large backlog does not monopolize the
            // CPU the daemon needs for everything else at startup.
            await Task.Yield();
        }

        logger.LogInformation(
            "memory_embedding_gap_repair_complete embedded={Embedded} failed={Failed} model={ModelId}",
            embedded, failed, embedder.ModelId);
    }
}
