using Microsoft.Data.Sqlite;
using Netclaw.Actors.Text;
using Netclaw.Configuration;

namespace Netclaw.Actors.Memory;

/// <summary>
/// SQLite-backed durable memory store with a minimal graph/policy schema.
/// </summary>
public sealed class SQLiteMemoryStore
{
    private const string MissingMetadataFacet = "needs_metadata_enrichment";
    private readonly string _connectionString;
    private readonly TimeProvider _timeProvider;

    public SQLiteMemoryStore(string sqlitePath, TimeProvider timeProvider)
    {
        _connectionString = new SqliteConnectionStringBuilder { DataSource = sqlitePath }.ToString();
        _timeProvider = timeProvider;
    }

    public async Task InitializeAsync(CancellationToken ct = default)
    {
        await WithConnectionAsync(async (conn, ct) =>
        {
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
              memory_class TEXT NOT NULL DEFAULT 'durable_fact',
              title TEXT NOT NULL,
              markdown_body TEXT NOT NULL,
              aliases_json TEXT NULL,
              facets_json TEXT NULL,
              slots_json TEXT NULL,
              update_semantics TEXT NOT NULL,
              domain TEXT NOT NULL,
              boundary TEXT NULL,
              audience TEXT NULL,
              sensitivity TEXT NOT NULL,
              recall_mode TEXT NOT NULL,
              confidence REAL NOT NULL,
              freshness_at INTEGER NULL,
              expires_at INTEGER NULL,
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
              memory_class TEXT NOT NULL DEFAULT 'evidence',
              record_type TEXT NOT NULL,
              payload_json TEXT NOT NULL,
              aliases_json TEXT NULL,
              facets_json TEXT NULL,
              slots_json TEXT NULL,
              supersedes_record_id TEXT NULL,
              update_semantics TEXT NOT NULL,
              domain TEXT NOT NULL,
              boundary TEXT NULL,
              audience TEXT NULL,
              sensitivity TEXT NOT NULL,
              recall_mode TEXT NOT NULL,
              confidence REAL NOT NULL,
              freshness_at INTEGER NULL,
              expires_at INTEGER NULL,
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

        await EnsureColumnExistsAsync(conn, "memory_documents", "memory_class", "TEXT NOT NULL DEFAULT 'durable_fact'", ct);
        await EnsureColumnExistsAsync(conn, "memory_documents", "expires_at", "INTEGER NULL", ct);
        await EnsureColumnExistsAsync(conn, "memory_documents", "aliases_json", "TEXT NULL", ct);
        await EnsureColumnExistsAsync(conn, "memory_documents", "facets_json", "TEXT NULL", ct);
        await EnsureColumnExistsAsync(conn, "memory_documents", "slots_json", "TEXT NULL", ct);
        await EnsureColumnExistsAsync(conn, "memory_documents", "boundary", "TEXT NULL", ct);
        await EnsureColumnExistsAsync(conn, "memory_documents", "audience", "TEXT NULL", ct);
        await EnsureColumnExistsAsync(conn, "memory_records", "memory_class", "TEXT NOT NULL DEFAULT 'evidence'", ct);
        await EnsureColumnExistsAsync(conn, "memory_records", "expires_at", "INTEGER NULL", ct);
        await EnsureColumnExistsAsync(conn, "memory_records", "aliases_json", "TEXT NULL", ct);
        await EnsureColumnExistsAsync(conn, "memory_records", "facets_json", "TEXT NULL", ct);
        await EnsureColumnExistsAsync(conn, "memory_records", "slots_json", "TEXT NULL", ct);
        await EnsureColumnExistsAsync(conn, "memory_records", "boundary", "TEXT NULL", ct);
        await EnsureColumnExistsAsync(conn, "memory_records", "audience", "TEXT NULL", ct);

        await NormalizeLegacyBoundariesAsync(conn, ct);

        // Phase A hygiene: conversation turn snapshots are diagnostic trace, not
        // durable auto-recall memory. This repo is prototype-only; normalize any
        // existing rows aggressively to prevent recall pollution.
        await using var hygieneCmd = conn.CreateCommand();
        hygieneCmd.CommandText = $"""
            UPDATE memory_documents
            SET recall_mode = '{MemoryRecallMode.Never.ToWireValue()}'
            WHERE title = 'turn-completion'
               OR update_semantics = '{MemoryUpdateSemantics.ConversationTrace.ToWireValue()}';
            """;
        await hygieneCmd.ExecuteNonQueryAsync(ct);

        await using var metadataCmd = conn.CreateCommand();
        metadataCmd.CommandText = $"""
            UPDATE memory_documents
            SET recall_mode = '{MemoryRecallMode.Searchable.ToWireValue()}',
                facets_json = CASE
                    WHEN facets_json IS NULL OR TRIM(facets_json) = '' THEN '[\"{MissingMetadataFacet}\"]'
                    ELSE facets_json
                END
            WHERE memory_class = '{MemoryClass.DurableFact.ToWireValue()}'
              AND recall_mode = '{MemoryRecallMode.Auto.ToWireValue()}'
              AND (
                    title LIKE 'doc:%'
                 OR aliases_json IS NULL
                 OR facets_json IS NULL
              );
            """;
        await metadataCmd.ExecuteNonQueryAsync(ct);

        // FTS5 full-text search indexes — rebuilt on every startup to stay in sync.
        // Runtime mutations INSERT new rows; stale phantoms are cleaned up here.
        await using var ftsRebuild = conn.CreateCommand();
        ftsRebuild.CommandText = $"""
            DROP TABLE IF EXISTS memory_documents_fts;
            DROP TABLE IF EXISTS memory_records_fts;

            CREATE VIRTUAL TABLE memory_documents_fts USING fts5(
                document_id UNINDEXED,
                title, body, aliases, facets,
                tokenize='porter unicode61 remove_diacritics 2'
            );

            CREATE VIRTUAL TABLE memory_records_fts USING fts5(
                record_id UNINDEXED,
                title, body, aliases, facets,
                tokenize='porter unicode61 remove_diacritics 2'
            );

            INSERT INTO memory_documents_fts(document_id, title, body, aliases, facets)
            SELECT document_id, title, markdown_body,
                   COALESCE(aliases_json, ''), COALESCE(facets_json, '')
            FROM memory_documents
            WHERE recall_mode IN ('{MemoryRecallMode.Auto.ToWireValue()}', '{MemoryRecallMode.Searchable.ToWireValue()}');

            INSERT INTO memory_records_fts(record_id, title, body, aliases, facets)
            SELECT record_id, record_type, payload_json,
                   COALESCE(aliases_json, ''), COALESCE(facets_json, '')
            FROM memory_records
            WHERE recall_mode IN ('{MemoryRecallMode.Auto.ToWireValue()}', '{MemoryRecallMode.Searchable.ToWireValue()}');
            """;
        await ftsRebuild.ExecuteNonQueryAsync(ct);
        }, ct);
    }

