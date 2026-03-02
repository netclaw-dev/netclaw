using Netclaw.Actors.Memory;
using Xunit;

namespace Netclaw.Actors.Tests.Memory;

public class FileMemoryExtractorTests : IDisposable
{
    private readonly string _tempDir;
    private readonly FileMemoryStore _store;

    public FileMemoryExtractorTests()
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
    public async Task PersistAsync_stores_with_session_title_and_extraction_tags()
    {
        var extractor = new FileMemoryExtractor(_store);

        await extractor.PersistAsync("chan123/ts456", "Key finding: the API rate limit is 100 req/min.");

        var entries = await _store.GetEntriesAsync();
        Assert.Single(entries);
        Assert.Equal("Session extraction — chan123/ts456", entries[0].Title);
        Assert.Contains("extraction", entries[0].Tags);
        Assert.Contains("compaction", entries[0].Tags);
        Assert.Contains("rate limit", entries[0].Content);
    }

    [Fact]
    public async Task PersistAsync_skips_empty_content()
    {
        var extractor = new FileMemoryExtractor(_store);

        await extractor.PersistAsync("session1", "");
        await extractor.PersistAsync("session2", "   ");

        var entries = await _store.GetEntriesAsync();
        Assert.Empty(entries);
    }
}
