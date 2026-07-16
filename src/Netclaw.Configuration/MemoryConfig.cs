// -----------------------------------------------------------------------
// <copyright file="MemoryConfig.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
namespace Netclaw.Configuration;

/// <summary>
/// Configuration for the cross-session memory subsystem.
/// SQLite-backed durable memory settings.
/// </summary>
public sealed class MemoryConfig
{
    /// <summary>
    /// When false, the entire cross-session memory subsystem is disabled.
    /// Tools and automatic recall are not wired up regardless of audience profile.
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Automatic recall timeout budget in milliseconds.
    /// </summary>
    public int RecallTimeoutMs { get; set; } = 300;

    /// <summary>
    /// Maximum number of items injected into the automatic recall bundle.
    /// </summary>
    public int AutoRecallMaxItems { get; set; } = 3;

    /// <summary>
    /// Embedding-based semantic memory settings (memory-core-redesign Slice 2: embedding
    /// foundation). See <see cref="MemoryEmbeddingsConfig.Enabled"/> for why this defaults off.
    /// </summary>
    public MemoryEmbeddingsConfig Embeddings { get; set; } = new();

    /// <summary>
    /// Write-side curation settings (memory-core-redesign Slice 3: nominate→decide +
    /// lossless merge). See <see cref="MemoryCurationConfig"/>.
    /// </summary>
    public MemoryCurationConfig Curation { get; set; } = new();

    /// <summary>
    /// Read-side hybrid recall settings (memory-core-redesign Slice 4: weighted lexical/vector
    /// fusion + absolute cosine floor). See <see cref="MemoryRecallConfig"/>.
    /// </summary>
    public MemoryRecallConfig Recall { get; set; } = new();
}

/// <summary>
/// Configuration for the in-process ONNX embedding runtime (memory-core-redesign D1/D2).
/// </summary>
public sealed class MemoryEmbeddingsConfig
{
    /// <summary>
    /// When true, the daemon provisions/loads the embedding model at startup
    /// (<c>EmbeddingWarmupHostedService</c>) and computes embeddings on memory writes.
    /// Defaults to <b>false</b> for Slice 2 ("embedding foundation"): this slice only writes
    /// vectors — nothing in the write or read path consumes them yet (nominate/decide dedup is
    /// Slice 3, hybrid recall is Slice 4). Flipping this default to <c>true</c> is a deliberate
    /// decision left to whichever of those slices ships first, not an oversight here.
    /// </summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// Allowlisted embedding model id (see <c>EmbeddingModelProvisioner.Allowlist</c> in
    /// <c>Netclaw.Embeddings</c>). An id absent from the allowlist is a configuration error,
    /// surfaced by the doctor check and warmup service — never a silently-accepted arbitrary
    /// model source (supply-chain boundary, design D2).
    ///
    /// <para>
    /// Defaults to the int8/uint8 quantized artifact (<c>snowflake-arctic-embed-m-int8</c>), not
    /// the fp32 <c>snowflake-arctic-embed-m</c> weights it is quantized from. A dedicated
    /// prefixed-query gold-set sweep measured int8 as a strict improvement over fp32 on every
    /// retrieval axis (F0.5, recall@3, zero-injection accuracy — see the allowlist entry's
    /// remarks for the numbers), not merely an acceptable size/latency tradeoff, at ~57% less
    /// steady-state RSS and ~1.7x the inference speed. fp32 and <c>mxbai-embed-large-v1</c> stay
    /// allowlisted as explicit operator choices. An existing install with fp32 vectors already
    /// stored self-heals on upgrade: the daemon's gap-repair sweep (<c>EmbeddingWarmupHostedService</c>)
    /// is scoped to the active model id, so it re-embeds the whole corpus under the new id
    /// automatically, and <c>netclaw doctor</c> surfaces the interim mixed-model state as a
    /// warning recommending <c>netclaw memory backfill-embeddings --force</c>.
    /// </para>
    /// </summary>
    public string ModelId { get; set; } = "snowflake-arctic-embed-m-int8";

    /// <summary>
    /// When true, the daemon downloads the model artifact at startup if not already
    /// provisioned. When false, a missing or invalid model is a loud degraded-mode condition
    /// (doctor error, daemon status <c>embeddings: degraded</c>) rather than a silent network
    /// fetch — operators can pre-provision the model file (or run
    /// <c>netclaw memory backfill-embeddings</c> after manually placing it) to stay fully
    /// offline.
    /// </summary>
    public bool AutoDownload { get; set; } = true;
}

