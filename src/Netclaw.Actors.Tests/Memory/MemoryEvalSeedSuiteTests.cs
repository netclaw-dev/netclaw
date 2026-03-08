using Microsoft.Data.Sqlite;
using Netclaw.Actors.Memory;
using Netclaw.Actors.Sessions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Netclaw.Actors.Tests.Memory;

public sealed class MemoryEvalSeedSuiteTests : IDisposable
{
    private readonly string _baseDir = Path.Combine(Path.GetTempPath(), "netclaw-memory-eval-tests", Guid.NewGuid().ToString("N"));
    private readonly string _dbPath;
    private readonly SQLiteMemoryStore _store;

    public MemoryEvalSeedSuiteTests()
    {
        Directory.CreateDirectory(_baseDir);
        _dbPath = Path.Combine(_baseDir, "netclaw-memory-eval.db");
        _store = new SQLiteMemoryStore(_dbPath, TimeProvider.System);
    }

    [Fact]
    public async Task RecallQuality_seeded_fixture_returns_relevant_auto_recall_item()
    {
        await _store.InitializeAsync();
        var now = TimeProvider.System.GetUtcNow().ToUnixTimeMilliseconds();
        var anchor = _store.CreateDefaultAnchor("netclaw", "project:ops");

        await _store.UpsertDocumentAsync(new SQLiteMemoryDocument(
            DocumentId: "doc-ops",
            Anchor: anchor,
            Title: "Router failover runbook",
            MarkdownBody: "Use VRRP preemption delay of 15 seconds for stable failover.",
            UpdateSemantics: "merge-document",
            Domain: "project:ops",
            Sensitivity: "normal",
            RecallMode: "auto",
            Confidence: 0.92,
            FreshnessAtMs: now,
            CreatedAtMs: now,
            UpdatedAtMs: now));

        var coordinator = new SQLiteMemoryRecallCoordinator(_store, NullLogger<SQLiteMemoryRecallCoordinator>.Instance);
        var result = await coordinator.RecallAsync(new AutomaticRecallRequest(
            SessionId: "ops/thread-1",
            Query: "router failover",
            RecentUserMessages: ["what was our vrrp delay"],
            MaxItems: 3));

        Assert.False(result.Degraded);
        Assert.Contains(result.Items, i => i.Id == "doc-ops");
        Assert.True(result.Items.Count <= 3);
    }

    [Fact]
    public async Task Privacy_seeded_fixture_blocks_secret_memory_from_auto_recall()
    {
        await _store.InitializeAsync();
        var now = TimeProvider.System.GetUtcNow().ToUnixTimeMilliseconds();
        var anchor = _store.CreateDefaultAnchor("netclaw", "project:ops");

        await _store.UpsertDocumentAsync(new SQLiteMemoryDocument(
            DocumentId: "doc-secret",
            Anchor: anchor,
            Title: "Prod token",
            MarkdownBody: "token=abc123",
            UpdateSemantics: "merge-document",
            Domain: "project:ops",
            Sensitivity: "secret",
            RecallMode: "auto",
            Confidence: 0.99,
            FreshnessAtMs: now,
            CreatedAtMs: now,
            UpdatedAtMs: now));

        var coordinator = new SQLiteMemoryRecallCoordinator(_store, NullLogger<SQLiteMemoryRecallCoordinator>.Instance);
        var result = await coordinator.RecallAsync(new AutomaticRecallRequest(
            SessionId: "ops/thread-1",
            Query: "token",
            RecentUserMessages: ["what is token"],
            MaxItems: 3));

        Assert.False(result.Degraded);
        Assert.DoesNotContain(result.Items, i => i.Id == "doc-secret");
    }

    [Fact]
    public async Task NoiseSuppression_seeded_fixture_drops_trivial_checkpoint_candidate()
    {
        await _store.InitializeAsync();
        var policy = new MemoryPolicyEvaluator();
        var extractor = new MemoryRulesFirstExtractor(policy);

        var payload = new MemoryCheckpointPayload(
            SessionId: "ops/thread-2",
            TriggerType: "turn-complete",
            Source: "assistant",
            Content: "thanks",
            UserContent: "thanks",
            AssistantContent: "thanks",
            IsExplicitRequest: false,
            HasVerifiedToolFinding: false,
            IsCompactionBoundary: false,
            HasAcceptedSubAgentFinding: false,
            Domain: "project:ops",
            Sensitivity: "normal",
            RecallMode: "auto",
            Confidence: 0.9);

        var candidates = extractor.Extract(payload, new HashSet<string>(StringComparer.OrdinalIgnoreCase));
        Assert.Empty(candidates);
    }

    [Fact]
    public async Task TurnCompletion_snapshot_is_classed_conversation_trace_and_rejected_from_durable_candidates()
    {
        await _store.InitializeAsync();
        var policy = new MemoryPolicyEvaluator();
        var extractor = new MemoryRulesFirstExtractor(policy);

        var payload = new MemoryCheckpointPayload(
            SessionId: "ops/thread-3",
            TriggerType: "turn-complete",
            Source: "session",
            Content: "User: Where should we start?\nAssistant: I don't remember that yet.",
            UserContent: "Where should we start?",
            AssistantContent: "I don't remember that yet.",
            IsExplicitRequest: false,
            HasVerifiedToolFinding: false,
            IsCompactionBoundary: false,
            HasAcceptedSubAgentFinding: false,
            Domain: "project:ops",
            Sensitivity: "normal",
            RecallMode: "auto",
            Confidence: 0.8,
            Kind: "document",
            Title: "turn-completion",
            UpdateSemantics: "append-document");

        var candidates = extractor.Extract(payload, new HashSet<string>(StringComparer.OrdinalIgnoreCase));
        Assert.Empty(candidates);
    }

    [Fact]
    public async Task Latency_seeded_fixture_recall_completes_under_budget_on_local_store()
    {
        await _store.InitializeAsync();
        var now = TimeProvider.System.GetUtcNow().ToUnixTimeMilliseconds();
        var anchor = _store.CreateDefaultAnchor("netclaw", "project:latency");

        for (var i = 0; i < 50; i++)
        {
            await _store.UpsertDocumentAsync(new SQLiteMemoryDocument(
                DocumentId: $"doc-{i}",
                Anchor: anchor,
                Title: $"Latency note {i}",
                MarkdownBody: "sqlite recall budget check",
                UpdateSemantics: "merge-document",
                Domain: "project:latency",
                Sensitivity: "normal",
                RecallMode: "auto",
                Confidence: 0.8,
                FreshnessAtMs: now,
                CreatedAtMs: now,
                UpdatedAtMs: now));
        }

        var coordinator = new SQLiteMemoryRecallCoordinator(_store, NullLogger<SQLiteMemoryRecallCoordinator>.Instance);
        var start = TimeProvider.System.GetTimestamp();
        var result = await coordinator.RecallAsync(new AutomaticRecallRequest(
            SessionId: "latency/thread-1",
            Query: "latency",
            RecentUserMessages: ["latency"],
            MaxItems: 3));
        var elapsed = TimeProvider.System.GetElapsedTime(start);

        Assert.False(result.Degraded);
        Assert.True(elapsed <= TimeSpan.FromMilliseconds(300));
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
