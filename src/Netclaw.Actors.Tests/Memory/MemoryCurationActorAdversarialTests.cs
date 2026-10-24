// -----------------------------------------------------------------------
// <copyright file="MemoryCurationActorAdversarialTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Netclaw.Actors.Memory;
using Netclaw.Actors.Protocol;
using Netclaw.Configuration;
using Netclaw.Daemon.Services;
using Xunit;
using static Netclaw.Actors.Tests.Memory.CurationAdversarialRulesTests;

namespace Netclaw.Actors.Tests.Memory;

public sealed partial class MemoryCurationActorNominatorTests : IAsyncDisposable
{
    // TestKit stops its actors before this fixture deletes its SQLite directory.
    async ValueTask IAsyncDisposable.DisposeAsync()
    {
        await base.DisposeAsync();
        await CleanupAsync();
    }

    [Theory]
    [InlineData("invalid-target", "UPDATE doc-synthetic-absent")]
    [InlineData("invalid-target", "UPDATE doc-synthetic-outside")]
    [InlineData("invalid-target", "CONSOLIDATE doc-synthetic-absent")]
    [InlineData("invalid-target", "CONSOLIDATE doc-synthetic-outside")]
    [InlineData("invalid-target", "CONSOLIDATE doc-synthetic-widget doc-synthetic-outside")]
    [InlineData("invalid-target", "CONSOLIDATE doc-synthetic-outside doc-synthetic-widget")]
    [InlineData("invalid-target", "CREATE_EXTRA")]
    [InlineData("invalid-target", "SKIPPER")]
    [InlineData("valid-append", "UPDATE doc-synthetic-widget\n---\nIncomplete merge.")]
    [InlineData("overlap-bypass", "SKIP")]
    [InlineData("actor-batch", "INVALID")]
    [InlineData("engine-batch", "INVALID")]
    [InlineData("engine", "")]
    [InlineData("worker", "")]
    [InlineData("nominee-same-unavailable", "")]
    [InlineData("nominee-normalized-invalid", "INVALID")]
    [InlineData("nominee-different-unavailable", "")]
    [InlineData("nominee-different-invalid", "INVALID")]
    public async Task Adversarial_curation_preserves_state_and_recovers(string flow, string response)
    {
        var ct = TestContext.Current.CancellationToken;
        var (store, dbPath) = await CreateStoreAsync();
        await SeedAdversarialDocumentAsync(store, DocumentId, CanonicalName, OriginalBody, 0.9, ct);
        var proposal = RewriteProposal(CanonicalName);
        var chat = new RecordingCurationChatClient(response);
        var evaluator = new MemoryCurationEvaluator(store, NullLogger.Instance, new MemoryCurationConfig(), chat);

        if (flow is "actor-batch" or "engine-batch")
        {
            // Distinct content terms prevent the lexical query from admitting the widget document.
            const string fastenerBody = "Brass fasteners secure panels. Torque charts stay beside benches.";
            await SeedAdversarialDocumentAsync(store, "doc-synthetic-fastener-a", "synthetic-fastener", fastenerBody, 0.9, ct);
            await SeedAdversarialDocumentAsync(store, "doc-synthetic-fastener-b", "synthetic-fastener-guide", fastenerBody, 0.9, ct);
            var first = RewriteProposal("synthetic-fastener-guides") with { Content = fastenerBody };
            var firstEvaluation = await evaluator.EvaluateAsync(first, new SessionId("synthetic-session"), ct);
            Assert.Equal(CurationDecisionKind.Consolidate, firstEvaluation.Decision.Kind);
            string[] fastenerTargets = ["doc-synthetic-fastener-a", "doc-synthetic-fastener-b"];
            Assert.Equal(fastenerTargets,
                firstEvaluation.Candidates.Select(candidate => candidate.DocumentId).Order(StringComparer.Ordinal));
            Assert.Equal(fastenerTargets,
                firstEvaluation.Decision.ConsolidationTargetIds!.Order(StringComparer.Ordinal));
            Assert.Contains(firstEvaluation.Decision.TargetDocumentId, fastenerTargets);
            var secondEvaluation = await evaluator.EvaluateAsync(proposal, new SessionId("synthetic-session"), ct);
            Assert.Equal(CurationDecisionKind.Ambiguous, secondEvaluation.Decision.Kind);
            var before = await ReadAdversarialStateAsync(dbPath, ct);

            var actor = Sys.ActorOf(MemoryCurationActor.CreateProps(store, new SessionId("synthetic-session"),
                new MemoryCurationConfig(), new SingleClientProvider(chat)));
            var probe = CreateTestProbe();
            if (flow == "engine-batch")
            {
                var engine = new MemoryCurationEngine(store,
                    new MemoryRulesFirstExtractor(new MemoryPolicyEvaluator()), new MemoryConfig());
                var checkpoint = new SQLiteMemoryCheckpoint("cp-synthetic-batch", "synthetic-session", null,
                    CheckpointTriggerType.ExplicitMemoryRequest.ToWireValue(), 100, "pending", "{}", 0, 1, 1);
                await Assert.ThrowsAsync<InvalidOperationException>(() =>
                    engine.EvaluateAndApplyAsync([first, proposal], checkpoint, ct));
            }
            else
            {
                actor.Tell(new EvaluateProposals([first, proposal]), probe.Ref);
                var failure = await probe.ExpectMsgAsync<CurationFailed>(TimeSpan.FromSeconds(10), cancellationToken: ct);
                Assert.False(string.IsNullOrWhiteSpace(failure.Reason));
            }
            Assert.Equal(before, await ReadAdversarialStateAsync(dbPath, ct));
            if (flow == "engine-batch")
                return;

            var safe = proposal with { Content = OriginalBody + " Retain the brass token. Place the spare fuse in an orange pouch." };
            Assert.Equal(CurationDecisionKind.Update,
                (await evaluator.EvaluateAsync(safe, new SessionId("synthetic-session"), ct)).Decision.Kind);
            actor.Tell(new EvaluateProposals([safe]), probe.Ref);
            var completed = await probe.ExpectMsgAsync<CurationCompleted>(TimeSpan.FromSeconds(10), cancellationToken: ct);
            Assert.Equal(new CurationCompleted(1, 0, 1, 0, 0), completed);
            var expected = before.Select(row => row.Kind == "document" && row.Id == DocumentId
                ? row with { Value = safe.Content } : row).ToArray();
            Assert.Equal(expected, await ReadAdversarialStateAsync(dbPath, ct));
            return;
        }

        if (flow == "overlap-bypass")
            await SeedAdversarialDocumentAsync(store, "doc-synthetic-sibling", "synthetic-widget-manual-copy", ProposalBody, 0.99, ct);
        var initialState = await ReadAdversarialStateAsync(dbPath, ct);
        if (flow is "engine" or "worker")
        {
            var extractor = new MemoryRulesFirstExtractor(new MemoryPolicyEvaluator());
            var payload = new MemoryCheckpointPayload(
                SessionId: "synthetic-session", TriggerType: CheckpointTriggerType.ExplicitMemoryRequest.ToWireValue(),
                Source: "store_memory", Content: ProposalBody, UserContent: "Remember the synthetic widget facts.",
                AssistantContent: null, IsExplicitRequest: true, HasVerifiedToolFinding: false,
                IsCompactionBoundary: false, HasAcceptedSubAgentFinding: false, Sensitivity: "normal",
                RecallMode: "auto", Confidence: 0.9, Kind: "document", Title: "Synthetic widget manual",
                FreshnessAtMs: 2, Boundary: TrustBoundary.TrustedInstanceValue, Audience: "public");
            var extracted = Assert.Single(extractor.Extract(payload, new HashSet<string>()));
            Assert.Equal(CanonicalName, extracted.AnchorCanonicalName);
            var checkpoint = new SQLiteMemoryCheckpoint("cp-synthetic-rewrite", payload.SessionId, null,
                payload.TriggerType, 100, "pending", JsonSerializer.Serialize(payload), 0, 1, 1);
            var engine = new MemoryCurationEngine(store, extractor, new MemoryConfig());
            if (flow == "engine")
            {
                await Assert.ThrowsAsync<InvalidOperationException>(() => engine.CurateAsync(checkpoint, ct));
            }
            else
            {
                await store.EnqueueCheckpointAsync(checkpoint, ct);
                var logger = new AdversarialWorkerLogger();
                using var holder = new MemoryEmbedderHolder(new UnavailableMemoryEmbedder(ModelId, "Synthetic offline fixture"), "", null);
                using var worker = new MemoryCurationWorkerService(store, engine, store.TimeProvider, holder, logger);
                try
                {
                    await worker.StartAsync(ct);
                    // Each failed attempt returns directly to the queue. No idle timer drives these retries.
                    await AwaitAssertAsync(async () =>
                        Assert.Equal(("failed", 5), await ReadAdversarialCheckpointAsync(dbPath, ct)),
                        duration: TimeSpan.FromSeconds(10), cancellationToken: ct);
                }
                finally
                {
                    await worker.StopAsync(ct).WaitAsync(TimeSpan.FromSeconds(10), ct);
                }
                Assert.Equal(("failed", 5), await ReadAdversarialCheckpointAsync(dbPath, ct));
                Assert.Equal(5, logger.Messages.Count(message => message.Contains("scheduling retry", StringComparison.Ordinal)));
                Assert.DoesNotContain(logger.Messages, message => message.Contains("curation completed", StringComparison.Ordinal));
                Assert.DoesNotContain(logger.Messages, message => message.Contains("terminated", StringComparison.Ordinal));
            }
            Assert.Equal(initialState, await ReadAdversarialStateAsync(dbPath, ct));
            return;
        }

        if (flow.StartsWith("nominee-", StringComparison.Ordinal))
        {
            await store.UpsertEmbeddingAsync(DocumentId, MemoryEmbedOnWriteCoordinator.DocumentItemKind, ModelId,
                MemoryContentHasher.ComputeHash("Synthetic widget manual", OriginalBody), ExistingVector, ct);
            using var holder = new MemoryEmbedderHolder(new ScriptedEmbedder(ModelId, Dimensions, QueryVectorAt093), "", null);
            var differentName = flow.Contains("different", StringComparison.Ordinal);
            var unavailable = flow.EndsWith("unavailable", StringComparison.Ordinal);
            proposal = proposal with
            {
                AnchorCanonicalName = differentName ? "synthetic-cabinet-notes" : " SYNTHETIC WIDGET MANUAL "
            };
            evaluator = new MemoryCurationEvaluator(store, NullLogger.Instance, new MemoryCurationConfig(),
                unavailable ? null : chat, holder, new MemoryVectorIndexHolder(store));
            var evaluation = await evaluator.EvaluateAsync(proposal, new SessionId("synthetic-session"), ct);
            Assert.Contains(evaluation.Candidates, candidate => candidate.CosineSimilarity.HasValue);
            Assert.Equal(unavailable ? 0 : 1, chat.CallCount);
            Assert.Equal(differentName ? CurationDecisionKind.Create : CurationDecisionKind.Ambiguous, evaluation.Decision.Kind);
            if (differentName)
            {
                var write = await evaluator.ApplyDecisionAsync(proposal, evaluation.Decision, evaluation.Candidates, ct);
                Assert.Equal(proposal, write);
                await store.ApplyInlineCurationBatchAsync([write!], ct);
                var after = await ReadAdversarialStateAsync(dbPath, ct);
                Assert.All(initialState, row => Assert.Contains(row, after));
                Assert.Equal(2, after.Count - initialState.Count);
                Assert.Contains(after, row => row.Kind == "document" && row.Value == ProposalBody
                    && row.Anchor == MemoryTypedId.AnchorId(proposal.AnchorCanonicalName));
            }
            else
            {
                await Assert.ThrowsAsync<InvalidOperationException>(() =>
                    evaluator.ApplyDecisionAsync(proposal, evaluation.Decision, evaluation.Candidates, ct));
                Assert.Equal(initialState, await ReadAdversarialStateAsync(dbPath, ct));
            }
            return;
        }

        var beforeResponseState = initialState;
        var outsideInserted = false;
        if (flow is "invalid-target" or "valid-append")
        {
            // The awaited hook inserts a stored target after candidate collection and before decision acceptance.
            var responseClient = new BeforeCurationResponseClient(chat, async responseCt =>
            {
                await SeedAdversarialDocumentAsync(store, "doc-synthetic-outside", "orchid-vault",
                    "Purple petals surround a ceramic vase.", 0.9, responseCt);
                beforeResponseState = await ReadAdversarialStateAsync(dbPath, responseCt);
                outsideInserted = true;
            });
            evaluator = new MemoryCurationEvaluator(store, NullLogger.Instance, new MemoryCurationConfig(), responseClient);
        }

        var result = await evaluator.EvaluateAsync(proposal, new SessionId("synthetic-session"), ct);
        if (flow is "invalid-target" or "valid-append")
        {
            // The evaluator catches client errors. Require hook completion outside that catch boundary.
            Assert.True(outsideInserted);
            AdversarialStoredRow[] insertedRows =
            [
                new("document", "doc-synthetic-outside", MemoryTypedId.AnchorId("orchid-vault"),
                    "Purple petals surround a ceramic vase.", "merge-document"),
                new("anchor", MemoryTypedId.AnchorId("orchid-vault"), "orchid-vault", "active", "")
            ];
            Assert.Equal(initialState.Concat(insertedRows)
                .OrderBy(row => row.Kind, StringComparer.Ordinal).ThenBy(row => row.Id, StringComparer.Ordinal), beforeResponseState);
            initialState = beforeResponseState;
        }
        string[] expectedCandidates = flow == "overlap-bypass"
            ? ["doc-synthetic-sibling", DocumentId] : [DocumentId];
        Assert.Equal(expectedCandidates,
            result.Candidates.Select(candidate => candidate.DocumentId).Order(StringComparer.Ordinal));
        Assert.Contains(result.Candidates, candidate => candidate.DocumentId == DocumentId);
        Assert.DoesNotContain(result.Candidates, candidate => candidate.DocumentId == "doc-synthetic-outside");
        Assert.Equal(1, chat.CallCount);
        if (flow == "overlap-bypass")
        {
            Assert.Equal(CurationDecisionKind.Skip, result.Decision.Kind);
            Assert.True(result.Decision.FromLlmTier);
            Assert.Equal(2, result.Candidates.Count);
            Assert.Null(await evaluator.ApplyDecisionAsync(proposal, result.Decision, result.Candidates, ct));
            Assert.Equal(initialState, await ReadAdversarialStateAsync(dbPath, ct));
        }
        else if (flow == "valid-append")
        {
            Assert.Equal(CurationDecisionKind.Update, result.Decision.Kind);
            var write = await evaluator.ApplyDecisionAsync(proposal, result.Decision, result.Candidates, ct);
            Assert.NotNull(write);
            Assert.Equal(DocumentId, write.MemoryId);
            Assert.Equal("append-document", write.UpdateSemantics);
            Assert.StartsWith(OriginalBody, write.Content);
            Assert.EndsWith(ProposalBody, write.Content);
            await store.ApplyInlineCurationBatchAsync([write], ct);
            var expected = initialState.Select(row => row.Kind == "document" && row.Id == DocumentId
                ? row with { Value = write.Content, Semantics = "append-document" } : row).ToArray();
            Assert.Equal(expected, await ReadAdversarialStateAsync(dbPath, ct));
        }
        else
        {
            Assert.Equal(CurationDecisionKind.Ambiguous, result.Decision.Kind);
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                evaluator.ApplyDecisionAsync(proposal, result.Decision, result.Candidates, ct));
            Assert.Equal(initialState, await ReadAdversarialStateAsync(dbPath, ct));
        }
    }

    private sealed class BeforeCurationResponseClient(IChatClient inner, Func<CancellationToken, Task> beforeResponse)
        : DelegatingChatClient(inner)
    {
        public override async Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            await beforeResponse(cancellationToken);
            return await base.GetResponseAsync(messages, options, cancellationToken);
        }

        public override async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await beforeResponse(cancellationToken);
            await foreach (var update in base.GetStreamingResponseAsync(messages, options, cancellationToken)
                .WithCancellation(cancellationToken))
                yield return update;
        }
    }

    private static Task SeedAdversarialDocumentAsync(
        SQLiteMemoryStore store, string id, string anchor, string body, double confidence, CancellationToken ct)
        => store.UpsertDocumentAsync(new SQLiteMemoryDocument(id, store.CreateDefaultAnchor(anchor),
            "durable_fact", "Synthetic widget manual", body, null, null, null, "merge-document",
            "normal", "auto", confidence, 1, null, 1, 1, TrustBoundary.TrustedInstanceValue, "public"), ct);

    private sealed record AdversarialStoredRow(string Kind, string Id, string Anchor, string Value, string Semantics);

    private static async Task<List<AdversarialStoredRow>> ReadAdversarialStateAsync(string dbPath, CancellationToken ct)
    {
        await using var connection = new SqliteConnection($"Data Source={dbPath}");
        await connection.OpenAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT 'document', document_id, anchor_id, markdown_body, update_semantics FROM memory_documents
            UNION ALL
            SELECT 'anchor', anchor_id, canonical_name, status, '' FROM memory_anchors
            ORDER BY 1, 2;
            """;
        await using var reader = await command.ExecuteReaderAsync(ct);
        var result = new List<AdversarialStoredRow>();
        while (await reader.ReadAsync(ct))
            result.Add(new(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetString(4)));
        return result;
    }

    private static async Task<(string Status, int Retries)> ReadAdversarialCheckpointAsync(string dbPath, CancellationToken ct)
    {
        await using var connection = new SqliteConnection($"Data Source={dbPath}");
        await connection.OpenAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT status, retry_count FROM memory_checkpoints WHERE checkpoint_id = 'cp-synthetic-rewrite';";
        await using var reader = await command.ExecuteReaderAsync(ct);
        Assert.True(await reader.ReadAsync(ct));
        return (reader.GetString(0), reader.GetInt32(1));
    }

    private sealed class AdversarialWorkerLogger : ILogger<MemoryCurationWorkerService>
    {
        public ConcurrentQueue<string> Messages { get; } = new();
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) => Messages.Enqueue(formatter(state, exception));
    }
}
