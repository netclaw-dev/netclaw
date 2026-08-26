// -----------------------------------------------------------------------
// <copyright file="SQLiteMemoryRecallGateTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Netclaw.Actors.Memory;
using Netclaw.Actors.Protocol;
using Netclaw.Actors.Sessions;
using Netclaw.Actors.Tests.Memory;
using Netclaw.Configuration;
using Xunit;

namespace Netclaw.Actors.Tests.Sessions;

/// <summary>
/// Covers <see cref="SQLiteMemoryRecallCoordinator"/>'s post-floor relevance-gate stage
/// (memory-relevance-gate, design D5/D6/D8, tasks 2.1/2.2/2.5): threshold admit/reject, the
/// zero-survivors-after-gate contract, every degradation path (scorer unavailable, no scorer
/// configured, gate disabled by config, sub-budget timeout), and the config nullable-follows-
/// manifest resolution for both <c>Enabled</c> and <c>Threshold</c>. Uses the same hand-crafted
/// 2D unit-vector geometry as <see cref="SQLiteMemoryRecallHybridTests"/> so every floor-survival
/// scenario here is exact and deterministic, and a <see cref="ScriptedRelevanceScorer"/> fake
/// (this file's own copy, mirroring that file's <c>ScriptedEmbedder</c> convention) so gate
/// scores are exact and deterministic too, without needing the real ONNX model.
/// </summary>
public sealed class SQLiteMemoryRecallGateTests : IAsyncDisposable
{
    private const string EmbedderModelId = "gate-test-embedder";
    private const string RelevanceModelId = "gate-test-relevance-model";
    private const int Dimensions = 2;
    private const double ManifestCalibratedThreshold = 0.5;

    private static readonly float[] QueryVector = [1f, 0f];

    private readonly string _baseDir = Path.Combine(Path.GetTempPath(), "netclaw-recall-gate-tests", Guid.NewGuid().ToString("N"));
    private readonly string _dbPath;
    private readonly SQLiteMemoryStore _store;

    public SQLiteMemoryRecallGateTests()
    {
        Directory.CreateDirectory(_baseDir);
        _dbPath = Path.Combine(_baseDir, "netclaw.db");
        _store = new SQLiteMemoryStore(_dbPath, TimeProvider.System);
    }

    public async ValueTask DisposeAsync() => await SqliteTempDirectoryCleanup.TryDeleteDirectoryAsync(_baseDir);

    // ── Threshold admit/reject boundary (task 2.5) ──────────────────────

    [Fact]
    public async Task Candidate_scoring_below_the_active_threshold_is_dropped()
    {
        var ct = TestContext.Current.CancellationToken;
        await _store.InitializeAsync(ct);
        await SeedFloorSurvivingDocumentAsync("doc-below-threshold", ct);

        var coordinator = BuildCoordinator(
            relevanceScorerHolder: BuildHolder(ScriptedRelevanceScorer.ReturningConstant(ManifestCalibratedThreshold - 0.1)));

        var result = await coordinator.RecallAsync(BuildRequest("gate/below-threshold"), ct);

        Assert.False(result.Degraded);
        Assert.DoesNotContain(result.Items, i => i.Id.Value == "doc-below-threshold");
    }

    [Fact]
    public async Task Candidate_scoring_at_or_above_the_active_threshold_survives()
    {
        var ct = TestContext.Current.CancellationToken;
        await _store.InitializeAsync(ct);
        await SeedFloorSurvivingDocumentAsync("doc-above-threshold", ct);

        var coordinator = BuildCoordinator(
            relevanceScorerHolder: BuildHolder(ScriptedRelevanceScorer.ReturningConstant(ManifestCalibratedThreshold + 0.1)));

        var result = await coordinator.RecallAsync(BuildRequest("gate/above-threshold"), ct);

        Assert.False(result.Degraded);
        Assert.Contains(result.Items, i => i.Id.Value == "doc-above-threshold");
    }

