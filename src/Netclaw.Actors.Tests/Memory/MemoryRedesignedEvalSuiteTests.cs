using Microsoft.Extensions.Logging.Abstractions;
using Netclaw.Actors.Memory;
using Netclaw.Actors.Sessions;
using Netclaw.Configuration;
using Netclaw.Tools;
using Xunit;

namespace Netclaw.Actors.Tests.Memory;

public sealed class MemoryRedesignedEvalSuiteTests : IDisposable
{
    private readonly string _baseDir = Path.Combine(Path.GetTempPath(), "netclaw-memory-redesigned-evals", Guid.NewGuid().ToString("N"));
    private readonly string _dbPath;
    private readonly FakeEvalTimeProvider _timeProvider;
    private readonly SQLiteMemoryStore _store;

    public MemoryRedesignedEvalSuiteTests()
    {
        Directory.CreateDirectory(_baseDir);
        _dbPath = Path.Combine(_baseDir, "netclaw-memory-redesigned-evals.db");
        _timeProvider = new FakeEvalTimeProvider(DateTimeOffset.Parse("2026-03-10T12:00:00Z"));
        _store = new SQLiteMemoryStore(_dbPath, _timeProvider);
    }

    [Fact]
    public async Task Formation_then_auto_recall_surfaces_durable_fact()
    {
        await _store.InitializeAsync();
        var now = _timeProvider.GetUtcNow().ToUnixTimeMilliseconds();
        var gate = new MemoryProposalGate();

        var gateResult = gate.Evaluate(
        [
            new MemoryProposal(
                "upsert_document",
                "durable_fact",
                "user",
                "self",
                new MemoryAnchor("user-travel-airline", "preference"),
                "Travel Profile: Preferred Airline",
                "Preferred airline: United Airlines",
                ["preferred airline", "united airlines"],
                ["travel_profile", "user_preference"],
                null,
                null,
                "auto",
                "normal",
                0.95,
                now,
                null,
                null,
                "strong user assertion")
        ],
        "project:slack",
        "normal",
        now);

        await _store.ApplyCurationBatchAsync("cp-eval-1", gateResult.MemoryOperations, CancellationToken.None);

        var recall = new SQLiteMemoryRecallCoordinator(
            _store,
            NullLogger<SQLiteMemoryRecallCoordinator>.Instance,
            sessionConfig: new SessionConfig { MemorySidecarsEnabled = true });

        var result = await recall.RecallAsync(new AutomaticRecallRequest(
            "slack/thread-1",
            "what airline do I usually use",
            ["I usually fly United"],
            3));

        Assert.False(result.Degraded);
        Assert.Contains(result.Items, x => x.Content.Contains("United Airlines", StringComparison.Ordinal));
        Assert.Single(gateResult.MemoryOperations);
    }

