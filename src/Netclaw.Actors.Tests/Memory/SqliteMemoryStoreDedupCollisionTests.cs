// -----------------------------------------------------------------------
// <copyright file="SqliteMemoryStoreDedupCollisionTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Time.Testing;
using Netclaw.Actors.Memory;
using Netclaw.Configuration;
using Xunit;

namespace Netclaw.Actors.Tests.Memory;

/// <summary>
/// Regression coverage for the anchor-based dedup collision bug: a curation Create
/// decision (or any no-MemoryId, merge-document proposal) that lands on an anchor which
/// already has a document used to be written via a blind <c>INSERT ... ON CONFLICT DO
/// UPDATE</c>, silently overwriting the existing row's title/body/classification with the
/// new proposal's raw values. The rules tier (<see cref="CurationRulesEvaluator"/>) can
/// legitimately emit Create for "different topic, similar anchor name" (fuzzy anchor match,
/// low content overlap), so this collision is a real, reachable production path — not a
/// theoretical one (audit: 88 silent overwrites/14 days, including a destroyed
/// LLM-merged memory). Both batch appliers (<see cref="SQLiteMemoryStore.ApplyCurationBatchAsync"/>,
/// used by the daemon checkpoint worker, and <see cref="SQLiteMemoryStore.ApplyInlineCurationBatchAsync"/>,
/// used by the inline per-session actor) share the identical dedup logic and must both
/// preserve the existing document by appending instead of overwriting. Also covers the
/// idempotency guard on that path: a collision whose content is already present verbatim
/// is a logged no-op (curation_dedup_duplicate_skipped), not a repeated append.
/// </summary>
public sealed class SqliteMemoryStoreDedupCollisionTests : IAsyncDisposable
{
    private readonly string _baseDir = Path.Combine(Path.GetTempPath(), "netclaw-dedup-collision-tests", Guid.NewGuid().ToString("N"));
    private readonly string _dbPath;
    private readonly FakeTimeProvider _timeProvider;
    private readonly SQLiteMemoryStore _store;

    public SqliteMemoryStoreDedupCollisionTests()
    {
        Directory.CreateDirectory(_baseDir);
        _dbPath = Path.Combine(_baseDir, "netclaw.db");
        _timeProvider = new FakeTimeProvider(DateTimeOffset.Parse("2026-06-01T09:00:00Z"));
        _store = new SQLiteMemoryStore(_dbPath, _timeProvider);
    }

    public async ValueTask DisposeAsync() => await SqliteTempDirectoryCleanup.TryDeleteDirectoryAsync(_baseDir);

    [Fact]
    public async Task ApplyCurationBatchAsync_CreateCollidesWithExistingAnchorDocument_AppendsInsteadOfOverwriting()
    {
        var ct = TestContext.Current.CancellationToken;
        await _store.InitializeAsync(ct);

        var createdAt = await SeedExistingDocumentAsync("doc-existing", "project-quasar", ct);

        // Time advances between the seed write and the colliding batch, so updated_at
        // must move while created_at (excluded from the ON CONFLICT SET clause) does not.
        _timeProvider.Advance(TimeSpan.FromHours(3));

        var written = await _store.ApplyCurationBatchAsync(
            "cp-collision-1",
            [MakeColliderOperation("project-quasar")],
            ct);

        var row = await ReadDocumentAsync("doc-existing", ct);

        Assert.Contains("Quasar uses a Postgres 15 read replica for reporting.", row.MarkdownBody);
        Assert.Contains("Quasar's on-call rotation moved to PagerDuty last sprint.", row.MarkdownBody);
        Assert.Contains("---", row.MarkdownBody);
        Assert.Matches(@"_\[merged \d{4}-\d{2}-\d{2}\]_", row.MarkdownBody);

        // Identity/classification of the existing row must survive the collision verbatim.
        Assert.Equal("Project Quasar datastore", row.Title);
        Assert.Equal(MemorySensitivity.Secret.ToWireValue(), row.Sensitivity);
        Assert.Equal(TrustAudience.Personal.ToWireValue(), row.Audience);
        Assert.Equal(TrustBoundary.PersonalValue, row.Boundary);

        Assert.Equal(MemoryUpdateSemantics.AppendDocument.ToWireValue(), row.UpdateSemantics);
        Assert.Equal(createdAt, row.CreatedAtMs);
        Assert.True(row.UpdatedAtMs > createdAt);
        var result = Assert.Single(written);
        Assert.Equal(row.Title, result.Title);
        Assert.Equal(row.MarkdownBody, result.Body);
    }

