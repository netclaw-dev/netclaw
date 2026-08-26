// -----------------------------------------------------------------------
// <copyright file="EmbeddingWarmupHostedServiceTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Security.Cryptography;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Netclaw.Actors.Memory;
using Netclaw.Configuration;
using Netclaw.Daemon.Services;
using Netclaw.Embeddings;
using Xunit;

namespace Netclaw.Daemon.Tests.Services;

/// <summary>
/// Covers <see cref="EmbeddingWarmupHostedService"/> (memory-core-redesign Slice 2, task 2.7):
/// degraded path, success path, and gap repair. Uses the tiny fixture ONNX graph committed at
/// <c>Netclaw.Embeddings.Tests/Fixtures</c> (linked into this project's output) — no network
/// access anywhere in these tests. The allowlist is an injected, required dependency of
/// <see cref="EmbeddingModelProvisioner"/> (see its remarks), so pointing it at the fixture
/// instead of the real HuggingFace allowlist requires no test-only seam beyond that.
/// </summary>
public sealed class EmbeddingWarmupHostedServiceTests : IAsyncLifetime
{
    private const string ModelId = "tiny-fixture";
    private const int Dimensions = 8;

    // memory-query-prefix design D2/D3 fixture calibration -- not a real model card figure, just
    // an exercisable prefix/floor pair so tests can assert the warmup service threads both
    // through to the holder.
    private const string QueryPrefix = "search_query: ";
    private const double CalibratedMinCosineSimilarity = 0.42;

    // WarmUpRelevanceGateAsync hardcodes this constant as the relevance model id to provision
    // (memory-relevance-gate: there is no config knob selecting which relevance model is
    // active), so any fixture allowlist a test supplies must be keyed under the SAME id.
    private const string RelevanceModelId = EmbeddingModelProvisioner.DefaultRelevanceModelId;
    private const double RelevanceCalibratedThreshold = 0.02;

    private readonly string _baseDir = Path.Combine(Path.GetTempPath(), $"netclaw-embedding-warmup-tests-{Guid.NewGuid():N}");
    private NetclawPaths _paths = null!;
    private SQLiteMemoryStore _store = null!;
    private EmbeddingModelProvisioner _provisioner = null!;
    private IReadOnlyDictionary<string, EmbeddingModelManifestEntry> _allowlist = null!;

    private static string FixturesDir => Path.Combine(AppContext.BaseDirectory, "Fixtures");

    public async ValueTask InitializeAsync()
    {
        _paths = new NetclawPaths(_baseDir);
        _paths.EnsureDirectoriesExist();
        _store = new SQLiteMemoryStore(_paths.MemorySqliteDbPath, TimeProvider.System);
        await _store.InitializeAsync();

        var modelBytes = await File.ReadAllBytesAsync(Path.Combine(FixturesDir, "tiny-embedder.onnx"));
        var vocabBytes = await File.ReadAllBytesAsync(Path.Combine(FixturesDir, "tiny-vocab.txt"));
        _allowlist = new Dictionary<string, EmbeddingModelManifestEntry>
        {
            [ModelId] = new(
                ModelId,
                // Never actually fetched in these tests: the fixture files are pre-placed as an
                // already-valid local copy, so ProvisionAsync's skip-if-valid path never reaches
                // the network. A live URL is not required for that path to work.
                ModelUrl: new Uri("http://127.0.0.1:1/unused-model.onnx"),
                TokenizerUrl: new Uri("http://127.0.0.1:1/unused-vocab.txt"),
                ModelSha256: Sha256Hex(modelBytes),
                TokenizerSha256: Sha256Hex(vocabBytes),
                Dimensions: Dimensions,
                ModelByteSize: modelBytes.Length,
                QueryPrefix: QueryPrefix,
                CalibratedMinCosineSimilarity: CalibratedMinCosineSimilarity),
        };
        _provisioner = new EmbeddingModelProvisioner(new HttpClient(), _allowlist);
    }

    public async ValueTask DisposeAsync() => await TryDeleteDirectoryAsync(_baseDir);

    [Fact]
    public async Task Success_path_loads_the_fixture_model_with_no_network_and_populates_the_holder()
    {
        PrePlaceValidModelFiles();
        var holder = new MemoryEmbedderHolder(new UnavailableMemoryEmbedder(ModelId, "warmup not yet run"), initialQueryPrefix: "", initialCalibratedMinCosineSimilarity: null);
        var memoryConfig = new MemoryConfig { Embeddings = { Enabled = true, ModelId = ModelId, AutoDownload = true } };
        var service = CreateService(holder, memoryConfig);

        await service.WarmUpAsync(TestContext.Current.CancellationToken);

        Assert.True(holder.Current.IsAvailable);
        Assert.Equal(ModelId, holder.Current.ModelId);
        Assert.Equal(Dimensions, holder.Current.Dimensions);

        // memory-query-prefix design D2/D3, task 1.4: the allowlist entry's QueryPrefix and
        // CalibratedMinCosineSimilarity travel onto the holder alongside the embedder itself.
        Assert.Equal(QueryPrefix, holder.QueryPrefix);
        Assert.Equal(CalibratedMinCosineSimilarity, holder.CalibratedMinCosineSimilarity);
    }

