// -----------------------------------------------------------------------
// <copyright file="SessionMemoryObserverStorageIntegrationTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Akka.Actor;
using Akka.Hosting;
using Akka.Hosting.TestKit;
using Netclaw.Actors.Memory;
using Netclaw.Actors.Tests.Memory;
using Netclaw.Configuration;
using Xunit;

namespace Netclaw.Actors.Tests.Sessions;

/// <summary>
/// End-to-end integration test for the memory formation chain: a memory
/// proposal sent to <see cref="MemoryCurationActor"/> must land as a row in
/// <see cref="SQLiteMemoryStore"/>'s <c>memory_documents</c> table.
///
/// <para>This test exists because the existing <see cref="SessionMemoryObserverActorTests"/>
/// suite stops at the actor message boundary (TestKit probe receives
/// <c>SessionDistillationCompleted</c> with proposals) and never verifies that
/// proposals actually become durable rows. That gap let regressions silently
/// kill memory formation for two days in early April 2026 (the legacy
/// <c>domain NOT NULL</c> constraint, fixed in PR #634) and again in April
/// 2026 (parser brittleness, fixed in this PR series). A test in this file
/// would have caught both.</para>
///
/// <para>The chain under test: proposal → MemoryCurationActor → SQLiteMemoryStore
/// → memory_documents. The observer side and the gate side are tested in
/// SessionMemoryObserverActorTests (parser unit tests). Routing from
/// LlmSessionActor.HandleDistillationResult is the next layer up and out of
/// scope for this initial integration test.</para>
/// </summary>
public sealed class SessionMemoryObserverStorageIntegrationTests : TestKit
{
    private readonly string _dbDir = Path.Combine(
        Path.GetTempPath(),
        "netclaw-observer-storage-tests",
        Guid.NewGuid().ToString("N"));

    public SessionMemoryObserverStorageIntegrationTests(ITestOutputHelper output) : base(output: output)
    {
    }

    protected override void ConfigureAkka(AkkaConfigurationBuilder builder, IServiceProvider provider)
    {
        // No persistence needed for these tests — the curation actor is a
        // ReceiveActor, not a persistent actor.
    }