    // ── Zero-survivors-after-gate contract (task 2.1, spec scenario) ────

    [Fact]
    public async Task Zero_survivors_after_the_gate_returns_a_healthy_empty_result_not_degraded()
    {
        var ct = TestContext.Current.CancellationToken;
        await _store.InitializeAsync(ct);
        await SeedFloorSurvivingDocumentAsync("doc-gated-out", ct);

        var coordinator = BuildCoordinator(
            relevanceScorerHolder: BuildHolder(ScriptedRelevanceScorer.ReturningConstant(0.0)));

        var result = await coordinator.RecallAsync(BuildRequest("gate/zero-survivors"), ct);

        Assert.False(result.Degraded);
        Assert.Empty(result.Items);
    }

    // ── Degradation paths (task 2.2, spec "loud degradation without silent fallback") ──

    [Fact]
    public async Task Unavailable_scorer_degrades_to_floor_only_unfiltered()
    {
        var ct = TestContext.Current.CancellationToken;
        await _store.InitializeAsync(ct);
        await SeedFloorSurvivingDocumentAsync("doc-scorer-unavailable", ct);

        // The scorer would reject everything if it ran -- proving the floor's own result reaches
        // injection UNFILTERED requires a scorer whose score, if honored, would exclude the item.
        var scorer = new ScriptedRelevanceScorer(RelevanceModelId, isAvailable: false, scoreFn: (_, candidates) => candidates.Select(_ => 0.0).ToArray());
        var coordinator = BuildCoordinator(relevanceScorerHolder: BuildHolder(scorer));

        var result = await coordinator.RecallAsync(BuildRequest("gate/scorer-unavailable"), ct);

        Assert.False(result.Degraded);
        Assert.Contains(result.Items, i => i.Id.Value == "doc-scorer-unavailable");
    }

    [Fact]
    public async Task No_scorer_configured_degrades_to_floor_only_unfiltered()
    {
        var ct = TestContext.Current.CancellationToken;
        await _store.InitializeAsync(ct);
        await SeedFloorSurvivingDocumentAsync("doc-no-scorer", ct);

        var coordinator = BuildCoordinator(relevanceScorerHolder: null);

        var result = await coordinator.RecallAsync(BuildRequest("gate/no-scorer"), ct);

        Assert.False(result.Degraded);
        Assert.Contains(result.Items, i => i.Id.Value == "doc-no-scorer");
    }

    [Fact]
    public async Task Gate_explicitly_disabled_degrades_to_floor_only_unfiltered()
    {
        var ct = TestContext.Current.CancellationToken;
        await _store.InitializeAsync(ct);
        await SeedFloorSurvivingDocumentAsync("doc-gate-disabled", ct);

        var scorer = ScriptedRelevanceScorer.ReturningConstant(0.0); // would reject if it ran
        var coordinator = BuildCoordinator(
            relevanceScorerHolder: BuildHolder(scorer),
            embeddingsEnabled: true,
            relevanceGateEnabled: false);

        var result = await coordinator.RecallAsync(BuildRequest("gate/explicitly-disabled"), ct);

        Assert.False(result.Degraded);
        Assert.Contains(result.Items, i => i.Id.Value == "doc-gate-disabled");
    }

    [Fact]
    public async Task Sub_budget_timeout_degrades_to_floor_only_unfiltered()
    {
        var ct = TestContext.Current.CancellationToken;
        await _store.InitializeAsync(ct);
        await SeedFloorSurvivingDocumentAsync("doc-timeout", ct);

        // Never completes on its own; only the coordinator's envelope-clamped sub-budget CTS
        // (ceiling 120ms, default 300ms RecallTimeoutMs here so the ceiling itself governs) can
        // cancel it. Task.Delay inside a fake is the sanctioned way to simulate latency
        // deterministically — no Thread.Sleep/Task.Delay appears in this test's own orchestration.
        var scorer = new HangingRelevanceScorer(RelevanceModelId);
        var coordinator = BuildCoordinator(relevanceScorerHolder: BuildHolder(scorer));

        var result = await coordinator.RecallAsync(BuildRequest("gate/sub-budget-timeout"), ct);

        Assert.False(result.Degraded);
        Assert.Contains(result.Items, i => i.Id.Value == "doc-timeout");
    }

