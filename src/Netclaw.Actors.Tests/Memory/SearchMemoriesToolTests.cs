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
    public void FileBacked_references_four_tools_and_two_phase_pattern()
    {
        var layer = new MemoryIndexContextLayer();
        layer.Update(MemoryContextState.FileBacked);

        var content = layer.GetContextLayer();

        Assert.Contains("file-backed", content);
        Assert.Contains("find_memories", content);
        Assert.Contains("get_memories", content);
        Assert.Contains("store_memory", content);
        Assert.Contains("update_memory", content);
        Assert.Contains("Two-Phase Retrieval", content);
        Assert.Contains("memory-usage", content);
        Assert.DoesNotContain("NOT AVAILABLE", content);
    }

    [Fact]
    public void SqlitePrimary_teaches_automatic_recall_with_manual_tools()
    {
        var layer = new MemoryIndexContextLayer();
        layer.Update(MemoryContextState.SqlitePrimary);

        var content = layer.GetContextLayer();

        Assert.Contains("sqlite-backed", content);
        Assert.Contains("automatic", content, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("find_memories", content);
        Assert.Contains("store_memory", content);
        Assert.Contains("manual", content, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void FileBacked_includes_quality_guidance()
    {
        var layer = new MemoryIndexContextLayer();
        layer.Update(MemoryContextState.FileBacked);

        var content = layer.GetContextLayer();

        // Quality bar examples
        Assert.Contains("BAD title", content);
        Assert.Contains("GOOD", content);
        Assert.Contains("WHY", content);
        Assert.Contains("markdown", content);
    }

    [Fact]
    public void MemorizerConnected_references_four_tools_and_subagent_latency()
    {
        var layer = new MemoryIndexContextLayer();
        layer.Update(MemoryContextState.MemorizerConnected);

        var content = layer.GetContextLayer();

        Assert.Contains("Memorizer connected", content);
        Assert.Contains("find_memories", content);
        Assert.Contains("get_memories", content);
        Assert.Contains("store_memory", content);
        Assert.Contains("update_memory", content);
        Assert.Contains("Two-Phase Retrieval", content);
        Assert.Contains("curation subagent", content);
        Assert.Contains("memory-usage", content);
        Assert.Contains("memorizer-usage", content);
        Assert.DoesNotContain("NOT AVAILABLE", content);
    }

    [Fact]
    public void MemorizerConnected_includes_quality_guidance()
    {
        var layer = new MemoryIndexContextLayer();
        layer.Update(MemoryContextState.MemorizerConnected);

        var content = layer.GetContextLayer();

        Assert.Contains("BAD title", content);
        Assert.Contains("GOOD", content);
        Assert.Contains("WHY", content);
    }

    [Fact]
    public void MemorizerDisconnected_shows_troubleshooting()
    {
        var layer = new MemoryIndexContextLayer();
        layer.Update(MemoryContextState.MemorizerDisconnected);

        var content = layer.GetContextLayer();

        Assert.Contains("NOT AVAILABLE", content);
        Assert.Contains("not connected", content);
        Assert.Contains("McpServers", content);
        Assert.Contains("identity-management", content);
    }
}
