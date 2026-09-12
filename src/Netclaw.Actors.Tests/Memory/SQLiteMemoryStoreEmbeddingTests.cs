// -----------------------------------------------------------------------
// <copyright file="SQLiteMemoryStoreEmbeddingTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Microsoft.Data.Sqlite;
using Netclaw.Actors.Memory;
using Netclaw.Configuration;
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
        await SeedDocumentAsync("doc-1", "Title", "Body");

        await _store.UpsertEmbeddingAsync(
            "doc-1", "document", "model-a", MemoryContentHasher.ComputeHash("Title", "Body"), vector,
            TestContext.Current.CancellationToken);

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
        await SeedDocumentAsync("doc-1", "Title", "Body");
        var hash = MemoryContentHasher.ComputeHash("Title", "Body");
        await _store.UpsertEmbeddingAsync("doc-1", "document", "model-a", hash, vector, TestContext.Current.CancellationToken);
        var versionAfterFirstWrite = _store.EmbeddingDataVersion;

        // Same hash, even with a different (bogus) vector — must be skipped entirely: the
        // stored vector is untouched and the version counter does not move.
        await _store.UpsertEmbeddingAsync("doc-1", "document", "model-a", hash, new float[] { 9f, 9f, 9f }, TestContext.Current.CancellationToken);

        var rows = await _store.GetEmbeddingsForModelAsync("model-a", TestContext.Current.CancellationToken);
        Assert.Equal(vector, Assert.Single(rows).Vector.ToArray());
        Assert.Equal(versionAfterFirstWrite, _store.EmbeddingDataVersion);
    }

    [Fact]
    public async Task UpsertEmbeddingAsync_after_document_change_writes_the_new_vector()
    {
        await SeedDocumentAsync("doc-1", "Title", "First body");
        await _store.UpsertEmbeddingAsync(
            "doc-1", "document", "model-a", MemoryContentHasher.ComputeHash("Title", "First body"),
            new float[] { 1f, 2f, 3f }, TestContext.Current.CancellationToken);
        var versionAfterFirstWrite = _store.EmbeddingDataVersion;

        await _store.ReplaceDocumentTextAsync("doc-1", "Second body", TestContext.Current.CancellationToken);
        await _store.UpsertEmbeddingAsync(
            "doc-1", "document", "model-a", MemoryContentHasher.ComputeHash("Title", "Second body"),
            new float[] { 4f, 5f, 6f }, TestContext.Current.CancellationToken);

        var rows = await _store.GetEmbeddingsForModelAsync("model-a", TestContext.Current.CancellationToken);
        Assert.Equal(new float[] { 4f, 5f, 6f }, Assert.Single(rows).Vector.ToArray());
        Assert.True(_store.EmbeddingDataVersion > versionAfterFirstWrite);
    }

    [Fact]
    public async Task ReplaceDocumentTextAsync_removes_the_stale_embedding()
    {
        await SeedDocumentAsync("doc-1", "Title", "First body");
        await _store.UpsertEmbeddingAsync(
            "doc-1", "document", "model-a", MemoryContentHasher.ComputeHash("Title", "First body"),
            new float[] { 1f }, TestContext.Current.CancellationToken);
        var versionBeforeUpdate = _store.EmbeddingDataVersion;

        var updated = await _store.ReplaceDocumentTextAsync(
            "doc-1", "Second body", TestContext.Current.CancellationToken);

        Assert.True(updated);
        Assert.Empty(await _store.GetEmbeddingsForModelAsync("model-a", TestContext.Current.CancellationToken));
        Assert.True(_store.EmbeddingDataVersion > versionBeforeUpdate);
    }

    [Fact]
    public async Task UpsertEmbeddingAsync_rejects_a_hash_from_before_a_document_update()
    {
        await SeedDocumentAsync("doc-1", "Title", "First body");
        var staleHash = MemoryContentHasher.ComputeHash("Title", "First body");
        await _store.ReplaceDocumentTextAsync("doc-1", "Second body", TestContext.Current.CancellationToken);

        var wrote = await _store.UpsertEmbeddingAsync(
            "doc-1", "document", "model-a", staleHash, new float[] { 1f }, TestContext.Current.CancellationToken);

        Assert.False(wrote);
        Assert.Empty(await _store.GetEmbeddingsForModelAsync("model-a", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task UpsertEmbeddingAsync_keys_rows_by_item_and_model_independently()
    {
        await SeedDocumentAsync("doc-1", "Title", "Body");
        var hash = MemoryContentHasher.ComputeHash("Title", "Body");
        await _store.UpsertEmbeddingAsync("doc-1", "document", "model-a", hash, new float[] { 1f }, TestContext.Current.CancellationToken);
        await _store.UpsertEmbeddingAsync("doc-1", "document", "model-b", hash, new float[] { 2f }, TestContext.Current.CancellationToken);

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
        await _store.UpsertEmbeddingAsync(
            "doc-1", "document", "model-a", MemoryContentHasher.ComputeHash("t", "b"),
            new float[] { 1f, 2f }, TestContext.Current.CancellationToken);
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
        await SeedDocAsync("doc-stale", "Stale", "original body");
        await _store.UpsertEmbeddingAsync(
            "doc-stale", "document", "model-a", MemoryContentHasher.ComputeHash("Stale", "original body"),
            new float[] { 2f }, TestContext.Current.CancellationToken);
        await MutateDocumentBodyDirectlyAsync("doc-stale", "edited body");

        // doc-other-model: only has a row under model-b.
        await SeedDocAsync("doc-other-model", "Other", "other model body");
        await _store.UpsertEmbeddingAsync(
            "doc-other-model", "document", "model-b", MemoryContentHasher.ComputeHash("Other", "other model body"),
            new float[] { 3f }, TestContext.Current.CancellationToken);

        // doc-unembedded: no embedding row at all.
        await SeedDocAsync("doc-unembedded", "Unembedded", "never embedded");

        var coverage = await _store.GetEmbeddingCoverageAsync("model-a", TestContext.Current.CancellationToken);

        Assert.Equal(4, coverage.TotalRecallableDocuments);
        Assert.Equal(1, coverage.EmbeddedCurrentHashCount);
        Assert.Equal(1, coverage.OtherModelCount);
    }

    [Fact]
    public async Task GetDocumentsNeedingEmbeddingAsync_returns_only_missing_or_stale_documents()
    {
        var anchor = _store.CreateDefaultAnchor("gap-repair-test");
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

        await SeedDocAsync("doc-current", "Current", "up to date body");
        var currentHash = MemoryContentHasher.ComputeHash("Current", "up to date body");
        await _store.UpsertEmbeddingAsync("doc-current", "document", "model-a", currentHash, new float[] { 1f }, TestContext.Current.CancellationToken);

        await SeedDocAsync("doc-stale", "Stale", "original body");
        await _store.UpsertEmbeddingAsync(
            "doc-stale", "document", "model-a", MemoryContentHasher.ComputeHash("Stale", "original body"),
            new float[] { 2f }, TestContext.Current.CancellationToken);
        await MutateDocumentBodyDirectlyAsync("doc-stale", "edited body");

        await SeedDocAsync("doc-unembedded", "Unembedded", "never embedded");

        var missing = await _store.GetDocumentsNeedingEmbeddingAsync("model-a", force: false, TestContext.Current.CancellationToken);

        Assert.Equal(
            new[] { "doc-stale", "doc-unembedded" },
            missing.Select(m => m.DocumentId).Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task GetDocumentsNeedingEmbeddingAsync_with_force_returns_every_recallable_document()
    {
        var anchor = _store.CreateDefaultAnchor("gap-repair-force-test");
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        await _store.UpsertDocumentAsync(new SQLiteMemoryDocument(
            DocumentId: "doc-current",
            Anchor: anchor,
            MemoryClass: "durable_fact",
            Title: "Current",
            MarkdownBody: "up to date body",
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
        var currentHash = MemoryContentHasher.ComputeHash("Current", "up to date body");
        await _store.UpsertEmbeddingAsync("doc-current", "document", "model-a", currentHash, new float[] { 1f }, TestContext.Current.CancellationToken);

        var forced = await _store.GetDocumentsNeedingEmbeddingAsync("model-a", force: true, TestContext.Current.CancellationToken);

        // Already fully current, but --force means "every recallable document" regardless.
        var doc = Assert.Single(forced);
        Assert.Equal("doc-current", doc.DocumentId);
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

    // ── Batch-apply write results (memory-core-redesign Slice 2, task 2.8: the seam
    // MemoryEmbedOnWriteCoordinator needs post-commit document ids+content) ──

    [Fact]
    public async Task ApplyInlineCurationBatchAsync_returns_written_documents_but_not_records()
    {
        var operations = new[]
        {
            DocumentOperation(memoryId: null, title: "New Doc", content: "doc body"),
            RecordOperation(memoryId: "rec-1", title: "Evidence", content: "evidence body"),
        };

        var written = await _store.ApplyInlineCurationBatchAsync(operations, TestContext.Current.CancellationToken);

        var doc = Assert.Single(written);
        Assert.Equal("New Doc", doc.Title);
        Assert.Equal("doc body", doc.Body);
        Assert.False(string.IsNullOrWhiteSpace(doc.DocumentId));
    }

    [Fact]
    public async Task ApplyInlineCurationBatchAsync_reports_the_final_document_id_for_an_update()
    {
        var written = await _store.ApplyInlineCurationBatchAsync(
            [DocumentOperation(memoryId: "doc-explicit-id", title: "Updated", content: "updated body")],
            TestContext.Current.CancellationToken);

        var doc = Assert.Single(written);
        Assert.Equal("doc-explicit-id", doc.DocumentId);
    }

    [Fact]
    public async Task ApplyCurationBatchAsync_returns_written_documents_but_not_records()
    {
        await _store.EnqueueCheckpointAsync(new SQLiteMemoryCheckpoint(
            CheckpointId: "cp-embed-1",
            SessionId: "chan/thread",
            TurnId: "turn-1",
            TriggerType: "turn-complete",
            Priority: 10,
            Status: "pending",
            PayloadJson: "{}",
            RetryCount: 0,
            CreatedAtMs: DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            UpdatedAtMs: DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()), TestContext.Current.CancellationToken);

        var operations = new[]
        {
            DocumentOperation(memoryId: null, title: "Worker Doc", content: "worker body"),
            RecordOperation(memoryId: "rec-2", title: "Worker Evidence", content: "worker evidence body"),
        };

        var written = await _store.ApplyCurationBatchAsync("cp-embed-1", operations, TestContext.Current.CancellationToken);

        var doc = Assert.Single(written);
        Assert.Equal("Worker Doc", doc.Title);
        Assert.Equal("worker body", doc.Body);
    }

    // ── GetRecallCandidatesByIdsAsync gated hydration (memory-core-redesign Slice 4, task 4.2) ──
    //
    // These prove SQLiteMemoryRecallCoordinator's hybrid path cannot use a vector-sourced hit to
    // bypass a policy gate a lexically-discovered hit (SearchByPlanAsync) would have to clear:
    // every scenario here mirrors one of SearchByPlanAsync's document-branch predicates
    // (recall_mode allowlist, boundary match, audience membership, sensitivity exclusion,
    // memory-class allowlist) via the shared DocumentRecallPolicyPredicateSql helper.

    [Fact]
    public async Task GetRecallCandidatesByIdsAsync_returns_a_document_that_clears_every_gate()
    {
        await SeedGatedDocumentAsync("doc-gated-ok");

        var result = await _store.GetRecallCandidatesByIdsAsync(
            ["doc-gated-ok"],
            [MemoryClass.DurableFact.ToWireValue()],
            TrustBoundary.TrustedInstanceValue,
            TrustAudience.Public,
            allowExpiredEvidence: false,
            TestContext.Current.CancellationToken);

        Assert.Single(result, x => x.Id == "doc-gated-ok");
    }

    [Fact]
    public async Task GetRecallCandidatesByIdsAsync_excludes_a_manual_recall_mode_document()
    {
        await SeedGatedDocumentAsync("doc-gated-manual", recallMode: "manual");

        var result = await _store.GetRecallCandidatesByIdsAsync(
            ["doc-gated-manual"],
            [MemoryClass.DurableFact.ToWireValue()],
            TrustBoundary.TrustedInstanceValue,
            TrustAudience.Public,
            allowExpiredEvidence: false,
            TestContext.Current.CancellationToken);

        Assert.Empty(result);
    }

    [Fact]
    public async Task GetRecallCandidatesByIdsAsync_excludes_a_secret_sensitivity_document()
    {
        await SeedGatedDocumentAsync("doc-gated-secret", sensitivity: "secret");

        var result = await _store.GetRecallCandidatesByIdsAsync(
            ["doc-gated-secret"],
            [MemoryClass.DurableFact.ToWireValue()],
            TrustBoundary.TrustedInstanceValue,
            TrustAudience.Public,
            allowExpiredEvidence: false,
            TestContext.Current.CancellationToken);

        Assert.Empty(result);
    }

    [Fact]
    public async Task GetRecallCandidatesByIdsAsync_excludes_a_document_outside_the_requested_boundary()
    {
        await SeedGatedDocumentAsync("doc-gated-boundary");

        var result = await _store.GetRecallCandidatesByIdsAsync(
            ["doc-gated-boundary"],
            [MemoryClass.DurableFact.ToWireValue()],
            "some-other-boundary",
            TrustAudience.Public,
            allowExpiredEvidence: false,
            TestContext.Current.CancellationToken);

        Assert.Empty(result);
    }

    [Fact]
    public async Task GetRecallCandidatesByIdsAsync_excludes_a_document_outside_the_requested_audience()
    {
        await SeedGatedDocumentAsync("doc-gated-audience", audience: TrustAudience.Team.ToWireValue());

        // Public's allowed-audience set (MemoryPolicyEvaluator.AllowedAudienceWireValues) is
        // [Public] only -- Team is not visible to a Public-scoped request.
        var result = await _store.GetRecallCandidatesByIdsAsync(
            ["doc-gated-audience"],
            [MemoryClass.DurableFact.ToWireValue()],
            TrustBoundary.TrustedInstanceValue,
            TrustAudience.Public,
            allowExpiredEvidence: false,
            TestContext.Current.CancellationToken);

        Assert.Empty(result);
    }

    [Fact]
    public async Task GetRecallCandidatesByIdsAsync_excludes_a_document_outside_the_requested_memory_class()
    {
        await SeedGatedDocumentAsync("doc-gated-class");

        var result = await _store.GetRecallCandidatesByIdsAsync(
            ["doc-gated-class"],
            [MemoryClass.Evidence.ToWireValue()],
            TrustBoundary.TrustedInstanceValue,
            TrustAudience.Public,
            allowExpiredEvidence: false,
            TestContext.Current.CancellationToken);

        Assert.Empty(result);
    }

    private async Task SeedGatedDocumentAsync(
        string documentId,
        string recallMode = "auto",
        string sensitivity = "normal",
        string audience = "public")
    {
        var anchor = _store.CreateDefaultAnchor(documentId);
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        await _store.UpsertDocumentAsync(new SQLiteMemoryDocument(
            DocumentId: documentId,
            Anchor: anchor,
            MemoryClass: "durable_fact",
            Title: "Gated hydration fixture",
            MarkdownBody: "Gated hydration fixture body.",
            AliasesJson: null,
            FacetsJson: null,
            SlotsJson: null,
            UpdateSemantics: "merge-document",
            Sensitivity: sensitivity,
            RecallMode: recallMode,
            Confidence: 0.9,
            FreshnessAtMs: now,
            ExpiresAtMs: null,
            CreatedAtMs: now,
            UpdatedAtMs: now,
            Audience: audience), TestContext.Current.CancellationToken);
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

    private async Task MutateDocumentBodyDirectlyAsync(string documentId, string body)
    {
        await using var connection = new SqliteConnection($"Data Source={_dbPath}");
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "UPDATE memory_documents SET markdown_body = $body WHERE document_id = $id;";
        command.Parameters.AddWithValue("$id", documentId);
        command.Parameters.AddWithValue("$body", body);
        await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
    }

    private static SQLiteMemoryCurationOperation DocumentOperation(string? memoryId, string title, string content)
        => new(
            Kind: "document",
            MemoryClass: "durable_fact",
            MemoryId: memoryId,
            AnchorCanonicalName: title,
            AnchorType: "topic",
            Title: title,
            Content: content,
            AliasesJson: null,
            FacetsJson: null,
            SlotsJson: null,
            Relations: null,
            UpdateSemantics: "merge-document",
            Boundary: TrustBoundary.TrustedInstanceValue,
            Audience: TrustAudience.Team,
            Sensitivity: "normal",
            RecallMode: "auto",
            Confidence: 0.9,
            FreshnessAtMs: DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            ExpiresAtMs: null);

    private static SQLiteMemoryCurationOperation RecordOperation(string memoryId, string title, string content)
        => new(
            Kind: "record",
            MemoryClass: "evidence",
            MemoryId: memoryId,
            AnchorCanonicalName: title,
            AnchorType: "topic",
            Title: title,
            Content: content,
            AliasesJson: null,
            FacetsJson: null,
            SlotsJson: null,
            Relations: null,
            UpdateSemantics: "immutable-record",
            Boundary: TrustBoundary.TrustedInstanceValue,
            Audience: TrustAudience.Team,
            Sensitivity: "normal",
            RecallMode: "searchable",
            Confidence: 0.8,
            FreshnessAtMs: DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            ExpiresAtMs: null);
}