    [Fact]
    public async Task ApplyInlineCurationBatchAsync_CreateCollidesWithExistingAnchorDocument_AppendsInsteadOfOverwriting()
    {
        var ct = TestContext.Current.CancellationToken;
        await _store.InitializeAsync(ct);

        var createdAt = await SeedExistingDocumentAsync("doc-existing-inline", "project-nebula", ct);

        _timeProvider.Advance(TimeSpan.FromHours(3));

        var written = await _store.ApplyInlineCurationBatchAsync(
            [MakeColliderOperation("project-nebula")],
            ct);

        var row = await ReadDocumentAsync("doc-existing-inline", ct);

        Assert.Contains("Quasar uses a Postgres 15 read replica for reporting.", row.MarkdownBody);
        Assert.Contains("Quasar's on-call rotation moved to PagerDuty last sprint.", row.MarkdownBody);
        Assert.Matches(@"_\[merged \d{4}-\d{2}-\d{2}\]_", row.MarkdownBody);

        Assert.Equal("Project Quasar datastore", row.Title);
        Assert.Equal(MemorySensitivity.Secret.ToWireValue(), row.Sensitivity);
        Assert.Equal(TrustAudience.Personal.ToWireValue(), row.Audience);
        Assert.Equal(TrustBoundary.PersonalValue, row.Boundary);

        Assert.Equal(MemoryUpdateSemantics.AppendDocument.ToWireValue(), row.UpdateSemantics);
        Assert.Equal(createdAt, row.CreatedAtMs);
        Assert.True(row.UpdatedAtMs > createdAt);
        var result = Assert.Single(written);
        Assert.Equal(row.Title, result.Title);
        Assert.Equal(row.MarkdownBody, result.Body);
    }

    [Fact]
    public async Task ApplyCurationBatchAsync_CollisionWithVerbatimDuplicateContent_LeavesRowUnchanged()
    {
        var ct = TestContext.Current.CancellationToken;
        await _store.InitializeAsync(ct);

        var createdAt = await SeedExistingDocumentAsync("doc-dup", "project-vega", ct);
        _timeProvider.Advance(TimeSpan.FromHours(3));

        // Content is verbatim already present in the seeded body: appending it would be
        // pure bloat, so the write must be a logged no-op — the row stays byte-identical.
        await _store.ApplyCurationBatchAsync(
            "cp-dup-1",
            [MakeColliderOperation("project-vega", content: "Quasar uses a Postgres 15 read replica for reporting.")],
            ct);

        var row = await ReadDocumentAsync("doc-dup", ct);
        Assert.Equal("Quasar uses a Postgres 15 read replica for reporting.", row.MarkdownBody);
        Assert.Equal("Project Quasar datastore", row.Title);
        Assert.Equal(MemoryUpdateSemantics.MergeDocument.ToWireValue(), row.UpdateSemantics);
        Assert.Equal(createdAt, row.CreatedAtMs);
        Assert.Equal(createdAt, row.UpdatedAtMs);
    }

    [Fact]
    public async Task ApplyInlineCurationBatchAsync_CollisionWithVerbatimDuplicateContent_LeavesRowUnchanged()
    {
        var ct = TestContext.Current.CancellationToken;
        await _store.InitializeAsync(ct);

        var createdAt = await SeedExistingDocumentAsync("doc-dup-inline", "project-lyra", ct);
        _timeProvider.Advance(TimeSpan.FromHours(3));

        await _store.ApplyInlineCurationBatchAsync(
            [MakeColliderOperation("project-lyra", content: "Quasar uses a Postgres 15 read replica for reporting.")],
            ct);

        var row = await ReadDocumentAsync("doc-dup-inline", ct);
        Assert.Equal("Quasar uses a Postgres 15 read replica for reporting.", row.MarkdownBody);
        Assert.Equal("Project Quasar datastore", row.Title);
        Assert.Equal(MemoryUpdateSemantics.MergeDocument.ToWireValue(), row.UpdateSemantics);
        Assert.Equal(createdAt, row.CreatedAtMs);
        Assert.Equal(createdAt, row.UpdatedAtMs);
    }

    [Fact]
    public async Task ApplyCurationBatchAsync_CreateWithNoCollision_InsertsFreshDocumentUnchanged()
    {
        var ct = TestContext.Current.CancellationToken;
        await _store.InitializeAsync(ct);

        // No document exists under "project-comet" yet, so this Create-shaped proposal
        // (no MemoryId, merge-document semantics) must be a plain insert: the dedup lookup
        // finds nothing, so none of the collision/append machinery should engage.
        var operation = MakeColliderOperation("project-comet");
        await _store.ApplyCurationBatchAsync("cp-no-collision", [operation], ct);

        var anchorId = MemoryTypedId.AnchorId("project-comet");
        var documentId = await ReadDocumentIdForAnchorAsync(anchorId, ct);
        Assert.NotNull(documentId);

        var row = await ReadDocumentAsync(documentId!, ct);
        Assert.Equal(operation.Title, row.Title);
        Assert.Equal(operation.Content, row.MarkdownBody);
        Assert.DoesNotContain("merged", row.MarkdownBody);
        Assert.Equal(MemoryUpdateSemantics.MergeDocument.ToWireValue(), row.UpdateSemantics);
        Assert.Equal(operation.Sensitivity, row.Sensitivity);
    }