    // ── Envelope-derived sub-budget (2026-07 production-canary fix, task 3) ────

    [Fact]
    public async Task Gate_sub_budget_is_capped_by_the_remaining_outer_envelope_not_just_the_ceiling()
    {
        var ct = TestContext.Current.CancellationToken;
        await _store.InitializeAsync(ct);
        await SeedFloorSurvivingDocumentAsync("doc-envelope-exhausted", ct);

        // A real 50ms delay comfortably UNDER the 120ms gate-sub-budget ceiling -- if the fixed
        // ceiling alone governed the gate's CTS, this scorer would complete in time and its
        // (rejecting) score would apply. An almost-zero outer RecallTimeoutMs forces the
        // envelope-derived clamp to hand the gate far less than 120ms instead, so the scorer gets
        // cancelled and the turn degrades to the floor's unfiltered result.
        var scorer = new DelayedRelevanceScorer(RelevanceModelId, TimeSpan.FromMilliseconds(50), score: 0.0);
        var coordinator = BuildCoordinator(relevanceScorerHolder: BuildHolder(scorer), recallTimeoutMs: 1);

        var result = await coordinator.RecallAsync(BuildRequest("gate/envelope-exhausted"), ct);

        Assert.False(result.Degraded);
        Assert.Contains(result.Items, i => i.Id.Value == "doc-envelope-exhausted");
    }

    [Fact]
    public async Task Gate_runs_to_completion_when_the_outer_envelope_still_has_headroom()
    {
        var ct = TestContext.Current.CancellationToken;
        await _store.InitializeAsync(ct);
        await SeedFloorSurvivingDocumentAsync("doc-envelope-headroom", ct);

        // Same 50ms real delay and same rejecting score as the test above -- the only difference
        // is a generous outer envelope. Proves the previous test's degradation was caused by the
        // exhausted envelope specifically, not merely by the fake being slow: with headroom, the
        // gate runs to completion and its score is honored (candidate dropped, not degraded).
        var scorer = new DelayedRelevanceScorer(RelevanceModelId, TimeSpan.FromMilliseconds(50), score: 0.0);
        var coordinator = BuildCoordinator(relevanceScorerHolder: BuildHolder(scorer), recallTimeoutMs: 5000);

        var result = await coordinator.RecallAsync(BuildRequest("gate/envelope-headroom"), ct);

        Assert.False(result.Degraded);
        Assert.DoesNotContain(result.Items, i => i.Id.Value == "doc-envelope-headroom");
    }

    [Fact]
    public async Task Gate_degraded_log_is_debug_when_disabled_by_config()
    {
        var ct = TestContext.Current.CancellationToken;
        await _store.InitializeAsync(ct);
        await SeedFloorSurvivingDocumentAsync("doc-log-debug", ct);

        var recordingLogger = new RecordingLogger<SQLiteMemoryRecallCoordinator>();
        var coordinator = BuildCoordinator(
            relevanceScorerHolder: BuildHolder(ScriptedRelevanceScorer.ReturningConstant(1.0)),
            embeddingsEnabled: false,
            relevanceGateEnabled: null,
            logger: recordingLogger);

        await coordinator.RecallAsync(BuildRequest("gate/log-debug"), ct);

        Assert.Contains(recordingLogger.Entries, e => e.Level == LogLevel.Debug && e.Message.Contains("memory_recall_gate_degraded"));
        Assert.DoesNotContain(recordingLogger.Entries, e => e.Level == LogLevel.Warning && e.Message.Contains("memory_recall_gate_degraded"));
    }

