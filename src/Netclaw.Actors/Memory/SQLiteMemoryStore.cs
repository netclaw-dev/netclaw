// -----------------------------------------------------------------------
// <copyright file="SQLiteMemoryStore.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Netclaw.Actors.Text;
using Netclaw.Configuration;

namespace Netclaw.Actors.Memory;

/// <summary>
/// SQLite-backed durable memory store with a minimal graph/policy schema.
/// </summary>
public sealed class SQLiteMemoryStore
{
    private readonly string _connectionString;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<SQLiteMemoryStore> _logger;

    public SQLiteMemoryStore(string sqlitePath, TimeProvider timeProvider, ILogger<SQLiteMemoryStore>? logger = null)
    {
        _connectionString = new SqliteConnectionStringBuilder { DataSource = sqlitePath }.ToString();
        _timeProvider = timeProvider;
        _logger = logger ?? NullLogger<SQLiteMemoryStore>.Instance;
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
              sensitivity TEXT NOT NULL,
              recall_mode TEXT NOT NULL,
              confidence REAL NOT NULL,
              freshness_at INTEGER NULL,
              status TEXT NOT NULL,
              created_at INTEGER NOT NULL,
              updated_at INTEGER NOT NULL
            );

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
              ON memory_documents(sensitivity, recall_mode, updated_at DESC);

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
              ON memory_records(sensitivity, recall_mode, created_at DESC);