    // ── helpers ──────────────────────────────────────────────────────

    /// <summary>
    /// Seeds an existing document under <paramref name="anchorCanonicalName"/> with a
    /// distinct title/body/classification so the collision test can prove every one of
    /// those values survives the append (rather than being replaced by the colliding
    /// proposal's own values). Returns the seeded row's created_at for the created_at-
    /// unchanged assertion.
    /// </summary>
    private async Task<long> SeedExistingDocumentAsync(string documentId, string anchorCanonicalName, CancellationToken ct)
    {
        var anchor = _store.CreateDefaultAnchor(anchorCanonicalName);
        var createdAt = _timeProvider.GetUtcNow().ToUnixTimeMilliseconds();

        await _store.UpsertDocumentAsync(new SQLiteMemoryDocument(
            DocumentId: documentId,
            Anchor: anchor,
            MemoryClass: "durable_fact",
            Title: "Project Quasar datastore",
            MarkdownBody: "Quasar uses a Postgres 15 read replica for reporting.",
            AliasesJson: null,
            FacetsJson: null,
            SlotsJson: null,
            UpdateSemantics: MemoryUpdateSemantics.MergeDocument.ToWireValue(),
            Sensitivity: MemorySensitivity.Secret.ToWireValue(),
            RecallMode: "auto",
            Confidence: 0.9,
            FreshnessAtMs: createdAt,
            ExpiresAtMs: null,
            CreatedAtMs: createdAt,
            UpdatedAtMs: createdAt,
            Boundary: TrustBoundary.PersonalValue,
            Audience: TrustAudience.Personal.ToWireValue()), ct);

        return createdAt;
    }

    /// <summary>
    /// A no-MemoryId, merge-document-semantics proposal — the shape a Create decision
    /// takes by the time it reaches the store (see MemoryCurationEvaluator.ApplyDecisionAsync,
    /// case CurationDecisionKind.Create: returns the operation unchanged). Content deliberately
    /// shares no words with the seeded body, characterizing "fuzzy anchor match but low content
    /// overlap": a genuinely different topic that happens to land on the same anchor.
    /// Sensitivity/audience/boundary are deliberately public/normal — the OPPOSITE of the
    /// seeded row's secret/personal/personal-boundary — so a test that only passed by
    /// coincidence (both sides equal) would not slip through.
    /// </summary>
    private static SQLiteMemoryCurationOperation MakeColliderOperation(
        string anchorCanonicalName,
        string content = "Quasar's on-call rotation moved to PagerDuty last sprint.") =>
        new(
            Kind: "document",
            MemoryClass: "durable_fact",
            MemoryId: null,
            AnchorCanonicalName: anchorCanonicalName,
            AnchorType: "concept",
            Title: "On-call rotation",
            Content: content,
            AliasesJson: null,
            FacetsJson: null,
            SlotsJson: null,
            Relations: null,
            UpdateSemantics: MemoryUpdateSemantics.MergeDocument.ToWireValue(),
            Boundary: TrustBoundary.PublicValue,
            Audience: TrustAudience.Public,
            Sensitivity: MemorySensitivity.Normal.ToWireValue(),
            RecallMode: "auto",
            Confidence: 0.9,
            FreshnessAtMs: null,
            ExpiresAtMs: null);

    private sealed record DocumentRow(
        string Title,
        string MarkdownBody,
        string UpdateSemantics,
        string? Boundary,
        string? Audience,
        string Sensitivity,
        long CreatedAtMs,
        long UpdatedAtMs);

    private async Task<DocumentRow> ReadDocumentAsync(string documentId, CancellationToken ct)
    {
        await using var conn = new SqliteConnection($"Data Source={_dbPath}");
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT title, markdown_body, update_semantics, boundary, audience, sensitivity, created_at, updated_at
            FROM memory_documents
            WHERE document_id = $id;
            """;
        cmd.Parameters.AddWithValue("$id", documentId);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        var found = await reader.ReadAsync(ct);
        Assert.True(found, $"Expected document row '{documentId}' to exist.");

        return new DocumentRow(
            reader.GetString(0),
            reader.GetString(1),
            reader.GetString(2),
            reader.IsDBNull(3) ? null : reader.GetString(3),
            reader.IsDBNull(4) ? null : reader.GetString(4),
            reader.GetString(5),
            reader.GetInt64(6),
            reader.GetInt64(7));
    }

    private async Task<string?> ReadDocumentIdForAnchorAsync(string anchorId, CancellationToken ct)
    {
        await using var conn = new SqliteConnection($"Data Source={_dbPath}");
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT document_id FROM memory_documents WHERE anchor_id = $anchorId";
        cmd.Parameters.AddWithValue("$anchorId", anchorId);
        return (string?)await cmd.ExecuteScalarAsync(ct);
    }
}
