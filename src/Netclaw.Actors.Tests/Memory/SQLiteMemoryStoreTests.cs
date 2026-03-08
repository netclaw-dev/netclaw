using Microsoft.Data.Sqlite;
using Netclaw.Actors.Memory;
using Xunit;

namespace Netclaw.Actors.Tests.Memory;

public sealed class SQLiteMemoryStoreTests : IDisposable
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
            Title: "Netclaw memory redesign",
            MarkdownBody: "Use sqlite-backed automatic recall.",
            UpdateSemantics: "merge-document",
            Domain: "project:test",
            Sensitivity: "normal",
            RecallMode: "auto",
            Confidence: 0.95,
            FreshnessAtMs: now,
            CreatedAtMs: now,
            UpdatedAtMs: now));

        await _store.UpsertDocumentAsync(new SQLiteMemoryDocument(
            DocumentId: "doc-2",
            Anchor: anchor,
            Title: "Secret token",
            MarkdownBody: "This should not auto recall.",
            UpdateSemantics: "merge-document",
            Domain: "project:test",
            Sensitivity: "secret",
            RecallMode: "auto",
            Confidence: 0.99,
            FreshnessAtMs: now,
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

    public void Dispose()
    {
        TryDeleteDirectory(_baseDir);
    }

    private static void TryDeleteDirectory(string path)
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
                Thread.Sleep(25 * (i + 1));
            }
            catch (UnauthorizedAccessException) when (i < 7)
            {
                Thread.Sleep(25 * (i + 1));
            }
        }

        // Best effort cleanup: file handles can remain briefly open on Windows CI.
        // Leaving temp dirs behind is preferable to failing the test run.
    }
}
