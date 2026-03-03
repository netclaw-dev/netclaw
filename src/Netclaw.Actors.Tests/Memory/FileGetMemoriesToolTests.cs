using Netclaw.Actors.Memory;
using Xunit;

namespace Netclaw.Actors.Tests.Memory;

public class FileGetMemoriesToolTests : IDisposable
{
    private readonly string _tempDir;
    private readonly FileMemoryStore _store;

    public FileGetMemoriesToolTests()
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
    public async Task Returns_full_content_for_known_ids()
    {
        await _store.StoreAsync("Alpha Memory", "Full content of alpha memory.", ["reference"]);
        var entries = await _store.GetEntriesAsync();
        var id = entries[0].Id;

        var tool = new FileGetMemoriesTool(_store);

        var result = await tool.ExecuteAsync(
            new Dictionary<string, object?> { ["Ids"] = id },
            CancellationToken.None);

        Assert.Contains("Alpha Memory", result);
        Assert.Contains("Full content of alpha memory.", result);
        Assert.Contains(id, result);
    }

    [Fact]
    public async Task Reports_not_found_ids()
    {
        await _store.StoreAsync("Existing", "Some content.");
        var entries = await _store.GetEntriesAsync();
        var id = entries[0].Id;

        var tool = new FileGetMemoriesTool(_store);

        var result = await tool.ExecuteAsync(
            new Dictionary<string, object?> { ["Ids"] = $"{id}, nonexistent-id" },
            CancellationToken.None);

        Assert.Contains("Existing", result);
        Assert.Contains("Not found: nonexistent-id", result);
    }

    [Fact]
    public async Task Returns_error_for_all_unknown_ids()
    {
        var tool = new FileGetMemoriesTool(_store);

        var result = await tool.ExecuteAsync(
            new Dictionary<string, object?> { ["Ids"] = "unknown-1, unknown-2" },
            CancellationToken.None);

        Assert.Contains("No memories found", result);
    }

    [Fact]
    public void Grant_category_is_builtin()
    {
        var tool = new FileGetMemoriesTool(_store);
        Assert.Equal("builtin", tool.GrantCategory);
    }
}
