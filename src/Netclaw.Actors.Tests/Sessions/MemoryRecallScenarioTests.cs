// -----------------------------------------------------------------------
// <copyright file="MemoryRecallScenarioTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Netclaw.Actors.Memory;
using Netclaw.Actors.Protocol;
using Netclaw.Actors.Sessions;
using Netclaw.Configuration;
using Xunit;

namespace Netclaw.Actors.Tests.Sessions;

/// <summary>
/// Scenario suite for the memory recall composite-score floor (issue #582), extended in
/// memory-core-redesign Slice 4 (tasks 4.7/4.8) into a gold-set regression suite covering hybrid
/// recall: the P09 paraphrase-gap flip, MRR/precision floors, zero-injection cases, and
/// policy-parity under a healthy embedder.
///
/// Seeds a 16-document corpus mirroring the production DB shape that caused
/// the pollution bug (a cluster of ops/eval trivia plus two topical clusters
/// of legitimate content), then drives a table of 18 prompts through the
/// real <see cref="SQLiteMemoryRecallCoordinator"/> and asserts which
/// memories may or may not appear in the recall result for each prompt.
///
/// Fixture table is documented in memorizer: "Netclaw Memory Recall Floor —
/// Test Scenario Fixture (issue #582)".
///
/// Diagnostic rows are the ones where the floor is doing real work:
/// P11–P14 (lexical collisions against the noise band) and P16 (hard
/// negative against an ops-heavy corpus).
///
/// Document-vs-record priority is a separate concern handled by RecallRank
/// weights, not the composite floor, and is deliberately out of scope here.
/// The corpus contains only durable-fact documents.
///
/// <para>
/// <b>Hybrid wiring (task 4.8):</b> every scenario in <see cref="Scenarios"/> still runs the
/// pre-Slice-4 lexical-only coordinator (no embedder/vector-index holders) EXCEPT P09, which
/// wires <see cref="ScriptedEmbedder"/> + a real <see cref="MemoryVectorIndex"/> loaded from the
/// store's <c>memory_embeddings</c> table (only M16 is embedded — see <see cref="SeedCorpusAsync"/>).
/// This is deliberate, not incidental: <see cref="SQLiteMemoryRecallCoordinator.ScoreHybrid"/>'s
/// absolute cosine floor only ever gates a candidate the index actually holds a vector for — an
/// unembedded candidate (a coverage gap) bypasses the floor and competes on fused/lexical score
/// alone (gap-repair fix; see the class's own summary and design.md D6) — so wiring the embedder
/// across the whole table would change every lexical-only scenario's ranking geometry (coverage
/// gaps no longer defaulting to a rejected cosine of 0.0, but to an admitted one) instead of
/// isolating the paraphrase-gap fix P09 exists to prove. See the zero-injection facts below for a
/// direct demonstration of the coverage-gap-bypasses-the-floor behavior.
/// </para>
/// </summary>
public sealed class MemoryRecallScenarioTests : IAsyncLifetime
{
    private const string TestSessionId = "test/thread-1";

    // Hybrid fixture geometry (task 4.8): a 2D unit-vector space, same technique as
    // MemoryCurationNominatorTests/SQLiteMemoryRecallHybridTests. Only M16 ever gets an
    // embedding row (in SeedCorpusAsync) under this model id, so any coordinator wired with a
    // ScriptedEmbedder under HybridModelId only ever has M16 as a possible vector candidate.
    private const string HybridModelId = "recall-scenario-hybrid-test-model";
    private const int HybridDimensions = 2;

    // cosine(P09QueryVector, M16EmbeddingVector) == 0.85 -- comfortably above MinCosineSimilarity
    // (default 0.68), the semantic bridge across the paraphrase gap the lexical path can't cross.
    private static readonly float[] P09QueryVector = [1f, 0f];
    private static readonly float[] M16EmbeddingVector = [0.85f, 0.5267828f];

    // cosine(NonMatchingQueryVector, M16EmbeddingVector) == 0.5267828 -- comfortably below the
    // 0.68 floor. Used by the zero-injection facts to prove a healthy embedder with no
    // qualifying candidate yields a healthy empty result, never a degraded one.
    private static readonly float[] NonMatchingQueryVector = [0f, 1f];

