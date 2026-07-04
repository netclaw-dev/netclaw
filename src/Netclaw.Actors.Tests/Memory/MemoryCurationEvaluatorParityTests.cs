// -----------------------------------------------------------------------
// <copyright file="MemoryCurationEvaluatorParityTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Runtime.CompilerServices;
using Akka.Event;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Netclaw.Actors.Memory;
using Netclaw.Actors.Protocol;
using Netclaw.Configuration;
using Xunit;
using AiChatMessage = Microsoft.Extensions.AI.ChatMessage;
using AiChatRole = Microsoft.Extensions.AI.ChatRole;

namespace Netclaw.Actors.Tests.Memory;

/// <summary>
/// Characterization tests for memory-core-redesign Slice 1: proves the evaluator produces
/// IDENTICAL decisions whether constructed the way the inline per-session actor constructs
/// it (Akka <see cref="ILoggingAdapter"/>, no LLM) or the way the daemon checkpoint worker
/// constructs it (Microsoft.Extensions.Logging <see cref="ILogger"/>, no LLM). Both
/// evaluators share one seeded <see cref="SQLiteMemoryStore"/> so their candidate lookups
/// see identical data. Covers the full decision matrix (record bypass, exact/fuzzy anchor
/// tiers, gray-zone auto-resolve, and the GuardDestructiveUpdate downgrade — the one
/// behavior that only fired on the inline path before this slice, audit finding D14) plus
/// the LLM tier (parseable decision, empty-response fallback) using a scripted
/// <see cref="IChatClient"/>.
/// </summary>
public sealed class MemoryCurationEvaluatorParityTests : IAsyncDisposable
{
    private static readonly SessionId TestSessionId = new("test-channel/curation-parity");

    private readonly string _baseDir = Path.Combine(Path.GetTempPath(), "netclaw-curation-evaluator-parity-tests", Guid.NewGuid().ToString("N"));
    private readonly string _dbPath;
    private readonly SQLiteMemoryStore _store;

    public MemoryCurationEvaluatorParityTests()
    {
        Directory.CreateDirectory(_baseDir);
        _dbPath = Path.Combine(_baseDir, "netclaw.db");
        _store = new SQLiteMemoryStore(_dbPath, TimeProvider.System);
    }

    public async ValueTask DisposeAsync() => await SqliteTempDirectoryCleanup.TryDeleteDirectoryAsync(_baseDir);

    // ── record bypass ───────────────────────────────────────────────

    [Fact]
    public async Task RecordBypass_returns_Create_identically_on_both_paths()
    {
        var ct = TestContext.Current.CancellationToken;
        await _store.InitializeAsync(ct);

        var operation = MakeOperation(
            "any-anchor", "irrelevant content", kind: "record", updateSemantics: "immutable-record");

        var (fromActor, fromEngine) = await EvaluateOnBothAsync(operation, ct);

        AssertSameDecision(fromActor, fromEngine);
        Assert.Equal(CurationDecisionKind.Create, fromActor.Kind);
        Assert.Contains("immutable record", fromActor.Reason);
    }

    // ── exact anchor, high overlap -> Skip ──────────────────────────

    [Fact]
    public async Task ExactAnchorHighOverlap_returns_Skip_identically()
    {
        var ct = TestContext.Current.CancellationToken;
        await _store.InitializeAsync(ct);
        await SeedDocumentAsync("favorite-color", "doc-color", "favorite color is blue", freshnessAtMs: 1000, ct);

        var operation = MakeOperation("favorite-color", "favorite color is blue", freshnessAtMs: 2000);

        var (fromActor, fromEngine) = await EvaluateOnBothAsync(operation, ct);

        AssertSameDecision(fromActor, fromEngine);
        Assert.Equal(CurationDecisionKind.Skip, fromActor.Kind);
        Assert.Equal("doc-color", fromActor.TargetDocumentId);
    }

    // ── exact anchor, newer content, guard-clean superset -> Update ─

    [Fact]
    public async Task ExactAnchorNewerSuperset_returns_Update_identically()
    {
        var ct = TestContext.Current.CancellationToken;
        await _store.InitializeAsync(ct);
        await SeedDocumentAsync("latest-version", "doc-version", "Latest version is 1.5.62.", freshnessAtMs: 1000, ct);

        // Proposal is a strict superset of the existing body, so GuardDestructiveUpdate
        // (applied to every non-ambiguous decision, not just LLM ones) does not downgrade it.
        var operation = MakeOperation(
            "latest-version",
            "Latest version is 1.5.62. Released with the new serializer.",
            freshnessAtMs: 2000);

        var (fromActor, fromEngine) = await EvaluateOnBothAsync(operation, ct);

        AssertSameDecision(fromActor, fromEngine);
        Assert.Equal(CurationDecisionKind.Update, fromActor.Kind);
        Assert.Equal("doc-version", fromActor.TargetDocumentId);
    }

