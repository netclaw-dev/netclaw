// -----------------------------------------------------------------------
// <copyright file="MemoryEmbedOnWriteCoordinatorTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Microsoft.Extensions.Logging.Abstractions;
using Netclaw.Actors.Memory;
using Xunit;

namespace Netclaw.Actors.Tests.Memory;

/// <summary>
/// Covers <see cref="MemoryEmbedOnWriteCoordinator"/> (memory-core-redesign Slice 2, task
/// 2.8): the single embed-on-write hook both curation write pipelines call after their store
/// batch-apply commits.
/// </summary>
public sealed class MemoryEmbedOnWriteCoordinatorTests : IAsyncLifetime
{
    private readonly string _baseDir = Path.Combine(Path.GetTempPath(), "netclaw-embed-on-write-tests", Guid.NewGuid().ToString("N"));
    private readonly string _dbPath;
    private readonly SQLiteMemoryStore _store;

    public MemoryEmbedOnWriteCoordinatorTests()
    {
        Directory.CreateDirectory(_baseDir);
        _dbPath = Path.Combine(_baseDir, "netclaw.db");
        _store = new SQLiteMemoryStore(_dbPath, TimeProvider.System);
    }

    public async ValueTask InitializeAsync() => await _store.InitializeAsync(TestContext.Current.CancellationToken);

    public async ValueTask DisposeAsync() => await SqliteTempDirectoryCleanup.TryDeleteDirectoryAsync(_baseDir);

    [Fact]
    public async Task Available_embedder_embeds_written_documents_with_the_correct_content_hash()
    {
        var anchor = _store.CreateDefaultAnchor("coordinator-test");
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        await _store.UpsertDocumentAsync(new SQLiteMemoryDocument(
            DocumentId: "doc-1",
            Anchor: anchor,
            MemoryClass: "durable_fact",
            Title: "Title",
            MarkdownBody: "Body",
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

        var holder = new MemoryEmbedderHolder(new FakeMemoryEmbedder("model-a", dimensions: 3), initialQueryPrefix: "", initialCalibratedMinCosineSimilarity: null);
        var written = new[] { new MemoryDocumentWriteResult("doc-1", "Title", "Body") };

        await MemoryEmbedOnWriteCoordinator.EmbedWrittenDocumentsAsync(
            holder, _store, written, NullLogger.Instance, TestContext.Current.CancellationToken);

        var rows = await _store.GetEmbeddingsForModelAsync("model-a", TestContext.Current.CancellationToken);
        var row = Assert.Single(rows);
        Assert.Equal("doc-1", row.ItemId);
        Assert.Equal("document", row.ItemKind);

        // The coverage query recomputes MemoryContentHasher over memory_documents and compares
        // against the stored content_hash — a non-zero EmbeddedCurrentHashCount here proves the
        // coordinator wrote the correct hash, not just some hash.
        var coverage = await _store.GetEmbeddingCoverageAsync("model-a", TestContext.Current.CancellationToken);
        Assert.Equal(1, coverage.EmbeddedCurrentHashCount);
    }

    [Fact]
    public async Task Null_holder_skips_embedding_without_throwing()
    {
        var written = new[] { new MemoryDocumentWriteResult("doc-1", "Title", "Body") };

        await MemoryEmbedOnWriteCoordinator.EmbedWrittenDocumentsAsync(
            holder: null, _store, written, NullLogger.Instance, TestContext.Current.CancellationToken);

        Assert.Empty(await _store.GetEmbeddingsForModelAsync("model-a", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Unavailable_embedder_skips_embedding_without_throwing()
    {
        var holder = new MemoryEmbedderHolder(new UnavailableMemoryEmbedder("model-a", "not provisioned"), initialQueryPrefix: "", initialCalibratedMinCosineSimilarity: null);
        var written = new[] { new MemoryDocumentWriteResult("doc-1", "Title", "Body") };

        await MemoryEmbedOnWriteCoordinator.EmbedWrittenDocumentsAsync(
            holder, _store, written, NullLogger.Instance, TestContext.Current.CancellationToken);

        Assert.Empty(await _store.GetEmbeddingsForModelAsync("model-a", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Embed_failure_on_one_item_is_isolated_and_does_not_throw_or_block_others()
    {
        await SeedDocumentAsync("doc-bad", "Bad", "Body");
        await SeedDocumentAsync("doc-good", "Good", "Body");
        var holder = new MemoryEmbedderHolder(new FakeMemoryEmbedder("model-a", dimensions: 2, failOnText: "Bad\nBody"), initialQueryPrefix: "", initialCalibratedMinCosineSimilarity: null);
        var written = new[]
        {
            new MemoryDocumentWriteResult("doc-bad", "Bad", "Body"),
            new MemoryDocumentWriteResult("doc-good", "Good", "Body"),
        };

        // Must not throw: an embedding failure must never propagate out of the coordinator and
        // fail/retry the memory write that already committed (design D3: vectors are derived data).
        await MemoryEmbedOnWriteCoordinator.EmbedWrittenDocumentsAsync(
            holder, _store, written, NullLogger.Instance, TestContext.Current.CancellationToken);

        var rows = await _store.GetEmbeddingsForModelAsync("model-a", TestContext.Current.CancellationToken);
        var row = Assert.Single(rows);
        Assert.Equal("doc-good", row.ItemId);
    }

    [Fact]
    public async Task Empty_written_list_is_a_no_op()
    {
        var holder = new MemoryEmbedderHolder(new FakeMemoryEmbedder("model-a", dimensions: 2), initialQueryPrefix: "", initialCalibratedMinCosineSimilarity: null);

        await MemoryEmbedOnWriteCoordinator.EmbedWrittenDocumentsAsync(
            holder, _store, [], NullLogger.Instance, TestContext.Current.CancellationToken);

        Assert.Empty(await _store.GetEmbeddingsForModelAsync("model-a", TestContext.Current.CancellationToken));
    }

    private async Task SeedDocumentAsync(string documentId, string title, string body)
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        await _store.UpsertDocumentAsync(new SQLiteMemoryDocument(
            DocumentId: documentId,
            Anchor: _store.CreateDefaultAnchor(documentId),
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

    private sealed class FakeMemoryEmbedder(string modelId, int dimensions, string? failOnText = null) : IMemoryEmbedder
    {
        public string ModelId => modelId;

        public int Dimensions => dimensions;

        public bool IsAvailable => true;

        public ValueTask<ReadOnlyMemory<float>> EmbedAsync(string text, EmbeddingPurpose purpose, CancellationToken ct)
        {
            if (failOnText is not null && string.Equals(text, failOnText, StringComparison.Ordinal))
                throw new InvalidOperationException("simulated embed failure");

            return ValueTask.FromResult<ReadOnlyMemory<float>>(new float[dimensions]);
        }

        public async ValueTask<IReadOnlyList<ReadOnlyMemory<float>>> EmbedBatchAsync(IReadOnlyList<string> texts, EmbeddingPurpose purpose, CancellationToken ct)
        {
            var results = new List<ReadOnlyMemory<float>>(texts.Count);
            foreach (var text in texts)
                results.Add(await EmbedAsync(text, purpose, ct));
            return results;
        }
    }
}
