// -----------------------------------------------------------------------
// <copyright file="ContextLayerAudienceTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Xunit;

namespace Netclaw.Configuration.Tests;

/// <summary>
/// Tests that context layers respect audience gating and config-level Enabled flags.
/// Each layer must return empty for Public audience, and must return empty for ALL
/// audiences when its config section has Enabled = false.
/// </summary>
public sealed class ContextLayerAudienceTests
{
    // ── SkillIndexContextLayer ──

    [Fact]
    public void SkillIndex_Public_ReturnsEmpty()
    {
        var layer = new SkillIndexContextLayer();
        layer.Update("skill-menu-content");

        Assert.Equal(string.Empty, layer.GetContextLayer(TrustAudience.Public));
    }

    [Theory]
    [InlineData(TrustAudience.Personal)]
    [InlineData(TrustAudience.Team)]
    public void SkillIndex_NonPublic_ReturnsContent_WhenRegistered(TrustAudience audience)
    {
        var layer = new SkillIndexContextLayer();
        layer.Update("Available skills:\n- netclaw-memory");

        var result = layer.GetContextLayer(audience);

        Assert.NotEmpty(result);
    }

    [Fact]
    public void SkillIndex_Disabled_ReturnsEmpty_ForAllAudiences()
    {
        var config = new SkillSyncConfig { Enabled = false };
        var layer = new SkillIndexContextLayer(config);
        layer.Update("skill-menu-content");

        Assert.Equal(string.Empty, layer.GetContextLayer(TrustAudience.Personal));
        Assert.Equal(string.Empty, layer.GetContextLayer(TrustAudience.Team));
        Assert.Equal(string.Empty, layer.GetContextLayer(TrustAudience.Public));
    }

    // ── MemoryIndexContextLayer ──

    [Fact]
    public void MemoryIndex_Public_ReturnsEmpty()
    {
        var layer = new MemoryIndexContextLayer();
        layer.Update(MemoryContextState.SqlitePrimary);

        Assert.Equal(string.Empty, layer.GetContextLayer(TrustAudience.Public));
    }

    [Theory]
    [InlineData(TrustAudience.Personal)]
    [InlineData(TrustAudience.Team)]
    public void MemoryIndex_NonPublic_ReturnsContent_WhenStateSet(TrustAudience audience)
    {
        var layer = new MemoryIndexContextLayer();
        layer.Update(MemoryContextState.SqlitePrimary);

        var result = layer.GetContextLayer(audience);

        Assert.NotEmpty(result);
    }

    [Fact]
    public void MemoryIndex_Disabled_ReturnsEmpty_ForAllAudiences()
    {
        var config = new MemoryConfig { Enabled = false };
        var layer = new MemoryIndexContextLayer(config);
        layer.Update(MemoryContextState.SqlitePrimary);

        Assert.Equal(string.Empty, layer.GetContextLayer(TrustAudience.Personal));
        Assert.Equal(string.Empty, layer.GetContextLayer(TrustAudience.Team));
        Assert.Equal(string.Empty, layer.GetContextLayer(TrustAudience.Public));
    }

    // ── SubAgentDiscoveryContextLayer ──

    [Fact]
    public void SubAgentDiscovery_Public_ReturnsEmpty()
    {
        var layer = new SubAgentDiscoveryContextLayer();
        layer.Update("subagent-index-content");

        Assert.Equal(string.Empty, layer.GetContextLayer(TrustAudience.Public));
    }

    [Theory]
    [InlineData(TrustAudience.Personal)]
    [InlineData(TrustAudience.Team)]
    public void SubAgentDiscovery_NonPublic_ReturnsContent_WhenRegistered(TrustAudience audience)
    {
        var layer = new SubAgentDiscoveryContextLayer();
        layer.Update("Available agents:\n- curation-agent");

        var result = layer.GetContextLayer(audience);

        Assert.NotEmpty(result);
    }

    [Fact]
    public void SubAgentDiscovery_Disabled_ReturnsEmpty_ForAllAudiences()
    {
        var config = new SubAgentConfig { Enabled = false };
        var layer = new SubAgentDiscoveryContextLayer(config);
        layer.Update("subagent-index-content");

        Assert.Equal(string.Empty, layer.GetContextLayer(TrustAudience.Personal));
        Assert.Equal(string.Empty, layer.GetContextLayer(TrustAudience.Team));
        Assert.Equal(string.Empty, layer.GetContextLayer(TrustAudience.Public));
    }
}
