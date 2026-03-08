using Microsoft.Data.Sqlite;

namespace Netclaw.Actors.Memory;

/// <summary>
/// SQLite-backed durable memory store with a minimal graph/policy schema.
/// </summary>
public sealed class SQLiteMemoryStore
{
    private readonly string _connectionString;
    private readonly TimeProvider _timeProvider;

    public SQLiteMemoryStore(string sqlitePath, TimeProvider timeProvider)
    {
        _connectionString = new SqliteConnectionStringBuilder { DataSource = sqlitePath }.ToString();
        _timeProvider = timeProvider;
    }

    public async Task InitializeAsync(CancellationToken ct = default)
    {
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);

        var schemaSql = """
            PRAGMA journal_mode = WAL;

            CREATE TABLE IF NOT EXISTS memory_anchors(
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

            CREATE INDEX IF NOT EXISTS idx_memory_anchors_domain_mode
              ON memory_anchors(domain, recall_mode, updated_at DESC);

            CREATE TABLE IF NOT EXISTS memory_documents(
              document_id TEXT PRIMARY KEY,
              anchor_id TEXT NOT NULL,
              title TEXT NOT NULL,
              markdown_body TEXT NOT NULL,
              update_semantics TEXT NOT NULL,
              domain TEXT NOT NULL,
              sensitivity TEXT NOT NULL,
              recall_mode TEXT NOT NULL,
              confidence REAL NOT NULL,
              freshness_at INTEGER NULL,
              created_at INTEGER NOT NULL,
              updated_at INTEGER NOT NULL,
              FOREIGN KEY(anchor_id) REFERENCES memory_anchors(anchor_id)
            );

            CREATE INDEX IF NOT EXISTS idx_memory_documents_anchor
              ON memory_documents(anchor_id, updated_at DESC);

            CREATE INDEX IF NOT EXISTS idx_memory_documents_policy
              ON memory_documents(domain, sensitivity, recall_mode, updated_at DESC);

            CREATE TABLE IF NOT EXISTS memory_records(
              record_id TEXT PRIMARY KEY,
              anchor_id TEXT NOT NULL,
              record_type TEXT NOT NULL,
              payload_json TEXT NOT NULL,
              supersedes_record_id TEXT NULL,
              update_semantics TEXT NOT NULL,
              domain TEXT NOT NULL,
              sensitivity TEXT NOT NULL,
              recall_mode TEXT NOT NULL,
              confidence REAL NOT NULL,
              freshness_at INTEGER NULL,
              created_at INTEGER NOT NULL,
              FOREIGN KEY(anchor_id) REFERENCES memory_anchors(anchor_id)
            );

            CREATE INDEX IF NOT EXISTS idx_memory_records_anchor
              ON memory_records(anchor_id, created_at DESC);

            CREATE INDEX IF NOT EXISTS idx_memory_records_policy
              ON memory_records(domain, sensitivity, recall_mode, created_at DESC);

            CREATE TABLE IF NOT EXISTS memory_edges(
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
              updated_at INTEGER NOT NULL,
              FOREIGN KEY(from_anchor_id) REFERENCES memory_anchors(anchor_id),
              FOREIGN KEY(to_anchor_id) REFERENCES memory_anchors(anchor_id)
            );

            CREATE INDEX IF NOT EXISTS idx_memory_edges_from
              ON memory_edges(from_anchor_id, relation_type);

            CREATE INDEX IF NOT EXISTS idx_memory_edges_to
              ON memory_edges(to_anchor_id, relation_type);

            CREATE TABLE IF NOT EXISTS memory_checkpoints(
              checkpoint_id TEXT PRIMARY KEY,
              session_id TEXT NOT NULL,
              turn_id TEXT NULL,
              trigger_type TEXT NOT NULL,
              priority INTEGER NOT NULL,
              status TEXT NOT NULL,
              payload_json TEXT NOT NULL,
              retry_count INTEGER NOT NULL DEFAULT 0,
              created_at INTEGER NOT NULL,
              updated_at INTEGER NOT NULL
            );

            CREATE INDEX IF NOT EXISTS idx_memory_checkpoints_pending
              ON memory_checkpoints(status, priority DESC, created_at ASC);
            """;

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = schemaSql;
        await cmd.ExecuteNonQueryAsync(ct);

