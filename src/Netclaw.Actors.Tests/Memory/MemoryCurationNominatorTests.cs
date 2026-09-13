// -----------------------------------------------------------------------
// <copyright file="MemoryCurationNominatorTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Runtime.CompilerServices;
using Akka.Event;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Netclaw.Actors.Memory;
using Netclaw.Actors.Protocol;
using Netclaw.Configuration;
using Xunit;
using AiChatMessage = Microsoft.Extensions.AI.ChatMessage;
using AiChatRole = Microsoft.Extensions.AI.ChatRole;

namespace Netclaw.Actors.Tests.Memory;

/// <summary>
/// Covers the embedding kNN nominator in <see cref="MemoryCurationEvaluator"/>
/// (memory-core-redesign Slice 3 Stage B, tasks 3.1/3.6). All fixtures are synthetic — no
/// operator corpus content — and cosine similarity is engineered directly via hand-crafted
/// unit vectors rather than a real embedding model, so every scenario is exact and
/// deterministic rather than dependent on a specific model's output.
///
/// <para>
/// The central invariant under test throughout this file (design D4, corroborated by
/// <c>docs/research/memory-recall-findings-2026-05.md</c> and
/// <c>docs/research/memory-audit-2026-07.md</c> §5): cosine similarity NOMINATES ONLY. It
/// forces a decision to the LLM tier; it never itself decides skip, merge, or create.
/// </para>
/// </summary>
public sealed class MemoryCurationNominatorTests : IAsyncDisposable
{
    private const string ModelId = "test-nominator-model";
    private const int Dimensions = 2;

    // Hand-crafted unit vectors with cosine similarity == 0.93 exactly (0.93^2 + 0.367623^2 ==
    // 0.999999...): the paraphrase-pair cosine the May 2026 measurement places inside the band
    // where duplicates and merely-related siblings are indistinguishable by threshold alone
    // (siblings measured at 0.905-0.941, design D4/proposal.md).
    private static readonly float[] ExistingVector = [1f, 0f];
    private static readonly float[] QueryVectorAt093 = [0.93f, 0.367623f];

    private static readonly SessionId TestSessionId = new("test-channel/nominator");

    private readonly string _baseDir = Path.Combine(Path.GetTempPath(), "netclaw-curation-nominator-tests", Guid.NewGuid().ToString("N"));
    private readonly string _dbPath;
    private readonly SQLiteMemoryStore _store;

    public MemoryCurationNominatorTests()
    {
        Directory.CreateDirectory(_baseDir);
        _dbPath = Path.Combine(_baseDir, "netclaw.db");
        _store = new SQLiteMemoryStore(_dbPath, TimeProvider.System);
    }

    public async ValueTask DisposeAsync() => await SqliteTempDirectoryCleanup.TryDeleteDirectoryAsync(_baseDir);

    // ── Nomination forcing ──────────────────────────────────────────────

    [Fact]
    public async Task Paraphrase_pair_at_cosine_0_93_forces_LLM_tier_even_though_Jaccard_band_would_have_said_Create()
    {
        var ct = TestContext.Current.CancellationToken;
        await _store.InitializeAsync(ct);

        const string existingBody = "The build pipeline stores intermediate render artifacts in a graphite-backed cache layer.";
        const string proposalContent = "Deployment jobs wait in a queue before promotion to production.";

        // Unrelated anchor names, near-zero word overlap: the pre-Slice-3 lexical/anchor tier
        // finds NO candidates at all here (CurationRulesEvaluator: "no existing candidates" ->
        // Create, zero LLM calls) — cosine nomination is the ONLY signal that finds this pair.
        Assert.True(WordJaccard(existingBody, proposalContent) < 0.4, "fixture must have low word overlap");

        await SeedDocumentWithEmbeddingAsync("graphite-render-cache", "doc-existing", existingBody, freshnessAtMs: 1000, ct);
        var operation = MakeOperation("sunfish-deploy-queue", proposalContent, freshnessAtMs: 2000);

        var embedderHolder = new MemoryEmbedderHolder(new ScriptedEmbedder(ModelId, Dimensions, QueryVectorAt093), initialQueryPrefix: "", initialCalibratedMinCosineSimilarity: null);
        var vectorIndexHolder = new MemoryVectorIndexHolder(_store);
        var chatClient = new RecordingCurationChatClient("CREATE");

        var evaluator = new MemoryCurationEvaluator(
            _store, (ILoggingAdapter)NoLogger.Instance, new MemoryCurationConfig(), chatClient, embedderHolder, vectorIndexHolder);

        var evaluation = await evaluator.EvaluateAsync(operation, TestSessionId, ct);

        Assert.Equal(1, chatClient.CallCount);
        Assert.True(evaluation.Decision.FromLlmTier);
        Assert.Contains(evaluation.Candidates, c => c.DocumentId == "doc-existing" && c.CosineSimilarity is not null);
    }