    public async Task UpsertDocumentAsync(SQLiteMemoryDocument document, CancellationToken ct = default)
    {
        await WithConnectionAsync(async (conn, ct) =>
        {
        await using var tx = (SqliteTransaction)await conn.BeginTransactionAsync(ct);

        await EnsureAnchorAsync(conn, tx, document.Anchor, ct);

        var resolvedBoundary = string.Equals(document.Boundary, SecurityPolicyDefaults.LegacyRestrictedBoundary, StringComparison.Ordinal)
            ? SecurityPolicyDefaults.InferLegacyBoundaryFromDomain(document.Domain)
            : document.Boundary;

        await using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            INSERT INTO memory_documents(
              document_id, anchor_id, memory_class, title, markdown_body, aliases_json, facets_json, slots_json, update_semantics,
              domain, boundary, audience, sensitivity, recall_mode, confidence, freshness_at,
              expires_at, created_at, updated_at)
            VALUES($id, $anchorId, $memoryClass, $title, $body, $aliasesJson, $facetsJson, $slotsJson, $semantics,
              $domain, $boundary, $audience, $sensitivity, $recallMode, $confidence, $freshnessAt,
              $expiresAt, $createdAt, $updatedAt)
            ON CONFLICT(document_id) DO UPDATE SET
              memory_class=excluded.memory_class,
              title=excluded.title,
              markdown_body=excluded.markdown_body,
              aliases_json=excluded.aliases_json,
              facets_json=excluded.facets_json,
              slots_json=excluded.slots_json,
              update_semantics=excluded.update_semantics,
              domain=excluded.domain,
              boundary=excluded.boundary,
              audience=excluded.audience,
              sensitivity=excluded.sensitivity,
              recall_mode=excluded.recall_mode,
              confidence=excluded.confidence,
              freshness_at=excluded.freshness_at,
              expires_at=excluded.expires_at,
              updated_at=excluded.updated_at;
            """;
        cmd.Parameters.AddWithValue("$id", document.DocumentId);
        cmd.Parameters.AddWithValue("$anchorId", document.Anchor.AnchorId);
        cmd.Parameters.AddWithValue("$memoryClass", document.MemoryClass);
        cmd.Parameters.AddWithValue("$title", document.Title);
        cmd.Parameters.AddWithValue("$body", document.MarkdownBody);
        cmd.Parameters.AddWithValue("$aliasesJson", (object?)document.AliasesJson ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$facetsJson", (object?)document.FacetsJson ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$slotsJson", (object?)document.SlotsJson ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$semantics", document.UpdateSemantics);
        cmd.Parameters.AddWithValue("$domain", document.Domain);
        cmd.Parameters.AddWithValue("$boundary", resolvedBoundary);
        cmd.Parameters.AddWithValue("$audience", document.Audience);
        cmd.Parameters.AddWithValue("$sensitivity", document.Sensitivity);
        cmd.Parameters.AddWithValue("$recallMode", document.RecallMode);
        cmd.Parameters.AddWithValue("$confidence", document.Confidence);
        cmd.Parameters.AddWithValue("$freshnessAt", (object?)document.FreshnessAtMs ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$expiresAt", (object?)document.ExpiresAtMs ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$createdAt", document.CreatedAtMs);
        cmd.Parameters.AddWithValue("$updatedAt", document.UpdatedAtMs);
        await cmd.ExecuteNonQueryAsync(ct);

        if (IsSearchableRecallMode(document.RecallMode))
            await InsertDocumentFtsAsync(conn, tx, document.DocumentId, document.Title, document.MarkdownBody, document.AliasesJson, document.FacetsJson, ct);

        await tx.CommitAsync(ct);
        }, ct);
    }

    public async Task<IReadOnlyList<SQLiteMemoryDocument>> SearchAutoRecallDocumentsAsync(
        string query,
        string domain,
        int maxResults,
        string? boundary = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(query) || maxResults <= 0)
            return [];

        var tokens = TextTokenizer.Tokenize(query);
        var matchQuery = BuildFtsMatchQuery(tokens);
        if (matchQuery is null)
            return [];

        return await WithConnectionAsync(async (conn, ct) =>
        {
        var limit = Math.Max(maxResults, 1);
        await using var cmd = conn.CreateCommand();

        cmd.CommandText = $"""
            WITH doc_hits AS (
                SELECT document_id, bm25(memory_documents_fts, 10.0, 1.0, 5.0, 3.0) AS fts_rank
                FROM memory_documents_fts
                WHERE memory_documents_fts MATCH $query
                ORDER BY fts_rank
                LIMIT $overfetch
            )
            SELECT
              d.document_id,
              d.anchor_id,
              a.anchor_type,
              a.canonical_name,
              a.parent_anchor_id,
              d.memory_class,
              d.title,
              d.markdown_body,
              d.aliases_json,
              d.facets_json,
              d.slots_json,
              d.update_semantics,
              d.domain,
              d.boundary,
              d.audience,
              d.sensitivity,
              d.recall_mode,
              d.confidence,
              d.freshness_at,
              d.expires_at,
              d.created_at,
              d.updated_at
            FROM doc_hits dh
            JOIN memory_documents d ON d.document_id = dh.document_id
            INNER JOIN memory_anchors a ON a.anchor_id = d.anchor_id
            WHERE d.recall_mode = '{MemoryRecallMode.Auto.ToWireValue()}'
              AND d.sensitivity != '{MemorySensitivity.Secret.ToWireValue()}'
              AND d.domain = $domain
              AND ($boundary IS NULL OR COALESCE(d.boundary, '{SecurityPolicyDefaults.LegacyRestrictedBoundary}') = $boundary)
              AND (d.expires_at IS NULL OR d.expires_at > $now)
              AND d.title != 'turn-completion'
              AND d.update_semantics != '{MemoryUpdateSemantics.ConversationTrace.ToWireValue()}'
            ORDER BY dh.fts_rank, d.confidence DESC, d.updated_at DESC
            LIMIT $limit;
            """;
        cmd.Parameters.AddWithValue("$query", matchQuery);
        cmd.Parameters.AddWithValue("$domain", domain);
        cmd.Parameters.AddWithValue("$boundary", (object?)boundary ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$now", _timeProvider.GetUtcNow().ToUnixTimeMilliseconds());
        cmd.Parameters.AddWithValue("$overfetch", limit * 5);
        cmd.Parameters.AddWithValue("$limit", limit);

        var results = new List<SQLiteMemoryDocument>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var anchor = new SQLiteMemoryAnchor(
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.IsDBNull(4) ? null : reader.GetString(4),
                reader.GetString(12),
                reader.GetString(15),
                reader.GetString(16),
                reader.GetDouble(17),
                reader.IsDBNull(18) ? null : reader.GetInt64(18),
                "active",
                reader.GetInt64(20),
                reader.GetInt64(21));

            results.Add(new SQLiteMemoryDocument(
                DocumentId: reader.GetString(0),
                Anchor: anchor,
                MemoryClass: reader.GetString(5),
                Title: reader.GetString(6),
                MarkdownBody: reader.GetString(7),
                AliasesJson: reader.IsDBNull(8) ? null : reader.GetString(8),
                FacetsJson: reader.IsDBNull(9) ? null : reader.GetString(9),
                SlotsJson: reader.IsDBNull(10) ? null : reader.GetString(10),
                UpdateSemantics: reader.GetString(11),
                Domain: reader.GetString(12),
                Boundary: reader.IsDBNull(13) ? SecurityPolicyDefaults.LegacyRestrictedBoundary : reader.GetString(13),
                Audience: reader.IsDBNull(14) ? TrustAudience.Public.ToWireValue() : reader.GetString(14),
                Sensitivity: reader.GetString(15),
                RecallMode: reader.GetString(16),
                Confidence: reader.GetDouble(17),
                FreshnessAtMs: reader.IsDBNull(18) ? null : reader.GetInt64(18),
                ExpiresAtMs: reader.IsDBNull(19) ? null : reader.GetInt64(19),
                CreatedAtMs: reader.GetInt64(20),
                UpdatedAtMs: reader.GetInt64(21)));
        }

        return results;
        }, ct);
    }

    public async Task<int> GetPendingCheckpointCountAsync(CancellationToken ct = default)
    {
        return await WithConnectionAsync(async (conn, ct) =>
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*) FROM memory_checkpoints WHERE status = 'pending';";
            var value = await cmd.ExecuteScalarAsync(ct);
            return value is long l ? (int)l : Convert.ToInt32(value ?? 0);
        }, ct);
    }

    public sealed record MemoryStats(
        int AnchorCount,
        int DocumentCount,
        int RecordCount,
        int EdgeCount,
        int PendingCheckpoints);

    public async Task<MemoryStats> GetStatsAsync(CancellationToken ct = default)
    {
        return await WithConnectionAsync(async (conn, ct) =>
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                SELECT
                    (SELECT COUNT(*) FROM memory_anchors),
                    (SELECT COUNT(*) FROM memory_documents),
                    (SELECT COUNT(*) FROM memory_records),
                    (SELECT COUNT(*) FROM memory_edges),
                    (SELECT COUNT(*) FROM memory_checkpoints WHERE status = 'pending')
                """;
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            if (await reader.ReadAsync(ct))
            {
                return new MemoryStats(
                    AnchorCount: reader.GetInt32(0),
                    DocumentCount: reader.GetInt32(1),
                    RecordCount: reader.GetInt32(2),
                    EdgeCount: reader.GetInt32(3),
                    PendingCheckpoints: reader.GetInt32(4));
            }

            return new MemoryStats(0, 0, 0, 0, 0);
        }, ct);
    }

    public async Task EnqueueCheckpointAsync(SQLiteMemoryCheckpoint checkpoint, CancellationToken ct = default)
    {
        await WithConnectionAsync(async (conn, ct) =>
        {
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
        }, ct);
    }