            CREATE TABLE IF NOT EXISTS memory_edges(
              edge_id TEXT PRIMARY KEY,
              from_anchor_id TEXT NOT NULL,
              to_anchor_id TEXT NOT NULL,
              relation_type TEXT NOT NULL,
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

        await using var ftsCreate = conn.CreateCommand();
        ftsCreate.CommandText = """
            CREATE VIRTUAL TABLE IF NOT EXISTS memory_documents_fts USING fts5(
                document_id UNINDEXED,
                title, body, aliases, facets,
                tokenize='porter unicode61 remove_diacritics 2'
            );

            CREATE VIRTUAL TABLE IF NOT EXISTS memory_records_fts USING fts5(
                record_id UNINDEXED,
                title, body, aliases, facets,
                tokenize='porter unicode61 remove_diacritics 2'
            );
            """;
        await ftsCreate.ExecuteNonQueryAsync(ct);

        // Data repair (idempotent): #1225 routed NEW compaction-boundary
        // summaries to Manual recall, but left rows created before the fix at
        // 'searchable' — which SearchByPlanAsync includes in the AUTOMATIC
        // recall pool. The July 2026 audit measured those legacy rows among
        // the top recall polluters (~19 injections/14 days, predominantly
        // judged irrelevant). Complete the fix retroactively.
        await using var compactionRepair = conn.CreateCommand();
        compactionRepair.CommandText = $"""
            UPDATE memory_documents
               SET recall_mode = '{MemoryRecallMode.Manual.ToWireValue()}'
             WHERE title = 'compaction-boundary'
               AND recall_mode = '{MemoryRecallMode.Searchable.ToWireValue()}';
            """;
        var repaired = await compactionRepair.ExecuteNonQueryAsync(ct);
        if (repaired > 0)
            _logger.LogInformation(
                "memory_data_repair_compaction_recall_mode rows={Rows} — legacy compaction-boundary summaries moved out of the automatic recall pool (retroactive #1225)",
                repaired);
        }, ct);
    }

    public async Task UpsertDocumentAsync(SQLiteMemoryDocument document, CancellationToken ct = default)
    {
        await WithConnectionAsync(async (conn, ct) =>
        {
        await using var tx = (SqliteTransaction)await conn.BeginTransactionAsync(ct);

        await EnsureAnchorAsync(conn, tx, document.Anchor, ct);

        var resolvedBoundary = string.Equals(document.Boundary, TrustBoundary.LegacyRestrictedValue, StringComparison.Ordinal)
            ? TrustBoundary.TrustedInstanceValue
            : document.Boundary;

        await using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            INSERT INTO memory_documents(
              document_id, anchor_id, memory_class, title, markdown_body, aliases_json, facets_json, slots_json, update_semantics,
              boundary, audience, sensitivity, recall_mode, confidence, freshness_at,
              expires_at, created_at, updated_at)
            VALUES($id, $anchorId, $memoryClass, $title, $body, $aliasesJson, $facetsJson, $slotsJson, $semantics,
              $boundary, $audience, $sensitivity, $recallMode, $confidence, $freshnessAt,
              $expiresAt, $createdAt, $updatedAt)
            ON CONFLICT(document_id) DO UPDATE SET
              memory_class=excluded.memory_class,
              title=excluded.title,
              markdown_body=excluded.markdown_body,
              aliases_json=excluded.aliases_json,
              facets_json=excluded.facets_json,
              slots_json=excluded.slots_json,
              update_semantics=excluded.update_semantics,
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
            await UpsertDocumentFtsAsync(conn, tx, document.DocumentId, document.Title, document.MarkdownBody, document.AliasesJson, document.FacetsJson, ct);

        await tx.CommitAsync(ct);
        }, ct);
    }

    public async Task<IReadOnlyList<SQLiteMemoryDocument>> SearchAutoRecallDocumentsAsync(
        string query,
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
              AND ($boundary IS NULL OR COALESCE(d.boundary, '{TrustBoundary.LegacyRestrictedValue}') = $boundary)
              AND (d.expires_at IS NULL OR d.expires_at > $now)
            ORDER BY dh.fts_rank, d.confidence DESC, d.updated_at DESC
            LIMIT $limit;
            """;
        cmd.Parameters.AddWithValue("$query", matchQuery);
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
                reader.GetString(14),
                reader.GetString(15),
                reader.GetDouble(16),
                reader.IsDBNull(17) ? null : reader.GetInt64(17),
                "active",
                reader.GetInt64(19),
                reader.GetInt64(20));

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
                Boundary: reader.IsDBNull(12) ? TrustBoundary.LegacyRestrictedValue : reader.GetString(12),
                Audience: reader.IsDBNull(13) ? TrustAudience.Personal.ToWireValue() : reader.GetString(13),
                Sensitivity: reader.GetString(14),
                RecallMode: reader.GetString(15),
                Confidence: reader.GetDouble(16),
                FreshnessAtMs: reader.IsDBNull(17) ? null : reader.GetInt64(17),
                ExpiresAtMs: reader.IsDBNull(18) ? null : reader.GetInt64(18),
                CreatedAtMs: reader.GetInt64(19),
                UpdatedAtMs: reader.GetInt64(20)));
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
            SELECT id, kind, memory_class, title, body, boundary, audience, sensitivity, recall_mode, confidence, sort_ts
            FROM (
                SELECT
                    d.document_id AS id, 'document' AS kind,
                    d.memory_class, d.title, d.markdown_body AS body,
                    COALESCE(d.boundary, $legacyBoundary) AS boundary,
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
                    COALESCE(r.boundary, $legacyBoundary) AS boundary,
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
        cmd.Parameters.AddWithValue("$legacyBoundary", TrustBoundary.LegacyRestrictedValue);
        cmd.Parameters.AddWithValue("$fallbackAudience", TrustAudience.Personal.ToWireValue());
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
                Score: reader.GetDouble(9),
                Boundary: reader.GetString(5),
                Audience: reader.GetString(6),
                Sensitivity: reader.GetString(7),
                RecallMode: reader.GetString(8)));
        }

        return results;
        }, ct);
    }

    /// <summary>
    /// Hydrates memories from handles that have already been resolved. Callers that run
    /// <see cref="ResolveMemoryHandlesAsync"/> up front (e.g. to surface per-ID errors) pass
    /// the result here so the same IDs are not resolved a second time.
    /// </summary>
    public async Task<IReadOnlyList<SQLiteMemoryHydratedItem>> GetMemoriesByResolvedHandlesAsync(
        IReadOnlyList<ResolvedMemoryHandle> resolvedIds,
        string boundary,
        TrustAudience audience,
        CancellationToken ct = default)
    {
        var documents = resolvedIds
            .Where(x => x.Resolved && x.Kind == MemoryKind.Document)
            .Select(x => x.StorageId!.Value.Value)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var records = resolvedIds
            .Where(x => x.Resolved && x.Kind == MemoryKind.Record)
            .Select(x => x.StorageId!.Value.Value)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (documents.Length == 0 && records.Length == 0)
            return [];

        var output = new List<SQLiteMemoryHydratedItem>();
        var allowedAudiences = MemoryPolicyEvaluator.AllowedAudienceWireValues(audience)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        return await WithConnectionAsync(async (conn, ct) =>
        {
        foreach (var id in documents)
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                SELECT document_id, memory_class, title, markdown_body, aliases_json, facets_json, slots_json, boundary, audience, sensitivity, recall_mode, update_semantics, expires_at, updated_at
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
                    Boundary: reader.IsDBNull(7) ? TrustBoundary.LegacyRestrictedValue : reader.GetString(7),
                    Audience: reader.IsDBNull(8) ? TrustAudience.Personal.ToWireValue() : reader.GetString(8),
                    Sensitivity: reader.GetString(9),
                    RecallMode: reader.GetString(10),
                    UpdateSemantics: reader.GetString(11),
                    ExpiresAtMs: reader.IsDBNull(12) ? null : reader.GetInt64(12),
                    UpdatedAtMs: reader.GetInt64(13)));

                if (!IsAccessible(output[^1].Boundary, output[^1].Audience, boundary, allowedAudiences))
                {
                    output.RemoveAt(output.Count - 1);
                }
            }
        }

        foreach (var id in records)
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                SELECT record_id, memory_class, record_type, payload_json, aliases_json, facets_json, slots_json, boundary, audience, sensitivity, recall_mode, update_semantics, expires_at, created_at
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
                    Boundary: reader.IsDBNull(7) ? TrustBoundary.LegacyRestrictedValue : reader.GetString(7),
                    Audience: reader.IsDBNull(8) ? TrustAudience.Personal.ToWireValue() : reader.GetString(8),
                    Sensitivity: reader.GetString(9),
                    RecallMode: reader.GetString(10),
                    UpdateSemantics: reader.GetString(11),
                    ExpiresAtMs: reader.IsDBNull(12) ? null : reader.GetInt64(12),
                    UpdatedAtMs: reader.GetInt64(13)));

                if (!IsAccessible(output[^1].Boundary, output[^1].Audience, boundary, allowedAudiences))
                {
                    output.RemoveAt(output.Count - 1);
                }
            }
        }

        return output;
        }, ct);
    }

    public async Task<IReadOnlyList<ResolvedMemoryHandle>> ResolveMemoryHandlesAsync(
        IReadOnlyList<string> rawIds,
        string boundary,
        TrustAudience audience,
        CancellationToken ct = default)
    {
        if (rawIds.Count == 0)
            return [];

        var allowedAudiences = MemoryPolicyEvaluator.AllowedAudienceWireValues(audience)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        // Resolve the whole batch over one connection so N ids cost a single connection open
        // (and one allowedAudiences set), not N of each.
        return await WithConnectionAsync(async (conn, ct) =>
        {
        var output = new List<ResolvedMemoryHandle>(rawIds.Count);
        foreach (var rawId in rawIds)
            output.Add(await ResolveHandleOnConnectionAsync(conn, rawId, boundary, allowedAudiences, ct));
        return (IReadOnlyList<ResolvedMemoryHandle>)output;
        }, ct);
    }

    public async Task<ResolvedMemoryHandle> ResolveMemoryHandleAsync(
        string rawId,
        string boundary,
        TrustAudience audience,
        CancellationToken ct = default)
    {
        var allowedAudiences = MemoryPolicyEvaluator.AllowedAudienceWireValues(audience)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        return await WithConnectionAsync(
            (conn, ct) => ResolveHandleOnConnectionAsync(conn, rawId, boundary, allowedAudiences, ct),
            ct);
    }

    // Core handle resolution against an already-open connection, so a batch (get_memories) can
    // share a single connection and allowedAudiences set instead of opening a connection per id.
    private static async Task<ResolvedMemoryHandle> ResolveHandleOnConnectionAsync(
        SqliteConnection conn,
        string rawId,
        string boundary,
        ISet<string> allowedAudiences,
        CancellationToken ct)
    {
        var parsed = MemoryTypedId.Parse(rawId);
        if (parsed.Kind is not (MemoryKind.Document or MemoryKind.Record))
            return ResolvedMemoryHandle.Failed(rawId, parsed.Kind, "ID must be prefixed with doc- or rec-.");
        if (parsed.Id.IsEmpty)
            return ResolvedMemoryHandle.Failed(rawId, parsed.Kind, "ID payload is required.");

        // Records are append-only: an edit inserts a new row that supersedes the old one and
        // leaves the old row physically present. Follow the supersede chain to the head (latest)
        // row so a stable handle the model was given earlier still resolves to the current
        // content instead of the pre-edit row. Documents edit in place and have no such chain.
        var storageId = parsed.Kind == MemoryKind.Record
            ? await ResolveRecordHeadAsync(conn, parsed.Id, ct)
            : parsed.Id;

        // The resolved id is the exact storage key (primary key, unique per table), so a single
        // visibility-scoped lookup either finds it or it does not — no candidate guessing.
        var visible = await MemoryIdVisibleAsync(conn, parsed.Kind, storageId, boundary, allowedAudiences, ct);
        return visible
            ? ResolvedMemoryHandle.Found(rawId, parsed.Kind, storageId)
            : ResolvedMemoryHandle.Failed(rawId, parsed.Kind, $"Memory \"{rawId}\" was not found or is not accessible from this session.");
    }

    public async Task<IReadOnlyList<SQLiteMemoryHydratedItem>> SearchByPlanAsync(
        IReadOnlyList<string> queryTerms,
        IReadOnlyList<string> memoryClasses,
        int limit,
        string boundary,
        TrustAudience audience,
        bool allowExpiredEvidence,
        CancellationToken ct = default)
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
            SELECT id, kind, memory_class, title, body, aliases_json, facets_json, slots_json, boundary, audience, sensitivity, recall_mode, update_semantics, expires_at, updated_at, score
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
                WHERE d.recall_mode IN ('{MemoryRecallMode.Auto.ToWireValue()}', '{MemoryRecallMode.Searchable.ToWireValue()}')
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
                WHERE r.recall_mode IN ('{MemoryRecallMode.Auto.ToWireValue()}', '{MemoryRecallMode.Searchable.ToWireValue()}')
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
        cmd.Parameters.AddWithValue("$boundary", boundary);
        cmd.Parameters.AddWithValue("$planLegacyBoundary", TrustBoundary.LegacyRestrictedValue);
        cmd.Parameters.AddWithValue("$planFallbackAudience", TrustAudience.Personal.ToWireValue());
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
                Boundary: reader.GetString(8),
                Audience: reader.GetString(9),
                Sensitivity: reader.GetString(10),
                RecallMode: reader.GetString(11),
                UpdateSemantics: reader.GetString(12),
                ExpiresAtMs: reader.IsDBNull(13) ? null : reader.GetInt64(13),
                UpdatedAtMs: reader.GetInt64(14)));
        }

        return output;
        }, ct);
    }

    public async Task<bool> UpdateDocumentTextAsync(string documentId, string oldText, string newText, CancellationToken ct = default)
    {
        return await WithConnectionAsync(async (conn, ct) =>
        {
        await using var tx = (SqliteTransaction)await conn.BeginTransactionAsync(ct);

        await using var read = conn.CreateCommand();
        read.Transaction = tx;
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
        write.Transaction = tx;
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
            await UpsertDocumentFtsAsync(conn, tx, documentId, title, updated, aliasesJson, facetsJson, ct);

        await tx.CommitAsync(ct);
        return affected > 0;
        }, ct);
    }

    public async Task<bool> ReplaceDocumentTextAsync(string documentId, string newText, CancellationToken ct = default)
    {
        return await WithConnectionAsync(async (conn, ct) =>
        {
        await using var tx = (SqliteTransaction)await conn.BeginTransactionAsync(ct);

        await using var read = conn.CreateCommand();
        read.Transaction = tx;
        read.CommandText = "SELECT title, aliases_json, facets_json, recall_mode FROM memory_documents WHERE document_id = $id;";
        read.Parameters.AddWithValue("$id", documentId);

        string title;
        string? aliasesJson;
        string? facetsJson;
        string recallMode;
        await using (var reader = await read.ExecuteReaderAsync(ct))
        {
            if (!await reader.ReadAsync(ct))
                return false;
            title = reader.GetString(0);
            aliasesJson = reader.IsDBNull(1) ? null : reader.GetString(1);
            facetsJson = reader.IsDBNull(2) ? null : reader.GetString(2);
            recallMode = reader.GetString(3);
        }

        await using var write = conn.CreateCommand();
        write.Transaction = tx;
        write.CommandText = """
            UPDATE memory_documents
            SET markdown_body = $body,
                updated_at = $updatedAt
            WHERE document_id = $id;
            """;
        write.Parameters.AddWithValue("$id", documentId);
        write.Parameters.AddWithValue("$body", newText);
        write.Parameters.AddWithValue("$updatedAt", _timeProvider.GetUtcNow().ToUnixTimeMilliseconds());
        var affected = await write.ExecuteNonQueryAsync(ct);

        if (affected > 0 && IsSearchableRecallMode(recallMode))
            await UpsertDocumentFtsAsync(conn, tx, documentId, title, newText, aliasesJson, facetsJson, ct);

        await tx.CommitAsync(ct);
        return affected > 0;
        }, ct);
    }

    public async Task<bool> TombstoneDocumentAsync(string documentId, CancellationToken ct = default)
    {
        return await WithConnectionAsync(async (conn, ct) =>
        {
        await using var tx = (SqliteTransaction)await conn.BeginTransactionAsync(ct);

        await using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = $"""
            UPDATE memory_documents
            SET update_semantics = '{MemoryUpdateSemantics.Tombstone.ToWireValue()}',
                recall_mode = '{MemoryRecallMode.Never.ToWireValue()}',
                updated_at = $updatedAt
            WHERE document_id = $id;
            """;
        cmd.Parameters.AddWithValue("$id", documentId);
        cmd.Parameters.AddWithValue("$updatedAt", _timeProvider.GetUtcNow().ToUnixTimeMilliseconds());
        var affected = await cmd.ExecuteNonQueryAsync(ct);

        if (affected > 0)
            await DeleteDocumentFtsAsync(conn, tx, documentId, ct);

        await tx.CommitAsync(ct);
        return affected > 0;
        }, ct);
    }

    public async Task<bool> SupersedeRecordAsync(string recordId, string payloadJson, CancellationToken ct = default)
    {
        return await WithConnectionAsync(async (conn, ct) =>
        {
        await using var tx = (SqliteTransaction)await conn.BeginTransactionAsync(ct);

        await using var read = conn.CreateCommand();
        read.Transaction = tx;
        read.CommandText = """
            SELECT anchor_id, record_type, sensitivity, recall_mode, confidence, freshness_at, boundary, audience, memory_class
            FROM memory_records
            WHERE record_id = $id;
            """;
        read.Parameters.AddWithValue("$id", recordId);

        string anchorId;
        string recordType;
        string sensitivity;
        string recallMode;
        double confidence;
        long? freshnessAt;
        string boundary;
        string audience;
        string memoryClass;
        await using (var reader = await read.ExecuteReaderAsync(ct))
        {
            if (!await reader.ReadAsync(ct))
                return false;

            anchorId = reader.GetString(0);
            recordType = reader.GetString(1);
            sensitivity = reader.GetString(2);
            recallMode = reader.GetString(3);
            confidence = reader.GetDouble(4);
            freshnessAt = reader.IsDBNull(5) ? (long?)null : reader.GetInt64(5);
            boundary = reader.IsDBNull(6) ? TrustBoundary.LegacyRestrictedValue : reader.GetString(6);
            audience = reader.IsDBNull(7) ? TrustAudience.Personal.ToWireValue() : reader.GetString(7);
            memoryClass = reader.IsDBNull(8) ? MemoryClass.Evidence.ToWireValue() : reader.GetString(8);
        }

        var now = _timeProvider.GetUtcNow().ToUnixTimeMilliseconds();
        var newId = MemoryTypedId.NewRecordId();

        await using var insert = conn.CreateCommand();
        insert.Transaction = tx;
        insert.CommandText = $"""
            INSERT INTO memory_records(
              record_id, anchor_id, memory_class, record_type, payload_json, supersedes_record_id,
              update_semantics, boundary, audience, sensitivity, recall_mode, confidence, freshness_at, created_at)
            VALUES($id, $anchorId, $memoryClass, $recordType, $payload, $supersedes,
              '{MemoryUpdateSemantics.SupersedeRecord.ToWireValue()}', $boundary, $audience, $sensitivity, $recallMode, $confidence, $freshnessAt, $createdAt);
            """;
        insert.Parameters.AddWithValue("$id", newId.Value);
        insert.Parameters.AddWithValue("$anchorId", anchorId);
        insert.Parameters.AddWithValue("$memoryClass", memoryClass);
        insert.Parameters.AddWithValue("$recordType", recordType);
        insert.Parameters.AddWithValue("$payload", payloadJson);
        insert.Parameters.AddWithValue("$supersedes", recordId);
        insert.Parameters.AddWithValue("$boundary", boundary);
        insert.Parameters.AddWithValue("$audience", audience);
        insert.Parameters.AddWithValue("$sensitivity", sensitivity);
        insert.Parameters.AddWithValue("$recallMode", recallMode);
        insert.Parameters.AddWithValue("$confidence", confidence);
        insert.Parameters.AddWithValue("$freshnessAt", (object?)freshnessAt ?? DBNull.Value);
        insert.Parameters.AddWithValue("$createdAt", now);
        await insert.ExecuteNonQueryAsync(ct);

        await DeleteRecordFtsAsync(conn, tx, recordId, ct);

        if (IsSearchableRecallMode(recallMode))
            await UpsertRecordFtsAsync(conn, tx, newId.Value, recordType, payloadJson, null, null, ct);

        await tx.CommitAsync(ct);
        return true;
        }, ct);
    }

    public async Task<bool> TombstoneRecordAsync(string recordId, CancellationToken ct = default)
    {
        return await SupersedeRecordAsync(recordId, "{\"status\":\"tombstone\"}", ct);
    }

    /// <summary>
    /// Find existing anchors whose names fuzzy-match the proposed anchor name.
    /// Returns candidates including the most recent document under each matching anchor.
    /// </summary>
    public async Task<IReadOnlyList<ExistingMemoryCandidate>> FindFuzzyAnchorMatchesAsync(
        string proposedAnchorName,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(proposedAnchorName))
            return [];

        return await WithConnectionAsync(async (conn, ct) =>
        {
        // Query all active anchors with their most recent document
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
            WHERE a.status = 'active';
            """;

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
        var normalizedProposedId = MemoryTypedId.AnchorId(proposedAnchorName);
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
            WHERE a.status = 'active'
              AND d.memory_class = '{MemoryClass.DurableFact.ToWireValue()}'
              AND d.update_semantics != '{MemoryUpdateSemantics.Tombstone.ToWireValue()}'
            ORDER BY dh.fts_rank, d.confidence DESC, d.updated_at DESC
            LIMIT $limit;
            """;
        cmd.Parameters.AddWithValue("$query", matchQuery);
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
    /// An existing document row found by the anchor-based dedup lookup in
    /// <see cref="ApplyInlineCurationBatchAsync"/>/<see cref="ApplyCurationBatchAsync"/>.
    /// Carries the values that must survive a dedup collision so ON CONFLICT DO UPDATE
    /// cannot silently replace this row's identity/classification with an unrelated
    /// proposal's raw values (title stays, boundary/audience/sensitivity stay — a widened
    /// audience or loosened sensitivity here would be a silent visibility escalation).
    /// </summary>
    private sealed record DedupCollision(string Title, string MarkdownBody, string? Boundary, string? Audience, string Sensitivity);

    /// <summary>
    /// Builds the anchor-dedup append body for a proposal that collides with an existing
    /// document under the same anchor. The rules tier can legitimately emit a Create
    /// decision for "different topic, similar anchor name" (see
    /// <see cref="CurationRulesEvaluator"/>'s fuzzy/low-overlap branch); when that proposal's
    /// anchor happens to already hold a document, appending instead of overwriting is
    /// unconditionally lossless — plain concatenation can never drop the prior content the
    /// way the old ON CONFLICT DO UPDATE overwrite did (audit: 88 silent overwrites/14 days,
    /// including a carefully LLM-merged memory). Matches the dated-separator convention used
    /// by MemoryCurationEvaluator.BuildAppendedBody on feature/memory-embeddings so the two
    /// branches stay convention-compatible when merged.
    /// </summary>
    private string BuildDedupAppendedBody(string existingBody, string proposalContent)
    {
        var isoDate = _timeProvider.GetUtcNow().ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
        return $"{existingBody}\n\n---\n_[merged {isoDate}]_\n{proposalContent}";
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

            await ApplyCurationOperationsAsync(conn, tx, operations, ct);

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

            await ApplyCurationOperationsAsync(conn, tx, operations, ct);

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

    /// <summary>
    /// Shared per-operation write loop for <see cref="ApplyInlineCurationBatchAsync"/> and
    /// <see cref="ApplyCurationBatchAsync"/>: anchor resolution, record insert, anchor-based
    /// dedup lookup, document upsert, and FTS upsert. Both callers run this inside their own
    /// transaction and commit (or add a checkpoint update) afterward.
    /// </summary>
    private async Task ApplyCurationOperationsAsync(
        SqliteConnection conn,
        SqliteTransaction tx,
        IReadOnlyList<SQLiteMemoryCurationOperation> operations,
        CancellationToken ct)
    {
        foreach (var operation in operations)
        {
            var now = _timeProvider.GetUtcNow().ToUnixTimeMilliseconds();
            var resolvedBoundary = string.Equals(operation.Boundary, TrustBoundary.LegacyRestrictedValue, StringComparison.Ordinal)
                ? TrustBoundary.TrustedInstanceValue
                : operation.Boundary;
            var canonicalName = string.IsNullOrWhiteSpace(operation.AnchorCanonicalName)
                ? operation.Title
                : operation.AnchorCanonicalName;
            var anchor = CreateDefaultAnchor(canonicalName) with
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
                var recId = string.IsNullOrWhiteSpace(operation.MemoryId) ? MemoryTypedId.NewRecordId().Value : operation.MemoryId;
                await using var recordCmd = conn.CreateCommand();
                recordCmd.Transaction = tx;
                recordCmd.CommandText = """
                    INSERT INTO memory_records(
                      record_id, anchor_id, memory_class, record_type, payload_json, aliases_json, facets_json, slots_json, supersedes_record_id,
                      update_semantics, boundary, audience, sensitivity, recall_mode, confidence,
                      freshness_at, expires_at, created_at)
                    VALUES($id, $anchorId, $memoryClass, $recordType, $payloadJson, $aliasesJson, $facetsJson, $slotsJson, $supersedes,
                      $semantics, $boundary, $audience, $sensitivity, $recallMode, $confidence,
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
                recordCmd.Parameters.AddWithValue("$boundary", resolvedBoundary);
                recordCmd.Parameters.AddWithValue("$audience", operation.Audience.ToWireValue());
                recordCmd.Parameters.AddWithValue("$sensitivity", operation.Sensitivity);
                recordCmd.Parameters.AddWithValue("$recallMode", operation.RecallMode);
                recordCmd.Parameters.AddWithValue("$confidence", operation.Confidence);
                recordCmd.Parameters.AddWithValue("$freshnessAt", (object?)operation.FreshnessAtMs ?? DBNull.Value);
                recordCmd.Parameters.AddWithValue("$expiresAt", (object?)operation.ExpiresAtMs ?? DBNull.Value);
                recordCmd.Parameters.AddWithValue("$createdAt", now);
                await recordCmd.ExecuteNonQueryAsync(ct);

                if (IsSearchableRecallMode(operation.RecallMode))
                    await UpsertRecordFtsAsync(conn, tx, recId, operation.Title, operation.Content, operation.AliasesJson, operation.FacetsJson, ct);

                continue;
            }

            var resolvedRecallMode = string.Equals(operation.MemoryClass, MemoryClass.Trace.ToWireValue(), StringComparison.OrdinalIgnoreCase)
                ? MemoryRecallMode.Never.ToWireValue()
                : operation.RecallMode;

            // Anchor-based dedup: for merge-document semantics, find existing document
            // by anchor_id and reuse its ID so the ON CONFLICT UPDATE fires instead of
            // creating a duplicate. This catches same-anchor duplicates like 10 copies
            // of "favorite color is blue" under the same anchor. Reusing an existing
            // document_id here (documentId set, collision left null) is safe when the
            // operation already carries an explicit target (Update decision). Finding an
            // existing row via the anchor lookup below (collision set) is a genuine Create
            // collision — see DedupCollision's remarks — and must append, not overwrite.
            string documentId;
            DedupCollision? collision = null;
            if (!string.IsNullOrWhiteSpace(operation.MemoryId))
            {
                documentId = operation.MemoryId;
            }
            else if (string.Equals(operation.UpdateSemantics, MemoryUpdateSemantics.MergeDocument.ToWireValue(), StringComparison.OrdinalIgnoreCase))
            {
                await using var lookupCmd = conn.CreateCommand();
                lookupCmd.Transaction = tx;
                lookupCmd.CommandText = """
                    SELECT document_id, title, markdown_body, boundary, audience, sensitivity
                    FROM memory_documents
                    WHERE anchor_id = $anchorId
                    ORDER BY updated_at DESC
                    LIMIT 1;
                    """;
                lookupCmd.Parameters.AddWithValue("$anchorId", anchor.AnchorId);
                await using var lookupReader = await lookupCmd.ExecuteReaderAsync(ct);
                if (await lookupReader.ReadAsync(ct))
                {
                    documentId = lookupReader.GetString(0);
                    collision = new DedupCollision(
                        lookupReader.GetString(1),
                        lookupReader.GetString(2),
                        lookupReader.IsDBNull(3) ? null : lookupReader.GetString(3),
                        lookupReader.IsDBNull(4) ? null : lookupReader.GetString(4),
                        lookupReader.GetString(5));
                }
                else
                {
                    documentId = MemoryTypedId.NewDocumentId().Value;
                }
            }
            else
            {
                documentId = MemoryTypedId.NewDocumentId().Value;
            }

            if (collision is not null)
            {
                // Idempotency guard: a colliding proposal whose content the existing body
                // already holds verbatim adds nothing — appending it would bloat the
                // document on every repeat (the inverse failure of the overwrite bug this
                // path fixes). Logged no-op: the document row stays byte-identical.
                if (collision.MarkdownBody.Contains(operation.Content, StringComparison.Ordinal))
                {
                    _logger.LogInformation(
                        "curation_dedup_duplicate_skipped anchor={AnchorCanonicalName} targetDoc={DocumentId}",
                        canonicalName,
                        documentId);
                    continue;
                }

                _logger.LogInformation(
                    "curation_dedup_append anchor={AnchorCanonicalName} targetDoc={DocumentId}",
                    canonicalName,
                    documentId);
            }

            // Preserve the colliding row's identity/classification; only the body grows
            // (appended) and update_semantics flips to append-document to record that this
            // write did not overwrite. Non-collision path is the pre-existing behavior.
            var effectiveTitle = collision is not null ? collision.Title : operation.Title;
            var effectiveBody = collision is not null
                ? BuildDedupAppendedBody(collision.MarkdownBody, operation.Content)
                : operation.Content;
            var effectiveBoundary = collision is not null ? collision.Boundary : resolvedBoundary;
            var effectiveAudience = collision is not null ? collision.Audience : operation.Audience.ToWireValue();
            var effectiveSensitivity = collision is not null ? collision.Sensitivity : operation.Sensitivity;
            var effectiveSemantics = collision is not null
                ? MemoryUpdateSemantics.AppendDocument.ToWireValue()
                : operation.UpdateSemantics;

            await using var documentCmd = conn.CreateCommand();
            documentCmd.Transaction = tx;
            documentCmd.CommandText = """
                INSERT INTO memory_documents(
                  document_id, anchor_id, memory_class, title, markdown_body, aliases_json, facets_json, slots_json, update_semantics,
                  boundary, audience, sensitivity, recall_mode, confidence, freshness_at,
                  expires_at, created_at, updated_at)
                VALUES($id, $anchorId, $memoryClass, $title, $body, $aliasesJson, $facetsJson, $slotsJson, $semantics,
                  $boundary, $audience, $sensitivity, $recallMode, $confidence, $freshnessAt,
                  $expiresAt, $createdAt, $updatedAt)
                ON CONFLICT(document_id) DO UPDATE SET
                  memory_class=excluded.memory_class,
                  title=excluded.title,
                  markdown_body=excluded.markdown_body,
                  aliases_json=excluded.aliases_json,
                  facets_json=excluded.facets_json,
                  slots_json=excluded.slots_json,
                  update_semantics=excluded.update_semantics,
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
            documentCmd.Parameters.AddWithValue("$title", effectiveTitle);
            documentCmd.Parameters.AddWithValue("$body", effectiveBody);
            documentCmd.Parameters.AddWithValue("$aliasesJson", (object?)operation.AliasesJson ?? DBNull.Value);
            documentCmd.Parameters.AddWithValue("$facetsJson", (object?)operation.FacetsJson ?? DBNull.Value);
            documentCmd.Parameters.AddWithValue("$slotsJson", (object?)operation.SlotsJson ?? DBNull.Value);
            documentCmd.Parameters.AddWithValue("$semantics", effectiveSemantics);
            documentCmd.Parameters.AddWithValue("$boundary", (object?)effectiveBoundary ?? DBNull.Value);
            documentCmd.Parameters.AddWithValue("$audience", (object?)effectiveAudience ?? DBNull.Value);
            documentCmd.Parameters.AddWithValue("$sensitivity", effectiveSensitivity);
            documentCmd.Parameters.AddWithValue("$recallMode", resolvedRecallMode);
            documentCmd.Parameters.AddWithValue("$confidence", operation.Confidence);
            documentCmd.Parameters.AddWithValue("$freshnessAt", (object?)operation.FreshnessAtMs ?? DBNull.Value);
            documentCmd.Parameters.AddWithValue("$expiresAt", (object?)operation.ExpiresAtMs ?? DBNull.Value);
            documentCmd.Parameters.AddWithValue("$createdAt", now);
            documentCmd.Parameters.AddWithValue("$updatedAt", now);
            await documentCmd.ExecuteNonQueryAsync(ct);

            if (IsSearchableRecallMode(resolvedRecallMode))
                await UpsertDocumentFtsAsync(conn, tx, documentId, effectiveTitle, effectiveBody, operation.AliasesJson, operation.FacetsJson, ct);
        }
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

    /// <summary>
    /// Walks <c>memory_records.supersedes_record_id</c> forward to the head (latest) row in the
    /// supersede chain. Returns the input id unchanged when it has not been superseded or does
    /// not exist. Chains are acyclic (each supersede points at an older row), so the deepest
    /// reachable row is the head.
    /// </summary>
    private static async Task<MemoryStorageId> ResolveRecordHeadAsync(
        SqliteConnection conn,
        MemoryStorageId recordId,
        CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            WITH RECURSIVE chain(id, depth) AS (
                SELECT $id, 0
                UNION ALL
                SELECT n.record_id, c.depth + 1
                FROM memory_records n
                JOIN chain c ON n.supersedes_record_id = c.id
            )
            SELECT id FROM chain ORDER BY depth DESC LIMIT 1;
            """;
        cmd.Parameters.AddWithValue("$id", recordId.Value);
        var head = (string?)await cmd.ExecuteScalarAsync(ct);
        return string.IsNullOrEmpty(head) ? recordId : new MemoryStorageId(head);
    }

    private static async Task<bool> MemoryIdVisibleAsync(
        SqliteConnection conn,
        MemoryKind kind,
        MemoryStorageId storageId,
        string boundary,
        ISet<string> allowedAudiences,
        CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = kind switch
        {
            MemoryKind.Document => """
                SELECT boundary, audience
                FROM memory_documents
                WHERE document_id = $id;
                """,
            MemoryKind.Record => """
                SELECT boundary, audience
                FROM memory_records
                WHERE record_id = $id;
                """,
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null)
        };
        cmd.Parameters.AddWithValue("$id", storageId.Value);

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
            return false;

        var itemBoundary = reader.IsDBNull(0) ? TrustBoundary.LegacyRestrictedValue : reader.GetString(0);
        var itemAudience = reader.IsDBNull(1) ? TrustAudience.Personal.ToWireValue() : reader.GetString(1);
        return IsAccessible(itemBoundary, itemAudience, boundary, allowedAudiences);
    }

    // Single source of truth for the boundary/audience visibility rule, shared by handle
    // resolution (MemoryIdVisibleAsync) and hydration so the two paths cannot drift.
    private static bool IsAccessible(string itemBoundary, string itemAudience, string boundary, ISet<string> allowedAudiences)
        => string.Equals(itemBoundary, boundary, StringComparison.OrdinalIgnoreCase)
           && allowedAudiences.Contains(itemAudience);

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
              sensitivity, recall_mode, confidence, freshness_at,
              status, created_at, updated_at)
            VALUES($id, $type, $name, $parent,
              $sensitivity, $recallMode, $confidence, $freshnessAt,
              $status, $createdAt, $updatedAt)
            ON CONFLICT(anchor_id) DO UPDATE SET
              anchor_type=excluded.anchor_type,
              canonical_name=excluded.canonical_name,
              parent_anchor_id=excluded.parent_anchor_id,
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

    private static async Task UpsertDocumentFtsAsync(
        SqliteConnection conn, SqliteTransaction? tx,
        string documentId, string title, string body,
        string? aliasesJson, string? facetsJson,
        CancellationToken ct)
    {
        await DeleteDocumentFtsAsync(conn, tx, documentId, ct);

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

    private static async Task UpsertRecordFtsAsync(
        SqliteConnection conn, SqliteTransaction? tx,
        string recordId, string title, string body,
        string? aliasesJson, string? facetsJson,
        CancellationToken ct)
    {
        await DeleteRecordFtsAsync(conn, tx, recordId, ct);

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

    private static async Task DeleteDocumentFtsAsync(
        SqliteConnection conn, SqliteTransaction? tx,
        string documentId,
        CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        if (tx is not null) cmd.Transaction = tx;
        cmd.CommandText = "DELETE FROM memory_documents_fts WHERE document_id = $id;";
        cmd.Parameters.AddWithValue("$id", documentId);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static async Task DeleteRecordFtsAsync(
        SqliteConnection conn, SqliteTransaction? tx,
        string recordId,
        CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        if (tx is not null) cmd.Transaction = tx;
        cmd.CommandText = "DELETE FROM memory_records_fts WHERE record_id = $id;";
        cmd.Parameters.AddWithValue("$id", recordId);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public SQLiteMemoryAnchor CreateDefaultAnchor(string canonicalName)
    {
        var nowMs = _timeProvider.GetUtcNow().ToUnixTimeMilliseconds();
        return new SQLiteMemoryAnchor(
            AnchorId: MemoryTypedId.AnchorId(canonicalName),
            AnchorType: "concept",
            CanonicalName: canonicalName,
            ParentAnchorId: null,
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
    string Sensitivity,
    string RecallMode,
    double Confidence,
    long? FreshnessAtMs,
    long? ExpiresAtMs,
    long CreatedAtMs,
    long UpdatedAtMs,
    string Boundary = TrustBoundary.LegacyRestrictedValue,
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
    string Sensitivity,
    string RecallMode,
    string Boundary = TrustBoundary.LegacyRestrictedValue,
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
    string Sensitivity,
    string RecallMode,
    string UpdateSemantics,
    long? ExpiresAtMs,
    long UpdatedAtMs,
    string Boundary = TrustBoundary.LegacyRestrictedValue,
    string Audience = "public");

public sealed record ResolvedMemoryHandle(
    string RawId,
    MemoryKind Kind,
    MemoryStorageId? StorageId,
    string? Error)
{
    public bool Resolved => StorageId is not null && Error is null;

    /// <summary>
    /// The canonical model-facing handle for this memory — its opaque storage id verbatim.
    /// Falls back to the raw input when resolution failed.
    /// </summary>
    public string Handle => StorageId?.Value ?? RawId;

    public static ResolvedMemoryHandle Found(string rawId, MemoryKind kind, MemoryStorageId storageId)
        => new(rawId, kind, storageId, null);

    public static ResolvedMemoryHandle Failed(string rawId, MemoryKind kind, string error)
        => new(rawId, kind, null, error);
}

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
    string Boundary,
    TrustAudience Audience,
    string Sensitivity,
    string RecallMode,
    double Confidence,
    long? FreshnessAtMs,
    long? ExpiresAtMs,
    string? SupersedesRecordId = null);

public sealed record SQLiteMemoryRelationOperation(
    string RelationType,
    string TargetCanonicalName,
    string TargetAnchorType,
    double Confidence);