    [Fact]
    public async Task Degraded_path_sets_an_unavailable_embedder_when_the_model_is_missing_and_autodownload_is_false()
    {
        // No PrePlaceValidModelFiles() call — the model directory is empty.
        var holder = new MemoryEmbedderHolder(new UnavailableMemoryEmbedder(ModelId, "warmup not yet run"), initialQueryPrefix: "", initialCalibratedMinCosineSimilarity: null);
        var memoryConfig = new MemoryConfig { Embeddings = { Enabled = true, ModelId = ModelId, AutoDownload = false } };
        var service = CreateService(holder, memoryConfig);

        await service.WarmUpAsync(TestContext.Current.CancellationToken);

        Assert.False(holder.Current.IsAvailable);
        Assert.IsType<UnavailableMemoryEmbedder>(holder.Current);
        // The manifest's prefix/floor are still known even though the model failed to load --
        // they describe the model id, not whether provisioning succeeded (mirrors the relevance
        // gate's own degraded-path assertion).
        Assert.Equal(QueryPrefix, holder.QueryPrefix);
        Assert.Equal(CalibratedMinCosineSimilarity, holder.CalibratedMinCosineSimilarity);
    }

    [Fact]
    public async Task Disabled_config_leaves_the_holder_at_its_initial_value()
    {
        var initial = new UnavailableMemoryEmbedder(ModelId, "embeddings disabled");
        var holder = new MemoryEmbedderHolder(initial, initialQueryPrefix: "", initialCalibratedMinCosineSimilarity: null);
        var memoryConfig = new MemoryConfig { Embeddings = { Enabled = false, ModelId = ModelId } };
        var service = CreateService(holder, memoryConfig);

        await service.WarmUpAsync(TestContext.Current.CancellationToken);

        Assert.Same(initial, holder.Current);
    }

    [Fact]
    public async Task Gap_repair_embeds_documents_missing_a_current_model_embedding()
    {
        PrePlaceValidModelFiles();

        var anchor = _store.CreateDefaultAnchor("gap-repair-warmup-test");
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        await _store.UpsertDocumentAsync(new SQLiteMemoryDocument(
            DocumentId: "doc-needs-embedding",
            Anchor: anchor,
            MemoryClass: "durable_fact",
            Title: "Needs Embedding",
            MarkdownBody: "this document has never been embedded",
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
            UpdatedAtMs: now), TestContext.Current.CancellationToken);

        var holder = new MemoryEmbedderHolder(new UnavailableMemoryEmbedder(ModelId, "warmup not yet run"), initialQueryPrefix: "", initialCalibratedMinCosineSimilarity: null);
        var memoryConfig = new MemoryConfig { Embeddings = { Enabled = true, ModelId = ModelId, AutoDownload = true } };
        var service = CreateService(holder, memoryConfig);

        await service.WarmUpAsync(TestContext.Current.CancellationToken);

        var rows = await _store.GetEmbeddingsForModelAsync(ModelId, TestContext.Current.CancellationToken);
        var row = Assert.Single(rows);
        Assert.Equal("doc-needs-embedding", row.ItemId);
    }

    // memory-embeddings-int8-default: proves the upgrade story when Memory.Embeddings.ModelId
    // switches (e.g. an existing install's fp32 `snowflake-arctic-embed-m` vectors on an
    // install that predates the int8 default flip) -- gap repair is scoped to the NEW active
    // model id (GetDocumentsNeedingEmbeddingAsync/GetEmbeddingsForModelAsync both filter by
    // model_id), so a document with only an old-model vector still looks "missing" under the
    // new id and gets re-embedded automatically at the next startup, with no operator action
    // required. The old vector is never deleted -- it just stops being the one anything reads,
    // since MemoryVectorIndex/the curation nominator only ever load the active model's rows
    // (see MemoryVectorIndex.LoadAsync -> GetEmbeddingsForModelAsync(ModelId)).
    [Fact]
    public async Task Model_id_switch_gap_repair_targets_the_new_active_model_id_and_leaves_old_vectors_in_place()
    {
        PrePlaceValidModelFiles();

        const string LegacyModelId = "tiny-fixture-legacy";
        const string Title = "Pre-upgrade document";
        const string Body = "this document was embedded under the old model before the default switched";

        var anchor = _store.CreateDefaultAnchor("model-switch-gap-repair-test");
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        await _store.UpsertDocumentAsync(new SQLiteMemoryDocument(
            DocumentId: "doc-under-legacy-model",
            Anchor: anchor,
            MemoryClass: "durable_fact",
            Title: Title,
            MarkdownBody: Body,
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
            UpdatedAtMs: now), TestContext.Current.CancellationToken);

        // Simulate a pre-upgrade install: written directly (not through a real embedder) since
        // only the model-id scoping behavior is under test here.
        var legacyHash = MemoryContentHasher.ComputeHash(Title, Body);
        await _store.UpsertEmbeddingAsync(
            "doc-under-legacy-model", MemoryEmbedOnWriteCoordinator.DocumentItemKind, LegacyModelId, legacyHash,
            new float[Dimensions], TestContext.Current.CancellationToken);

        var holder = new MemoryEmbedderHolder(new UnavailableMemoryEmbedder(ModelId, "warmup not yet run"), initialQueryPrefix: "", initialCalibratedMinCosineSimilarity: null);
        // Active config now points at the NEW model id (ModelId = "tiny-fixture") -- the same
        // shape as an operator upgrading onto a new default embedding model.
        var memoryConfig = new MemoryConfig { Embeddings = { Enabled = true, ModelId = ModelId, AutoDownload = true } };
        var service = CreateService(holder, memoryConfig);

        await service.WarmUpAsync(TestContext.Current.CancellationToken);

        var newRows = await _store.GetEmbeddingsForModelAsync(ModelId, TestContext.Current.CancellationToken);
        var newRow = Assert.Single(newRows);
        Assert.Equal("doc-under-legacy-model", newRow.ItemId);

        // The legacy vector is left in place -- a model-id switch never deletes old rows.
        var legacyRows = await _store.GetEmbeddingsForModelAsync(LegacyModelId, TestContext.Current.CancellationToken);
        Assert.Single(legacyRows);
    }

