// -----------------------------------------------------------------------
// <copyright file="SQLiteMemoryStoreEmbeddingTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Actors.Memory;
using Xunit;

namespace Netclaw.Actors.Tests.Memory;

/// <summary>
/// Covers the <c>memory_embeddings</c> table added in memory-core-redesign Slice 2:
/// upsert/coverage/hash-skip round-trips, deletion via document tombstone, and
/// <see cref="SQLiteMemoryStore.EmbeddingDataVersion"/> bump semantics.
/// </summary>
public sealed class SQLiteMemoryStoreEmbeddingTests : IAsyncLifetime
{
    private readonly string _baseDir = Path.Combine(Path.GetTempPath(), "netclaw-sqlite-embedding-tests", Guid.NewGuid().ToString("N"));
    private readonly string _dbPath;
    private readonly SQLiteMemoryStore _store;

    public SQLiteMemoryStoreEmbeddingTests()
    {
        Directory.CreateDirectory(_baseDir);
        _dbPath = Path.Combine(_baseDir, "netclaw.db");
        _store = new SQLiteMemoryStore(_dbPath, TimeProvider.System);
    }

    public async ValueTask InitializeAsync() => await _store.InitializeAsync(TestContext.Current.CancellationToken);

    public async ValueTask DisposeAsync() => await SqliteTempDirectoryCleanup.TryDeleteDirectoryAsync(_baseDir);

    [Fact]
    public async Task UpsertEmbeddingAsync_round_trips_the_vector()
    {
        float[] vector = [0.1f, 0.2f, 0.3f, 0.4f];

        await _store.UpsertEmbeddingAsync("doc-1", "document", "model-a", "hash-1", vector, TestContext.Current.CancellationToken);

        var rows = await _store.GetEmbeddingsForModelAsync("model-a", TestContext.Current.CancellationToken);

        var row = Assert.Single(rows);
        Assert.Equal("doc-1", row.ItemId);
        Assert.Equal("document", row.ItemKind);
        Assert.Equal(vector, row.Vector.ToArray());
    }

    [Fact]
    public async Task UpsertEmbeddingAsync_with_unchanged_hash_is_a_no_op_and_does_not_bump_the_version()
    {
        float[] vector = [1f, 2f, 3f];
        await _store.UpsertEmbeddingAsync("doc-1", "document", "model-a", "hash-1", vector, TestContext.Current.CancellationToken);
        var versionAfterFirstWrite = _store.EmbeddingDataVersion;

        // Same hash, even with a different (bogus) vector — must be skipped entirely: the
        // stored vector is untouched and the version counter does not move.
        await _store.UpsertEmbeddingAsync("doc-1", "document", "model-a", "hash-1", new float[] { 9f, 9f, 9f }, TestContext.Current.CancellationToken);

        var rows = await _store.GetEmbeddingsForModelAsync("model-a", TestContext.Current.CancellationToken);
        Assert.Equal(vector, Assert.Single(rows).Vector.ToArray());
        Assert.Equal(versionAfterFirstWrite, _store.EmbeddingDataVersion);
    }

    [Fact]
    public async Task UpsertEmbeddingAsync_with_changed_hash_overwrites_and_bumps_the_version()
    {
        await _store.UpsertEmbeddingAsync("doc-1", "document", "model-a", "hash-1", new float[] { 1f, 2f, 3f }, TestContext.Current.CancellationToken);
        var versionAfterFirstWrite = _store.EmbeddingDataVersion;

        await _store.UpsertEmbeddingAsync("doc-1", "document", "model-a", "hash-2", new float[] { 4f, 5f, 6f }, TestContext.Current.CancellationToken);

        var rows = await _store.GetEmbeddingsForModelAsync("model-a", TestContext.Current.CancellationToken);
        Assert.Equal(new float[] { 4f, 5f, 6f }, Assert.Single(rows).Vector.ToArray());
        Assert.True(_store.EmbeddingDataVersion > versionAfterFirstWrite);
    }

    [Fact]
    public async Task UpsertEmbeddingAsync_keys_rows_by_item_and_model_independently()
    {
        await _store.UpsertEmbeddingAsync("doc-1", "document", "model-a", "hash-1", new float[] { 1f }, TestContext.Current.CancellationToken);
        await _store.UpsertEmbeddingAsync("doc-1", "document", "model-b", "hash-1", new float[] { 2f }, TestContext.Current.CancellationToken);

        var modelARows = await _store.GetEmbeddingsForModelAsync("model-a", TestContext.Current.CancellationToken);
        var modelBRows = await _store.GetEmbeddingsForModelAsync("model-b", TestContext.Current.CancellationToken);

        Assert.Equal(new float[] { 1f }, Assert.Single(modelARows).Vector.ToArray());
        Assert.Equal(new float[] { 2f }, Assert.Single(modelBRows).Vector.ToArray());
    }