    private readonly string _baseDir = Path.Combine(
        Path.GetTempPath(),
        "netclaw-recall-scenarios",
        Guid.NewGuid().ToString("N"));
    private readonly string _dbPath;
    private readonly SQLiteMemoryStore _store;

    public MemoryRecallScenarioTests()
    {
        Directory.CreateDirectory(_baseDir);
        _dbPath = Path.Combine(_baseDir, "netclaw-recall-scenarios.db");
        _store = new SQLiteMemoryStore(_dbPath, TimeProvider.System);
    }

    public async ValueTask InitializeAsync()
    {
        await _store.InitializeAsync(CancellationToken.None);
        await SeedCorpusAsync(CancellationToken.None);
    }

    public async ValueTask DisposeAsync()
    {
        await TryDeleteDirectoryAsync(_baseDir);
    }

    /// <summary>
    /// Scenario rows: (id, prompt, expectedIds, forbiddenIds).
    /// Don't-care IDs (see P15) are simply absent from both lists.
    /// </summary>
    public static IEnumerable<object[]> Scenarios()
    {
        // Easy positives
        yield return Row("P01",
            "How does backpressure work in Akka Streams?",
            expected: ["M07"],
            forbidden: NoiseBand);
        yield return Row("P02",
            "When do we ship new minor versions?",
            expected: ["M13"],
            forbidden: NoiseBand);
        yield return Row("P03",
            "How often should I snapshot a PersistentActor?",
            expected: ["M09"],
            forbidden: NoiseBand);
        yield return Row("P04",
            "What's our Sev2 response time for commercial support?",
            expected: ["M14"],
            forbidden: NoiseBand);

        // Subtle positives (paraphrase — no exact keyword in the doc title)
        yield return Row("P05",
            "I need to rebalance entities across nodes",
            expected: ["M08"],
            forbidden: NoiseBand);
        yield return Row("P06",
            "Best way to test an actor that sends messages",
            expected: ["M10"],
            forbidden: NoiseBand);
        yield return Row("P07",
            "How do I build a read model from an event log?",
            expected: ["M12"],
            forbidden: NoiseBand);

        // Easy positives (second batch)
        yield return Row("P08",
            "What transport does Akka.Remote use?",
            expected: ["M11"],
            forbidden: NoiseBand);
        // P09 is a PARAPHRASE-GAP scenario: the query shares almost no lexical
        // tokens with M16 ("versions ... cover" vs "runs net8.0 net9.0 ...
        // runners"), so under lexical recall M16 only ever surfaced via a
        // single weak token match — the exact signature of the measured
        // pollution vector (docs/research/memory-audit-2026-07.md). Flipped back to
        // expected-recall (memory-core-redesign Slice 4, task 4.8): the fixture wires a
        // ScriptedEmbedder + real vector index (see the class summary and BuildCoordinator)
        // whose query vector sits at cosine 0.85 to M16's seeded embedding, the semantic bridge
        // lexical recall alone can't cross.
        yield return Row("P09",
            "Which .NET versions does our CI cover?",
            expected: ["M16"],
            forbidden: NoiseBand,
            useHybridRecall: true);
        yield return Row("P10",
            "Are our NuGet packages signed?",
            expected: ["M15"],
            forbidden: NoiseBand);

        // Word collisions against the noise band — the critical diagnostic rows.
        yield return Row("P11",
            "I lost context in this conversation, can you recap?",
            expected: [],
            forbidden: ["M02"]);
        yield return Row("P12",
            "The shell in my bash is acting weird",
            expected: [],
            forbidden: ["M01", "M04"]);
        // P13 (deliberately omitted): "Can we get the SignalR integration
        // working again?" against M03 (slack channel allowlist for the
        // 'signalr' channel). This is a legitimate lexical collision — both
        // query and memory literally contain "signalr" — and distinguishing
        // the framework from the channel name requires semantic context the
        // deterministic scorer doesn't have. Out of scope for the floor.
        yield return Row("P14",
            "How's the system doing overall?",
            expected: [],
            forbidden: ["M06"]);

        // Multi-hit positive
        yield return Row("P15",
            "Tell me about Akka testing patterns",
            expected: ["M10"],
            forbidden: NoiseBand);

        // Hard negative — nothing should match an off-topic query against this DB.
        yield return Row("P16",
            "What's the deployment environment like for the agent?",
            expected: [],
            forbidden: NoiseBand);

        // Stress paths — empty and stopword-only queries.
        yield return Row("P19",
            "",
            expected: [],
            forbidden: NoiseBand);
        yield return Row("P20",
            "a the and or",
            expected: [],
            forbidden: NoiseBand);
        // Conversational stopwords (issue #693) — should produce empty recall.
        yield return Row("P21",
            "ok can that this yeah sure",
            expected: [],
            forbidden: NoiseBand);
    }

