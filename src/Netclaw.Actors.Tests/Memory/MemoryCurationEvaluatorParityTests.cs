// -----------------------------------------------------------------------
// <copyright file="MemoryCurationEvaluatorParityTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Runtime.CompilerServices;
using Akka.Event;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Netclaw.Actors.Memory;
using Netclaw.Actors.Protocol;
using Netclaw.Configuration;
using Netclaw.Tests.Utilities;
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
        // The primary write target: ApplyDecisionAsync sets MemoryId from this so the
        // store takes the explicit-target overwrite path (collapse), not dedup-append.
        Assert.Equal("doc-akka", fromActor.TargetDocumentId);
    }

    // ── Consolidate end-to-end: writes a guarded append into the explicit target ──

    [Fact]
    public async Task ConsolidateDecision_appliedThroughStore_writesGuardedAppendIntoExplicitTarget()
    {
        var ct = TestContext.Current.CancellationToken;
        await _store.InitializeAsync(ct);
        await SeedDocumentAsync(
            "akka-net-latest-release", "doc-akka", "Akka.NET latest release version is 1.5.62", freshnessAtMs: 1000, ct);

        // One extra token ("now") keeps Jaccard overlap at 0.9 (> 0.8 threshold) while making
        // the proposal body distinct from the seed, so the write is observable.
        var operation = MakeOperation(
            "akka-net-release", "Akka.NET latest release version is now 1.5.62", freshnessAtMs: 2000);

        var evaluator = new MemoryCurationEvaluator(_store, (ILoggingAdapter)NoLogger.Instance, new MemoryCurationConfig());
        var evaluation = await evaluator.EvaluateAsync(operation, TestSessionId, ct);
        Assert.Equal(CurationDecisionKind.Consolidate, evaluation.Decision.Kind);

        var writeOp = await evaluator.ApplyDecisionAsync(operation, evaluation.Decision, evaluation.Candidates, ct);
        Assert.NotNull(writeOp);
        // The deterministic rules tier never synthesizes a MergedBody, so
        // ApplyGuardedMergeOrAppend always takes the lossless structural-append branch for
        // this tier — but it still writes into the SAME explicit primary target document
        // (doc-akka) that ApplyConsolidate resolved from the decision's ConsolidationTargetIds,
        // rather than falling through to the store's separate anchor-based dedup lookup.
        Assert.Equal("doc-akka", writeOp!.MemoryId);
        Assert.Equal("append-document", writeOp.UpdateSemantics);

        await _store.ApplyInlineCurationBatchAsync([writeOp], ct);

        var (body, updateSemantics) = await ReadDocumentBodyAndSemanticsAsync("doc-akka", ct);
        // Lossless append: original content survives verbatim, the proposal is appended
        // under a dated separator — never a destructive overwrite.
        Assert.Contains("Akka.NET latest release version is 1.5.62", body);
        Assert.Contains("Akka.NET latest release version is now 1.5.62", body);
        Assert.Contains("_[merged", body);
        Assert.Equal("append-document", updateSemantics);
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

    // ── guard downgrade: Update whose proposal drops existing body -> falls through ──

    /// <summary>
    /// Guard-fallthrough fix (July 2026 audit, eval run <c>ad9a2312</c>): before this fix, a
    /// guard-rejected anchor-matched Update terminated as Skip — an explicit proposal silently
    /// becoming a no-op with zero writes. No other candidate exists in this store for the
    /// fallen-through content search or embedding nominator (both unavailable/empty here) to
    /// find, so the terminal decision is the Create default the flow's remarks document — NOT
    /// the old Skip. This is the same fixture <c>GuardDowngrade_narrowerProposal_downgradesUpdateToSkip_identically</c>
    /// used pre-fix (renamed here since Skip was exactly the bug).
    /// </summary>
    [Fact]
    public async Task GuardDowngrade_narrowerProposal_noOtherCandidates_fallsThroughToCreate_identically()
    {
        var ct = TestContext.Current.CancellationToken;
        await _store.InitializeAsync(ct);
        await SeedDocumentAsync(
            "widget-specs",
            "doc-widget",
            "Widget specs: 16 cores, 64GB RAM, 2 NICs. Pricing on file. Vendor contacts listed.",
            freshnessAtMs: 1000,
            ct);

        // Newer, but narrower — the rules tier would pick Update (exact anchor, low overlap,
        // fresher), and GuardDestructiveUpdate downgrades it on BOTH paths (audit finding D14:
        // this guard used to run only on the inline actor path). Pre-fix, that downgrade was
        // returned as the terminal Skip decision — the silent-fallback bug. Post-fix, the guard
        // rejection triggers a fall-through re-evaluation (no exact anchor match, nomination/
        // content search run for the first time, rules tier re-runs as pure fuzzy) which here
        // finds nothing else to match against and lands on Create.
        var operation = MakeOperation("widget-specs", "Widget pricing is TBD as of Q2.", freshnessAtMs: 2000);

        var (fromActor, fromEngine) = await EvaluateOnBothAsync(operation, ct);

        AssertSameDecision(fromActor, fromEngine);
        Assert.Equal(CurationDecisionKind.Create, fromActor.Kind);
        Assert.Contains("fuzzy anchor match but low content overlap", fromActor.Reason);
    }

    /// <summary>
    /// Regression companion to the Create case above: when the guard-rejected proposal IS close
    /// enough in content to the anchor target to clear the deterministic auto-resolve thresholds
    /// (<see cref="CurationRulesEvaluator.TryAutoResolveAmbiguous"/>'s 60% content-overlap / 50%
    /// anchor-Jaccard bars) once re-evaluated as an ordinary fuzzy candidate, the fall-through
    /// must NOT force Create — it must let the normal ambiguous/auto-resolve machinery decide,
    /// which correctly lands on Skip here. This proves the fix doesn't trade "always drops the
    /// fact" for "never skips a real duplicate" — the fall-through defers to whatever the rest of
    /// the flow genuinely produces.
    /// </summary>
    [Fact]
    public async Task GuardDowngrade_contentCloseEnoughToAutoResolve_fallsThroughToLegitimateSkip_identically()
    {
        var ct = TestContext.Current.CancellationToken;
        await _store.InitializeAsync(ct);
        await SeedDocumentAsync(
            "widget-specs",
            "doc-widget",
            "Widget specs: 16 cores, 64GB RAM, 2 NICs. Warranty is 3 years from Acme Corp in Denver.",
            freshnessAtMs: 1000,
            ct);

        // Reworded/reordered restatement of the SAME facts: word-level overlap is ~62% (inside
        // the exact-match tier's Update band, ≤80%) but GuardDestructiveUpdate's stricter
        // substring-containment check fails (the words are reordered, not a literal superset),
        // so the guard still rejects. Re-evaluated as a fuzzy candidate after fall-through, that
        // same ~62% overlap clears TryAutoResolveAmbiguous's 60% content / 100% anchor-Jaccard
        // (identical anchor name) thresholds, so this is genuine skip territory rather than the
        // guard's blunt termination.
        var operation = MakeOperation(
            "widget-specs",
            "Acme Corp widget in Denver: 2 NICs, 64GB RAM, 16 cores, and a 3 year warranty included.",
            freshnessAtMs: 2000);

        var (fromActor, fromEngine) = await EvaluateOnBothAsync(operation, ct);

        AssertSameDecision(fromActor, fromEngine);
        Assert.Equal(CurationDecisionKind.Skip, fromActor.Kind);
        Assert.Contains("auto-resolved", fromActor.Reason);
        Assert.Equal("doc-widget", fromActor.TargetDocumentId);
    }

    /// <summary>
    /// Asserts the structured <c>curation_guard_fallthrough</c> marker fires with the rejected
    /// anchor and target — the July 2026 audit tooling greps daemon logs for this exact string
    /// (per this class's remarks), so the marker itself is a load-bearing observability
    /// contract, not incidental.
    /// </summary>
    [Fact]
    public async Task GuardDowngrade_logsStructuredFallthroughMarker()
    {
        var ct = TestContext.Current.CancellationToken;
        await _store.InitializeAsync(ct);
        await SeedDocumentAsync(
            "widget-specs",
            "doc-widget",
            "Widget specs: 16 cores, 64GB RAM, 2 NICs. Pricing on file. Vendor contacts listed.",
            freshnessAtMs: 1000,
            ct);

        var operation = MakeOperation("widget-specs", "Widget pricing is TBD as of Q2.", freshnessAtMs: 2000);

        var recordingLogger = new RecordingLogger();
        var evaluator = new MemoryCurationEvaluator(_store, (ILogger)recordingLogger, new MemoryCurationConfig());

        var evaluation = await evaluator.EvaluateAsync(operation, TestSessionId, ct);

        Assert.Equal(CurationDecisionKind.Create, evaluation.Decision.Kind);
        Assert.Contains(
            recordingLogger.Entries,
            e => e.Contains("curation_guard_fallthrough", StringComparison.Ordinal)
                 && e.Contains("widget-specs", StringComparison.Ordinal)
                 && e.Contains("doc-widget", StringComparison.Ordinal));
    }

    // ── guard downgrade + nominator: near-dupe elsewhere still forces the LLM tier ──

    /// <summary>
    /// A guard-rejected anchor Update must not merely fall through to Create/auto-resolve when a
    /// real embedding nominee is available — the fall-through re-runs nomination (this proposal's
    /// exact anchor match previously short-circuited it entirely, so it had never run at all), and
    /// a nominee at or above <see cref="MemoryCurationConfig.NominatorSimilarityThreshold"/> must
    /// still force the LLM tier exactly as it would for a proposal with no anchor match in the
    /// first place (design D4: cosine nominates, it never auto-decides).
    /// </summary>
    [Fact]
    public async Task GuardDowngrade_withNominatorNearDupe_forcesLlmTier_identically()
    {
        var ct = TestContext.Current.CancellationToken;
        await _store.InitializeAsync(ct);

        // Same anchor-collision shape as the eval-run repro: an exact anchor match whose content
        // is unrelated to the proposal (guard will reject the Update), PLUS a real near-duplicate
        // elsewhere in the store that only the embedding nominator — never run on the first pass
        // because the exact anchor match short-circuited it — can find.
        await SeedDocumentAsync(
            "the",
            "doc-unrelated-junk-anchor",
            "Unrelated content that happens to share the same junk anchor name.",
            freshnessAtMs: 1000,
            ct);

        const string nearDupeBody = "The build pipeline stores intermediate render artifacts in a graphite-backed cache layer.";
        var nearDupeAnchor = _store.CreateDefaultAnchor("graphite-render-cache");
        await _store.UpsertDocumentAsync(new SQLiteMemoryDocument(
            DocumentId: "doc-near-dupe",
            Anchor: nearDupeAnchor,
            MemoryClass: "durable_fact",
            Title: "Existing near-dupe",
            MarkdownBody: nearDupeBody,
            AliasesJson: null,
            FacetsJson: null,
            SlotsJson: null,
            UpdateSemantics: "merge-document",
            Sensitivity: "normal",
            RecallMode: "auto",
            Confidence: 0.9,
            FreshnessAtMs: 1000,
            ExpiresAtMs: null,
            CreatedAtMs: 1000,
            UpdatedAtMs: 1000), ct);
        await _store.UpsertEmbeddingAsync(
            "doc-near-dupe", MemoryEmbedOnWriteCoordinator.DocumentItemKind, "test-nominator-model",
            MemoryContentHasher.ComputeHash("Existing near-dupe", nearDupeBody),
            new float[] { 1f, 0f }, ct);

        var operation = MakeOperation(
            "the", "Deployment jobs wait in a queue before promotion to production.", freshnessAtMs: 2000);

        var embedderHolder = new MemoryEmbedderHolder(
            new ScriptedEmbedder("test-nominator-model", dimensions: 2, [0.93f, 0.367623f]),
            initialQueryPrefix: "",
            initialCalibratedMinCosineSimilarity: null);
        var vectorIndexHolder = new MemoryVectorIndexHolder(_store);

        var actorLike = new MemoryCurationEvaluator(
            _store, (ILoggingAdapter)NoLogger.Instance, new MemoryCurationConfig(),
            new ScriptedCurationChatClient("SKIP"), embedderHolder, vectorIndexHolder);
        var engineLike = new MemoryCurationEvaluator(
            _store, (ILogger)NullLogger.Instance, new MemoryCurationConfig(),
            new ScriptedCurationChatClient("SKIP"), embedderHolder, vectorIndexHolder);

        var fromActor = (await actorLike.EvaluateAsync(operation, TestSessionId, ct)).Decision;
        var fromEngine = (await engineLike.EvaluateAsync(operation, TestSessionId, ct)).Decision;

        AssertSameDecision(fromActor, fromEngine);
        Assert.True(fromActor.FromLlmTier);
        Assert.Equal(CurationDecisionKind.Skip, fromActor.Kind);
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
            _store, (ILoggingAdapter)NoLogger.Instance, new MemoryCurationConfig(), new ScriptedCurationChatClient("SKIP"));

        var decision = (await evaluator.EvaluateAsync(operation, TestSessionId, ct)).Decision;

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

        // ResponseText = null makes FakeChatClient emit its default marker text
        // ("[fake] Response #1") rather than a truly empty response, but the marker
        // still isn't a recognized SKIP/CREATE/UPDATE/CONSOLIDATE keyword, so
        // CurationPromptBuilder.ParseResponse still returns no decision and
        // TryLlmEvaluationAsync still surfaces curation_llm_no_decision and falls
        // through to the same deterministic auto-resolve path the no-LLM matrix case
        // exercises.
        var evaluator = new MemoryCurationEvaluator(
            _store, (ILoggingAdapter)NoLogger.Instance, new MemoryCurationConfig(), new ScriptedCurationChatClient(responseText: null));

        var decision = (await evaluator.EvaluateAsync(operation, TestSessionId, ct)).Decision;

        Assert.Equal(CurationDecisionKind.Skip, decision.Kind);
        Assert.Contains("auto-resolved", decision.Reason);
    }

    // ── Nominator-present parity (memory-core-redesign Slice 3 Stage B) ──

    /// <summary>
    /// Extends the parity contract to the embedding kNN nominator (task 3.1): both evaluator
    /// constructions — actor-style (<see cref="ILoggingAdapter"/>) and engine-style
    /// (<see cref="ILogger"/>) — must reach the SAME forced-LLM decision when a nominee fires,
    /// sharing one <see cref="MemoryEmbedderHolder"/> and <see cref="MemoryVectorIndexHolder"/>
    /// exactly as <see cref="EvaluateOnBothAsync"/> shares one <see cref="SQLiteMemoryStore"/>.
    /// </summary>
    [Fact]
    public async Task NomineePresent_returns_identical_forced_LLM_decision_on_both_paths()
    {
        var ct = TestContext.Current.CancellationToken;
        await _store.InitializeAsync(ct);

        const string existingBody = "The build pipeline stores intermediate render artifacts in a graphite-backed cache layer.";
        var anchor = _store.CreateDefaultAnchor("graphite-render-cache");
        await _store.UpsertDocumentAsync(new SQLiteMemoryDocument(
            DocumentId: "doc-existing",
            Anchor: anchor,
            MemoryClass: "durable_fact",
            Title: "Existing",
            MarkdownBody: existingBody,
            AliasesJson: null,
            FacetsJson: null,
            SlotsJson: null,
            UpdateSemantics: "merge-document",
            Sensitivity: "normal",
            RecallMode: "auto",
            Confidence: 0.9,
            FreshnessAtMs: 1000,
            ExpiresAtMs: null,
            CreatedAtMs: 1000,
            UpdatedAtMs: 1000), ct);
        await _store.UpsertEmbeddingAsync(
            "doc-existing", MemoryEmbedOnWriteCoordinator.DocumentItemKind, "test-nominator-model",
            MemoryContentHasher.ComputeHash("Existing", existingBody),
            new float[] { 1f, 0f }, ct);

        var operation = MakeOperation(
            "sunfish-deploy-queue", "Deployment jobs wait in a queue before promotion to production.", freshnessAtMs: 2000);

        var embedderHolder = new MemoryEmbedderHolder(
            new ScriptedEmbedder("test-nominator-model", dimensions: 2, [0.93f, 0.367623f]),
            initialQueryPrefix: "",
            initialCalibratedMinCosineSimilarity: null);
        var vectorIndexHolder = new MemoryVectorIndexHolder(_store);

        var actorLike = new MemoryCurationEvaluator(
            _store, (ILoggingAdapter)NoLogger.Instance, new MemoryCurationConfig(),
            new ScriptedCurationChatClient("SKIP"), embedderHolder, vectorIndexHolder);
        var engineLike = new MemoryCurationEvaluator(
            _store, (ILogger)NullLogger.Instance, new MemoryCurationConfig(),
            new ScriptedCurationChatClient("SKIP"), embedderHolder, vectorIndexHolder);

        var fromActor = (await actorLike.EvaluateAsync(operation, TestSessionId, ct)).Decision;
        var fromEngine = (await engineLike.EvaluateAsync(operation, TestSessionId, ct)).Decision;

        AssertSameDecision(fromActor, fromEngine);
        Assert.True(fromActor.FromLlmTier);
        Assert.Equal(CurationDecisionKind.Skip, fromActor.Kind);
    }

    // ── helpers ──────────────────────────────────────────────────────

    private async Task<(CurationDecision FromActor, CurationDecision FromEngine)> EvaluateOnBothAsync(
        SQLiteMemoryCurationOperation operation, CancellationToken ct)
    {
        // Constructed exactly as MemoryCurationActor and MemoryCurationEngine construct
        // their evaluators today: no LLM client, differing only in which logger stack
        // they log through.
        var actorLike = new MemoryCurationEvaluator(_store, (ILoggingAdapter)NoLogger.Instance, new MemoryCurationConfig());
        var engineLike = new MemoryCurationEvaluator(_store, (ILogger)NullLogger.Instance, new MemoryCurationConfig());

        var fromActor = (await actorLike.EvaluateAsync(operation, TestSessionId, ct)).Decision;
        var fromEngine = (await engineLike.EvaluateAsync(operation, TestSessionId, ct)).Decision;
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

    private async Task<(string Body, string UpdateSemantics)> ReadDocumentBodyAndSemanticsAsync(string documentId, CancellationToken ct)
    {
        await using var conn = new SqliteConnection($"Data Source={_dbPath}");
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT markdown_body, update_semantics FROM memory_documents WHERE document_id = $id";
        cmd.Parameters.AddWithValue("$id", documentId);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        Assert.True(await reader.ReadAsync(ct), $"Expected document row '{documentId}' to exist.");
        return (reader.GetString(0), reader.GetString(1));
    }

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

    /// <summary>
    /// Fake embedder that ignores its input text and always returns the same hand-crafted query
    /// vector — sufficient for <see cref="NomineePresent_returns_identical_forced_LLM_decision_on_both_paths"/>,
    /// which embeds at most one proposal per evaluator.
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
    /// Records every log line emitted through the Microsoft.Extensions.Logging ctor path, so the
    /// <c>curation_guard_fallthrough</c> marker can be asserted directly rather than only
    /// inferred from the resulting decision shape. Mirrors
    /// <c>MemoryCurationNominatorTests.RecordingLogger</c> (kept as a separate private copy per
    /// that file's own convention for test-only doubles).
    /// </summary>
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
