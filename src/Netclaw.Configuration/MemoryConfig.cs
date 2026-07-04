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
    /// </summary>
    public string ModelId { get; set; } = "snowflake-arctic-embed-m";

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
