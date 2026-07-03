// -----------------------------------------------------------------------
// <copyright file="SQLiteMemoryStoreTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Microsoft.Data.Sqlite;
using Netclaw.Actors.Memory;
using Netclaw.Configuration;
using Xunit;

namespace Netclaw.Actors.Tests.Memory;

public sealed class SQLiteMemoryStoreTests : IAsyncLifetime
{
    private readonly string _baseDir = Path.Combine(Path.GetTempPath(), "netclaw-sqlite-memory-tests", Guid.NewGuid().ToString("N"));
    private readonly string _dbPath;
    private readonly SQLiteMemoryStore _store;

    public SQLiteMemoryStoreTests()
    {
        Directory.CreateDirectory(_baseDir);
        _dbPath = Path.Combine(_baseDir, "netclaw.db");
        _store = new SQLiteMemoryStore(_dbPath, TimeProvider.System);
    }

    [Fact]
    public async Task InitializeAsync_creates_schema_and_checkpoint_table()
    {
        await _store.InitializeAsync(TestContext.Current.CancellationToken);

        var pending = await _store.GetPendingCheckpointCountAsync(TestContext.Current.CancellationToken);
        Assert.Equal(0, pending);
        Assert.True(File.Exists(_dbPath));
    }

    [Fact]
    public async Task InitializeAsync_repairs_legacy_compaction_boundary_recall_mode()
    {
        await _store.InitializeAsync(TestContext.Current.CancellationToken);

        var anchor = _store.CreateDefaultAnchor("session-compaction");
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        // A pre-#1225 compaction summary (searchable => still in the automatic
        // recall pool) and an unrelated searchable doc that must NOT be touched.
        await UpsertSimpleDoc("doc-legacy-compaction", anchor, "compaction-boundary", "searchable", now);
        await UpsertSimpleDoc("doc-other-searchable", anchor, "Some Other Memory", "searchable", now);

        // Re-running initialization applies the idempotent data repair.
        await _store.InitializeAsync(TestContext.Current.CancellationToken);

        Assert.Equal("manual", await ReadRecallMode("doc-legacy-compaction"));
        Assert.Equal("searchable", await ReadRecallMode("doc-other-searchable"));

        // Idempotent: a third run changes nothing.
        await _store.InitializeAsync(TestContext.Current.CancellationToken);
        Assert.Equal("manual", await ReadRecallMode("doc-legacy-compaction"));
    }

    private async Task UpsertSimpleDoc(string id, SQLiteMemoryAnchor anchor, string title, string recallMode, long now)
    {
        await _store.UpsertDocumentAsync(new SQLiteMemoryDocument(
            DocumentId: id,
            Anchor: anchor,
            MemoryClass: "evidence",
            Title: title,
            MarkdownBody: "Summary body.",
            AliasesJson: null,
            FacetsJson: null,
            SlotsJson: null,
            UpdateSemantics: "merge-document",
            Sensitivity: "normal",
            RecallMode: recallMode,
            Confidence: 0.9,
            FreshnessAtMs: now,
            ExpiresAtMs: null,
            CreatedAtMs: now,
            UpdatedAtMs: now), TestContext.Current.CancellationToken);
    }