    // ── Sibling never-auto-merge ─────────────────────────────────────────

    [Fact]
    public async Task Nominee_present_LLM_says_Create_persists_two_separate_documents_not_a_merge()
    {
        var ct = TestContext.Current.CancellationToken;
        await _store.InitializeAsync(ct);

        const string existingBody = "The build pipeline stores intermediate render artifacts in a graphite-backed cache layer.";
        await SeedDocumentWithEmbeddingAsync("graphite-render-cache", "doc-existing", existingBody, freshnessAtMs: 1000, ct);

        var operation = MakeOperation(
            "sunfish-deploy-queue", "Deployment jobs wait in a queue before promotion to production.", freshnessAtMs: 2000);

        var embedderHolder = new MemoryEmbedderHolder(new ScriptedEmbedder(ModelId, Dimensions, QueryVectorAt093), initialQueryPrefix: "", initialCalibratedMinCosineSimilarity: null);
        var vectorIndexHolder = new MemoryVectorIndexHolder(_store);
        var chatClient = new RecordingCurationChatClient("CREATE");

        var evaluator = new MemoryCurationEvaluator(
            _store, (ILoggingAdapter)NoLogger.Instance, new MemoryCurationConfig(), chatClient, embedderHolder, vectorIndexHolder);

        var evaluation = await evaluator.EvaluateAsync(operation, TestSessionId, ct);
        Assert.Equal(CurationDecisionKind.Create, evaluation.Decision.Kind);
        Assert.Equal(1, chatClient.CallCount);

        var writeOp = await evaluator.ApplyDecisionAsync(operation, evaluation.Decision, evaluation.Candidates, ct);
        Assert.NotNull(writeOp);
        await _store.ApplyInlineCurationBatchAsync([writeOp!], ct);

        // Two documents survive — the nominee (a cosine-adjacent sibling in this fixture, per
        // the LLM's own CREATE call) was never auto-merged into the existing one.
        Assert.Equal(2, await CountNonTombstonedDocumentsAsync(ct));
    }

    [Fact]
    public async Task Nominee_present_with_no_LLM_available_conservatively_creates_never_auto_merges()
    {
        var ct = TestContext.Current.CancellationToken;
        await _store.InitializeAsync(ct);

        const string existingBody = "The build pipeline stores intermediate render artifacts in a graphite-backed cache layer.";
        await SeedDocumentWithEmbeddingAsync("graphite-render-cache", "doc-existing", existingBody, freshnessAtMs: 1000, ct);

        var operation = MakeOperation(
            "sunfish-deploy-queue", "Deployment jobs wait in a queue before promotion to production.", freshnessAtMs: 2000);

        var embedderHolder = new MemoryEmbedderHolder(new ScriptedEmbedder(ModelId, Dimensions, QueryVectorAt093), initialQueryPrefix: "", initialCalibratedMinCosineSimilarity: null);
        var vectorIndexHolder = new MemoryVectorIndexHolder(_store);

        // No LLM client at all — the daemon-checkpoint-worker shape today. A nominee here must
        // NOT fall to TryAutoResolveAmbiguous (which could return Skip); the outcome must be
        // Create, never a merge decided by cosine alone.
        var evaluator = new MemoryCurationEvaluator(
            _store, (ILoggingAdapter)NoLogger.Instance, new MemoryCurationConfig(), llmClient: null,
            embedderHolder: embedderHolder, vectorIndexHolder: vectorIndexHolder);

        var evaluation = await evaluator.EvaluateAsync(operation, TestSessionId, ct);
        Assert.Equal(CurationDecisionKind.Create, evaluation.Decision.Kind);
        Assert.False(evaluation.Decision.FromLlmTier);
        Assert.Contains("conservative create", evaluation.Decision.Reason);
        Assert.Contains("no auto-merge on cosine alone", evaluation.Decision.Reason);

        var writeOp = await evaluator.ApplyDecisionAsync(operation, evaluation.Decision, evaluation.Candidates, ct);
        Assert.NotNull(writeOp);
        await _store.ApplyInlineCurationBatchAsync([writeOp!], ct);

        Assert.Equal(2, await CountNonTombstonedDocumentsAsync(ct));
    }

    // ── Novel proposal skips the curator ─────────────────────────────────

