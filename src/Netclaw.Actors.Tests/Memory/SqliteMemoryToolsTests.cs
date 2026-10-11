// -----------------------------------------------------------------------
// <copyright file="SqliteMemoryToolsTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Microsoft.Extensions.Time.Testing;
using Netclaw.Actors.Memory;
using Netclaw.Configuration;
using Netclaw.Tools;
using Xunit;

namespace Netclaw.Actors.Tests.Memory;

public sealed class SqliteMemoryToolsTests : IAsyncDisposable
{
    private readonly string _baseDir = Path.Combine(Path.GetTempPath(), $"netclaw-sqlite-memory-tool-tests-{Guid.NewGuid():N}");
    private readonly string _dbPath;
    private readonly FakeTimeProvider _timeProvider;
    private readonly SQLiteMemoryStore _store;

    public SqliteMemoryToolsTests()
    {
        Directory.CreateDirectory(_baseDir);
        _dbPath = Path.Combine(_baseDir, "netclaw.db");
        _timeProvider = new FakeTimeProvider(DateTimeOffset.Parse("2026-03-09T12:00:00Z"));
        _store = new SQLiteMemoryStore(_dbPath, _timeProvider);
    }

    [Fact]
    public async Task FindMemories_returns_evidence_but_filters_trace_from_normal_results()
    {
        await _store.InitializeAsync(TestContext.Current.CancellationToken);
        var now = _timeProvider.GetUtcNow().ToUnixTimeMilliseconds();

        await _store.ApplyCurationBatchAsync(
            "cp-1",
            [
                new SQLiteMemoryCurationOperation(
                    Kind: "document",
                    MemoryClass: "durable_fact",
                    MemoryId: "doc-1",
                    AnchorCanonicalName: "stir trek",
                    AnchorType: "event",
                    Title: "Conference destination",
                    Content: "Stir Trek is in Columbus.",
                    AliasesJson: "[\"stir trek\",\"conference destination\"]",
                    FacetsJson: "[\"trip_planning\"]",
                    SlotsJson: null,
                    Relations: null,
                    UpdateSemantics: "merge-document",
                    Boundary: TrustBoundary.TrustedInstanceValue,
                    Audience: TrustAudience.Team,
                    Sensitivity: "normal",
                    RecallMode: "auto",
                    Confidence: 0.9,
                    FreshnessAtMs: now,
                    ExpiresAtMs: null),
                new SQLiteMemoryCurationOperation(
                    Kind: "record",
                    MemoryClass: "evidence",
                    MemoryId: "rec-1",
                    AnchorCanonicalName: "stir trek",
                    AnchorType: "event",
                    Title: "Hotel options",
                    Content: "Hilton Easton was recommended for Stir Trek.",
                    AliasesJson: "[\"hotel options\"]",
                    FacetsJson: "[\"trip_planning\"]",
                    SlotsJson: null,
                    Relations: null,
                    UpdateSemantics: "immutable-record",
                    Boundary: TrustBoundary.TrustedInstanceValue,
                    Audience: TrustAudience.Team,
                    Sensitivity: "normal",
                    RecallMode: "searchable",
                    Confidence: 0.8,
                    FreshnessAtMs: now,
                    ExpiresAtMs: now + (long)TimeSpan.FromDays(7).TotalMilliseconds),
                new SQLiteMemoryCurationOperation(
                    Kind: "record",
                    MemoryClass: "trace",
                    MemoryId: "rec-2",
                    AnchorCanonicalName: "stir trek",
                    AnchorType: "event",
                    Title: "Trace breadcrumb",
                    Content: "Investigated hotel search tool output.",
                    AliasesJson: null,
                    FacetsJson: null,
                    SlotsJson: null,
                    Relations: null,
                    UpdateSemantics: "conversation_trace",
                    Boundary: TrustBoundary.TrustedInstanceValue,
                    Audience: TrustAudience.Team,
                    Sensitivity: "normal",
                    RecallMode: "never",
                    Confidence: 0.5,
                    FreshnessAtMs: now,
                    ExpiresAtMs: now + (long)TimeSpan.FromDays(1).TotalMilliseconds)
            ],
            CancellationToken.None);

        var tool = new SqliteFindMemoriesTool(_store, _timeProvider);
        var result = await tool.ExecuteAsync(
            new Dictionary<string, object?>
            {
                ["Query"] = "stir trek hotel",
                ["Limit"] = 5
            },
            TestToolExecutionContext.CreateBound("slack/thread-1", sessionDirectory: null, TrustAudience.Personal),
            CancellationToken.None);

        Assert.Contains("Conference destination", result);
        Assert.Contains("Hotel options", result);
        Assert.Contains("class=evidence", result);
        Assert.DoesNotContain("Trace breadcrumb", result);
    }