    public async Task ResetProcessingCheckpointsAsync(CancellationToken ct = default)
    {
        await WithConnectionAsync(async (conn, ct) =>
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                UPDATE memory_checkpoints
                SET status = 'pending',
                    updated_at = $updatedAt
                WHERE status = 'processing';
                """;
            cmd.Parameters.AddWithValue("$updatedAt", _timeProvider.GetUtcNow().ToUnixTimeMilliseconds());
            await cmd.ExecuteNonQueryAsync(ct);
        }, ct);
    }

    public async Task<SQLiteMemoryCheckpoint?> LeaseNextPendingCheckpointAsync(CancellationToken ct = default)
    {
        return await WithConnectionAsync(async (conn, ct) =>
        {
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
        }, ct);
    }

    public async Task MarkCheckpointRetryAsync(string checkpointId, int maxRetries, CancellationToken ct = default)
    {
        await WithConnectionAsync(async (conn, ct) =>
        {
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
        }, ct);
    }

    public async Task<IReadOnlyList<SQLiteMemorySearchResult>> SearchMemoriesAsync(string query, int limit, string boundary, TrustAudience audience = TrustAudience.Public, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(query) || limit <= 0)
            return [];

        var tokens = TextTokenizer.Tokenize(query);
        var matchQuery = BuildFtsMatchQuery(tokens);
        if (matchQuery is null)
            return [];

        return await WithConnectionAsync(async (conn, ct) =>
        {
        await using var cmd = conn.CreateCommand();
        var allowedAudiences = MemoryPolicyEvaluator.AllowedAudienceWireValues(audience);
        var audienceClauses = new List<string>();
        for (var i = 0; i < allowedAudiences.Count; i++)
        {
            audienceClauses.Add($"$a{i}");
            cmd.Parameters.AddWithValue($"$a{i}", allowedAudiences[i]);
        }

        var whereAudiences = string.Join(",", audienceClauses);

        cmd.CommandText = $"""
            WITH doc_hits AS (
                SELECT document_id, bm25(memory_documents_fts, 10.0, 1.0, 5.0, 3.0) AS fts_rank
                FROM memory_documents_fts
                WHERE memory_documents_fts MATCH $query
                ORDER BY fts_rank
                LIMIT $overfetch
            ),
            rec_hits AS (
                SELECT record_id, bm25(memory_records_fts, 10.0, 1.0, 5.0, 3.0) AS fts_rank
                FROM memory_records_fts
                WHERE memory_records_fts MATCH $query
                ORDER BY fts_rank
                LIMIT $overfetch
            )
            SELECT id, kind, memory_class, title, body, domain, boundary, audience, sensitivity, recall_mode, confidence, sort_ts
            FROM (
                SELECT
                    d.document_id AS id, 'document' AS kind,
                    d.memory_class, d.title, d.markdown_body AS body,
                    d.domain, COALESCE(d.boundary, $legacyBoundary) AS boundary,
                    COALESCE(d.audience, $fallbackAudience) AS audience,
                    d.sensitivity, d.recall_mode, d.confidence,
                    d.updated_at AS sort_ts, dh.fts_rank
                FROM doc_hits dh
                JOIN memory_documents d ON d.document_id = dh.document_id
                WHERE d.recall_mode IN ('{MemoryRecallMode.Auto.ToWireValue()}', '{MemoryRecallMode.Searchable.ToWireValue()}')
                  AND COALESCE(d.boundary, $legacyBoundary) = $boundary
                  AND COALESCE(d.audience, $fallbackAudience) IN ({whereAudiences})

                UNION ALL

                SELECT
                    r.record_id AS id, 'record' AS kind,
                    r.memory_class, r.record_type AS title, r.payload_json AS body,
                    r.domain, COALESCE(r.boundary, $legacyBoundary) AS boundary,
                    COALESCE(r.audience, $fallbackAudience) AS audience,
                    r.sensitivity, r.recall_mode, r.confidence,
                    r.created_at AS sort_ts, rh.fts_rank
                FROM rec_hits rh
                JOIN memory_records r ON r.record_id = rh.record_id
                WHERE r.recall_mode IN ('{MemoryRecallMode.Auto.ToWireValue()}', '{MemoryRecallMode.Searchable.ToWireValue()}')
                  AND COALESCE(r.boundary, $legacyBoundary) = $boundary
                  AND COALESCE(r.audience, $fallbackAudience) IN ({whereAudiences})
            ) all_memories
            ORDER BY fts_rank, confidence DESC, sort_ts DESC
            LIMIT $limit;
            """;
        cmd.Parameters.AddWithValue("$query", matchQuery);
        cmd.Parameters.AddWithValue("$boundary", boundary);
        cmd.Parameters.AddWithValue("$legacyBoundary", SecurityPolicyDefaults.LegacyRestrictedBoundary);
        cmd.Parameters.AddWithValue("$fallbackAudience", audience.ToWireValue());
        cmd.Parameters.AddWithValue("$overfetch", limit * 5);
        cmd.Parameters.AddWithValue("$limit", limit);

        var results = new List<SQLiteMemorySearchResult>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var body = reader.GetString(4);
            results.Add(new SQLiteMemorySearchResult(
                Id: reader.GetString(0),
                Kind: reader.GetString(1),
                MemoryClass: reader.GetString(2),
                Title: reader.GetString(3),
                Snippet: body.Length <= 160 ? body : body[..160] + "...",
                Score: reader.GetDouble(10),
                Domain: reader.GetString(5),
                Boundary: reader.GetString(6),
                Audience: reader.GetString(7),
                Sensitivity: reader.GetString(8),
                RecallMode: reader.GetString(9)));
        }

        return results;
        }, ct);
    }

    public async Task<IReadOnlyList<SQLiteMemoryHydratedItem>> GetMemoriesByIdsAsync(
        IReadOnlyList<string> ids,
        string boundary,
        TrustAudience audience,
        CancellationToken ct = default)
    {
        if (ids.Count == 0)
            return [];

        var documents = ids
            .Select(ParseTypedId)
            .Where(x => x.Kind is MemoryKind.Document or MemoryKind.Unknown)
            .Select(x => x.Id)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var records = ids
            .Select(ParseTypedId)
            .Where(x => x.Kind is MemoryKind.Record or MemoryKind.Unknown)
            .Select(x => x.Id)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var output = new List<SQLiteMemoryHydratedItem>();
        var allowedAudiences = MemoryPolicyEvaluator.AllowedAudienceWireValues(audience)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        return await WithConnectionAsync(async (conn, ct) =>
        {
        foreach (var id in documents)
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                SELECT document_id, memory_class, title, markdown_body, aliases_json, facets_json, slots_json, domain, boundary, audience, sensitivity, recall_mode, update_semantics, expires_at, updated_at
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
                    MemoryClass: reader.GetString(1),
                    Title: reader.GetString(2),
                    Content: reader.GetString(3),
                    AliasesJson: reader.IsDBNull(4) ? null : reader.GetString(4),
                    FacetsJson: reader.IsDBNull(5) ? null : reader.GetString(5),
                    SlotsJson: reader.IsDBNull(6) ? null : reader.GetString(6),
                    Domain: reader.GetString(7),
                    Boundary: reader.IsDBNull(8) ? SecurityPolicyDefaults.LegacyRestrictedBoundary : reader.GetString(8),
                    Audience: reader.IsDBNull(9) ? TrustAudience.Public.ToWireValue() : reader.GetString(9),
                    Sensitivity: reader.GetString(10),
                    RecallMode: reader.GetString(11),
                    UpdateSemantics: reader.GetString(12),
                    ExpiresAtMs: reader.IsDBNull(13) ? null : reader.GetInt64(13),
                    UpdatedAtMs: reader.GetInt64(14)));

                if (!string.Equals(output[^1].Boundary, boundary, StringComparison.OrdinalIgnoreCase)
                    || !allowedAudiences.Contains(output[^1].Audience))
                {
                    output.RemoveAt(output.Count - 1);
                }
            }
        }

        foreach (var id in records)
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                SELECT record_id, memory_class, record_type, payload_json, aliases_json, facets_json, slots_json, domain, boundary, audience, sensitivity, recall_mode, update_semantics, expires_at, created_at
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
                    MemoryClass: reader.GetString(1),
                    Title: reader.GetString(2),
                    Content: reader.GetString(3),
                    AliasesJson: reader.IsDBNull(4) ? null : reader.GetString(4),
                    FacetsJson: reader.IsDBNull(5) ? null : reader.GetString(5),
                    SlotsJson: reader.IsDBNull(6) ? null : reader.GetString(6),
                    Domain: reader.GetString(7),
                    Boundary: reader.IsDBNull(8) ? SecurityPolicyDefaults.LegacyRestrictedBoundary : reader.GetString(8),
                    Audience: reader.IsDBNull(9) ? TrustAudience.Public.ToWireValue() : reader.GetString(9),
                    Sensitivity: reader.GetString(10),
                    RecallMode: reader.GetString(11),
                    UpdateSemantics: reader.GetString(12),
                    ExpiresAtMs: reader.IsDBNull(13) ? null : reader.GetInt64(13),
                    UpdatedAtMs: reader.GetInt64(14)));

                if (!string.Equals(output[^1].Boundary, boundary, StringComparison.OrdinalIgnoreCase)
                    || !allowedAudiences.Contains(output[^1].Audience))
                {
                    output.RemoveAt(output.Count - 1);
                }
            }
        }

        return output;
        }, ct);
    }

    public async Task<IReadOnlyList<SQLiteMemoryHydratedItem>> SearchByPlanAsync(
        IReadOnlyList<string> queryTerms,
        string domain,
        IReadOnlyList<string> memoryClasses,
        int limit,
        string boundary,
        TrustAudience audience,
        bool allowExpiredEvidence,
        CancellationToken ct = default)
        => await SearchByPlanInternalAsync(queryTerms, domain, memoryClasses, limit, boundary, audience, allowExpiredEvidence, ct);

    public async Task<IReadOnlyList<SQLiteMemoryHydratedItem>> SearchAcrossDomainsByPlanAsync(
        IReadOnlyList<string> queryTerms,
        IReadOnlyList<string> memoryClasses,
        int limit,
        string boundary,
        TrustAudience audience,
        bool allowExpiredEvidence,
        CancellationToken ct = default)
        => await SearchByPlanInternalAsync(queryTerms, null, memoryClasses, limit, boundary, audience, allowExpiredEvidence, ct);

    private async Task<IReadOnlyList<SQLiteMemoryHydratedItem>> SearchByPlanInternalAsync(
        IReadOnlyList<string> queryTerms,
        string? domain,
        IReadOnlyList<string> memoryClasses,
        int limit,
        string boundary,
        TrustAudience audience,
        bool allowExpiredEvidence,
        CancellationToken ct)
    {
        var matchQuery = BuildFtsMatchQuery(queryTerms);
        if (matchQuery is null || limit <= 0)
            return [];

        return await WithConnectionAsync(async (conn, ct) =>
        {
        var now = _timeProvider.GetUtcNow().ToUnixTimeMilliseconds();
        await using var cmd = conn.CreateCommand();

        var classClauses = new List<string>();
        for (var i = 0; i < memoryClasses.Count; i++)
        {
            classClauses.Add($"$c{i}");
            cmd.Parameters.AddWithValue($"$c{i}", memoryClasses[i]);
        }

        var whereClasses = string.Join(",", classClauses);
        var allowedAudiencesForPlan = MemoryPolicyEvaluator.AllowedAudienceWireValues(audience);
        var audiencePlanClauses = new List<string>();
        for (var i = 0; i < allowedAudiencesForPlan.Count; i++)
        {
            audiencePlanClauses.Add($"$pa{i}");
            cmd.Parameters.AddWithValue($"$pa{i}", allowedAudiencesForPlan[i]);
        }

        var whereAudiences = string.Join(",", audiencePlanClauses);
        var documentDomainClause = domain is null ? string.Empty : "AND d.domain = $domain";
        var recordDomainClause = domain is null ? string.Empty : "AND r.domain = $domain";

        cmd.CommandText = $"""
            WITH doc_hits AS (
                SELECT document_id, bm25(memory_documents_fts, 10.0, 1.0, 5.0, 3.0) AS fts_rank
                FROM memory_documents_fts
                WHERE memory_documents_fts MATCH $query
                ORDER BY fts_rank
                LIMIT $overfetch
            ),
            rec_hits AS (
                SELECT record_id, bm25(memory_records_fts, 10.0, 1.0, 5.0, 3.0) AS fts_rank
                FROM memory_records_fts
                WHERE memory_records_fts MATCH $query
                ORDER BY fts_rank
                LIMIT $overfetch
            )
            SELECT id, kind, memory_class, title, body, aliases_json, facets_json, slots_json, domain, boundary, audience, sensitivity, recall_mode, update_semantics, expires_at, updated_at, score
            FROM (
                SELECT
                    d.document_id AS id,
                    'document' AS kind,
                    d.memory_class AS memory_class,
                    d.title AS title,
                    d.markdown_body AS body,
                    d.aliases_json AS aliases_json,
                    d.facets_json AS facets_json,
                    d.slots_json AS slots_json,
                    d.domain AS domain,
                    COALESCE(d.boundary, $planLegacyBoundary) AS boundary,
                    COALESCE(d.audience, $planFallbackAudience) AS audience,
                    d.sensitivity AS sensitivity,
                    d.recall_mode AS recall_mode,
                    d.update_semantics AS update_semantics,
                    d.expires_at AS expires_at,
                    d.updated_at AS updated_at,
                    dh.fts_rank AS score
                FROM doc_hits dh
                JOIN memory_documents d ON d.document_id = dh.document_id
                WHERE 1 = 1
                  {documentDomainClause}
                  AND d.recall_mode IN ('{MemoryRecallMode.Auto.ToWireValue()}', '{MemoryRecallMode.Searchable.ToWireValue()}')
                  AND COALESCE(d.boundary, $planLegacyBoundary) = $boundary
                  AND COALESCE(d.audience, $planFallbackAudience) IN ({whereAudiences})
                  AND d.sensitivity != '{MemorySensitivity.Secret.ToWireValue()}'
                  AND d.memory_class IN ({whereClasses})
                  AND (d.expires_at IS NULL OR d.expires_at > $now OR $allowExpiredEvidence = 1)

                UNION ALL

                SELECT
                    r.record_id AS id,
                    'record' AS kind,
                    r.memory_class AS memory_class,
                    r.record_type AS title,
                    r.payload_json AS body,
                    r.aliases_json AS aliases_json,
                    r.facets_json AS facets_json,
                    r.slots_json AS slots_json,
                    r.domain AS domain,
                    COALESCE(r.boundary, $planLegacyBoundary) AS boundary,
                    COALESCE(r.audience, $planFallbackAudience) AS audience,
                    r.sensitivity AS sensitivity,
                    r.recall_mode AS recall_mode,
                    r.update_semantics AS update_semantics,
                    r.expires_at AS expires_at,
                    r.created_at AS updated_at,
                    rh.fts_rank AS score
                FROM rec_hits rh
                JOIN memory_records r ON r.record_id = rh.record_id
                WHERE 1 = 1
                  {recordDomainClause}
                  AND r.recall_mode IN ('{MemoryRecallMode.Auto.ToWireValue()}', '{MemoryRecallMode.Searchable.ToWireValue()}')
                  AND COALESCE(r.boundary, $planLegacyBoundary) = $boundary
                  AND COALESCE(r.audience, $planFallbackAudience) IN ({whereAudiences})
                  AND r.sensitivity != '{MemorySensitivity.Secret.ToWireValue()}'
                  AND r.memory_class IN ({whereClasses})
                  AND (r.expires_at IS NULL OR r.expires_at > $now OR $allowExpiredEvidence = 1)
            ) ranked
            ORDER BY score, updated_at DESC
            LIMIT $limit;
            """;
        cmd.Parameters.AddWithValue("$query", matchQuery);
        if (domain is not null)
            cmd.Parameters.AddWithValue("$domain", domain);
        cmd.Parameters.AddWithValue("$boundary", boundary);
        cmd.Parameters.AddWithValue("$planLegacyBoundary", SecurityPolicyDefaults.LegacyRestrictedBoundary);
        cmd.Parameters.AddWithValue("$planFallbackAudience", audience.ToWireValue());
        cmd.Parameters.AddWithValue("$now", now);
        cmd.Parameters.AddWithValue("$allowExpiredEvidence", allowExpiredEvidence ? 1 : 0);
        cmd.Parameters.AddWithValue("$overfetch", limit * 5);
        cmd.Parameters.AddWithValue("$limit", limit);

        var output = new List<SQLiteMemoryHydratedItem>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            output.Add(new SQLiteMemoryHydratedItem(
                Id: reader.GetString(0),
                Kind: reader.GetString(1),
                MemoryClass: reader.GetString(2),
                Title: reader.GetString(3),
                Content: reader.GetString(4),
                AliasesJson: reader.IsDBNull(5) ? null : reader.GetString(5),
                FacetsJson: reader.IsDBNull(6) ? null : reader.GetString(6),
                SlotsJson: reader.IsDBNull(7) ? null : reader.GetString(7),
                Domain: reader.GetString(8),
                Boundary: reader.GetString(9),
                Audience: reader.GetString(10),
                Sensitivity: reader.GetString(11),
                RecallMode: reader.GetString(12),
                UpdateSemantics: reader.GetString(13),
                ExpiresAtMs: reader.IsDBNull(14) ? null : reader.GetInt64(14),
                UpdatedAtMs: reader.GetInt64(15)));
        }

        return output;
        }, ct);
    }

    public async Task<bool> UpdateDocumentTextAsync(string documentId, string oldText, string newText, CancellationToken ct = default)
    {
        return await WithConnectionAsync(async (conn, ct) =>
        {

        await using var read = conn.CreateCommand();
        read.CommandText = "SELECT markdown_body, title, aliases_json, facets_json, recall_mode FROM memory_documents WHERE document_id = $id;";
        read.Parameters.AddWithValue("$id", documentId);

        string current;
        string title;
        string? aliasesJson;
        string? facetsJson;
        string recallMode;
        await using (var reader = await read.ExecuteReaderAsync(ct))
        {
            if (!await reader.ReadAsync(ct))
                return false;
            current = reader.GetString(0);
            title = reader.GetString(1);
            aliasesJson = reader.IsDBNull(2) ? null : reader.GetString(2);
            facetsJson = reader.IsDBNull(3) ? null : reader.GetString(3);
            recallMode = reader.GetString(4);
        }

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

        if (affected > 0 && IsSearchableRecallMode(recallMode))
            await InsertDocumentFtsAsync(conn, null, documentId, title, updated, aliasesJson, facetsJson, ct);

        return affected > 0;
        }, ct);
    }

    public async Task<bool> TombstoneDocumentAsync(string documentId, CancellationToken ct = default)
    {
        return await WithConnectionAsync(async (conn, ct) =>
        {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            UPDATE memory_documents
            SET update_semantics = '{MemoryUpdateSemantics.Tombstone.ToWireValue()}',
                recall_mode = '{MemoryRecallMode.Never.ToWireValue()}',
                updated_at = $updatedAt
            WHERE document_id = $id;
            """;
        cmd.Parameters.AddWithValue("$id", documentId);
        cmd.Parameters.AddWithValue("$updatedAt", _timeProvider.GetUtcNow().ToUnixTimeMilliseconds());
        var affected = await cmd.ExecuteNonQueryAsync(ct);
        return affected > 0;
        }, ct);
    }

    public async Task<bool> SupersedeRecordAsync(string recordId, string payloadJson, CancellationToken ct = default)
    {
        return await WithConnectionAsync(async (conn, ct) =>
        {
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
              '{MemoryUpdateSemantics.SupersedeRecord.ToWireValue()}', $domain, $sensitivity, $recallMode, $confidence, $freshnessAt, $createdAt);
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

        if (IsSearchableRecallMode(recallMode))
            await InsertRecordFtsAsync(conn, null, newId, recordType, payloadJson, null, null, ct);

        return true;
        }, ct);
    }

    public async Task<bool> TombstoneRecordAsync(string recordId, CancellationToken ct = default)
    {
        return await SupersedeRecordAsync(recordId, "{\"status\":\"tombstone\"}", ct);
    }

    /// <summary>
    /// Find existing anchors in a domain whose names fuzzy-match the proposed anchor name.
    /// Returns candidates including the most recent document under each matching anchor.
    /// </summary>
    public async Task<IReadOnlyList<ExistingMemoryCandidate>> FindFuzzyAnchorMatchesAsync(
        string proposedAnchorName,
        string domain,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(proposedAnchorName))
            return [];

        return await WithConnectionAsync(async (conn, ct) =>
        {
        // Query all active anchors in the domain with their most recent document
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"""
            SELECT a.anchor_id, a.canonical_name,
                   d.document_id, d.markdown_body, d.freshness_at, d.confidence
            FROM memory_anchors a
            LEFT JOIN (
                SELECT anchor_id, document_id, markdown_body, freshness_at, confidence,
                       ROW_NUMBER() OVER (PARTITION BY anchor_id ORDER BY updated_at DESC) AS rn
                FROM memory_documents
                WHERE update_semantics != '{MemoryUpdateSemantics.Tombstone.ToWireValue()}'
            ) d ON d.anchor_id = a.anchor_id AND d.rn = 1
            WHERE a.domain = $domain
              AND a.status = 'active';
            """;
        cmd.Parameters.AddWithValue("$domain", domain);

        var allAnchors = new List<(string AnchorId, string CanonicalName, string? DocId, string? Content, long? FreshnessAt, double Confidence)>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            allAnchors.Add((
                AnchorId: reader.GetString(0),
                CanonicalName: reader.GetString(1),
                DocId: reader.IsDBNull(2) ? null : reader.GetString(2),
                Content: reader.IsDBNull(3) ? null : reader.GetString(3),
                FreshnessAt: reader.IsDBNull(4) ? null : reader.GetInt64(4),
                Confidence: reader.IsDBNull(5) ? 0.0 : reader.GetDouble(5)));
        }

        // Build the expected anchor ID for exact match detection
        var normalizedProposedId = $"anchor:{proposedAnchorName.Trim().ToLowerInvariant().Replace(' ', '-')}";
        var proposedTokens = AnchorNameMatcher.Tokenize(proposedAnchorName);

        var candidates = new List<ExistingMemoryCandidate>();
        foreach (var anchor in allAnchors)
        {
            if (anchor.DocId is null || anchor.Content is null)
                continue;

            var isExact = string.Equals(anchor.AnchorId, normalizedProposedId, StringComparison.OrdinalIgnoreCase);
            var isFuzzy = !isExact && AnchorNameMatcher.IsFuzzyMatch(
                proposedTokens,
                AnchorNameMatcher.Tokenize(anchor.CanonicalName));

            if (isExact || isFuzzy)
            {
                candidates.Add(new ExistingMemoryCandidate(
                    DocumentId: anchor.DocId,
                    AnchorId: anchor.AnchorId,
                    AnchorCanonicalName: anchor.CanonicalName,
                    Content: anchor.Content,
                    FreshnessAtMs: anchor.FreshnessAt,
                    Confidence: anchor.Confidence,
                    IsExactAnchorMatch: isExact));
            }
        }

        return candidates;
        }, ct);
    }

    /// <summary>
    /// Content-based candidate search: finds existing documents whose content matches
    /// the given terms, joined with their anchors. Uses the same per-term scoring strategy
    /// as the recall pipeline's SearchByPlanAsync. Returns ExistingMemoryCandidate records
    /// for direct use by the curation actor's evaluation pipeline.
    /// </summary>
    public async Task<IReadOnlyList<ExistingMemoryCandidate>> FindCandidatesByContentAsync(
        IReadOnlyList<string> contentTerms,
        string domain,
        int limit = 5,
        CancellationToken ct = default)
    {
        var matchQuery = BuildFtsMatchQuery(contentTerms);
        if (matchQuery is null || limit <= 0)
            return [];

        return await WithConnectionAsync(async (conn, ct) =>
        {
        await using var cmd = conn.CreateCommand();

        cmd.CommandText = $"""
            WITH doc_hits AS (
                SELECT document_id, bm25(memory_documents_fts, 10.0, 1.0, 5.0, 3.0) AS fts_rank
                FROM memory_documents_fts
                WHERE memory_documents_fts MATCH $query
                ORDER BY fts_rank
                LIMIT $overfetch
            )
            SELECT a.anchor_id, a.canonical_name,
                   d.document_id, d.markdown_body, d.freshness_at, d.confidence
            FROM doc_hits dh
            JOIN memory_documents d ON d.document_id = dh.document_id
            JOIN memory_anchors a ON d.anchor_id = a.anchor_id
            WHERE a.domain = $domain
              AND a.status = 'active'
              AND d.memory_class = '{MemoryClass.DurableFact.ToWireValue()}'
              AND d.update_semantics != '{MemoryUpdateSemantics.Tombstone.ToWireValue()}'
            ORDER BY dh.fts_rank, d.confidence DESC, d.updated_at DESC
            LIMIT $limit;
            """;
        cmd.Parameters.AddWithValue("$query", matchQuery);
        cmd.Parameters.AddWithValue("$domain", domain);
        cmd.Parameters.AddWithValue("$overfetch", limit * 5);
        cmd.Parameters.AddWithValue("$limit", limit);

        var results = new List<ExistingMemoryCandidate>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            results.Add(new ExistingMemoryCandidate(
                DocumentId: reader.GetString(2),
                AnchorId: reader.GetString(0),
                AnchorCanonicalName: reader.GetString(1),
                Content: reader.GetString(3),
                FreshnessAtMs: reader.IsDBNull(4) ? null : reader.GetInt64(4),
                Confidence: reader.IsDBNull(5) ? 0.0 : reader.GetDouble(5),
                IsExactAnchorMatch: false));
        }

        return results;
        }, ct);
    }

    /// <summary>
    /// Tombstone an anchor by setting its status to 'tombstoned'.
    /// Documents under this anchor are NOT affected — they should be re-anchored first.
    /// </summary>
    public async Task<bool> TombstoneAnchorAsync(string anchorId, CancellationToken ct = default)
    {
        return await WithConnectionAsync(async (conn, ct) =>
        {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            UPDATE memory_anchors
            SET status = 'tombstoned',
                updated_at = $updatedAt
            WHERE anchor_id = $id;
            """;
        cmd.Parameters.AddWithValue("$id", anchorId);
        cmd.Parameters.AddWithValue("$updatedAt", _timeProvider.GetUtcNow().ToUnixTimeMilliseconds());
        var affected = await cmd.ExecuteNonQueryAsync(ct);
        return affected > 0;
        }, ct);
    }

    /// <summary>
    /// Move a document from one anchor to another (re-anchor).
    /// </summary>
    public async Task<bool> ReanchorDocumentAsync(string documentId, string newAnchorId, CancellationToken ct = default)
    {
        return await WithConnectionAsync(async (conn, ct) =>
        {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            UPDATE memory_documents
            SET anchor_id = $newAnchorId,
                updated_at = $updatedAt
            WHERE document_id = $id;
            """;
        cmd.Parameters.AddWithValue("$id", documentId);
        cmd.Parameters.AddWithValue("$newAnchorId", newAnchorId);
        cmd.Parameters.AddWithValue("$updatedAt", _timeProvider.GetUtcNow().ToUnixTimeMilliseconds());
        var affected = await cmd.ExecuteNonQueryAsync(ct);
        return affected > 0;
        }, ct);
    }

    /// <summary>
    /// Write a batch of curation operations without an associated checkpoint.
    /// Used by the inline curation actor path where proposals are sent directly
    /// from the session actor rather than through the checkpoint queue.
    /// </summary>
    public async Task ApplyInlineCurationBatchAsync(
        IReadOnlyList<SQLiteMemoryCurationOperation> operations,
        CancellationToken ct = default)
    {
        await WithConnectionAsync(async (conn, ct) =>
        {
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

            if (operation.Kind == MemoryKind.Record.ToWireValue())
            {
                var recId = string.IsNullOrWhiteSpace(operation.MemoryId) ? $"rec-{Guid.NewGuid():N}" : operation.MemoryId;
                await using var recordCmd = conn.CreateCommand();
                recordCmd.Transaction = tx;
                recordCmd.CommandText = """
                    INSERT INTO memory_records(
                      record_id, anchor_id, memory_class, record_type, payload_json, aliases_json, facets_json, slots_json, supersedes_record_id,
                      update_semantics, domain, sensitivity, recall_mode, confidence,
                      freshness_at, expires_at, created_at)
                    VALUES($id, $anchorId, $memoryClass, $recordType, $payloadJson, $aliasesJson, $facetsJson, $slotsJson, $supersedes,
                      $semantics, $domain, $sensitivity, $recallMode, $confidence,
                      $freshnessAt, $expiresAt, $createdAt);
                    """;
                recordCmd.Parameters.AddWithValue("$id", recId);
                recordCmd.Parameters.AddWithValue("$anchorId", anchor.AnchorId);
                recordCmd.Parameters.AddWithValue("$memoryClass", operation.MemoryClass);
                recordCmd.Parameters.AddWithValue("$recordType", operation.Title);
                recordCmd.Parameters.AddWithValue("$payloadJson", operation.Content);
                recordCmd.Parameters.AddWithValue("$aliasesJson", (object?)operation.AliasesJson ?? DBNull.Value);
                recordCmd.Parameters.AddWithValue("$facetsJson", (object?)operation.FacetsJson ?? DBNull.Value);
                recordCmd.Parameters.AddWithValue("$slotsJson", (object?)operation.SlotsJson ?? DBNull.Value);
                recordCmd.Parameters.AddWithValue("$supersedes", (object?)operation.SupersedesRecordId ?? DBNull.Value);
                recordCmd.Parameters.AddWithValue("$semantics", operation.UpdateSemantics);
                recordCmd.Parameters.AddWithValue("$domain", operation.Domain);
                recordCmd.Parameters.AddWithValue("$sensitivity", operation.Sensitivity);
                recordCmd.Parameters.AddWithValue("$recallMode", operation.RecallMode);
                recordCmd.Parameters.AddWithValue("$confidence", operation.Confidence);
                recordCmd.Parameters.AddWithValue("$freshnessAt", (object?)operation.FreshnessAtMs ?? DBNull.Value);
                recordCmd.Parameters.AddWithValue("$expiresAt", (object?)operation.ExpiresAtMs ?? DBNull.Value);
                recordCmd.Parameters.AddWithValue("$createdAt", now);
                await recordCmd.ExecuteNonQueryAsync(ct);

                if (IsSearchableRecallMode(operation.RecallMode))
                    await InsertRecordFtsAsync(conn, tx, recId, operation.Title, operation.Content, operation.AliasesJson, operation.FacetsJson, ct);

                continue;
            }

            var resolvedRecallMode = string.Equals(operation.MemoryClass, MemoryClass.Trace.ToWireValue(), StringComparison.OrdinalIgnoreCase)
                ? MemoryRecallMode.Never.ToWireValue()
                : operation.RecallMode;

            // Anchor-based dedup: same logic as ApplyCurationBatchAsync
            string documentId;
            if (!string.IsNullOrWhiteSpace(operation.MemoryId))
            {
                documentId = operation.MemoryId;
            }
            else if (string.Equals(operation.UpdateSemantics, MemoryUpdateSemantics.MergeDocument.ToWireValue(), StringComparison.OrdinalIgnoreCase))
            {
                await using var lookupCmd = conn.CreateCommand();
                lookupCmd.Transaction = tx;
                lookupCmd.CommandText = """
                    SELECT document_id FROM memory_documents
                    WHERE anchor_id = $anchorId
                    ORDER BY updated_at DESC
                    LIMIT 1;
                    """;
                lookupCmd.Parameters.AddWithValue("$anchorId", anchor.AnchorId);
                documentId = (string?)await lookupCmd.ExecuteScalarAsync(ct)
                    ?? $"doc-{Guid.NewGuid():N}";
            }
            else
            {
                documentId = $"doc-{Guid.NewGuid():N}";
            }

            await using var documentCmd = conn.CreateCommand();
            documentCmd.Transaction = tx;
            documentCmd.CommandText = """
                INSERT INTO memory_documents(
                  document_id, anchor_id, memory_class, title, markdown_body, aliases_json, facets_json, slots_json, update_semantics,
                  domain, boundary, audience, sensitivity, recall_mode, confidence, freshness_at,
                  expires_at, created_at, updated_at)
                VALUES($id, $anchorId, $memoryClass, $title, $body, $aliasesJson, $facetsJson, $slotsJson, $semantics,
                  $domain, $boundary, $audience, $sensitivity, $recallMode, $confidence, $freshnessAt,
                  $expiresAt, $createdAt, $updatedAt)
                ON CONFLICT(document_id) DO UPDATE SET
                  memory_class=excluded.memory_class,
                  title=excluded.title,
                  markdown_body=excluded.markdown_body,
                  aliases_json=excluded.aliases_json,
                  facets_json=excluded.facets_json,
                  slots_json=excluded.slots_json,
                  update_semantics=excluded.update_semantics,
                  domain=excluded.domain,
                  boundary=excluded.boundary,
                  audience=excluded.audience,
                  sensitivity=excluded.sensitivity,
                  recall_mode=excluded.recall_mode,
                  confidence=excluded.confidence,
                  freshness_at=excluded.freshness_at,
                  expires_at=excluded.expires_at,
                  updated_at=excluded.updated_at;
                """;
            documentCmd.Parameters.AddWithValue("$id", documentId);
            documentCmd.Parameters.AddWithValue("$anchorId", anchor.AnchorId);
            documentCmd.Parameters.AddWithValue("$memoryClass", operation.MemoryClass);
            documentCmd.Parameters.AddWithValue("$title", operation.Title);
            documentCmd.Parameters.AddWithValue("$body", operation.Content);
            documentCmd.Parameters.AddWithValue("$aliasesJson", (object?)operation.AliasesJson ?? DBNull.Value);
            documentCmd.Parameters.AddWithValue("$facetsJson", (object?)operation.FacetsJson ?? DBNull.Value);
            documentCmd.Parameters.AddWithValue("$slotsJson", (object?)operation.SlotsJson ?? DBNull.Value);
            documentCmd.Parameters.AddWithValue("$semantics", operation.UpdateSemantics);
            documentCmd.Parameters.AddWithValue("$domain", operation.Domain);
            documentCmd.Parameters.AddWithValue("$boundary", (object?)operation.Boundary ?? DBNull.Value);
            documentCmd.Parameters.AddWithValue("$audience", (object?)operation.Audience ?? DBNull.Value);
            documentCmd.Parameters.AddWithValue("$sensitivity", operation.Sensitivity);
            documentCmd.Parameters.AddWithValue("$recallMode", resolvedRecallMode);
            documentCmd.Parameters.AddWithValue("$confidence", operation.Confidence);
            documentCmd.Parameters.AddWithValue("$freshnessAt", (object?)operation.FreshnessAtMs ?? DBNull.Value);
            documentCmd.Parameters.AddWithValue("$expiresAt", (object?)operation.ExpiresAtMs ?? DBNull.Value);
            documentCmd.Parameters.AddWithValue("$createdAt", now);
            documentCmd.Parameters.AddWithValue("$updatedAt", now);
            await documentCmd.ExecuteNonQueryAsync(ct);

            if (IsSearchableRecallMode(resolvedRecallMode))
                await InsertDocumentFtsAsync(conn, tx, documentId, operation.Title, operation.Content, operation.AliasesJson, operation.FacetsJson, ct);
        }

        await tx.CommitAsync(ct);
        }, ct);
    }

    public async Task ApplyCurationBatchAsync(
        string checkpointId,
        IReadOnlyList<SQLiteMemoryCurationOperation> operations,
        CancellationToken ct = default)
    {
        await WithConnectionAsync(async (conn, ct) =>
        {
        await using var tx = (SqliteTransaction)await conn.BeginTransactionAsync(ct);

        foreach (var operation in operations)
        {
            var now = _timeProvider.GetUtcNow().ToUnixTimeMilliseconds();
            var resolvedBoundary = string.Equals(operation.Boundary, SecurityPolicyDefaults.LegacyRestrictedBoundary, StringComparison.Ordinal)
                ? SecurityPolicyDefaults.InferLegacyBoundaryFromDomain(operation.Domain)
                : operation.Boundary;
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

            if (operation.Kind == MemoryKind.Record.ToWireValue())
            {
                var recId = string.IsNullOrWhiteSpace(operation.MemoryId) ? $"rec-{Guid.NewGuid():N}" : operation.MemoryId;
                await using var recordCmd = conn.CreateCommand();
                recordCmd.Transaction = tx;
                recordCmd.CommandText = """
                    INSERT INTO memory_records(
                      record_id, anchor_id, memory_class, record_type, payload_json, aliases_json, facets_json, slots_json, supersedes_record_id,
                      update_semantics, domain, boundary, audience, sensitivity, recall_mode, confidence,
                      freshness_at, expires_at, created_at)
                    VALUES($id, $anchorId, $memoryClass, $recordType, $payloadJson, $aliasesJson, $facetsJson, $slotsJson, $supersedes,
                      $semantics, $domain, $boundary, $audience, $sensitivity, $recallMode, $confidence,
                      $freshnessAt, $expiresAt, $createdAt);
                    """;
                recordCmd.Parameters.AddWithValue("$id", recId);
                recordCmd.Parameters.AddWithValue("$anchorId", anchor.AnchorId);
                recordCmd.Parameters.AddWithValue("$memoryClass", operation.MemoryClass);
                recordCmd.Parameters.AddWithValue("$recordType", operation.Title);
                recordCmd.Parameters.AddWithValue("$payloadJson", operation.Content);
                recordCmd.Parameters.AddWithValue("$aliasesJson", (object?)operation.AliasesJson ?? DBNull.Value);
                recordCmd.Parameters.AddWithValue("$facetsJson", (object?)operation.FacetsJson ?? DBNull.Value);
                recordCmd.Parameters.AddWithValue("$slotsJson", (object?)operation.SlotsJson ?? DBNull.Value);
                recordCmd.Parameters.AddWithValue("$supersedes", (object?)operation.SupersedesRecordId ?? DBNull.Value);
                recordCmd.Parameters.AddWithValue("$semantics", operation.UpdateSemantics);
                recordCmd.Parameters.AddWithValue("$domain", operation.Domain);
                recordCmd.Parameters.AddWithValue("$boundary", resolvedBoundary);
                recordCmd.Parameters.AddWithValue("$audience", operation.Audience);
                recordCmd.Parameters.AddWithValue("$sensitivity", operation.Sensitivity);
                recordCmd.Parameters.AddWithValue("$recallMode", operation.RecallMode);
                recordCmd.Parameters.AddWithValue("$confidence", operation.Confidence);
                recordCmd.Parameters.AddWithValue("$freshnessAt", (object?)operation.FreshnessAtMs ?? DBNull.Value);
                recordCmd.Parameters.AddWithValue("$expiresAt", (object?)operation.ExpiresAtMs ?? DBNull.Value);
                recordCmd.Parameters.AddWithValue("$createdAt", now);
                await recordCmd.ExecuteNonQueryAsync(ct);

                if (IsSearchableRecallMode(operation.RecallMode))
                    await InsertRecordFtsAsync(conn, tx, recId, operation.Title, operation.Content, operation.AliasesJson, operation.FacetsJson, ct);

                continue;
            }

            var resolvedRecallMode = string.Equals(operation.MemoryClass, MemoryClass.Trace.ToWireValue(), StringComparison.OrdinalIgnoreCase)
                ? MemoryRecallMode.Never.ToWireValue()
                : operation.RecallMode;

            // Anchor-based dedup: for merge-document semantics, find existing document
            // by anchor_id and reuse its ID so the ON CONFLICT UPDATE fires instead of
            // creating a duplicate. This catches same-anchor duplicates like 10 copies
            // of "favorite color is blue" under the same anchor.
            string documentId;
            if (!string.IsNullOrWhiteSpace(operation.MemoryId))
            {
                documentId = operation.MemoryId;
            }
            else if (string.Equals(operation.UpdateSemantics, MemoryUpdateSemantics.MergeDocument.ToWireValue(), StringComparison.OrdinalIgnoreCase))
            {
                await using var lookupCmd = conn.CreateCommand();
                lookupCmd.Transaction = tx;
                lookupCmd.CommandText = """
                    SELECT document_id FROM memory_documents
                    WHERE anchor_id = $anchorId
                    ORDER BY updated_at DESC
                    LIMIT 1;
                    """;
                lookupCmd.Parameters.AddWithValue("$anchorId", anchor.AnchorId);
                documentId = (string?)await lookupCmd.ExecuteScalarAsync(ct)
                    ?? $"doc-{Guid.NewGuid():N}";
            }
            else
            {
                documentId = $"doc-{Guid.NewGuid():N}";
            }

            await using var documentCmd = conn.CreateCommand();
            documentCmd.Transaction = tx;
            documentCmd.CommandText = """
                INSERT INTO memory_documents(
                  document_id, anchor_id, memory_class, title, markdown_body, aliases_json, facets_json, slots_json, update_semantics,
                  domain, boundary, audience, sensitivity, recall_mode, confidence, freshness_at,
                  expires_at, created_at, updated_at)
                VALUES($id, $anchorId, $memoryClass, $title, $body, $aliasesJson, $facetsJson, $slotsJson, $semantics,
                  $domain, $boundary, $audience, $sensitivity, $recallMode, $confidence, $freshnessAt,
                  $expiresAt, $createdAt, $updatedAt)
                ON CONFLICT(document_id) DO UPDATE SET
                  memory_class=excluded.memory_class,
                  title=excluded.title,
                  markdown_body=excluded.markdown_body,
                  aliases_json=excluded.aliases_json,
                  facets_json=excluded.facets_json,
                  slots_json=excluded.slots_json,
                  update_semantics=excluded.update_semantics,
                  domain=excluded.domain,
                  boundary=excluded.boundary,
                  audience=excluded.audience,
                  sensitivity=excluded.sensitivity,
                  recall_mode=excluded.recall_mode,
                  confidence=excluded.confidence,
                  freshness_at=excluded.freshness_at,
                  expires_at=excluded.expires_at,
                  updated_at=excluded.updated_at;
                """;
            documentCmd.Parameters.AddWithValue("$id", documentId);
            documentCmd.Parameters.AddWithValue("$anchorId", anchor.AnchorId);
            documentCmd.Parameters.AddWithValue("$memoryClass", operation.MemoryClass);
            documentCmd.Parameters.AddWithValue("$title", operation.Title);
            documentCmd.Parameters.AddWithValue("$body", operation.Content);
            documentCmd.Parameters.AddWithValue("$aliasesJson", (object?)operation.AliasesJson ?? DBNull.Value);
            documentCmd.Parameters.AddWithValue("$facetsJson", (object?)operation.FacetsJson ?? DBNull.Value);
            documentCmd.Parameters.AddWithValue("$slotsJson", (object?)operation.SlotsJson ?? DBNull.Value);
            documentCmd.Parameters.AddWithValue("$semantics", operation.UpdateSemantics);
            documentCmd.Parameters.AddWithValue("$domain", operation.Domain);
            documentCmd.Parameters.AddWithValue("$boundary", resolvedBoundary);
            documentCmd.Parameters.AddWithValue("$audience", operation.Audience);
            documentCmd.Parameters.AddWithValue("$sensitivity", operation.Sensitivity);
            documentCmd.Parameters.AddWithValue("$recallMode", resolvedRecallMode);
            documentCmd.Parameters.AddWithValue("$confidence", operation.Confidence);
            documentCmd.Parameters.AddWithValue("$freshnessAt", (object?)operation.FreshnessAtMs ?? DBNull.Value);
            documentCmd.Parameters.AddWithValue("$expiresAt", (object?)operation.ExpiresAtMs ?? DBNull.Value);
            documentCmd.Parameters.AddWithValue("$createdAt", now);
            documentCmd.Parameters.AddWithValue("$updatedAt", now);
            await documentCmd.ExecuteNonQueryAsync(ct);

            if (IsSearchableRecallMode(resolvedRecallMode))
                await InsertDocumentFtsAsync(conn, tx, documentId, operation.Title, operation.Content, operation.AliasesJson, operation.FacetsJson, ct);
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
        }, ct);
    }

    private async Task<T> WithConnectionAsync<T>(
        Func<SqliteConnection, CancellationToken, Task<T>> work,
        CancellationToken ct)
    {
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);
        return await work(conn, ct);
    }

    private async Task WithConnectionAsync(
        Func<SqliteConnection, CancellationToken, Task> work,
        CancellationToken ct)
    {
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);
        await work(conn, ct);
    }

    private static MemoryTypedId ParseTypedId(string raw) => MemoryTypedId.Parse(raw);

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

    private static bool IsSearchableRecallMode(string recallMode)
        => string.Equals(recallMode, MemoryRecallMode.Auto.ToWireValue(), StringComparison.OrdinalIgnoreCase)
        || string.Equals(recallMode, MemoryRecallMode.Searchable.ToWireValue(), StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Build an FTS5 MATCH query from pre-tokenized terms.
    /// Each term is double-quoted to prevent FTS5 operator injection, joined with OR.
    /// Returns null if terms is empty.
    /// </summary>
    private static string? BuildFtsMatchQuery(IReadOnlyList<string> terms)
    {
        if (terms.Count == 0)
            return null;

        var escaped = terms
            .Where(t => !string.IsNullOrWhiteSpace(t))
            .Select(t => $"\"{t.Replace("\"", "\"\"", StringComparison.Ordinal)}\"")
            .ToArray();

        return escaped.Length == 0 ? null : string.Join(" OR ", escaped);
    }

    private static async Task InsertDocumentFtsAsync(
        SqliteConnection conn, SqliteTransaction? tx,
        string documentId, string title, string body,
        string? aliasesJson, string? facetsJson,
        CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        if (tx is not null) cmd.Transaction = tx;
        cmd.CommandText = """
            INSERT INTO memory_documents_fts(document_id, title, body, aliases, facets)
            VALUES($id, $title, $body, $aliases, $facets);
            """;
        cmd.Parameters.AddWithValue("$id", documentId);
        cmd.Parameters.AddWithValue("$title", title);
        cmd.Parameters.AddWithValue("$body", body);
        cmd.Parameters.AddWithValue("$aliases", aliasesJson ?? "");
        cmd.Parameters.AddWithValue("$facets", facetsJson ?? "");
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static async Task InsertRecordFtsAsync(
        SqliteConnection conn, SqliteTransaction? tx,
        string recordId, string title, string body,
        string? aliasesJson, string? facetsJson,
        CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        if (tx is not null) cmd.Transaction = tx;
        cmd.CommandText = """
            INSERT INTO memory_records_fts(record_id, title, body, aliases, facets)
            VALUES($id, $title, $body, $aliases, $facets);
            """;
        cmd.Parameters.AddWithValue("$id", recordId);
        cmd.Parameters.AddWithValue("$title", title);
        cmd.Parameters.AddWithValue("$body", body);
        cmd.Parameters.AddWithValue("$aliases", aliasesJson ?? "");
        cmd.Parameters.AddWithValue("$facets", facetsJson ?? "");
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static async Task EnsureColumnExistsAsync(
        SqliteConnection conn,
        string tableName,
        string columnName,
        string columnSql,
        CancellationToken ct)
    {
        await using var pragma = conn.CreateCommand();
        pragma.CommandText = $"PRAGMA table_info({tableName});";
        await using var reader = await pragma.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            if (string.Equals(reader.GetString(1), columnName, StringComparison.OrdinalIgnoreCase))
                return;
        }

        await using var alter = conn.CreateCommand();
        alter.CommandText = $"ALTER TABLE {tableName} ADD COLUMN {columnName} {columnSql};";
        await alter.ExecuteNonQueryAsync(ct);
    }

    private static async Task NormalizeLegacyBoundariesAsync(SqliteConnection conn, CancellationToken ct)
    {
        await using var docs = conn.CreateCommand();
        docs.CommandText = $"""
            UPDATE memory_documents
            SET boundary = CASE
                WHEN lower(domain) IN ('project:signalr', 'project:tui', 'project:headless', 'project:manual', 'project:default') THEN '{SecurityPolicyDefaults.LocalDaemonBoundary}'
                WHEN lower(domain) GLOB 'project:[cdg]*' THEN '{SecurityPolicyDefaults.SlackWorkspaceBoundary}'
                ELSE '{SecurityPolicyDefaults.LegacyRestrictedBoundary}'
            END
            WHERE boundary IS NULL OR trim(boundary) = '';
            """;
        await docs.ExecuteNonQueryAsync(ct);

        await using var records = conn.CreateCommand();
        records.CommandText = $"""
            UPDATE memory_records
            SET boundary = CASE
                WHEN lower(domain) IN ('project:signalr', 'project:tui', 'project:headless', 'project:manual', 'project:default') THEN '{SecurityPolicyDefaults.LocalDaemonBoundary}'
                WHEN lower(domain) GLOB 'project:[cdg]*' THEN '{SecurityPolicyDefaults.SlackWorkspaceBoundary}'
                ELSE '{SecurityPolicyDefaults.LegacyRestrictedBoundary}'
            END
            WHERE boundary IS NULL OR trim(boundary) = '';
            """;
        await records.ExecuteNonQueryAsync(ct);
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
            Sensitivity: MemorySensitivity.Normal.ToWireValue(),
            RecallMode: MemoryRecallMode.Auto.ToWireValue(),
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
    string MemoryClass,
    string Title,
    string MarkdownBody,
    string? AliasesJson,
    string? FacetsJson,
    string? SlotsJson,
    string UpdateSemantics,
    string Domain,
    string Sensitivity,
    string RecallMode,
    double Confidence,
    long? FreshnessAtMs,
    long? ExpiresAtMs,
    long CreatedAtMs,
    long UpdatedAtMs,
    string Boundary = SecurityPolicyDefaults.LegacyRestrictedBoundary,
    string Audience = "public");

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
    string MemoryClass,
    string Title,
    string Snippet,
    double Score,
    string Domain,
    string Sensitivity,
    string RecallMode,
    string Boundary = SecurityPolicyDefaults.LegacyRestrictedBoundary,
    string Audience = "public");

