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
/// Scenario suite for the memory recall composite-score floor (issue #582).
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
/// </summary>
public sealed class MemoryRecallScenarioTests : IAsyncLifetime
{
    private const string TestSessionId = "test/thread-1";

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
        // pollution vector (docs/research/memory-audit-2026-07.md). With the calibrated floor the
        // correct deterministic behavior is to inject NOTHING here rather
        // than admit single-token matches corpus-wide. Semantic (embedding)
        // recall is what serves this query; when hybrid recall lands, flip
        // this back to expected: ["M16"].
        yield return Row("P09",
            "Which .NET versions does our CI cover?",
            expected: [],
            forbidden: NoiseBand);
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
        string[] forbiddenIds)
    {
        _ = scenarioId; // carried for failure diagnostics
        var coordinator = new SQLiteMemoryRecallCoordinator(
            _store,
            NullLogger<SQLiteMemoryRecallCoordinator>.Instance,
            sessionTuning: new SessionTuning());

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

    private static object[] Row(string id, string prompt, string[] expected, string[] forbidden)
        => [id, prompt, expected, forbidden];


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
}