    // ── Relevance gate provisioning (memory-relevance-gate, design D4, task 1.4) ──

    [Fact]
    public async Task Relevance_gate_success_path_loads_the_fixture_scorer_and_pairs_the_manifest_threshold()
    {
        PrePlaceValidModelFiles();
        PrePlaceValidRelevanceModelFiles();

        var holder = new MemoryEmbedderHolder(new UnavailableMemoryEmbedder(ModelId, "warmup not yet run"), initialQueryPrefix: "", initialCalibratedMinCosineSimilarity: null);
        var relevanceHolder = CreateRelevanceScorerHolder();
        var memoryConfig = new MemoryConfig { Embeddings = { Enabled = true, ModelId = ModelId, AutoDownload = true } };
        var service = CreateService(holder, memoryConfig, relevanceHolder, RelevanceFixtureAllowlist());

        await service.WarmUpAsync(TestContext.Current.CancellationToken);

        Assert.True(relevanceHolder.Current.IsAvailable);
        Assert.Equal(RelevanceModelId, relevanceHolder.Current.ModelId);
        Assert.Equal(RelevanceCalibratedThreshold, relevanceHolder.CalibratedThreshold);
    }

    [Fact]
    public async Task Relevance_gate_degraded_path_sets_an_unavailable_scorer_when_the_model_is_missing()
    {
        PrePlaceValidModelFiles();
        // No PrePlaceValidRelevanceModelFiles() call -- the relevance model directory is empty.

        var holder = new MemoryEmbedderHolder(new UnavailableMemoryEmbedder(ModelId, "warmup not yet run"), initialQueryPrefix: "", initialCalibratedMinCosineSimilarity: null);
        var relevanceHolder = CreateRelevanceScorerHolder();
        var memoryConfig = new MemoryConfig { Embeddings = { Enabled = true, ModelId = ModelId, AutoDownload = false } };
        var service = CreateService(holder, memoryConfig, relevanceHolder, RelevanceFixtureAllowlist());

        await service.WarmUpAsync(TestContext.Current.CancellationToken);

        // The embedder itself still succeeds -- the two models are independently lifecycled.
        Assert.True(holder.Current.IsAvailable);
        Assert.False(relevanceHolder.Current.IsAvailable);
        Assert.IsType<UnavailableRelevanceScorer>(relevanceHolder.Current);
        // The manifest's calibrated threshold is still known even though the model failed to
        // load -- it describes the model id, not whether provisioning succeeded.
        Assert.Equal(RelevanceCalibratedThreshold, relevanceHolder.CalibratedThreshold);
    }

    [Fact]
    public async Task Relevance_gate_disabled_config_leaves_the_relevance_holder_at_its_initial_value()
    {
        var holder = new MemoryEmbedderHolder(new UnavailableMemoryEmbedder(ModelId, "embeddings disabled"), initialQueryPrefix: "", initialCalibratedMinCosineSimilarity: null);
        var initialRelevance = new UnavailableRelevanceScorer(RelevanceModelId, "embeddings disabled");
        var relevanceHolder = new RelevanceScorerHolder(initialRelevance, initialCalibratedThreshold: 0.0);
        var memoryConfig = new MemoryConfig { Embeddings = { Enabled = false, ModelId = ModelId } };
        var service = CreateService(holder, memoryConfig, relevanceHolder, RelevanceFixtureAllowlist());

        await service.WarmUpAsync(TestContext.Current.CancellationToken);

        // Memory.Embeddings.Enabled=false short-circuits WarmUpAsync entirely -- neither model's
        // provisioning step ever runs.
        Assert.Same(initialRelevance, relevanceHolder.Current);
    }