    [Fact]
    public async Task GetMemories_marks_stale_evidence()
    {
        await _store.InitializeAsync(TestContext.Current.CancellationToken);
        var now = _timeProvider.GetUtcNow().ToUnixTimeMilliseconds();

        await _store.ApplyCurationBatchAsync(
            "cp-2",
            [
                new SQLiteMemoryCurationOperation(
                    Kind: "record",
                    MemoryClass: "evidence",
                    MemoryId: "rec-stale",
                    AnchorCanonicalName: "travel research",
                    AnchorType: "event",
                    Title: "Expired hotel note",
                    Content: "Old hotel rates from last month.",
                    AliasesJson: "[\"hotel rates\"]",
                    FacetsJson: "[\"trip_planning\"]",
                    SlotsJson: null,
                    Relations: null,
                    UpdateSemantics: "immutable-record",
                    Boundary: TrustBoundary.TrustedInstanceValue,
                    Audience: TrustAudience.Team,
                    Sensitivity: "normal",
                    RecallMode: "searchable",
                    Confidence: 0.7,
                    FreshnessAtMs: now - (long)TimeSpan.FromDays(30).TotalMilliseconds,
                    ExpiresAtMs: now - (long)TimeSpan.FromDays(1).TotalMilliseconds)
            ],
            CancellationToken.None);

        var tool = new SqliteGetMemoriesTool(_store, _timeProvider);
        var result = await tool.ExecuteAsync(
            new Dictionary<string, object?> { ["Ids"] = "rec:rec-stale" },
            TestToolExecutionContext.CreateBound("slack/thread-1", sessionDirectory: null, TrustAudience.Personal),
            CancellationToken.None);

        Assert.Contains("class=evidence", result);
        Assert.Contains("stale=true", result);
    }

    [Fact]
    public async Task FindMemories_hides_stale_evidence_unless_include_stale_is_true()
    {
        await _store.InitializeAsync(TestContext.Current.CancellationToken);
        var now = _timeProvider.GetUtcNow().ToUnixTimeMilliseconds();

        await _store.ApplyCurationBatchAsync(
            "cp-3",
            [
                new SQLiteMemoryCurationOperation(
                    Kind: "record",
                    MemoryClass: "evidence",
                    MemoryId: "rec-stale-find",
                    AnchorCanonicalName: "stir trek",
                    AnchorType: "event",
                    Title: "Old venue note",
                    Content: "Old parking instructions.",
                    AliasesJson: "[\"parking instructions\"]",
                    FacetsJson: "[\"trip_planning\"]",
                    SlotsJson: null,
                    Relations: null,
                    UpdateSemantics: "immutable-record",
                    Boundary: TrustBoundary.TrustedInstanceValue,
                    Audience: TrustAudience.Team,
                    Sensitivity: "normal",
                    RecallMode: "searchable",
                    Confidence: 0.7,
                    FreshnessAtMs: now - (long)TimeSpan.FromDays(30).TotalMilliseconds,
                    ExpiresAtMs: now - (long)TimeSpan.FromDays(1).TotalMilliseconds)
            ],
            CancellationToken.None);

        var tool = new SqliteFindMemoriesTool(_store, _timeProvider);

        var normal = await tool.ExecuteAsync(
            new Dictionary<string, object?>
            {
                ["Query"] = "stir trek parking",
                ["Limit"] = 5
            },
            TestToolExecutionContext.CreateBound("slack/thread-1", sessionDirectory: null, TrustAudience.Personal),
            CancellationToken.None);

        var debug = await tool.ExecuteAsync(
            new Dictionary<string, object?>
            {
                ["Query"] = "stir trek parking",
                ["Limit"] = 5,
                ["IncludeStale"] = true
            },
            TestToolExecutionContext.CreateBound("slack/thread-1", sessionDirectory: null, TrustAudience.Personal),
            CancellationToken.None);

        Assert.Equal("No memories found.", normal);
        Assert.Contains("Old venue note", debug);
        Assert.Contains("stale=true", debug);
    }

