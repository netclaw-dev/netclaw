using Netclaw.Actors.Memory;
using Xunit;

namespace Netclaw.Actors.Tests.Memory;

public class StoreMemoryToolTests : IDisposable
{
    private readonly string _tempDir;
    private readonly FileMemoryStore _store;

    public StoreMemoryToolTests()
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
    public void Grant_category_is_builtin()
    {
        var tool = new StoreMemoryTool(_store);
        Assert.Equal("builtin", tool.GrantCategory);
    }

    [Fact]
    public async Task Stores_memory_and_returns_confirmation()
    {
        var tool = new StoreMemoryTool(_store);

        var result = await tool.ExecuteAsync(
            new Dictionary<string, object?>
            {
                ["Title"] = "Test Memory",
                ["Content"] = "Some test content"
            },
            CancellationToken.None);

        Assert.Contains("Memory saved", result);
        Assert.Contains("Test Memory", result);

        // Verify file was created
        var entries = await _store.GetEntriesAsync();
        Assert.Single(entries);
        Assert.Equal("Test Memory", entries[0].Title);
    }

    [Fact]
    public async Task Parses_comma_separated_tags()
    {
        var tool = new StoreMemoryTool(_store);

        await tool.ExecuteAsync(
            new Dictionary<string, object?>
            {
                ["Title"] = "Tagged Memory",
                ["Content"] = "Content",
                ["Tags"] = "reference, how-to, akka"
            },
            CancellationToken.None);

        var entries = await _store.GetEntriesAsync();
        Assert.Single(entries);
        Assert.Contains("reference", entries[0].Tags);
        Assert.Contains("how-to", entries[0].Tags);
        Assert.Contains("akka", entries[0].Tags);
    }

    [Fact]
    public async Task Handles_null_tags()
    {
        var tool = new StoreMemoryTool(_store);

        var result = await tool.ExecuteAsync(
            new Dictionary<string, object?>
            {
                ["Title"] = "No Tags Memory",
                ["Content"] = "Content with no tags"
            },
            CancellationToken.None);

        Assert.Contains("Memory saved", result);

        var entries = await _store.GetEntriesAsync();
        Assert.Single(entries);
        Assert.Empty(entries[0].Tags);
    }
}
