// -----------------------------------------------------------------------
// <copyright file="MemoryUpdateToolRegistrationTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Netclaw.Actors.Memory;
using Netclaw.Actors.Tools;
using Netclaw.Configuration;
using Netclaw.Daemon.Configuration;
using Netclaw.Tests.Utilities;
using Netclaw.Tools;
using Xunit;

namespace Netclaw.Daemon.Tests.Configuration;

public sealed class MemoryUpdateToolRegistrationTests : IAsyncDisposable
{
    private readonly string _baseDir = Path.Combine(Path.GetTempPath(), $"netclaw-memory-update-registration-{Guid.NewGuid():N}");

    [Fact]
    public async Task Registered_update_memory_tool_re_embeds_through_the_registered_holder()
    {
        Directory.CreateDirectory(_baseDir);
        var store = new SQLiteMemoryStore(Path.Combine(_baseDir, "netclaw.db"), TimeProvider.System);
        await store.InitializeAsync(TestContext.Current.CancellationToken);
        await SeedDocumentAsync(store, "doc-1", "Favorite color", "Blue.");

        // Same starting state the daemon registers: the holder points at an unavailable stub until warmup finishes.
        var toolRegistry = new ToolRegistry();
        var services = new ServiceCollection();
        services.AddMemoryUpdateTool(
            toolRegistry,
            store,
            new MemoryEmbedderHolder(
                new UnavailableMemoryEmbedder(FixedEmbedder.Model, "warmup has not completed yet"),
                initialQueryPrefix: string.Empty,
                initialCalibratedMinCosineSimilarity: null));
        await using var provider = services.BuildServiceProvider();

        // What warmup does once the model loads: publish the real embedder through the registered holder.
        provider.GetRequiredService<MemoryEmbedderHolder>()
            .Set(new FixedEmbedder(), queryPrefix: string.Empty, calibratedMinCosineSimilarity: null);

        var tool = Assert.IsType<SqliteUpdateMemoryTool>(toolRegistry.GetByName("update_memory"));
        var context = TestToolExecutionContext.CreateBound(
            "slack/thread-1", null, new TestToolExecutionContextOptions { Audience = TrustAudience.Personal });
        var result = await tool.ExecuteAsync(
            new Dictionary<string, object?> { ["id"] = "doc-1", ["new_content"] = "Green." },
            context,
            CancellationToken.None);

        Assert.Contains("updated", result);
        var row = Assert.Single(await store.GetEmbeddingsForModelAsync(FixedEmbedder.Model, TestContext.Current.CancellationToken));
        Assert.Equal("doc-1", row.ItemId);
    }

    private static Task SeedDocumentAsync(SQLiteMemoryStore store, string id, string title, string content)
        => store.ApplyCurationBatchAsync(
            $"cp-{id}",
            [
                new SQLiteMemoryCurationOperation(
                    Kind: MemoryKind.Document.ToWireValue(),
                    MemoryClass: MemoryClass.DurableFact.ToWireValue(),
                    MemoryId: id,
                    AnchorCanonicalName: title,
                    AnchorType: "concept",
                    Title: title,
                    Content: content,
                    AliasesJson: null,
                    FacetsJson: null,
                    SlotsJson: null,
                    Relations: null,
                    UpdateSemantics: MemoryUpdateSemantics.MergeDocument.ToWireValue(),
                    Boundary: TrustBoundary.TrustedInstanceValue,
                    Audience: TrustAudience.Team,
                    Sensitivity: MemorySensitivity.Normal.ToWireValue(),
                    RecallMode: MemoryRecallMode.Auto.ToWireValue(),
                    Confidence: 0.9,
                    FreshnessAtMs: TimeProvider.System.GetUtcNow().ToUnixTimeMilliseconds(),
                    ExpiresAtMs: null)
            ],
            CancellationToken.None);

    private sealed class FixedEmbedder : IMemoryEmbedder
    {
        public const string Model = "fixed-model";

        public string ModelId => Model;

        public int Dimensions => 2;

        public bool IsAvailable => true;

        public ValueTask<ReadOnlyMemory<float>> EmbedAsync(string text, EmbeddingPurpose purpose, CancellationToken ct)
            => ValueTask.FromResult<ReadOnlyMemory<float>>(new float[] { 1f, 2f });

        public ValueTask<IReadOnlyList<ReadOnlyMemory<float>>> EmbedBatchAsync(IReadOnlyList<string> texts, EmbeddingPurpose purpose, CancellationToken ct)
            => ValueTask.FromResult<IReadOnlyList<ReadOnlyMemory<float>>>(texts.Select(_ => (ReadOnlyMemory<float>)new float[] { 1f, 2f }).ToArray());
    }

    public ValueTask DisposeAsync()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_baseDir))
            Directory.Delete(_baseDir, recursive: true);
        return ValueTask.CompletedTask;
    }
}
