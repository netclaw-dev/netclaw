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
/// every embed-on-write and (in later slices) recall consumer resolves at time of use. Also
/// provisions/warms the post-floor relevance-gate's cross-encoder model
/// (memory-relevance-gate D4, task 1.4), populating <see cref="RelevanceScorerHolder"/> — a
/// second, independent provision-or-degrade step gated by the same
/// <c>Memory.Embeddings.Enabled</c> switch, with no gap-repair analogue (there is no per-item
/// derived state for a scoring-only model to repair).
///
/// <para>
/// <b>Never fails startup:</b> ANY failure here (missing model with <c>AutoDownload=false</c>,
/// download/hash failure, ONNX load failure) leaves the holder pointed at an
/// <see cref="UnavailableMemoryEmbedder"/> (or, for the relevance gate,
/// <see cref="UnavailableRelevanceScorer"/>) carrying the failure reason, logs
/// <c>memory_embedding_unavailable</c> (or <c>memory_relevance_gate_unavailable</c>) at error
/// level, and returns normally — degraded is a running state, not a startup fault (design D2,
/// spec "Loud degradation without silent fallback"). This runs on a background thread pool task
/// rather than blocking <see cref="StartAsync"/> so a slow/hanging download can never delay the
/// rest of the host's startup sequence either.
/// </para>
///
/// <para>
/// <b>Keep-warm (memory-relevance-gate 2026-07 canary fix):</b> the one-shot warm-up call above
/// only pays first-call ONNX session / JIT cost once, at startup. On a long-lived daemon a
/// subsequent idle gap (no memory-touching turns for a while — the exact shape of a scheduled
/// reminder session waking up) lets the OS page out an ONNX session's working set entirely; the
/// next real turn then pays a full cold-start cost that a fixed per-turn sub-budget was never
/// sized for. A live production canary caught exactly this: two <c>memory_recall_gate_degraded</c>
/// events with <c>TaskCanceledException</c>, both in reminder sessions firing after an idle
/// period. <see cref="KeepWarmLoopAsync"/> re-exercises both ONNX sessions on a periodic tick
/// while embeddings are enabled, so neither ever goes cold enough to blow its sub-budget on the
/// next real turn — see <see cref="Netclaw.Actors.Sessions.SQLiteMemoryRecallCoordinator"/>'s
/// relevance-gate sub-budget remarks for the other half of this fix (the envelope-derived
/// sub-budget clamp).
/// </para>
///
/// <para>
/// <b>Operator alerting:</b> the log line alone is not operator-facing — nobody watches daemon
/// logs in steady state, and the health endpoint/doctor check are pull-based (someone has to go
/// look). Each provision-or-degrade failure above additionally fires an
/// <see cref="OperationalAlert"/> through the injected <see cref="IOperationalNotificationSink"/>
/// (the same push-to-operator seam <c>McpReconnectionService</c>, <c>ReminderManagerActor</c>, and
/// <c>RoutingChatClient</c> already use for MCP/reminder/provider degradation) carrying the model
/// id, the failure reason, the concrete consequence (lexical-only recall/dedup, or an unfiltered
/// relevance gate), and a remediation hint. Latched per model (<see cref="_embedderAlertFired"/>,
/// <see cref="_relevanceAlertFired"/>) so a given model fires at most once per daemon run — this
/// method only ever runs once per host lifetime in production (see <see cref="StartAsync"/>), but
/// the latch is cheap insurance against a future caller awaiting it more than once, and is the
/// seam a mid-run keep-warm failure would also latch through if that path is ever wired up (see
/// <see cref="LogKeepWarmFailed"/>'s remarks for why it currently is not). No alert fires when
/// <c>Memory.Embeddings.Enabled</c> is false — that is an intentional, not degraded, state.
/// </para>
/// </summary>
internal sealed class EmbeddingWarmupHostedService(
    EmbeddingModelProvisioner provisioner,
    SQLiteMemoryStore store,
    MemoryEmbedderHolder holder,
    RelevanceScorerHolder relevanceScorerHolder,
    IReadOnlyDictionary<string, EmbeddingModelManifestEntry> allowlist,
    IReadOnlyDictionary<string, RelevanceModelManifestEntry> relevanceAllowlist,
    MemoryConfig memoryConfig,
    NetclawPaths paths,
    TimeProvider timeProvider,
    IOperationalNotificationSink notificationSink,
    ILogger<EmbeddingWarmupHostedService> logger) : IHostedService, IDisposable
{
    /// <summary>
    /// Gap-repair batch size. Kept small and yielding between batches (task 2.7) so a large
    /// backlog on a fresh <c>Enabled=true</c> flip does not monopolize the CPU the daemon needs
    /// for everything else at startup.
    /// </summary>
    internal const int GapRepairBatchSize = 16;

    /// <summary>
    /// Keep-warm tick period (memory-relevance-gate 2026-07 canary fix). Frequent enough that
    /// neither ONNX session's working set gets fully paged out between ticks on the idle-reminder
    /// shape the canary caught, cheap enough (one tiny embed + one tiny 1-pair score, a handful of
    /// milliseconds warm) that it is negligible background CPU for a daemon otherwise doing
    /// nothing.
    /// </summary>
    internal static readonly TimeSpan KeepWarmInterval = TimeSpan.FromMinutes(5);

    /// <summary>Fixed, tiny keep-warm query/candidate text — content is irrelevant, only inference-path exercise matters.</summary>
    private const string KeepWarmQueryText = "netclaw keep-warm probe";

    private const string KeepWarmCandidateText = "netclaw keep-warm reference candidate";

    /// <summary>Minimum interval between two keep-warm-failure debug log lines, mirroring the recall coordinator's degradation-log cooldowns.</summary>
    private static readonly TimeSpan KeepWarmFailureLogCooldown = TimeSpan.FromMinutes(5);

    private readonly CancellationTokenSource _lifetimeCts = new();
    private Task? _warmupTask;
    private Task? _keepWarmLoop;
    // 0 (not long.MinValue) is the safe "never logged" sentinel: any real Unix-ms timestamp minus
    // 0 is astronomically larger than KeepWarmFailureLogCooldown, so the very first failure always
    // logs, and there is no risk of the subtraction below overflowing.
    private long _lastKeepWarmFailureLogMs;

    // Operator-alert latches (0/1 via Interlocked.CompareExchange): guarantee each model fires at
    // most one OperationalAlert per daemon run even though this is currently only ever reachable
    // from one call site each (see the class remarks' "Operator alerting" paragraph).
    private int _embedderAlertFired;
    private int _relevanceAlertFired;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _warmupTask = Task.Run(() => WarmUpAsync(_lifetimeCts.Token), CancellationToken.None);
        _keepWarmLoop = Task.Run(() => KeepWarmLoopAsync(_lifetimeCts.Token), CancellationToken.None);
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        await _lifetimeCts.CancelAsync();
        if (_warmupTask is not null)
            await _warmupTask.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);

        if (_keepWarmLoop is not null)
            await _keepWarmLoop.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
    }

    public void Dispose() => _lifetimeCts.Dispose();

    /// <summary>
    /// Periodic keep-warm loop (memory-relevance-gate 2026-07 canary fix): ticks every
    /// <see cref="KeepWarmInterval"/> for as long as embeddings are enabled, re-exercising both
    /// ONNX sessions via <see cref="KeepWarmTickAsync"/> so an idle gap between real turns never
    /// lets either session's working set page out entirely. Built on <see cref="PeriodicTimer"/>
    /// over the injected <see cref="TimeProvider"/> — the same virtualizable-timer pattern
    /// <c>McpReconnectionService</c> already uses for its own periodic tick — so tests can drive
    /// ticks deterministically with a <c>FakeTimeProvider</c> instead of real wall-clock delays.
    /// A disabled config is checked once up front rather than per tick: an operator flip requires
    /// a restart, same as every other <c>Memory.*</c> setting this service already assumes.
    /// </summary>
    internal async Task KeepWarmLoopAsync(CancellationToken ct)
    {
        if (!memoryConfig.Embeddings.Enabled)
            return;

        using var timer = new PeriodicTimer(KeepWarmInterval, timeProvider);
        while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
        {
            await KeepWarmTickAsync(ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// One keep-warm tick: a single tiny embed (<see cref="EmbeddingPurpose.RetrievalQuery"/>,
    /// mirroring the shape of a real recall turn's query embed) and a single tiny 1-pair
    /// cross-encoder score, each only attempted while its holder currently reports
    /// <c>IsAvailable</c> (a holder still pointed at an <c>Unavailable*</c> stub — warmup not yet
    /// complete, or a load that failed — has nothing to keep warm). Never throws: any failure
    /// (a transient ONNX error, a holder swapped mid-tick) is caught and rate-limited-logged at
    /// Debug, since a missed keep-warm tick is not itself a user-visible degradation — the next
    /// tick or the next real turn's own degradation path is what would actually surface a
    /// persistently broken model.
    /// </summary>
    internal async Task KeepWarmTickAsync(CancellationToken ct)
    {
        try
        {
            var embedder = holder.Current;
            if (embedder.IsAvailable)
                await embedder.EmbedAsync(KeepWarmQueryText, EmbeddingPurpose.RetrievalQuery, ct).ConfigureAwait(false);

            var scorer = relevanceScorerHolder.Current;
            if (scorer.IsAvailable)
                await scorer.ScoreAsync(KeepWarmQueryText, [KeepWarmCandidateText], ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Host shutdown mid-tick -- let this propagate so KeepWarmLoopAsync's own
            // WaitForNextTickAsync(ct) loop unwinds normally instead of being masked as a tick
            // failure.
            throw;
        }
        catch (Exception ex)
        {
            LogKeepWarmFailed(ex);
        }
    }

    /// <summary>
    /// Rate-limited keep-warm-failure log: at most one Debug line per
    /// <see cref="KeepWarmFailureLogCooldown"/>, the same cooldown-throttle shape
    /// <c>SQLiteMemoryRecallCoordinator</c>'s degradation logs use, so a persistently failing
    /// keep-warm tick (e.g. a model that failed to load) does not spam the log every 5 minutes
    /// forever.
    ///
    /// <para>
    /// <b>Deliberately not wired to the operator-alert latches:</b> a single keep-warm tick
    /// failure is a transient probe result (a slow/hung ONNX call under load, a momentary holder
    /// swap mid-tick), not proof a model "went bad" — the very next tick, 5 minutes later, may
    /// well succeed. Promoting the first miss to an operator page would be a false-positive
    /// alert on exactly the condition this method's own doc comment already calls out as not
    /// user-visible degradation. Doing this properly needs a consecutive-failure threshold
    /// (mirroring <c>ReminderManagerActor</c>'s auto-disable threshold pattern) before treating a
    /// keep-warm miss as equivalent-severity to a provisioning failure — a real design decision,
    /// not just plumbing, so it is left as a follow-up rather than bolted on here.
    /// </para>
    /// </summary>
    private void LogKeepWarmFailed(Exception ex)
    {
        var nowMs = timeProvider.GetUtcNow().ToUnixTimeMilliseconds();
        var lastMs = Interlocked.Read(ref _lastKeepWarmFailureLogMs);
        if (nowMs - lastMs < KeepWarmFailureLogCooldown.TotalMilliseconds)
            return;

        Interlocked.Exchange(ref _lastKeepWarmFailureLogMs, nowMs);
        logger.LogDebug(ex, "memory_embedding_keep_warm_failed");
    }

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

        // Prefix/floor are looked up unconditionally (success or failure below) since they
        // describe the model id, not whether it actually loaded (memory-query-prefix design
        // D2/D3) -- mirrors WarmUpRelevanceGateAsync's calibratedThreshold lookup exactly.
        allowlist.TryGetValue(modelId, out var manifestEntry);
        var queryPrefix = manifestEntry?.QueryPrefix ?? string.Empty;
        var calibratedMinCosineSimilarity = manifestEntry?.CalibratedMinCosineSimilarity;

        // Nullable and only ever assigned on the success path below -- deliberately NOT an early
        // return out of the catch block (a pre-existing bug this PR fixes: the relevance gate's
        // provisioning attempt below was unreachable whenever the embedder itself failed,
        // contradicting this method's own "runs regardless" contract for the relevance gate, and
        // silently suppressing the relevance-model alert in exactly the both-models-degraded case
        // an operator most needs to hear about).
        IMemoryEmbedder? embedder = null;
        try
        {
            embedder = await LoadEmbedderAsync(modelId, queryPrefix, ct).ConfigureAwait(false);
            holder.Set(embedder, queryPrefix, calibratedMinCosineSimilarity);
            logger.LogInformation(
                "memory_embedding_ready model={ModelId} dims={Dimensions} hasQueryPrefix={HasQueryPrefix} calibratedMinCosineSimilarity={CalibratedMinCosineSimilarity}",
                embedder.ModelId,
                embedder.Dimensions,
                queryPrefix.Length > 0,
                calibratedMinCosineSimilarity);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "memory_embedding_unavailable model={ModelId} reason={Reason}", modelId, ex.Message);
            holder.Set(new UnavailableMemoryEmbedder(modelId, ex.Message), queryPrefix, calibratedMinCosineSimilarity);
            EmitEmbedderUnavailableAlert(modelId, ex.Message);
        }

        if (embedder is not null)
        {
            try
            {
                await GapRepairAsync(embedder, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
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

        // Relevance gate (memory-relevance-gate, design D4, task 1.4): a second, independent
        // provision-or-degrade step gated by the same Memory.Embeddings.Enabled switch (D6's
        // "one mental switch" — there is no separate RelevanceGate.AutoDownload/ModelId knob).
        // Runs regardless of whether the embedder itself just degraded above: the two models are
        // separately lifecycled artifacts, so an embedder failure should not also prevent an
        // attempt to provision the relevance model. No gap-repair analogue exists here — there is
        // no per-item derived state to repair for a scoring-only model.
        await WarmUpRelevanceGateAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Provisions and warms the relevance (cross-encoder) model, mirroring
    /// <see cref="LoadEmbedderAsync"/>'s provision-or-degrade shape exactly. The manifest's
    /// <c>CalibratedThreshold</c> is looked up unconditionally (success or failure) since it
    /// describes the model id, not whether the model actually loaded — <see cref="RelevanceScorerHolder"/>
    /// always pairs a scorer (available or not) with the correct threshold for its model id.
    /// </summary>
    private async Task WarmUpRelevanceGateAsync(CancellationToken ct)
    {
        var modelId = EmbeddingModelProvisioner.DefaultRelevanceModelId;
        var calibratedThreshold = relevanceAllowlist.TryGetValue(modelId, out var entry)
            ? entry.CalibratedThreshold
            : 0.0;

        try
        {
            var scorer = await LoadRelevanceScorerAsync(modelId, ct).ConfigureAwait(false);
            relevanceScorerHolder.Set(scorer, calibratedThreshold);
            logger.LogInformation("memory_relevance_gate_ready model={ModelId}", scorer.ModelId);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "memory_relevance_gate_unavailable model={ModelId} reason={Reason}", modelId, ex.Message);
            relevanceScorerHolder.Set(new UnavailableRelevanceScorer(modelId, ex.Message), calibratedThreshold);
            EmitRelevanceModelUnavailableAlert(modelId, ex.Message);
        }
    }

    /// <summary>
    /// Fires <see cref="AlertType.MemoryEmbeddingModelUnavailable"/> at most once per daemon run
    /// (see <see cref="_embedderAlertFired"/>). Content mirrors the doctor check's own remediation
    /// wording (<c>MemoryEmbeddingDoctorCheck</c>) so an operator sees the same guidance whether
    /// they are pulling <c>netclaw doctor</c> or reacting to a pushed alert.
    /// </summary>
    private void EmitEmbedderUnavailableAlert(string modelId, string reason)
    {
        if (Interlocked.CompareExchange(ref _embedderAlertFired, 1, 0) != 0)
            return;

        const string consequence = "Memory recall/dedup is running lexical-only — semantic features are degraded.";
        const string remediation = "Check network access and disk space, run `netclaw doctor`, or run " +
            "`netclaw memory backfill-embeddings` — the daemon re-provisions the model on its next start.";

        notificationSink.Emit(OperationalAlert.Create(
            timeProvider,
            "memory.embedding_model.unavailable",
            AlertType.MemoryEmbeddingModelUnavailable,
            $"Memory embedding model '{modelId}' could not be provisioned or loaded: {reason} {consequence}",
            AlertSeverity.Warning,
            source: modelId,
            context: new Dictionary<string, string>
            {
                ["modelId"] = modelId,
                ["reason"] = reason,
                ["consequence"] = consequence,
                ["remediation"] = remediation,
            }));
    }

    /// <summary>
    /// Fires <see cref="AlertType.MemoryRelevanceModelUnavailable"/> at most once per daemon run
    /// (see <see cref="_relevanceAlertFired"/>). Unlike <see cref="EmitEmbedderUnavailableAlert"/>,
    /// the remediation does not mention <c>netclaw memory backfill-embeddings</c> — that command
    /// only re-embeds the document corpus, it has no relevance-model analogue (mirrors
    /// <c>MemoryRelevanceGateDoctorCheck</c>'s own remediation wording).
    /// </summary>
    private void EmitRelevanceModelUnavailableAlert(string modelId, string reason)
    {
        if (Interlocked.CompareExchange(ref _relevanceAlertFired, 1, 0) != 0)
            return;

        const string consequence = "The relevance gate is disabled — recall is unfiltered by the cross-encoder.";
        const string remediation = "Check network access and disk space, then run `netclaw doctor` or restart the " +
            "daemon to re-provision the model.";

        notificationSink.Emit(OperationalAlert.Create(
            timeProvider,
            "memory.relevance_model.unavailable",
            AlertType.MemoryRelevanceModelUnavailable,
            $"Memory relevance (cross-encoder) model '{modelId}' could not be provisioned or loaded: {reason} {consequence}",
            AlertSeverity.Warning,
            source: modelId,
            context: new Dictionary<string, string>
            {
                ["modelId"] = modelId,
                ["reason"] = reason,
                ["consequence"] = consequence,
                ["remediation"] = remediation,
            }));
    }

    private async Task<IRelevanceScorer> LoadRelevanceScorerAsync(string modelId, CancellationToken ct)
    {
        // Keyed under the same ModelsDirectory root as embedding models (NetclawPaths.
        // EmbeddingModelDirectory is already generalized by model id) — a distinct id string is
        // all that's needed to avoid collisions, so no dedicated relevance-model path helper
        // exists.
        var modelDirectory = paths.EmbeddingModelDirectory(modelId);

        ProvisionedRelevanceModel provisioned;
        if (memoryConfig.Embeddings.AutoDownload)
        {
            provisioned = await provisioner.ProvisionRelevanceModelAsync(modelId, relevanceAllowlist, modelDirectory, ct)
                .ConfigureAwait(false);
        }
        else
        {
            provisioned = await provisioner.TryLoadVerifiedRelevanceModelAsync(modelId, relevanceAllowlist, modelDirectory, ct)
                .ConfigureAwait(false)
                ?? throw new InvalidOperationException(
                    $"Relevance model '{modelId}' is not provisioned (or failed hash verification) at " +
                    $"{modelDirectory}, and Memory.Embeddings.AutoDownload is false. Provision it manually " +
                    "or enable AutoDownload, then restart the daemon.");
        }

        var scorer = await OnnxCrossEncoderScorer.LoadAsync(provisioned.ModelPath, provisioned.VocabPath, provisioned.ModelId, ct: ct)
            .ConfigureAwait(false);
        try
        {
            // Warm-up inference (mirrors the embedder's own warm-up call): pays first-call ONNX
            // session / JIT cost here rather than on the first real recall turn.
            await scorer.ScoreAsync("netclaw relevance gate warmup query", ["netclaw relevance gate warmup candidate"], ct)
                .ConfigureAwait(false);

            return scorer;
        }
        catch
        {
            scorer.Dispose();
            throw;
        }
    }

    private async Task<IMemoryEmbedder> LoadEmbedderAsync(string modelId, string queryPrefix, CancellationToken ct)
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
            queryPrefix,
            ct: ct).ConfigureAwait(false);
        try
        {
            // Warm-up inference (design D1/D2): pays first-call ONNX session / JIT cost here rather
            // than on the first real memory write or recall query. Passage purpose: this is a
            // generic session/JIT warm-up, not a real query, so there is nothing gained from also
            // exercising the query-prefix path here (the first real recall turn pays that cost, well
            // inside its own sub-budget per design D2's negligible token-count claim).
            await embedder.EmbedAsync("netclaw embedding warmup", EmbeddingPurpose.Passage, ct).ConfigureAwait(false);

            return embedder;
        }
        catch
        {
            embedder.Dispose();
            throw;
        }
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
                var vectors = await embedder.EmbedBatchAsync(texts, EmbeddingPurpose.Passage, ct).ConfigureAwait(false);
                for (var i = 0; i < batch.Length; i++)
                {
                    var hash = MemoryContentHasher.ComputeHash(batch[i].Title, batch[i].Body);
                    if (await store.UpsertEmbeddingAsync(
                            batch[i].DocumentId, MemoryEmbedOnWriteCoordinator.DocumentItemKind,
                            embedder.ModelId, hash, vectors[i], ct).ConfigureAwait(false))
                    {
                        embedded++;
                    }
                    else
                    {
                        failed++;
                    }
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
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
