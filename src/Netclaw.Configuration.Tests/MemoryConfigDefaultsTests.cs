// -----------------------------------------------------------------------
// <copyright file="MemoryConfigDefaultsTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Xunit;

namespace Netclaw.Configuration.Tests;

/// <summary>
/// Bear-trap tests for <see cref="MemoryEmbeddingsConfig"/> defaults. If you change a default,
/// you must update these assertions — forcing a deliberate decision rather than an accidental
/// drift.
/// </summary>
public sealed class MemoryConfigDefaultsTests
{
    [Fact]
    public void Embeddings_enabled_by_default()
    {
        var config = new MemoryConfig();
        Assert.True(config.Embeddings.Enabled);
    }

    // int8 default: a dedicated prefixed-query gold-set sweep (arctic-int8-prefix-eval)
    // measured the int8/uint8 quantized artifact as a strict retrieval-quality improvement over
    // the fp32 weights it is quantized from (F0.5, recall@3, and zero-injection accuracy all
    // better, not just smaller/faster) — see EmbeddingModelProvisioner.Allowlist's remarks for
    // the full numbers. fp32 (snowflake-arctic-embed-m) remains allowlisted as an explicit
    // operator choice.
    [Fact]
    public void Embeddings_model_id_defaults_to_snowflake_arctic_embed_m_int8()
    {
        var config = new MemoryConfig();
        Assert.Equal("snowflake-arctic-embed-m-int8", config.Embeddings.ModelId);
    }

    [Fact]
    public void Embeddings_auto_download_defaults_to_true()
    {
        var config = new MemoryConfig();
        Assert.True(config.Embeddings.AutoDownload);
    }

    [Fact]
    public void Memory_subsystem_remains_enabled_by_default()
    {
        var config = new MemoryConfig();
        Assert.True(config.Enabled);
    }

    // ── MemoryCurationConfig (memory-core-redesign Slice 3, task 3.5) ──

    [Fact]
    public void Curation_nominator_similarity_threshold_defaults_to_0_86()
    {
        var config = new MemoryConfig();
        Assert.Equal(0.86, config.Curation.NominatorSimilarityThreshold);
    }

    [Fact]
    public void Curation_nominator_k_defaults_to_5()
    {
        var config = new MemoryConfig();
        Assert.Equal(5, config.Curation.NominatorK);
    }

    [Fact]
    public void Curation_llm_max_output_tokens_defaults_to_4096()
    {
        var config = new MemoryConfig();
        Assert.Equal(4096, config.Curation.LlmMaxOutputTokens);
    }

    [Fact]
    public void Curation_llm_timeout_seconds_defaults_to_60()
    {
        var config = new MemoryConfig();
        Assert.Equal(60, config.Curation.LlmTimeoutSeconds);
    }

    // ── MemoryRecallConfig (memory-core-redesign Slice 4, task 4.5) ──

    [Fact]
    public void Recall_vector_weight_defaults_to_0_7()
    {
        var config = new MemoryConfig();
        Assert.Equal(0.7, config.Recall.VectorWeight);
    }

    [Fact]
    public void Recall_lexical_weight_defaults_to_0_3()
    {
        var config = new MemoryConfig();
        Assert.Equal(0.3, config.Recall.LexicalWeight);
    }

    // memory-query-prefix design D3: the 0.68 non-null default is superseded — the manifest now
    // carries 0.24 for the prefixed arctic encoding, and MinCosineSimilarity defaults to null so
    // the coordinator follows whichever model's manifest calibration is active.
    [Fact]
    public void Recall_min_cosine_similarity_defaults_to_null_and_follows_the_active_models_manifest_calibration()
    {
        var config = new MemoryConfig();
        Assert.Null(config.Recall.MinCosineSimilarity);
    }

    [Fact]
    public void Recall_recency_half_life_days_defaults_to_30()
    {
        var config = new MemoryConfig();
        Assert.Equal(30, config.Recall.RecencyHalfLifeDays);
    }

    // ── MemoryRelevanceGateConfig (memory-relevance-gate, design D6) ────

    [Fact]
    public void RelevanceGate_enabled_defaults_to_null_and_follows_embeddings_enabled()
    {
        var config = new MemoryConfig();
        Assert.Null(config.Recall.RelevanceGate.Enabled);
    }

    [Fact]
    public void RelevanceGate_threshold_defaults_to_null_and_follows_the_manifest_calibrated_value()
    {
        var config = new MemoryConfig();
        Assert.Null(config.Recall.RelevanceGate.Threshold);
    }
}