    [Theory]
    [MemberData(nameof(Scenarios))]
    public async Task Scenario_matches_expected_and_rejects_forbidden(
        string scenarioId,
        string prompt,
        string[] expectedIds,
        string[] forbiddenIds,
        bool useHybridRecall)
    {
        _ = scenarioId; // carried for failure diagnostics
        var coordinator = BuildCoordinator(useHybridRecall);

        var request = new AutomaticRecallRequest(
            SessionId: (SessionId)TestSessionId,
            Query: prompt,
            RecentUserMessages: string.IsNullOrEmpty(prompt) ? [] : [prompt],
            MaxItems: 3,
            Audience: TrustAudience.Public);

        var result = await coordinator.RecallAsync(request, TestContext.Current.CancellationToken);

        Assert.False(
            result.Degraded,
            $"[{scenarioId}] recall degraded: {result.DegradeStage}/{result.DegradeReason}");

        var returnedIds = result.Items.Select(i => i.Id.Value).ToArray();
        var returnedWithScores = string.Join(", ", result.Items.Select(i => $"{i.Id.Value}={i.Score:F3}"));

        foreach (var expected in expectedIds)
        {
            Assert.True(
                returnedIds.Contains(expected),
                $"[{scenarioId}] expected {expected} in result, got [{returnedWithScores}]");
        }

        foreach (var forbidden in forbiddenIds)
        {
            Assert.False(
                returnedIds.Contains(forbidden),
                $"[{scenarioId}] forbidden {forbidden} leaked into result, got [{returnedWithScores}]");
        }
    }

    // ── Gold-set MRR / precision floors (memory-core-redesign Slice 4, task 4.7) ───────────

    /// <summary>
    /// Computes MRR and precision@3 across every scenario in <see cref="Scenarios"/> that has at
    /// least one expected id (the standard IR definition needs a known-relevant item to rank) —
    /// P01-P10/P15 lexical, P09 hybrid. Zero-expected scenarios (P11/P12/P14/P16/P19-21) are
    /// covered by their own pass/fail assertions above and by the dedicated zero-injection facts
    /// below; folding them into precision@3 here would either reward vacuous "returned nothing"
    /// results or conflate two different failure modes into one number.
    ///
    /// <para>
    /// Floors are ~10% headroom below the values measured against this fixture corpus (as of
    /// this test's authoring): MRR 1.000 (every positive scenario's expected id ranks first) and
    /// precision@3 0.849 (P01/P15 each admit one extra non-forbidden, non-expected item alongside
    /// the expected one — see their diagnostics in a failure message — and P08 admits two; every
    /// other positive scenario returns exactly its expected id and nothing else). Tight enough to
    /// catch a real regression, loose enough not to flake on an incidental single-candidate rank
    /// change.
    /// </para>
    /// </summary>
    [Fact]
    public async Task Gold_set_MRR_and_precision_at_3_meet_the_calibrated_floor()
    {
        const double MrrFloor = 0.90;
        const double PrecisionAt3Floor = 0.75;

        var ct = TestContext.Current.CancellationToken;
        var reciprocalRanks = new List<double>();
        var precisions = new List<double>();
        var diagnostics = new List<string>();

        foreach (var row in Scenarios())
        {
            var scenarioId = (string)row[0];
            var prompt = (string)row[1];
            var expectedIds = (string[])row[2];
            var useHybridRecall = (bool)row[4];
            if (expectedIds.Length == 0)
                continue;

            var coordinator = BuildCoordinator(useHybridRecall);
            var result = await coordinator.RecallAsync(new AutomaticRecallRequest(
                SessionId: (SessionId)TestSessionId,
                Query: prompt,
                RecentUserMessages: [prompt],
                MaxItems: 3,
                Audience: TrustAudience.Public), ct);

            Assert.False(result.Degraded, $"[{scenarioId}] recall degraded: {result.DegradeStage}/{result.DegradeReason}");

            var items = result.Items;
            var rankIndex = items.Select(i => i.Id.Value).ToList().FindIndex(id => expectedIds.Contains(id));
            var reciprocalRank = rankIndex >= 0 ? 1.0 / (rankIndex + 1) : 0.0;
            reciprocalRanks.Add(reciprocalRank);

            var hitCount = items.Count(i => expectedIds.Contains(i.Id.Value));
            var precision = items.Count > 0 ? hitCount / (double)items.Count : 0.0;
            precisions.Add(precision);

            diagnostics.Add(
                $"{scenarioId}: rr={reciprocalRank:F3} precision={precision:F3} items=[{string.Join(",", items.Select(i => $"{i.Id.Value}={i.Score:F3}"))}]");
        }

        var mrr = reciprocalRanks.Average();
        var precisionAt3 = precisions.Average();
        var report = string.Join("\n", diagnostics);

        Assert.True(mrr >= MrrFloor, $"MRR {mrr:F3} fell below floor {MrrFloor:F3}\n{report}");
        Assert.True(precisionAt3 >= PrecisionAt3Floor, $"precision@3 {precisionAt3:F3} fell below floor {PrecisionAt3Floor:F3}\n{report}");
    }

