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
}