    [Fact]
    public async Task Novel_proposal_with_no_nominee_and_no_anchor_match_skips_the_curator_entirely()
    {
        var ct = TestContext.Current.CancellationToken;
        await _store.InitializeAsync(ct);

        // Empty store: no anchor to match, and the vector index has nothing to nominate — the
        // "median nominee count on a random write is 0" common case (design D4 point 3).
        var operation = MakeOperation(
            "brand-new-topic", "Completely novel content nobody has proposed before.", freshnessAtMs: 1000);

        var embedderHolder = new MemoryEmbedderHolder(new ScriptedEmbedder(ModelId, Dimensions, QueryVectorAt093), initialQueryPrefix: "", initialCalibratedMinCosineSimilarity: null);
        var vectorIndexHolder = new MemoryVectorIndexHolder(_store);
        var chatClient = new RecordingCurationChatClient("CREATE");

        var evaluator = new MemoryCurationEvaluator(
            _store, (ILoggingAdapter)NoLogger.Instance, new MemoryCurationConfig(), chatClient, embedderHolder, vectorIndexHolder);

        var evaluation = await evaluator.EvaluateAsync(operation, TestSessionId, ct);

        Assert.Equal(CurationDecisionKind.Create, evaluation.Decision.Kind);
        Assert.False(evaluation.Decision.FromLlmTier);
        Assert.Equal(0, chatClient.CallCount);
    }

    // ── Degraded path ─────────────────────────────────────────────────────

    [Fact]
    public async Task Embedder_unavailable_falls_back_to_lexical_search_and_logs_the_degraded_marker()
    {
        var ct = TestContext.Current.CancellationToken;
        await _store.InitializeAsync(ct);

        const string existingBody = "Deployment jobs wait in a queue before promotion to production servers.";
        const string proposalContent = "Deployment jobs wait in a queue before reaching production.";

        // Substantial lexical overlap (unlike the nomination-forcing fixture above) so the
        // degraded path's lexical content-term search actually surfaces this document.
        Assert.True(WordJaccard(existingBody, proposalContent) > 0.4, "fixture must have high word overlap");

        await SeedDocumentWithEmbeddingAsync("totally-different-topic", "doc-existing", existingBody, freshnessAtMs: 1000, ct);
        var operation = MakeOperation("another-unrelated-subject", proposalContent, freshnessAtMs: 2000);

        var recordingLogger = new RecordingLogger();
        var embedderHolder = new MemoryEmbedderHolder(new UnavailableMemoryEmbedder(ModelId, "not provisioned"), initialQueryPrefix: "", initialCalibratedMinCosineSimilarity: null);
        var vectorIndexHolder = new MemoryVectorIndexHolder(_store);

        var evaluator = new MemoryCurationEvaluator(
            _store, (ILogger)recordingLogger, new MemoryCurationConfig(), llmClient: null, embedderHolder, vectorIndexHolder);

        var evaluation = await evaluator.EvaluateAsync(operation, TestSessionId, ct);

        Assert.Contains(recordingLogger.Entries, e => e.Contains("curation_nominator_degraded", StringComparison.Ordinal));
        Assert.Contains(evaluation.Candidates, c => c.DocumentId == "doc-existing");
        // Lexical-path candidates never carry cosine evidence.
        Assert.All(evaluation.Candidates, c => Assert.Null(c.CosineSimilarity));
    }

    [Fact]
    public async Task Null_embedder_holder_is_treated_identically_to_unavailable_and_degrades()
    {
        var ct = TestContext.Current.CancellationToken;
        await _store.InitializeAsync(ct);

        const string existingBody = "Deployment jobs wait in a queue before promotion to production servers.";
        const string proposalContent = "Deployment jobs wait in a queue before reaching production.";

        await SeedDocumentWithEmbeddingAsync("totally-different-topic", "doc-existing", existingBody, freshnessAtMs: 1000, ct);
        var operation = MakeOperation("another-unrelated-subject", proposalContent, freshnessAtMs: 2000);

        var recordingLogger = new RecordingLogger();

        // No embedder holder AND no vector index holder at all — a test harness / build that
        // never wired up the embedding subsystem, same as the pre-Slice-3 constructor shape.
        var evaluator = new MemoryCurationEvaluator(
            _store, (ILogger)recordingLogger, new MemoryCurationConfig());

        var evaluation = await evaluator.EvaluateAsync(operation, TestSessionId, ct);

        Assert.Contains(recordingLogger.Entries, e => e.Contains("curation_nominator_degraded", StringComparison.Ordinal));
        Assert.Contains(evaluation.Candidates, c => c.DocumentId == "doc-existing");
    }

    // ── helpers ──────────────────────────────────────────────────────────