    [Fact]
    public async Task Formation_then_intentional_search_returns_evidence_without_auto_recall_leakage()
    {
        await _store.InitializeAsync();
        var now = _timeProvider.GetUtcNow().ToUnixTimeMilliseconds();

        await _store.ApplyCurationBatchAsync(
            "cp-eval-2",
            [
                new SQLiteMemoryCurationOperation(
                    Kind: "document",
                    MemoryClass: "durable_fact",
                    MemoryId: "doc-hotel-city",
                    AnchorCanonicalName: "stir trek",
                    AnchorType: "event",
                    Title: "Conference destination",
                    Content: "Stir Trek is in Columbus.",
                    AliasesJson: "[\"stir trek\",\"conference destination\"]",
                    FacetsJson: "[\"trip_planning\"]",
                    UpdateSemantics: "merge-document",
                    Domain: "project:slack",
                    Sensitivity: "normal",
                    RecallMode: "auto",
                    Confidence: 0.9,
                    FreshnessAtMs: now,
                    ExpiresAtMs: null),
                new SQLiteMemoryCurationOperation(
                    Kind: "record",
                    MemoryClass: "evidence",
                    MemoryId: "rec-hotel-evidence",
                    AnchorCanonicalName: "stir trek",
                    AnchorType: "event",
                    Title: "Hotel options",
                    Content: "Hilton Easton is close to the venue.",
                    AliasesJson: "[\"hotel options\"]",
                    FacetsJson: "[\"trip_planning\"]",
                    UpdateSemantics: "immutable-record",
                    Domain: "project:slack",
                    Sensitivity: "normal",
                    RecallMode: "searchable",
                    Confidence: 0.8,
                    FreshnessAtMs: now,
                    ExpiresAtMs: now + (long)TimeSpan.FromDays(7).TotalMilliseconds)
            ],
            CancellationToken.None);

        var recall = new SQLiteMemoryRecallCoordinator(
            _store,
            NullLogger<SQLiteMemoryRecallCoordinator>.Instance,
            sessionConfig: new SessionConfig { MemorySidecarsEnabled = true });

        var auto = await recall.RecallAsync(new AutomaticRecallRequest(
            "slack/thread-2",
            "where should I stay",
            ["where should I stay near Stir Trek"],
            3));

        Assert.DoesNotContain(auto.Items, x => x.Id == "rec-hotel-evidence");

        var tool = new SqliteFindMemoriesTool(_store, _timeProvider);
        var search = await tool.ExecuteAsync(
            new Dictionary<string, object?>
            {
                ["Query"] = "stir trek hotel",
                ["Limit"] = 5
            },
            new ToolExecutionContext("slack/thread-2", null),
            CancellationToken.None);

        Assert.Contains("Hotel options", search);
    }

    [Fact]
    public void Proposal_gate_rejection_blocks_invalid_or_identity_violating_proposals()
    {
        var gate = new MemoryProposalGate();
        var now = _timeProvider.GetUtcNow().ToUnixTimeMilliseconds();

        var gateResult = gate.Evaluate(
        [
            new MemoryProposal(
                "ignore",
                "durable_fact",
                "user",
                "self",
                new MemoryAnchor("ignored", "concept"),
                "Ignored",
                "Should not persist",
                ["ignored"],
                ["project_fact"],
                null,
                null,
                "auto",
                "normal",
                0.8,
                now,
                null,
                null,
                "invalid op"),
            new MemoryProposal(
                "upsert_document",
                "evidence",
                "event",
                "stir trek",
                new MemoryAnchor("stir-trek", "event"),
                "Identity profile update",
                "Research note should not route to identity",
                ["research note"],
                ["trip_planning"],
                null,
                null,
                "searchable",
                "normal",
                0.7,
                now,
                null,
                "identity_profile",
                "research passage"),
            new MemoryProposal(
                "upsert_document",
                "durable_fact",
                "assistant",
                "self",
                new MemoryAnchor("assistant-communication-style", "preference"),
                "Communication style",
                "Prefer concise responses.",
                ["communication preference"],
                ["user_preference"],
                null,
                null,
                "auto",
                "normal",
                0.9,
                now,
                null,
                "identity_profile",
                "standing communication preference")
        ],
        "project:test",
        "normal",
        now);

        Assert.Empty(gateResult.MemoryOperations);
        var acceptedItem = Assert.Single(gateResult.IdentityUpdates);
        Assert.Equal("Communication style", acceptedItem.Title);
    }