/// <summary>
/// Configuration for write-side curation: the embedding kNN nominator and the curation LLM
/// call (memory-core-redesign Slice 3, design D4/D5).
/// </summary>
public sealed class MemoryCurationConfig
{
    /// <summary>
    /// Embedding cosine similarity threshold above which an existing memory is nominated as a
    /// dedup candidate, forcing the curator LLM to adjudicate the relationship (design D4: "no
    /// cosine threshold separates duplicates from siblings," so similarity only nominates —
    /// it never auto-merges or auto-skips). Consumed by
    /// <see cref="Netclaw.Actors.Memory.MemoryCurationEvaluator"/>'s embedding kNN nominator
    /// (memory-core-redesign Slice 3 Stage B, task 3.1) via
    /// <c>Netclaw.Actors.Memory.MemoryVectorIndex.TopK</c>.
    /// </summary>
    public double NominatorSimilarityThreshold { get; set; } = 0.86;

    /// <summary>
    /// Maximum number of nearest-neighbor nominees the kNN nominator shortlists per proposal.
    /// See <see cref="NominatorSimilarityThreshold"/>'s remarks — same Slice 3 Stage B consumer.
    /// </summary>
    public int NominatorK { get; set; } = 5;

    /// <summary>
    /// Maximum output tokens for the curation LLM call
    /// (<see cref="Netclaw.Actors.Memory.MemoryCurationEvaluator"/>'s
    /// <c>TryLlmEvaluationAsync</c>). Sized generously by default: the token cap is the third
    /// line of defense against a truncated reply (after reasoning suppression and the call
    /// timeout below), so it must never be the binding constraint — the July 2026 audit found
    /// a 512-token cap produced zero successful curation decisions ever, because a
    /// reasoning-capable model was truncated mid-think before emitting its answer. Raising
    /// this further is nearly free (unemitted tokens cost nothing); lowering it below what a
    /// verbose merged body needs risks reproducing that failure with the new merged-body
    /// protocol (task 3.2).
    /// </summary>
    public int LlmMaxOutputTokens { get; set; } = 4096;

    /// <summary>
    /// Wall-clock timeout, in seconds, for the curation LLM call. Bounds latency when a model
    /// ignores reasoning suppression and thinks at length regardless of the token cap above.
    /// Curation is background quality work — success matters far more than latency — so this is
    /// sized to let the 4096-token <see cref="LlmMaxOutputTokens"/> ceiling actually be reached on
    /// real providers rather than to bound perceived latency. The July 2026 canary
    /// (0.25.0-alpha.onnx.7) measured a 46% curation LLM failure rate (11/24 over 14 days), 100%
    /// attributable to <c>curation_llm_timeout</c> at the previous 10-second default — zero parse
    /// errors or exceptions among the failures — because generating a full merged-body reply
    /// routinely took longer than that.
    /// </summary>
    public int LlmTimeoutSeconds { get; set; } = 60;
}

/// <summary>
/// Configuration for read-side hybrid recall: weighted lexical/vector fusion and the absolute
/// cosine floor (memory-core-redesign Slice 4, design D6). Consumed by
/// <see cref="Netclaw.Actors.Sessions.SQLiteMemoryRecallCoordinator"/>. Every property is
/// defaulted, so no operator configuration is required once
/// <see cref="MemoryEmbeddingsConfig.Enabled"/> is also on — a turn with no query vector
/// available (embedder unavailable, over its sub-budget, or embeddings disabled) degrades to
/// the pre-Slice-4 lexical-only composite floor unchanged, regardless of these values.
/// </summary>
public sealed class MemoryRecallConfig
{
    /// <summary>
    /// Weight applied to a candidate's cosine similarity in the hybrid fusion score
    /// (<c>fused = VectorWeight*cosine + LexicalWeight*squash(selectorScore) + classPrior</c>,
    /// then recency-decayed). Only used in hybrid mode (a query vector was produced); ignored by
    /// the lexical-only degraded path.
    /// </summary>
    public double VectorWeight { get; set; } = 0.7;

    /// <summary>
    /// Weight applied to a candidate's squashed lexical selector score in the hybrid fusion
    /// score. See <see cref="VectorWeight"/> for the full formula.
    /// </summary>
    public double LexicalWeight { get; set; } = 0.3;