    // ── Zero-injection / coverage-gap cases under a healthy embedder (memory-core-redesign
    //    Slice 4, task 4.7; gap-repair fix) ──
    //
    // The Scenarios() theory's zero-expected rows (P11/P12/P14/P16/P19-21) all run the
    // lexical-only coordinator. These facts instead exercise the hybrid path directly: the first
    // proves the zero-injection contract holds once a query vector exists and truly nothing
    // qualifies (no lexical candidates, no vector matches). The second proves the gap-repair fix
    // — a candidate with NO embedding row at all is a coverage gap, not a floor violation, so it
    // is recalled on its lexical/fused score exactly as it would be pre-Slice-4, even though a
    // healthy query vector exists this turn.

    [Fact]
    public async Task ZeroInjection_novel_query_with_no_qualifying_candidate_is_empty_and_not_degraded()
    {
        var ct = TestContext.Current.CancellationToken;
        var coordinator = BuildHybridCoordinator(NonMatchingQueryVector);

        var result = await coordinator.RecallAsync(new AutomaticRecallRequest(
            SessionId: (SessionId)TestSessionId,
            Query: "orchestra kayak zeppelin",
            RecentUserMessages: ["orchestra kayak zeppelin"],
            MaxItems: 3,
            Audience: TrustAudience.Public), ct);

        Assert.False(result.Degraded);
        Assert.Empty(result.Items);
    }

    [Fact]
    public async Task CoverageGap_strong_lexical_match_without_an_embedding_is_recalled_when_the_embedder_is_healthy()
    {
        var ct = TestContext.Current.CancellationToken;

        // Reuses P01's exact query text: under the lexical-only coordinator (see the P01 row
        // above) this clears the composite floor comfortably and recalls M07. M07 has no
        // embedding row at all under HybridModelId -- a coverage gap, not a candidate the index
        // scored and rejected -- so the gap-repair fix bypasses the absolute floor for it
        // entirely and it competes on fused score alone. NonMatchingQueryVector keeps M16 (the
        // corpus's only embedded doc under this model) below the floor throughout, so M07's
        // recall here is attributable ONLY to the coverage-gap bypass, not to any cosine signal.
        var coordinator = BuildHybridCoordinator(NonMatchingQueryVector);

        var result = await coordinator.RecallAsync(new AutomaticRecallRequest(
            SessionId: (SessionId)TestSessionId,
            Query: "How does backpressure work in Akka Streams?",
            RecentUserMessages: ["How does backpressure work in Akka Streams?"],
            MaxItems: 3,
            Audience: TrustAudience.Public), ct);

        Assert.False(result.Degraded);
        Assert.Contains(result.Items, i => i.Id.Value == "M07");
        Assert.DoesNotContain(result.Items, i => i.Id.Value == "M16");
    }

    // ── Policy parity under a healthy embedder (memory-core-redesign Slice 4, task 4.8) ────
    //
    // SQLiteMemoryStoreEmbeddingTests already proves GetRecallCandidatesByIdsAsync itself applies
    // every SearchByPlanAsync gate to a vector-sourced id. These two facts close the loop at the
    // full RecallAsync level: a document with a HIGH cosine match (well above the floor) must
    // still be withheld end-to-end when a policy gate says no — a strong vector signal can never
    // stand in for a policy violation.

