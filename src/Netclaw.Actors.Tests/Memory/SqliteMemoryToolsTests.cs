using Netclaw.Actors.Memory;
using Netclaw.Configuration;
using Netclaw.Tools;
using Xunit;

namespace Netclaw.Actors.Tests.Memory;

public sealed class SqliteMemoryToolsTests : IDisposable
{
    private readonly string _baseDir = Path.Combine(Path.GetTempPath(), "netclaw-sqlite-memory-tool-tests", Guid.NewGuid().ToString("N"));
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
                    Domain: "project:slack",
                    Boundary: SecurityPolicyDefaults.TrustedInstanceBoundary,
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
                    Domain: "project:slack",
                    Boundary: SecurityPolicyDefaults.TrustedInstanceBoundary,
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
                    Domain: "project:slack",
                    Boundary: SecurityPolicyDefaults.TrustedInstanceBoundary,
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
            new ToolExecutionContext("slack/thread-1", sessionDirectory: null),
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
                    Domain: "project:slack",
                    Boundary: SecurityPolicyDefaults.TrustedInstanceBoundary,
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
            new ToolExecutionContext("slack/thread-1", sessionDirectory: null),
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
                    Domain: "project:slack",
                    Boundary: SecurityPolicyDefaults.TrustedInstanceBoundary,
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
            new ToolExecutionContext("slack/thread-1", sessionDirectory: null),
            CancellationToken.None);

        var debug = await tool.ExecuteAsync(
            new Dictionary<string, object?>
            {
                ["Query"] = "stir trek parking",
                ["Limit"] = 5,
                ["IncludeStale"] = true
            },
            new ToolExecutionContext("slack/thread-1", sessionDirectory: null),
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
                    Domain: "project:netclaw",
                    Sensitivity: "normal",
                    RecallMode: "auto",
                    Confidence: 0.9,
                    FreshnessAtMs: now,
                    ExpiresAtMs: null,
                    Boundary: SecurityPolicyDefaults.TrustedInstanceBoundary,
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
                    Domain: "project:netclaw",
                    Sensitivity: "normal",
                    RecallMode: "auto",
                    Confidence: 0.9,
                    FreshnessAtMs: now,
                    ExpiresAtMs: null,
                    Boundary: SecurityPolicyDefaults.TrustedInstanceBoundary,
                    Audience: TrustAudience.Personal)
            ],
            CancellationToken.None);

        var tool = new SqliteGetMemoriesTool(_store);
        var result = await tool.ExecuteAsync(
            new Dictionary<string, object?> { ["Ids"] = "doc:doc-team,doc:doc-personal" },
            new ToolExecutionContext("slack/thread-1", sessionDirectory: null),
            CancellationToken.None);

        Assert.Contains("Repository name", result);
        Assert.DoesNotContain("Security issue", result);
    }

    public void Dispose()
    {
        if (Directory.Exists(_baseDir))
            Directory.Delete(_baseDir, recursive: true);
    }

    private sealed class FakeTimeProvider(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;

        public override DateTimeOffset GetUtcNow() => _now;
    }
}
