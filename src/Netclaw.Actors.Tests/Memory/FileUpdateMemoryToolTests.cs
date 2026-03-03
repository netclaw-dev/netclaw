using Netclaw.Actors.Memory;
using Xunit;

namespace Netclaw.Actors.Tests.Memory;

public class FileUpdateMemoryToolTests : IDisposable
{
    private readonly string _tempDir;
    private readonly FileMemoryStore _store;

    public FileUpdateMemoryToolTests()
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
    public async Task Edit_replaces_text_in_memory()
    {
        await _store.StoreAsync("Test Entry", "The old value is here.");
        var entries = await _store.GetEntriesAsync();
        var id = entries[0].Id;

        var tool = new FileUpdateMemoryTool(_store);

        var result = await tool.ExecuteAsync(
            new Dictionary<string, object?>
            {
                ["Id"] = id,
                ["OldText"] = "old value",
                ["NewText"] = "new value"
            },
            CancellationToken.None);

        Assert.Contains("updated", result);

        // Verify the edit took effect
        var search = await _store.SearchAsync("new value");
        Assert.Single(search);
    }

    [Fact]
    public async Task Edit_fails_when_old_text_not_found()
    {
        await _store.StoreAsync("Test Entry", "Some content.");
        var entries = await _store.GetEntriesAsync();
        var id = entries[0].Id;

        var tool = new FileUpdateMemoryTool(_store);

        var result = await tool.ExecuteAsync(
            new Dictionary<string, object?>
            {
                ["Id"] = id,
                ["OldText"] = "nonexistent text",
                ["NewText"] = "replacement"
            },
            CancellationToken.None);

        Assert.Contains("not found", result);
    }

    [Fact]
    public async Task Delete_removes_memory()
    {
        await _store.StoreAsync("Delete Me", "Content to delete.");
        var entries = await _store.GetEntriesAsync();
        var id = entries[0].Id;

        var tool = new FileUpdateMemoryTool(_store);

        var result = await tool.ExecuteAsync(
            new Dictionary<string, object?>
            {
                ["Id"] = id,
                ["Delete"] = "true"
            },
            CancellationToken.None);

        Assert.Contains("deleted", result);

        var search = await _store.SearchAsync("delete");
        Assert.Empty(search);
    }

    [Fact]
    public async Task Returns_error_when_missing_params()
    {
        var tool = new FileUpdateMemoryTool(_store);

        var result = await tool.ExecuteAsync(
            new Dictionary<string, object?>
            {
                ["Id"] = "some-id"
            },
            CancellationToken.None);

        Assert.Contains("Error", result);
    }

    [Fact]
    public void Grant_category_is_builtin()
    {
        var tool = new FileUpdateMemoryTool(_store);
        Assert.Equal("builtin", tool.GrantCategory);
    }
}