    // ── Operator alerting (memory embedding/reranker provisioning-failure alert) ──

    [Fact]
    public async Task Embedder_provisioning_failure_emits_exactly_one_operator_alert_naming_the_model_and_reason()
    {
        // No PrePlaceValidModelFiles() call -- the embedder fails. The relevance model succeeds so
        // only the embedder's alert is under test here.
        PrePlaceValidRelevanceModelFiles();

        var holder = new MemoryEmbedderHolder(new UnavailableMemoryEmbedder(ModelId, "warmup not yet run"), initialQueryPrefix: "", initialCalibratedMinCosineSimilarity: null);
        var relevanceHolder = CreateRelevanceScorerHolder();
        var memoryConfig = new MemoryConfig { Embeddings = { Enabled = true, ModelId = ModelId, AutoDownload = false } };
        var sink = new FakeNotificationSink();
        var service = CreateService(holder, memoryConfig, relevanceHolder, RelevanceFixtureAllowlist(), notificationSink: sink);

        await service.WarmUpAsync(TestContext.Current.CancellationToken);

        Assert.True(relevanceHolder.Current.IsAvailable);
        var alert = Assert.Single(sink.Alerts);
        Assert.Equal(AlertType.MemoryEmbeddingModelUnavailable, alert.Category);
        Assert.Equal(ModelId, alert.Source);
        Assert.Contains(ModelId, alert.Summary);
        Assert.Equal(ModelId, alert.Context?["modelId"]);
        Assert.False(string.IsNullOrWhiteSpace(alert.Context?["reason"]));
        Assert.Contains("lexical-only", alert.Context?["consequence"]);
        Assert.Contains("netclaw doctor", alert.Context?["remediation"]);
    }

    [Fact]
    public async Task Relevance_model_provisioning_failure_emits_exactly_one_operator_alert_naming_the_model_and_reason()
    {
        // Embedder succeeds; the relevance model fails (no PrePlaceValidRelevanceModelFiles call).
        PrePlaceValidModelFiles();

        var holder = new MemoryEmbedderHolder(new UnavailableMemoryEmbedder(ModelId, "warmup not yet run"), initialQueryPrefix: "", initialCalibratedMinCosineSimilarity: null);
        var relevanceHolder = CreateRelevanceScorerHolder();
        var memoryConfig = new MemoryConfig { Embeddings = { Enabled = true, ModelId = ModelId, AutoDownload = false } };
        var sink = new FakeNotificationSink();
        var service = CreateService(holder, memoryConfig, relevanceHolder, RelevanceFixtureAllowlist(), notificationSink: sink);

        await service.WarmUpAsync(TestContext.Current.CancellationToken);

        Assert.True(holder.Current.IsAvailable);
        var alert = Assert.Single(sink.Alerts);
        Assert.Equal(AlertType.MemoryRelevanceModelUnavailable, alert.Category);
        Assert.Equal(RelevanceModelId, alert.Source);
        Assert.Contains(RelevanceModelId, alert.Summary);
        Assert.Equal(RelevanceModelId, alert.Context?["modelId"]);
        Assert.False(string.IsNullOrWhiteSpace(alert.Context?["reason"]));
        Assert.Contains("relevance gate is disabled", alert.Context?["consequence"]);
        // The relevance model has no backfill-embeddings analogue -- its remediation must not
        // suggest that command (mirrors MemoryRelevanceGateDoctorCheck's own wording).
        Assert.DoesNotContain("backfill-embeddings", alert.Context?["remediation"]);
    }

    [Fact]
    public async Task Both_models_failing_emits_two_distinct_operator_alerts()
    {
        // Neither PrePlaceValidModelFiles() nor PrePlaceValidRelevanceModelFiles() is called --
        // both models fail to provision independently.
        var holder = new MemoryEmbedderHolder(new UnavailableMemoryEmbedder(ModelId, "warmup not yet run"), initialQueryPrefix: "", initialCalibratedMinCosineSimilarity: null);
        var relevanceHolder = CreateRelevanceScorerHolder();
        var memoryConfig = new MemoryConfig { Embeddings = { Enabled = true, ModelId = ModelId, AutoDownload = false } };
        var sink = new FakeNotificationSink();
        var service = CreateService(holder, memoryConfig, relevanceHolder, RelevanceFixtureAllowlist(), notificationSink: sink);

        await service.WarmUpAsync(TestContext.Current.CancellationToken);

        Assert.False(holder.Current.IsAvailable);
        Assert.False(relevanceHolder.Current.IsAvailable);
        Assert.Equal(2, sink.Alerts.Count);
        Assert.Contains(sink.Alerts, a => a.Category == AlertType.MemoryEmbeddingModelUnavailable);
        Assert.Contains(sink.Alerts, a => a.Category == AlertType.MemoryRelevanceModelUnavailable);
        // Distinct alert ids -- these are two independent events, not one duplicated.
        Assert.NotEqual(sink.Alerts[0].AlertId, sink.Alerts[1].AlertId);
    }

