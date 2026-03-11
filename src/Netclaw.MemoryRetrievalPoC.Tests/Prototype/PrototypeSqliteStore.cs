using Microsoft.Data.Sqlite;

namespace Netclaw.MemoryRetrievalPoC.Tests.Prototype;

internal sealed class PrototypeSqliteStore : IDisposable
{
    private readonly string _dbPath;
    private readonly string _connectionString;

    public PrototypeSqliteStore()
    {
        var root = Path.Combine(Path.GetTempPath(), "netclaw-memory-retrieval-poc", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        _dbPath = Path.Combine(root, "prototype.db");
        _connectionString = new SqliteConnectionStringBuilder { DataSource = _dbPath }.ToString();
    }

    public async Task InitializeAndSeedAsync(RetrievalFixture fixture, CancellationToken ct = default)
    {
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);

        var schemaSql = """
            PRAGMA journal_mode = WAL;

            CREATE TABLE memory_anchors(
              anchor_id TEXT PRIMARY KEY,
              anchor_type TEXT NOT NULL,
              canonical_name TEXT NOT NULL,
              parent_anchor_id TEXT NULL,
              domain TEXT NOT NULL,
              sensitivity TEXT NOT NULL,
              recall_mode TEXT NOT NULL,
              confidence REAL NOT NULL,
              freshness_at INTEGER NULL,
              status TEXT NOT NULL,
              created_at INTEGER NOT NULL,
              updated_at INTEGER NOT NULL
            );

            CREATE TABLE memory_documents(
              document_id TEXT PRIMARY KEY,
              anchor_id TEXT NOT NULL,
              memory_class TEXT NOT NULL DEFAULT 'durable_fact',
              title TEXT NOT NULL,
              markdown_body TEXT NOT NULL,
              update_semantics TEXT NOT NULL,
              domain TEXT NOT NULL,
              sensitivity TEXT NOT NULL,
              recall_mode TEXT NOT NULL,
              confidence REAL NOT NULL,
              freshness_at INTEGER NULL,
              expires_at INTEGER NULL,
              created_at INTEGER NOT NULL,
              updated_at INTEGER NOT NULL
            );

            CREATE TABLE memory_edges(
              edge_id TEXT PRIMARY KEY,
              from_anchor_id TEXT NOT NULL,
              to_anchor_id TEXT NOT NULL,
              relation_type TEXT NOT NULL,
              domain TEXT NOT NULL,
              sensitivity TEXT NOT NULL,
              recall_mode TEXT NOT NULL,
              confidence REAL NOT NULL,
              freshness_at INTEGER NULL,
              created_at INTEGER NOT NULL,
              updated_at INTEGER NOT NULL
            );
            """;

        await using (var schema = conn.CreateCommand())
        {
            schema.CommandText = schemaSql;
            await schema.ExecuteNonQueryAsync(ct);
        }

        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        foreach (var doc in fixture.SeedDocuments)
        {
            await using var anchor = conn.CreateCommand();
            anchor.CommandText = """
                INSERT INTO memory_anchors(anchor_id, anchor_type, canonical_name, parent_anchor_id, domain, sensitivity, recall_mode, confidence, freshness_at, status, created_at, updated_at)
                VALUES($anchorId, $anchorType, $canonicalName, NULL, $domain, $sensitivity, $recallMode, $confidence, $freshnessAt, 'active', $createdAt, $updatedAt);
                """;
            anchor.Parameters.AddWithValue("$anchorId", doc.AnchorId);
            anchor.Parameters.AddWithValue("$anchorType", doc.AnchorType);
            anchor.Parameters.AddWithValue("$canonicalName", doc.CanonicalName);
            anchor.Parameters.AddWithValue("$domain", doc.Domain);
            anchor.Parameters.AddWithValue("$sensitivity", doc.Sensitivity);
            anchor.Parameters.AddWithValue("$recallMode", doc.RecallMode);
            anchor.Parameters.AddWithValue("$confidence", doc.Confidence);
            anchor.Parameters.AddWithValue("$freshnessAt", DBNull.Value);
            anchor.Parameters.AddWithValue("$createdAt", now);
            anchor.Parameters.AddWithValue("$updatedAt", now);
            await anchor.ExecuteNonQueryAsync(ct);

            await using var document = conn.CreateCommand();
            document.CommandText = """
                INSERT INTO memory_documents(document_id, anchor_id, memory_class, title, markdown_body, update_semantics, domain, sensitivity, recall_mode, confidence, freshness_at, expires_at, created_at, updated_at)
                VALUES($documentId, $anchorId, $memoryClass, $title, $body, 'merge-document', $domain, $sensitivity, $recallMode, $confidence, NULL, NULL, $createdAt, $updatedAt);
                """;
            document.Parameters.AddWithValue("$documentId", doc.DocumentId);
            document.Parameters.AddWithValue("$anchorId", doc.AnchorId);
            document.Parameters.AddWithValue("$memoryClass", doc.MemoryClass);
            document.Parameters.AddWithValue("$title", doc.Title);
            document.Parameters.AddWithValue("$body", doc.MarkdownBody);
            document.Parameters.AddWithValue("$domain", doc.Domain);
            document.Parameters.AddWithValue("$sensitivity", doc.Sensitivity);
            document.Parameters.AddWithValue("$recallMode", doc.RecallMode);
            document.Parameters.AddWithValue("$confidence", doc.Confidence);
            document.Parameters.AddWithValue("$createdAt", now);
            document.Parameters.AddWithValue("$updatedAt", now);
            await document.ExecuteNonQueryAsync(ct);

            foreach (var alias in doc.Aliases)
            {
                await using var edge = conn.CreateCommand();
                edge.CommandText = """
                    INSERT INTO memory_edges(edge_id, from_anchor_id, to_anchor_id, relation_type, domain, sensitivity, recall_mode, confidence, freshness_at, created_at, updated_at)
                    VALUES($edgeId, $fromAnchorId, $toAnchorId, 'alias', $domain, $sensitivity, $recallMode, $confidence, NULL, $createdAt, $updatedAt);
                    """;
                edge.Parameters.AddWithValue("$edgeId", $"edge:{doc.AnchorId}:{alias}");
                edge.Parameters.AddWithValue("$fromAnchorId", doc.AnchorId);
                edge.Parameters.AddWithValue("$toAnchorId", $"alias:{alias}");
                edge.Parameters.AddWithValue("$domain", doc.Domain);
                edge.Parameters.AddWithValue("$sensitivity", doc.Sensitivity);
                edge.Parameters.AddWithValue("$recallMode", doc.RecallMode);
                edge.Parameters.AddWithValue("$confidence", doc.Confidence);
                edge.Parameters.AddWithValue("$createdAt", now);
                edge.Parameters.AddWithValue("$updatedAt", now);
                await edge.ExecuteNonQueryAsync(ct);
            }
        }

        foreach (var edgeSeed in fixture.SeedEdges)
        {
            await using var edge = conn.CreateCommand();
            edge.CommandText = """
                INSERT INTO memory_edges(edge_id, from_anchor_id, to_anchor_id, relation_type, domain, sensitivity, recall_mode, confidence, freshness_at, created_at, updated_at)
                VALUES($edgeId, $fromAnchorId, $toAnchorId, $relationType, $domain, $sensitivity, $recallMode, $confidence, NULL, $createdAt, $updatedAt);
                """;
            edge.Parameters.AddWithValue("$edgeId", edgeSeed.EdgeId);
            edge.Parameters.AddWithValue("$fromAnchorId", edgeSeed.FromAnchorId);
            edge.Parameters.AddWithValue("$toAnchorId", edgeSeed.ToAnchorId);
            edge.Parameters.AddWithValue("$relationType", edgeSeed.RelationType);
            edge.Parameters.AddWithValue("$domain", edgeSeed.Domain);
            edge.Parameters.AddWithValue("$sensitivity", edgeSeed.Sensitivity);
            edge.Parameters.AddWithValue("$recallMode", edgeSeed.RecallMode);
            edge.Parameters.AddWithValue("$confidence", edgeSeed.Confidence);
            edge.Parameters.AddWithValue("$createdAt", now);
            edge.Parameters.AddWithValue("$updatedAt", now);
            await edge.ExecuteNonQueryAsync(ct);
        }
    }

