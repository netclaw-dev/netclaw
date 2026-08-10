// -----------------------------------------------------------------------
// <copyright file="SearchMemoriesToolTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Configuration;
using Xunit;

namespace Netclaw.Actors.Tests.Memory;

/// <summary>
/// Tests for <see cref="MemoryIndexContextLayer"/> — the dynamic context layer
/// that tells the LLM about available memory tools and usage patterns.
/// </summary>
public class MemoryIndexContextLayerTests
{
    [Fact]
    public void SqlitePrimary_teaches_automatic_recall_with_manual_tools()
    {
        var layer = new MemoryIndexContextLayer();
        layer.Update(MemoryContextState.SqlitePrimary);

        var content = layer.GetContextLayer(TrustAudience.Personal);

        Assert.Contains("sqlite-backed", content);
        Assert.Contains("automatic", content, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("find_memories", content);
        Assert.Contains("store_memory", content);
        Assert.Contains("manual", content, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void GetContextLayer_ReturnsEmptyForPublicAudience()
    {
        var layer = new MemoryIndexContextLayer();
        layer.Update(MemoryContextState.SqlitePrimary);

        var content = layer.GetContextLayer(TrustAudience.Public);

        Assert.Equal(string.Empty, content);
    }

    [Fact]
    public void GetContextLayer_ReturnsEmptyWhenMemoryDisabled()
    {
        var config = new MemoryConfig { Enabled = false };
        var layer = new MemoryIndexContextLayer(config);
        layer.Update(MemoryContextState.SqlitePrimary);

        // Even for Personal audience, disabled config returns empty
        var content = layer.GetContextLayer(TrustAudience.Personal);

        Assert.Equal(string.Empty, content);
    }

    [Fact]
    public void GetContextLayer_ReturnsContentForTeamAudience()
    {
        var layer = new MemoryIndexContextLayer();
        layer.Update(MemoryContextState.SqlitePrimary);

        var content = layer.GetContextLayer(TrustAudience.Team);

        Assert.Contains("sqlite-backed", content);
    }
}