    [Fact]
    public async Task Soul_boundary_keeps_project_facts_in_sqlite_memory()
    {
        await _store.InitializeAsync();
        var now = _timeProvider.GetUtcNow().ToUnixTimeMilliseconds();
        var gate = new MemoryProposalGate();

        var accepted = gate.Accept(
        [
            new MemoryProposal(
                "upsert_document",
                "durable_fact",
                "project",
                "netclaw",
                new MemoryAnchor("netclaw-deployment-region", "project"),
                "Deployment region",
                "Netclaw deploys in us-east-2.",
                ["deployment region"],
                ["project_fact"],
                null,
                null,
                "auto",
                "normal",
                0.9,
                now,
                null,
                "identity_profile",
                "project fact"),
            new MemoryProposal(
                "upsert_document",
                "durable_fact",
                "project",
                "netclaw",
                new MemoryAnchor("netclaw-deployment-region", "project"),
                "Deployment region",
                "Netclaw deploys in us-east-2.",
                ["deployment region"],
                ["project_fact"],
                null,
                null,
                "auto",
                "normal",
                0.9,
                now,
                null,
                null,
                "project fact")
        ],
        "project:ops",
        "normal",
        now);

        await _store.ApplyCurationBatchAsync("cp-eval-3", accepted, CancellationToken.None);
        var items = await _store.SearchByPlanAsync(["deploys", "east-2"], "project:ops", ["durable_fact"], 5, false);

        Assert.Single(items);
        Assert.Equal("Deployment region", items[0].Title);
    }

    [Fact]
    public async Task Expiry_and_staleness_hides_expired_evidence_by_default_but_allows_debug_search()
    {
        await _store.InitializeAsync();
        var now = _timeProvider.GetUtcNow().ToUnixTimeMilliseconds();

        await _store.ApplyCurationBatchAsync(
            "cp-eval-4",
            [
                new SQLiteMemoryCurationOperation(
                    Kind: "record",
                    MemoryClass: "evidence",
                    MemoryId: "rec-expired-eval",
                    AnchorCanonicalName: "stir trek",
                    AnchorType: "event",
                    Title: "Old venue note",
                    Content: "Old hotel shuttle note.",
                    AliasesJson: "[\"hotel shuttle\"]",
                    FacetsJson: "[\"trip_planning\"]",
                    UpdateSemantics: "immutable-record",
                    Domain: "project:slack",
                    Sensitivity: "normal",
                    RecallMode: "searchable",
                    Confidence: 0.75,
                    FreshnessAtMs: now - (long)TimeSpan.FromDays(30).TotalMilliseconds,
                    ExpiresAtMs: now - 1)
            ],
            CancellationToken.None);

        var tool = new SqliteFindMemoriesTool(_store, _timeProvider);
        var normal = await tool.ExecuteAsync(
            new Dictionary<string, object?>
            {
                ["Query"] = "stir trek shuttle",
                ["Limit"] = 5
            },
            new ToolExecutionContext("slack/thread-3", null),
            CancellationToken.None);
        var debug = await tool.ExecuteAsync(
            new Dictionary<string, object?>
            {
                ["Query"] = "stir trek shuttle",
                ["Limit"] = 5,
                ["IncludeStale"] = true
            },
            new ToolExecutionContext("slack/thread-3", null),
            CancellationToken.None);

        Assert.Equal("No memories found.", normal);
        Assert.Contains("Old venue note", debug);
        Assert.Contains("stale=true", debug);
    }