    [Fact]
    public async Task GetMemories_respects_active_boundary_and_audience()
    {
        await _store.InitializeAsync(TestContext.Current.CancellationToken);
        var now = _timeProvider.GetUtcNow().ToUnixTimeMilliseconds();

        await _store.ApplyCurationBatchAsync(
            "cp-4",
            [
                new SQLiteMemoryCurationOperation(
                    Kind: "document",
                    MemoryClass: "durable_fact",
                    MemoryId: "doc-team",
                    AnchorCanonicalName: "netclaw-repo",
                    AnchorType: "project",
                    Title: "Repository name",
                    Content: "This repository is Netclaw.",
                    AliasesJson: "[\"netclaw\"]",
                    FacetsJson: "[\"project_fact\"]",
                    SlotsJson: null,
                    Relations: null,
                    UpdateSemantics: "merge-document",
                    Sensitivity: "normal",
                    RecallMode: "auto",
                    Confidence: 0.9,
                    FreshnessAtMs: now,
                    ExpiresAtMs: null,
                    Boundary: TrustBoundary.TrustedInstanceValue,
                    Audience: TrustAudience.Team),
                new SQLiteMemoryCurationOperation(
                    Kind: "document",
                    MemoryClass: "durable_fact",
                    MemoryId: "doc-personal",
                    AnchorCanonicalName: "netclaw-security-note",
                    AnchorType: "project",
                    Title: "Security issue",
                    Content: "Investigating a private security issue in Netclaw.",
                    AliasesJson: "[\"security issue\"]",
                    FacetsJson: "[\"project_fact\"]",
                    SlotsJson: null,
                    Relations: null,
                    UpdateSemantics: "merge-document",
                    Sensitivity: "normal",
                    RecallMode: "auto",
                    Confidence: 0.9,
                    FreshnessAtMs: now,
                    ExpiresAtMs: null,
                    Boundary: TrustBoundary.TrustedInstanceValue,
                    Audience: TrustAudience.Personal)
            ],
            CancellationToken.None);

        var tool = new SqliteGetMemoriesTool(_store);
        var result = await tool.ExecuteAsync(
            new Dictionary<string, object?> { ["Ids"] = "doc:doc-team,doc:doc-personal" },
            TestToolExecutionContext.CreateBound("slack/thread-1", sessionDirectory: null, TrustAudience.Team),
            CancellationToken.None);

        Assert.Contains("Repository name", result);
        Assert.DoesNotContain("Security issue", result);
    }

    [Fact]
    public async Task GetMemories_accepts_legacy_raw_and_typed_storage_ids()
    {
        await _store.InitializeAsync(TestContext.Current.CancellationToken);
        var now = _timeProvider.GetUtcNow().ToUnixTimeMilliseconds();

        await StoreDocumentAsync(
            "doc-auto",
            "Auto recalled note",
            "This came from automatic recall.",
            now);

        var tool = new SqliteGetMemoriesTool(_store, _timeProvider);
        var rawResult = await tool.ExecuteAsync(
            new Dictionary<string, object?> { ["Ids"] = "doc-auto" },
            PersonalContext(),
            CancellationToken.None);
        var typedResult = await tool.ExecuteAsync(
            new Dictionary<string, object?> { ["Ids"] = "doc:doc-auto" },
            PersonalContext(),
            CancellationToken.None);

        // Both the raw storage id and a legacy doc: envelope are accepted as input, and the
        // output surfaces the storage id verbatim (no doc: envelope).
        Assert.Contains("Auto recalled note", rawResult);
        Assert.Contains("[doc-auto]", rawResult);
        Assert.DoesNotContain("doc:doc-auto", rawResult);
        Assert.Contains("Auto recalled note", typedResult);
        Assert.Contains("[doc-auto]", typedResult);
        Assert.DoesNotContain("doc:doc-auto", typedResult);
    }