    [Fact]
    public async Task TombstoneDocumentAsync_deletes_the_document_embedding_and_bumps_the_version()
    {
        var anchor = _store.CreateDefaultAnchor("embedding-tombstone-test");
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        await _store.UpsertDocumentAsync(new SQLiteMemoryDocument(
            DocumentId: "doc-1",
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
        await _store.UpsertEmbeddingAsync("doc-1", "document", "model-a", "hash-1", new float[] { 1f, 2f }, TestContext.Current.CancellationToken);
        var versionBeforeTombstone = _store.EmbeddingDataVersion;

        var tombstoned = await _store.TombstoneDocumentAsync("doc-1", TestContext.Current.CancellationToken);

        Assert.True(tombstoned);
        Assert.Empty(await _store.GetEmbeddingsForModelAsync("model-a", TestContext.Current.CancellationToken));
        Assert.True(_store.EmbeddingDataVersion > versionBeforeTombstone);
    }

    [Fact]
    public async Task TombstoneDocumentAsync_with_no_embedding_row_does_not_bump_the_version()
    {
        var anchor = _store.CreateDefaultAnchor("no-embedding-tombstone-test");
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        await _store.UpsertDocumentAsync(new SQLiteMemoryDocument(
            DocumentId: "doc-no-embedding",
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
        var versionBefore = _store.EmbeddingDataVersion;

        var tombstoned = await _store.TombstoneDocumentAsync("doc-no-embedding", TestContext.Current.CancellationToken);

        Assert.True(tombstoned);
        Assert.Equal(versionBefore, _store.EmbeddingDataVersion);
    }

    [Fact]
    public async Task GetEmbeddingCoverageAsync_reports_total_current_and_other_model_counts()
    {
        var anchor = _store.CreateDefaultAnchor("coverage-test");
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        async Task SeedDocAsync(string id, string title, string body)
        {
            await _store.UpsertDocumentAsync(new SQLiteMemoryDocument(
                DocumentId: id,
                Anchor: anchor,
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
        }

        // doc-current: embedded under model-a with the hash matching its current content.
        await SeedDocAsync("doc-current", "Current", "up to date body");
        var currentHash = MemoryContentHasher.ComputeHash("Current", "up to date body");
        await _store.UpsertEmbeddingAsync("doc-current", "document", "model-a", currentHash, new float[] { 1f }, TestContext.Current.CancellationToken);

        // doc-stale: has a model-a row, but its stored hash no longer matches (content edited
        // since the embedding was written) — should NOT count toward EmbeddedCurrentHashCount.
        await SeedDocAsync("doc-stale", "Stale", "edited body");
        await _store.UpsertEmbeddingAsync("doc-stale", "document", "model-a", "stale-hash-from-before-the-edit", new float[] { 2f }, TestContext.Current.CancellationToken);

        // doc-other-model: only has a row under model-b.
        await SeedDocAsync("doc-other-model", "Other", "other model body");
        await _store.UpsertEmbeddingAsync("doc-other-model", "document", "model-b", "whatever", new float[] { 3f }, TestContext.Current.CancellationToken);

        // doc-unembedded: no embedding row at all.
        await SeedDocAsync("doc-unembedded", "Unembedded", "never embedded");

        var coverage = await _store.GetEmbeddingCoverageAsync("model-a", TestContext.Current.CancellationToken);

        Assert.Equal(4, coverage.TotalRecallableDocuments);
        Assert.Equal(1, coverage.EmbeddedCurrentHashCount);
        Assert.Equal(1, coverage.OtherModelCount);
    }

    [Fact]
    public async Task GetEmbeddingCoverageAsync_excludes_tombstoned_documents_from_the_total()
    {
        var anchor = _store.CreateDefaultAnchor("coverage-tombstone-test");
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        await _store.UpsertDocumentAsync(new SQLiteMemoryDocument(
            DocumentId: "doc-live",
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
        await _store.UpsertDocumentAsync(new SQLiteMemoryDocument(
            DocumentId: "doc-tombstoned",
            Anchor: anchor,
            MemoryClass: "durable_fact",
            Title: "t2",
            MarkdownBody: "b2",
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
        await _store.TombstoneDocumentAsync("doc-tombstoned", TestContext.Current.CancellationToken);

        var coverage = await _store.GetEmbeddingCoverageAsync("model-a", TestContext.Current.CancellationToken);

        Assert.Equal(1, coverage.TotalRecallableDocuments);
    }
}