    public async Task<IReadOnlyList<RetrievedDocument>> LoadDocumentsAsync(string domain, CancellationToken ct = default)
    {
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT d.document_id, d.anchor_id, a.canonical_name, d.title, d.markdown_body, d.memory_class, d.domain, d.sensitivity, d.recall_mode, d.confidence
            FROM memory_documents d
            INNER JOIN memory_anchors a ON a.anchor_id = d.anchor_id
            WHERE d.domain = $domain AND d.recall_mode = 'auto' AND d.sensitivity != 'secret';
            """;
        cmd.Parameters.AddWithValue("$domain", domain);

        var docs = new List<RetrievedDocument>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            docs.Add(new RetrievedDocument(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetString(4),
                reader.GetString(5),
                reader.GetString(6),
                reader.GetString(7),
                reader.GetString(8),
                reader.GetDouble(9)));
        }

        return docs;
    }

    public async Task<IReadOnlyList<RetrievedEdge>> LoadEdgesAsync(string domain, CancellationToken ct = default)
    {
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT from_anchor_id, to_anchor_id, relation_type, confidence
            FROM memory_edges
            WHERE domain = $domain AND recall_mode = 'auto' AND sensitivity != 'secret';
            """;
        cmd.Parameters.AddWithValue("$domain", domain);

        var edges = new List<RetrievedEdge>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            edges.Add(new RetrievedEdge(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetDouble(3)));
        }

        return edges;
    }

    public void Dispose()
    {
        var path = Path.GetDirectoryName(_dbPath);
        if (path is not null && Directory.Exists(path))
            Directory.Delete(path, recursive: true);
    }
}