    [Fact]
    public async Task UpdateMemory_edits_document_from_typed_recall_id_without_checkpoint_clobber()
    {
        await _store.InitializeAsync(TestContext.Current.CancellationToken);
        var now = _timeProvider.GetUtcNow().ToUnixTimeMilliseconds();
        await StoreDocumentAsync("doc-edit", "Favorite color", "The user's favorite color is blue.", now);

        var update = new SqliteUpdateMemoryTool(_store);
        var result = await update.ExecuteAsync(
            new Dictionary<string, object?>
            {
                ["id"] = "doc:doc-edit",
                ["old_text"] = "blue",
                ["new_text"] = "green"
            },
            PersonalContext(),
            CancellationToken.None);

        var get = new SqliteGetMemoriesTool(_store, _timeProvider);
        var hydrated = await get.ExecuteAsync(
            new Dictionary<string, object?> { ["ids"] = "doc:doc-edit" },
            PersonalContext(),
            CancellationToken.None);

        Assert.Contains("updated", result);
        Assert.Contains("green", hydrated);
        Assert.DoesNotContain("blue", hydrated);
        Assert.Equal(0, await _store.GetPendingCheckpointCountAsync(CancellationToken.None));
    }

    [Fact]
    public async Task UpdateMemory_replaces_document_content_from_legacy_raw_id()
    {
        await _store.InitializeAsync(TestContext.Current.CancellationToken);
        var now = _timeProvider.GetUtcNow().ToUnixTimeMilliseconds();
        await StoreDocumentAsync("doc-replace", "Working preference", "Use short replies.", now);

        var update = new SqliteUpdateMemoryTool(_store);
        var result = await update.ExecuteAsync(
            new Dictionary<string, object?>
            {
                ["id"] = "doc-replace",
                ["new_content"] = "Use direct replies with command examples when useful."
            },
            PersonalContext(),
            CancellationToken.None);

        var get = new SqliteGetMemoriesTool(_store, _timeProvider);
        var hydrated = await get.ExecuteAsync(
            new Dictionary<string, object?> { ["ids"] = "doc:doc-replace" },
            PersonalContext(),
            CancellationToken.None);

        Assert.Contains("updated", result);
        Assert.Contains("direct replies", hydrated);
        Assert.DoesNotContain("short replies", hydrated);
    }

    [Fact]
    public async Task UpdateMemory_tombstones_document_from_typed_recall_id()
    {
        await _store.InitializeAsync(TestContext.Current.CancellationToken);
        var now = _timeProvider.GetUtcNow().ToUnixTimeMilliseconds();
        await StoreDocumentAsync("doc-delete", "Delete me", "This memory should be removed from search.", now);

        var update = new SqliteUpdateMemoryTool(_store);
        var result = await update.ExecuteAsync(
            new Dictionary<string, object?>
            {
                ["id"] = "doc:doc-delete",
                ["delete"] = true
            },
            PersonalContext(),
            CancellationToken.None);

        var find = new SqliteFindMemoriesTool(_store, _timeProvider);
        var search = await find.ExecuteAsync(
            new Dictionary<string, object?> { ["query"] = "removed search", ["limit"] = 5 },
            PersonalContext(),
            CancellationToken.None);

        Assert.Contains("tombstoned", result);
        Assert.Equal("No memories found.", search);
    }