    [Fact]
    public async Task Success_path_emits_no_operator_alerts()
    {
        PrePlaceValidModelFiles();
        PrePlaceValidRelevanceModelFiles();

        var holder = new MemoryEmbedderHolder(new UnavailableMemoryEmbedder(ModelId, "warmup not yet run"), initialQueryPrefix: "", initialCalibratedMinCosineSimilarity: null);
        var relevanceHolder = CreateRelevanceScorerHolder();
        var memoryConfig = new MemoryConfig { Embeddings = { Enabled = true, ModelId = ModelId, AutoDownload = true } };
        var sink = new FakeNotificationSink();
        var service = CreateService(holder, memoryConfig, relevanceHolder, RelevanceFixtureAllowlist(), notificationSink: sink);

        await service.WarmUpAsync(TestContext.Current.CancellationToken);

        Assert.True(holder.Current.IsAvailable);
        Assert.True(relevanceHolder.Current.IsAvailable);
        Assert.Empty(sink.Alerts);
    }

    [Fact]
    public async Task Disabled_config_emits_no_operator_alerts()
    {
        var holder = new MemoryEmbedderHolder(new UnavailableMemoryEmbedder(ModelId, "embeddings disabled"), initialQueryPrefix: "", initialCalibratedMinCosineSimilarity: null);
        var relevanceHolder = CreateRelevanceScorerHolder();
        var memoryConfig = new MemoryConfig { Embeddings = { Enabled = false, ModelId = ModelId } };
        var sink = new FakeNotificationSink();
        var service = CreateService(holder, memoryConfig, relevanceHolder, RelevanceFixtureAllowlist(), notificationSink: sink);

        await service.WarmUpAsync(TestContext.Current.CancellationToken);

        // Embeddings disabled is an intentional, not degraded, state -- no alert should fire.
        Assert.Empty(sink.Alerts);
    }

    [Fact]
    public async Task Provisioning_failure_alert_is_latched_and_does_not_refire_across_repeated_warmup_runs()
    {
        // Neither model's fixture files are placed -- both fail every time WarmUpAsync runs.
        var holder = new MemoryEmbedderHolder(new UnavailableMemoryEmbedder(ModelId, "warmup not yet run"), initialQueryPrefix: "", initialCalibratedMinCosineSimilarity: null);
        var relevanceHolder = CreateRelevanceScorerHolder();
        var memoryConfig = new MemoryConfig { Embeddings = { Enabled = true, ModelId = ModelId, AutoDownload = false } };
        var sink = new FakeNotificationSink();
        var service = CreateService(holder, memoryConfig, relevanceHolder, RelevanceFixtureAllowlist(), notificationSink: sink);

        await service.WarmUpAsync(TestContext.Current.CancellationToken);
        await service.WarmUpAsync(TestContext.Current.CancellationToken);

        // Exactly one alert per model in total across both runs -- the latch, not the retry count,
        // governs how many alerts an operator sees.
        Assert.Equal(2, sink.Alerts.Count);
        Assert.Single(sink.Alerts, a => a.Category == AlertType.MemoryEmbeddingModelUnavailable);
        Assert.Single(sink.Alerts, a => a.Category == AlertType.MemoryRelevanceModelUnavailable);
    }

    // ── Keep-warm ticks (memory-relevance-gate 2026-07 canary fix) ──
    //
    // These tests exercise KeepWarmTickAsync/KeepWarmLoopAsync directly against simple signaling
    // fakes rather than going through StartAsync -- StartAsync also fires the real, fixture-backed
    // WarmUpAsync in the background (task 2.7's own coverage above), which would race to overwrite
    // whatever embedder/scorer these tests plant in the holders. Testing the keep-warm loop's own
    // scheduling/cancellation contract in isolation is both faster and immune to that race.

    [Fact]
    public async Task Keep_warm_tick_calls_both_the_embedder_and_the_scorer_exactly_once()
    {
        var embedder = new SignalingEmbedder(ModelId, Dimensions);
        var scorer = new SignalingRelevanceScorer(RelevanceModelId);
        var holder = new MemoryEmbedderHolder(embedder, initialQueryPrefix: "", initialCalibratedMinCosineSimilarity: null);
        var relevanceHolder = new RelevanceScorerHolder(scorer, initialCalibratedThreshold: 0.0);
        var memoryConfig = new MemoryConfig { Embeddings = { Enabled = true } };
        var service = CreateService(holder, memoryConfig, relevanceHolder, EmptyRelevanceAllowlist);

        await service.KeepWarmTickAsync(TestContext.Current.CancellationToken);

        Assert.Equal(1, embedder.CallCount);
        Assert.Equal(1, scorer.CallCount);
    }