    private async Task<string?> ReadRecallMode(string documentId)
    {
        await using var conn = new SqliteConnection($"Data Source={_dbPath}");
        await conn.OpenAsync(TestContext.Current.CancellationToken);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT recall_mode FROM memory_documents WHERE document_id = $id";
        cmd.Parameters.AddWithValue("$id", documentId);
        return (string?)await cmd.ExecuteScalarAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task UpsertAndSearchAutoRecallDocuments_filters_by_policy()
    {
        await _store.InitializeAsync(TestContext.Current.CancellationToken);

        var anchor = _store.CreateDefaultAnchor("netclaw");
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        await _store.UpsertDocumentAsync(new SQLiteMemoryDocument(
            DocumentId: "doc-1",
            Anchor: anchor,
            MemoryClass: "durable_fact",
            Title: "Netclaw memory redesign",
            MarkdownBody: "Use sqlite-backed automatic recall.",
            AliasesJson: "[\"sqlite memory\",\"automatic recall\"]",
            FacetsJson: "[\"project_fact\"]",
            SlotsJson: null,
            UpdateSemantics: "merge-document",
            Sensitivity: "normal",
            RecallMode: "auto",
            Confidence: 0.95,
            FreshnessAtMs: now,
            ExpiresAtMs: null,
            CreatedAtMs: now,
            UpdatedAtMs: now), TestContext.Current.CancellationToken);

        await _store.UpsertDocumentAsync(new SQLiteMemoryDocument(
            DocumentId: "doc-2",
            Anchor: anchor,
            MemoryClass: "durable_fact",
            Title: "Secret token",
            MarkdownBody: "This should not auto recall.",
            AliasesJson: "[\"secret token\"]",
            FacetsJson: "[\"project_fact\"]",
            SlotsJson: null,
            UpdateSemantics: "merge-document",
            Sensitivity: "secret",
            RecallMode: "auto",
            Confidence: 0.99,
            FreshnessAtMs: now,
            ExpiresAtMs: null,
            CreatedAtMs: now,
            UpdatedAtMs: now), TestContext.Current.CancellationToken);

        var results = await _store.SearchAutoRecallDocumentsAsync("sqlite", 5, ct: TestContext.Current.CancellationToken);

        Assert.Single(results);
        Assert.Equal("doc-1", results[0].DocumentId);
    }

    [Fact]
    public async Task EnqueueCheckpoint_increments_pending_count()
    {
        await _store.InitializeAsync(TestContext.Current.CancellationToken);
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        await _store.EnqueueCheckpointAsync(new SQLiteMemoryCheckpoint(
            CheckpointId: "cp-1",
            SessionId: "chan/thread",
            TurnId: "turn-1",
            TriggerType: "turn-complete",
            Priority: 10,
            Status: "pending",
            PayloadJson: "{}",
            RetryCount: 0,
            CreatedAtMs: now,
            UpdatedAtMs: now), TestContext.Current.CancellationToken);

        var pending = await _store.GetPendingCheckpointCountAsync(TestContext.Current.CancellationToken);
        Assert.Equal(1, pending);
    }

    [Fact]
    public async Task SearchAutoRecallDocuments_excludes_expired_evidence_and_trace()
    {
        await _store.InitializeAsync(TestContext.Current.CancellationToken);

        var anchor = _store.CreateDefaultAnchor("netclaw");
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        await _store.UpsertDocumentAsync(new SQLiteMemoryDocument(
            DocumentId: "doc-durable",
            Anchor: anchor,
            MemoryClass: "durable_fact",
            Title: "Active durable fact",
            MarkdownBody: "keep this visible in auto recall",
            AliasesJson: "[\"durable fact\"]",
            FacetsJson: "[\"project_fact\"]",
            SlotsJson: null,
            UpdateSemantics: "merge-document",
            Sensitivity: "normal",
            RecallMode: "auto",
            Confidence: 0.95,
            FreshnessAtMs: now,
            ExpiresAtMs: null,
            CreatedAtMs: now,
            UpdatedAtMs: now), TestContext.Current.CancellationToken);

        await _store.UpsertDocumentAsync(new SQLiteMemoryDocument(
            DocumentId: "doc-expired-evidence",
            Anchor: anchor,
            MemoryClass: "evidence",
            Title: "Expired evidence",
            MarkdownBody: "should be excluded from auto recall",
            AliasesJson: "[\"expired evidence\"]",
            FacetsJson: "[\"project_fact\"]",
            SlotsJson: null,
            UpdateSemantics: "merge-document",
            Sensitivity: "normal",
            RecallMode: "auto",
            Confidence: 0.8,
            FreshnessAtMs: now - 1000,
            ExpiresAtMs: now - 1,
            CreatedAtMs: now,
            UpdatedAtMs: now), TestContext.Current.CancellationToken);

        await _store.UpsertDocumentAsync(new SQLiteMemoryDocument(
            DocumentId: "doc-expired-trace",
            Anchor: anchor,
            MemoryClass: "trace",
            Title: "Trace breadcrumb",
            MarkdownBody: "should never appear",
            AliasesJson: null,
            FacetsJson: null,
            SlotsJson: null,
            UpdateSemantics: "conversation_trace",
            Sensitivity: "normal",
            RecallMode: "auto",
            Confidence: 0.5,
            FreshnessAtMs: now - 1000,
            ExpiresAtMs: now - 1,
            CreatedAtMs: now,
            UpdatedAtMs: now), TestContext.Current.CancellationToken);

        var results = await _store.SearchAutoRecallDocumentsAsync("visible excluded", 10, ct: TestContext.Current.CancellationToken);

        Assert.Contains(results, x => x.DocumentId == "doc-durable");
        Assert.DoesNotContain(results, x => x.DocumentId == "doc-expired-evidence");
        Assert.DoesNotContain(results, x => x.DocumentId == "doc-expired-trace");
    }

    [Fact]
    public async Task SearchByPlan_filters_results_by_allowed_audience()
    {
        await _store.InitializeAsync(TestContext.Current.CancellationToken);

        var anchor = _store.CreateDefaultAnchor("netclaw");
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        await _store.UpsertDocumentAsync(new SQLiteMemoryDocument(
            DocumentId: "doc-public",
            Anchor: anchor,
            MemoryClass: "durable_fact",
            Title: "Public note",
            MarkdownBody: "Visible to everyone.",
            AliasesJson: null,
            FacetsJson: "[\"project_fact\"]",
            SlotsJson: null,
            UpdateSemantics: "merge-document",
            Sensitivity: "normal",
            RecallMode: "auto",
            Confidence: 0.9,
            FreshnessAtMs: now,
            ExpiresAtMs: null,
            CreatedAtMs: now,
            UpdatedAtMs: now,
            Audience: TrustAudience.Public.ToWireValue()), TestContext.Current.CancellationToken);

        await _store.UpsertDocumentAsync(new SQLiteMemoryDocument(
            DocumentId: "doc-personal",
            Anchor: anchor,
            MemoryClass: "durable_fact",
            Title: "Personal note",
            MarkdownBody: "Visible only in personal contexts.",
            AliasesJson: null,
            FacetsJson: "[\"project_fact\"]",
            SlotsJson: null,
            UpdateSemantics: "merge-document",
            Sensitivity: "normal",
            RecallMode: "auto",
            Confidence: 0.95,
            FreshnessAtMs: now,
            ExpiresAtMs: null,
            CreatedAtMs: now,
            UpdatedAtMs: now,
            Audience: TrustAudience.Personal.ToWireValue()), TestContext.Current.CancellationToken);

        var publicResults = await _store.SearchByPlanAsync(
            ["visible"],
            [MemoryClass.DurableFact.ToWireValue()],
            10,
            TrustBoundary.TrustedInstanceValue,
            TrustAudience.Public,
            false, TestContext.Current.CancellationToken);

        var personalResults = await _store.SearchByPlanAsync(
            ["visible"],
            [MemoryClass.DurableFact.ToWireValue()],
            10,
            TrustBoundary.TrustedInstanceValue,
            TrustAudience.Personal,
            false, TestContext.Current.CancellationToken);

        Assert.Contains(publicResults, x => x.Id == "doc-public");
        Assert.DoesNotContain(publicResults, x => x.Id == "doc-personal");
        Assert.Contains(personalResults, x => x.Id == "doc-public");
        Assert.Contains(personalResults, x => x.Id == "doc-personal");
    }

    [Fact]
    public async Task ResolveMemoryHandleAsync_maps_each_id_form_to_its_exact_storage_key()
    {
        await _store.InitializeAsync(TestContext.Current.CancellationToken);

        var anchor = _store.CreateDefaultAnchor("distinct-memory");
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        // Two distinct rows whose keys differ only by the legacy prefix. Because the parsed id is
        // used as the exact primary key, each id form resolves to exactly one row — no ambiguity.
        await _store.UpsertDocumentAsync(CreateDocument("abc", anchor, "Bare ID", now), TestContext.Current.CancellationToken);
        await _store.UpsertDocumentAsync(CreateDocument("doc-abc", anchor, "Legacy ID", now), TestContext.Current.CancellationToken);

        var bare = await _store.ResolveMemoryHandleAsync("doc:abc", TrustBoundary.TrustedInstanceValue, TrustAudience.Personal, TestContext.Current.CancellationToken);
        var dash = await _store.ResolveMemoryHandleAsync("doc-abc", TrustBoundary.TrustedInstanceValue, TrustAudience.Personal, TestContext.Current.CancellationToken);
        var envelope = await _store.ResolveMemoryHandleAsync("doc:doc-abc", TrustBoundary.TrustedInstanceValue, TrustAudience.Personal, TestContext.Current.CancellationToken);

        Assert.True(bare.Resolved);
        Assert.Equal("abc", bare.StorageId!.Value.Value);
        Assert.True(dash.Resolved);
        Assert.Equal("doc-abc", dash.StorageId!.Value.Value);
        // The colon envelope over the dash key resolves to the same row as the dash key.
        Assert.True(envelope.Resolved);
        Assert.Equal("doc-abc", envelope.StorageId!.Value.Value);
    }

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    public async ValueTask DisposeAsync()
    {
        await TryDeleteDirectoryAsync(_baseDir);
    }

    private static async Task TryDeleteDirectoryAsync(string path)
    {
        if (!Directory.Exists(path))
            return;

        SqliteConnection.ClearAllPools();

        for (var i = 0; i < 8; i++)
        {
            try
            {
                Directory.Delete(path, recursive: true);
                return;
            }
            catch (IOException) when (i < 7)
            {
                await Task.Delay(25 * (i + 1));
            }
            catch (UnauthorizedAccessException) when (i < 7)
            {
                await Task.Delay(25 * (i + 1));
            }
        }

        // Best effort cleanup: file handles can remain briefly open on Windows CI.
        // Leaving temp dirs behind is preferable to failing the test run.
    }

    private static SQLiteMemoryDocument CreateDocument(string id, SQLiteMemoryAnchor anchor, string title, long now)
        => new(
            DocumentId: id,
            Anchor: anchor,
            MemoryClass: MemoryClass.DurableFact.ToWireValue(),
            Title: title,
            MarkdownBody: $"Content for {title}.",
            AliasesJson: null,
            FacetsJson: null,
            SlotsJson: null,
            UpdateSemantics: MemoryUpdateSemantics.MergeDocument.ToWireValue(),
            Sensitivity: MemorySensitivity.Normal.ToWireValue(),
            RecallMode: MemoryRecallMode.Auto.ToWireValue(),
            Confidence: 0.9,
            FreshnessAtMs: now,
            ExpiresAtMs: null,
            CreatedAtMs: now,
            UpdatedAtMs: now,
            Boundary: TrustBoundary.TrustedInstanceValue,
            Audience: TrustAudience.Team.ToWireValue());
}