    [Fact]
    public async Task UpdateMemory_rejects_empty_new_content_without_wiping_document()
    {
        await _store.InitializeAsync(TestContext.Current.CancellationToken);
        var now = _timeProvider.GetUtcNow().ToUnixTimeMilliseconds();
        await StoreDocumentAsync("doc-guard", "Working preference", "Use short replies.", now);

        var update = new SqliteUpdateMemoryTool(_store);
        var result = await update.ExecuteAsync(
            new Dictionary<string, object?>
            {
                ["id"] = "doc:doc-guard",
                ["new_content"] = "   "
            },
            PersonalContext(),
            CancellationToken.None);

        var get = new SqliteGetMemoriesTool(_store, _timeProvider);
        var hydrated = await get.ExecuteAsync(
            new Dictionary<string, object?> { ["ids"] = "doc:doc-guard" },
            PersonalContext(),
            CancellationToken.None);

        Assert.Contains("new_content cannot be empty", result);
        Assert.Contains("Use short replies.", hydrated);
    }

    [Fact]
    public async Task UpdateMemory_rejects_old_text_for_record_with_specific_error()
    {
        await _store.InitializeAsync(TestContext.Current.CancellationToken);
        var now = _timeProvider.GetUtcNow().ToUnixTimeMilliseconds();
        await StoreRecordAsync("rec-guard", "Hotel note", "Hilton Easton was recommended.", now);

        var update = new SqliteUpdateMemoryTool(_store);
        var result = await update.ExecuteAsync(
            new Dictionary<string, object?>
            {
                ["id"] = "rec:rec-guard",
                ["old_text"] = "Hilton",
                ["new_text"] = "Marriott"
            },
            PersonalContext(),
            CancellationToken.None);

        Assert.Contains("records do not support old_text/new_text", result);
    }

    [Fact]
    public async Task UpdateMemory_record_edits_stay_readable_via_the_original_handle()
    {
        await _store.InitializeAsync(TestContext.Current.CancellationToken);
        var now = _timeProvider.GetUtcNow().ToUnixTimeMilliseconds();
        await StoreRecordAsync("rec-pref", "Coffee order", "The order is a flat white.", now);

        // Records are superseded (append-only), not edited in place. The stable handle the model
        // holds (rec-pref) must keep resolving to the CURRENT content across successive edits —
        // walking the supersede chain to the head — not the pre-edit or first-edit row.
        var update = new SqliteUpdateMemoryTool(_store);
        await update.ExecuteAsync(
            new Dictionary<string, object?> { ["id"] = "rec-pref", ["new_text"] = "The order is a cortado." },
            PersonalContext(),
            CancellationToken.None);
        var second = await update.ExecuteAsync(
            new Dictionary<string, object?> { ["id"] = "rec-pref", ["new_text"] = "The order is an espresso." },
            PersonalContext(),
            CancellationToken.None);

        var get = new SqliteGetMemoriesTool(_store, _timeProvider);
        var hydrated = await get.ExecuteAsync(
            new Dictionary<string, object?> { ["ids"] = "rec-pref" },
            PersonalContext(),
            CancellationToken.None);

        Assert.Contains("superseded", second);
        Assert.Contains("espresso", hydrated);
        Assert.DoesNotContain("flat white", hydrated);
        Assert.DoesNotContain("cortado", hydrated);
    }

    [Fact]
    public async Task UpdateMemory_edit_re_embeds_the_document_with_the_new_text()
    {
        await _store.InitializeAsync(TestContext.Current.CancellationToken);
        var holder = AvailableEmbedderHolder();
        await StoreEmbeddedDocumentAsync(holder, "doc-vec", "Favorite color", "The user's favorite color is blue.");
        Assert.Equal(
            await TextVectorEmbedder.VectorOf("Favorite color\nThe user's favorite color is blue."),
            Assert.Single(await _store.GetEmbeddingsForModelAsync(TextVectorEmbedder.Model, TestContext.Current.CancellationToken)).Vector.ToArray());

        var update = new SqliteUpdateMemoryTool(_store, holder);
        await update.ExecuteAsync(
            new Dictionary<string, object?> { ["id"] = "doc-vec", ["old_text"] = "blue", ["new_text"] = "green" },
            PersonalContext(),
            CancellationToken.None);
        await update.ExecuteAsync(
            new Dictionary<string, object?> { ["id"] = "doc-vec", ["new_content"] = "Prefers teal." },
            PersonalContext(),
            CancellationToken.None);

        var row = Assert.Single(await _store.GetEmbeddingsForModelAsync(TextVectorEmbedder.Model, TestContext.Current.CancellationToken));
        Assert.Equal(await TextVectorEmbedder.VectorOf("Favorite color\nPrefers teal."), row.Vector.ToArray());
        var coverage = await _store.GetEmbeddingCoverageAsync(TextVectorEmbedder.Model, TestContext.Current.CancellationToken);
        Assert.Equal(1, coverage.EmbeddedCurrentHashCount);
    }