    [Fact]
    public async Task Keep_warm_tick_swallows_a_scorer_exception_without_throwing()
    {
        var embedder = new SignalingEmbedder(ModelId, Dimensions);
        var scorer = new SignalingRelevanceScorer(RelevanceModelId, throwOnScore: new InvalidOperationException("simulated ONNX scoring failure"));
        var holder = new MemoryEmbedderHolder(embedder, initialQueryPrefix: "", initialCalibratedMinCosineSimilarity: null);
        var relevanceHolder = new RelevanceScorerHolder(scorer, initialCalibratedThreshold: 0.0);
        var memoryConfig = new MemoryConfig { Embeddings = { Enabled = true } };
        var service = CreateService(holder, memoryConfig, relevanceHolder, EmptyRelevanceAllowlist);

        // Must not throw -- a keep-warm tick failure is background maintenance, never a caller-
        // visible fault.
        await service.KeepWarmTickAsync(TestContext.Current.CancellationToken);

        Assert.Equal(1, embedder.CallCount);
        Assert.Equal(1, scorer.CallCount);
    }

    [Fact]
    public async Task Keep_warm_tick_skips_whichever_side_is_unavailable()
    {
        var embedder = new SignalingEmbedder(ModelId, Dimensions, isAvailable: false);
        var scorer = new SignalingRelevanceScorer(RelevanceModelId, isAvailable: false);
        var holder = new MemoryEmbedderHolder(embedder, initialQueryPrefix: "", initialCalibratedMinCosineSimilarity: null);
        var relevanceHolder = new RelevanceScorerHolder(scorer, initialCalibratedThreshold: 0.0);
        var memoryConfig = new MemoryConfig { Embeddings = { Enabled = true } };
        var service = CreateService(holder, memoryConfig, relevanceHolder, EmptyRelevanceAllowlist);

        await service.KeepWarmTickAsync(TestContext.Current.CancellationToken);

        Assert.Equal(0, embedder.CallCount);
        Assert.Equal(0, scorer.CallCount);
    }

    [Fact]
    public async Task Keep_warm_loop_never_ticks_when_embeddings_are_disabled()
    {
        var embedder = new SignalingEmbedder(ModelId, Dimensions);
        var scorer = new SignalingRelevanceScorer(RelevanceModelId);
        var holder = new MemoryEmbedderHolder(embedder, initialQueryPrefix: "", initialCalibratedMinCosineSimilarity: null);
        var relevanceHolder = new RelevanceScorerHolder(scorer, initialCalibratedThreshold: 0.0);
        var memoryConfig = new MemoryConfig { Embeddings = { Enabled = false } };
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var service = CreateService(holder, memoryConfig, relevanceHolder, EmptyRelevanceAllowlist, time);

        // Returns immediately (config checked up front, before the timer is even armed).
        await service.KeepWarmLoopAsync(TestContext.Current.CancellationToken);

        // Advancing time after the fact proves no timer was ever armed either.
        time.Advance(EmbeddingWarmupHostedService.KeepWarmInterval * 3);
        Assert.Equal(0, embedder.CallCount);
        Assert.Equal(0, scorer.CallCount);
    }

    [Fact]
    public async Task Keep_warm_loop_ticks_on_schedule_and_stops_cleanly_on_cancellation()
    {
        var embedder = new SignalingEmbedder(ModelId, Dimensions);
        var scorer = new SignalingRelevanceScorer(RelevanceModelId);
        var holder = new MemoryEmbedderHolder(embedder, initialQueryPrefix: "", initialCalibratedMinCosineSimilarity: null);
        var relevanceHolder = new RelevanceScorerHolder(scorer, initialCalibratedThreshold: 0.0);
        var memoryConfig = new MemoryConfig { Embeddings = { Enabled = true } };
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var service = CreateService(holder, memoryConfig, relevanceHolder, EmptyRelevanceAllowlist, time);

        using var cts = new CancellationTokenSource();
        // This is the exact cancellation contract StopAsync relies on internally (cancel the
        // token passed to KeepWarmLoopAsync, then await the loop task) -- driving it directly here
        // avoids also triggering StartAsync's real, fixture-backed WarmUpAsync (see this section's
        // header comment).
        var loopTask = service.KeepWarmLoopAsync(cts.Token);

        time.Advance(EmbeddingWarmupHostedService.KeepWarmInterval);
        await embedder.WaitForCallAsync(TestContext.Current.CancellationToken);
        await scorer.WaitForCallAsync(TestContext.Current.CancellationToken);
        Assert.Equal(1, embedder.CallCount);
        Assert.Equal(1, scorer.CallCount);

        // A second tick proves this is a recurring schedule, not a one-shot.
        time.Advance(EmbeddingWarmupHostedService.KeepWarmInterval);
        await embedder.WaitForCallAsync(TestContext.Current.CancellationToken);
        await scorer.WaitForCallAsync(TestContext.Current.CancellationToken);
        Assert.Equal(2, embedder.CallCount);
        Assert.Equal(2, scorer.CallCount);

        await cts.CancelAsync();
        // PeriodicTimer.WaitForNextTickAsync throws OperationCanceledException when its token is
        // cancelled (mirrors PidFileWatchdogService.StopAsync's own SuppressThrowing usage) --
        // that is how the loop unwinds, not a normal return.
        await loopTask.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing | ConfigureAwaitOptions.ContinueOnCapturedContext);

