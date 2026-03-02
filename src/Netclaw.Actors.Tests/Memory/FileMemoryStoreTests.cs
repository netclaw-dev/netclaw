using Netclaw.Actors.Memory;
using Xunit;

namespace Netclaw.Actors.Tests.Memory;

public class FileMemoryStoreTests : IDisposable
{
    private readonly string _tempDir;
    private readonly FakeTimeProvider _timeProvider;
    private readonly FileMemoryStore _store;

    public FileMemoryStoreTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"netclaw-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
        _timeProvider = new FakeTimeProvider(new DateTimeOffset(2026, 3, 2, 12, 0, 0, TimeSpan.Zero));
        _store = new FileMemoryStore(_tempDir, _timeProvider);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }

    [Fact]
    public async Task Store_creates_markdown_file_with_front_matter()
    {
        await _store.StoreAsync("Test Memory", "Some content here.", ["reference", "test"]);

        var files = Directory.GetFiles(_tempDir, "*.md")
            .Where(f => !Path.GetFileName(f).Equals("memory.md", StringComparison.OrdinalIgnoreCase))
            .ToArray();
        Assert.Single(files);

        var content = await File.ReadAllTextAsync(files[0]);
        Assert.StartsWith("---", content);
        Assert.Contains("title: \"Test Memory\"", content);
        Assert.Contains("tags: [reference, test]", content);
        Assert.Contains("created: 2026-03-02T12:00:00Z", content);
        Assert.Contains("Some content here.", content);
    }

    [Fact]
    public async Task Store_rebuilds_index_file()
    {
        await _store.StoreAsync("First Memory", "Content one.");

        var indexPath = Path.Combine(_tempDir, "memory.md");
        Assert.True(File.Exists(indexPath));

        var index = await File.ReadAllTextAsync(indexPath);
        Assert.Contains("# Memory Index", index);
        Assert.Contains("First Memory", index);
        Assert.Contains("Total: 1 memories", index);
    }

    [Fact]
    public async Task Search_finds_by_title()
    {
        await _store.StoreAsync("Akka.NET Best Practices", "Use Ask with timeout.");
        await _store.StoreAsync("Docker Compose Tips", "Use named volumes.");

        var results = await _store.SearchAsync("akka");

        Assert.Single(results);
        Assert.Equal("Akka.NET Best Practices", results[0].Title);
    }

    [Fact]
    public async Task Search_finds_by_tag()
    {
        await _store.StoreAsync("Networking Guide", "Use TCP.", ["networking", "infrastructure"]);
        await _store.StoreAsync("Code Style", "Use var.", ["coding-standard"]);

        var results = await _store.SearchAsync("infrastructure");

        Assert.Single(results);
        Assert.Equal("Networking Guide", results[0].Title);
    }

    [Fact]
    public async Task Search_finds_by_content()
    {
        await _store.StoreAsync("Setup Guide", "Run dotnet restore first.");
        await _store.StoreAsync("Deploy Guide", "Use kubectl apply.");

        var results = await _store.SearchAsync("kubectl");

        Assert.Single(results);
        Assert.Equal("Deploy Guide", results[0].Title);
    }

    [Fact]
    public async Task Search_ranks_title_matches_higher_than_content()
    {
        await _store.StoreAsync("Background Info", "The deployment uses kubernetes.");
        await _store.StoreAsync("Kubernetes Deployment", "Run the apply command.");

        var results = await _store.SearchAsync("kubernetes");

        Assert.Equal(2, results.Count);
        Assert.Equal("Kubernetes Deployment", results[0].Title);
    }

    [Fact]
    public async Task Search_returns_empty_for_no_match()
    {
        await _store.StoreAsync("Some Memory", "Unrelated content.");

        var results = await _store.SearchAsync("nonexistent");

        Assert.Empty(results);
    }

    [Fact]
    public async Task Search_respects_max_results()
    {
        for (var i = 0; i < 10; i++)
            await _store.StoreAsync($"Memory {i}", $"Content about topic {i}.");

        var results = await _store.SearchAsync("topic", maxResults: 3);

        Assert.Equal(3, results.Count);
    }

    [Fact]
    public async Task Index_rebuild_handles_manually_added_files()
    {
        // Simulate a manually created memory file
        var manualPath = Path.Combine(_tempDir, "2026-03-01-manual-note.md");
        await File.WriteAllTextAsync(manualPath, """
            ---
            title: "Manual Note"
            tags: [manual]
            created: 2026-03-01T10:00:00Z
            ---

            This was added by hand.
            """);

        // Force index rebuild via search
        var results = await _store.SearchAsync("manual");

        Assert.Single(results);
        Assert.Equal("Manual Note", results[0].Title);

        // Index should also be updated
        var index = await File.ReadAllTextAsync(Path.Combine(_tempDir, "memory.md"));
        Assert.Contains("Manual Note", index);
    }

    [Fact]
    public async Task Concurrent_writes_are_thread_safe()
    {
        var tasks = Enumerable.Range(0, 10)
            .Select(i => _store.StoreAsync($"Concurrent Memory {i}", $"Content {i}."))
            .ToArray();

        await Task.WhenAll(tasks);

        var entries = await _store.GetEntriesAsync();
        Assert.Equal(10, entries.Count);
    }

    [Fact]
    public void GenerateFileName_produces_kebab_case()
    {
        var date = new DateTimeOffset(2026, 3, 2, 0, 0, 0, TimeSpan.Zero);
        var fileName = FileMemoryStore.GenerateFileName(date, "My Test Memory Title");
        Assert.Equal("2026-03-02-my-test-memory-title.md", fileName);
    }

    [Fact]
    public void GenerateFileName_truncates_long_titles()
    {
        var date = new DateTimeOffset(2026, 3, 2, 0, 0, 0, TimeSpan.Zero);
        var longTitle = new string('a', 200);
        var fileName = FileMemoryStore.GenerateFileName(date, longTitle);
        Assert.True(fileName.Length <= 80); // date prefix + 60 char slug + .md
    }

    private sealed class FakeTimeProvider : TimeProvider
    {
        private DateTimeOffset _utcNow;

        public FakeTimeProvider(DateTimeOffset utcNow) => _utcNow = utcNow;

        public override DateTimeOffset GetUtcNow() => _utcNow;

        public void Advance(TimeSpan delta) => _utcNow += delta;
    }
}
