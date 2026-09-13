// -----------------------------------------------------------------------
// <copyright file="MemoryVectorIndexTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Actors.Memory;
using Xunit;

namespace Netclaw.Actors.Tests.Memory;

public sealed class MemoryVectorIndexTests : IAsyncLifetime
{
    private const string ModelId = "test-model";
    private const int Dimensions = 3;

    private readonly string _baseDir = Path.Combine(Path.GetTempPath(), "netclaw-vector-index-tests", Guid.NewGuid().ToString("N"));
    private SQLiteMemoryStore _store = null!;
    private MemoryVectorIndex _index = null!;

    public async ValueTask InitializeAsync()
    {
        Directory.CreateDirectory(_baseDir);
        _store = new SQLiteMemoryStore(Path.Combine(_baseDir, "netclaw.db"), TimeProvider.System);
        await _store.InitializeAsync(TestContext.Current.CancellationToken);
        _index = new MemoryVectorIndex(_store, ModelId, Dimensions);
    }

    public async ValueTask DisposeAsync() => await SqliteTempDirectoryCleanup.TryDeleteDirectoryAsync(_baseDir);

    private async Task SeedAsync(string itemId, float[] vector)
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var title = $"Title for {itemId}";
        var body = $"Body for {itemId}";
        await _store.UpsertDocumentAsync(new SQLiteMemoryDocument(
            DocumentId: itemId,
            Anchor: _store.CreateDefaultAnchor(itemId),
            MemoryClass: "durable_fact",
            Title: title,
            MarkdownBody: body,
            AliasesJson: null,
            FacetsJson: null,
            SlotsJson: null,
            UpdateSemantics: "merge-document",
            Sensitivity: "normal",
            RecallMode: "auto",
            Confidence: 0.9,
            FreshnessAtMs: now,
            ExpiresAtMs: null,
            CreatedAtMs: now,
            UpdatedAtMs: now), TestContext.Current.CancellationToken);
        await _store.UpsertEmbeddingAsync(
            itemId,
            "document",
            ModelId,
            MemoryContentHasher.ComputeHash(title, body),
            vector,
            TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task TopK_orders_by_descending_cosine_and_applies_the_minCosine_floor()
    {
        await SeedAsync("doc-exact", [1f, 0f, 0f]);
        await SeedAsync("doc-close", [0.95f, 0.05f, 0f]);
        await SeedAsync("doc-orthogonal", [0f, 1f, 0f]);
        await SeedAsync("doc-opposite", [-1f, 0f, 0f]);

        await _index.ReloadIfStaleAsync(TestContext.Current.CancellationToken);

        var results = _index.TopK([1f, 0f, 0f], k: 10, minCosine: 0.5);

        Assert.Equal(["doc-exact", "doc-close"], results.Select(r => r.ItemId));
        Assert.True(results[0].Cosine >= results[1].Cosine);
    }

    [Fact]
    public async Task TopK_limits_results_to_k()
    {
        await SeedAsync("doc-1", [1f, 0f, 0f]);
        await SeedAsync("doc-2", [0.99f, 0.01f, 0f]);
        await SeedAsync("doc-3", [0.98f, 0.02f, 0f]);

        await _index.ReloadIfStaleAsync(TestContext.Current.CancellationToken);

        var results = _index.TopK([1f, 0f, 0f], k: 2, minCosine: -1.0);

        Assert.Equal(2, results.Count);
    }

    [Fact]
    public async Task TopK_returns_empty_before_any_reload()
    {
        await SeedAsync("doc-1", [1f, 0f, 0f]);

        // No ReloadIfStaleAsync call yet — the index has never loaded anything.
        var results = _index.TopK([1f, 0f, 0f], k: 10, minCosine: -1.0);

        Assert.Empty(results);
    }

    [Fact]
    public async Task ReloadIfStaleAsync_is_a_no_op_when_the_store_version_has_not_changed()
    {
        await SeedAsync("doc-1", [1f, 0f, 0f]);

        var firstReload = await _index.ReloadIfStaleAsync(TestContext.Current.CancellationToken);
        var secondReload = await _index.ReloadIfStaleAsync(TestContext.Current.CancellationToken);

        Assert.True(firstReload);
        Assert.False(secondReload);
    }

    [Fact]
    public async Task ReloadIfStaleAsync_picks_up_new_rows_after_a_version_bump()
    {
        await SeedAsync("doc-1", [1f, 0f, 0f]);
        await _index.ReloadIfStaleAsync(TestContext.Current.CancellationToken);
        Assert.Single(_index.TopK([1f, 0f, 0f], k: 10, minCosine: -1.0));

        await SeedAsync("doc-2", [0f, 1f, 0f]);
        var reloaded = await _index.ReloadIfStaleAsync(TestContext.Current.CancellationToken);

        Assert.True(reloaded);
        Assert.Equal(2, _index.Count);
    }

    [Fact]
    public async Task ReloadIfStaleAsync_reflects_deletion_via_document_tombstone()
    {
        var anchor = _store.CreateDefaultAnchor("vector-index-tombstone-test");
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        await _store.UpsertDocumentAsync(new SQLiteMemoryDocument(
            DocumentId: "doc-to-delete",
            Anchor: anchor,
            MemoryClass: "durable_fact",
            Title: "t",
            MarkdownBody: "b",
            AliasesJson: null,
            FacetsJson: null,
            SlotsJson: null,
            UpdateSemantics: "merge-document",
            Sensitivity: "normal",
            RecallMode: "auto",
            Confidence: 0.9,
            FreshnessAtMs: now,
            ExpiresAtMs: null,
            CreatedAtMs: now,
            UpdatedAtMs: now), TestContext.Current.CancellationToken);
        await SeedAsync("doc-to-delete", [1f, 0f, 0f]);
        await _index.ReloadIfStaleAsync(TestContext.Current.CancellationToken);
        Assert.Equal(1, _index.Count);

        await _store.TombstoneDocumentAsync("doc-to-delete", TestContext.Current.CancellationToken);
        await _index.ReloadIfStaleAsync(TestContext.Current.CancellationToken);

        Assert.Equal(0, _index.Count);
    }

    [Fact]
    public async Task TopK_rejects_a_query_of_the_wrong_dimension()
    {
        await SeedAsync("doc-1", [1f, 0f, 0f]);
        await _index.ReloadIfStaleAsync(TestContext.Current.CancellationToken);

        Assert.Throws<ArgumentException>(() => _index.TopK([1f, 0f], k: 5, minCosine: 0.0));
    }
}