    /// <summary>
    /// Absolute relevance floor (design D6, recalibrated by memory-query-prefix design D3/D4):
    /// when a query vector is available, any candidate — vector- or lexical-sourced — whose
    /// cosine similarity to the query falls below this value is dropped before ranking,
    /// regardless of fused score. Nothing surviving means nothing is injected and the
    /// <c>[memory-recall]</c> block is omitted entirely — a healthy empty result, not a degraded
    /// one.
    ///
    /// <para>
    /// <c>null</c> (default) — the effective floor follows the active embedding model's
    /// manifest-carried <c>CalibratedMinCosineSimilarity</c>
    /// (<c>Netclaw.Embeddings.EmbeddingModelManifestEntry</c>; 0.24 for the shipped default
    /// <c>snowflake-arctic-embed-m-int8</c> prefixed encoding, and also 0.24 for the fp32
    /// <c>snowflake-arctic-embed-m</c> prefixed encoding it was calibrated independently
    /// against — see the memory-query-prefix design doc and the int8 default-model calibration
    /// for the full gold-set sweeps). A concrete value is an explicit operator override,
    /// independent of which model is active.
    /// </para>
    ///
    /// <para>
    /// <b>The numeric meaning of this value is model- and encoding-specific.</b> It is NOT a
    /// portable "relevance percentage" — cosine distributions differ across models and shift
    /// materially when a model's documented query prefix is adopted or removed (measured: 0.68
    /// with no prefix vs. 0.24 with the prefix, for the SAME model). A value pinned for one
    /// model/encoding and silently carried into another combination can measure catastrophically
    /// wrong (F0.5 = 0.0 was measured for the prefixed encoding at the old no-prefix floor). Only
    /// set this explicitly after re-running the calibration-verification procedure
    /// (memory-relevance-gate design doc) against the model and encoding actually active.
    /// </para>
    /// </summary>
    public double? MinCosineSimilarity { get; set; }

    /// <summary>
    /// Half-life, in days, for the recency-decay multiplier applied to a candidate's fused score
    /// in hybrid mode (<c>0.85 + 0.15 * 2^(-ageDays/RecencyHalfLifeDays)</c>). Floor-bounded at
    /// 0.85 by construction (the decay term is always in (0, 1] for non-negative age), so an
    /// old-but-otherwise-strong match is downweighted only enough to break ties toward fresher
    /// knowledge, never zeroed by age alone. Age is measured from the item's
    /// <c>updated_at</c> timestamp against <see cref="TimeProvider.GetUtcNow"/>.
    /// </summary>
    public double RecencyHalfLifeDays { get; set; } = 30;

    /// <summary>
    /// Post-floor cross-encoder relevance gate settings (memory-relevance-gate, design D6). See
    /// <see cref="MemoryRelevanceGateConfig"/>.
    /// </summary>
    public MemoryRelevanceGateConfig RelevanceGate { get; set; } = new();
}

/// <summary>
/// Configuration for the post-floor relevance gate (memory-relevance-gate D6): a tiny
/// cross-encoder scores each floor-surviving candidate jointly against the query and drops
/// anything below the active threshold. Both properties are genuinely-optional nullables (not a
/// backward-compatibility shim) — their absence is a real, intended runtime state: "follow
/// whatever the embeddings switch / the model's calibrated manifest value already say," so an
/// operator who only wants "on/off" never has to discover or set a second knob.
/// </summary>
public sealed class MemoryRelevanceGateConfig
{
    /// <summary>
    /// <c>null</c> (default) — the gate follows <see cref="MemoryEmbeddingsConfig.Enabled"/>:
    /// an operator who turns on embeddings gets the gate with no second switch to flip.
    /// <c>true</c>/<c>false</c> — explicit override, independent of the embeddings switch (e.g.
    /// an operator who wants embeddings for dedup/hybrid-recall but not the extra per-turn
    /// cross-encoder latency).
    /// </summary>
    public bool? Enabled { get; set; }

    /// <summary>
    /// <c>null</c> (default) — the active threshold follows the provisioned relevance model's
    /// manifest-carried <c>CalibratedThreshold</c> (<c>RelevanceModelManifestEntry</c> in
    /// <c>Netclaw.Embeddings</c>; S*=0.02 for the shipped <c>ms-marco-minilm-l-6-v2</c>) — the
    /// same "config default, manifest provides the calibrated number" relationship
    /// <see cref="MemoryRecallConfig.MinCosineSimilarity"/> already established. A concrete
    /// value is an explicit operator override, e.g. after re-running the threshold-sweep
    /// protocol against a different corpus or relevance model.
    /// </summary>
    public double? Threshold { get; set; }
}
