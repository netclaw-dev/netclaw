using Netclaw.Actors.Memory;
using Xunit;

namespace Netclaw.Actors.Tests.Memory;

/// <summary>
/// End-to-end integration test exercising the full 4-tool file-backed memory cycle
/// through real <see cref="FileMemoryStore"/>. Tests the tool wrappers (parameter
/// parsing, result formatting) over real files.
/// </summary>
public sealed class FileMemoryToolIntegrationTests : IDisposable
{
    private readonly string _tempDir;
    private readonly FileMemoryStore _store;

    public FileMemoryToolIntegrationTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"netclaw-integ-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
        _store = new FileMemoryStore(_tempDir, TimeProvider.System);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }

    [Fact]
    public async Task FullCycle_Store_Find_Get_Update_Delete()
    {
        var storeTool = new StoreMemoryTool(_store);
        var findTool = new FileFindMemoriesTool(_store);
        var getTool = new FileGetMemoriesTool(_store);
        var updateTool = new FileUpdateMemoryTool(_store);

        // 1. Store a memory
        var storeResult = await storeTool.ExecuteAsync(
            new Dictionary<string, object?>
            {
                ["Title"] = "PostgreSQL Connection Pooling",
                ["Content"] = "Use Npgsql 8.x with multiplexing enabled for best throughput.",
                ["Tags"] = "reference, database"
            });

        Assert.Contains("Memory saved", storeResult);

        // 2. Verify index file exists and contains the title
        var indexPath = Path.Combine(_tempDir, "memory.md");
        Assert.True(File.Exists(indexPath));
        var indexContent = await File.ReadAllTextAsync(indexPath);
        Assert.Contains("PostgreSQL Connection Pooling", indexContent);

        // 3. Find the memory
        var findResult = await findTool.ExecuteAsync(
            new Dictionary<string, object?>
            {
                ["Query"] = "postgresql pooling"
            });

        Assert.Contains("PostgreSQL Connection Pooling", findResult);
        Assert.DoesNotContain("No memories found", findResult);

        // 4. Extract ID from find result (format: [id] title (score: ...))
        var idStart = findResult.IndexOf('[') + 1;
        var idEnd = findResult.IndexOf(']');
        var memoryId = findResult[idStart..idEnd];
        Assert.False(string.IsNullOrWhiteSpace(memoryId));

        // 5. Get full content by ID
        var getResult = await getTool.ExecuteAsync(
            new Dictionary<string, object?>
            {
                ["Ids"] = memoryId
            });

        Assert.Contains("PostgreSQL Connection Pooling", getResult);
        Assert.Contains("Npgsql 8.x", getResult);
        Assert.Contains("multiplexing enabled", getResult);

        // 6. Edit the memory (find-and-replace)
        var editResult = await updateTool.ExecuteAsync(
            new Dictionary<string, object?>
            {
                ["Id"] = memoryId,
                ["OldText"] = "multiplexing enabled",
                ["NewText"] = "multiplexing disabled"
            });

        Assert.Contains("updated", editResult);

        // 7. Verify edit via get
        var getAfterEdit = await getTool.ExecuteAsync(
            new Dictionary<string, object?>
            {
                ["Ids"] = memoryId
            });

        Assert.Contains("multiplexing disabled", getAfterEdit);
        Assert.DoesNotContain("multiplexing enabled", getAfterEdit);

        // 8. Delete the memory
        var deleteResult = await updateTool.ExecuteAsync(
            new Dictionary<string, object?>
            {
                ["Id"] = memoryId,
                ["Delete"] = "true"
            });

        Assert.Contains("deleted", deleteResult);

        // 9. Verify deleted — find returns no results
        var findAfterDelete = await findTool.ExecuteAsync(
            new Dictionary<string, object?>
            {
                ["Query"] = "postgresql pooling"
            });

        Assert.Contains("No memories found", findAfterDelete);
    }
}