    [Fact]
    public async Task PolicyParity_high_cosine_secret_document_is_withheld()
    {
        var ct = TestContext.Current.CancellationToken;
        const string modelId = "policy-parity-secret-test-model";
        float[] queryVector = [0f, 1f];
        float[] secretDocVector = [0.4359f, 0.9f]; // cosine ~0.9 to queryVector

        await SeedPolicyParityDocumentAsync(
            "M17-secret", "Confidential Executive Compensation Review",
            "Confidential executive compensation review figures withheld from automatic recall.",
            modelId, secretDocVector, sensitivity: "secret", ct: ct);

        var coordinator = BuildHybridCoordinator(modelId, queryVector);

        var result = await coordinator.RecallAsync(new AutomaticRecallRequest(
            SessionId: (SessionId)TestSessionId,
            Query: "confidential executive compensation review",
            RecentUserMessages: ["confidential executive compensation review"],
            MaxItems: 3,
            Audience: TrustAudience.Public), ct);

        Assert.False(result.Degraded);
        Assert.DoesNotContain(result.Items, i => i.Id.Value == "M17-secret");
    }

    [Fact]
    public async Task PolicyParity_high_cosine_wrong_audience_document_is_withheld()
    {
        var ct = TestContext.Current.CancellationToken;
        const string modelId = "policy-parity-audience-test-model";
        float[] queryVector = [0f, 1f];
        float[] teamDocVector = [0.4359f, 0.9f]; // cosine ~0.9 to queryVector

        await SeedPolicyParityDocumentAsync(
            "M18-team-only", "Team Roadmap Planning Notes",
            "Team roadmap planning notes scoped to the team audience only.",
            modelId, teamDocVector, audience: TrustAudience.Team.ToWireValue(), ct: ct);

        // Request audience is the default Public -- Public's allowed-audience set
        // (MemoryPolicyEvaluator.AllowedAudienceWireValues) is [Public] only, so a Team-scoped
        // document must never surface regardless of its cosine similarity.
        var coordinator = BuildHybridCoordinator(modelId, queryVector);

        var result = await coordinator.RecallAsync(new AutomaticRecallRequest(
            SessionId: (SessionId)TestSessionId,
            Query: "team roadmap planning notes",
            RecentUserMessages: ["team roadmap planning notes"],
            MaxItems: 3,
            Audience: TrustAudience.Public), ct);

        Assert.False(result.Degraded);
        Assert.DoesNotContain(result.Items, i => i.Id.Value == "M18-team-only");
    }

    private async Task SeedPolicyParityDocumentAsync(
        string documentId, string title, string body, string modelId, float[] vector,
        CancellationToken ct, string sensitivity = "normal", string audience = "public")
    {
        var anchor = _store.CreateDefaultAnchor(documentId);
        var now = TimeProvider.System.GetUtcNow().ToUnixTimeMilliseconds();
        await _store.UpsertDocumentAsync(new SQLiteMemoryDocument(
            DocumentId: documentId,
            Anchor: anchor,
            MemoryClass: "durable_fact",
            Title: title,
            MarkdownBody: body,
            AliasesJson: null,
            FacetsJson: null,
            SlotsJson: null,
            UpdateSemantics: "merge-document",
            Sensitivity: sensitivity,
            RecallMode: "auto",
            Confidence: 0.9,
            FreshnessAtMs: now,
            ExpiresAtMs: null,
            CreatedAtMs: now,
            UpdatedAtMs: now,
            Audience: audience), ct);
        await _store.UpsertEmbeddingAsync(
            documentId,
            MemoryEmbedOnWriteCoordinator.DocumentItemKind,
            modelId,
            MemoryContentHasher.ComputeHash(title, body),
            vector,
            ct);
    }

    /// <summary>Hybrid coordinator over HybridModelId/HybridDimensions (only M16 is embedded there).</summary>
    private SQLiteMemoryRecallCoordinator BuildHybridCoordinator(float[] queryVector)
        => BuildHybridCoordinator(HybridModelId, queryVector, HybridDimensions);

