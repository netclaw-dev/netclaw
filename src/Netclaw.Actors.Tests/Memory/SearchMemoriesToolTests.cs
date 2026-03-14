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

        var content = layer.GetContextLayer();

        Assert.Contains("sqlite-backed", content);
        Assert.Contains("automatic", content, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("find_memories", content);
        Assert.Contains("store_memory", content);
        Assert.Contains("manual", content, StringComparison.OrdinalIgnoreCase);
    }
}
