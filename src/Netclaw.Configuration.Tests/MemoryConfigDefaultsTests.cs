// -----------------------------------------------------------------------
// <copyright file="MemoryConfigDefaultsTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Xunit;

namespace Netclaw.Configuration.Tests;

/// <summary>
/// Bear-trap tests for <see cref="MemoryEmbeddingsConfig"/> defaults (memory-core-redesign
/// Slice 2, task 2.11). If you change a default, you must update these assertions — forcing a
/// deliberate decision rather than an accidental drift. <see cref="MemoryEmbeddingsConfig.Enabled"/>
/// defaults to false in particular: flipping it is a deliberate Slice 3/4 decision, not something
/// that should silently change because a refactor touched the property initializer.
/// </summary>
public sealed class MemoryConfigDefaultsTests
{
    [Fact]
    public void Embeddings_disabled_by_default()
    {
        var config = new MemoryConfig();
        Assert.False(config.Embeddings.Enabled);
    }

    [Fact]
    public void Embeddings_model_id_defaults_to_snowflake_arctic_embed_m()
    {
        var config = new MemoryConfig();
        Assert.Equal("snowflake-arctic-embed-m", config.Embeddings.ModelId);
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
    public void Curation_llm_timeout_seconds_defaults_to_10()
    {
        var config = new MemoryConfig();
        Assert.Equal(10, config.Curation.LlmTimeoutSeconds);
    }
}