    [Fact]
    public async Task Gate_degraded_log_is_warning_when_enabled_but_the_turn_still_degraded()
    {
        var ct = TestContext.Current.CancellationToken;
        await _store.InitializeAsync(ct);
        await SeedFloorSurvivingDocumentAsync("doc-log-warning", ct);

        var recordingLogger = new RecordingLogger<SQLiteMemoryRecallCoordinator>();
        var coordinator = BuildCoordinator(
            relevanceScorerHolder: null,
            embeddingsEnabled: true,
            relevanceGateEnabled: null,
            logger: recordingLogger);

        await coordinator.RecallAsync(BuildRequest("gate/log-warning"), ct);

        Assert.Contains(recordingLogger.Entries, e => e.Level == LogLevel.Warning && e.Message.Contains("memory_recall_gate_degraded"));
    }

    // ── Logging: gateScores / droppedByGate fields (task 2.4) ───────────

    [Fact]
    public async Task Final_retrieval_log_carries_droppedByGate_and_gateScores()
    {
        var ct = TestContext.Current.CancellationToken;
        await _store.InitializeAsync(ct);
        await SeedFloorSurvivingDocumentAsync("doc-logged", ct);

        var recordingLogger = new RecordingLogger<SQLiteMemoryRecallCoordinator>();
        var coordinator = BuildCoordinator(
            relevanceScorerHolder: BuildHolder(ScriptedRelevanceScorer.ReturningConstant(ManifestCalibratedThreshold - 0.1)),
            logger: recordingLogger);

        await coordinator.RecallAsync(BuildRequest("gate/logged"), ct);

        Assert.Contains(recordingLogger.Entries, e =>
            e.Level == LogLevel.Information
            && e.Message.Contains("memory_retrieval_final")
            && e.Message.Contains("droppedByGate=1")
            && e.Message.Contains("doc-logged="));
    }

    // ── Config nullable-follows-manifest resolution (task 1.5, 2.5) ─────

    [Fact]
    public async Task Enabled_null_follows_embeddings_enabled_true()
    {
        var ct = TestContext.Current.CancellationToken;
        await _store.InitializeAsync(ct);
        await SeedFloorSurvivingDocumentAsync("doc-follows-embeddings-on", ct);

        var coordinator = BuildCoordinator(
            relevanceScorerHolder: BuildHolder(ScriptedRelevanceScorer.ReturningConstant(0.0)),
            embeddingsEnabled: true,
            relevanceGateEnabled: null);

        var result = await coordinator.RecallAsync(BuildRequest("gate/follows-on"), ct);

        // Embeddings enabled + gate follows (null) => gate is ACTIVE, so the below-threshold
        // score actually drops the candidate.
        Assert.False(result.Degraded);
        Assert.DoesNotContain(result.Items, i => i.Id.Value == "doc-follows-embeddings-on");
    }

    [Fact]
    public async Task Enabled_null_follows_embeddings_enabled_false()
    {
        var ct = TestContext.Current.CancellationToken;
        await _store.InitializeAsync(ct);
        await SeedFloorSurvivingDocumentAsync("doc-follows-embeddings-off", ct);

        var coordinator = BuildCoordinator(
            relevanceScorerHolder: BuildHolder(ScriptedRelevanceScorer.ReturningConstant(0.0)),
            embeddingsEnabled: false,
            relevanceGateEnabled: null);

        var result = await coordinator.RecallAsync(BuildRequest("gate/follows-off"), ct);

        // Embeddings disabled + gate follows (null) => gate is INACTIVE, so the below-threshold
        // score never applies and the candidate survives unfiltered.
        Assert.False(result.Degraded);
        Assert.Contains(result.Items, i => i.Id.Value == "doc-follows-embeddings-off");
    }