    private SQLiteMemoryRecallCoordinator BuildHybridCoordinator(string modelId, float[] queryVector, int dimensions = 2)
        => new(
            _store,
            NullLogger<SQLiteMemoryRecallCoordinator>.Instance,
            new MemoryConfig(),
            TimeProvider.System,
            sessionTuning: new SessionTuning(),
            // memory-query-prefix design D3: Memory.Recall.MinCosineSimilarity now defaults to
            // null (manifest-follows), so this fixture's own P09 floor (0.68 — see the class
            // summary's cosine geometry comment) is supplied directly as the holder's
            // manifest-carried calibration rather than a config value.
            embedderHolder: new MemoryEmbedderHolder(
                new ScriptedEmbedder(modelId, dimensions, queryVector), initialQueryPrefix: "", initialCalibratedMinCosineSimilarity: 0.68),
            vectorIndexHolder: new MemoryVectorIndexHolder(_store));

    private static object[] Row(string id, string prompt, string[] expected, string[] forbidden, bool useHybridRecall = false)
        => [id, prompt, expected, forbidden, useHybridRecall];

    /// <summary>
    /// Builds the coordinator a scenario runs against (task 4.8). Every scenario except P09 gets
    /// the pre-Slice-4 lexical-only coordinator (no holders wired) — this is the path
    /// <see cref="SQLiteMemoryRecallCoordinator"/> already pins as behaviorally identical to
    /// "no embedder configured" (see <c>SQLiteMemoryRecallHybridTests</c>'s degraded-parity
    /// test), so it is not a lesser or stale code path, just the one every scenario here other
    /// than P09 is designed to exercise. See the class summary for why the whole table can't
    /// share a single hybrid-wired coordinator.
    /// </summary>
    private SQLiteMemoryRecallCoordinator BuildCoordinator(bool useHybridRecall)
        => useHybridRecall
            ? BuildHybridCoordinator(P09QueryVector)
            : new SQLiteMemoryRecallCoordinator(
                _store,
                NullLogger<SQLiteMemoryRecallCoordinator>.Instance,
                new MemoryConfig(),
                TimeProvider.System,
                sessionTuning: new SessionTuning());

    // The noise band — ops/eval trivia mirroring the polluting docs from #582.
    // Most scenarios assert that none of these leak into the recall result.
    private static readonly string[] NoiseBand =
    [
        "M01", "M02", "M03", "M04", "M05", "M06"
    ];