    [Fact]
    public async Task UpdateMemory_find_and_replace_edit_stores_the_vector_of_the_new_text()
    {
        await _store.InitializeAsync(TestContext.Current.CancellationToken);
        var holder = AvailableEmbedderHolder();
        await StoreEmbeddedDocumentAsync(holder, "doc-vec", "Favorite color", "The user's favorite color is blue.");

        await new SqliteUpdateMemoryTool(_store, holder).ExecuteAsync(
            new Dictionary<string, object?> { ["id"] = "doc-vec", ["old_text"] = "blue", ["new_text"] = "green" },
            PersonalContext(),
            CancellationToken.None);

        var row = Assert.Single(await _store.GetEmbeddingsForModelAsync(TextVectorEmbedder.Model, TestContext.Current.CancellationToken));
        Assert.Equal(await TextVectorEmbedder.VectorOf("Favorite color\nThe user's favorite color is green."), row.Vector.ToArray());
    }

    [Fact]
    public async Task UpdateMemory_delete_removes_the_vector()
    {
        await _store.InitializeAsync(TestContext.Current.CancellationToken);
        var holder = AvailableEmbedderHolder();
        await StoreEmbeddedDocumentAsync(holder, "doc-vec", "Favorite color", "Blue.");

        await new SqliteUpdateMemoryTool(_store, holder).ExecuteAsync(
            new Dictionary<string, object?> { ["id"] = "doc-vec", ["delete"] = true },
            PersonalContext(),
            CancellationToken.None);

        Assert.Empty(await _store.GetEmbeddingsForModelAsync(TextVectorEmbedder.Model, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task UpdateMemory_edit_succeeds_without_a_vector_when_the_embedder_is_unavailable()
    {
        await _store.InitializeAsync(TestContext.Current.CancellationToken);
        await StoreDocumentAsync("doc-vec", "Favorite color", "Blue.", _timeProvider.GetUtcNow().ToUnixTimeMilliseconds());
        var unavailable = new MemoryEmbedderHolder(
            new UnavailableMemoryEmbedder(TextVectorEmbedder.Model, "not provisioned"), initialQueryPrefix: "", initialCalibratedMinCosineSimilarity: null);

        foreach (var tool in new[] { new SqliteUpdateMemoryTool(_store, unavailable), new SqliteUpdateMemoryTool(_store) })
        {
            var result = await tool.ExecuteAsync(
                new Dictionary<string, object?> { ["id"] = "doc-vec", ["old_text"] = "Blue", ["new_text"] = "Red" },
                PersonalContext(),
                CancellationToken.None);

            Assert.Contains("updated", result);
            Assert.Empty(await _store.GetEmbeddingsForModelAsync(TextVectorEmbedder.Model, TestContext.Current.CancellationToken));
            await _store.UpdateDocumentTextAsync("doc-vec", "Red", "Blue", TestContext.Current.CancellationToken);
        }

        // The existing gap-repair/backfill query still sees the edited document as needing a vector.
        var missing = await _store.GetDocumentsNeedingEmbeddingAsync(TextVectorEmbedder.Model, force: false, TestContext.Current.CancellationToken);
        Assert.Equal("doc-vec", Assert.Single(missing).DocumentId);
    }

    [Fact]
    public async Task UpdateMemory_overlapping_edits_leave_exactly_the_vector_of_the_final_text()
    {
        await _store.InitializeAsync(TestContext.Current.CancellationToken);
        var gated = new GatedTextVectorEmbedder(gatedText: "first edit");
        var holder = new MemoryEmbedderHolder(gated, initialQueryPrefix: "", initialCalibratedMinCosineSimilarity: null);
        await StoreEmbeddedDocumentAsync(holder, "doc-vec", "Favorite color", "Blue.");
        var update = new SqliteUpdateMemoryTool(_store, holder);

        // Edit A commits its text, then blocks inside the embed call.
        var editA = update.ExecuteAsync(
            new Dictionary<string, object?> { ["id"] = "doc-vec", ["new_content"] = "first edit" },
            PersonalContext(),
            CancellationToken.None);
        await gated.Entered.Task.WaitAsync(TestContext.Current.CancellationToken);

        // Edit B commits and embeds while A is still mid-embed.
        await update.ExecuteAsync(
            new Dictionary<string, object?> { ["id"] = "doc-vec", ["new_content"] = "second edit" },
            PersonalContext(),
            CancellationToken.None);

        // A resumes holding a vector for text that is no longer the document's text.
        gated.Release.SetResult();
        await editA.WaitAsync(TestContext.Current.CancellationToken);

        var rows = await _store.GetEmbeddingsForModelAsync(TextVectorEmbedder.Model, TestContext.Current.CancellationToken);
        Assert.Equal(await TextVectorEmbedder.VectorOf("Favorite color\nsecond edit"), Assert.Single(rows).Vector.ToArray());
    }

    [Fact]
    public async Task UpdateMemory_returns_only_after_the_new_vector_is_stored()
    {
        await _store.InitializeAsync(TestContext.Current.CancellationToken);
        var gated = new GatedTextVectorEmbedder(gatedText: "new body");
        var holder = new MemoryEmbedderHolder(gated, initialQueryPrefix: "", initialCalibratedMinCosineSimilarity: null);
        await StoreEmbeddedDocumentAsync(holder, "doc-vec", "Favorite color", "Blue.");

        var edit = new SqliteUpdateMemoryTool(_store, holder).ExecuteAsync(
            new Dictionary<string, object?> { ["id"] = "doc-vec", ["new_content"] = "new body" },
            PersonalContext(),
            CancellationToken.None);
        await gated.Entered.Task.WaitAsync(TestContext.Current.CancellationToken);
        gated.Release.SetResult();
        await edit.WaitAsync(TestContext.Current.CancellationToken);

        var rows = await _store.GetEmbeddingsForModelAsync(TextVectorEmbedder.Model, TestContext.Current.CancellationToken);
        Assert.Equal(await TextVectorEmbedder.VectorOf("Favorite color\nnew body"), Assert.Single(rows).Vector.ToArray());
    }

    private static MemoryEmbedderHolder AvailableEmbedderHolder()
        => new(new TextVectorEmbedder(), initialQueryPrefix: "", initialCalibratedMinCosineSimilarity: null);

    private async Task StoreEmbeddedDocumentAsync(MemoryEmbedderHolder holder, string id, string title, string content)
    {
        await StoreDocumentAsync(id, title, content, _timeProvider.GetUtcNow().ToUnixTimeMilliseconds());
        await MemoryEmbedOnWriteCoordinator.EmbedWrittenDocumentsAsync(
            holder, _store, [new MemoryDocumentWriteResult(id, title, content)],
            Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance, TestContext.Current.CancellationToken);
    }

    /// <summary>Deterministic embedder whose vector depends on the exact text, so a stale vector is distinguishable from a fresh one.</summary>
    private sealed class TextVectorEmbedder : IMemoryEmbedder
    {
        public const string Model = "text-vector-model";

        public string ModelId => Model;

        public int Dimensions => 2;

        public bool IsAvailable => true;

        public static async Task<float[]> VectorOf(string text)
            => (await new TextVectorEmbedder().EmbedAsync(text, EmbeddingPurpose.Passage, CancellationToken.None)).ToArray();

        // Yields so the call completes asynchronously, as the real ONNX embedder's does.
        public async ValueTask<ReadOnlyMemory<float>> EmbedAsync(string text, EmbeddingPurpose purpose, CancellationToken ct)
        {
            await Task.Yield();
            return new float[] { text.Length, text.Sum(c => c) % 9973 };
        }

        public async ValueTask<IReadOnlyList<ReadOnlyMemory<float>>> EmbedBatchAsync(IReadOnlyList<string> texts, EmbeddingPurpose purpose, CancellationToken ct)
        {
            var results = new List<ReadOnlyMemory<float>>(texts.Count);
            foreach (var text in texts)
                results.Add(await EmbedAsync(text, purpose, ct));
            return results;
        }
    }

    /// <summary>Embedder that blocks the first call whose text contains <c>gatedText</c> until the test releases it.</summary>
    private sealed class GatedTextVectorEmbedder(string gatedText) : IMemoryEmbedder
    {
        private readonly TextVectorEmbedder _inner = new();

        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public string ModelId => _inner.ModelId;

        public int Dimensions => _inner.Dimensions;

        public bool IsAvailable => true;

        public async ValueTask<ReadOnlyMemory<float>> EmbedAsync(string text, EmbeddingPurpose purpose, CancellationToken ct)
        {
            if (text.Contains(gatedText, StringComparison.Ordinal) && Entered.TrySetResult())
                await Release.Task.WaitAsync(ct);
            return await _inner.EmbedAsync(text, purpose, ct);
        }

        public async ValueTask<IReadOnlyList<ReadOnlyMemory<float>>> EmbedBatchAsync(IReadOnlyList<string> texts, EmbeddingPurpose purpose, CancellationToken ct)
        {
            var results = new List<ReadOnlyMemory<float>>(texts.Count);
            foreach (var text in texts)
                results.Add(await EmbedAsync(text, purpose, ct));
            return results;
        }
    }

    public async ValueTask DisposeAsync()
    {
        await SqliteTempDirectoryCleanup.TryDeleteDirectoryAsync(_baseDir);
    }

    private async Task StoreDocumentAsync(string id, string title, string content, long now)
    {
        await _store.ApplyCurationBatchAsync(
            $"cp-{id}",
            [
                new SQLiteMemoryCurationOperation(
                    Kind: MemoryKind.Document.ToWireValue(),
                    MemoryClass: MemoryClass.DurableFact.ToWireValue(),
                    MemoryId: id,
                    AnchorCanonicalName: title,
                    AnchorType: "concept",
                    Title: title,
                    Content: content,
                    AliasesJson: null,
                    FacetsJson: null,
                    SlotsJson: null,
                    Relations: null,
                    UpdateSemantics: MemoryUpdateSemantics.MergeDocument.ToWireValue(),
                    Boundary: TrustBoundary.TrustedInstanceValue,
                    Audience: TrustAudience.Team,
                    Sensitivity: MemorySensitivity.Normal.ToWireValue(),
                    RecallMode: MemoryRecallMode.Auto.ToWireValue(),
                    Confidence: 0.9,
                    FreshnessAtMs: now,
                    ExpiresAtMs: null)
            ],
            CancellationToken.None);
    }

    private async Task StoreRecordAsync(string id, string title, string content, long now)
    {
        await _store.ApplyCurationBatchAsync(
            $"cp-{id}",
            [
                new SQLiteMemoryCurationOperation(
                    Kind: MemoryKind.Record.ToWireValue(),
                    MemoryClass: MemoryClass.Evidence.ToWireValue(),
                    MemoryId: id,
                    AnchorCanonicalName: title,
                    AnchorType: "concept",
                    Title: title,
                    Content: content,
                    AliasesJson: null,
                    FacetsJson: null,
                    SlotsJson: null,
                    Relations: null,
                    UpdateSemantics: MemoryUpdateSemantics.SupersedeRecord.ToWireValue(),
                    Boundary: TrustBoundary.TrustedInstanceValue,
                    Audience: TrustAudience.Team,
                    Sensitivity: MemorySensitivity.Normal.ToWireValue(),
                    RecallMode: MemoryRecallMode.Searchable.ToWireValue(),
                    Confidence: 0.9,
                    FreshnessAtMs: now,
                    ExpiresAtMs: null)
            ],
            CancellationToken.None);
    }

    private static ToolExecutionContext PersonalContext()
        => TestToolExecutionContext.CreateBound("slack/thread-1", null, new TestToolExecutionContextOptions
        { Audience = TrustAudience.Personal });

}