    [Fact]
    public async Task Eval_reporting_thresholds_meet_smoke_targets_for_current_fixture_set()
    {
        await _store.InitializeAsync();
        var now = _timeProvider.GetUtcNow().ToUnixTimeMilliseconds();
        var proposalGate = new MemoryProposalGate();
        var recall = new SQLiteMemoryRecallCoordinator(
            _store,
            NullLogger<SQLiteMemoryRecallCoordinator>.Instance,
            sessionConfig: new SessionConfig { MemorySidecarsEnabled = true });

        var acceptedFact = proposalGate.Accept(
        [
            new MemoryProposal(
                "upsert_document",
                "durable_fact",
                "user",
                "self",
                new MemoryAnchor("user-travel-airline", "preference"),
                "Travel Profile: Preferred Airline",
                "Preferred airline: United Airlines",
                ["preferred airline", "united airlines"],
                ["travel_profile", "user_preference"],
                null,
                null,
                "auto",
                "normal",
                0.95,
                now,
                null,
                null,
                "strong user assertion")
        ],
        "project:slack",
        "normal",
        now);
        await _store.ApplyCurationBatchAsync("cp-report-1", acceptedFact, CancellationToken.None);

        await _store.ApplyCurationBatchAsync(
            "cp-report-2",
            [
                new SQLiteMemoryCurationOperation(
                    Kind: "record",
                    MemoryClass: "evidence",
                    MemoryId: "rec-report-evidence",
                    AnchorCanonicalName: "stir trek",
                    AnchorType: "event",
                    Title: "Hotel options",
                    Content: "Hilton Easton is close to the venue.",
                    AliasesJson: "[\"hotel options\"]",
                    FacetsJson: "[\"trip_planning\"]",
                    UpdateSemantics: "immutable-record",
                    Domain: "project:slack",
                    Sensitivity: "normal",
                    RecallMode: "searchable",
                    Confidence: 0.8,
                    FreshnessAtMs: now,
                    ExpiresAtMs: now + (long)TimeSpan.FromDays(7).TotalMilliseconds),
                new SQLiteMemoryCurationOperation(
                    Kind: "record",
                    MemoryClass: "evidence",
                    MemoryId: "rec-report-stale",
                    AnchorCanonicalName: "stir trek",
                    AnchorType: "event",
                    Title: "Old venue note",
                    Content: "Old hotel shuttle note.",
                    AliasesJson: "[\"hotel shuttle\"]",
                    FacetsJson: "[\"trip_planning\"]",
                    UpdateSemantics: "immutable-record",
                    Domain: "project:slack",
                    Sensitivity: "normal",
                    RecallMode: "searchable",
                    Confidence: 0.75,
                    FreshnessAtMs: now - (long)TimeSpan.FromDays(30).TotalMilliseconds,
                    ExpiresAtMs: now - 1)
            ],
            CancellationToken.None);

        var auto = await recall.RecallAsync(new AutomaticRecallRequest(
            "slack/thread-report",
            "what airline do I use and where should I stay",
            ["what airline do I use and where should I stay near Stir Trek"],
            3));
        var searchTool = new SqliteFindMemoriesTool(_store, _timeProvider);
        var search = await searchTool.ExecuteAsync(
            new Dictionary<string, object?>
            {
                ["Query"] = "stir trek hotel",
                ["Limit"] = 5
            },
            new ToolExecutionContext("slack/thread-report", null),
            CancellationToken.None);
        var staleDebug = await searchTool.ExecuteAsync(
            new Dictionary<string, object?>
            {
                ["Query"] = "stir trek shuttle",
                ["Limit"] = 5,
                ["IncludeStale"] = true
            },
            new ToolExecutionContext("slack/thread-report", null),
            CancellationToken.None);

        var autoRecallHitRate = auto.Items.Any(x => x.Content.Contains("United Airlines", StringComparison.Ordinal)) ? 1.0 : 0.0;
        var intentionalEvidenceHitRate = search.Contains("Hotel options", StringComparison.Ordinal) ? 1.0 : 0.0;
        var gateCorrectness = acceptedFact.Count == 1 ? 1.0 : 0.0;
        var explicitWriteTruthfulness = acceptedFact.Count == 1 ? 1.0 : 0.0;
        var evidenceLeakage = auto.Items.Any(x => x.Id == "rec-report-evidence") ? 1.0 : 0.0;

        Assert.Contains("stale=true", staleDebug);

        Assert.True(autoRecallHitRate >= 0.90, $"autoRecallHitRate={autoRecallHitRate:F2}");
        Assert.True(intentionalEvidenceHitRate >= 0.90, $"intentionalEvidenceHitRate={intentionalEvidenceHitRate:F2}");
        Assert.Equal(1.0, gateCorrectness);
        Assert.Equal(1.0, explicitWriteTruthfulness);
        Assert.Equal(0.0, evidenceLeakage);
    }

    public void Dispose()
    {
        if (Directory.Exists(_baseDir))
            Directory.Delete(_baseDir, recursive: true);
    }

    private sealed class FakeEvalTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