    private async Task SeedDocumentWithEmbeddingAsync(
        string anchorName, string docId, string content, long freshnessAtMs, CancellationToken ct)
    {
        var anchor = _store.CreateDefaultAnchor(anchorName);
        await _store.UpsertDocumentAsync(new SQLiteMemoryDocument(
            DocumentId: docId,
            Anchor: anchor,
            MemoryClass: "durable_fact",
            Title: $"Existing {anchorName}",
            MarkdownBody: content,
            AliasesJson: null,
            FacetsJson: null,
            SlotsJson: null,
            UpdateSemantics: "merge-document",
            Sensitivity: "normal",
            RecallMode: "auto",
            Confidence: 0.9,
            FreshnessAtMs: freshnessAtMs,
            ExpiresAtMs: null,
            CreatedAtMs: freshnessAtMs,
            UpdatedAtMs: freshnessAtMs), ct);

        await _store.UpsertEmbeddingAsync(
            docId,
            MemoryEmbedOnWriteCoordinator.DocumentItemKind,
            ModelId,
            MemoryContentHasher.ComputeHash($"Existing {anchorName}", content),
            ExistingVector,
            ct);
    }

    private async Task<int> CountNonTombstonedDocumentsAsync(CancellationToken ct)
    {
        await using var conn = new SqliteConnection($"Data Source={_dbPath}");
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM memory_documents WHERE update_semantics != 'tombstone';";
        return Convert.ToInt32(await cmd.ExecuteScalarAsync(ct));
    }

    private static double WordJaccard(string a, string b)
    {
        var wordsA = Tokenize(a);
        var wordsB = Tokenize(b);
        var union = wordsA.Union(wordsB).Count();
        return union == 0 ? 0 : (double)wordsA.Intersect(wordsB).Count() / union;

        static HashSet<string> Tokenize(string text) =>
            text.Split([' ', '.', ',', ':', ';', '!', '?'], StringSplitOptions.RemoveEmptyEntries)
                .Select(w => w.Trim().ToLowerInvariant())
                .Where(w => w.Length > 0)
                .ToHashSet();
    }

    private static SQLiteMemoryCurationOperation MakeOperation(
        string anchor,
        string content,
        string kind = "document",
        string updateSemantics = "merge-document",
        long freshnessAtMs = 2000) =>
        new(
            Kind: kind,
            MemoryClass: "durable_fact",
            MemoryId: null,
            AnchorCanonicalName: anchor,
            AnchorType: "concept",
            Title: $"Title for {anchor}",
            Content: content,
            AliasesJson: null,
            FacetsJson: null,
            SlotsJson: null,
            Relations: null,
            UpdateSemantics: updateSemantics,
            Boundary: TrustBoundary.TrustedInstanceValue,
            Audience: TrustAudience.Public,
            Sensitivity: "normal",
            RecallMode: "auto",
            Confidence: 0.9,
            FreshnessAtMs: freshnessAtMs,
            ExpiresAtMs: null);

    /// <summary>
    /// Fake embedder that ignores its input text and always returns the same, hand-crafted
    /// query vector — sufficient here because every test in this file embeds at most one
    /// proposal, and the geometry (not the input text) is what needs to be controlled.
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
    /// Scripted <see cref="IChatClient"/> that records how many times it was invoked, so tests
    /// can assert the LLM tier was (or was never) reached — the nomination-forcing contract's
    /// core observable. Mirrors <c>MemoryCurationEvaluatorParityTests.ScriptedCurationChatClient</c>
    /// plus a call counter (kept as a separate private copy per that file's own convention).
    /// </summary>
    private sealed class RecordingCurationChatClient(string? responseText) : IChatClient
    {
        public int CallCount { get; private set; }

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            CallCount++;
            return Task.FromResult(new ChatResponse(new AiChatMessage(AiChatRole.Assistant, responseText ?? string.Empty)));
        }

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            CallCount++;
            return StreamAsync(cancellationToken);
        }

        private async IAsyncEnumerable<ChatResponseUpdate> StreamAsync([EnumeratorCancellation] CancellationToken cancellationToken)
        {
            if (responseText is not null)
                yield return new ChatResponseUpdate(AiChatRole.Assistant, responseText);

            await Task.CompletedTask;
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }
    }

    /// <summary>Records every log line emitted through the Microsoft.Extensions.Logging ctor.</summary>
    private sealed class RecordingLogger : ILogger
    {
        public List<string> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => true;

        public void Log<TState>(
            Microsoft.Extensions.Logging.LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
            => Entries.Add(formatter(state, exception));
    }
}
