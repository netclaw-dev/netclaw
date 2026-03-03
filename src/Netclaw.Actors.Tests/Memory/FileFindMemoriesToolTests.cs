using Netclaw.Actors.Memory;
using Xunit;

namespace Netclaw.Actors.Tests.Memory;

public class FileFindMemoriesToolTests : IDisposable
{
    private readonly string _tempDir;
    private readonly FileMemoryStore _store;

    public FileFindMemoriesToolTests()
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
        var tool = new FileFindMemoriesTool(_store);

        var result = await tool.ExecuteAsync(
            new Dictionary<string, object?> { ["Query"] = "test query" },
            CancellationToken.None);

        Assert.Equal("No memories found.", result);
    }

    [Fact]
    public void Grant_category_is_builtin()
    {
        var tool = new FileFindMemoriesTool(_store);
        Assert.Equal("builtin", tool.GrantCategory);
    }

    [Fact]
    public async Task Returns_lightweight_results_without_full_content()
    {
        await _store.StoreAsync("Akka.NET clustering guide",
            "Use cluster sharding for entity distribution. This is a long detailed guide about actors.",
            ["reference", "akka"]);

        var tool = new FileFindMemoriesTool(_store);

        var result = await tool.ExecuteAsync(
            new Dictionary<string, object?> { ["Query"] = "akka clustering" },
            CancellationToken.None);

        // Should contain ID, title, score, and snippet
        Assert.Contains("Akka.NET clustering guide", result);
        Assert.Contains("score:", result);
        Assert.Contains("get_memories(", result);
        // Should NOT contain the raw full content block as the old tool did
        Assert.DoesNotContain("━━━", result);
    }

    [Fact]
    public async Task Results_include_memory_id()
    {
        await _store.StoreAsync("Test Entry", "Some content.");

        var tool = new FileFindMemoriesTool(_store);

        var result = await tool.ExecuteAsync(
            new Dictionary<string, object?> { ["Query"] = "test" },
            CancellationToken.None);

        // ID is the filename without extension — should be in square brackets
        Assert.Matches(@"\[.+-test-entry\]", result);
    }

    [Fact]
    public async Task Scores_are_normalized_zero_to_one()
    {
        await _store.StoreAsync("Kubernetes Deployment", "Deploy with kubectl.");

        var tool = new FileFindMemoriesTool(_store);

        var result = await tool.ExecuteAsync(
            new Dictionary<string, object?> { ["Query"] = "kubernetes" },
            CancellationToken.None);

        // Score format is "score: X.XX" — extract and verify range
        var scoreMatch = System.Text.RegularExpressions.Regex.Match(result, @"score: (\d+\.\d+)");
        Assert.True(scoreMatch.Success);
        var score = double.Parse(scoreMatch.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
        Assert.InRange(score, 0.0, 1.0);
    }

    [Fact]
    public void NormalizeScore_caps_at_one()
    {
        // rawScore of 12, single-term query → max possible = 6 → normalized = min(1.0, 12/6) = 1.0
        var score = FileFindMemoriesTool.NormalizeScore(12.0, "test");
        Assert.Equal(1.0, score);
    }

    [Fact]
    public void NormalizeScore_scales_correctly()
    {
        // rawScore of 3, single-term → 3/6 = 0.5
        var score = FileFindMemoriesTool.NormalizeScore(3.0, "test");
        Assert.Equal(0.5, score);
    }

    [Fact]
    public async Task Tag_filter_narrows_results()
    {
        await _store.StoreAsync("Alpha guide", "Content about topic.", ["reference"]);
        await _store.StoreAsync("Beta guide", "Content about topic.", ["how-to"]);

        var tool = new FileFindMemoriesTool(_store);

        var result = await tool.ExecuteAsync(
            new Dictionary<string, object?> { ["Query"] = "topic", ["Tags"] = "how-to" },
            CancellationToken.None);

        Assert.Contains("Beta guide", result);
        Assert.DoesNotContain("Alpha guide", result);
    }
}
