using Netclaw.Actors.Memory;
using Netclaw.Configuration;
using Xunit;

namespace Netclaw.Actors.Tests.Memory;

public class SearchMemoriesToolTests : IDisposable
{
    private readonly string _tempDir;
    private readonly FileMemoryStore _store;

    public SearchMemoriesToolTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"netclaw-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
        _store = new FileMemoryStore(_tempDir, TimeProvider.System);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }

    [Fact]
    public async Task Returns_no_memories_when_store_is_empty()
    {
        var tool = new SearchMemoriesTool(_store);

        var result = await tool.ExecuteAsync(
            new Dictionary<string, object?> { ["Query"] = "test query" },
            CancellationToken.None);

        Assert.Equal("No memories found.", result);
    }

    [Fact]
    public void Grant_category_is_builtin()
    {
        var tool = new SearchMemoriesTool(_store);
        Assert.Equal("builtin", tool.GrantCategory);
    }

    [Fact]
    public async Task Returns_matching_memories_after_store()
    {
        await _store.StoreAsync("Akka.NET clustering guide", "Use cluster sharding for entity distribution.", ["reference", "akka"], CancellationToken.None);

        var tool = new SearchMemoriesTool(_store);

        var result = await tool.ExecuteAsync(
            new Dictionary<string, object?> { ["Query"] = "akka clustering" },
            CancellationToken.None);

        Assert.Contains("Akka.NET clustering guide", result);
        Assert.Contains("cluster sharding", result);
    }

    [Fact]
    public void MemoryIndexContextLayer_file_backed_references_skill()
    {
        var layer = new MemoryIndexContextLayer();
        layer.Update(MemoryContextState.FileBacked);

        var content = layer.GetContextLayer();

        Assert.Contains("file-backed", content);
        Assert.Contains("RETRIEVE", content);
        Assert.Contains("SAVE", content);
        Assert.Contains("memory-usage", content);
        Assert.DoesNotContain("NOT AVAILABLE", content);
    }

    [Fact]
    public void MemoryIndexContextLayer_memorizer_connected_references_skills()
    {
        var layer = new MemoryIndexContextLayer();
        layer.Update(MemoryContextState.MemorizerConnected);

        var content = layer.GetContextLayer();

        Assert.Contains("Memorizer connected", content);
        Assert.Contains("RETRIEVE", content);
        Assert.Contains("SAVE", content);
        Assert.Contains("memory-usage", content);
        Assert.Contains("memorizer-usage", content);
        Assert.DoesNotContain("NOT AVAILABLE", content);
    }

    [Fact]
    public void MemoryIndexContextLayer_memorizer_disconnected_shows_troubleshooting()
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