        // Phase A hygiene: conversation turn snapshots are diagnostic trace, not
        // durable auto-recall memory. This repo is prototype-only; normalize any
        // existing rows aggressively to prevent recall pollution.
        await using var hygieneCmd = conn.CreateCommand();
        hygieneCmd.CommandText = """
            UPDATE memory_documents
            SET recall_mode = 'never'
            WHERE title = 'turn-completion'
               OR update_semantics = 'conversation_trace';
            """;
        await hygieneCmd.ExecuteNonQueryAsync(ct);
    }

    public async Task UpsertDocumentAsync(SQLiteMemoryDocument document, CancellationToken ct = default)
    {
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);

        await using var tx = (SqliteTransaction)await conn.BeginTransactionAsync(ct);

        await EnsureAnchorAsync(conn, tx, document.Anchor, ct);

        await using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            INSERT INTO memory_documents(
              document_id, anchor_id, title, markdown_body, update_semantics,
              domain, sensitivity, recall_mode, confidence, freshness_at,
              created_at, updated_at)
            VALUES($id, $anchorId, $title, $body, $semantics,
              $domain, $sensitivity, $recallMode, $confidence, $freshnessAt,
              $createdAt, $updatedAt)
            ON CONFLICT(document_id) DO UPDATE SET
              title=excluded.title,
              markdown_body=excluded.markdown_body,
              update_semantics=excluded.update_semantics,
              domain=excluded.domain,
              sensitivity=excluded.sensitivity,
              recall_mode=excluded.recall_mode,
              confidence=excluded.confidence,
              freshness_at=excluded.freshness_at,
              updated_at=excluded.updated_at;
            """;
        cmd.Parameters.AddWithValue("$id", document.DocumentId);
        cmd.Parameters.AddWithValue("$anchorId", document.Anchor.AnchorId);
        cmd.Parameters.AddWithValue("$title", document.Title);
        cmd.Parameters.AddWithValue("$body", document.MarkdownBody);
        cmd.Parameters.AddWithValue("$semantics", document.UpdateSemantics);
        cmd.Parameters.AddWithValue("$domain", document.Domain);
        cmd.Parameters.AddWithValue("$sensitivity", document.Sensitivity);
        cmd.Parameters.AddWithValue("$recallMode", document.RecallMode);
        cmd.Parameters.AddWithValue("$confidence", document.Confidence);
        cmd.Parameters.AddWithValue("$freshnessAt", (object?)document.FreshnessAtMs ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$createdAt", document.CreatedAtMs);
        cmd.Parameters.AddWithValue("$updatedAt", document.UpdatedAtMs);
        await cmd.ExecuteNonQueryAsync(ct);

        await tx.CommitAsync(ct);
    }

    public async Task<IReadOnlyList<SQLiteMemoryDocument>> SearchAutoRecallDocumentsAsync(
        string query,
        string domain,
        int maxResults,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(query) || maxResults <= 0)
            return [];

        var tokens = TokenizeQuery(query);
        if (tokens.Count == 0)
            return [];

        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);

        await using var cmd = conn.CreateCommand();
        var termClauses = new List<string>();
        for (var i = 0; i < tokens.Count; i++)
        {
            termClauses.Add($"(d.title LIKE $t{i} OR d.markdown_body LIKE $t{i} OR a.canonical_name LIKE $t{i})");
            cmd.Parameters.AddWithValue($"$t{i}", $"%{tokens[i]}%");
        }

        var scoredTerms = string.Join(" + ", Enumerable.Range(0, tokens.Count).Select(i => $"(CASE WHEN {termClauses[i]} THEN 1 ELSE 0 END)"));
        var whereTerms = string.Join(" OR ", termClauses);

        cmd.CommandText = $"""
            SELECT
              d.document_id,
              d.anchor_id,
              a.anchor_type,
              a.canonical_name,
              a.parent_anchor_id,
              d.title,
              d.markdown_body,
              d.update_semantics,
              d.domain,
              d.sensitivity,
              d.recall_mode,
              d.confidence,
              d.freshness_at,
              d.created_at,
              d.updated_at,
              ({scoredTerms}) AS token_score
            FROM memory_documents d
            INNER JOIN memory_anchors a ON a.anchor_id = d.anchor_id
            WHERE d.recall_mode = 'auto'
              AND d.sensitivity != 'secret'
              AND d.domain = $domain
              AND d.title != 'turn-completion'
              AND d.update_semantics != 'conversation_trace'
              AND ({whereTerms})
            ORDER BY token_score DESC, d.confidence DESC, d.updated_at DESC
            LIMIT $limit;
            """;
        cmd.Parameters.AddWithValue("$domain", domain);
        cmd.Parameters.AddWithValue("$limit", Math.Max(maxResults, 1));

        var results = new List<SQLiteMemoryDocument>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var anchor = new SQLiteMemoryAnchor(
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.IsDBNull(4) ? null : reader.GetString(4),
                reader.GetString(8),
                reader.GetString(9),
                reader.GetString(10),
                reader.GetDouble(11),
                reader.IsDBNull(12) ? null : reader.GetInt64(12),
                "active",
                reader.GetInt64(13),
                reader.GetInt64(14));

            results.Add(new SQLiteMemoryDocument(
                reader.GetString(0),
                anchor,
                reader.GetString(5),
                reader.GetString(6),
                reader.GetString(7),
                reader.GetString(8),
                reader.GetString(9),
                reader.GetString(10),
                reader.GetDouble(11),
                reader.IsDBNull(12) ? null : reader.GetInt64(12),
                reader.GetInt64(13),
                reader.GetInt64(14)));
        }

        return results;
    }

    private static List<string> TokenizeQuery(string query)
    {
        var tokens = query
            .Split(new[] { ' ', '\t', '\n', '\r', '.', ',', ':', ';', '!', '?', '(', ')', '[', ']', '{', '}', '/', '\\', '"', '\'' },
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(t => t.Trim())
            .Where(t => t.Length >= 3)
            .Select(t => t.ToLowerInvariant())
            .Where(t => !StopWords.Contains(t))
            .Distinct(StringComparer.Ordinal)
            .Take(8)
            .ToList();

        // If the query had no useful tokens after stopword filtering, fall back
        // to best-effort lexical terms so recall doesn't collapse to zero.
        if (tokens.Count > 0)
            return tokens;

        return query
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(t => t.Trim().ToLowerInvariant())
            .Distinct(StringComparer.Ordinal)
            .Take(3)
            .ToList();
    }

    private static readonly HashSet<string> StopWords = new(StringComparer.Ordinal)
    {
        "the", "and", "for", "with", "from", "that", "this", "what", "when", "where", "have", "into", "your", "about", "again"
    };

    public async Task<int> GetPendingCheckpointCountAsync(CancellationToken ct = default)
    {
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM memory_checkpoints WHERE status = 'pending';";
        var value = await cmd.ExecuteScalarAsync(ct);
        return value is long l ? (int)l : Convert.ToInt32(value ?? 0);
    }

    public async Task EnqueueCheckpointAsync(SQLiteMemoryCheckpoint checkpoint, CancellationToken ct = default)
    {
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO memory_checkpoints(
              checkpoint_id, session_id, turn_id, trigger_type, priority,
              status, payload_json, retry_count, created_at, updated_at)
            VALUES($id, $sessionId, $turnId, $triggerType, $priority,
              $status, $payload, $retryCount, $createdAt, $updatedAt)
            ON CONFLICT(checkpoint_id) DO UPDATE SET
              session_id=excluded.session_id,
              turn_id=excluded.turn_id,
              trigger_type=excluded.trigger_type,
              priority=excluded.priority,
              status=excluded.status,
              payload_json=excluded.payload_json,
              retry_count=excluded.retry_count,
              updated_at=excluded.updated_at;
            """;
        cmd.Parameters.AddWithValue("$id", checkpoint.CheckpointId);
        cmd.Parameters.AddWithValue("$sessionId", checkpoint.SessionId);
        cmd.Parameters.AddWithValue("$turnId", (object?)checkpoint.TurnId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$triggerType", checkpoint.TriggerType);
        cmd.Parameters.AddWithValue("$priority", checkpoint.Priority);
        cmd.Parameters.AddWithValue("$status", checkpoint.Status);
        cmd.Parameters.AddWithValue("$payload", checkpoint.PayloadJson);
        cmd.Parameters.AddWithValue("$retryCount", checkpoint.RetryCount);
        cmd.Parameters.AddWithValue("$createdAt", checkpoint.CreatedAtMs);
        cmd.Parameters.AddWithValue("$updatedAt", checkpoint.UpdatedAtMs);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task ResetProcessingCheckpointsAsync(CancellationToken ct = default)
    {
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            UPDATE memory_checkpoints
            SET status = 'pending',
                updated_at = $updatedAt
            WHERE status = 'processing';
            """;
        cmd.Parameters.AddWithValue("$updatedAt", _timeProvider.GetUtcNow().ToUnixTimeMilliseconds());
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<SQLiteMemoryCheckpoint?> LeaseNextPendingCheckpointAsync(CancellationToken ct = default)
    {
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);
        await using var tx = (SqliteTransaction)await conn.BeginTransactionAsync(ct);

        await using var select = conn.CreateCommand();
        select.Transaction = tx;
        select.CommandText = """
            SELECT checkpoint_id, session_id, turn_id, trigger_type, priority,
                   status, payload_json, retry_count, created_at, updated_at
            FROM memory_checkpoints
            WHERE status = 'pending'
            ORDER BY priority DESC, created_at ASC
            LIMIT 1;
            """;

        await using var reader = await select.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
        {
            await tx.CommitAsync(ct);
            return null;
        }

        var checkpoint = new SQLiteMemoryCheckpoint(
            CheckpointId: reader.GetString(0),
            SessionId: reader.GetString(1),
            TurnId: reader.IsDBNull(2) ? null : reader.GetString(2),
            TriggerType: reader.GetString(3),
            Priority: reader.GetInt32(4),
            Status: reader.GetString(5),
            PayloadJson: reader.GetString(6),
            RetryCount: reader.GetInt32(7),
            CreatedAtMs: reader.GetInt64(8),
            UpdatedAtMs: reader.GetInt64(9));

        await reader.CloseAsync();

        await using var update = conn.CreateCommand();
        update.Transaction = tx;
        update.CommandText = """
            UPDATE memory_checkpoints
            SET status = 'processing',
                updated_at = $updatedAt
            WHERE checkpoint_id = $id
              AND status = 'pending';
            """;
        update.Parameters.AddWithValue("$id", checkpoint.CheckpointId);
        update.Parameters.AddWithValue("$updatedAt", _timeProvider.GetUtcNow().ToUnixTimeMilliseconds());

        var updated = await update.ExecuteNonQueryAsync(ct);
        if (updated == 0)
        {
            await tx.RollbackAsync(ct);
            return null;
        }

        await tx.CommitAsync(ct);
        return checkpoint with { Status = "processing" };
    }

    public async Task MarkCheckpointRetryAsync(string checkpointId, int maxRetries, CancellationToken ct = default)
    {
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            UPDATE memory_checkpoints
            SET retry_count = retry_count + 1,
                status = CASE WHEN retry_count + 1 >= $maxRetries THEN 'failed' ELSE 'pending' END,
                updated_at = $updatedAt
            WHERE checkpoint_id = $id;
            """;
        cmd.Parameters.AddWithValue("$id", checkpointId);
        cmd.Parameters.AddWithValue("$maxRetries", maxRetries);
        cmd.Parameters.AddWithValue("$updatedAt", _timeProvider.GetUtcNow().ToUnixTimeMilliseconds());
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<IReadOnlyList<SQLiteMemorySearchResult>> SearchMemoriesAsync(string query, int limit, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(query) || limit <= 0)
            return [];

        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT id, kind, title, body, domain, sensitivity, recall_mode, confidence, sort_ts
            FROM (
                SELECT
                    d.document_id AS id,
                    'document' AS kind,
                    d.title AS title,
                    d.markdown_body AS body,
                    d.domain AS domain,
                    d.sensitivity AS sensitivity,
                    d.recall_mode AS recall_mode,
                    d.confidence AS confidence,
                    d.updated_at AS sort_ts
                FROM memory_documents d
                WHERE d.title LIKE $query OR d.markdown_body LIKE $query

                UNION ALL

                SELECT
                    r.record_id AS id,
                    'record' AS kind,
                    r.record_type AS title,
                    r.payload_json AS body,
                    r.domain AS domain,
                    r.sensitivity AS sensitivity,
                    r.recall_mode AS recall_mode,
                    r.confidence AS confidence,
                    r.created_at AS sort_ts
                FROM memory_records r
                WHERE r.record_type LIKE $query OR r.payload_json LIKE $query
            ) all_memories
            ORDER BY confidence DESC, sort_ts DESC
            LIMIT $limit;
            """;
        cmd.Parameters.AddWithValue("$query", $"%{query}%");
        cmd.Parameters.AddWithValue("$limit", limit);

        var results = new List<SQLiteMemorySearchResult>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var body = reader.GetString(3);
            results.Add(new SQLiteMemorySearchResult(
                Id: reader.GetString(0),
                Kind: reader.GetString(1),
                Title: reader.GetString(2),
                Snippet: body.Length <= 160 ? body : body[..160] + "...",
                Score: reader.GetDouble(7),
                Domain: reader.GetString(4),
                Sensitivity: reader.GetString(5),
                RecallMode: reader.GetString(6)));
        }

        return results;
    }

    public async Task<IReadOnlyList<SQLiteMemoryHydratedItem>> GetMemoriesByIdsAsync(IReadOnlyList<string> ids, CancellationToken ct = default)
    {
        if (ids.Count == 0)
            return [];

        var documents = ids
            .Select(ParseTypedId)
            .Where(x => x.Kind is "document" or "unknown")
            .Select(x => x.Id)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var records = ids
            .Select(ParseTypedId)
            .Where(x => x.Kind is "record" or "unknown")
            .Select(x => x.Id)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var output = new List<SQLiteMemoryHydratedItem>();

        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);

        foreach (var id in documents)
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                SELECT document_id, title, markdown_body, domain, sensitivity, recall_mode, update_semantics, updated_at
                FROM memory_documents
                WHERE document_id = $id;
                """;
            cmd.Parameters.AddWithValue("$id", id);
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            if (await reader.ReadAsync(ct))
            {
                output.Add(new SQLiteMemoryHydratedItem(
                    Id: reader.GetString(0),
                    Kind: "document",
                    Title: reader.GetString(1),
                    Content: reader.GetString(2),
                    Domain: reader.GetString(3),
                    Sensitivity: reader.GetString(4),
                    RecallMode: reader.GetString(5),
                    UpdateSemantics: reader.GetString(6),
                    UpdatedAtMs: reader.GetInt64(7)));
            }
        }

        foreach (var id in records)
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                SELECT record_id, record_type, payload_json, domain, sensitivity, recall_mode, update_semantics, created_at
                FROM memory_records
                WHERE record_id = $id;
                """;
            cmd.Parameters.AddWithValue("$id", id);
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            if (await reader.ReadAsync(ct))
            {
                output.Add(new SQLiteMemoryHydratedItem(
                    Id: reader.GetString(0),
                    Kind: "record",
                    Title: reader.GetString(1),
                    Content: reader.GetString(2),
                    Domain: reader.GetString(3),
                    Sensitivity: reader.GetString(4),
                    RecallMode: reader.GetString(5),
                    UpdateSemantics: reader.GetString(6),
                    UpdatedAtMs: reader.GetInt64(7)));
            }
        }

        return output;
    }

    public async Task<bool> UpdateDocumentTextAsync(string documentId, string oldText, string newText, CancellationToken ct = default)
    {
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);

        await using var read = conn.CreateCommand();
        read.CommandText = "SELECT markdown_body FROM memory_documents WHERE document_id = $id;";
        read.Parameters.AddWithValue("$id", documentId);
        var current = (string?)await read.ExecuteScalarAsync(ct);
        if (current is null)
            return false;

        if (!current.Contains(oldText, StringComparison.Ordinal))
            return false;

        var updated = current.Replace(oldText, newText, StringComparison.Ordinal);

        await using var write = conn.CreateCommand();
        write.CommandText = """
            UPDATE memory_documents
            SET markdown_body = $body,
                updated_at = $updatedAt
            WHERE document_id = $id;
            """;
        write.Parameters.AddWithValue("$id", documentId);
        write.Parameters.AddWithValue("$body", updated);
        write.Parameters.AddWithValue("$updatedAt", _timeProvider.GetUtcNow().ToUnixTimeMilliseconds());
        var affected = await write.ExecuteNonQueryAsync(ct);
        return affected > 0;
    }

    public async Task<bool> TombstoneDocumentAsync(string documentId, CancellationToken ct = default)
    {
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            UPDATE memory_documents
            SET update_semantics = 'tombstone',
                recall_mode = 'never',
                updated_at = $updatedAt
            WHERE document_id = $id;
            """;
        cmd.Parameters.AddWithValue("$id", documentId);
        cmd.Parameters.AddWithValue("$updatedAt", _timeProvider.GetUtcNow().ToUnixTimeMilliseconds());
        var affected = await cmd.ExecuteNonQueryAsync(ct);
        return affected > 0;
    }

    public async Task<bool> SupersedeRecordAsync(string recordId, string payloadJson, CancellationToken ct = default)
    {
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);

        await using var read = conn.CreateCommand();
        read.CommandText = """
            SELECT anchor_id, record_type, domain, sensitivity, recall_mode, confidence, freshness_at
            FROM memory_records
            WHERE record_id = $id;
            """;
        read.Parameters.AddWithValue("$id", recordId);

        await using var reader = await read.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
            return false;

        var anchorId = reader.GetString(0);
        var recordType = reader.GetString(1);
        var domain = reader.GetString(2);
        var sensitivity = reader.GetString(3);
        var recallMode = reader.GetString(4);
        var confidence = reader.GetDouble(5);
        var freshnessAt = reader.IsDBNull(6) ? (long?)null : reader.GetInt64(6);
        await reader.CloseAsync();

        var now = _timeProvider.GetUtcNow().ToUnixTimeMilliseconds();
        var newId = $"rec-{Guid.NewGuid():N}";

        await using var insert = conn.CreateCommand();
        insert.CommandText = """
            INSERT INTO memory_records(
              record_id, anchor_id, record_type, payload_json, supersedes_record_id,
              update_semantics, domain, sensitivity, recall_mode, confidence, freshness_at, created_at)
            VALUES($id, $anchorId, $recordType, $payload, $supersedes,
              'supersede-record', $domain, $sensitivity, $recallMode, $confidence, $freshnessAt, $createdAt);
            """;
        insert.Parameters.AddWithValue("$id", newId);
        insert.Parameters.AddWithValue("$anchorId", anchorId);
        insert.Parameters.AddWithValue("$recordType", recordType);
        insert.Parameters.AddWithValue("$payload", payloadJson);
        insert.Parameters.AddWithValue("$supersedes", recordId);
        insert.Parameters.AddWithValue("$domain", domain);
        insert.Parameters.AddWithValue("$sensitivity", sensitivity);
        insert.Parameters.AddWithValue("$recallMode", recallMode);
        insert.Parameters.AddWithValue("$confidence", confidence);
        insert.Parameters.AddWithValue("$freshnessAt", (object?)freshnessAt ?? DBNull.Value);
        insert.Parameters.AddWithValue("$createdAt", now);
        await insert.ExecuteNonQueryAsync(ct);
        return true;
    }

    public async Task<bool> TombstoneRecordAsync(string recordId, CancellationToken ct = default)
    {
        return await SupersedeRecordAsync(recordId, "{\"status\":\"tombstone\"}", ct);
    }

    public async Task ApplyCurationBatchAsync(
        string checkpointId,
        IReadOnlyList<SQLiteMemoryCurationOperation> operations,
        CancellationToken ct = default)
    {
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);
        await using var tx = (SqliteTransaction)await conn.BeginTransactionAsync(ct);

        foreach (var operation in operations)
        {
            var now = _timeProvider.GetUtcNow().ToUnixTimeMilliseconds();
            var canonicalName = string.IsNullOrWhiteSpace(operation.AnchorCanonicalName)
                ? operation.Title
                : operation.AnchorCanonicalName;
            var anchor = CreateDefaultAnchor(canonicalName, operation.Domain) with
            {
                AnchorType = operation.AnchorType,
                Sensitivity = operation.Sensitivity,
                RecallMode = operation.RecallMode,
                Confidence = operation.Confidence,
                FreshnessAtMs = now,
                CreatedAtMs = now,
                UpdatedAtMs = now
            };

            await EnsureAnchorAsync(conn, tx, anchor, ct);

            if (operation.Kind == "record")
            {
                await using var recordCmd = conn.CreateCommand();
                recordCmd.Transaction = tx;
                recordCmd.CommandText = """
                    INSERT INTO memory_records(
                      record_id, anchor_id, record_type, payload_json, supersedes_record_id,
                      update_semantics, domain, sensitivity, recall_mode, confidence,
                      freshness_at, created_at)
                    VALUES($id, $anchorId, $recordType, $payloadJson, $supersedes,
                      $semantics, $domain, $sensitivity, $recallMode, $confidence,
                      $freshnessAt, $createdAt);
                    """;
                recordCmd.Parameters.AddWithValue("$id", string.IsNullOrWhiteSpace(operation.MemoryId) ? $"rec-{Guid.NewGuid():N}" : operation.MemoryId);
                recordCmd.Parameters.AddWithValue("$anchorId", anchor.AnchorId);
                recordCmd.Parameters.AddWithValue("$recordType", operation.Title);
                recordCmd.Parameters.AddWithValue("$payloadJson", operation.Content);
                recordCmd.Parameters.AddWithValue("$supersedes", (object?)operation.SupersedesRecordId ?? DBNull.Value);
                recordCmd.Parameters.AddWithValue("$semantics", operation.UpdateSemantics);
                recordCmd.Parameters.AddWithValue("$domain", operation.Domain);
                recordCmd.Parameters.AddWithValue("$sensitivity", operation.Sensitivity);
                recordCmd.Parameters.AddWithValue("$recallMode", operation.RecallMode);
                recordCmd.Parameters.AddWithValue("$confidence", operation.Confidence);
                recordCmd.Parameters.AddWithValue("$freshnessAt", (object?)operation.FreshnessAtMs ?? DBNull.Value);
                recordCmd.Parameters.AddWithValue("$createdAt", now);
                await recordCmd.ExecuteNonQueryAsync(ct);
                continue;
            }

            var resolvedRecallMode = string.Equals(operation.MemoryClass, "conversation_trace", StringComparison.OrdinalIgnoreCase)
                ? "never"
                : operation.RecallMode;

            await using var documentCmd = conn.CreateCommand();
            documentCmd.Transaction = tx;
            documentCmd.CommandText = """
                INSERT INTO memory_documents(
                  document_id, anchor_id, title, markdown_body, update_semantics,
                  domain, sensitivity, recall_mode, confidence, freshness_at,
                  created_at, updated_at)
                VALUES($id, $anchorId, $title, $body, $semantics,
                  $domain, $sensitivity, $recallMode, $confidence, $freshnessAt,
                  $createdAt, $updatedAt)
                ON CONFLICT(document_id) DO UPDATE SET
                  title=excluded.title,
                  markdown_body=excluded.markdown_body,
                  update_semantics=excluded.update_semantics,
                  domain=excluded.domain,
                  sensitivity=excluded.sensitivity,
                  recall_mode=excluded.recall_mode,
                  confidence=excluded.confidence,
                  freshness_at=excluded.freshness_at,
                  updated_at=excluded.updated_at;
                """;
            documentCmd.Parameters.AddWithValue("$id", string.IsNullOrWhiteSpace(operation.MemoryId) ? $"doc-{Guid.NewGuid():N}" : operation.MemoryId);
            documentCmd.Parameters.AddWithValue("$anchorId", anchor.AnchorId);
            documentCmd.Parameters.AddWithValue("$title", operation.Title);
            documentCmd.Parameters.AddWithValue("$body", operation.Content);
            documentCmd.Parameters.AddWithValue("$semantics", operation.UpdateSemantics);
            documentCmd.Parameters.AddWithValue("$domain", operation.Domain);
            documentCmd.Parameters.AddWithValue("$sensitivity", operation.Sensitivity);
            documentCmd.Parameters.AddWithValue("$recallMode", resolvedRecallMode);
            documentCmd.Parameters.AddWithValue("$confidence", operation.Confidence);
            documentCmd.Parameters.AddWithValue("$freshnessAt", (object?)operation.FreshnessAtMs ?? DBNull.Value);
            documentCmd.Parameters.AddWithValue("$createdAt", now);
            documentCmd.Parameters.AddWithValue("$updatedAt", now);
            await documentCmd.ExecuteNonQueryAsync(ct);
        }

        await using var markDone = conn.CreateCommand();
        markDone.Transaction = tx;
        markDone.CommandText = """
            UPDATE memory_checkpoints
            SET status = 'completed',
                updated_at = $updatedAt
            WHERE checkpoint_id = $id;
            """;
        markDone.Parameters.AddWithValue("$id", checkpointId);
        markDone.Parameters.AddWithValue("$updatedAt", _timeProvider.GetUtcNow().ToUnixTimeMilliseconds());
        await markDone.ExecuteNonQueryAsync(ct);

        await tx.CommitAsync(ct);
    }

    private static (string Kind, string Id) ParseTypedId(string raw)
    {
        if (raw.StartsWith("doc:", StringComparison.OrdinalIgnoreCase))
            return ("document", raw[4..]);
        if (raw.StartsWith("rec:", StringComparison.OrdinalIgnoreCase))
            return ("record", raw[4..]);
        return ("unknown", raw);
    }

    private static async Task EnsureAnchorAsync(
        SqliteConnection conn,
        SqliteTransaction tx,
        SQLiteMemoryAnchor anchor,
        CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            INSERT INTO memory_anchors(
              anchor_id, anchor_type, canonical_name, parent_anchor_id,
              domain, sensitivity, recall_mode, confidence, freshness_at,
              status, created_at, updated_at)
            VALUES($id, $type, $name, $parent,
              $domain, $sensitivity, $recallMode, $confidence, $freshnessAt,
              $status, $createdAt, $updatedAt)
            ON CONFLICT(anchor_id) DO UPDATE SET
              anchor_type=excluded.anchor_type,
              canonical_name=excluded.canonical_name,
              parent_anchor_id=excluded.parent_anchor_id,
              domain=excluded.domain,
              sensitivity=excluded.sensitivity,
              recall_mode=excluded.recall_mode,
              confidence=excluded.confidence,
              freshness_at=excluded.freshness_at,
              status=excluded.status,
              updated_at=excluded.updated_at;
            """;
        cmd.Parameters.AddWithValue("$id", anchor.AnchorId);
        cmd.Parameters.AddWithValue("$type", anchor.AnchorType);
        cmd.Parameters.AddWithValue("$name", anchor.CanonicalName);
        cmd.Parameters.AddWithValue("$parent", (object?)anchor.ParentAnchorId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$domain", anchor.Domain);
        cmd.Parameters.AddWithValue("$sensitivity", anchor.Sensitivity);
        cmd.Parameters.AddWithValue("$recallMode", anchor.RecallMode);
        cmd.Parameters.AddWithValue("$confidence", anchor.Confidence);
        cmd.Parameters.AddWithValue("$freshnessAt", (object?)anchor.FreshnessAtMs ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$status", anchor.Status);
        cmd.Parameters.AddWithValue("$createdAt", anchor.CreatedAtMs);
        cmd.Parameters.AddWithValue("$updatedAt", anchor.UpdatedAtMs);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public SQLiteMemoryAnchor CreateDefaultAnchor(string canonicalName, string domain = "project:default")
    {
        var nowMs = _timeProvider.GetUtcNow().ToUnixTimeMilliseconds();
        return new SQLiteMemoryAnchor(
            AnchorId: $"anchor:{canonicalName.Trim().ToLowerInvariant().Replace(' ', '-')}",
            AnchorType: "concept",
            CanonicalName: canonicalName,
            ParentAnchorId: null,
            Domain: domain,
            Sensitivity: "normal",
            RecallMode: "auto",
            Confidence: 0.8,
            FreshnessAtMs: nowMs,
            Status: "active",
            CreatedAtMs: nowMs,
            UpdatedAtMs: nowMs);
    }
}

public sealed record SQLiteMemoryAnchor(
    string AnchorId,
    string AnchorType,
    string CanonicalName,
    string? ParentAnchorId,
    string Domain,
    string Sensitivity,
    string RecallMode,
    double Confidence,
    long? FreshnessAtMs,
    string Status,
    long CreatedAtMs,
    long UpdatedAtMs);

public sealed record SQLiteMemoryDocument(
    string DocumentId,
    SQLiteMemoryAnchor Anchor,
    string Title,
    string MarkdownBody,
    string UpdateSemantics,
    string Domain,
    string Sensitivity,
    string RecallMode,
    double Confidence,
    long? FreshnessAtMs,
    long CreatedAtMs,
    long UpdatedAtMs);

public sealed record SQLiteMemoryCheckpoint(
    string CheckpointId,
    string SessionId,
    string? TurnId,
    string TriggerType,
    int Priority,
    string Status,
    string PayloadJson,
    int RetryCount,
    long CreatedAtMs,
    long UpdatedAtMs);

public sealed record SQLiteMemorySearchResult(
    string Id,
    string Kind,
    string Title,
    string Snippet,
    double Score,
    string Domain,
    string Sensitivity,
    string RecallMode);

public sealed record SQLiteMemoryHydratedItem(
    string Id,
    string Kind,
    string Title,
    string Content,
    string Domain,
    string Sensitivity,
    string RecallMode,
    string UpdateSemantics,
    long UpdatedAtMs);

public sealed record SQLiteMemoryCurationOperation(
    string Kind,
    string MemoryClass,
    string? MemoryId,
    string AnchorCanonicalName,
    string AnchorType,
    string Title,
    string Content,
    string UpdateSemantics,
    string Domain,
    string Sensitivity,
    string RecallMode,
    double Confidence,
    long? FreshnessAtMs,
    string? SupersedesRecordId = null);