    [Fact]
    public async Task Curation_actor_persists_create_decision_to_memory_documents()
    {
        // Setup: real SQLite store against a temp DB
        var (store, dbPath) = await CreateStoreAsync();

        try
        {
            var curationActor = Sys.ActorOf(
                MemoryCurationActor.CreateProps(store),
                "curation-create");

            var probe = CreateTestProbe("curation-create-probe");
            var operation = MakeOperation(
                anchorCanonicalName: "synthetic-create-anchor",
                title: "Synthetic Create Test",
                content: "Content that should land in memory_documents via Create decision.",
                aliasesJson: "[\"synthetic create\",\"create alias\"]",
                facetsJson: "[\"integration_test\"]");

            curationActor.Tell(new EvaluateProposals([operation]), probe.Ref);

            var completion = await probe.ExpectMsgAsync<CurationCompleted>(
                TimeSpan.FromSeconds(10),
                cancellationToken: TestContext.Current.CancellationToken);

            Assert.True(completion.Created >= 1,
                $"Expected at least 1 create, got Created={completion.Created} Updated={completion.Updated} Skipped={completion.Skipped}");
            Assert.Equal(0, completion.Skipped);

            // Verify the row exists in the store via search
            var results = await store.SearchMemoriesAsync(
                "synthetic create",
                limit: 5,
                boundary: SecurityPolicyDefaults.TrustedInstanceBoundary,
                audience: TrustAudience.Public,
                TestContext.Current.CancellationToken);

            Assert.NotEmpty(results);
            Assert.Contains(results, r => r.Title.Contains("Synthetic Create Test", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            await CleanupAsync();
        }
    }

    [Fact]
    public async Task Curation_actor_persists_multiple_proposals_in_one_batch()
    {
        var (store, dbPath) = await CreateStoreAsync();

        try
        {
            var curationActor = Sys.ActorOf(
                MemoryCurationActor.CreateProps(store),
                "curation-batch");

            var probe = CreateTestProbe("curation-batch-probe");
            var operations = new[]
            {
                MakeOperation(
                    anchorCanonicalName: "synthetic-batch-one",
                    title: "Batch One",
                    content: "First proposal in a batch.",
                    aliasesJson: "[\"batch one\"]",
                    facetsJson: "[\"integration_test\"]"),
                MakeOperation(
                    anchorCanonicalName: "synthetic-batch-two",
                    title: "Batch Two",
                    content: "Second proposal in a batch.",
                    aliasesJson: "[\"batch two\"]",
                    facetsJson: "[\"integration_test\"]"),
            };

            curationActor.Tell(new EvaluateProposals(operations), probe.Ref);

            var completion = await probe.ExpectMsgAsync<CurationCompleted>(
                TimeSpan.FromSeconds(10),
                cancellationToken: TestContext.Current.CancellationToken);

            Assert.Equal(2, completion.Evaluated);
            Assert.True(completion.Created >= 2,
                $"Expected at least 2 creates, got Created={completion.Created} Updated={completion.Updated}");

            var results = await store.SearchMemoriesAsync(
                "Batch",
                limit: 5,
                boundary: SecurityPolicyDefaults.TrustedInstanceBoundary,
                audience: TrustAudience.Public,
                TestContext.Current.CancellationToken);

            Assert.True(results.Count >= 2,
                $"Expected at least 2 search hits, got {results.Count}");
        }
        finally
        {
            await CleanupAsync();
        }
    }

    [Fact]
    public async Task Curation_actor_replies_with_zero_evaluated_for_empty_batch()
    {
        // Edge case: an empty proposal list shouldn't crash; it should reply
        // with a zero-count CurationCompleted. This is the contract LlmSessionActor
        // relies on at line 945-949 when accepted.Count > 0 is checked.
        var (store, _) = await CreateStoreAsync();

        try
        {
            var curationActor = Sys.ActorOf(
                MemoryCurationActor.CreateProps(store),
                "curation-empty");

            var probe = CreateTestProbe("curation-empty-probe");
            curationActor.Tell(new EvaluateProposals([]), probe.Ref);

            var completion = await probe.ExpectMsgAsync<CurationCompleted>(
                TimeSpan.FromSeconds(5),
                cancellationToken: TestContext.Current.CancellationToken);

            Assert.Equal(0, completion.Evaluated);
            Assert.Equal(0, completion.Created);
            Assert.Equal(0, completion.Updated);
        }
        finally
        {
            await CleanupAsync();
        }
    }

    // ── Helpers ────────────────────────────────────────────────────────

    private async Task<(SQLiteMemoryStore Store, string DbPath)> CreateStoreAsync()
    {
        Directory.CreateDirectory(_dbDir);
        var dbPath = Path.Combine(_dbDir, "test.db");
        var store = new SQLiteMemoryStore(dbPath, TimeProvider.System);
        await store.InitializeAsync(TestContext.Current.CancellationToken);
        return (store, dbPath);
    }

    private static SQLiteMemoryCurationOperation MakeOperation(
        string anchorCanonicalName,
        string title,
        string content,
        string aliasesJson,
        string facetsJson) =>
        new(
            Kind: "document",
            MemoryClass: "durable_fact",
            MemoryId: null,
            AnchorCanonicalName: anchorCanonicalName,
            AnchorType: "preference",
            Title: title,
            Content: content,
            AliasesJson: aliasesJson,
            FacetsJson: facetsJson,
            SlotsJson: null,
            Relations: null,
            UpdateSemantics: "merge-document",
            Boundary: SecurityPolicyDefaults.TrustedInstanceBoundary,
            Audience: TrustAudience.Public,
            Sensitivity: "normal",
            RecallMode: "auto",
            Confidence: 0.9,
            FreshnessAtMs: TimeProvider.System.GetUtcNow().ToUnixTimeMilliseconds(),
            ExpiresAtMs: null);

    private Task CleanupAsync() => SqliteTempDirectoryCleanup.TryDeleteDirectoryAsync(_dbDir);
}
