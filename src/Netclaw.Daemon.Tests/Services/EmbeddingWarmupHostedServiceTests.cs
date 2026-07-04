// -----------------------------------------------------------------------
// <copyright file="EmbeddingWarmupHostedServiceTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Security.Cryptography;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
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

    private readonly string _baseDir = Path.Combine(Path.GetTempPath(), $"netclaw-embedding-warmup-tests-{Guid.NewGuid():N}");
    private NetclawPaths _paths = null!;
    private SQLiteMemoryStore _store = null!;
    private EmbeddingModelProvisioner _provisioner = null!;

    private static string FixturesDir => Path.Combine(AppContext.BaseDirectory, "Fixtures");

    public async ValueTask InitializeAsync()
    {
        _paths = new NetclawPaths(_baseDir);
        _paths.EnsureDirectoriesExist();
        _store = new SQLiteMemoryStore(_paths.MemorySqliteDbPath, TimeProvider.System);
        await _store.InitializeAsync();

        var modelBytes = await File.ReadAllBytesAsync(Path.Combine(FixturesDir, "tiny-embedder.onnx"));
        var vocabBytes = await File.ReadAllBytesAsync(Path.Combine(FixturesDir, "tiny-vocab.txt"));
        var allowlist = new Dictionary<string, EmbeddingModelManifestEntry>
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
                ModelByteSize: modelBytes.Length),
        };
        _provisioner = new EmbeddingModelProvisioner(new HttpClient(), allowlist);
    }

    public async ValueTask DisposeAsync() => await TryDeleteDirectoryAsync(_baseDir);

    [Fact]
    public async Task Success_path_loads_the_fixture_model_with_no_network_and_populates_the_holder()
    {
        PrePlaceValidModelFiles();
        var holder = new MemoryEmbedderHolder(new UnavailableMemoryEmbedder(ModelId, "warmup not yet run"));
        var memoryConfig = new MemoryConfig { Embeddings = { Enabled = true, ModelId = ModelId, AutoDownload = true } };
        var service = CreateService(holder, memoryConfig);

        await service.WarmUpAsync(TestContext.Current.CancellationToken);

        Assert.True(holder.Current.IsAvailable);
        Assert.Equal(ModelId, holder.Current.ModelId);
        Assert.Equal(Dimensions, holder.Current.Dimensions);
    }

    [Fact]
    public async Task Degraded_path_sets_an_unavailable_embedder_when_the_model_is_missing_and_autodownload_is_false()
    {
        // No PrePlaceValidModelFiles() call — the model directory is empty.
        var holder = new MemoryEmbedderHolder(new UnavailableMemoryEmbedder(ModelId, "warmup not yet run"));
        var memoryConfig = new MemoryConfig { Embeddings = { Enabled = true, ModelId = ModelId, AutoDownload = false } };
        var service = CreateService(holder, memoryConfig);

        await service.WarmUpAsync(TestContext.Current.CancellationToken);

        Assert.False(holder.Current.IsAvailable);
        Assert.IsType<UnavailableMemoryEmbedder>(holder.Current);
    }

    [Fact]
    public async Task Disabled_config_leaves_the_holder_at_its_initial_value()
    {
        var initial = new UnavailableMemoryEmbedder(ModelId, "embeddings disabled");
        var holder = new MemoryEmbedderHolder(initial);
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

        var holder = new MemoryEmbedderHolder(new UnavailableMemoryEmbedder(ModelId, "warmup not yet run"));
        var memoryConfig = new MemoryConfig { Embeddings = { Enabled = true, ModelId = ModelId, AutoDownload = true } };
        var service = CreateService(holder, memoryConfig);

        await service.WarmUpAsync(TestContext.Current.CancellationToken);

        var rows = await _store.GetEmbeddingsForModelAsync(ModelId, TestContext.Current.CancellationToken);
        var row = Assert.Single(rows);
        Assert.Equal("doc-needs-embedding", row.ItemId);
    }

    private EmbeddingWarmupHostedService CreateService(MemoryEmbedderHolder holder, MemoryConfig memoryConfig)
        => new(_provisioner, _store, holder, memoryConfig, _paths, NullLogger<EmbeddingWarmupHostedService>.Instance);

    private void PrePlaceValidModelFiles()
    {
        var dir = _paths.EmbeddingModelDirectory(ModelId);
        Directory.CreateDirectory(dir);
        File.Copy(Path.Combine(FixturesDir, "tiny-embedder.onnx"), Path.Combine(dir, "model.onnx"), overwrite: true);
        File.Copy(Path.Combine(FixturesDir, "tiny-vocab.txt"), Path.Combine(dir, "vocab.txt"), overwrite: true);
    }

    private static string Sha256Hex(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

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
}