    [Fact]
    public async Task Enabled_explicit_true_overrides_embeddings_disabled()
    {
        var ct = TestContext.Current.CancellationToken;
        await _store.InitializeAsync(ct);
        await SeedFloorSurvivingDocumentAsync("doc-explicit-override", ct);

        var coordinator = BuildCoordinator(
            relevanceScorerHolder: BuildHolder(ScriptedRelevanceScorer.ReturningConstant(0.0)),
            embeddingsEnabled: false,
            relevanceGateEnabled: true);

        var result = await coordinator.RecallAsync(BuildRequest("gate/explicit-override"), ct);

        Assert.False(result.Degraded);
        Assert.DoesNotContain(result.Items, i => i.Id.Value == "doc-explicit-override");
    }

    [Fact]
    public async Task Threshold_null_follows_the_scorers_manifest_calibrated_value()
    {
        var ct = TestContext.Current.CancellationToken;
        await _store.InitializeAsync(ct);
        await SeedFloorSurvivingDocumentAsync("doc-manifest-threshold", ct);

        var coordinator = BuildCoordinator(
            relevanceScorerHolder: BuildHolder(ScriptedRelevanceScorer.ReturningConstant(ManifestCalibratedThreshold), calibratedThreshold: ManifestCalibratedThreshold),
            thresholdOverride: null);

        var result = await coordinator.RecallAsync(BuildRequest("gate/manifest-threshold"), ct);

        // Score exactly equals the manifest threshold -- admitted (>=), proving the manifest
        // value (not some other default) is what was actually compared against.
        Assert.False(result.Degraded);
        Assert.Contains(result.Items, i => i.Id.Value == "doc-manifest-threshold");
    }

    [Fact]
    public async Task Threshold_explicit_override_takes_precedence_over_the_manifest_value()
    {
        var ct = TestContext.Current.CancellationToken;
        await _store.InitializeAsync(ct);
        await SeedFloorSurvivingDocumentAsync("doc-threshold-override", ct);

        // Score clears the manifest's calibrated threshold (0.5) but not the operator's explicit
        // override (0.9) -- if the override were ignored, this candidate would wrongly survive.
        var coordinator = BuildCoordinator(
            relevanceScorerHolder: BuildHolder(ScriptedRelevanceScorer.ReturningConstant(0.6), calibratedThreshold: ManifestCalibratedThreshold),
            thresholdOverride: 0.9);

        var result = await coordinator.RecallAsync(BuildRequest("gate/threshold-override"), ct);

        Assert.False(result.Degraded);
        Assert.DoesNotContain(result.Items, i => i.Id.Value == "doc-threshold-override");
    }

    // ── Fixtures ─────────────────────────────────────────────────────────

    private static RelevanceScorerHolder BuildHolder(IRelevanceScorer scorer, double calibratedThreshold = ManifestCalibratedThreshold)
        => new(scorer, calibratedThreshold);

    private SQLiteMemoryRecallCoordinator BuildCoordinator(
        RelevanceScorerHolder? relevanceScorerHolder,
        bool embeddingsEnabled = true,
        bool? relevanceGateEnabled = null,
        double? thresholdOverride = null,
        ILogger<SQLiteMemoryRecallCoordinator>? logger = null,
        int recallTimeoutMs = 300)
        => new(
            _store,
            logger ?? NullLogger<SQLiteMemoryRecallCoordinator>.Instance,
            new MemoryConfig
            {
                RecallTimeoutMs = recallTimeoutMs,
                Embeddings = new MemoryEmbeddingsConfig { Enabled = embeddingsEnabled },
                Recall = new MemoryRecallConfig
                {
                    RelevanceGate = new MemoryRelevanceGateConfig { Enabled = relevanceGateEnabled, Threshold = thresholdOverride },
                },
            },
            TimeProvider.System,
            sessionTuning: new SessionTuning { DeterministicRetrievalEnabled = true },
            // memory-query-prefix design D3: Memory.Recall.MinCosineSimilarity now defaults to
            // null (manifest-follows). Every candidate here embeds at cosine 1.0 against itself
            // (SeedFloorSurvivingDocumentAsync), so any floor below 1.0 clears it identically to
            // this file's pre-existing fixture geometry.
            embedderHolder: new MemoryEmbedderHolder(
                new ScriptedEmbedder(EmbedderModelId, Dimensions, QueryVector), initialQueryPrefix: "", initialCalibratedMinCosineSimilarity: 0.5),
            vectorIndexHolder: new MemoryVectorIndexHolder(_store),
            relevanceScorerHolder: relevanceScorerHolder);