    // ── fuzzy anchor, high overlap -> Consolidate ───────────────────

    [Fact]
    public async Task FuzzyAnchorHighOverlap_returns_Consolidate_identically()
    {
        var ct = TestContext.Current.CancellationToken;
        await _store.InitializeAsync(ct);
        await SeedDocumentAsync(
            "akka-net-latest-release", "doc-akka", "Akka.NET latest release version is 1.5.62", freshnessAtMs: 1000, ct);

        var operation = MakeOperation(
            "akka-net-release", "Akka.NET latest release version is 1.5.62", freshnessAtMs: 2000);

        var (fromActor, fromEngine) = await EvaluateOnBothAsync(operation, ct);

        AssertSameDecision(fromActor, fromEngine);
        Assert.Equal(CurationDecisionKind.Consolidate, fromActor.Kind);
        Assert.NotNull(fromActor.ConsolidationTargetIds);
        Assert.Contains("doc-akka", fromActor.ConsolidationTargetIds!);
    }

    // ── gray zone (0.4-0.8), no LLM -> deterministic auto-resolve ───

    [Fact]
    public async Task GrayZoneNoLlm_autoResolves_to_Skip_identically()
    {
        var ct = TestContext.Current.CancellationToken;
        await _store.InitializeAsync(ct);
        await SeedDocumentAsync(
            "netclaw-github-repository",
            "doc-repo",
            "Netclaw GitHub repository: https://github.com/netclaw-dev/netclaw. The repository is private.",
            freshnessAtMs: 1000,
            ct);

        var operation = MakeOperation(
            "netclaw-github-repo",
            "Netclaw GitHub repository at https://github.com/netclaw-dev/netclaw, private repo",
            freshnessAtMs: 2000);

        var (fromActor, fromEngine) = await EvaluateOnBothAsync(operation, ct);

        AssertSameDecision(fromActor, fromEngine);
        Assert.Equal(CurationDecisionKind.Skip, fromActor.Kind);
        Assert.Contains("auto-resolved", fromActor.Reason);
    }

    // ── fuzzy anchor, low overlap -> Create ─────────────────────────

    [Fact]
    public async Task FuzzyAnchorLowOverlap_returns_Create_identically()
    {
        var ct = TestContext.Current.CancellationToken;
        await _store.InitializeAsync(ct);
        await SeedDocumentAsync(
            "akka-net-latest-release",
            "doc-postgres",
            "PostgreSQL 15 is the primary datastore for user profiles and session history",
            freshnessAtMs: 1000,
            ct);

        var operation = MakeOperation(
            "akka-net-release", "The CI pipeline deploys to staging on every PR merge", freshnessAtMs: 2000);

        var (fromActor, fromEngine) = await EvaluateOnBothAsync(operation, ct);

        AssertSameDecision(fromActor, fromEngine);
        Assert.Equal(CurationDecisionKind.Create, fromActor.Kind);
    }

    // ── guard downgrade: Update whose proposal drops existing body -> Skip ──

    [Fact]
    public async Task GuardDowngrade_narrowerProposal_downgradesUpdateToSkip_identically()
    {
        var ct = TestContext.Current.CancellationToken;
        await _store.InitializeAsync(ct);
        await SeedDocumentAsync(
            "widget-specs",
            "doc-widget",
            "Widget specs: 16 cores, 64GB RAM, 2 NICs. Pricing on file. Vendor contacts listed.",
            freshnessAtMs: 1000,
            ct);

        // Newer, but narrower — the rules tier would pick Update (exact anchor, low
        // overlap, fresher), and GuardDestructiveUpdate must downgrade it on BOTH paths
        // now (audit finding D14: this guard used to run only on the inline actor path).
        var operation = MakeOperation("widget-specs", "Widget pricing is TBD as of Q2.", freshnessAtMs: 2000);

        var (fromActor, fromEngine) = await EvaluateOnBothAsync(operation, ct);

        AssertSameDecision(fromActor, fromEngine);
        Assert.Equal(CurationDecisionKind.Skip, fromActor.Kind);
        Assert.Contains("update guarded", fromActor.Reason);
        Assert.Equal("doc-widget", fromActor.TargetDocumentId);
    }

