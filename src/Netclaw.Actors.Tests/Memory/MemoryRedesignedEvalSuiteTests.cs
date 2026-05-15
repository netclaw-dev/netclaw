// -----------------------------------------------------------------------
// <copyright file="MemoryRedesignedEvalSuiteTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Netclaw.Actors.Memory;
using Netclaw.Actors.Protocol;
using Netclaw.Actors.Sessions;
using Netclaw.Configuration;
using Netclaw.Tools;
using Xunit;

namespace Netclaw.Actors.Tests.Memory;

public sealed class MemoryRedesignedEvalSuiteTests : IAsyncDisposable
{
    private readonly string _baseDir = Path.Combine(Path.GetTempPath(), "netclaw-memory-redesigned-evals", Guid.NewGuid().ToString("N"));
    private readonly string _dbPath;
    private readonly FakeTimeProvider _timeProvider;
    private readonly SQLiteMemoryStore _store;

    public MemoryRedesignedEvalSuiteTests()
    {
        Directory.CreateDirectory(_baseDir);
        _dbPath = Path.Combine(_baseDir, "netclaw-memory-redesigned-evals.db");
        _timeProvider = new FakeTimeProvider(DateTimeOffset.Parse("2026-03-10T12:00:00Z"));
        _store = new SQLiteMemoryStore(_dbPath, _timeProvider);
    }

    [Fact]
    public async Task Formation_then_auto_recall_surfaces_durable_fact()
    {
        await _store.InitializeAsync(TestContext.Current.CancellationToken);
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
        "normal",
        now);

        await _store.ApplyCurationBatchAsync("cp-eval-1", gateResult.MemoryOperations, CancellationToken.None);

        var recall = new SQLiteMemoryRecallCoordinator(
            _store,
            NullLogger<SQLiteMemoryRecallCoordinator>.Instance,
            sessionTuning: new SessionTuning());

        var result = await recall.RecallAsync(new AutomaticRecallRequest(
            (SessionId)"slack/thread-1",
            "what airline do I usually use",
            ["I usually fly United"],
            3), TestContext.Current.CancellationToken);