        // Further time advances after cancellation must not produce more ticks.
        time.Advance(EmbeddingWarmupHostedService.KeepWarmInterval * 3);
        Assert.Equal(2, embedder.CallCount);
        Assert.Equal(2, scorer.CallCount);
    }

    [Fact]
    public async Task StopAsync_cancels_and_awaits_the_startup_download()
    {
        var handler = new BlockingHttpMessageHandler();
        using var httpClient = new HttpClient(handler);
        var provisioner = new EmbeddingModelProvisioner(httpClient, _allowlist);
        var holder = new MemoryEmbedderHolder(
            new UnavailableMemoryEmbedder(ModelId, "warmup not yet run"),
            initialQueryPrefix: string.Empty,
            initialCalibratedMinCosineSimilarity: null);
        var memoryConfig = new MemoryConfig
        {
            Embeddings = { Enabled = true, ModelId = ModelId, AutoDownload = true },
        };
        using var service = new EmbeddingWarmupHostedService(
            provisioner,
            _store,
            holder,
            CreateRelevanceScorerHolder(),
            _allowlist,
            EmptyRelevanceAllowlist,
            memoryConfig,
            _paths,
            TimeProvider.System,
            NullNotificationSink.Instance,
            NullLogger<EmbeddingWarmupHostedService>.Instance);

        await service.StartAsync(TestContext.Current.CancellationToken);
        await handler.RequestStarted.Task.WaitAsync(TestContext.Current.CancellationToken);

        await service.StopAsync(TestContext.Current.CancellationToken);

        Assert.True(handler.RequestCancelled.Task.IsCompletedSuccessfully);
    }

    private EmbeddingWarmupHostedService CreateService(MemoryEmbedderHolder holder, MemoryConfig memoryConfig)
        => CreateService(holder, memoryConfig, CreateRelevanceScorerHolder(), EmptyRelevanceAllowlist);

    private EmbeddingWarmupHostedService CreateService(
        MemoryEmbedderHolder holder,
        MemoryConfig memoryConfig,
        RelevanceScorerHolder relevanceScorerHolder,
        IReadOnlyDictionary<string, RelevanceModelManifestEntry> relevanceAllowlist,
        TimeProvider? timeProvider = null,
        IOperationalNotificationSink? notificationSink = null)
        => new(_provisioner, _store, holder, relevanceScorerHolder, _allowlist, relevanceAllowlist, memoryConfig, _paths,
            timeProvider ?? TimeProvider.System, notificationSink ?? NullNotificationSink.Instance,
            NullLogger<EmbeddingWarmupHostedService>.Instance);

    private static RelevanceScorerHolder CreateRelevanceScorerHolder()
        => new(new UnavailableRelevanceScorer(RelevanceModelId, "warmup not yet run"), initialCalibratedThreshold: 0.0);

    private static readonly IReadOnlyDictionary<string, RelevanceModelManifestEntry> EmptyRelevanceAllowlist =
        new Dictionary<string, RelevanceModelManifestEntry>();

    private void PrePlaceValidModelFiles()
    {
        var dir = _paths.EmbeddingModelDirectory(ModelId);
        Directory.CreateDirectory(dir);
        File.Copy(Path.Combine(FixturesDir, "tiny-embedder.onnx"), Path.Combine(dir, "model.onnx"), overwrite: true);
        File.Copy(Path.Combine(FixturesDir, "tiny-vocab.txt"), Path.Combine(dir, "vocab.txt"), overwrite: true);
    }

    private void PrePlaceValidRelevanceModelFiles()
    {
        var dir = _paths.EmbeddingModelDirectory(RelevanceModelId);
        Directory.CreateDirectory(dir);
        File.Copy(Path.Combine(FixturesDir, "tiny-cross-encoder.onnx"), Path.Combine(dir, "model.onnx"), overwrite: true);
        File.Copy(Path.Combine(FixturesDir, "tiny-cross-encoder-vocab.txt"), Path.Combine(dir, "vocab.txt"), overwrite: true);
    }

    private IReadOnlyDictionary<string, RelevanceModelManifestEntry> RelevanceFixtureAllowlist()
    {
        var modelBytes = File.ReadAllBytes(Path.Combine(FixturesDir, "tiny-cross-encoder.onnx"));
        var vocabBytes = File.ReadAllBytes(Path.Combine(FixturesDir, "tiny-cross-encoder-vocab.txt"));

        return new Dictionary<string, RelevanceModelManifestEntry>
        {
            [RelevanceModelId] = new(
                RelevanceModelId,
                ModelUrl: new Uri("http://127.0.0.1:1/unused-model.onnx"),
                TokenizerUrl: new Uri("http://127.0.0.1:1/unused-vocab.txt"),
                ModelSha256: Sha256Hex(modelBytes),
                TokenizerSha256: Sha256Hex(vocabBytes),
                ModelByteSize: modelBytes.Length,
                CalibratedThreshold: RelevanceCalibratedThreshold),
        };
    }

    private static string Sha256Hex(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    private sealed class BlockingHttpMessageHandler : HttpMessageHandler
    {
        public TaskCompletionSource RequestStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource RequestCancelled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RequestStarted.TrySetResult();
            var response = new TaskCompletionSource<HttpResponseMessage>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            using var registration = cancellationToken.Register(
                () => response.TrySetCanceled(cancellationToken));
            try
            {
                return await response.Task;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                RequestCancelled.TrySetResult();
                throw;
            }
        }
    }

    private static async Task TryDeleteDirectoryAsync(string path)
    {
        if (!Directory.Exists(path))
            return;

        var dbPath = Path.Combine(path, "netclaw.db");
        if (File.Exists(dbPath))
        {
            var connectionString = new SqliteConnectionStringBuilder { DataSource = dbPath }.ToString();
            SqliteConnection.ClearPool(new SqliteConnection(connectionString));
        }

        for (var i = 0; i < 8; i++)
        {
            try
            {
                Directory.Delete(path, recursive: true);
                return;
            }
            catch (IOException) when (i < 7)
            {
                await Task.Delay(25 * (i + 1));
            }
            catch (UnauthorizedAccessException) when (i < 7)
            {
                await Task.Delay(25 * (i + 1));
            }
        }
    }

    /// <summary>
    /// Fake embedder for the keep-warm tests above: counts calls and signals a waiter each time
    /// <see cref="EmbedAsync"/> runs, so a test driving a <c>FakeTimeProvider</c>-scheduled
    /// <see cref="PeriodicTimer"/> can await the tick's actual completion deterministically instead
    /// of racing a real-time delay against the background loop task.
    /// </summary>
    private sealed class SignalingEmbedder(string modelId, int dimensions, bool isAvailable = true) : IMemoryEmbedder
    {
        private readonly SemaphoreSlim _signal = new(0);
        private int _callCount;

        public string ModelId => modelId;

        public int Dimensions => dimensions;

        public bool IsAvailable => isAvailable;

        public int CallCount => Volatile.Read(ref _callCount);

        public Task WaitForCallAsync(CancellationToken ct) => _signal.WaitAsync(ct);

        public ValueTask<ReadOnlyMemory<float>> EmbedAsync(string text, EmbeddingPurpose purpose, CancellationToken ct)
        {
            Interlocked.Increment(ref _callCount);
            _signal.Release();
            return ValueTask.FromResult<ReadOnlyMemory<float>>(new float[dimensions]);
        }

        public ValueTask<IReadOnlyList<ReadOnlyMemory<float>>> EmbedBatchAsync(IReadOnlyList<string> texts, EmbeddingPurpose purpose, CancellationToken ct)
            => throw new NotSupportedException("Keep-warm ticks only ever call EmbedAsync, never the batch path.");
    }

    /// <summary>
    /// Fake relevance scorer for the keep-warm tests above — mirrors <see cref="SignalingEmbedder"/>'s
    /// call-counting/signaling shape, plus an optional <paramref name="throwOnScore"/> to exercise
    /// the tick's own exception-swallowing contract.
    /// </summary>
    private sealed class SignalingRelevanceScorer(string modelId, bool isAvailable = true, Exception? throwOnScore = null) : IRelevanceScorer
    {
        private readonly SemaphoreSlim _signal = new(0);
        private int _callCount;

        public string ModelId => modelId;

        public bool IsAvailable => isAvailable;

        public int CallCount => Volatile.Read(ref _callCount);

        public Task WaitForCallAsync(CancellationToken ct) => _signal.WaitAsync(ct);

        public ValueTask<IReadOnlyList<double>> ScoreAsync(string query, IReadOnlyList<string> candidates, CancellationToken ct)
        {
            Interlocked.Increment(ref _callCount);
            _signal.Release();
            if (throwOnScore is not null)
                throw throwOnScore;
            return ValueTask.FromResult<IReadOnlyList<double>>(candidates.Select(_ => 1.0).ToArray());
        }
    }

    /// <summary>
    /// Captures every <see cref="OperationalAlert"/> emitted during a test — mirrors
    /// <c>McpReconnectionServiceTests.FakeNotificationSink</c>'s shape. Tests below only ever
    /// await <c>WarmUpAsync</c> to completion before inspecting <see cref="Alerts"/>, so no
    /// additional synchronization is needed.
    /// </summary>
    private sealed class FakeNotificationSink : IOperationalNotificationSink
    {
        public List<OperationalAlert> Alerts { get; } = [];

        public void Emit(OperationalAlert alert) => Alerts.Add(alert);
    }
}