    private static AutomaticRecallRequest BuildRequest(string sessionId)
        => new(
            SessionId: (SessionId)sessionId,
            Query: "what is our grafana dashboard provisioning convention?",
            RecentUserMessages: ["what is our grafana dashboard provisioning convention?"],
            MaxItems: 3);

    private async Task SeedFloorSurvivingDocumentAsync(string documentId, CancellationToken ct)
    {
        var anchor = _store.CreateDefaultAnchor(documentId);
        var now = TimeProvider.System.GetUtcNow().ToUnixTimeMilliseconds();
        await _store.UpsertDocumentAsync(new SQLiteMemoryDocument(
            DocumentId: documentId,
            Anchor: anchor,
            MemoryClass: "durable_fact",
            Title: "Grafana dashboard provisioning convention",
            MarkdownBody: "Grafana dashboard provisioning convention details for the ops team.",
            AliasesJson: null,
            FacetsJson: null,
            SlotsJson: null,
            UpdateSemantics: "merge-document",
            Sensitivity: "normal",
            RecallMode: "auto",
            Confidence: 0.9,
            FreshnessAtMs: now,
            ExpiresAtMs: null,
            CreatedAtMs: now,
            UpdatedAtMs: now), ct);

        // Clears the absolute cosine floor (QueryVector against itself, cosine 1.0) so this
        // candidate reaches the gate stage exactly like SQLiteMemoryRecallHybridTests's own
        // floor-admission fixtures.
        await _store.UpsertEmbeddingAsync(
            documentId,
            MemoryEmbedOnWriteCoordinator.DocumentItemKind,
            EmbedderModelId,
            MemoryContentHasher.ComputeHash(
                "Grafana dashboard provisioning convention",
                "Grafana dashboard provisioning convention details for the ops team."),
            QueryVector,
            ct);
    }

    /// <summary>
    /// Fake embedder that ignores its input text and always returns the same, hand-crafted query
    /// vector. This file's own copy of the identical fake used by
    /// <c>SQLiteMemoryRecallHybridTests</c> and <c>MemoryCurationNominatorTests</c> — kept
    /// separate per those files' own stated convention.
    /// </summary>
    private sealed class ScriptedEmbedder(string modelId, int dimensions, float[] queryVector) : IMemoryEmbedder
    {
        public string ModelId => modelId;

        public int Dimensions => dimensions;

        public bool IsAvailable => true;

        public ValueTask<ReadOnlyMemory<float>> EmbedAsync(string text, EmbeddingPurpose purpose, CancellationToken ct)
            => ValueTask.FromResult<ReadOnlyMemory<float>>(queryVector);

        public ValueTask<IReadOnlyList<ReadOnlyMemory<float>>> EmbedBatchAsync(IReadOnlyList<string> texts, EmbeddingPurpose purpose, CancellationToken ct)
            => ValueTask.FromResult<IReadOnlyList<ReadOnlyMemory<float>>>(
                texts.Select(_ => (ReadOnlyMemory<float>)queryVector).ToList());
    }

