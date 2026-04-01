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
        await _store.InitializeAsync();

        var pending = await _store.GetPendingCheckpointCountAsync();
        Assert.Equal(0, pending);
        Assert.True(File.Exists(_dbPath));
    }

    [Fact]
    public async Task UpsertAndSearchAutoRecallDocuments_filters_by_policy()
    {
        await _store.InitializeAsync();

        var anchor = _store.CreateDefaultAnchor("netclaw", "project:test");
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
            Domain: "project:test",
            Sensitivity: "normal",
            RecallMode: "auto",
            Confidence: 0.95,
            FreshnessAtMs: now,
            ExpiresAtMs: null,
            CreatedAtMs: now,
            UpdatedAtMs: now));

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
            Domain: "project:test",
            Sensitivity: "secret",
            RecallMode: "auto",
            Confidence: 0.99,
            FreshnessAtMs: now,
            ExpiresAtMs: null,
            CreatedAtMs: now,
            UpdatedAtMs: now));

        var results = await _store.SearchAutoRecallDocumentsAsync("sqlite", "project:test", 5);

        Assert.Single(results);
        Assert.Equal("doc-1", results[0].DocumentId);
    }

    [Fact]
    public async Task EnqueueCheckpoint_increments_pending_count()
    {
        await _store.InitializeAsync();
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
            UpdatedAtMs: now));

        var pending = await _store.GetPendingCheckpointCountAsync();
        Assert.Equal(1, pending);
    }

    [Fact]
    public async Task SearchAutoRecallDocuments_excludes_expired_evidence_and_trace()
    {
        await _store.InitializeAsync();

        var anchor = _store.CreateDefaultAnchor("netclaw", "project:test");
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
            Domain: "project:test",
            Sensitivity: "normal",
            RecallMode: "auto",
            Confidence: 0.95,
            FreshnessAtMs: now,
            ExpiresAtMs: null,
            CreatedAtMs: now,
            UpdatedAtMs: now));

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
            Domain: "project:test",
            Sensitivity: "normal",
            RecallMode: "auto",
            Confidence: 0.8,
            FreshnessAtMs: now - 1000,
            ExpiresAtMs: now - 1,
            CreatedAtMs: now,
            UpdatedAtMs: now));

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
            Domain: "project:test",
            Sensitivity: "normal",
            RecallMode: "auto",
            Confidence: 0.5,
            FreshnessAtMs: now - 1000,
            ExpiresAtMs: now - 1,
            CreatedAtMs: now,
            UpdatedAtMs: now));

        var results = await _store.SearchAutoRecallDocumentsAsync("visible excluded", "project:test", 10);

        Assert.Contains(results, x => x.DocumentId == "doc-durable");
        Assert.DoesNotContain(results, x => x.DocumentId == "doc-expired-evidence");
        Assert.DoesNotContain(results, x => x.DocumentId == "doc-expired-trace");
    }

    [Fact]
    public async Task InitializeAsync_demotes_malformed_auto_recall_documents_to_searchable()
    {
        await _store.InitializeAsync();

        var anchor = _store.CreateDefaultAnchor("textforge", "project:test");
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        await _store.UpsertDocumentAsync(new SQLiteMemoryDocument(
            DocumentId: "doc-bad",
            Anchor: anchor,
            MemoryClass: "durable_fact",
            Title: "doc:doc-bad",
            MarkdownBody: "Malformed imported document.",
            AliasesJson: null,
            FacetsJson: null,
            SlotsJson: null,
            UpdateSemantics: "merge-document",
            Domain: "project:test",
            Sensitivity: "normal",
            RecallMode: "auto",
            Confidence: 0.8,
            FreshnessAtMs: now,
            ExpiresAtMs: null,
            CreatedAtMs: now,
            UpdatedAtMs: now));

        await _store.InitializeAsync();

        await using var conn = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = _dbPath }.ToString());
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT recall_mode, facets_json FROM memory_documents WHERE document_id = 'doc-bad';";
        await using var reader = await cmd.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal("searchable", reader.GetString(0));
        Assert.Contains("needs_metadata_enrichment", reader.GetString(1), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SearchByPlan_filters_results_by_allowed_audience()
    {
        await _store.InitializeAsync();

        var anchor = _store.CreateDefaultAnchor("netclaw", "project:test");
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
            Domain: "project:test",
            Sensitivity: "normal",
            RecallMode: "auto",
            Confidence: 0.9,
            FreshnessAtMs: now,
            ExpiresAtMs: null,
            CreatedAtMs: now,
            UpdatedAtMs: now,
            Audience: TrustAudience.Public.ToWireValue()));

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
            Domain: "project:test",
            Sensitivity: "normal",
            RecallMode: "auto",
            Confidence: 0.95,
            FreshnessAtMs: now,
            ExpiresAtMs: null,
            CreatedAtMs: now,
            UpdatedAtMs: now,
            Audience: TrustAudience.Personal.ToWireValue()));

        var publicResults = await _store.SearchByPlanAsync(
            ["visible"],
            "project:test",
            [MemoryClass.DurableFact.ToWireValue()],
            10,
            SecurityPolicyDefaults.InferLegacyBoundaryFromDomain("project:test"),
            TrustAudience.Public,
            false);

        var personalResults = await _store.SearchByPlanAsync(
            ["visible"],
            "project:test",
            [MemoryClass.DurableFact.ToWireValue()],
            10,
            SecurityPolicyDefaults.InferLegacyBoundaryFromDomain("project:test"),
            TrustAudience.Personal,
            false);

        Assert.Contains(publicResults, x => x.Id == "doc-public");
        Assert.DoesNotContain(publicResults, x => x.Id == "doc-personal");
        Assert.Contains(personalResults, x => x.Id == "doc-public");
        Assert.Contains(personalResults, x => x.Id == "doc-personal");
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
}