    private async Task SeedCorpusAsync(CancellationToken ct)
    {
        var now = TimeProvider.System.GetUtcNow().ToUnixTimeMilliseconds();

        // Each memory gets its own anchor so anchor-dedup doesn't collapse them.
        // Documents go through UpsertDocumentAsync; records go through the batch
        // API since UpsertDocumentAsync is document-only.

        // --- Noise band (ops / eval trivia) ---
        await UpsertDoc("M01", "Full Host Shell Access Permission",
            "Personal profile allows full host shell access, which carries a high blast radius risk.",
            facets: "[\"operational\",\"shell\"]", now: now, ct: ct);
        await UpsertDoc("M02", "Default Context Window Configuration",
            "No explicit context window is configured. The system uses the default 32K token context window.",
            facets: "[\"operational\",\"context-window\"]", now: now, ct: ct);
        await UpsertDoc("M03", "Slack Channel Access Restrictions",
            "The signalr channel is not in the allowed channels list for posting messages.",
            facets: "[\"operational\",\"slack\"]", now: now, ct: ct);
        await UpsertDoc("M04", "Shell Execution Environment Restriction",
            "Shell execution is denied in the current environment. Use web search alternatives.",
            facets: "[\"operational\",\"shell\"]", now: now, ct: ct);
        await UpsertDoc("M05", "Development Environment Configuration",
            "Development environment uses unrestricted filesystem access with MCP tools not scoped.",
            facets: "[\"operational\",\"devenv\"]", now: now, ct: ct);
        await UpsertDoc("M06", "System Diagnostic Health",
            "netclaw doctor shows the system is technically healthy with recommendations for tuning.",
            facets: "[\"operational\",\"diagnostics\"]", now: now, ct: ct);

        // --- Akka technical cluster ---
        await UpsertDoc("M07", "Akka Stream Backpressure Semantics",
            "Akka stream uses demand-based backpressure. Consumers signal how many elements they can accept. Async boundaries use bounded buffers.",
            facets: "[\"akka\",\"streams\"]", now: now, ct: ct);
        await UpsertDoc("M08", "Cluster Sharding Entity Placement",
            "Entity actors are distributed across cluster shards via a shard extractor. Shards rebalance across nodes on member changes.",
            facets: "[\"akka\",\"cluster\",\"sharding\"]", now: now, ct: ct);
        await UpsertDoc("M09", "Akka Persistence Snapshot Strategy",
            "PersistentActor snapshots reduce recovery time. Typical cadence is every 1000 events.",
            facets: "[\"akka\",\"persistence\"]", now: now, ct: ct);
        await UpsertDoc("M10", "Akka TestKit Probe Patterns",
            "Use TestProbe to assert on actor messages. ExpectMsg with timeouts. Avoid Thread.Sleep in tests.",
            facets: "[\"akka\",\"testing\"]", now: now, ct: ct);
        await UpsertDoc("M11", "Akka Remoting Transport",
            "Akka.Remote uses the DotNetty TCP transport. Artery has not yet been ported to .NET.",
            facets: "[\"akka\",\"remoting\"]", now: now, ct: ct);
        await UpsertDoc("M12", "EventSourced Projection Pattern",
            "Read-side projections consume PersistenceQuery journal streams to build queryable read models from the event log.",
            facets: "[\"akka\",\"persistence\",\"cqrs\"]", now: now, ct: ct);

        // --- Release / ops cluster ---
        await UpsertDoc("M13", "Release Cadence Policy",
            "We ship minor versions roughly quarterly. Patches go out as needed.",
            facets: "[\"release\",\"ops\"]", now: now, ct: ct);
        await UpsertDoc("M14", "Commercial Support SLA",
            "Commercial support responds within 1 business day for Sev2 issues.",
            facets: "[\"support\",\"commercial\"]", now: now, ct: ct);
        await UpsertDoc("M15", "NuGet Package Signing",
            "All published NuGet packages are Authenticode-signed before push.",
            facets: "[\"release\",\"packaging\"]", now: now, ct: ct);
        await UpsertDoc("M16", "CI Build Matrix",
            "CI runs net8.0 and net9.0 on Linux and Windows runners for every pull request.",
            facets: "[\"ci\",\"build\"]", now: now, ct: ct);

        // The ONLY embedded document in this corpus (task 4.8) -- every other scenario runs a
        // coordinator without embedder/vector-index holders wired, so this row is inert for them
        // (TryEmbedQueryAsync returns null before ever touching the store's embedding table).
        await _store.UpsertEmbeddingAsync(
            "M16",
            MemoryEmbedOnWriteCoordinator.DocumentItemKind,
            HybridModelId,
            MemoryContentHasher.ComputeHash(
                "CI Build Matrix",
                "CI runs net8.0 and net9.0 on Linux and Windows runners for every pull request."),
            M16EmbeddingVector,
            ct);
    }

    private async Task UpsertDoc(
        string id,
        string title,
        string body,
        string facets,
        long now,
        CancellationToken ct)
    {
        var anchor = _store.CreateDefaultAnchor(id);
        await _store.UpsertDocumentAsync(new SQLiteMemoryDocument(
            DocumentId: id,
            Anchor: anchor,
            MemoryClass: "durable_fact",
            Title: title,
            MarkdownBody: body,
            AliasesJson: null,
            FacetsJson: facets,
            SlotsJson: null,
            UpdateSemantics: "merge-document",
            Sensitivity: "normal",
            RecallMode: "auto",
            Confidence: 0.9,
            FreshnessAtMs: now,
            ExpiresAtMs: null,
            CreatedAtMs: now,
            UpdatedAtMs: now), ct);
    }

    private static async Task TryDeleteDirectoryAsync(string path)
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
                await Task.Delay(25 * (i + 1));
            }
            catch (UnauthorizedAccessException) when (i < 7)
            {
                await Task.Delay(25 * (i + 1));
            }
        }
    }

    /// <summary>
    /// Fake embedder that ignores its input text and always returns the same, hand-crafted query
    /// vector — sufficient here because every coordinator built against one instance embeds at
    /// most one distinct query per test, and the geometry (not the input text) is what needs to
    /// be controlled. Mirrors <c>SQLiteMemoryRecallHybridTests.ScriptedEmbedder</c> (kept as a
    /// separate private copy per that file's own convention).
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
}