        Assert.False(result.Degraded);
        Assert.Contains(result.Items, x => x.Content.Contains("United Airlines", StringComparison.Ordinal));
        Assert.Single(gateResult.MemoryOperations);
    }

    [Fact]
    public async Task Formation_then_recall_surfaces_travel_origin_and_persists_metadata()
    {
        await _store.InitializeAsync(TestContext.Current.CancellationToken);
        var now = _timeProvider.GetUtcNow().ToUnixTimeMilliseconds();
        var gate = new MemoryProposalGate();

        var gateResult = gate.Evaluate(
        [
            new MemoryProposal(
                "upsert_document",
                "durable_fact",
                "user",
                "self",
                new MemoryAnchor("user-travel-origin", "preference"),
                "Travel Profile: Primary Origin Airport",
                "Primary origin airport is IAH in Houston.",
                ["origin airport", "fly out of", "IAH"],
                ["travel_profile", "user_preference"],
                ["origin_airport"],
                null,
                "auto",
                "normal",
                0.97,
                now,
                null,
                null,
                "stable explicit user travel preference")
        ],
        "normal",
        now);

        var op = Assert.Single(gateResult.MemoryOperations);
        Assert.Contains("IAH", op.AliasesJson ?? string.Empty, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("travel_profile", op.FacetsJson ?? string.Empty, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("origin_airport", op.SlotsJson ?? string.Empty, StringComparison.OrdinalIgnoreCase);

        await _store.ApplyCurationBatchAsync("cp-formation-iah", gateResult.MemoryOperations, TestContext.Current.CancellationToken);

        var stored = await _store.SearchAutoRecallDocumentsAsync("airport IAH fly", 5, ct: TestContext.Current.CancellationToken);
        var storedDoc = Assert.Single(stored, x => x.Title == "Travel Profile: Primary Origin Airport");
        Assert.Contains("IAH", storedDoc.AliasesJson ?? string.Empty, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("travel_profile", storedDoc.FacetsJson ?? string.Empty, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("origin_airport", storedDoc.SlotsJson ?? string.Empty, StringComparison.OrdinalIgnoreCase);

        var recall = new SQLiteMemoryRecallCoordinator(
            _store,
            NullLogger<SQLiteMemoryRecallCoordinator>.Instance,
            sessionTuning: new SessionTuning { DeterministicRetrievalEnabled = true });

        var result = await recall.RecallAsync(new AutomaticRecallRequest(
            (SessionId)"signalr/thread-iah",
            "What airport do I usually fly out of?",
            ["What airport do I usually fly out of?"],
            3,
            ThreadTitle: "Travel preferences"), TestContext.Current.CancellationToken);

        Assert.False(result.Degraded);
        Assert.Contains(result.Items, x => x.Content.Contains("IAH", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Formation_then_recall_surfaces_preferred_airline_and_persists_metadata()
    {
        await _store.InitializeAsync(TestContext.Current.CancellationToken);
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
                "Preferred airline is United Airlines because status benefits matter.",
                ["preferred airline", "United Airlines", "status with United"],
                ["travel_profile", "user_preference"],
                ["preferred_airline"],
                null,
                "auto",
                "normal",
                0.96,
                now,
                null,
                null,
                "stable explicit user airline preference")
        ],
        "normal",
        now);

        var op = Assert.Single(gateResult.MemoryOperations);
        Assert.Contains("United Airlines", op.AliasesJson ?? string.Empty, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("travel_profile", op.FacetsJson ?? string.Empty, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("preferred_airline", op.SlotsJson ?? string.Empty, StringComparison.OrdinalIgnoreCase);

        await _store.ApplyCurationBatchAsync("cp-formation-united", gateResult.MemoryOperations, TestContext.Current.CancellationToken);

        var stored = await _store.SearchAutoRecallDocumentsAsync("airline United status", 5, ct: TestContext.Current.CancellationToken);
        var storedDoc = Assert.Single(stored, x => x.Title == "Travel Profile: Preferred Airline");
        Assert.Contains("United Airlines", storedDoc.AliasesJson ?? string.Empty, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("travel_profile", storedDoc.FacetsJson ?? string.Empty, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("preferred_airline", storedDoc.SlotsJson ?? string.Empty, StringComparison.OrdinalIgnoreCase);

        var recall = new SQLiteMemoryRecallCoordinator(
            _store,
            NullLogger<SQLiteMemoryRecallCoordinator>.Instance,
            sessionTuning: new SessionTuning { DeterministicRetrievalEnabled = true });

        var result = await recall.RecallAsync(new AutomaticRecallRequest(
            (SessionId)"signalr/thread-united",
            "What airline do I usually take?",
            ["What airline do I usually take?"],
            3,
            ThreadTitle: "Travel preferences"), TestContext.Current.CancellationToken);

        Assert.False(result.Degraded);
        Assert.Contains(result.Items, x => x.Content.Contains("United Airlines", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Formation_then_intentional_search_returns_evidence_without_auto_recall_leakage()
    {
        await _store.InitializeAsync(TestContext.Current.CancellationToken);
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
                    SlotsJson: null,
                    Relations: null,
                    UpdateSemantics: "merge-document",
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
                    MemoryId: "rec-hotel-evidence",
                    AnchorCanonicalName: "stir trek",
                    AnchorType: "event",
                    Title: "Hotel options",
                    Content: "Hilton Easton is close to the venue.",
                    AliasesJson: "[\"hotel options\"]",
                    FacetsJson: "[\"trip_planning\"]",
                    SlotsJson: null,
                    Relations: null,
                    UpdateSemantics: "immutable-record",
                    Boundary: SecurityPolicyDefaults.TrustedInstanceBoundary,
                    Audience: TrustAudience.Team,
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
            sessionTuning: new SessionTuning());

        var auto = await recall.RecallAsync(new AutomaticRecallRequest(
            (SessionId)"slack/thread-2",
            "where should I stay",
            ["where should I stay near Stir Trek"],
            3), TestContext.Current.CancellationToken);

        Assert.DoesNotContain(auto.Items, x => x.Id == "rec-hotel-evidence");

        var tool = new SqliteFindMemoriesTool(_store, _timeProvider);
        var search = await tool.ExecuteAsync(
            new Dictionary<string, object?>
            {
                ["Query"] = "stir trek hotel",
                ["Limit"] = 5
            },
            new ToolExecutionContext("slack/thread-2", null) { Audience = TrustAudience.Personal },
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
        "normal",
        now);

        Assert.Empty(gateResult.MemoryOperations);
        var acceptedItem = Assert.Single(gateResult.IdentityUpdates);
        Assert.Equal("Communication style", acceptedItem.Title);
    }

    [Fact]
    public async Task Soul_boundary_keeps_project_facts_in_sqlite_memory()
    {
        await _store.InitializeAsync(TestContext.Current.CancellationToken);
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
        "normal",
        now);

        await _store.ApplyCurationBatchAsync("cp-eval-3", accepted, CancellationToken.None);
        var items = await _store.SearchByPlanAsync(["deploys", "east-2"], ["durable_fact"], 5, SecurityPolicyDefaults.TrustedInstanceBoundary, TrustAudience.Public, false, TestContext.Current.CancellationToken);

        Assert.Single(items);
        Assert.Equal("Deployment region", items[0].Title);
    }

    [Fact]
    public async Task Expiry_and_staleness_hides_expired_evidence_by_default_but_allows_debug_search()
    {
        await _store.InitializeAsync(TestContext.Current.CancellationToken);
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
                    SlotsJson: null,
                    Relations: null,
                    UpdateSemantics: "immutable-record",
                    Boundary: SecurityPolicyDefaults.TrustedInstanceBoundary,
                    Audience: TrustAudience.Team,
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
            new ToolExecutionContext("slack/thread-3", null) { Audience = TrustAudience.Personal },
            CancellationToken.None);
        var debug = await tool.ExecuteAsync(
            new Dictionary<string, object?>
            {
                ["Query"] = "stir trek shuttle",
                ["Limit"] = 5,
                ["IncludeStale"] = true
            },
            new ToolExecutionContext("slack/thread-3", null) { Audience = TrustAudience.Personal },
            CancellationToken.None);

        Assert.Equal("No memories found.", normal);
        Assert.Contains("Old venue note", debug);
        Assert.Contains("stale=true", debug);
    }

    [Fact]
    public async Task Eval_reporting_thresholds_meet_smoke_targets_for_current_fixture_set()
    {
        await _store.InitializeAsync(TestContext.Current.CancellationToken);
        var now = _timeProvider.GetUtcNow().ToUnixTimeMilliseconds();
        var proposalGate = new MemoryProposalGate();
        var recall = new SQLiteMemoryRecallCoordinator(
            _store,
            NullLogger<SQLiteMemoryRecallCoordinator>.Instance,
            sessionTuning: new SessionTuning());

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
                    SlotsJson: null,
                    Relations: null,
                    UpdateSemantics: "immutable-record",
                    Boundary: SecurityPolicyDefaults.TrustedInstanceBoundary,
                    Audience: TrustAudience.Team,
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
                    SlotsJson: null,
                    Relations: null,
                    UpdateSemantics: "immutable-record",
                    Boundary: SecurityPolicyDefaults.TrustedInstanceBoundary,
                    Audience: TrustAudience.Team,
                    Sensitivity: "normal",
                    RecallMode: "searchable",
                    Confidence: 0.75,
                    FreshnessAtMs: now - (long)TimeSpan.FromDays(30).TotalMilliseconds,
                    ExpiresAtMs: now - 1)
            ],
            CancellationToken.None);

        var auto = await recall.RecallAsync(new AutomaticRecallRequest(
            (SessionId)"slack/thread-report",
            "what airline do I use and where should I stay",
            ["what airline do I use and where should I stay near Stir Trek"],
            3), TestContext.Current.CancellationToken);
        var searchTool = new SqliteFindMemoriesTool(_store, _timeProvider);
        var search = await searchTool.ExecuteAsync(
            new Dictionary<string, object?>
            {
                ["Query"] = "stir trek hotel",
                ["Limit"] = 5
            },
            new ToolExecutionContext("slack/thread-report", null) { Audience = TrustAudience.Personal },
            CancellationToken.None);
        var staleDebug = await searchTool.ExecuteAsync(
            new Dictionary<string, object?>
            {
                ["Query"] = "stir trek shuttle",
                ["Limit"] = 5,
                ["IncludeStale"] = true
            },
            new ToolExecutionContext("slack/thread-report", null) { Audience = TrustAudience.Personal },
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

    public async ValueTask DisposeAsync()
    {
        await SqliteTempDirectoryCleanup.TryDeleteDirectoryAsync(_baseDir);
    }
}