    // ── LLM tier: parseable decision ─────────────────────────────────

    [Fact]
    public async Task LlmPresent_parseableDecision_returns_guarded_llm_decision()
    {
        var ct = TestContext.Current.CancellationToken;
        await _store.InitializeAsync(ct);
        await SeedDocumentAsync(
            "netclaw-github-repository",
            "doc-repo",
            "Netclaw GitHub repository: https://github.com/netclaw-dev/netclaw. The repository is private.",
            freshnessAtMs: 1000,
            ct);

        var operation = MakeOperation(
            "netclaw-github-repo",
            "Netclaw GitHub repository at https://github.com/netclaw-dev/netclaw, private repo",
            freshnessAtMs: 2000);

        var evaluator = new MemoryCurationEvaluator(
            _store, (ILoggingAdapter)NoLogger.Instance, new ScriptedCurationChatClient("SKIP"));

        var decision = await evaluator.EvaluateAsync(operation, TestSessionId, ct);

        Assert.Equal(CurationDecisionKind.Skip, decision.Kind);
        Assert.Contains("LLM decision", decision.Reason);
    }

    // ── LLM tier: empty response -> deterministic fallback ──────────

    [Fact]
    public async Task LlmPresent_emptyResponse_fallsBackToDeterministicAutoResolve()
    {
        var ct = TestContext.Current.CancellationToken;
        await _store.InitializeAsync(ct);
        await SeedDocumentAsync(
            "netclaw-github-repository",
            "doc-repo",
            "Netclaw GitHub repository: https://github.com/netclaw-dev/netclaw. The repository is private.",
            freshnessAtMs: 1000,
            ct);

        var operation = MakeOperation(
            "netclaw-github-repo",
            "Netclaw GitHub repository at https://github.com/netclaw-dev/netclaw, private repo",
            freshnessAtMs: 2000);

        // Empty stream (no yields) reproduces a provider that returns nothing parseable —
        // TryLlmEvaluationAsync must surface curation_llm_no_decision and fall through to
        // the same deterministic auto-resolve path the no-LLM matrix case exercises.
        var evaluator = new MemoryCurationEvaluator(
            _store, (ILoggingAdapter)NoLogger.Instance, new ScriptedCurationChatClient(responseText: null));

        var decision = await evaluator.EvaluateAsync(operation, TestSessionId, ct);

        Assert.Equal(CurationDecisionKind.Skip, decision.Kind);
        Assert.Contains("auto-resolved", decision.Reason);
    }

    // ── helpers ──────────────────────────────────────────────────────

    private async Task<(CurationDecision FromActor, CurationDecision FromEngine)> EvaluateOnBothAsync(
        SQLiteMemoryCurationOperation operation, CancellationToken ct)
    {
        // Constructed exactly as MemoryCurationActor and MemoryCurationEngine construct
        // their evaluators today: no LLM client, differing only in which logger stack
        // they log through.
        var actorLike = new MemoryCurationEvaluator(_store, (ILoggingAdapter)NoLogger.Instance);
        var engineLike = new MemoryCurationEvaluator(_store, (ILogger)NullLogger.Instance);

        var fromActor = await actorLike.EvaluateAsync(operation, TestSessionId, ct);
        var fromEngine = await engineLike.EvaluateAsync(operation, TestSessionId, ct);
        return (fromActor, fromEngine);
    }

    private async Task SeedDocumentAsync(
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

    private static void AssertSameDecision(CurationDecision expected, CurationDecision actual)
    {
        Assert.Equal(expected.Kind, actual.Kind);
        Assert.Equal(expected.TargetDocumentId, actual.TargetDocumentId);
        Assert.Equal(expected.CanonicalAnchorName, actual.CanonicalAnchorName);
        Assert.Equal(expected.Reason, actual.Reason);

        if (expected.ConsolidationTargetIds is null)
            Assert.Null(actual.ConsolidationTargetIds);
        else
            Assert.Equal(expected.ConsolidationTargetIds, actual.ConsolidationTargetIds);
    }

    /// <summary>
    /// Minimal scripted <see cref="IChatClient"/>: streams <paramref name="responseText"/>
    /// as a single update, or nothing at all when null (reproducing an empty/garbled
    /// provider response so the deterministic fallback path can be exercised).
    /// </summary>
    private sealed class ScriptedCurationChatClient(string? responseText) : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
            => Task.FromResult(new ChatResponse(new AiChatMessage(AiChatRole.Assistant, responseText ?? string.Empty)));

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
            => StreamAsync(cancellationToken);

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
}