    /// <summary>
    /// Fake relevance scorer whose score is fully controlled by the test — no ONNX involved, so
    /// threshold-boundary scenarios can use exact values instead of a real model's opaque score
    /// distribution.
    /// </summary>
    private sealed class ScriptedRelevanceScorer(
        string modelId,
        Func<string, IReadOnlyList<string>, IReadOnlyList<double>> scoreFn,
        bool isAvailable = true) : IRelevanceScorer
    {
        public static ScriptedRelevanceScorer ReturningConstant(double score)
            => new(RelevanceModelId, (_, candidates) => candidates.Select(_ => score).ToArray());

        public string ModelId => modelId;

        public bool IsAvailable => isAvailable;

        public ValueTask<IReadOnlyList<double>> ScoreAsync(string query, IReadOnlyList<string> candidates, CancellationToken ct)
            => ValueTask.FromResult(scoreFn(query, candidates));
    }

    /// <summary>
    /// Fake relevance scorer that never completes on its own — only the coordinator's own
    /// sub-budget-linked <see cref="CancellationTokenSource"/> can end the call, so the sub-
    /// budget-timeout test is deterministic rather than racing a wall-clock delay against the
    /// coordinator's timer.
    /// </summary>
    private sealed class HangingRelevanceScorer(string modelId) : IRelevanceScorer
    {
        public string ModelId => modelId;

        public bool IsAvailable => true;

        public async ValueTask<IReadOnlyList<double>> ScoreAsync(string query, IReadOnlyList<string> candidates, CancellationToken ct)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            return [];
        }
    }

    /// <summary>
    /// Fake relevance scorer that completes after a fixed, finite real-wall-clock delay (2026-07
    /// production-canary envelope-derived-budget tests) rather than hanging forever like
    /// <see cref="HangingRelevanceScorer"/> — this file's own copy of a "slow but not infinite"
    /// fake, needed to prove the gate's sub-budget is actually smaller than the fixed
    /// <c>RelevanceGateSubBudgetMs</c> ceiling when the outer envelope is nearly exhausted. The
    /// delay itself is real (Task.Delay inside the fake, not this test's own orchestration) — the
    /// sanctioned way to simulate latency deterministically per this repo's testing guidelines.
    /// </summary>
    private sealed class DelayedRelevanceScorer(string modelId, TimeSpan delay, double score) : IRelevanceScorer
    {
        public string ModelId => modelId;

        public bool IsAvailable => true;

        [SlopwatchSuppress("SW004", "Intentional latency simulation inside a fake (never in test orchestration) -- proves the envelope-derived sub-budget clamp actually cancels a scorer that would otherwise complete within the fixed 120ms ceiling.")]
        public async ValueTask<IReadOnlyList<double>> ScoreAsync(string query, IReadOnlyList<string> candidates, CancellationToken ct)
        {
            await Task.Delay(delay, ct);
            return candidates.Select(_ => score).ToArray();
        }
    }

    /// <summary>Records every (level, message) pair logged through the generic ILogger ctor seam.</summary>
    private sealed class RecordingLogger<T> : ILogger<T>
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
            => Entries.Add((logLevel, formatter(state, exception)));
    }
}

/// <summary>
/// Lightweight stand-in for Slopwatch's suppression attribute (mirrors
/// <c>samples/Netclaw.Demo.AppHost.IntegrationTests/DemoEndToEndSmokeTests.cs</c>'s own copy) so
/// this project can build without taking a hard dependency on the slopwatch tooling. Slopwatch
/// reads the attribute name as text via the source file, so an internal definition with matching
/// shape is enough.
/// </summary>
[AttributeUsage(AttributeTargets.Method | AttributeTargets.Class | AttributeTargets.Property | AttributeTargets.Field | AttributeTargets.Constructor, AllowMultiple = true)]
internal sealed class SlopwatchSuppressAttribute : Attribute
{
    public SlopwatchSuppressAttribute(string ruleId, string reason)
    {
        RuleId = ruleId;
        Reason = reason;
    }

    public string RuleId { get; }
    public string Reason { get; }
}