public sealed record SQLiteMemoryHydratedItem(
    string Id,
    string Kind,
    string MemoryClass,
    string Title,
    string Content,
    string? AliasesJson,
    string? FacetsJson,
    string? SlotsJson,
    string Domain,
    string Sensitivity,
    string RecallMode,
    string UpdateSemantics,
    long? ExpiresAtMs,
    long UpdatedAtMs,
    string Boundary = SecurityPolicyDefaults.LegacyRestrictedBoundary,
    string Audience = "public");

public sealed record SQLiteMemoryCurationOperation(
    string Kind,
    string MemoryClass,
    string? MemoryId,
    string AnchorCanonicalName,
    string AnchorType,
    string Title,
    string Content,
    string? AliasesJson,
    string? FacetsJson,
    string? SlotsJson,
    IReadOnlyList<SQLiteMemoryRelationOperation>? Relations,
    string UpdateSemantics,
    string Domain,
    string Sensitivity,
    string RecallMode,
    double Confidence,
    long? FreshnessAtMs,
    long? ExpiresAtMs,
    string? SupersedesRecordId = null,
    string Boundary = SecurityPolicyDefaults.LegacyRestrictedBoundary,
    string Audience = "public");

public sealed record SQLiteMemoryRelationOperation(
    string RelationType,
    string TargetCanonicalName,
    string TargetAnchorType,
    double Confidence);
