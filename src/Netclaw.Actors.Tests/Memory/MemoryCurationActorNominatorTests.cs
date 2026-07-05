// -----------------------------------------------------------------------
// <copyright file="MemoryCurationActorNominatorTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Runtime.CompilerServices;
using Akka.Hosting;
using Akka.Hosting.TestKit;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.AI;
using Netclaw.Actors.Memory;
using Netclaw.Actors.Protocol;
using Netclaw.Configuration;
using Xunit;
using AiChatMessage = Microsoft.Extensions.AI.ChatMessage;
using AiChatRole = Microsoft.Extensions.AI.ChatRole;

namespace Netclaw.Actors.Tests.Memory;

/// <summary>
/// Actor-level end-to-end coverage for the embedding kNN nominator (memory-core-redesign
/// Slice 3 Stage B, task 3.6): drives a proposal through the REAL <see cref="MemoryCurationActor"/>
/// with a fake embedder + scripted LLM, all the way to a committed store write. Complements
/// <see cref="MemoryCurationNominatorTests"/>'s evaluator-level coverage by proving the same
/// contract holds through the actor's full Idle -> Evaluating -> Writing state machine, using
/// <c>AwaitAssertAsync</c> to poll for the LLM call rather than a sleep. Mirrors
/// <see cref="Netclaw.Actors.Tests.Sessions.SessionMemoryObserverStorageIntegrationTests"/>'s
/// temp-store/try-finally-cleanup shape for driving <see cref="MemoryCurationActor"/> directly.
/// </summary>
public sealed class MemoryCurationActorNominatorTests : TestKit
{
    private const string ModelId = "test-nominator-model";
    private const int Dimensions = 2;

    // Same hand-crafted 0.93-cosine pair as MemoryCurationNominatorTests.
    private static readonly float[] ExistingVector = [1f, 0f];
    private static readonly float[] QueryVectorAt093 = [0.93f, 0.367623f];

    private readonly string _dbDir = Path.Combine(
        Path.GetTempPath(), "netclaw-curation-actor-nominator-tests", Guid.NewGuid().ToString("N"));

    public MemoryCurationActorNominatorTests(ITestOutputHelper output) : base(output: output)
    {
    }

    protected override void ConfigureAkka(AkkaConfigurationBuilder builder, IServiceProvider provider)
    {
        // No persistence or hosting needed — MemoryCurationActor is a plain ReceiveActor.
    }

    [Fact]
    public async Task Proposal_with_a_forced_nominee_reaches_the_LLM_and_commits_two_documents_end_to_end()
    {
        var ct = TestContext.Current.CancellationToken;
        var (store, dbPath) = await CreateStoreAsync();

        try
        {
            const string existingBody = "The build pipeline stores intermediate render artifacts in a graphite-backed cache layer.";
            var anchor = store.CreateDefaultAnchor("graphite-render-cache");
            var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            await store.UpsertDocumentAsync(new SQLiteMemoryDocument(
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
                FreshnessAtMs: now,
                ExpiresAtMs: null,
                CreatedAtMs: now,
                UpdatedAtMs: now), ct);
            await store.UpsertEmbeddingAsync(
                "doc-existing", MemoryEmbedOnWriteCoordinator.DocumentItemKind, ModelId, "hash-existing", ExistingVector, ct);

            var embedderHolder = new MemoryEmbedderHolder(new ScriptedEmbedder(ModelId, Dimensions, QueryVectorAt093));
            var vectorIndexHolder = new MemoryVectorIndexHolder(store);
            var chatClient = new RecordingCurationChatClient("CREATE");
            var clientProvider = new SingleClientProvider(chatClient);

            var curationActor = Sys.ActorOf(
                MemoryCurationActor.CreateProps(
                    store, new SessionId("test-session"), new MemoryCurationConfig(),
                    clientProvider, embedderHolder, vectorIndexHolder),
                "curation-nominator");

            var probe = CreateTestProbe("curation-nominator-probe");
            var operation = MakeOperation(
                "sunfish-deploy-queue", "Deployment jobs wait in a queue before promotion to production.");

            curationActor.Tell(new EvaluateProposals([operation]), probe.Ref);

            // No sleeps: poll until the scripted LLM has actually been reached, proving the
            // nominee forced the LLM tier, before asserting the final reply/store state.
            await AwaitAssertAsync(
                () => Assert.True(chatClient.CallCount >= 1,
                    $"Expected the nominee to force an LLM call, but CallCount={chatClient.CallCount}"),
                cancellationToken: ct);

            var completed = await probe.ExpectMsgAsync<CurationCompleted>(TimeSpan.FromSeconds(10), cancellationToken: ct);
            Assert.Equal(1, completed.Evaluated);
            Assert.Equal(1, completed.Created);
            Assert.Equal(0, completed.Skipped);
            Assert.Equal(0, completed.Updated);
            Assert.Equal(0, completed.Consolidated);

            // By the time CurationCompleted is sent, ApplyInlineCurationBatchAsync has already
            // committed (StartWriting awaits it before replying) — both the pre-existing
            // sibling and the newly created proposal survive as separate documents, never merged.
            await using var conn = new SqliteConnection($"Data Source={dbPath}");
            await conn.OpenAsync(ct);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*) FROM memory_documents WHERE update_semantics != 'tombstone';";
            var count = Convert.ToInt32(await cmd.ExecuteScalarAsync(ct));
            Assert.Equal(2, count);
        }
        finally
        {
            await CleanupAsync();
        }
    }

    // ── Helpers ──────────────────────────────────────────────────────────

    private async Task<(SQLiteMemoryStore Store, string DbPath)> CreateStoreAsync()
    {
        Directory.CreateDirectory(_dbDir);
        var dbPath = Path.Combine(_dbDir, "test.db");
        var store = new SQLiteMemoryStore(dbPath, TimeProvider.System);
        await store.InitializeAsync(TestContext.Current.CancellationToken);
        return (store, dbPath);
    }

    private Task CleanupAsync() => SqliteTempDirectoryCleanup.TryDeleteDirectoryAsync(_dbDir);

    private static SQLiteMemoryCurationOperation MakeOperation(string anchor, string content) =>
        new(
            Kind: "document",
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
            UpdateSemantics: "merge-document",
            Boundary: TrustBoundary.TrustedInstanceValue,
            Audience: TrustAudience.Public,
            Sensitivity: "normal",
            RecallMode: "auto",
            Confidence: 0.9,
            FreshnessAtMs: DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            ExpiresAtMs: null);

    private sealed class SingleClientProvider(IChatClient client) : IChatClientProvider
    {
        public IChatClient GetClient(ModelRole role) => client;
    }

    private sealed class ScriptedEmbedder(string modelId, int dimensions, float[] queryVector) : IMemoryEmbedder
    {
        public string ModelId => modelId;

        public int Dimensions => dimensions;

        public bool IsAvailable => true;

        public ValueTask<ReadOnlyMemory<float>> EmbedAsync(string text, CancellationToken ct)
            => ValueTask.FromResult<ReadOnlyMemory<float>>(queryVector);

        public ValueTask<IReadOnlyList<ReadOnlyMemory<float>>> EmbedBatchAsync(IReadOnlyList<string> texts, CancellationToken ct)
            => ValueTask.FromResult<IReadOnlyList<ReadOnlyMemory<float>>>(
                texts.Select(_ => (ReadOnlyMemory<float>)queryVector).ToList());
    }

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
}
